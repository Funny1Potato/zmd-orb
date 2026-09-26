using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 设备性能页的"占用率走势"黄柱图。画法照搬 zmd-manager 的 drawChart()：
/// 底色 #a9a9a5、顶部 16% 留灰带不画柱、柱宽 max(1.5, w/n - 0.6)、
/// 柱色 rgb(213,199,58)，透明度 0.80+((i*7)%5)*0.05（细密深浅交替）。
/// 数据直接引用 DeviceRow.Hist（每采样推一个点，最多 180 个）。
/// </summary>
sealed class SparkVisual : FrameworkElement
{
    static readonly Brush Bg = Freeze(new SolidColorBrush(Ring.Hex("#a9a9a5")));
    static readonly Color BarColor = Color.FromRgb(213, 199, 58);

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(SparkVisual),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>每采样一次由行对象自增：单独用它触发重绘（数组类型的 DP 在模板里 XAML 解析不了）。</summary>
    public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
        nameof(Tick), typeof(int), typeof(SparkVisual),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    readonly Brush[] _bars = new Brush[5];

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
        for (int k = 0; k < 5; k++)
        {
            var b = new SolidColorBrush(BarColor) { Opacity = 0.80 + k * 0.05 };
            b.Freeze();
            _bars[k] = b;
        }
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        var hist = (Data as DeviceRow)?.Hist;
        int n = hist?.Count ?? 0;
        if (n == 0) return;
        double bandH = Math.Round(h * 0.16);
        double zoneH = h - bandH;
        double bw = w / n;
        double barW = Math.Max(1.5, bw - 0.6);
        for (int i = 0; i < n; i++)
        {
            double v = Math.Max(0, Math.Min(100, hist![i])) / 100.0;
            double bh = Math.Max(2, v * zoneH);
            dc.DrawRectangle(_bars[(i * 7) % 5], null, new Rect(i * bw, h - bh, barW, bh));
        }
    }

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}