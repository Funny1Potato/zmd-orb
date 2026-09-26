/* 环形几何 / 配色 / 粒子团 —— 从 zmd-manager（终末地管理器）移植，参数按球的尺寸缩小。
 *
 * 角度口径（与 zmd 一致）：0° = 12 点方向，顺时针为正；弧长 > 180° 时置 large-arc 标志。
 * 配色也沿用 zmd：灰衬环 #d3d3ce / 淡黄轨道 #f2edc4 / 亮黄进度弧 #ffe23d / 内盘 #ededea
 * 装饰弧取真正的中低明色：左琥珀 #ecb063 / 右青蓝 #7fb2cc。
 */
const RING_NS = 'http://www.w3.org/2000/svg';

const RING = {
  size: 160,          // 球窗口边长
  cx: 80, cy: 80,
  r: 52,              // 电量环半径
  wRail: 9,           // 灰衬环线宽（比轨道宽，外露一圈边，同 zmd 的 24/18 比例）
  wTrack: 6.5,        // 轨道 / 进度弧线宽
  rDeco: 70,          // 装饰弧半径（在环外侧，同 zmd 的 202/150 比例）
  wDeco: 9.7,
  rDisc: 43,          // 内盘半径（粒子团背景）
  canvas: 104,        // 粒子团画布边长（CSS 像素）
  particles: 120,     // zmd 是 650/290px，按面积比缩到约 120
  fps: 30,            // zmd 全速 rAF（开窗才看）；球常驻，限 30fps
  colorRail: '#d3d3ce',
  colorTrack: '#f2edc4',
  colorArc: '#ffe23d',      // 低占用
  colorArcMid: '#ecb063',   // 中占用（琥珀）
  colorArcHigh: '#e8703a',  // 高占用（橙红）
  colorDecoLeft: '#ecb063',
  colorDecoRight: '#7fb2cc',
  colorDisc: '#ededea',
  blobRGB: '96,96,92',
  glow: 'drop-shadow(0 0 7px rgba(255,224,70,.5))',
};

function polar(cx, cy, r, deg) {
  const a = (deg - 90) * Math.PI / 180;
  return [cx + r * Math.cos(a), cy + r * Math.sin(a)];
}

function arcPath(cx, cy, r, s, e) {
  if (e < s) e += 360;
  const large = (e - s) > 180 ? 1 : 0;
  const p1 = polar(cx, cy, r, s), p2 = polar(cx, cy, r, e);
  return `M ${p1[0]} ${p1[1]} A ${r} ${r} 0 ${large} 1 ${p2[0]} ${p2[1]}`;
}

/** 占用越高越警示：<70% 亮黄、<88% 琥珀、否则橙红 */
function arcColor(pct) {
  if (pct < 70) return RING.colorArc;
  if (pct < 88) return RING.colorArcMid;
  return RING.colorArcHigh;
}

/** 在给定 <svg> 上搭出电量环，返回操作句柄。装饰弧是固定不动的，只有 #arc 会变。 */
function buildRing(svg, opts) {
  const o = Object.assign({}, RING, opts || {});
  const NS = RING_NS, { cx, cy } = o;
  svg.setAttribute('width', o.size);
  svg.setAttribute('height', o.size);
  svg.setAttribute('viewBox', `0 0 ${o.size} ${o.size}`);
  const el = (name, attrs) => {
    const n = document.createElementNS(NS, name);
    for (const k in attrs) n.setAttribute(k, attrs[k]);
    svg.appendChild(n);
    return n;
  };
  const decoL = el('path', { fill: 'none', stroke: o.colorDecoLeft, 'stroke-width': o.wDeco, opacity: '.95' });
  const decoR = el('path', { fill: 'none', stroke: o.colorDecoRight, 'stroke-width': o.wDeco, opacity: '.95' });
  decoL.setAttribute('d', arcPath(cx, cy, o.rDeco, 270, 360));   // 9 点 → 12 点（左上）
  decoR.setAttribute('d', arcPath(cx, cy, o.rDeco, 90, 180));     // 3 点 → 6 点（右下）
  el('circle', { cx, cy, r: o.r, fill: 'none', stroke: o.colorRail, 'stroke-width': o.wRail });
  el('circle', { cx, cy, r: o.r, fill: 'none', stroke: o.colorTrack, 'stroke-width': o.wTrack });
  el('circle', { cx, cy, r: o.rDisc, fill: o.colorDisc });
  const arc = el('path', {
    fill: 'none', stroke: o.colorArc, 'stroke-width': o.wTrack,
    'stroke-linecap': 'round', style: `filter:${o.glow}`,
  });
  let last = -1;
  return {
    opts: o,
    /** pct: 0~100，环长 = pct% × 360°；扫到极值时留 0.5° 以免弧退化 */
    setPct(pct) {
      const p = Math.max(0, Math.min(100, pct));
      if (Math.abs(p - last) < 0.05) return;
      last = p;
      arc.setAttribute('d', arcPath(cx, cy, o.r, 0, Math.max(0.5, Math.min(359.5, p * 3.6))));
      arc.setAttribute('stroke', arcColor(p));
    },
  };
}

