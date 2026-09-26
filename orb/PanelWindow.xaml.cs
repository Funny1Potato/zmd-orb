using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZmdOrb;

/// <summary>任务管理器面板。M1：三档整理真生效；进程表在 M3。</summary>
public partial class PanelWindow : Window
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _polling, _quitting, _cleaning;

    /// <summary>球上报的粒子团实测帧率。</summary>
    public Func<double>? BallFps { get; set; }

    public PanelWindow()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        Loaded += (_, _) =>
        {
            Poll();
            PreviewKeyDown += OnPreviewKeyDown;
            _ = LoadLastResultAsync();
        };
    }

    public void PrepareQuit() => _quitting = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitting) return;      // 关窗只收起，退出走 ✕ / QuitApp
        e.Cancel = true;
        Hide();
    }

    /* ---------------- 取数 ---------------- */

    async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await MemoryApi.GetAsync();
            if (s == null)
            {
                statusLine.Text = "采集端未启动（127.0.0.1:8910）——球此时取不到数，也不会执行整理。";
                return;
            }
            Set(cPct, s.Pct.ToString("F1") + "%", s.Pct >= 88);
            Set(cAvail, (s.AvailMb / 1024).ToString("F1") + " GB", false);
            Set(cFreeZero, (s.FreeZeroMb / 1024).ToString("F1") + " GB", s.FreeZeroMb / 1024 < 1.0);
            Set(cStandby, s.StandbyMb / 1024 >= 1 ? (s.StandbyMb / 1024).ToString("F1") + " GB" : "—", false);
            Set(cCache, s.SystemCacheMb > 0 ? (s.SystemCacheMb / 1024).ToString("F2") + " GB" : "—", false);
            bool warn = s.CommitPct >= 85;
            Set(cCommit, (s.CommittedMb / 1024).ToString("F1") + " / " + (s.CommitLimitMb / 1024).ToString("F1")
                       + " GB · " + s.CommitPct.ToString("F0") + "%", warn);

            adminHint.Text = s.Admin
                ? "当前已是管理员：三档都不弹 UAC"
                : "深度/全部整理要管理员，点了会弹一次 UAC";
        }
        finally { _polling = false; }
    }

    async System.Threading.Tasks.Task LoadLastResultAsync()
    {
        var r = await MemoryApi.LastCleanAsync();
        if (r != null && !string.IsNullOrEmpty(r.Detail))
            lastResult.Text = $"最近一次（{r.Tier}）：{r.Detail}";
    }

    /* ---------------- 三档整理 ---------------- */

    void CleanL1_Click(object sender, RoutedEventArgs e) => RunClean("l1", "轻度整理");
    void CleanL2_Click(object sender, RoutedEventArgs e) => RunClean("l2", "深度整理");
    void CleanL3_Click(object sender, RoutedEventArgs e) => RunClean("l3", "全部整理");

    async void RunClean(string tier, string label)
    {
        if (_cleaning) return;
        _cleaning = true;
        BtnL1.IsEnabled = BtnL2.IsEnabled = BtnL3.IsEnabled = false;
        statusLine.Text = label + "中…" + (tier == "l1" ? "" : "（会弹一次管理员授权，请在 UAC 对话框上点“是”）");
        Diag.Log($"面板：{label}（tier={tier}）开始");
        try
        {
            var r = await MemoryApi.CleanAsync(tier);
            if (r.Ok)
            {
                lastResult.Text = $"最近一次（{r.Tier}）：{r.Detail}";
                statusLine.Text = r.Summary + "。冷却 30 秒。";
                Diag.Log($"面板：{label} 完成 → {r.Summary}｜{r.Detail}");
            }
            else
            {
                statusLine.Text = "没能完成：" + (string.IsNullOrEmpty(r.Error) ? "未知原因" : r.Error);
                Diag.Log($"面板：{label} 未完成 → {r.Error}");
            }
        }
        finally
        {
            _cleaning = false;
            BtnL1.IsEnabled = BtnL2.IsEnabled = BtnL3.IsEnabled = true;
            Poll();                      // 立刻刷新一次读数
        }
    }

    /* ---------------- 杂项 ---------------- */

    static void Set(TextBlock el, string text, bool warn)
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