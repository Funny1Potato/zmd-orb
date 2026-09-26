using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 面板首页的大圆环（470px）。几何与画法照搬 zmd-manager 的 index.html：
///   装饰弧 r=202 宽 28（左琥珀 #ecb063 / 右青蓝 #7fb2cc）
///   装饰弧内外各一条 3px 浅灰描边（rails，r=221 / r=183，#cfcfc9）
///   装饰弧上叠"指向圆心的细条纹"：径向宽度恒定（189→215），切向宽度/间距随机且密，
///     颜色在底弧色的邻域内抖动（±14% 亮度 / ±10% 饱和度），随机种子 31/97 与参考一致
///   内盘 r=126 #ededea；电量环：衬环 r=151 宽 24、轨道 r=150 宽 18 #f2edc4、
///     进度弧 r=150 宽 18 #ffe23d 圆头 + 7px 黄色辉光，从 12 点起顺时针、弧长 = 占用×360°
/// </summary>
sealed class GaugeVisual : FrameworkElement
{
    public const double Size = 470, CX = 235, CY = 235;
    const double RDeco = 202, WDeco = 28;
    const double RRailOut = 221, RRailIn = 183, WRail = 3;
    const double StripeIn = 189, StripeOut = 215;
    const double RDisc = 126;
    const double RRingRail = 151, WRingRail = 24;
    const double RTrack = 150, WTrack = 18;

    static readonly Color DecoLeft = Ring.Hex("#ecb063");
    static readonly Color DecoRight = Ring.Hex("#7fb2cc");
    static readonly Color Rail = Ring.Hex("#cfcfc9");
    static readonly Color Disc = Ring.Hex("#ededea");
    static readonly Color RingRail = Ring.Hex("#d3d3ce");
    static readonly Color Track = Ring.Hex("#f2edc4");
    static readonly Color Yellow = Ring.Hex("#ffe23d");

    readonly Geometry _baseL, _baseR;
    readonly Geometry[] _rails;
    readonly DrawingGroup _stripes = new();
    readonly Pen _railPen, _rangRailPen, _trackPen;
    readonly Brush _discBrush;
    readonly Pen[] _arcPens = new Pen[3], _glows = new Pen[3];
    Pen _arcPen, _glowNear, _glowMid, _glowFar;
    double _comp;
    Geometry? _arc;
    double _arcComp = double.NaN;

    public GaugeVisual()
    {
        _baseL = Ring.Arc(CX, CY, RDeco, 270, 360);
        _baseR = Ring.Arc(CX, CY, RDeco, 90, 180);
        _rails = new[]
        {
            Ring.Arc(CX, CY, RRailOut, 270, 360), Ring.Arc(CX, CY, RRailIn, 270, 360),
            Ring.Arc(CX, CY, RRailOut, 90, 180), Ring.Arc(CX, CY, RRailIn, 90, 180),
        };
        _railPen = Pen(Rail, WRail);

        // 条纹：一次性建好冻结（静态内容，不随数据变）
        var lg = new DrawingGroup();
        BuildStripes(lg, 270, 360, DecoLeft, 31);
        BuildStripes(lg, 90, 180, DecoRight, 97);
        lg.Freeze();
        _stripes = lg;

        _discBrush = Freeze(new SolidColorBrush(Disc));
        _rangRailPen = Pen(RingRail, WRingRail);
        _trackPen = Pen(Track, WTrack);
        double sweep0 = 0.5;
        _arc = Ring.Arc(CX, CY, RTrack, 0, sweep0);
        _arcComp = 0;
        for (int i = 0; i < 3; i++)
        {
            Color c = i == 0 ? Yellow : (i == 1 ? DecoLeft : Ring.Hex("#e8703a"));
            _arcPens[i] = Pen(c, WTrack, 1.0, round: true);
            _glows[i] = Glow(c, WTrack + 8, 0.30);
        }
        _arcPen = _arcPens[0];
        _glowNear = _glows[0];
        _glowMid = _glowNear;
        _glowFar = _glowNear;
        IsHitTestVisible = false;
    }

