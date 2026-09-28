using System;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>环形几何与配色。这一表参数是球面的唯一口径，`frontend/ring.js` 是它在浏览器里的对照版。
/// 配色取自《明日方舟：终末地》协议核心电量面板的那一套（灰衬环 / 淡黄轨道 / 亮黄进度弧 / 浅内盘）。</summary>
static class Ring
{
    public const double Size = 160;      // 球窗口边长
    public const double Cx = 80, Cy = 80;
    public const double R = 52;          // 电量环半径
    public const double WTrack = 6.5;    // 轨道 / 进度弧线宽
    public const double RDeco = 70;      // 装饰弧半径（在环外侧）
    public const double WDeco = 9.7;
    public const double RDisc = 43;      // 内盘半径（粒子团背景）
    public const int Particles = 120;
    public const int Fps = 30;           // 球常驻，限 30fps
    public const double OutlineW = 1.1;  // 描边厚度（单侧；比早先的 1.8 细一档，按反馈再细）
    public const double DiscAlpha = 0.70; // 内盘的半透量级（磨砂盘的 alpha 停靠点照它算；盘缘最后 8 DIP 渐隐到 0）
    // ↑ 取 0.70：纯黑底上读到 179，与主环未占用轨道（#f8f6e6 叠 0.72）同一亮度。半透盘在深色壁纸上
    //   是"按背景比例变暗"的（见 README"中间内盘"一条），α 太低中间就会成一个暗洞。

    public static readonly Color Rail = Hex("#d3d3ce");
    public static readonly Color Outline = Hex("#9a9a94");   // 深一档的描边色（当前只在大纲/命中测试用）
    public static readonly Color Base = Hex("#f2f1ec");      // 环与两侧计量条的**底衬**：浅色半透，像磨砂底托
                                                             // （原来是 #9a9a94 灰，看着灰蒙蒙，按反馈改白）
    public static readonly Color Track = Hex("#f8f6e6");     // 主环未占用段（比原来更浅，几乎只剩一点暖调）
    public static readonly Color ArcLow = Hex("#ffe23d");
    public static readonly Color ArcMid = Hex("#ecb063");
    public static readonly Color ArcHigh = Hex("#e8703a");
    public static readonly Color DecoLeft = Hex("#ecb063");
    public static readonly Color DecoRight = Hex("#7fb2cc");
    // 计量条底槽：各自弧色的**浅色版**（与弧同色系），再叠半透明
    public static readonly Color DecoLeftTrack = Hex("#f3d6b9");
    public static readonly Color DecoRightTrack = Hex("#cbe0ed");
    public static readonly Color Disc = Hex("#ededea");
    public static readonly Color FrostDot = Hex("#ffffff");      // 内盘磨砂纹理的点色
    public static readonly Color BlobRgb = Hex("#60605c");

    public static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    /// <summary>Halton 低差异序列的第 n 项（给定进制）。球与面板的粒子团都用它撒点：
    /// u = Halton(i,2) 定纬度、v = Halton(i,3) 定经度 —— 均匀，且不像等分角的螺旋那样留条纹。</summary>
    public static double Halton(int n, int b)
    {
        double f = 1, r = 0;
        while (n > 0)
        {
            f /= b;
            r += f * (n % b);
            n /= b;
        }
        return r;
    }

    /// <summary>圆上取点。口径是"0° = 12 点方向、顺时针为正"，于是直接按 sin/cos 组合写即可 ——
    /// 等价于"先把角度减掉 90° 再取 cos/sin"，少一步。</summary>
    public static Point Polar(double cx, double cy, double r, double deg)
    {
        double t = deg * Math.PI / 180;
        return new Point(cx + r * Math.Sin(t), cy - r * Math.Cos(t));
    }

    /// <summary>一段弧（只描边、不闭合）。两件事要自己管：to 比 from 小时说明是"跨过 12 点"的写法，
    /// 得补一圈；弧长超过 180° 必须置 large-arc，否则会挑短的那条弧画。</summary>
    public static Geometry Arc(double cx, double cy, double r, double from, double to)
    {
        double sweep = to < from ? to + 360 : to;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Polar(cx, cy, r, from), false, false);
            c.ArcTo(Polar(cx, cy, r, sweep), new Size(r, r), 0,
                    sweep - from > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>圆环扇段（外弧 + 内弧回环 + 闭合）。用它做"浅色底 + 深色描边"：
    /// 描边能顺着形状把**两端平头**也描上，这是"粗描边垫在底下、再压一条细的"那套做不到的
    /// （那套只能露出两条长边，且底衬颜色会被描边色带灰）。</summary>
    public static Geometry Sector(double cx, double cy, double rIn, double rOut, double from, double to)
    {
        double sweep = to < from ? to + 360 : to;
        bool large = sweep - from > 180;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Polar(cx, cy, rOut, from), true, true);
            c.ArcTo(Polar(cx, cy, rOut, sweep), new Size(rOut, rOut), 0, large,
                     SweepDirection.Clockwise, true, false);
            c.LineTo(Polar(cx, cy, rIn, sweep), true, false);
            c.ArcTo(Polar(cx, cy, rIn, from), new Size(rIn, rIn), 0, large,
                     SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>整圈圆环：外圆 + 内圆的 EvenOdd 差集。</summary>
    public static Geometry Annulus(double cx, double cy, double rIn, double rOut)
    {
        var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
        g.Children.Add(new EllipseGeometry(new Point(cx, cy), rOut, rOut));
        g.Children.Add(new EllipseGeometry(new Point(cx, cy), rIn, rIn));
        g.Freeze();
        return g;
    }

    /// <summary>占用越高越警示：&lt;70% 亮黄、&lt;88% 琥珀、否则橙红。</summary>
    public static Color ArcColor(double pct)
    {
        if (pct < 70) return ArcLow;
        return pct < 88 ? ArcMid : ArcHigh;
    }
}