using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ZmdOrb;

/// <summary>
/// 面板。界面照搬 zmd-manager（终末地管理器）：
///   页1 综合占用（470px 圆环 + 应用概况列表 + 底部信息条）
///   页2 设备性能（设备行 + 占用率走势柱 + 型号与规格覆盖页）
///   页3 整理与系统信息（zmd-orb 自己的：三级整理、内存指标、自动整理、机器/软件环境）
/// 实时数据来自本机采集端 http://127.0.0.1:8910/snapshot（字段与参考的采集端口径一致）。
/// </summary>
public partial class PanelWindow : Window
{
    // ---- 数据源 ----
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Stopwatch _since = Stopwatch.StartNew();
    DateTime _lastRefresh = DateTime.Now;
    double _sysCpu, _memPct, _usedMb, _committedMb;                      // 已占用内存 / 已提交（MB）
    double _maxOcc = UiSettings.DefaultMaxOccMb, _commitLimitMb = 32768; // 综合占用的分母：100% 对应的 MB
    int _page;
    bool _polling, _quitting, _cleaning, _autoBusy, _appsHidden, _snapLogged;
    bool _settingsTouched;      // "显示设置"里有未保存的改动：这时候别用轮询回填把用户填的东西冲掉
    bool _filling;              // 正在程序化回填：这期间控件事件不算"用户改动"
    int _snapCount;
    long _lastFrame;

    // ---- 页1 应用概况 / 页2 设备 ----
    List<AppRow> _appRows = new();
    List<AppInfo> _lastApps = new();
    string _appSig = "";
    string _appSort = "Cpu";            // 应用概况排序：Cpu / Mem / Name（点表头切换）
    bool _appSortDesc = true;
    int _appLimit = 40;                 // 显示条数（来自采集端的 app_limit 设置）
    List<DeviceRow> _devices = new();
    string _devSig = "";

    // ---- 页3 系统信息 ----
    string _specSig = "";
    SpecItem? _uptimeRow;               // "运行时长"那行：它要每秒动，单独留引用
    double _bootUnix;                   // 系统开机时刻（采集端给）

    public Func<double>? BallFps { get; set; }

    static readonly Brush LiveDot = Frozen("#3bb36b");
    static readonly Brush DemoDot = Frozen("#b9b9b3");
    static readonly Brush YellowBr = Frozen("#ffdf00");
    static readonly Brush GreyBr = Frozen("#75756f");

