using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZmdOrb;

/// <summary>任务管理器面板。M0 只有四张读数卡；进程表在 M3。</summary>
public partial class PanelWindow : Window
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _polling, _quitting;

    /// <summary>球上报的粒子团实测帧率。</summary>
    public Func<double>? BallFps { get; set; }

    public PanelWindow()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Loaded += (_, _) => { Poll(); PreviewKeyDown += OnPreviewKeyDown; };
    }

    public void PrepareQuit() => _quitting = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitting) return;      // 关窗只收起，退出走 ✕ / QuitApp
        e.Cancel = true;
        Hide();
    }

    async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await MemoryApi.GetAsync();
            if (s == null)
            {
                stateLine.Text = "采集端未启动（127.0.0.1:8910）";
                return;
            }

            Set(cPct, s.Pct.ToString("F1") + "%", s.Pct >= 88);
            Set(cAvail, (s.AvailMb / 1024).ToString("F1") + " GB", false);
            Set(cCache, s.SystemCacheMb > 0 ? (s.SystemCacheMb / 1024).ToString("F2") + " GB" : "—", false);
            bool warn = s.CommitPct >= 85;
            Set(cCommit, (s.CommittedMb / 1024).ToString("F1") + " / " + (s.CommitLimitMb / 1024).ToString("F1")
                       + " GB · " + s.CommitPct.ToString("F0") + "%", warn);

            stateLine.Text = "数据来自本地采集端 127.0.0.1:8910，每秒刷新。"
                + (warn ? "提交压力偏高（本机页面文件小，注意 JVM/大程序）" : "实时")
                + "。球 " + (BallFps?.Invoke() ?? 0).ToString("F0") + " fps";
        }
        finally { _polling = false; }
    }

    static void Set(System.Windows.Controls.TextBlock el, string text, bool warn)
    {
        el.Text = text;
        el.Foreground = warn ? Warn : Normal;
    }

    static readonly Brush Normal = Frozen("#3f3f3a");
    static readonly Brush Warn = Frozen("#c2703a");

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush(Ring.Hex(hex));
        b.Freeze();
        return b;
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }

    void ToBall_Click(object sender, RoutedEventArgs e) => Hide();

    void Quit_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.QuitApp();
}