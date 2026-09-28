/* 环形几何 / 配色 / 粒子团 —— 本项目的浏览器预览版，参数与壳里那份（orb/BallVisual.cs）一一对应。
 *
 * 配色取自《明日方舟：终末地》协议核心电量面板那一套：灰衬环 #d3d3ce / 淡黄轨道 #f2edc4 /
 * 亮黄进度弧 #ffe23d / 内盘 #ededea；装饰弧用真正的中低明色 —— 左琥珀 #ecb063、右青蓝 #7fb2cc。
 * 角度口径：0° = 12 点方向，顺时针为正；弧长 > 180° 时置 large-arc 标志。
 */
const RING_NS = 'http://www.w3.org/2000/svg';

const RING = {
  size: 160,          // 球窗口边长
  cx: 80, cy: 80,
  r: 52,              // 电量环半径
  wRail: 9,           // 灰衬环线宽（比轨道宽一点，外露一圈边）
  wTrack: 6.5,        // 轨道 / 进度弧线宽
  rDeco: 70,          // 装饰弧半径（在环外侧，约 1.35 倍环半径）
  wDeco: 9.7,
  rDisc: 43,          // 内盘半径（粒子团背景）
  canvas: 104,        // 粒子团画布边长（CSS 像素）
  particles: 120,     // 粒子数（按内盘面积定的密度）
  fps: 30,            // 这个页面只用来调色；壳里是常驻的，限 30fps
  colorRail: '#d3d3ce',
  colorOutline: '#9a9a94',   // 描边色（比 colorRail 深一档：球浮在任意桌面内容上，浅灰会看不清）
  outline: 1,                // 是否给主环与装饰弧整圈描边
  outlineW: 1.8,             // 描边厚度（单侧，按 160px 球径计；调用方按尺寸缩放）
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

/* 圆上取点：口径是"0° = 12 点方向、顺时针为正"，直接按 sin/cos 组合写即可 ——
   等价于"先把角度减 90° 再取 cos/sin"，但少一步。 */
const toRad = deg => (deg * Math.PI) / 180;

/* Halton 低差异序列的第 n 项（给定进制）。粒子团用它撒点：u = halton(i,2) 定纬度、
   v = halton(i,3) 定经度 —— 均匀，且不像等分角的螺旋那样留出条纹感。 */
function halton(n, b) {
  let f = 1, r = 0;
  while (n > 0) { f /= b; r += f * (n % b); n = Math.floor(n / b); }
  return r;
}

function pointOn(cx, cy, r, deg) {
  const t = toRad(deg);
  return [cx + r * Math.sin(t), cy - r * Math.cos(t)];
}

/* 一段弧的 path。to 小于 from 时按"跨过 12 点"补一圈；弧长超过 180° 必须置 large-arc 标志，
   否则 SVG 会挑短的那条弧来画。 */
function arcD(cx, cy, r, from, to) {
  const sweep = to < from ? to + 360 : to;
  const a = pointOn(cx, cy, r, from), b = pointOn(cx, cy, r, sweep);
  const large = (sweep - from) > 180 ? 1 : 0;
  return `M ${a[0]} ${a[1]} A ${r} ${r} 0 ${large} 1 ${b[0]} ${b[1]}`;
}

/** 占用越高越警示：<70% 亮黄、<88% 琥珀、否则橙红 */
function arcColor(pct) {
  return pct < 70 ? RING.colorArc : (pct < 88 ? RING.colorArcMid : RING.colorArcHigh);
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
  /* 描边：主环与两条装饰弧都描一圈，做法是在底色下面再画一道更粗的同形弧。
     装饰弧两端是平头棱角（不圆头），圆头包不住端面，所以让描边这层在角度上朝两端各外延
     capDeg（＝描边厚度折算成的角度），端面就被平直地描上了。 */
  const ow = o.outlineW;
  const useOutline = o.outline !== 0;
  const capDeg = (ow * 180) / (Math.PI * o.rDeco);
  const deco = (from, to, color) => {
    const layers = [{ stroke: color, width: o.wDeco, from: from, to: to, opacity: '.95' }];
    if (useOutline) {
      layers.unshift({ stroke: o.colorOutline, width: o.wDeco + 2 * ow,
                       from: from - capDeg, to: to + capDeg });
    }
    for (const L of layers) {
      const p = el('path', { fill: 'none', stroke: L.stroke, 'stroke-width': L.width });
      p.setAttribute('d', arcD(cx, cy, o.rDeco, L.from, L.to));
      if (L.opacity) p.setAttribute('opacity', L.opacity);
    }
  };
  deco(270, 360, o.colorDecoLeft);                       // 9 点 → 12 点（左上）
  deco(90, 180, o.colorDecoRight);                       // 3 点 → 6 点（右下）
  // 主环：衬底 = 轨道宽度 + 两侧描边厚，形成均匀的一圈描边（黄弧与其圆头都落在这圈里）
  el('circle', { cx, cy, r: o.r, fill: 'none',
                 stroke: useOutline ? o.colorOutline : o.colorRail,
                 'stroke-width': useOutline ? o.wTrack + 2 * ow : o.wRail });
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
      arc.setAttribute('d', arcD(cx, cy, o.r, 0, Math.max(0.5, Math.min(359.5, p * 3.6))));
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
    for (let i = 0; i < this.n; i++) {              // Halton 低差异序列撒在球面上（与壳里那份同一个撒法）
      const u = halton(i + 1, 2), v = halton(i + 1, 3);
      const y = 1 - 2 * u, ring = Math.sqrt(Math.max(0, 1 - y * y)), th = 2 * Math.PI * v;
      this.pts.push({ x: ring * Math.cos(th), y, z: ring * Math.sin(th), ph: Math.random() * Math.PI * 2 });
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
    const S = o.canvas, c = S / 2;
    ctx.clearRect(0, 0, S, S);
    // 半径以内盘为基准（最大半径正好等于内盘半径），画布比它大只是留白
    const inflate = 0.79 + 0.12 * (0.5 + 0.5 * Math.sin(this.t * 0.9)) * this.amp;
    const spin = this.t * 0.12, cos = Math.cos(spin), sin = Math.sin(spin);
    for (const p of this.pts) {
      const r = o.rDisc * this.squish * (inflate + 0.09 * this._puff(p));
      const x = p.x * cos + p.z * sin;                 // 绕竖轴自转
      const depth = (p.z * cos - p.x * sin + 1) / 2;   // 0 = 背面，1 = 正面
      ctx.beginPath();
      ctx.arc(c + x * r, c + p.y * r, 0.6 + depth * 1.0, 0, 6.2832);
      ctx.fillStyle = `rgba(${o.blobRGB},${(0.10 + depth * 0.40).toFixed(3)})`;
      ctx.fill();
    }
  }

  /** 三层正弦相乘当噪声（与壳里那份同一套），让每个点的半径无规则起伏。 */
  _puff(p) {
    return Math.sin(3.1 * p.x + this.t * 0.7 + p.ph)
         * Math.sin(2.7 * p.y - this.t * 0.5)
         * Math.sin(2.3 * p.z + this.t * 0.6);
  }
}