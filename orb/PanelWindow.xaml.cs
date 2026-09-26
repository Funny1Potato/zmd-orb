using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ZmdOrb;

/// <summary>任务管理器面板。M1 三档整理；M2 硬缺页率与自动整理；M3 进程表与结束进程。</summary>
public partial class PanelWindow : Window
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    // 进程表只在面板可见时才刷新（面板平时是收起来的，没必要一直采）
    readonly DispatcherTimer _procTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _polling, _quitting, _cleaning, _autoBusy, _procBusy, _killing, _procLogged;

    List<ProcRow> _procAll = new();
    string _sortPath = "MemMb";
    bool _sortDesc = true;

    /// <summary>球上报的粒子团实测帧率。</summary>
    public Func<double>? BallFps { get; set; }

    public PanelWindow()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        _procTimer.Tick += async (_, _) => await LoadProcsAsync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _procLogged = false;
                _procTimer.Start();
                _ = LoadProcsAsync();
            }
            else
            {
                _procTimer.Stop();       // 收起就不再采
            }
        };
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

    /* ---------------- 进程表（M3） ---------------- */

    async System.Threading.Tasks.Task LoadProcsAsync()
    {
        if (_procBusy || _killing || !IsVisible) return;
        _procBusy = true;
        try
        {
            var list = await MemoryApi.ProcessesAsync();
            if (list == null)
            {
                procCount.Text = "采集端未启动";
                return;
            }
            _procAll = list;
            RefreshGrid();
            if (!_procLogged)
            {
                _procLogged = true;      // 每次打开面板只记一条，免得刷日志
                Diag.Log($"面板：进程表已加载 {list.Count} 个进程，最大内存 "
                         + (list.Count > 0 ? list[0].Name + " " + list[0].MemText : "—"));
            }
        }
        finally { _procBusy = false; }
    }

    void RefreshGrid()
    {
        string q = procSearch.Text.Trim();
        IEnumerable<ProcRow> rows = _procAll;
        if (q.Length > 0)
        {
            rows = rows.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || r.Pid.ToString() == q);
        }
        // 自己排：直接重建 ItemsSource，DataGrid 原来的排序会被冲掉
        Func<ProcRow, object> key = _sortPath switch
        {
            "Name" => r => r.Name,
            "Pid" => r => r.Pid,
            "Cpu" => r => r.Cpu,
            "Threads" => r => r.Threads,
            _ => r => r.MemMb,
        };
        var ordered = (_sortDesc ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToList();

        int? keep = (procGrid.SelectedItem as ProcRow)?.Pid;
        procGrid.ItemsSource = ordered;
        if (keep != null)
        {
            var again = ordered.FirstOrDefault(r => r.Pid == keep);
            if (again != null) procGrid.SelectedItem = again;    // 刷新后尽量别丢选中
        }
        procCount.Text = q.Length > 0
            ? $"{ordered.Count} / {_procAll.Count} 个"
            : $"{_procAll.Count} 个 · 每 2 秒刷新（CPU% 是这两秒的均值）";
    }

    void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;                    // 自己排，别让 DataGrid 排完又被重建冲掉
        string path = e.Column.SortMemberPath;
        _sortDesc = path == _sortPath ? !_sortDesc : true;
        _sortPath = path;
        foreach (var c in procGrid.Columns)
        {
            c.SortDirection = c.SortMemberPath == path
                ? (_sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
        RefreshGrid();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => RefreshGrid();

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadProcsAsync();

    async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (_killing) return;
        if (procGrid.SelectedItem is not ProcRow row)
        {
            statusLine.Text = "先在进程表里点选一个进程。";
            return;
        }
        bool tree = TreeChk.IsChecked == true;
        var ok = MessageBox.Show(this,
            $"确定结束 {row.Name}（pid {row.Pid}）{(tree ? "及其子孙进程" : "")}？\n\n该进程没保存的数据会丢。",
            "结束进程", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        _killing = true;
        BtnKill.IsEnabled = false;
        statusLine.Text = $"正在结束 {row.Name}（pid {row.Pid}）{(tree ? "及子孙" : "")}…";
        Diag.Log($"面板：结束进程 {row.Name}({row.Pid}) tree={tree}");
        try
        {
            var r = await MemoryApi.KillAsync(row.Pid, tree);
            statusLine.Text = r.Ok
                ? (string.IsNullOrEmpty(r.Summary) ? "已结束" : r.Summary)
                : "没能结束：" + (string.IsNullOrEmpty(r.Error) ? "未知原因" : r.Error);
            Diag.Log($"面板：结束进程结果 → {statusLine.Text}");
        }
        finally
        {
            _killing = false;
            BtnKill.IsEnabled = true;
        }
        await LoadProcsAsync();
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