    public PanelWindow()
    {
        InitializeComponent();
        UiSettings.Load();
        // "显示设置"是一张**表单**：输入框与三个勾选框都只改界面，点「保存」才一起生效
        // （轻量那条会顺带重启，见 SaveSettings_Click）。谁被用户动过就置 _settingsTouched，
        // 之后 1 秒一次的轮询不再回填，免得把用户填的东西冲掉。
        foreach (var b in new[] { setMaxOcc, setWCpu, setWMem, setPoll,
                                  setAppLimit, setThreshold, setCheck, setGap })
            b.TextChanged += (_, _) => Settings_Edited();
        foreach (var c in new[] { chkAutostart, chkTrayMode, chkLite })
            c.Click += (_, _) => Settings_Edited();
        _poll.Interval = TimeSpan.FromSeconds(UiSettings.PollSecs);
        _poll.Tick += (_, _) => Poll();
        _tick.Tick += (_, _) => TickUi();
        // 定时器不在构造里起：这个窗口现在**懒构造**（首次打开才建），
        // 起来的那一刻 Loaded / IsVisibleChanged 会把它们启动（见下面两处）
        CompositionTarget.Rendering += OnFrame;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _poll.Start();
                _tick.Start();
            }
            else
            {
                _poll.Stop();          // 收起来就别采了
                _tick.Stop();
            }
        };
        Loaded += (_, _) =>
        {
            PreviewKeyDown += OnPreviewKeyDown;
            _poll.Start();
            _tick.Start();
            if (StartPage != 0) ShowPage(StartPage);   // 上次收起时停在哪一页，就接着停那儿
            Poll();
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

    /* ================= 页面切换 ================= */

    void Tab_Click(object sender, RoutedEventArgs e)
    {
        int idx = sender == tabPerf ? 1 : sender == tabMem ? 2 : sender == tabOrb ? 3 : 0;
        ShowPage(idx);
    }

    /// <summary>切到第 idx 页（0=综合占用 1=设备性能 2=应用内存 3=整理与系统信息）。
    /// 抽出来是为了"重新构造面板时接着停在上次那一页"。</summary>
    void ShowPage(int idx)
    {
        if (idx == _page) return;
        _page = idx;
        tabOverview.Tag = idx == 0 ? "active" : null;
        tabPerf.Tag = idx == 1 ? "active" : null;
        tabMem.Tag = idx == 2 ? "active" : null;
        tabOrb.Tag = idx == 3 ? "active" : null;
        ic1.Stroke = idx == 0 ? YellowBr : GreyBr;
        ic2.Stroke = idx == 1 ? YellowBr : GreyBr;
        ic3.Stroke = idx == 2 ? YellowBr : GreyBr;
        ic4.Stroke = idx == 3 ? YellowBr : GreyBr;

        pageOverview.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        pagePerf.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        pageMem.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        pageOrb.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;

        // 参考的进场动画：淡入 + 上移 10px，0.36s 缓出
        var page = idx == 0 ? pageOverview : idx == 1 ? pagePerf : idx == 2 ? pageMem : pageOrb;
        page.Opacity = 0;
        var tt = new TranslateTransform(0, 10);
        page.RenderTransform = tt;
        page.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(360)) { EasingFunction = EaseOut() });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = EaseOut() });

        string[] pageNames = { "综合占用", "设备性能", "应用内存", "整理与系统信息" };
        Diag.Log($"面板：切到第 {idx + 1} 页（{pageNames[idx]}）");
        Poll();        // 这一页要的数据可能刚被跳过（按页取数），立刻补一次，别等下一拍
    }

    static IEasingFunction EaseOut() =>
        new System.Windows.Media.Animation.CubicEase { EasingMode = EasingMode.EaseOut };

    /* ================= 数据 ================= */

    /// <summary>这次要向采集端要什么：只有当前页真用得到才要"重"数据——
    /// 设备页要显卡/磁盘/网络，首页与应用内存页要进程列表，整理/系统信息页只要那几个数。
    /// 采集端据此决定采不采（没人要时它既不枚举进程、也不拉 PowerShell 读显卡计数器）。</summary>
    string Want() => !IsVisible ? "none"
                   : _page == 1 ? "dev"
                   : _page == 3 ? "none"
                   : "procs";

    async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await MemoryApi.GetAsync(Want());
            if (s == null)
            {
                srcDot.Fill = DemoDot;
                srcText.Text = "演示";
                statusLine.Text = "采集端正在启动或未运行（127.0.0.1:8910）——球此时取不到数，也不会执行整理。";
                return;
            }
            _lastRefresh = DateTime.Now;
            srcDot.Fill = LiveDot;
            srcText.Text = "实时";
            ApplySnapshot(s);
        }
        finally { _polling = false; }
    }

    void ApplySnapshot(MemSnapshot s)
    {
        _sysCpu = s.Cpu.Util;
        _memPct = s.Mem.Pct;
        _usedMb = s.UsedMb;
        _committedMb = s.CommittedMb;
        // 分母：优先用显示设置里的值（出厂 325799），0 则跟随提交额度上限
        double limitMb = s.Mem.CommitLimitGb * 1024;
        _maxOcc = UiSettings.MaxOccMb > 0 ? UiSettings.MaxOccMb : (limitMb > 0 ? limitMb : _maxOcc);
        if (limitMb > 0) _commitLimitMb = limitMb;

        _appLimit = s.AutoAppLimit > 0 ? (int)s.AutoAppLimit : 40;   // 显示条数来自采集端设置
        // 只组装当前页用得到的：切页时下一次轮询（≤1 秒）就会补齐
        if (_page == 0) SyncApps(s.Procs);              // 首页：应用概况
        if (_page == 1) SyncDevices(BuildDevices(s));   // 设备页：设备行 + 走势
        if (_page == 2) SyncMem(s);                     // 应用内存页：按应用聚合
        if (_page == 3) BuildSpecs(s);                  // 系统信息页：规格网格（签名没变就不重建）
        UpdateOverview();
        // 数据回来了就把"采集端未启动"那行清掉：以前它只在轮询失败时写、没人清，
        // 于是采集端起来了提示还一直挂着（看起来像坏了）
        if (statusLine.Text.StartsWith("采集端", StringComparison.Ordinal)) statusLine.Text = "";
        if (!_snapLogged)
        {
            _snapLogged = true;
            Diag.Log($"面板：首帧数据（本页向采集端要的是 {Want()}）"
                     + $"综合={Combined():F1}%（权重 CPU {UiSettings.WCpu:0.##} / 内存 {UiSettings.WMem:0.##}）"
                     + $" CPU={_sysCpu:F1}% 内存={_memPct:F1}% 分母={_maxOcc:F0}MB "
                     + $"已占用={_usedMb:F0}MB 已提交={_committedMb:F0}MB 应用={s.Procs.Count} 设备={_devices.Count} "
                     + $"显卡={s.Sys.GpuFull}({s.Gpu.Util:F0}%) 磁盘={s.Disks.Count} 网络={s.Net.Name}");
            foreach (var d in _devices)
                Diag.Log($"面板：设备行 {d.Name}｜{d.Sub}｜{d.Cur1Lbl} {d.Cur1Val}｜{d.Cur2Lbl} {d.Cur2Val}｜{d.Spec}"
                         + $"｜主 {d.Util:F2}{d.AxisUnit1} 次 {d.Linev:F2}{d.AxisUnit2}");
            Diag.Log($"面板：应用内存页 {_memRows.Count} 行（候选 {s.Procs.Count} 个进程，聚合后 "
                     + $"{_appMem.Count} 个应用）："
                     + string.Join("、", _memRows.Take(12).Select(r => $"{r.Name}({r.MemVal})")));
        }
        _snapCount++;
        if (_snapCount == 6)      // 第 6 帧再打一次，用来核对两个指标是否都在推进
        {
            foreach (var d in _devices)
                Diag.Log($"面板：设备指标(第6帧) {d.Name}｜主 {d.Util:F2}{d.AxisUnit1}｜次 {d.Linev:F2}{d.AxisUnit2}");
        }

        // ---- 页3：内存指标 + 自动整理 ----
        SetVal(cPct, s.Pct.ToString("F1") + "%", s.Pct >= 88);
        SetVal(cAvail, (s.AvailMb / 1024).ToString("F1") + " GB", false);
        SetVal(cFreeZero, (s.FreeZeroMb / 1024).ToString("F1") + " GB", s.FreeZeroMb / 1024 < 1.0);
        SetVal(cStandby, s.StandbyMb / 1024 >= 1 ? (s.StandbyMb / 1024).ToString("F1") + " GB" : "—", false);
        SetVal(cCache, s.SystemCacheMb > 0 ? (s.SystemCacheMb / 1024).ToString("F2") + " GB" : "—", false);
        bool warn = s.CommitPct >= 85;
        SetVal(cCommit, (s.CommittedMb / 1024).ToString("F1") + " / " + (s.CommitLimitMb / 1024).ToString("F1")
                       + " GB · " + s.CommitPct.ToString("F0") + "%", warn);
        SetVal(cFault, double.IsNaN(s.HardFaultRate) ? "—" : s.HardFaultRate.ToString("N0") + " 页/s",
               !double.IsNaN(s.HardFaultRate) && s.HardFaultRate >= 5000);
        SetVal(cModified, s.ModifiedMb >= 512 ? (s.ModifiedMb / 1024).ToString("F1") + " GB"
                                             : s.ModifiedMb.ToString("F0") + " MB", false);

        if (!_autoBusy && AutoChk.IsChecked != s.AutoEnabled) AutoChk.IsChecked = s.AutoEnabled;
        var last = s.AutoLastUnix > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)s.AutoLastUnix).LocalDateTime.ToString("HH:mm:ss")
            : "还没自动整理过";
        autoLine.Text = $"阈值 {s.AutoThresholdMb:F0} MB · 已自动整理 {s.AutoCount:F0} 次 · 上次 {last}"
                      + (string.IsNullOrEmpty(s.AutoReason) ? "" : " · 上次判断：" + s.AutoReason);

        adminHint.Text = s.Admin ? "当前已是管理员：三档都不弹 UAC"
                                 : "深度/全部整理要管理员，点了会弹一次 UAC";

        // 显示设置回填（只有用户还没动过这张表单时才回填：1 秒一次的轮询不能把填的东西吃掉）
        if (!_settingsTouched)
        {
            Prefill(setMaxOcc, UiSettings.MaxOccMb > 0 ? UiSettings.MaxOccMb : _commitLimitMb);
            Prefill(setWCpu, UiSettings.WCpu, "0.##");
            Prefill(setWMem, UiSettings.WMem, "0.##");
            Prefill(setPoll, UiSettings.PollSecs, "0.##");
            Prefill(setAppLimit, s.AutoAppLimit);
            Prefill(setThreshold, s.AutoThresholdMb);
            Prefill(setCheck, s.AutoCheckSecs);
            Prefill(setGap, s.AutoMinGapSecs);
            // 三个开关反映已保存的状态（自启看注册表，托盘模式看壳当前形态，轻量看 ui.json）
            bool auto = Autostart.IsEnabled;
            if (chkAutostart.IsChecked != auto) chkAutostart.IsChecked = auto;
            bool tray = (Application.Current as App)?.Mode == "tray";
            if (chkTrayMode.IsChecked != tray) chkTrayMode.IsChecked = tray;
            if (chkLite.IsChecked != UiSettings.Lite) chkLite.IsChecked = UiSettings.Lite;
        }
    }

    /// <summary>"显示设置"里有控件被**用户**动过：标记成"未保存"，轮询不再回填。
    /// 程序化写入（Prefill）期间的事件不算 —— 用 _filling 挡掉。</summary>
    void Settings_Edited()
    {
        if (_filling) return;
        _settingsTouched = true;
        setHint.Text = "改动会在点「保存」后生效";
    }

    /// <summary>回填设置框。**程序化写入期间 TextChanged 不算用户改动**（见 Settings_Edited），
    /// 聚焦中不覆盖（用户正在编辑），值没变就不写（省掉一次事件）。</summary>
    void Prefill(TextBox box, double value, string fmt = "F0")
    {
        if (box.IsFocused || value <= 0) return;
        string s = value.ToString(fmt);
        if (box.Text == s) return;
        _filling = true;
        try { box.Text = s; }
        finally { _filling = false; }
    }

    /* ---- 整理与系统信息那一页的开关与按钮各管各的（自动整理那条见 Auto_Click），
       而"显示设置"那一整张表单 —— 输入框与三个勾选框 —— 只由「保存」按钮统一生效 ---- */

    /// <summary>收起面板交给 App 处理（App 会把实例置空并释放整棵树）。</summary>
    public int Page => _page;

    /// <summary>重新构造时接着停在这一页（0 = 首页）。</summary>
    public int StartPage { get; set; }

    /// <summary>刷新间隔改了之后应用（球那边也走这条）。</summary>
    public void ApplyPollSecs() => _poll.Interval = TimeSpan.FromSeconds(UiSettings.PollSecs);

    /// <summary>把整棵视觉树交还给 GC：先摘掉**静态事件**（CompositionTarget.Rendering，
    /// 不摘的话这个实例永远活着），再停表、放开关闭拦截、关掉窗口。</summary>
    public void Shutdown()
    {
        CompositionTarget.Rendering -= OnFrame;
        _poll.Stop();
        _tick.Stop();
        _quitting = true;                      // 别让 OnClosing 再把这次关闭拦下来
        Close();
    }

    /* ---- 显示设置：一整张"填表" —— 综合分母 / 两项权重 / 刷新间隔 / 开机自启 / 托盘模式 / 轻量模式
       都在点「保存」时一起生效；勾选框点了只改界面（`Settings_Edited` 标一下"有未保存改动"）。
       轻量模式比较特殊：它省的那笔内存靠切到软件渲染，而渲染方式只能在建窗口之前定（App.OnStartup），
       在跑的实例里切只停逐帧重画、不还那口写合并池（实测常驻 53.8 MB 一动不动）——
       所以保存时如果轻量状态变了，就写设置并**重启一次**（见 SaveSettings_Click 末尾）。 ---- */

    async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        double maxOcc = Parse(setMaxOcc, UiSettings.MaxOccMb);
        double wCpu = Parse(setWCpu, UiSettings.WCpu);
        double wMem = Parse(setWMem, UiSettings.WMem);
        double poll = Parse(setPoll, UiSettings.PollSecs);
        int appLimit = (int)Parse(setAppLimit, 40);
        int threshold = (int)Parse(setThreshold, 2048);
        int check = (int)Parse(setCheck, 60);
        int gap = (int)Parse(setGap, 180);

        UiSettings.MaxOccMb = Math.Max(0, maxOcc);
        if (wCpu + wMem > 0) { UiSettings.WCpu = wCpu; UiSettings.WMem = wMem; }   // 全 0 就保持原值
        UiSettings.PollSecs = Math.Min(10, Math.Max(0.3, poll));
        UiSettings.Save();
        var app = Application.Current as App;
        app?.ApplyPollSecs();                               // 球也一起改（以前只改面板）

        var a = await MemoryApi.SetAutoAsync(null, threshold, check, gap, Math.Max(1, appLimit));

        /* 三个勾选框也在这里生效（以前是点了立刻生效）。开机自启看注册表、托盘模式看当前形态、
           轻量模式看 ui.json —— 只有"跟当前状态不一样"才动手。 */
        string note = "";
        bool wantAuto = chkAutostart.IsChecked == true;
        if (wantAuto != Autostart.IsEnabled)
        {
            bool ok = wantAuto ? Autostart.Enable() : Autostart.Disable();
            if (ok) note += "；开机自启" + (wantAuto ? "已开" : "已关");
            else { chkAutostart.IsChecked = !wantAuto; note += "；开机自启没设置上（看日志）"; }
        }
        bool wantTray = chkTrayMode.IsChecked == true;
        if (app != null && wantTray != (app.Mode == "tray"))
        {
            app.SetMode(wantTray ? "tray" : "ball");
            note += wantTray ? "；已切到托盘模式" : "；已切回桌面悬浮球";
        }
        bool wantLite = chkLite.IsChecked == true;
        _settingsTouched = false;                           // 表单提交了，之后允许轮询回填
        setHint.Text = (a == null ? "采集端那边的设置没保存上" : "已保存") + note;
        Diag.Log($"面板：显示设置 综合分母={UiSettings.MaxOccMb:F0}MB（0=跟随提交上限{_commitLimitMb:F0}）"
                 + $" 权重 CPU={UiSettings.WCpu:0.##} 内存={UiSettings.WMem:0.##}"
                 + $" 刷新={UiSettings.PollSecs:0.##}s 应用概况={appLimit}条 阈值={threshold}MB "
                 + $"检查={check}s 最小间隔={gap}s → 综合={Combined():F1}%"
                 + $"；自启={wantAuto} 托盘={wantTray} 轻量={wantLite}");

        if (wantLite != UiSettings.Lite)
        {
            // 轻量状态变了 → 渲染方式跟着变 → 只能重启。重启要 ~1 秒，这一行是给这段空档用的
            // （面板重启前还开着：RestartApp 会带 --panel 把它带回来）
            setHint.Text = "已保存，正在重启…";
            app?.ToggleLite(wantLite);                      // 写设置 + 重启自己
            return;                                         // 已经开始重启了，别再往下动界面
        }
        Poll();
    }

    void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        // 和别的设置一样：只把**界面**填成出厂值，点「保存」才生效
        _settingsTouched = true;
        setMaxOcc.Text = UiSettings.DefaultMaxOccMb.ToString("F0");    // 分母出厂值 325799
        setWCpu.Text = "0.4";
        setWMem.Text = "0.6";
        setPoll.Text = "1";
        setAppLimit.Text = "40";
        setThreshold.Text = "2048";
        setCheck.Text = "60";
        setGap.Text = "180";
        setHint.Text = "已填入出厂值，点「保存」生效";
    }

    static double Parse(TextBox box, double fallback) =>
        double.TryParse(box.Text.Trim(), out var v) && v >= 0 ? v : fallback;

    /* ---- 页1：应用概况（排序 + 显示条数由本页决定；按 pid 序列判断是重建还是就地更新） ---- */

    void SyncApps(List<AppInfo> procs)
    {
        _lastApps = procs;                       // 留着给"点表头改排序"时立刻重排
        var all = new List<AppRow>(procs.Count);
        foreach (var p in procs)
        {
            string disp = string.IsNullOrEmpty(p.Display) ? p.Name : p.Display;
            all.Add(new AppRow
            {
                Pid = p.Pid,
                Name = disp,
                Sub = string.IsNullOrEmpty(p.Title) ? p.Name : p.Title,
                IconKey = IconRules.For((p.Name ?? "") + " " + (p.Display ?? "")),
                Cpu = p.Cpu,
                MemMb = p.MemMb,
            });
        }
        // 采集端只发候选集（活跃度过滤 + 宽上限），排序与显示条数在这里定
        Func<AppRow, object> key = _appSort switch
        {
            "Name" => r => r.Name,
            "Mem" => r => r.MemMb,
            _ => r => r.Cpu,
        };
        var ordered = (_appSortDesc ? all.OrderByDescending(key) : all.OrderBy(key));
        var rows = ordered.Take(Math.Max(1, _appLimit)).ToList();

        string arrow = _appSortDesc ? " ▾" : " ▴";
        headAppName.Text = "应用名称" + (_appSort == "Name" ? arrow : "");
        headAppCpu.Text = "CPU 占用" + (_appSort == "Cpu" ? arrow : "");
        headAppMem.Text = "内存占用" + (_appSort == "Mem" ? arrow : "");

        string sig = string.Join(",", rows.Select(r => r.Pid));
        if (sig != _appSig)
        {
            _appSig = sig;
            _appRows = rows;
            appList.ItemsSource = rows;
        }
        else
        {
            for (int i = 0; i < rows.Count && i < _appRows.Count; i++)
            {
                var a = _appRows[i];
                a.Cpu = rows[i].Cpu;
                a.MemMb = rows[i].MemMb;
                a.Name = rows[i].Name;
                a.Sub = rows[i].Sub;
            }
        }
        // 迷你条按全表最大值归一（参考的 updateAppTexts）
        double maxCpu = Math.Max(10, _appRows.Count > 0 ? _appRows.Max(r => r.Cpu) : 1);
        double maxMem = Math.Max(500, _appRows.Count > 0 ? _appRows.Max(r => r.MemMb) : 1);
        foreach (var a in _appRows)
        {
            a.CpuText = a.Cpu.ToString("F1");
            a.MemText = a.MemMb >= 1024 ? (a.MemMb / 1024).ToString("F2") + " GB"
                                        : a.MemMb.ToString("F0") + " MB";
            a.CpuBar = Math.Min(86, Math.Max(2, a.Cpu / maxCpu * 86));
            a.MemBar = Math.Min(86, Math.Max(2, a.MemMb / maxMem * 86));
            a.Notify();
        }
    }

    /* ---- 页1：应用概况的排序（点表头） ---- */

    void AppSort_Name(object sender, MouseButtonEventArgs e) => SetAppSort("Name");
    void AppSort_Cpu(object sender, MouseButtonEventArgs e) => SetAppSort("Cpu");
    void AppSort_Mem(object sender, MouseButtonEventArgs e) => SetAppSort("Mem");

    void SetAppSort(string key)
    {
        // 名称默认升序，数值默认降序；点同一列则反向
        _appSortDesc = key == _appSort ? !_appSortDesc : key != "Name";
        _appSort = key;
        SyncApps(_lastApps);        // 立刻重排，不等下一次轮询
        var top = _appRows.Take(3).Select(r => $"{r.Name}({r.CpuText}%/{r.MemText})");
        Diag.Log($"面板：应用概况排序 → {key}{(_appSortDesc ? " 降序" : " 升序")}，前三：{string.Join(" | ", top)}");
    }

    /* ---- 页3：应用内存（按应用名聚合，同名多进程合并成一行） ---- */

    readonly Dictionary<string, AppMemRow> _appMem = new();
    List<AppMemRow> _memRows = new();
    string _memSig = "";

    void SyncMem(MemSnapshot s)
    {
        double totalMb = s.TotalMb > 0 ? s.TotalMb : 32768;
        double limitMb = s.Mem.CommitLimitGb * 1024;
        if (limitMb <= 0) limitMb = totalMb;

        var agg = new Dictionary<string, (double mem, double commit, int n)>();
        foreach (var p in s.Procs)
        {
            string name = string.IsNullOrEmpty(p.Display) ? p.Name : p.Display;
            if (string.IsNullOrEmpty(name)) continue;
            agg.TryGetValue(name, out var cur);
            agg[name] = (cur.mem + p.MemMb, cur.commit + p.CommitMb, cur.n + 1);
        }

        var list = new List<AppMemRow>(agg.Count);
        foreach (var kv in agg)
        {
            if (!_appMem.TryGetValue(kv.Key, out var row))
            {
                row = new AppMemRow { Key = kv.Key, Name = kv.Key, IconKey = IconRules.For(kv.Key) };
                _appMem[kv.Key] = row;
            }
            row.MemMb = kv.Value.mem;
            row.CommitMb = kv.Value.commit;
            row.Sub = kv.Value.n > 1 ? $"{kv.Value.n} 个进程" : "单进程";
            row.MemPct = row.MemMb / totalMb * 100;                 // 已占用 → 占物理内存
            row.CommitPct = row.CommitMb / limitMb * 100;           // 已提交 → 占提交额度
            row.MemVal = Gb(row.MemMb);
            row.CommitVal = Gb(row.CommitMb);
            row.PctText = $"已占用 {row.MemPct:F1}% 物理内存 · 已提交 {row.CommitPct:F1}% 提交额度";
            list.Add(row);
        }
        list.Sort((x, y) => y.MemMb.CompareTo(x.MemMb));
        var shown = list.Take(Math.Max(1, _appLimit)).ToList();

        string sig = string.Join(",", shown.Select(r => r.Key));
        if (sig != _memSig)
        {
            _memSig = sig;
            _memRows = shown;
            memList.ItemsSource = shown;
        }
        foreach (var r in _memRows)
        {
            r.Push(r.MemPct, r.CommitPct);
            r.Notify();
        }
    }

    static string Gb(double mb) =>
        mb >= 1024 ? (mb / 1024).ToString("F2") + " GB" : mb.ToString("F0") + " MB";

    /* ---- 页2：设备行（key 序列变化才重建，否则就地更新并推进走势） ---- */

    List<DeviceRow> BuildDevices(MemSnapshot s)
    {
        var devs = new List<DeviceRow>();
        var cpu = s.Cpu;
        devs.Add(new DeviceRow
        {
            Key = "cpu", Type = "cpu", IconKey = "cpu", Name = "处理器",
            Sub = string.IsNullOrEmpty(cpu.Full)
                ? $"{cpu.Threads:F0} 线程"
                : $"{cpu.Full} · {cpu.Cores:F0} 核 {cpu.Threads:F0} 线程",
            Util = cpu.Util,
            // 次指标用**原始单位**（GHz），并在图上用右轴单独缩放 —— 占用率与频率不是同一类型
            Linev = cpu.Freq, DualAxis = true, AxisUnit2 = "GHz",
            Cur1Lbl = "占用", Cur1Val = cpu.Util.ToString("F0") + "%",
            Cur2Lbl = "频率", Cur2Val = cpu.Freq.ToString("F2") + " GHz",
            Spec = $"基准 {cpu.Base:F2} GHz",
            Detail = new List<string[]>
            {
                new[] { "型号", string.IsNullOrEmpty(cpu.Full) ? "—" : cpu.Full },
                new[] { "核心 / 线程", $"{cpu.Cores:F0} 核 {cpu.Threads:F0} 线程" },
                new[] { "基准频率", cpu.Base.ToString("F2") + " GHz" },
                new[] { "实时频率", cpu.Freq.ToString("F2") + " GHz"
                        + (!double.IsNaN(cpu.PerfPct) ? $"（性能 {cpu.PerfPct:F0}%）" : "") },
                new[] { "本次观察最高", cpu.SeenMax > 0 ? cpu.SeenMax.ToString("F2") + " GHz" : "—" },
            },
        });
        var gpu = s.Gpu;
        devs.Add(new DeviceRow
        {
            Key = "gpu", Type = "gpu", IconKey = "gpu", Name = "显卡",
            Sub = string.IsNullOrEmpty(gpu.Full)
                ? (gpu.MemTotal > 0 ? $"显存 {gpu.MemTotal:F1} GB" : "显卡")
                : gpu.Full + (gpu.MemTotal > 0 ? $" · 显存 {gpu.MemTotal:F1} GB" : ""),
            Util = gpu.Util,
            // 次指标：已用显存（GB，右轴单独缩放）
            Linev = !double.IsNaN(gpu.MemUsed) ? gpu.MemUsed : (gpu.MemTotal > 0 ? 0 : gpu.Util),
            DualAxis = true, AxisUnit2 = "GB",
            Cur1Lbl = "占用", Cur1Val = gpu.Util.ToString("F0") + "%",
            Cur2Lbl = "显存", Cur2Val = gpu.MemTotal > 0
                ? $"{(!double.IsNaN(gpu.MemUsed) ? gpu.MemUsed.ToString("F2") + " / " : "")}{gpu.MemTotal:F1} GB"
                  + (gpu.MemTotal > 0 && !double.IsNaN(gpu.MemUsed)
                     ? " · " + (gpu.MemUsed / gpu.MemTotal * 100).ToString("F0") + "%" : "")
                : "—",
            Spec = $"{(gpu.MemTotal > 0 ? gpu.MemTotal.ToString("F1") : "—")} GB 显存",
            Detail = new List<string[]>
            {
                new[] { "型号", string.IsNullOrEmpty(gpu.Full) ? "—" : gpu.Full },
                new[] { "显存（独立）", (gpu.MemTotal > 0 ? gpu.MemTotal.ToString("F1") : "—") + " GB" },
                new[] { "已用显存", !double.IsNaN(gpu.MemUsed) ? gpu.MemUsed.ToString("F2") + " GB" : "—" },
            },
        });
        var mem = s.Mem;
        devs.Add(new DeviceRow
        {
            Key = "mem", Type = "mem", IconKey = "mem", Name = "内存",
            Sub = mem.Modules > 0
                ? $"{mem.Modules:F0}×{mem.PerGb:F0} GB {mem.Type}"
                  + (!string.IsNullOrEmpty(mem.Speed) && mem.Speed != "—" ? " · " + mem.Speed : "")
                : $"已用 {mem.UsedGb:F1} / {mem.TotalGb:F1} GB",
            Util = mem.Pct,
            // 次指标：提交额度占用率（这才是会不会被打崩的指标）
            Linev = mem.CommitLimitGb > 0 ? mem.CommittedGb / mem.CommitLimitGb * 100 : mem.Pct,
            Cur1Lbl = "占用", Cur1Val = mem.Pct.ToString("F0") + "%",
            Cur2Lbl = "提交", Cur2Val = mem.CommitLimitGb > 0
                ? $"{mem.CommittedGb:F1} / {mem.CommitLimitGb:F1} GB · "
                  + (mem.CommittedGb / mem.CommitLimitGb * 100).ToString("F0") + "%"
                : "—",
            Spec = $"{mem.TotalGb:F0} GB {mem.Type}".Trim(),
            Detail = new List<string[]>
            {
                new[] { "容量", mem.TotalGb.ToString("F1") + " GB" },
                new[] { "类型", string.IsNullOrEmpty(mem.Type) ? "—" : mem.Type },
                new[] { "频率", string.IsNullOrEmpty(mem.Speed) ? "—" : mem.Speed },
                new[] { "模块", mem.Modules > 0 ? $"{mem.Modules:F0} × {mem.PerGb:F0} GB" : "—" },
                new[] { "厂商", string.IsNullOrEmpty(mem.Vendor) ? "—" : mem.Vendor },
                new[] { "型号", string.IsNullOrEmpty(mem.Part) ? "—" : mem.Part },
                new[] { "空闲+零页", (mem.FreeZeroMb / 1024).ToString("F1") + " GB" },
                new[] { "待命列表", (mem.StandbyMb / 1024).ToString("F1") + " GB" },
            },
        });
        for (int i = 0; i < s.Disks.Count; i++)
        {
            var d = s.Disks[i];
            double rwNum = 0;
            double.TryParse((d.Rw ?? "").Split(' ')[0], out rwNum);
            devs.Add(new DeviceRow
            {
                Key = "disk-" + i, Type = "disk", IconKey = "disk", Name = d.Name,
                Sub = string.IsNullOrEmpty(d.Model)
                    ? $"已用 {d.UsedGb:F1} / {d.TotalGb:F1} GB"
                    : d.Model + (string.IsNullOrEmpty(d.Media) ? "" : " · " + d.Media),
                Util = d.Util,
                Linev = rwNum, DualAxis = true, AxisUnit2 = "MB/s",   // 次指标：读写速率（MB/s，右轴单独缩放）
                Cur1Lbl = "占用", Cur1Val = d.Util.ToString("F0") + "%",
                Cur2Lbl = "读写", Cur2Val = string.IsNullOrEmpty(d.Rw) ? "—" : d.Rw,
                Spec = $"{d.TotalGb:F0} GB · 已用 {d.Pct:F0}%",
                Detail = new List<string[]>
                {
                    new[] { "型号", string.IsNullOrEmpty(d.Model) ? "—" : d.Model },
                    new[] { "介质", string.IsNullOrEmpty(d.Media) ? "—" : d.Media },
                    new[] { "容量", d.TotalGb.ToString("F1") + " GB" },
                    new[] { "已用", d.UsedGb.ToString("F1") + " GB" },
                },
            });
        }
        var net = s.Net;
        devs.Add(new DeviceRow
        {
            Key = "net", Type = "net", IconKey = "net", Name = string.IsNullOrEmpty(net.Name) ? "网络" : net.Name,
            Sub = string.IsNullOrEmpty(net.Desc) ? $"链路 {net.Link:F0} Mbps" : net.Desc,
            Util = net.Util,
            Linev = net.Link > 0 ? Math.Max(0, Math.Min(100, net.Up / net.Link * 100)) : 0,  // 次指标：上行占链路
            Cur1Lbl = "下行", Cur1Val = net.Down.ToString("F1") + " Mbps",
            Cur2Lbl = "上行", Cur2Val = net.Up.ToString("F1") + " Mbps",
            Spec = $"{net.Link:F0} Mbps",
            Detail = new List<string[]>
            {
                new[] { "适配器", string.IsNullOrEmpty(net.Name) ? "—" : net.Name },
                new[] { "链路速度", net.Link.ToString("F0") + " Mbps" },
                new[] { "当前下行", net.Down.ToString("F2") + " Mbps" },
                new[] { "当前上行", net.Up.ToString("F2") + " Mbps" },
            },
        });
        return devs;
    }

    void SyncDevices(List<DeviceRow> list)
    {
        string sig = string.Join("|", list.Select(d => d.Key));
        if (sig != _devSig)
        {
            var old = _devices.ToDictionary(d => d.Key, d => (A: d.Hist.ToList(), B: d.Hist2.ToList()));
            foreach (var d in list)
            {
                if (old.TryGetValue(d.Key, out var h))
                {
                    int n = Math.Min(h.A.Count, h.B.Count);
                    for (int i = 0; i < n; i++) d.Push(h.A[i], h.B[i]);   // 保留原有走势
                }
                else
                {
                    d.Push(d.Util, d.Linev);
                }
            }
            _devSig = sig;
            _devices = list;
            devList.ItemsSource = list;
        }
        else
        {
            for (int i = 0; i < list.Count; i++)
            {
                var t = _devices[i];
                t.Name = list[i].Name;
                t.Sub = list[i].Sub;
                t.Spec = list[i].Spec;
                t.Cur1Lbl = list[i].Cur1Lbl;
                t.Cur1Val = list[i].Cur1Val;
                t.Cur2Lbl = list[i].Cur2Lbl;
                t.Cur2Val = list[i].Cur2Val;
                t.Detail = list[i].Detail;
                t.Util = list[i].Util;
                t.Linev = list[i].Linev;        // ← 曾经漏了这一行：次指标永远停在首帧值，蓝线是直线
                t.DualAxis = list[i].DualAxis;
                t.AxisUnit2 = list[i].AxisUnit2;
            }
        }
        foreach (var d in _devices)
        {
            d.Push(d.Util, d.Linev);     // 主指标 + 次指标各推一个点
            d.Notify();
        }
    }

    /* ---- 页1：概览数字（中间大数 = 综合占用按分母折成的 MB；两个方框 = 已占用内存 / 已提交） ---- */

    void UpdateOverview()
    {
        double comp = Combined();
        double denom = Math.Max(1, _maxOcc);
        gauge.Comp = comp;
        bigNum.Text = Math.Round(comp / 100 * denom).ToString();
        ofMax.Text = "/ " + Math.Round(denom);
        usedNum.Text = (_usedMb / 1024).ToString("F1");            // 物理内存口径（驻留），按 GB 给
        committedNum.Text = (_committedMb / 1024).ToString("F1"); // 提交额度口径（含未驻留的私有提交）
        tagCpu.Text = $"CPU {_sysCpu:F0}%";
        tagMem.Text = $"MEM {_memPct:F0}%";
        tagComp.Text = $"综合 {comp:F1}%";
    }

    /// <summary>综合占用%：CPU 与内存占用率各乘权重后**按权重之和归一**（0.4/0.6 与 4/6 等价，
    /// 只影响两者的相对比例）。两个权重都是 0 时退回出厂比例。</summary>
    static double Combined(double cpu, double mem, double wCpu, double wMem)
    {
        double sum = wCpu + wMem;
        if (sum <= 0) { wCpu = 0.4; wMem = 0.6; sum = 1; }
        return Math.Max(0, Math.Min(100, (cpu * wCpu + mem * wMem) / sum));
    }

    double Combined() => Combined(_sysCpu, _memPct, UiSettings.WCpu, UiSettings.WMem);

    void TickUi()
    {
        var up = TimeSpan.FromMilliseconds(_since.ElapsedMilliseconds);
        uptime.Text = $"{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";
        if (_uptimeRow != null) _uptimeRow.Val = UptimeText();   // 系统运行时长跟着走（静态信息重建时不刷新）
        double sec = (DateTime.Now - _lastRefresh).TotalSeconds;
        lastRefresh.Text = sec < 3 ? "刚刚" : $"{(int)sec} 秒前";
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (_page != 0 || !IsVisible) return;
        if (UiSettings.Lite) return;      // 轻量模式：粒子团不画了，也就不必逐帧推进/重画
        long now = _since.ElapsedMilliseconds;
        if (_lastFrame != 0)
        {
            double dt = (now - _lastFrame) / 1000.0;
            // 门控没过就直接返回，**不要**更新 _lastFrame：
            // 否则 60fps 回调下每帧都把基准重置，dt 恒为 ~16ms，条件永远不成立（粒子团就冻住了）
            if (dt < 1.0 / 30) return;
            blob.Advance(Math.Min(dt, 0.1));
            _blobFrames++;
            if (_blobFrames == 120)      // 大约 4 秒后记一条，用来确认真的在动
                Diag.Log($"面板：粒子团 4 秒内绘制 {_blobFrames} 帧（≈{_blobFrames / 4.0:F0} fps）");
        }
        _lastFrame = now;
    }

    long _blobFrames;

    /* ---- 页1：占用报告开关（隐藏右侧应用概况） ---- */

    void Report_Click(object sender, MouseButtonEventArgs e)
    {
        _appsHidden = !_appsHidden;
        reportTogglePill.Background = _appsHidden ? Frozen("#777777") : YellowBr;
        reportToggleDot.HorizontalAlignment = _appsHidden ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        reportToggleDot.Fill = _appsHidden ? Brushes.White : Frozen("#3a3a38");
        if (_appsHidden)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) => appsPanel.Visibility = Visibility.Collapsed;
            appsPanel.BeginAnimation(OpacityProperty, fade);
            appsPanel.RenderTransform = new ScaleTransform(0.94, 0.94);
        }
        else
        {
            appsPanel.Visibility = Visibility.Visible;
            appsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));
            appsPanel.RenderTransform = new ScaleTransform(1, 1);
        }
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _lastRefresh = DateTime.Now;
        Poll();
    }

    /* ---- 设备详情覆盖页 ---- */

    void DevIcon_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DeviceRow row) return;
        dtTable.Children.Clear();
        foreach (var r in row.Detail)
            dtTable.Children.Add(DetailLine(r[0], r[1]));
        dtTable.Children.Add(DetailLine(row.Cur1Lbl, row.Cur1Val));
        dtTable.Children.Add(DetailLine(row.Cur2Lbl, row.Cur2Val));
        detailOverlay.Visibility = Visibility.Visible;
    }

    static Grid DetailLine(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, FontSize = 13, Foreground = Frozen("#8a8a84") };
        var v = new TextBlock
        {
            Text = value, FontSize = 13, Foreground = Frozen("#3f3f3c"),
            TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(l, 0);
        Grid.SetColumn(v, 1);
        var line = new Border
        {
            BorderBrush = Frozen("#d5d5d0"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 7, 0, 7),
        };
        g.Children.Add(l);
        g.Children.Add(v);
        line.Child = g;
        var host = new Grid();
        host.Children.Add(line);
        return host;
    }

    void Detail_Close(object sender, RoutedEventArgs e) => detailOverlay.Visibility = Visibility.Collapsed;

    void Detail_BackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, detailOverlay))
            detailOverlay.Visibility = Visibility.Collapsed;
    }

    /* ================= 页3：整理 / 自动 / 进程表（zmd-orb 自己的） ================= */

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
                AutoChk.IsChecked = !want;
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
            Poll();
        }
    }

    async System.Threading.Tasks.Task LoadLastResultAsync()
    {
        var r = await MemoryApi.LastCleanAsync();
        if (r != null && !string.IsNullOrEmpty(r.Detail))
            lastResult.Text = $"最近一次（{r.Tier}）：{r.Detail}";
    }

    /* ---- 系统信息（格式照 nonebot-plugin-status-zmd 的 specs 网格：
       两列，标签在左、值在右，行底一条虚线；数据来自采集端 sys 块 + 壳自己的落盘路径） ---- */

    static readonly string DataDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "zmd-orb");

    void BuildSpecs(MemSnapshot s)
    {
        var sys = s.Sys;
        string sig = $"{sys.Host}|{sys.Os}|{sys.Kernel}|{sys.Arch}|{sys.NetAddrs}"
                   + $"|{s.Mem.TotalGb}|{s.Cpu.Full}|{sys.GpuFull}|{sys.Disks}|{UiSettings.PollSecs}";
        if (sig == _specSig) return;      // 静态信息没变就别重建（1 秒一次的轮询不该反复重建 UI）
        _specSig = sig;

        var gb = s.Mem.TotalGb;
        _bootUnix = sys.BootUnix;
        var rows = new List<SpecItem>
        {
            new("主机名", sys.Host.Length > 0 ? sys.Host : "—"),
            new("操作系统", sys.Os.Length > 0 ? sys.Os : "—"),
            new("内核", $"{sys.Kernel} · {sys.Arch}"),
            new("运行时长", UptimeText()),
            new("处理器", s.Cpu.Full.Length > 0 ? s.Cpu.Full : "—"),
            new("核心", $"{s.Cpu.Cores:F0} 核 / {s.Cpu.Threads:F0} 线程 · 基准 {s.Cpu.Base:F2} GHz"),
            new("内存", $"{gb:F1} GB · {s.Mem.Type} · {s.Mem.Speed}"
                        + (s.Mem.PerGb > 0 ? $" · {s.Mem.Modules:F0} × {s.Mem.PerGb:F0} GB" : "")),
            new("显卡", (sys.GpuFull.Length > 0 ? sys.GpuFull : "—")
                        + (sys.GpuMemTotal > 0 ? $" · 显存 {sys.GpuMemTotal:F1} GB" : "")),
            new("磁盘", sys.Disks.Length > 0 ? sys.Disks : "—"),
            new("网络", (string.IsNullOrEmpty(sys.NetAddrs) ? "—" : sys.NetAddrs)
                        + (sys.NetLink > 0 ? $" · {sys.NetLink:F0} Mbps" : "")),
            new("数据目录", DataDir),
            new("日志文件", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zmd_orb_shell.log")),
        };
        _uptimeRow = rows.First(r => r.Lbl == "运行时长");    // 这一行要每秒动，单独留个引用
        specList.ItemsSource = rows;
        sysMeta.Text = sys.Host.Length > 0 ? sys.Host + " · " + sys.Os : "";
        Diag.Log("面板：系统信息 " + rows.Count + " 项已生成（主机=" + sys.Host + " 系统=" + sys.Os
                 + " 内核=" + sys.Kernel + " " + sys.Arch + "）");
    }

    /// <summary>时长文本：不足一天只给 HH:MM:SS（与参考项目 format_duration 一致）。</summary>
    static string Dur(double secs)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, secs));
        string clock = $"{(int)t.TotalHours % 24:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        return (int)t.TotalDays > 0 ? $"{(int)t.TotalDays}天 {clock}" : clock;
    }

    /// <summary>系统运行时长（开机至今）。静态信息不会变，所以这一行由 TickUi 每 0.5 秒刷新。</summary>
    string UptimeText()
    {
        if (_bootUnix <= 0) return "—";
        return Dur(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _bootUnix);
    }

    /* ---- 杂项 ---- */

    /// <summary>指标块数值：深炭底白字，警戒时把底色换成红（不能改前景色——深灰字在深炭底上等于看不见）。</summary>
    static void SetVal(TextBlock el, string text, bool warn)
    {
        el.Text = text;
        el.Background = warn ? WarnBg : DarkBg;
        el.Foreground = Brushes.White;
    }

    static readonly Brush Normal = Frozen("#3f3f3c");
    static readonly Brush Warn = Frozen("#c2703a");
    static readonly Brush DarkBg = Frozen("#3a3a38");
    static readonly Brush WarnBg = Frozen("#e04a3a");

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush(Ring.Hex(hex));
        b.Freeze();
        return b;
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (detailOverlay.Visibility == Visibility.Visible) detailOverlay.Visibility = Visibility.Collapsed;
        else Hide();
    }

    void ToBall_Click(object sender, RoutedEventArgs e) => Hide();

    void Quit_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.QuitApp();
}

/// <summary>可通知变更的基类（绑定的行对象就地更新用）。</summary>
class RowBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>通知"整行都变了"（空属性名 = 所有绑定刷新）。</summary>
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>系统信息网格里的一行（绑定用：必须是属性；值可变，所以继承可通知基类）。</summary>
sealed class SpecItem : RowBase
{
    string _val = "";
    public string Lbl { get; set; } = "";
    public string Val
    {
        get => _val;
        set
        {
            if (_val == value) return;      // 值没变就别触发绑定刷新
            _val = value;
            Notify();
        }
    }

    public SpecItem(string lbl, string val) { Lbl = lbl; Val = val; }
}

/// <summary>应用概况列表的一行。</summary>
sealed class AppRow : RowBase
{
    public int Pid { get; set; }
    public string IconKey { get; set; } = "app";
    public string Name { get; set; } = "";
    public string Sub { get; set; } = "";
    public double Cpu { get; set; }
    public double MemMb { get; set; }
    public string CpuText { get; set; } = "0.0";
    public string MemText { get; set; } = "0 MB";
    public double CpuBar { get; set; } = 2;
    public double MemBar { get; set; } = 2;
}