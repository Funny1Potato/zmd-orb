using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZmdOrb;

/// <summary>任务管理器面板。M1 三档整理真生效；M2 加硬缺页率与自动整理开关；进程表在 M3。</summary>
public partial class PanelWindow : Window
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _polling, _quitting, _cleaning, _autoBusy;

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
            // 硬缺页率是整理的真实代价：清完那些页，程序下次碰到它们就得读盘
            Set(cFault, double.IsNaN(s.HardFaultRate) ? "—" : s.HardFaultRate.ToString("N0") + " 页/s",
                !double.IsNaN(s.HardFaultRate) && s.HardFaultRate >= 5000);
            Set(cModified, s.ModifiedMb >= 512 ? (s.ModifiedMb / 1024).ToString("F1") + " GB"
                                              : s.ModifiedMb.ToString("F0") + " MB", false);

            if (!_autoBusy && AutoChk.IsChecked != s.AutoEnabled) AutoChk.IsChecked = s.AutoEnabled;
            var last = s.AutoLastUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds((long)s.AutoLastUnix).LocalDateTime.ToString("HH:mm:ss")
                : "还没自动整理过";
            autoLine.Text = $"阈值 {s.AutoThresholdMb:F0} MB · 已自动整理 {s.AutoCount:F0} 次 · 上次 {last}"
                          + (string.IsNullOrEmpty(s.AutoReason) ? "" : " · 上次判断：" + s.AutoReason);

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

    async void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_autoBusy) return;
        _autoBusy = true;
        bool want = AutoChk.IsChecked == true;
        AutoChk.IsEnabled = false;
        try
        {
            var a = await MemoryApi.SetAutoAsync(want);
            if (a == null)
            {
                AutoChk.IsChecked = !want;               // 没成，回滚显示
                autoLine.Text = "切换失败：连不上采集端";
            }
            else
            {
                AutoChk.IsChecked = a.Enabled;
                Diag.Log($"面板：自动整理 → {(a.Enabled ? "开" : "关")}");
            }
        }
        finally
        {
            _autoBusy = false;
            AutoChk.IsEnabled = true;
        }
    }

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
                // l3 可能"部分成功"：某一步没成时要说清是哪一步，别笼统报失败
                string failed = r.FailedSteps.Count > 0 ? "（未成：" + string.Join("；", r.FailedSteps) + "）" : "";
                if (string.IsNullOrEmpty(r.Summary))
                {
                    statusLine.Text = "没能完成：" + (string.IsNullOrEmpty(r.Error) ? "未知原因" : r.Error);
                }
                else
                {
                    statusLine.Text = "部分完成：" + r.Summary + failed;
                    lastResult.Text = $"最近一次（{r.Tier}，部分完成）：{r.Detail}{failed}";
                }
                Diag.Log($"面板：{label} 未全部完成 → {r.Summary}｜{r.Error}{failed}");
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