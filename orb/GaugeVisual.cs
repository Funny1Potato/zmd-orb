using System;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 面板首页的大圆环（470×470，圆心硬编码在 (235,235)：给别的尺寸只会画偏，不会报错）。
///
/// 分两层建：
///   静态壳 —— 装饰弧、装饰弧内外那两条浅灰描边、弧上的细条纹、内盘、电量环的衬环与轨道。
///     构造里一次建好并 Freeze，OnRender 一次 DrawDrawing 铺完，之后每帧不再碰它们。
///   动态黄弧 —— 只有它随 Comp 变：几何在占用率变化 ≥0.05% 时用 Ring.Arc 重建，
///     三档颜色与每档那两层辉光的笔刷都在构造里预建，换档只换引用。
/// 这么分是因为面板每秒都重绘一次：原来把 28px 的装饰弧笔刷放在 OnRender 里 new，
/// 每帧白发一支 Pen（面板常驻重绘里纯属白花的托管分配），而且静态内容每帧重解一遍几何。
///
/// 数字全是观感本身（来自《终末地》协议核心电量面板那套口径），实现是自有的：
///   装饰弧 r=202/宽 28（左上琥珀、右下青蓝）、内外描边 r=221/183 宽 3、
///   细条纹径向恒定 189→215（亮度/饱和度的抖动区间见 Shift）、内盘 r=126、
///   电量环衬 r=151 宽 24、轨道 r=150 宽 18。
/// </summary>
sealed class GaugeVisual : FrameworkElement
{
    public const double Size = 470, CX = 235, CY = 235;

    // 装饰弧的角度区间（0° = 12 点，顺时针）：左上 270→360、右下 90→180
    const double LeftFrom = 270, LeftTo = 360, RightFrom = 90, RightTo = 180;

    const double DecoR = 202, DecoW = 28;                               // 装饰弧：半径 / 线宽
    const double CollarOuterR = 221, CollarInnerR = 183, CollarW = 3;   // 弧内外的浅灰描边（rails）
    const double StripeInnerR = 189, StripeOuterR = 215;                // 细条纹的径向范围
    const double DiscR = 126;                                           // 内盘
    const double SliderRailR = 151, SliderRailW = 24;                   // 电量环的浅色衬环
    const double SliderR = 150, SliderW = 18;                           // 轨道 / 进度弧

    // 进度弧的三档色，阈值 70 / 88 与 ring.js 的 arcColor 同口径（球面小环也是这三档）
    static readonly Color[] BandColor = { Ring.ArcLow, Ring.ArcMid, Ring.ArcHigh };

    /// <summary>辉光两层：外层宽、把弧缘往背景里"晕"开，内层窄、贴住弧线提亮。
    /// 原来还备过一档 +10/α.14 的中间层，但它从没进过 OnRender —— 不留死笔刷。</summary>
    static readonly (double Widen, double Alpha)[] Halo =
    {
        (16, 0.06),
        (5, 0.30),
    };

    static readonly Color Collar = Ring.Hex("#cfcfc9");   // 装饰弧内外那两条描边（比球面 Ring.Rail 深一档）
    static readonly Color Track = Ring.Hex("#f2edc4");    // 未占用段：暖白，比衬环亮一点

    readonly DrawingGroup _chrome;         // 静态壳（已冻结）
    readonly Pen[] _arcPens = new Pen[3];  // 进度弧本体，按档
    readonly Pen[][] _halos = new Pen[3][];// 每档的外→内两层辉光

    double _comp;
    Geometry? _arc;
    double _arcComp = double.NaN;          // _arc 是按哪个占用率建的

    public GaugeVisual()
    {
        _chrome = BuildChrome();
        for (int i = 0; i < 3; i++)
        {
            _arcPens[i] = Stroke(BandColor[i], SliderW, round: true);
            _halos[i] = new Pen[Halo.Length];
            for (int k = 0; k < Halo.Length; k++)
                _halos[i][k] = Stroke(BandColor[i], SliderW + Halo[k].Widen, Halo[k].Alpha, round: true);
        }
        IsHitTestVisible = false;          // 纯装饰：别在面板上挡出一块无响应区
    }