    /// <summary>综合占用（0~100）：弧长 = 占用% × 360°。</summary>
    public double Comp
    {
        get => _comp;
        set
        {
            double v = Math.Max(0, Math.Min(100, value));
            if (Math.Abs(v - _comp) < 0.01) return;
            _comp = v;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        // 装饰弧基底 + 内外描边
        dc.DrawGeometry(null, _railPen, _rails[0]);
        dc.DrawGeometry(null, _railPen, _rails[1]);
        dc.DrawGeometry(null, _railPen, _rails[2]);
        dc.DrawGeometry(null, _railPen, _rails[3]);
        dc.DrawGeometry(null, _railPen, _baseL);
        dc.DrawGeometry(null, _railPen, _baseR);
        dc.DrawGeometry(null, Pen(DecoLeft, WDeco), _baseL);
        dc.DrawGeometry(null, Pen(DecoRight, WDeco), _baseR);
        dc.DrawDrawing(_stripes);

        var c = new Point(CX, CY);
        dc.DrawEllipse(_discBrush, null, c, RDisc, RDisc);
        dc.DrawEllipse(null, _rangRailPen, c, RRingRail, RRingRail);
        dc.DrawEllipse(null, _trackPen, c, RTrack, RTrack);

        EnsureArc();
        if (_arc != null)
        {
            dc.DrawGeometry(null, _glowFar, _arc);
            dc.DrawGeometry(null, _glowNear, _arc);
            dc.DrawGeometry(null, _arcPen, _arc);
        }
    }

    void EnsureArc()
    {
        if (!double.IsNaN(_arcComp) && Math.Abs(_comp - _arcComp) < 0.05) return;
        _arcComp = _comp;
        _arc = Ring.Arc(CX, CY, RTrack, 0, Math.Max(0.5, Math.Min(359.5, _comp * 3.6)));
        int idx = _comp < 70 ? 0 : _comp < 88 ? 1 : 2;
        _arcPen = _arcPens[idx];
        Color c = idx == 0 ? Yellow : (idx == 1 ? DecoLeft : Ring.Hex("#e8703a"));
        _glowNear = Glow(c, WTrack + 5, 0.30);
        _glowMid = Glow(c, WTrack + 10, 0.14);
        _glowFar = Glow(c, WTrack + 16, 0.06);
    }

    static Pen Glow(Color c, double w, double a)
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)), w)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    /// <summary>指向圆心的细条纹（照搬 buildStripes：LCG 随机 + HSL 邻域抖动）。</summary>
    static void BuildStripes(DrawingGroup into, double from, double to, Color baseColor, int seed)
    {
        double ri = StripeIn, ro = StripeOut;
        Hsl( baseColor, out double bh, out double bs, out double bl);
        double a = from + 1.0;
        while (a < to - 1.0)
        {
            double stripeW = 0.2 + Next(ref seed) * 0.7;
            double gap = 0.25 + Next(ref seed) * 1.4;
            double a2 = a + stripeW;
            if (a2 > to - 0.5) break;
            double l = Math.Min(88, Math.Max(30, bl + (Next(ref seed) - 0.5) * 14));
            double s = Math.Min(96, Math.Max(24, bs + (Next(ref seed) - 0.5) * 10));
            var p1 = Ring.Polar(CX, CY, ri, a);
            var p2 = Ring.Polar(CX, CY, ro, a);
            var p3 = Ring.Polar(CX, CY, ro, a2);
            var p4 = Ring.Polar(CX, CY, ri, a2);
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(p1, true, true);
                gc.LineTo(p2, true, false);
                gc.LineTo(p3, true, false);
                gc.LineTo(p4, true, false);
            }
            g.Freeze();
            var br = Freeze(new SolidColorBrush(FromHsl(bh, s, l)));
            into.Children.Add(new GeometryDrawing(br, null, g));
            a = a2 + gap;
        }
    }

    static double Next(ref int seed)
    {
        seed = (seed * 9301 + 49297) % 233280;
        return seed / 233280.0;
    }

    static void Hsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
        l = (mx + mn) / 2;
        h = 0; s = 0;
        if (d > 0)
        {
            s = d / (1 - Math.Abs(2 * l - 1));
            if (mx == r) h = ((g - b) / d) % 6;
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        s *= 100;
        l *= 100;
    }

    static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s /= 100; l /= 100;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255),
                             (byte)Math.Round((b + m) * 255));
    }

    static Pen Pen(Color c, double w, double opacity = 1.0, bool round = false)
    {
        var p = new Pen(new SolidColorBrush(c) { Opacity = opacity }, w);
        if (round) p.StartLineCap = p.EndLineCap = PenLineCap.Round;
        p.Freeze();
        return p;
    }

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}