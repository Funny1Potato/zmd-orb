using System;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>环形几何与配色。参数与 frontend/ring.js 一一对应（沿用 zmd-manager 的电量环口径）。</summary>
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
    public const double OutlineW = 1.8;  // 描边厚度（单侧）

    public static readonly Color Rail = Hex("#d3d3ce");
    public static readonly Color Outline = Hex("#9a9a94");   // 描边：比衬环深一档，浮在任意桌面上都看得清
    public static readonly Color Track = Hex("#f2edc4");
    public static readonly Color ArcLow = Hex("#ffe23d");
    public static readonly Color ArcMid = Hex("#ecb063");
    public static readonly Color ArcHigh = Hex("#e8703a");
    public static readonly Color DecoLeft = Hex("#ecb063");
    public static readonly Color DecoRight = Hex("#7fb2cc");
    public static readonly Color Disc = Hex("#ededea");
    public static readonly Color BlobRgb = Hex("#60605c");

    public static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    /// <summary>0° = 12 点方向，顺时针为正。</summary>
    public static Point Polar(double cx, double cy, double r, double deg)
    {
        double a = (deg - 90) * Math.PI / 180;
        return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }

    /// <summary>arcPath 的等价物：弧长 &gt; 180° 时置 large-arc 标志，端点越界自动补一圈。</summary>
    public static Geometry Arc(double cx, double cy, double r, double from, double to)
    {
        if (to < from) to += 360;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Polar(cx, cy, r, from), false, false);
            c.ArcTo(Polar(cx, cy, r, to), new Size(r, r), 0,
                    to - from > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>占用越高越警示：&lt;70% 亮黄、&lt;88% 琥珀、否则橙红。</summary>
    public static Color ArcColor(double pct) => pct < 70 ? ArcLow : pct < 88 ? ArcMid : ArcHigh;
}