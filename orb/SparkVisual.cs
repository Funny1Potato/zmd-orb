using System;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 设备性能页的"占用率走势"折线图（原来是照 zmd-manager 的细密柱状，180 个点太密，改成折线）。
/// 风格沿用设计语言：底色 #a9a9a5、线色用参考的柱色 #ded24e、线下方一层同色渐变填充，
/// 末点画一个圆点，中部加一条 50% 参考线（便于判断量级）。
/// 纵轴固定 0~100%（不自动放大，免得把小占用画成满格）。
/// 数据直接引用 DeviceRow.Hist；Tick 变化即重绘。
/// </summary>
sealed class SparkVisual : FrameworkElement
{
    static readonly Brush Bg = Freeze(new SolidColorBrush(Ring.Hex("#a9a9a5")));
    static readonly Brush Grid = Freeze(new SolidColorBrush(Ring.Hex("#b9b9b4")));
    static readonly Color LineColor = Ring.Hex("#ded24e");
    static readonly Pen LinePen;
    static readonly Pen LineGlow;
    static readonly Brush Dot;
    static readonly Brush AreaFill;

    static SparkVisual()
    {
        LinePen = new Pen(new SolidColorBrush(LineColor), 1.6)
        { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        LinePen.Freeze();
        LineGlow = new Pen(new SolidColorBrush(Color.FromArgb(70, LineColor.R, LineColor.G, LineColor.B)), 3.4)
        { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        LineGlow.Freeze();
        var dot = new SolidColorBrush(LineColor);
        dot.Freeze();
        Dot = dot;
        var g = new LinearGradientBrush(
            Color.FromArgb(96, LineColor.R, LineColor.G, LineColor.B),
            Color.FromArgb(10, LineColor.R, LineColor.G, LineColor.B), 90);
        g.Freeze();
        AreaFill = g;
    }

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(SparkVisual),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>每采样一次由行对象自增：单独用它触发重绘（数组类型的 DP 在模板里 XAML 解析不了）。</summary>
    public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
        nameof(Tick), typeof(int), typeof(SparkVisual),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>行对象（DeviceRow）。</summary>
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

    public SparkVisual()
    {
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));

        var hist = (Data as DeviceRow)?.Hist;
        int n = hist?.Count ?? 0;
        if (n < 2) return;

        double padTop = Math.Round(h * 0.16), padBot = 3;
        double zone = h - padTop - padBot;
        if (zone < 4) return;
        // 50% 参考线
        dc.DrawLine(new Pen(Grid, 1), new Point(0, padTop + zone * 0.5), new Point(w, padTop + zone * 0.5));

        // 折线 + 线下渐变填充（一起构建，填充用同一批点闭合到底边）
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        double step = w / (n - 1);
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            Point P(int i)
            {
                double v = Math.Max(0, Math.Min(100, hist![i])) / 100.0;
                return new Point(i * step, padTop + zone * (1 - v));
            }
            lc.BeginFigure(P(0), false, false);
            ac.BeginFigure(new Point(0, h), true, true);
            ac.LineTo(P(0), true, false);
            for (int i = 1; i < n; i++)
            {
                lc.LineTo(P(i), true, false);
                ac.LineTo(P(i), true, false);
            }
            ac.LineTo(new Point((n - 1) * step, h), true, false);
        }
        line.Freeze();
        area.Freeze();

        dc.DrawGeometry(AreaFill, null, area);
        dc.DrawGeometry(null, LineGlow, line);      // 一层很淡的描边让它浮起来
        dc.DrawGeometry(null, LinePen, line);

        // 末点
        double last = Math.Max(0, Math.Min(100, hist![n - 1])) / 100.0;
        dc.DrawEllipse(Dot, null, new Point((n - 1) * step, padTop + zone * (1 - last)), 2.4, 2.4);
    }

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}