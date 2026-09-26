using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>画一个 IconFactory 里的图标（24×24 viewBox 等比缩放到 Size，描边渲染）。</summary>
sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key), typeof(string), typeof(IconView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(IconView),
        new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(IconView),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public IconView()
    {
        IsHitTestVisible = false;
        Width = 18;
        Height = 18;
        SnapsToDevicePixels = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = ActualWidth > 1 ? ActualWidth : Size;
        var pen = new Pen(Stroke, StrokeThickness * size / 24.0)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        double s = size / 24.0;
        dc.PushTransform(new ScaleTransform(s, s));
        dc.DrawGeometry(null, pen, IconFactory.Get(Key));
        dc.Pop();
    }
}

/// <summary>进程名 → 图标（照搬 zmd-manager 的 ICON_RULES）。</summary>
static class IconRules
{
    static readonly (Regex Re, string Key)[] Rules =
    {
        (new Regex(@"msedge|chrome|firefox|brave|browser|360se|qqbrowser|sogou|opera|iexplore|edge", RegexOptions.IgnoreCase), "browser"),
        (new Regex(@"code|devenv|idea|pycharm|webstorm|cursor|notepad|sublime|eclipse|studio64|rider|goland|clion|vim", RegexOptions.IgnoreCase), "code"),
        (new Regex(@"blender|maya|3dsmax|cinema|unity|unreal|obs|photoshop|illustrator|premiere|afterfx|davinci|corona|vray", RegexOptions.IgnoreCase), "layers"),
        (new Regex(@"steam|epic|wegame|valorant|league|minecraft|overwolf|roblox|game", RegexOptions.IgnoreCase), "video"),
        (new Regex(@"mysql|postgres|redis|mongod|sqlservr|oracle|sqlite|elasticsearch|etcd", RegexOptions.IgnoreCase), "db"),
        (new Regex(@"wechat|weixin|\bqq\b|dingtalk|telegram|discord|slack|feishu|lark|wecom|wxwork|skype|teams", RegexOptions.IgnoreCase), "chat"),
        (new Regex(@"music|spotify|cloudmusic|kugou|kuwo|foobar", RegexOptions.IgnoreCase), "music"),
        (new Regex(@"powershell|cmd|windowsterminal|^wt$|conhost|bash|zsh|ssh|putty|wsl|ubuntu|debian", RegexOptions.IgnoreCase), "terminal"),
        (new Regex(@"explorer|everything|totalcmd|directory|7zfm|files", RegexOptions.IgnoreCase), "folder"),
        (new Regex(@"svchost|system|csrss|wininit|services|lsass|dwm|winlogon|audiodg|spoolsv|searchindexer|msmpeng|runtimebroker", RegexOptions.IgnoreCase), "cog"),
    };

    public static string For(string text)
    {
        foreach (var (re, key) in Rules)
            if (re.IsMatch(text)) return key;
        return "app";
    }
}