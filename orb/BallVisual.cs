using System;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 电量环 + 中心粒子团。主环与粒子团的几何、配色、动画参数对应 frontend/ring.js（同一套 zmd 口径）；
/// 两侧的 1/4 装饰弧改成了计量条：左上橙 = 内存占用率、右下蓝 = 提交额度占用率，水平线为零点。
/// 不参与命中测试：球的可点区域由外层 Grid 统一处理。
/// </summary>
sealed class BallVisual : FrameworkElement
{
    struct P { public double X, Y, Z; }

    const int Buckets = 20;                  // 粒子透明度分档：避免每帧新建 120 个画刷
    // 未占到段的透明度：两侧计量条宽一些，0.55 就够；主环那条带更窄（6.5），太透会糊掉看不见，
    // 所以单独给高一档（配浅色底衬）
    const double TrackAlpha = 0.55;      // 两侧计量条的底槽（0.35 偏透，按反馈回调一档）
    const double RingTrackAlpha = 0.72;  // 主环未占用段（用户要求再实一点）
    const double BaseAlpha = 0.55;       // 底衬：浅色半透的"磨砂底托"
    const double OutlineAlpha = 0.80;    // 描边：深一档、几乎实色（底衬改白后描边不能再跟着白，否则轮廓糊掉）
    const double ShadowShiftY = 2.4;     // 阴影往下偏一点，才像投影（不是均匀的描边）
    static readonly Color ShadowRgb = Color.FromRgb(0x2f, 0x2f, 0x2c);   // 中性暗灰，不用描边那个暖灰
    // 投影的软衰减：宽度（在描边之外再加的量）与透明度一一对应，从实到透共五圈
    static readonly double[] ShadowW = { 1.5, 3.0, 5.0, 7.0, 9.5 };
    static readonly double[] ShadowA = { 0.13, 0.095, 0.065, 0.04, 0.022 };

    /* 装饰弧的末端原来有两处硬边（反馈"头部和辉光之间的分界太明显"）：
       一是填充段的平头——一整块实色方块；二是辉光溢出段的末端——一刀切齐。
       实测那两处的像素跳变各是 57 与 25 个 B 单位（都在 0.3 DIP 内），而这一段本该是渐隐的。
       改法仍然不用渐变笔刷：填充段末端 DecoFade 度分 N 段递降透明度，三层辉光溢出长度各不相同
       （越宽越淡的那层溢得越远），末端就成了错开的三级台阶。**平头保持不变**（用户明确要平头）。 */
    const double DecoFade = 3.0;                              // 填充段末端收尾的长度（度；另受整条 25% 的上限约束）
    // 收尾各段的透明度**取等差**（[0]＝实色段）：等差 → 相邻两级在像素上的落差也基本相等。
    // 早先按等比降（0.95/0.80/0.66/0.52/0.38/0.24）算出来最后一级落差是前几级的两倍，
    // 末端仍然看得出一下台阶。实测这档每级 ΔB≈12，整体是一条 4.6px 宽的连续渐变。
    static readonly double[] DecoFadeA = { 0.95, 0.78, 0.60, 0.43, 0.26, 0.10 };
    static readonly double[] DecoGlowW = { 4, 7, 9 };         // 辉光三层的加宽量（近/中/远）
    static readonly double[] DecoGlowA = { 0.21, 0.11, 0.05 };
    static readonly double[] DecoGlowOver = { 0.6, 2.4, 4.2 }; // 各层比填充段多扫的角度

    /// <summary>内盘边缘虚化的起点（占盘半径的比例）：0.90 → 只有最后 10%（≈4 DIP / 5 物理像素）
    /// 在渐隐。**这个数就是"虚边有多宽"的唯一旋钮**，越靠近 1 边越硬。</summary>
    const double DiscEdgeFade = 0.90;

    readonly P[] _pts = new P[Ring.Particles];
    readonly double[] _phase = new double[Ring.Particles];
    readonly Brush[] _blobBrush = new Brush[Buckets + 1];

