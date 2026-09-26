using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 双指标走势折线图（设备页与"应用内存"页共用）。
/// 主指标（Hist）画黄线并在其下方铺一层同色渐变，次指标（Hist2）画蓝线；
/// 两条线各自在末点画圆点，中部有一条 50% 参考线。底色沿用设计语言的 #a9a9a5。
/// 纵轴固定 0~100%，不自动放大（免得把小占用画成满格）。
/// </summary>
sealed class SparkVisual : FrameworkElement
{
    static readonly Brush Bg = Freeze(new SolidColorBrush(Ring.Hex("#a9a9a5")));
    static readonly Brush Grid = Freeze(new SolidColorBrush(Ring.Hex("#b9b9b4")));
    static readonly Pen GridPen = FreezePen(new Pen(Grid, 1));

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(SparkVisual),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>每次采样由行对象自增：用它触发重绘（数组类型的 DP 在模板里 XAML 解析不了）。</summary>
    public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
        nameof(Tick), typeof(int), typeof(SparkVisual),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Line1Property = DependencyProperty.Register(
        nameof(Line1), typeof(Brush), typeof(SparkVisual),
        new FrameworkPropertyMetadata(FrozenBrush("#ded24e"), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Line2Property = DependencyProperty.Register(
        nameof(Line2), typeof(Brush), typeof(SparkVisual),
        new FrameworkPropertyMetadata(FrozenBrush("#3ba3dc"), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>行对象（ITrendRow）。</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public int Tick
    {
        get => (int)GetValue(TickProperty);
        set => SetValue(TickProperty, value);
    }

    /// <summary>主指标线色（默认黄 = 已占用）。</summary>
    public Brush Line1
    {
        get => (Brush)GetValue(Line1Property);
        set => SetValue(Line1Property, value);
    }

    /// <summary>次指标线色（默认蓝 = 已提交）。</summary>
    public Brush Line2
    {
        get => (Brush)GetValue(Line2Property);
        set => SetValue(Line2Property, value);
    }

    public SparkVisual()
    {
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));

        if (Data is not ITrendRow row) return;
        var a = row.Hist;
        var b = row.Hist2;
        if (a.Count < 2) return;

        double padTop = Math.Round(h * 0.16), padBot = 3;
        double zone = h - padTop - padBot;
        if (zone < 4) return;
        dc.DrawLine(GridPen, new Point(0, padTop + zone * 0.5), new Point(w, padTop + zone * 0.5));

        double step = w / (a.Count - 1);
        Point P(List<double> src, int i)
        {
            double v = Math.Max(0, Math.Min(100, src[i])) / 100.0;
            return new Point(i * step, padTop + zone * (1 - v));
        }

        // 主指标：折线 + 线下渐变
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            lc.BeginFigure(P(a, 0), false, false);
            ac.BeginFigure(new Point(0, h), true, true);
            ac.LineTo(P(a, 0), true, false);
            for (int i = 1; i < a.Count; i++)
            {
                lc.LineTo(P(a, i), true, false);
                ac.LineTo(P(a, i), true, false);
            }
            ac.LineTo(new Point((a.Count - 1) * step, h), true, false);
        }
        line.Freeze();
        area.Freeze();

        dc.DrawGeometry(AreaFill(Line1, 96, 10), null, area);
        dc.DrawGeometry(null, GlowPen(Line1, 3.4, 70), line);
        dc.DrawGeometry(null, SolidPen(Line1, 1.6), line);
        dc.DrawEllipse(Line1, null, P(a, a.Count - 1), 2.4, 2.4);

        // 次指标：只画线（不铺填充，免得两条叠在一起糊）
        if (b.Count >= 2)
        {
            var line2 = new StreamGeometry();
            using (var lc = line2.Open())
            {
                lc.BeginFigure(P(b, 0), false, false);
                for (int i = 1; i < b.Count; i++) lc.LineTo(P(b, i), true, false);
            }
            line2.Freeze();
            dc.DrawGeometry(null, GlowPen(Line2, 3.2, 60), line2);
            dc.DrawGeometry(null, SolidPen(Line2, 1.5), line2);
            dc.DrawEllipse(Line2, null, P(b, b.Count - 1), 2.2, 2.2);
        }
    }

    /* ---- 画刷/画笔构造（按需缓存，避免每帧新建） ---- */
    static readonly Dictionary<string, Pen> PenCache = new();

    static Pen SolidPen(Brush brush, double w)
    {
        string k = "s" + brush.GetHashCode() + "_" + w;
        if (PenCache.TryGetValue(k, out var p)) return p;
        p = new Pen(brush, w)
        { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        PenCache[k] = p;
        return p;
    }

    static Pen GlowPen(Brush brush, double w, byte alpha)
    {
        string k = "g" + brush.GetHashCode() + "_" + w;
        if (PenCache.TryGetValue(k, out var p)) return p;
        var c = ((SolidColorBrush)brush).Color;
        p = new Pen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)), w)
        { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        PenCache[k] = p;
        return p;
    }

    static readonly Dictionary<string, Brush> AreaCache = new();

    static Brush AreaFill(Brush brush, byte top, byte bottom)
    {
        string k = "a" + brush.GetHashCode();
        if (AreaCache.TryGetValue(k, out var b)) return b;
        var c = ((SolidColorBrush)brush).Color;
        var g = new LinearGradientBrush(Color.FromArgb(top, c.R, c.G, c.B),
                                        Color.FromArgb(bottom, c.R, c.G, c.B), 90);
        g.Freeze();
        AreaCache[k] = g;
        return g;
    }

    static Brush FrozenBrush(string hex)
    {
        var b = new SolidColorBrush(Ring.Hex(hex));
        b.Freeze();
        return b;
    }

    static Pen FreezePen(Pen p)
    {
        p.Freeze();
        return p;
    }

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}