    /// <summary>综合占用（0~100）：弧长 = 占用% × 360°。</summary>
    public double Comp
    {
        get => _comp;
        set
        {
            double v = Math.Max(0, Math.Min(100, value));
            if (Math.Abs(v - _comp) < 0.01) return;   // 面板每秒推一次，抖 0.01 以内不值得重画
            _comp = v;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawDrawing(_chrome);

        // 换档不看几何重建那 0.05% 的阈值：否则占用率正好卡在 70/88 边缘时，
        // 颜色要等占用率再动 0.05% 才跳，跟数字对不上
        int band = _comp < 70 ? 0 : _comp < 88 ? 1 : 2;

        Geometry arc = ArcFor(_comp);
        if (!UiSettings.Lite)      // 轻量模式：不铺辉光，弧本身照旧
        {
            for (int k = 0; k < Halo.Length; k++) dc.DrawGeometry(null, _halos[band][k], arc);
        }
        dc.DrawGeometry(null, _arcPens[band], arc);
    }

    /// <summary>进度弧的几何：从 12 点起顺时针，弧长 = 占用 ×3.6°，两端夹在 0.5°~359.5°
    /// （与 ring.js 的 clamp 同口径；圆头笔刷在极短的弧上画成一个点，不是看不见）。</summary>
    Geometry ArcFor(double pct)
    {
        if (_arc == null || Math.Abs(pct - _arcComp) >= 0.05)
        {
            _arcComp = pct;
            _arc = Ring.Arc(CX, CY, SliderR, 0, Math.Max(0.5, Math.Min(359.5, pct * 3.6)));
        }
        return _arc;
    }

    /// <summary>静态壳：一次建好冻结。层序跟观感绑着（描边在最下、条纹压在装饰弧上、内盘压在最上），
    /// 顺手把原来每帧都现建的那支 28px 弧笔也固化进来。</summary>
    static DrawingGroup BuildChrome()
    {
        var g = new DrawingGroup();
        var collarPen = Stroke(Collar, CollarW);

        // 1) 装饰弧内外的浅灰描边：外圈 221 / 内圈 183，左右各一份
        foreach (double r in new[] { CollarOuterR, CollarInnerR })
        {
            g.Children.Add(StrokeDrawing(Ring.Arc(CX, CY, r, LeftFrom, LeftTo), collarPen));
            g.Children.Add(StrokeDrawing(Ring.Arc(CX, CY, r, RightFrom, RightTo), collarPen));
        }

        // 2) 装饰弧本体。原来在这之前还先拿 collarPen 描了一遍同样的弧，
        //    可它整条都落在 28px 的填充里（横向 188~216 包住 200.5~203.5），画了看不见
        g.Children.Add(StrokeDrawing(Ring.Arc(CX, CY, DecoR, LeftFrom, LeftTo), Stroke(Ring.DecoLeft, DecoW)));
        g.Children.Add(StrokeDrawing(Ring.Arc(CX, CY, DecoR, RightFrom, RightTo), Stroke(Ring.DecoRight, DecoW)));

        // 3) 弧上的细条纹：左右各一套，各自换个种子（同一套种子两边会像镜像）
        AddStripes(g, LeftFrom, LeftTo, Ring.DecoLeft, 31);
        AddStripes(g, RightFrom, RightTo, Ring.DecoRight, 97);

        // 4) 内盘，再压上电量环的浅色衬环（24 宽）与轨道（18 宽）——
        //    衬环只在轨道两侧各露出 2px，当"环槽"的边
        var c = new Point(CX, CY);
        g.Children.Add(new GeometryDrawing(Solid(Ring.Disc), null, new EllipseGeometry(c, DiscR, DiscR)));
        g.Children.Add(new GeometryDrawing(null, Stroke(Ring.Rail, SliderRailW), new EllipseGeometry(c, SliderRailR, SliderRailR)));
        g.Children.Add(new GeometryDrawing(null, Stroke(Track, SliderW), new EllipseGeometry(c, SliderR, SliderR)));

        g.Freeze();   // 冻结：之后每帧只管铺，也免掉跨线程访问的检查
        return g;
    }

    /// <summary>指向圆心的细条纹：径向宽度恒定（189→215，两端都压在装饰弧的 28px 带里），
    /// 切向又细又密且随机；颜色只在底弧色的 HSL 邻域里抖（见 Shift）——在 RGB 里抖会把色相
    /// 一起带偏，看着就不像"同一条弧上的纹理"了。
    /// 宽/间距与抖动都走名义分布（0.2°~0.9° / 0.25°~1.65°、±amp/2）：基线那条 LCG 在 C# 里会
    /// int32 溢出成负值（233279×9301 已越界），宽度/间距因此出现过负值（退化的窄条、彼此叠着），
    /// 抖动区间也实际成了 (−1.5amp, +0.5amp)——木纹里那几道更深的条。那些都是溢出这个 bug 的
    /// 副产品，不复刻。</summary>
    static void AddStripes(DrawingGroup g, double from, double to, Color basis, int seed)
    {
        var rnd = new Rnd(seed);
        Hsl(basis, out double h, out double s0, out double l0);
        double a = from + 1.0;                     // 两端各退 1°，别顶到装饰弧的平口上
        while (a < to - 1.0)
        {
            double a2 = a + 0.2 + rnd.Next() * 0.7;   // 切向宽度 0.2°~0.9°
            if (a2 > to - 0.5) break;
            double l = Math.Min(88, Math.Max(30, l0 + Shift(ref rnd, 14)));
            double s = Math.Min(96, Math.Max(24, s0 + Shift(ref rnd, 10)));
            // 用 Ring.Sector 的弧边而不是直线弦：0.2°~0.9° 的窄角上两者差 <0.01px，
            // 肉眼分不出，却省得自己手搓四个顶点
            g.Children.Add(new GeometryDrawing(
                Solid(Rgb(h, s, l)), null,
                Ring.Sector(CX, CY, StripeInnerR, StripeOuterR, a, a2)));
            a = a2 + 0.25 + rnd.Next() * 1.4;         // 切向间距 0.25°~1.65°
        }
    }

    /// <summary>条纹亮度/饱和度的偏移量：名义幅度是 ±amp/2。</summary>
    static double Shift(ref Rnd rnd, double amp) => (rnd.Next() - 0.5) * amp;

    /// <summary>定种子的 xorshift32。条纹是静态纹理，每次构建必须给出同一套图案
    /// （否则每开一次面板纹理都在跳）；只用它的均匀性和速度，不图强度。</summary>
    struct Rnd
    {
        uint _s;
        public Rnd(int seed) => _s = (uint)seed * 2654435761u | 1u;   // 状态不能为 0，否则 xorshift 卡死
        public double Next()
        {
            _s ^= _s << 13;
            _s ^= _s >> 17;
            _s ^= _s << 5;
            return (_s >> 8) * (1.0 / 16777216.0);
        }
    }

    /// <summary>#rrggbb → HSL（h 0~360、s/l 0~100）。</summary>
    static void Hsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, gg = c.G / 255.0, b = c.B / 255.0;
        double hi = Math.Max(r, Math.Max(gg, b)), lo = Math.Min(r, Math.Min(gg, b));
        double d = hi - lo;
        l = (hi + lo) / 2;
        if (d == 0) { h = 0; s = 0; l *= 100; return; }   // 灰：色相没定义，饱和度 0
        // s 要用 0~1 的 l 算（1-|2l-1|），所以 l 折成百分比得放到最后，别提前乘 100
        s = d / (1 - Math.Abs(2 * l - 1)) * 100;
        double k = hi == r ? (gg - b) / d + (gg < b ? 6 : 0)   // 红区跨过 360° 要补一圈
                 : hi == gg ? (b - r) / d + 2
                 : (r - gg) / d + 4;
        h = k * 60;
        l *= 100;
    }

    /// <summary>HSL → #rrggbb（h 任意，自动归一到 0~360）。</summary>
    static Color Rgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s /= 100; l /= 100;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        switch ((int)(h / 60))
        {
            case 0: r = c; g = x; b = 0; break;
            case 1: r = x; g = c; b = 0; break;
            case 2: r = 0; g = c; b = x; break;
            case 3: r = 0; g = x; b = c; break;
            case 4: r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255),
                             (byte)Math.Round((b + m) * 255));
    }

    static GeometryDrawing StrokeDrawing(Geometry geo, Pen pen) => new(null, pen, geo);

    /// <summary>一支冻结的实色笔。α 走 8 位颜色而不是笔刷 Opacity：辉光本来就是按字节折算的，
    /// 少一层 float→byte 的舍入，逐像素对拍时不至于在弧缘糊出末位差。</summary>
    static Pen Stroke(Color c, double w, double alpha = 1.0, bool round = false)
    {
        var pen = new Pen(Solid(c, alpha), w);
        if (round) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        pen.Freeze();
        return pen;
    }

    static SolidColorBrush Solid(Color c, double alpha = 1.0)
    {
        var b = new SolidColorBrush(alpha >= 1.0 ? c
            : Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}