    readonly Geometry _decoLeftBaseG, _decoLeftTrackG, _decoRightBaseG, _decoRightTrackG, _ringBaseG, _glowClip;
    readonly Pen _outlinePen, _decoLeftTrack, _decoRightTrack, _trackPen;
    readonly Pen[] _shadow = new Pen[5];
    readonly Transform _shadowShift;
    readonly Pen[] _arcPen = new Pen[3], _glowNear = new Pen[3], _glowMid = new Pen[3], _glowFar = new Pen[3];
    readonly Pen[] _decoFadeL = new Pen[DecoFadeA.Length], _decoFadeR = new Pen[DecoFadeA.Length];
    readonly Pen[] _decoGlowL = new Pen[3], _decoGlowR = new Pen[3];   // [0]=近（窄而实）… [2]=远（宽而淡）
    readonly Brush _baseBrush, _discBrush, _frostDots;
    readonly Geometry _dotClipG;

    Geometry? _arc, _decoLeftFillG, _decoRightFillG;
    readonly Geometry?[] _decoTailL = new Geometry?[DecoFadeA.Length - 1];
    readonly Geometry?[] _decoTailR = new Geometry?[DecoFadeA.Length - 1];
    readonly Geometry?[] _decoGlowGL = new Geometry?[3], _decoGlowGR = new Geometry?[3];
    double _arcPct = double.NaN, _decoPct = double.NaN, _decoCommit = double.NaN;
    double _pct, _commitPct;
    double _t, _squish = 1, _amp = 1;