/** 中心粒子团：斐波那契球面撒点 + 三层正弦噪声，随占用"呼吸"。 */
class Blob {
  constructor(canvas, opts) {
    this.o = Object.assign({}, RING, opts || {});
    this.cv = canvas;
    this.ctx = canvas.getContext('2d');
    this.dpr = window.devicePixelRatio || 1;
    this.cv.width = this.o.canvas * this.dpr;
    this.cv.height = this.o.canvas * this.dpr;
    this.ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    this.n = this.o.particles;
    this.pts = [];
    for (let i = 0; i < this.n; i++) {              // 斐波那契球面均匀撒点
      const y = 1 - (i / (this.n - 1)) * 2, r = Math.sqrt(Math.max(0, 1 - y * y)), th = i * 2.39996;
      this.pts.push({ x: Math.cos(th) * r, y, z: Math.sin(th) * r, ph: Math.random() * Math.PI * 2 });
    }
    this.t = Math.random() * 100;
    this.squish = 1;      // 整理动画的收缩比
    this.amp = 1;         // 呼吸幅度（整理时压低）
    this.frames = 0;      // 累计绘制帧数（M0 实测常驻开销用）
    this.running = false;
    this._last = 0;
    this._onVis = () => this.setRunning(document.visibilityState !== 'hidden');
    document.addEventListener('visibilitychange', this._onVis);
  }

  setRunning(on) {
    if (on === this.running) return;
    this.running = on;
    if (on) { this._last = 0; requestAnimationFrame(ts => this._loop(ts)); }
  }

  setFps(fps) { this.o.fps = Math.max(1, fps); }

  /** 整理动画：先被"吸"进中心，再回弹一次 */
  pulse() {
    const t0 = performance.now();
    const step = () => {
      const k = (performance.now() - t0) / 700;
      if (k >= 1) { this.squish = 1; this.amp = 1; return; }
      if (k < 0.45) { const u = k / 0.45; this.squish = 1 - 0.26 * u; this.amp = 1 - 0.5 * u; }
      else { const u = (k - 0.45) / 0.55; this.squish = 1 + 0.10 * Math.sin(u * Math.PI) - 0.26 * (1 - u); this.amp = 0.5 + 0.5 * u; }
      requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  }

  _loop(ts) {
    if (!this.running) return;
    const interval = 1000 / this.o.fps;
    if (!this._last || ts - this._last >= interval) {
      this._last = ts;
      this.frames++;
      this._draw(1 / this.o.fps);
    }
    requestAnimationFrame(t => this._loop(t));
  }

  _draw(dt) {
    this.t += dt;
    const { ctx, o } = this;
    const S = o.canvas;
    ctx.clearRect(0, 0, S, S);
    const c = S / 2;
    const breath = 0.5 + 0.5 * Math.sin(this.t * 0.9);
    const rot = this.t * 0.12, cs = Math.cos(rot), sn = Math.sin(rot);
    for (const p of this.pts) {
      const n = Math.sin(3.1 * p.x + this.t * 0.7 + p.ph) * Math.sin(2.7 * p.y - this.t * 0.5) * Math.sin(2.3 * p.z + this.t * 0.6);
      // 以"内盘半径"为基准（zmd 里 blob 最大半径正好等于内盘 r=126），画布更大只是留白
      const R = o.rDisc * (0.79 + 0.12 * breath * this.amp + 0.09 * n) * this.squish;
      const x = p.x * cs + p.z * sn, z = -p.x * sn + p.z * cs, y = p.y;
      const depth = (z + 1) / 2;
      ctx.beginPath();
      ctx.arc(c + x * R, c + y * R, 0.6 + depth * 1.0, 0, 6.2832);
      ctx.fillStyle = `rgba(${o.blobRGB},${(0.10 + depth * 0.40).toFixed(3)})`;
      ctx.fill();
    }
  }
}