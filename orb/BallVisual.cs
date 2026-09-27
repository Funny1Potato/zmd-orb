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
    // 未占到段的透明度：两侧计量条细，0.35 就够；主环那条带更窄（6.5），太透会糊掉看不见，
    // 所以单独给高一档（配 0.50 的衬底，整体约八成半实）
    const double TrackAlpha = 0.35;      // 两侧计量条的底槽
    const double RailAlpha = 0.50;       // 主环的衬底（描边带）
    const double RingTrackAlpha = 0.72;  // 主环未占用段（用户要求再实一点）

    readonly P[] _pts = new P[Ring.Particles];
    readonly double[] _phase = new double[Ring.Particles];
    readonly Brush[] _blobBrush = new Brush[Buckets + 1];

    readonly Geometry _decoLeftUnderG, _decoLeftTrackG, _decoRightUnderG, _decoRightTrackG, _glowClip;
    readonly Pen _decoUnder, _decoLeftPen, _decoLeftTrack, _decoRightPen, _decoRightTrack, _railPen, _trackPen;
    readonly Pen[] _arcPen = new Pen[3], _glowNear = new Pen[3], _glowMid = new Pen[3], _glowFar = new Pen[3];
    readonly Pen[] _decoGlowNear = new Pen[2], _decoGlowMid = new Pen[2], _decoGlowFar = new Pen[2];
    readonly Brush _discBrush, _frostDots;
    readonly Geometry _discG;

    Geometry? _arc, _decoLeftFillG, _decoRightFillG;
    double _arcPct = double.NaN, _decoPct = double.NaN, _decoCommit = double.NaN;
    double _pct, _commitPct;
    double _t, _squish = 1, _amp = 1;

    public BallVisual()
    {
        var rnd = new Random();
        for (int i = 0; i < Ring.Particles; i++)      // 斐波那契球面均匀撒点
        {
            double y = 1 - i / (double)(Ring.Particles - 1) * 2;
            double r = Math.Sqrt(Math.Max(0, 1 - y * y));
            double th = i * 2.39996;
            _pts[i] = new P { X = Math.Cos(th) * r, Y = y, Z = Math.Sin(th) * r };
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
           满量程仍是这 1/4 弧（270~360 / 90~180），底槽保持整条弧的浅色打底。
           描边沿用 ring.js 的做法：在底色下面再画一道更粗的同形描边。
           装饰弧两端是平头，所以描边得朝两端各外延 capExt（＝描边厚度折算成的角度），
           端面才会被平直地描上而不是靠圆头包住。 */
        double ow = Ring.OutlineW;
        double capExt = ow * 180 / (Math.PI * Ring.RDeco);
        _decoLeftUnderG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 270 - capExt, 360 + capExt);
        _decoLeftTrackG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 270, 360);
        _decoRightUnderG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 90 - capExt, 180 + capExt);
        _decoRightTrackG = Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 90, 180);

        _decoUnder = Pen(Ring.Outline, Ring.WDeco + 2 * ow, 0.30);   // 描边带也要半透：它是"跑道"宽的实心带，
                                                                    // 不透明的话底槽再透也透不出壁纸
        _decoLeftPen = Pen(Ring.DecoLeft, Ring.WDeco, 0.95);
        _decoRightPen = Pen(Ring.DecoRight, Ring.WDeco, 0.95);
        // 没占到的那一段：各自弧色的浅色版 + **明显半透明**（0.35：壁纸透得上来，才是"没占到"的观感）
        _decoLeftTrack = Pen(Ring.DecoLeftTrack, Ring.WDeco, TrackAlpha);
        _decoRightTrack = Pen(Ring.DecoRightTrack, Ring.WDeco, TrackAlpha);
        _railPen = Pen(Ring.Outline, Ring.WTrack + 2 * ow, RailAlpha);   // 衬底半透：不然它会挡在主环轨道下面，轨道再透也透不出壁纸
        _trackPen = Pen(Ring.Track, Ring.WTrack, RingTrackAlpha);   // 主环未占用段：半透明，但比两侧的条实一些
        /* 中间的盘：浅色半透 + 一层细点纹理，做出"磨砂玻璃"的观感。
           真正的背景模糊（DWM 亚克力）是按整窗矩形铺的，会把当初去掉的"泛底"请回来，
           所以这里用"高光渐变 + 微点纹理"来近似磨砂，任意壁纸上都不会变成一块方雾。 */
        var frost = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.42, 0.34),
            Center = new Point(0.5, 0.46),
            RadiusX = 0.68,
            RadiusY = 0.68,
        };
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(0x7a, 0xff, 0xff, 0xff), 0));
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 0xf1, 0xf1, 0xed), 1));
        frost.Freeze();
        _discBrush = frost;

        var dotBrush = new SolidColorBrush(Ring.FrostDot) { Opacity = 0.10 };
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
        _discG = new EllipseGeometry(new Point(Ring.Cx, Ring.Cy), Ring.RDisc, Ring.RDisc);
        _discG.Freeze();

        var colors = new[] { Ring.ArcLow, Ring.ArcMid, Ring.ArcHigh };
        for (int i = 0; i < 3; i++)
        {
            _arcPen[i] = Pen(colors[i], Ring.WTrack, 1.0, round: true);
            // 原 SVG 是 drop-shadow(0 0 7px rgba(255,224,70,.5))：三层递减宽度的半透明描边近似这圈辉光
            _glowNear[i] = Pen(colors[i], Ring.WTrack + 4, 0.26, round: true);
            _glowMid[i] = Pen(colors[i], Ring.WTrack + 8, 0.13, round: true);
            _glowFar[i] = Pen(colors[i], Ring.WTrack + 12, 0.06, round: true);
        }

        // 两侧计量条的填充段也带同样的辉光（用户要求），颜色用各自的橙 / 蓝
        var decoColors = new[] { Ring.DecoLeft, Ring.DecoRight };
        for (int i = 0; i < 2; i++)
        {
            _decoGlowNear[i] = Pen(decoColors[i], Ring.WDeco + 4, 0.26, round: true);
            _decoGlowMid[i] = Pen(decoColors[i], Ring.WDeco + 8, 0.13, round: true);
            _decoGlowFar[i] = Pen(decoColors[i], Ring.WDeco + 12, 0.06, round: true);
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

        dc.DrawGeometry(null, _decoUnder, _decoLeftUnderG);
        dc.DrawGeometry(null, _decoLeftTrack, _decoLeftTrackG);
        if (_decoLeftFillG != null)
        {
            dc.DrawGeometry(null, _decoGlowFar[0], _decoLeftFillG);
            dc.DrawGeometry(null, _decoGlowMid[0], _decoLeftFillG);
            dc.DrawGeometry(null, _decoGlowNear[0], _decoLeftFillG);
            dc.DrawGeometry(null, _decoLeftPen, _decoLeftFillG);
        }
        dc.DrawGeometry(null, _decoUnder, _decoRightUnderG);
        dc.DrawGeometry(null, _decoRightTrack, _decoRightTrackG);
        if (_decoRightFillG != null)
        {
            dc.DrawGeometry(null, _decoGlowFar[1], _decoRightFillG);
            dc.DrawGeometry(null, _decoGlowMid[1], _decoRightFillG);
            dc.DrawGeometry(null, _decoGlowNear[1], _decoRightFillG);
            dc.DrawGeometry(null, _decoRightPen, _decoRightFillG);
        }

        var c = new Point(Ring.Cx, Ring.Cy);
        dc.DrawEllipse(null, _railPen, c, Ring.R, Ring.R);
        dc.DrawEllipse(null, _trackPen, c, Ring.R, Ring.R);
        dc.DrawEllipse(_discBrush, null, c, Ring.RDisc, Ring.RDisc);
        dc.DrawGeometry(_frostDots, null, _discG);      // 磨砂盘上的细点纹理（裁在盘内）

        EnsureArc();
        EnsureDeco();
        if (_arc != null)
        {
            int ci = _arcPct < 70 ? 0 : _arcPct < 88 ? 1 : 2;
            dc.PushClip(_glowClip);
            dc.DrawGeometry(null, _glowFar[ci], _arc);
            dc.DrawGeometry(null, _glowMid[ci], _arc);
            dc.DrawGeometry(null, _glowNear[ci], _arc);
            dc.Pop();
            dc.DrawGeometry(null, _arcPen[ci], _arc);
        }

        DrawBlob(dc);
    }

    /// <summary>扫到极值时留 0.5° 以免弧退化；进度几乎没变就不重建几何。</summary>
    void EnsureArc()
    {
        double p = Math.Max(0, Math.Min(100, _pct));
        if (!double.IsNaN(_arcPct) && Math.Abs(p - _arcPct) < 0.05) return;
        _arcPct = p;
        _arc = Ring.Arc(Ring.Cx, Ring.Cy, Ring.R, 0, Math.Max(0.5, Math.Min(359.5, p * 3.6)));
    }

    /// <summary>两条装饰弧的填充段：从水平线（橙=9 点、蓝=3 点）起，按百分比涨到各自的 1/4 弧满量程。
    /// 进度几乎没变就不重建几何；占比为 0 时整段不画（只留浅色底槽）。</summary>
    void EnsureDeco()
    {
        double a = Math.Max(0, Math.Min(100, _pct));
        double b = Math.Max(0, Math.Min(100, _commitPct));
        if (Math.Abs(a - _decoPct) < 0.05 && Math.Abs(b - _decoCommit) < 0.05) return;
        _decoPct = a;
        _decoCommit = b;
        _decoLeftFillG = a <= 0.05 ? null
            : Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 270, 270 + Math.Max(0.5, a * 0.9));
        _decoRightFillG = b <= 0.05 ? null
            : Ring.Arc(Ring.Cx, Ring.Cy, Ring.RDeco, 90, 90 + Math.Max(0.5, b * 0.9));
    }

    void DrawBlob(DrawingContext dc)
    {
        double breath = 0.5 + 0.5 * Math.Sin(_t * 0.9);
        double rot = _t * 0.12, cs = Math.Cos(rot), sn = Math.Sin(rot);
        for (int i = 0; i < _pts.Length; i++)
        {
            var p = _pts[i];
            double n = Math.Sin(3.1 * p.X + _t * 0.7 + _phase[i])
                     * Math.Sin(2.7 * p.Y - _t * 0.5)
                     * Math.Sin(2.3 * p.Z + _t * 0.6);
            double r = Ring.RDisc * (0.79 + 0.12 * breath * _amp + 0.09 * n) * _squish;
            double x = p.X * cs + p.Z * sn;
            double z = -p.X * sn + p.Z * cs;
            double depth = (z + 1) / 2;
            int b = (int)Math.Round(depth * Buckets);
            double rad = 0.6 + depth;
            dc.DrawEllipse(_blobBrush[b], null, new Point(Ring.Cx + x * r, Ring.Cy + p.Y * r), rad, rad);
        }
    }

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