    public BallVisual()
    {
        var rnd = new Random();
        for (int i = 0; i < Ring.Particles; i++)      // Halton 低差异序列撒在球面上（与面板那个粒子团同一套撒法）
        {
            double u = Ring.Halton(i + 1, 2), v = Ring.Halton(i + 1, 3);
            double y = 1 - 2 * u;
            double rr = Math.Sqrt(Math.Max(0, 1 - y * y));
            double th = 2 * Math.PI * v;
            _pts[i] = new P { X = rr * Math.Cos(th), Y = y, Z = rr * Math.Sin(th) };
            _phase[i] = rnd.NextDouble() * Math.PI * 2;
        }
        _t = rnd.NextDouble() * 100;

        for (int b = 0; b <= Buckets; b++)
        {
            var br = new SolidColorBrush(Ring.BlobRgb) { Opacity = 0.10 + 0.40 * b / Buckets };
            br.Freeze();
            _blobBrush[b] = br;
        }

        /* 两侧的 1/4 弧改成计量条（用户要求）：
           左上橙 = 内存占用率（used/total）、右下蓝 = 提交额度占用率（committed/limit），
           都以**水平线为零点**向各自那一侧涨：橙从 9 点(270°)往 12 点涨、蓝从 3 点(90°)往 6 点涨，
           满量程仍是这 1/4 弧（270~360 / 90~180）。
           底衬与描边**分开**画：底衬是浅色半透（磨砂底托），描边是深一档的独立细线。
           两者都用**闭合扇段**（Ring.Sector）实现——这样描边能顺着形状把两端平头也描上，
           而"粗描边垫在底下再压一条细的"只能露出两条长边，且底衬会被描边色带灰（实测就是灰蒙蒙的来源）。 */
        _decoLeftTrackG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 270, 360);
        _decoRightTrackG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 90, 180);
        _decoLeftBaseG = Ring.Sector(Ring.Cx, Ring.Cy, Ring.RDeco - Ring.WDeco / 2,
                                     Ring.RDeco + Ring.WDeco / 2, 270, 360);
        _decoRightBaseG = Ring.Sector(Ring.Cx, Ring.Cy, Ring.RDeco - Ring.WDeco / 2,
                                      Ring.RDeco + Ring.WDeco / 2, 90, 180);

        _baseBrush = Freeze(new SolidColorBrush(Ring.Base) { Opacity = BaseAlpha });
        _outlinePen = Pen(Ring.Outline, Ring.OutlineW, OutlineAlpha);
        /* 投影：五圈从实到透的同心描边（宽度与透明度一一对应）+ 下偏 2.4 DIP，画在底衬填充之前，
           朝内那半边被填充盖掉，只留朝外的渐隐。颜色用中性暗灰，不用描边那个暖灰。 */
        for (int i = 0; i < _shadow.Length; i++)
            _shadow[i] = Pen(ShadowRgb, Ring.OutlineW + ShadowW[i], ShadowA[i]);
        var shift = new TranslateTransform(0, ShadowShiftY);
        shift.Freeze();
        _shadowShift = shift;
        // 没占到的那一段：各自弧色的浅色版 + 半透明（壁纸透得上来，才是"没占到"的观感）
        _decoLeftTrack = Pen(Ring.DecoLeftTrack, Ring.WDeco, TrackAlpha);
        _decoRightTrack = Pen(Ring.DecoRightTrack, Ring.WDeco, TrackAlpha);
        for (int i = 0; i < DecoFadeA.Length; i++)          // 填充段的实色段 + 末端收尾各段
        {
            _decoFadeL[i] = Pen(Ring.DecoLeft, Ring.WDeco, DecoFadeA[i]);
            _decoFadeR[i] = Pen(Ring.DecoRight, Ring.WDeco, DecoFadeA[i]);
        }
        // 主环：整圈底衬 + 整圈描边（同样是两个独立的东西）
        _ringBaseG = Ring.Annulus(Ring.Cx, Ring.Cy, Ring.R - Ring.WTrack / 2, Ring.R + Ring.WTrack / 2);
        _trackPen = Pen(Ring.Track, Ring.WTrack, RingTrackAlpha);   // 主环未占用段：半透明，但比两侧的条实一些
        /* 中间的盘：浅色高光渐变 + 一层更细的点纹理，做出"离焦的磨砂玻璃"观感。
           透明度按 Ring.DiscAlpha 定，半径取包围盒的 **0.50 → 渐变的 1.0 正好落在盘缘**（r=43 DIP），
           所以最后那一小段就是"边缘虚化"，宽度由 `DiscEdgeFade` 定（0.90 → 只有最后 4 DIP ≈ 5 像素）。
           早先半径取 0.53 且末端还留着 31% 的 alpha，两个毛病一起犯：整块盘从 r≈24 就开始渐隐
           （虚边 15~20 DIP，反馈"边缘模糊太多"），盘边又还剩一道硬边的圆边。
           真正把背景模糊掉的方案不存在：分层窗口不吃 DWM 的亚克力（整窗矩形铺，会把当初
           在 WebView2 上吃过的"泛底"请回来），所以这里用"盘缘渐隐 + 微点"近似。 */
        var frost = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.42, 0.34),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.50,
            RadiusY = 0.50,
        };
        byte A(double k) => (byte)Math.Round(255 * Ring.DiscAlpha * k);
        double e0 = DiscEdgeFade, e1 = e0 + (1 - e0) * 0.6;   // 虚化段里再垫一档，收尾才像离焦不是一条直线
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(A(1.00), 0xff, 0xff, 0xff), 0));
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.97), 0xf6, 0xf6, 0xf2), 0.45));
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.90), 0xf2, 0xf2, 0xee), e0));
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(A(0.42), 0xef, 0xef, 0xea), e1));
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xed, 0xed, 0xea), 1));
        frost.Freeze();
        _discBrush = frost;

        var dotBrush = new SolidColorBrush(Ring.FrostDot) { Opacity = 0.05 };
        dotBrush.Freeze();
        var dots = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 4, 4),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            Drawing = new GeometryDrawing(dotBrush, null, new EllipseGeometry(new Point(2, 2), 0.85, 0.85)),
        };
        dots.Freeze();
        _frostDots = dots;
        // 点纹理裁到"虚边起点以内"（比盘小 6 DIP）：盘缘已经渐隐，纹理要是跟着到边就留下一个"点状圆边"
        _dotClipG = new EllipseGeometry(new Point(Ring.Cx, Ring.Cy), Ring.RDisc - 6, Ring.RDisc - 6);
        _dotClipG.Freeze();

        var colors = new[] { Ring.ArcLow, Ring.ArcMid, Ring.ArcHigh };
        for (int i = 0; i < 3; i++)
        {
            _arcPen[i] = Pen(colors[i], Ring.WTrack, 1.0, round: true);
            // 原 SVG 是 drop-shadow(0 0 7px rgba(255,224,70,.5))：三层递减宽度的半透明描边近似这圈辉光
            // （宽度 +5/+10/+16 保留，透明度按反馈再降一档：0.32/0.17/0.08 → 0.24/0.13/0.06）
            _glowNear[i] = Pen(colors[i], Ring.WTrack + 5, 0.24, round: true);
            _glowMid[i] = Pen(colors[i], Ring.WTrack + 10, 0.13, round: true);
            _glowFar[i] = Pen(colors[i], Ring.WTrack + 16, 0.06, round: true);
        }

        /* 两侧计量条的填充段也有辉光，但不能照搬主环那套：
           1) 装饰弧两端是平头，辉光也得是平头——用圆头会在两端各多出一坨光；
           2) 宽度比主环收一档（+4/+7/+9），最外缘 79.35 DIP，留出余量不被裁；
           3) 透明度不能比主环低太多：装饰弧的辉光色（#ecb063 / #7fb2cc）几乎就是它自己那条
              浅色底槽（#f3d6b9 / #cbe0ed）的深色版，同色系上压 16% 等于看不见。 */
        var decoColors = new[] { Ring.DecoLeft, Ring.DecoRight };
        for (int i = 0; i < DecoGlowW.Length; i++)
        {
            _decoGlowL[i] = Pen(decoColors[0], Ring.WDeco + DecoGlowW[i], DecoGlowA[i]);
            _decoGlowR[i] = Pen(decoColors[1], Ring.WDeco + DecoGlowW[i], DecoGlowA[i]);
        }

        /* 辉光要裁掉主环内缘以内的部分：最外层描边是 52±9.25，会糊到内盘(43)与主环之间
           那道透明缝上（实测在红底上留下 7% 的黄）。裁到主环内缘(=不透明衬底的起点)，
           剪影正好落在衬底边缘上，看不出硬边。 */
        var clip = new GeometryGroup { FillRule = FillRule.EvenOdd };
        clip.Children.Add(new RectangleGeometry(new Rect(0, 0, Ring.Size, Ring.Size)));
        clip.Children.Add(new EllipseGeometry(new Point(Ring.Cx, Ring.Cy), 46.9, 46.9));
        clip.Freeze();
        _glowClip = clip;

        IsHitTestVisible = false;
    }

    /// <summary>环的进度（0~100）。</summary>
    public double Pct
    {
        get => _pct;
        set
        {
            double v = value;
            if (Math.Abs(v - _pct) < 0.0001) return;
            _pct = v;
            InvalidateVisual();
        }
    }

    /// <summary>右侧蓝条（提交额度占用率 0~100）。</summary>
    public double CommitPct
    {
        get => _commitPct;
        set
        {
            double v = value;
            if (Math.Abs(v - _commitPct) < 0.0001) return;
            _commitPct = v;
            InvalidateVisual();
        }
    }

    /// <summary>累计绘制帧数（开销观测用）。</summary>
    public long Frames { get; private set; }

    /// <summary>整理动画：粒子团被"吸"进中心再回弹（1 = 常态）。</summary>
    public void SetBreathe(double squish, double amp)
    {
        _squish = squish;
        _amp = amp;
    }

    /// <summary>推进一帧。</summary>
    public void Advance(double dt)
    {
        _t += dt;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        Frames++;
        // 轻量模式：只留环 + 数字 —— 不画粒子团、不铺辉光、不画磨砂点纹理
        // （用户选的"外观也变"：一眼能看出轻量开着，顺带把常驻那点图形开销也去掉）
        bool lite = UiSettings.Lite;

        /* 几何必须在**画之前**重建。以前这两行排在装饰弧与弧的绘制之后，于是每次"几何变了 + 这一帧才画"
           都会晚一帧：填充段那一帧画不出来，要等下一次重画才出现。普通模式 30fps 连着重画看不出来，
           轻量模式只有启动那一次绘制 —— 表现就是"轻量模式启动后装饰弧的占用条不显示，
           点一次整理（动画里连续重画）才出来"（用户反馈）。 */
        EnsureArc();
        EnsureDeco();

        // 每条都先垫一层向下的柔影，再画底衬 + 细描边
        DrawDeco(dc, lite, _decoLeftBaseG, _decoLeftTrackG, _decoLeftTrack,
                 _decoFadeL, _decoLeftFillG, _decoTailL, _decoGlowGL, _decoGlowL);
        DrawDeco(dc, lite, _decoRightBaseG, _decoRightTrackG, _decoRightTrack,
                 _decoFadeR, _decoRightFillG, _decoTailR, _decoGlowGR, _decoGlowR);

        var c = new Point(Ring.Cx, Ring.Cy);
        DrawBase(dc, _ringBaseG);                                   // 主环：柔影 + 底衬 + 细描边
        dc.DrawEllipse(null, _trackPen, c, Ring.R, Ring.R);
        dc.DrawEllipse(_discBrush, null, c, Ring.RDisc, Ring.RDisc);
        if (!lite) dc.DrawGeometry(_frostDots, null, _dotClipG);   // 磨砂盘上的细点纹理（裁在盘内）

        if (_arc != null)
        {
            int ci = _arcPct < 70 ? 0 : _arcPct < 88 ? 1 : 2;
            if (!lite)
            {
                dc.PushClip(_glowClip);
                dc.DrawGeometry(null, _glowFar[ci], _arc);
                dc.DrawGeometry(null, _glowMid[ci], _arc);
                dc.DrawGeometry(null, _glowNear[ci], _arc);
                dc.Pop();
            }
            dc.DrawGeometry(null, _arcPen[ci], _arc);
        }

        if (!lite) DrawBlob(dc);
    }

    /// <summary>一侧装饰弧的画法：柔影 + 底衬/描边 → 底槽 → （非轻量）三层辉光 → 实色段 → 收尾各段。
    /// 辉光从最外（最淡）往里叠，末端才与填充段自然接上。</summary>
    void DrawDeco(DrawingContext dc, bool lite, Geometry baseG, Geometry trackG, Pen track,
                  Pen[] fade, Geometry? fill, Geometry?[] tail, Geometry?[] glowG, Pen[] glow)
    {
        DrawBase(dc, baseG);
        dc.DrawGeometry(null, track, trackG);
        if (fill == null) return;                         // 占用为 0：只留浅色底槽
        if (!lite)
            for (int i = glowG.Length - 1; i >= 0; i--)
                if (glowG[i] != null) dc.DrawGeometry(null, glow[i], glowG[i]);
        dc.DrawGeometry(null, fade[0], fill);
        for (int i = 0; i < tail.Length; i++)
            if (tail[i] != null) dc.DrawGeometry(null, fade[i + 1], tail[i]);
    }

    /// <summary>底衬的通用画法：[向下的软投影（五圈衰减）] → [浅色底衬 + 细描边]。
    /// 投影画在填充之前，朝内那半边会被填充盖掉，只留朝外的渐隐。</summary>
    void DrawBase(DrawingContext dc, Geometry geo)
    {
        dc.PushTransform(_shadowShift);
        for (int i = _shadow.Length - 1; i >= 0; i--)     // 从最外（最透）往里画，叠出来才连续
            dc.DrawGeometry(null, _shadow[i], geo);
        dc.Pop();
        dc.DrawGeometry(_baseBrush, _outlinePen, geo);
    }

    /// <summary>扫到极值时留 0.5° 以免弧退化；进度几乎没变就不重建几何。</summary>
    void EnsureArc()
    {
        double p = Math.Max(0, Math.Min(100, _pct));
        if (!double.IsNaN(_arcPct) && Math.Abs(p - _arcPct) < 0.05) return;
        _arcPct = p;
        _arc = Ring.Arc(Ring.Cx, Ring.Cy, Ring.R, 0, Math.Max(0.5, Math.Min(359.5, p * 3.6)));
    }

    /// <summary>两条装饰弧的几何：从水平线（橙=9 点、蓝=3 点）起，按百分比涨到各自的 1/4 弧满量程。
    /// 进度几乎没变就不重建几何；占比为 0 时整段不画（只留浅色底槽）。</summary>
    void EnsureDeco()
    {
        double a = Math.Max(0, Math.Min(100, _pct));
        double b = Math.Max(0, Math.Min(100, _commitPct));
        if (Math.Abs(a - _decoPct) < 0.05 && Math.Abs(b - _decoCommit) < 0.05) return;
        _decoPct = a;
        _decoCommit = b;
        EnsureDecoSide(270, a, ref _decoLeftFillG, _decoTailL, _decoGlowGL);
        EnsureDecoSide(90, b, ref _decoRightFillG, _decoTailR, _decoGlowGR);
    }

    /// <summary>一侧的实色段 / 收尾段 / 三层辉光（start = 270 橙、90 蓝）。
    /// 收尾段把末端那几度分成 N 段递降透明度；三层辉光各自多扫不同角度 → 末端的硬切错开成台阶。</summary>
    static void EnsureDecoSide(double start, double pct, ref Geometry? fill, Geometry?[] tail, Geometry?[] glow)
    {
        fill = null;
        for (int i = 0; i < tail.Length; i++) tail[i] = null;
        for (int i = 0; i < glow.Length; i++) glow[i] = null;
        if (pct <= 0.05) return;
        double span = Math.Max(0.5, pct * 0.9);            // 满量程 90°
        double head = start + span;
        double fade = Math.Min(DecoFade, span * 0.25);     // 收尾长度（短弧上留出实色段，别整条都发虚）
        double step = fade / tail.Length;
        double solidTo = head - fade;
        if (solidTo - start >= 0.5)
            fill = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, start, solidTo);
        for (int i = 0; i < tail.Length; i++)
        {
            double f0 = solidTo + step * i;
            tail[i] = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, f0, f0 + step);
        }
        for (int i = 0; i < glow.Length; i++)
            glow[i] = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, start, head + DecoGlowOver[i]);
    }

    /// <summary>球心的粒子团：半径随"呼吸 × 整理手势"与三层正弦噪声起伏，绕竖轴缓慢自转。
    /// 半径以**内盘**为基准（常态 0.79×RDisc），所以盘缘虚化怎么调都不影响它的相对大小。</summary>
    void DrawBlob(DrawingContext dc)
    {
        double inflate = 0.79 + 0.12 * (0.5 + 0.5 * Math.Sin(_t * 0.9)) * _amp;
        double spin = _t * 0.12, cos = Math.Cos(spin), sin = Math.Sin(spin);
        for (int i = 0; i < _pts.Length; i++)
        {
            var p = _pts[i];
            double r = Ring.RDisc * _squish * (inflate + 0.09 * Puff(p, _phase[i]));
            double x = p.X * cos + p.Z * sin;                  // 绕竖轴自转
            double depth = (p.Z * cos - p.X * sin + 1) / 2;    // 0 = 背面，1 = 正面
            double rad = 0.6 + depth;
            dc.DrawEllipse(_blobBrush[(int)Math.Round(depth * Buckets)], null,
                           new Point(Ring.Cx + x * r, Ring.Cy + p.Y * r), rad, rad);
        }
    }

    /// <summary>三层正弦相乘当噪声（与面板粒子团同一套），让每个点的半径无规则起伏。</summary>
    double Puff(in P p, double phase) => Math.Sin(3.1 * p.X + _t * 0.7 + phase)
                                       * Math.Sin(2.7 * p.Y - _t * 0.5)
                                       * Math.Sin(2.3 * p.Z + _t * 0.6);

    static Pen Pen(Color c, double w, double opacity = 1.0, bool round = false)
    {
        var brush = new SolidColorBrush(c) { Opacity = opacity };
        var pen = new Pen(brush, w);
        if (round) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        pen.Freeze();
        return pen;
    }

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}