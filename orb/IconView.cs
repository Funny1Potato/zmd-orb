using System;
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

/// <summary>
/// 进程名 / 窗口标题 → 图标键。按关键词分组归类：第一组命中的胜出，都没命中就用 "app"。
/// 分组有序、组内按词表顺序试，所以词表要"越具体越靠前"（例如 windowsterminal 得排在别的
/// terminal 词前面）。命中判断是子串包含，词表里不要放太短的通用词。
/// </summary>
static class IconRules
{
    static readonly (string Key, string[] Words)[] Groups =
    {
        ("browser", new[] { "msedge", "chrome", "firefox", "brave", "vivaldi", "opera",
                            "360se", "qqbrowser", "sogou", "iexplore", "edge", "browser" }),
        ("code", new[] { "devenv", "pycharm", "webstorm", "goland", "clion", "rider",
                         "androidstudio", "studio64", "sublime", "notepad", "eclipse",
                         "cursor", "idea", "vim", "code" }),
        ("layers", new[] { "photoshop", "illustrator", "premiere", "afterfx", "davinci",
                           "blender", "3dsmax", "cinema", "unity", "unreal", "corona",
                           "vray", "maya", "obs" }),
        ("video", new[] { "steam", "wegame", "valorant", "league", "minecraft", "roblox",
                          "overwolf", "epic", "game" }),
        ("db", new[] { "elasticsearch", "postgres", "sqlservr", "mongod", "sqlite",
                       "oracle", "redis", "mysql", "etcd" }),
        ("chat", new[] { "dingtalk", "telegram", "discord", "feishu", "wxwork", "wecom",
                         "weixin", "wechat", "skype", "teams", "slack", "lark", "qq" }),
        ("music", new[] { "cloudmusic", "spotify", "kugou", "kuwo", "foobar", "music" }),
        ("terminal", new[] { "windowsterminal", "powershell", "conhost", "putty",
                             "ubuntu", "debian", "wsl", "cmd", "bash", "zsh", "ssh" }),
        ("folder", new[] { "everything", "totalcmd", "explorer", "directory", "7zfm", "files" }),
        ("cog", new[] { "searchindexer", "runtimebroker", "svchost", "winlogon", "spoolsv",
                        "audiodg", "services", "wininit", "csrss", "lsass", "msmpeng",
                        "system", "dwm" }),
    };

    public static string For(string text)
    {
        string t = (text ?? string.Empty).ToLowerInvariant();
        if (t.Length == 0) return "app";
        foreach (var (key, words) in Groups)
            foreach (var w in words)
                if (t.Contains(w)) return key;
        return "app";
    }
}