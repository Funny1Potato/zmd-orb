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
///   页3 整理与进程（zmd-orb 自己的：三级整理、内存指标、自动整理、进程表与结束进程）
/// 实时数据来自本机采集端 http://127.0.0.1:8910/snapshot（字段与参考的采集端口径一致）。
/// </summary>
public partial class PanelWindow : Window
{
    // ---- 数据源 ----
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _procTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Stopwatch _since = Stopwatch.StartNew();
    DateTime _lastRefresh = DateTime.Now;
    double _sysCpu, _memPct, _maxOcc = 32768, _commitLimitMb = 32768;   // 最大占用 = 提交额度上限或自定义值（MB）
    int _page;
    bool _polling, _quitting, _cleaning, _autoBusy, _procBusy, _killing, _procLogged, _appsHidden, _snapLogged;
    long _lastFrame;

    // ---- 页1 应用概况 / 页2 设备 ----
    List<AppRow> _appRows = new();
    string _appSig = "";
    List<DeviceRow> _devices = new();
    string _devSig = "";

    // ---- 页3 进程表 ----
    List<ProcRow> _procAll = new();
    string _sortPath = "Mem";
    bool _sortDesc = true;

    public Func<double>? BallFps { get; set; }

    static readonly Brush LiveDot = Frozen("#3bb36b");
    static readonly Brush DemoDot = Frozen("#b9b9b3");
    static readonly Brush YellowBr = Frozen("#ffdf00");
    static readonly Brush GreyBr = Frozen("#75756f");

    public PanelWindow()
    {
        InitializeComponent();
        UiSettings.Load();
        _poll.Interval = TimeSpan.FromSeconds(UiSettings.PollSecs);
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        _tick.Tick += (_, _) => TickUi();
        _tick.Start();
        _procTimer.Tick += async (_, _) => await LoadProcsAsync();
        CompositionTarget.Rendering += OnFrame;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _procLogged = false;
                _poll.Start();
                _tick.Start();
            }
            else
            {
                _poll.Stop();          // 收起来就别采了
                _tick.Stop();
                _procTimer.Stop();
            }
        };
        Loaded += (_, _) =>
        {
            PreviewKeyDown += OnPreviewKeyDown;
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
        int idx = sender == tabPerf ? 1 : sender == tabOrb ? 2 : 0;
        if (idx == _page) return;
        _page = idx;
        tabOverview.Tag = idx == 0 ? "active" : null;
        tabPerf.Tag = idx == 1 ? "active" : null;
        tabOrb.Tag = idx == 2 ? "active" : null;
        ic1.Stroke = idx == 0 ? YellowBr : GreyBr;
        ic2.Stroke = idx == 1 ? YellowBr : GreyBr;
        ic3.Stroke = idx == 2 ? YellowBr : GreyBr;

        pageOverview.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        pagePerf.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        pageOrb.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;

        // 参考的进场动画：淡入 + 上移 10px，0.36s 缓出
        var page = idx == 0 ? pageOverview : idx == 1 ? pagePerf : pageOrb;
        page.Opacity = 0;
        var tt = new TranslateTransform(0, 10);
        page.RenderTransform = tt;
        page.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(360)) { EasingFunction = EaseOut() });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = EaseOut() });

        if (idx == 2) _procTimer.Start();
        else _procTimer.Stop();
    }

    static IEasingFunction EaseOut() =>
        new System.Windows.Media.Animation.CubicEase { EasingMode = EasingMode.EaseOut };

    /* ================= 数据 ================= */

    async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await MemoryApi.GetAsync();
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
        // 最大占用：优先用显示设置里的自定义值，0 则跟随提交额度上限
        double limitMb = s.Mem.CommitLimitGb * 1024;
        _maxOcc = UiSettings.MaxOccMb > 0 ? UiSettings.MaxOccMb : (limitMb > 0 ? limitMb : _maxOcc);
        if (limitMb > 0) _commitLimitMb = limitMb;

        SyncApps(s.Procs);
        SyncDevices(BuildDevices(s));
        UpdateOverview();
        // 数据回来了就把"采集端未启动"那行清掉：以前它只在轮询失败时写、没人清，
        // 于是采集端起来了提示还一直挂着（看起来像坏了）
        if (statusLine.Text.StartsWith("采集端", StringComparison.Ordinal)) statusLine.Text = "";
        if (!_snapLogged)
        {
            _snapLogged = true;
            Diag.Log($"面板：首帧数据 综合={_sysCpu * 0.4 + _memPct * 0.6:F1}% CPU={_sysCpu:F1}% "
                     + $"内存={_memPct:F1}% 上限={_maxOcc:F0}MB 应用={s.Procs.Count} 设备={_devices.Count} "
                     + $"显卡={s.Gpu.Name}({s.Gpu.Util:F0}%) 磁盘={s.Disks.Count} 网络={s.Net.Name}");
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

        // 显示设置回填（正在编辑的框不动，免得 1 秒一次轮询把输入吃掉）
        Prefill(setMaxOcc, UiSettings.MaxOccMb > 0 ? UiSettings.MaxOccMb : _commitLimitMb);
        Prefill(setPoll, UiSettings.PollSecs, "0.##");
        Prefill(setAppLimit, s.AutoAppLimit);
        Prefill(setThreshold, s.AutoThresholdMb);
        Prefill(setCheck, s.AutoCheckSecs);
        Prefill(setGap, s.AutoMinGapSecs);
    }

    static void Prefill(TextBox box, double value, string fmt = "F0")
    {
        if (!box.IsFocused && value > 0) box.Text = value.ToString(fmt);
    }

    /* ---- 显示设置：最大占用 / 刷新间隔（壳侧落盘）+ 自动整理三项与列表条数（采集端落盘） ---- */

    async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        double maxOcc = Parse(setMaxOcc, UiSettings.MaxOccMb);
        double poll = Parse(setPoll, UiSettings.PollSecs);
        int appLimit = (int)Parse(setAppLimit, 40);
        int threshold = (int)Parse(setThreshold, 2048);
        int check = (int)Parse(setCheck, 60);
        int gap = (int)Parse(setGap, 180);

        UiSettings.MaxOccMb = Math.Max(0, maxOcc);
        UiSettings.PollSecs = Math.Min(10, Math.Max(0.3, poll));
        UiSettings.Save();
        _poll.Interval = TimeSpan.FromSeconds(UiSettings.PollSecs);

        var a = await MemoryApi.SetAutoAsync(null, threshold, check, gap, Math.Max(1, appLimit));
        setHint.Text = a == null ? "采集端那边的设置没保存上" : "已保存";
        Diag.Log($"面板：显示设置 最大占用={UiSettings.MaxOccMb:F0}MB（0=跟随提交上限{_commitLimitMb:F0}）"
                 + $" 刷新={UiSettings.PollSecs:0.##}s 应用概况={appLimit}条 阈值={threshold}MB "
                 + $"检查={check}s 最小间隔={gap}s");
        Poll();
    }

    async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        UiSettings.MaxOccMb = 0;
        UiSettings.PollSecs = 1.0;
        UiSettings.Save();
        _poll.Interval = TimeSpan.FromSeconds(1.0);
        await MemoryApi.SetAutoAsync(null, 2048, 60, 180, 40);
        setMaxOcc.Text = _commitLimitMb.ToString("F0");
        setPoll.Text = "1";
        setAppLimit.Text = "40";
        setThreshold.Text = "2048";
        setCheck.Text = "60";
        setGap.Text = "180";
        setHint.Text = "已恢复默认";
        Diag.Log("面板：显示设置恢复默认");
        Poll();
    }

    static double Parse(TextBox box, double fallback) =>
        double.TryParse(box.Text.Trim(), out var v) && v >= 0 ? v : fallback;

    /* ---- 页1：应用概况（按 pid 序列判断是重建还是就地更新，与参考一致） ---- */

    void SyncApps(List<AppInfo> procs)
    {
        var rows = new List<AppRow>(procs.Count);
        foreach (var p in procs)
        {
            string disp = string.IsNullOrEmpty(p.Display) ? p.Name : p.Display;
            rows.Add(new AppRow
            {
                Pid = p.Pid,
                Name = disp,
                Sub = string.IsNullOrEmpty(p.Title) ? p.Name : p.Title,
                IconKey = IconRules.For((p.Name ?? "") + " " + (p.Display ?? "")),
                Cpu = p.Cpu,
                MemMb = p.MemMb,
            });
        }
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

    /* ---- 页2：设备行（key 序列变化才重建，否则就地更新并推进走势） ---- */

    List<DeviceRow> BuildDevices(MemSnapshot s)
    {
        var devs = new List<DeviceRow>();
        var cpu = s.Cpu;
        devs.Add(new DeviceRow
        {
            Key = "cpu", Type = "cpu", IconKey = "cpu", Name = "处理器",
            Sub = $"{cpu.Threads:F0} 线程 · {cpu.Util:F0}% 负载",
            Util = cpu.Util,
            Linev = cpu.Max > 0 ? Math.Max(0, Math.Min(100, cpu.Freq / cpu.Max * 100)) : 0,
            Cur1Lbl = "占用", Cur1Val = cpu.Util.ToString("F0") + "%",
            Cur2Lbl = "速度", Cur2Val = cpu.Freq.ToString("F2") + " GHz",
            Spec = $"最高 {(cpu.Max > 0 ? cpu.Max.ToString("F2") : "—")} GHz",
            Detail = new List<string[]>
            {
                new[] { "型号", string.IsNullOrEmpty(cpu.Name) ? "—" : cpu.Name },
                new[] { "逻辑处理器", cpu.Threads.ToString("F0") + " 线程" },
                new[] { "基准频率", cpu.Base.ToString("F2") + " GHz" },
                new[] { "最高频率", cpu.Max.ToString("F2") + " GHz" },
            },
        });
        var gpu = s.Gpu;
        devs.Add(new DeviceRow
        {
            Key = "gpu", Type = "gpu", IconKey = "gpu", Name = "显卡",
            Sub = gpu.MemTotal > 0
                ? $"独立显存 {(!double.IsNaN(gpu.MemUsed) ? gpu.MemUsed.ToString("F1") + " / " : "")}{gpu.MemTotal:F1} GB"
                : gpu.Name,
            Util = gpu.Util,
            Linev = !double.IsNaN(gpu.Freq) ? Math.Max(0, Math.Min(100, gpu.Freq / 20)) : gpu.Util,
            Cur1Lbl = "占用", Cur1Val = gpu.Util.ToString("F0") + "%",
            Cur2Lbl = "核心频率", Cur2Val = !double.IsNaN(gpu.Freq) ? gpu.Freq.ToString("F0") + " MHz" : "—",
            Spec = $"{(gpu.MemTotal > 0 ? gpu.MemTotal.ToString("F1") : "—")} GB 显存",
            Detail = new List<string[]>
            {
                new[] { "型号", string.IsNullOrEmpty(gpu.Name) ? "—" : gpu.Name },
                new[] { "显存", (gpu.MemTotal > 0 ? gpu.MemTotal.ToString("F1") : "—") + " GB" },
                new[] { "已用显存", !double.IsNaN(gpu.MemUsed) ? gpu.MemUsed.ToString("F1") + " GB" : "—" },
            },
        });
        var mem = s.Mem;
        devs.Add(new DeviceRow
        {
            Key = "mem", Type = "mem", IconKey = "mem", Name = "内存",
            Sub = $"已用 {mem.UsedGb:F1} / {mem.TotalGb:F1} GB",
            Util = mem.Pct, Linev = mem.Pct,
            Cur1Lbl = "占用", Cur1Val = mem.Pct.ToString("F0") + "%",
            Cur2Lbl = "速度", Cur2Val = string.IsNullOrEmpty(mem.Speed) ? "—" : mem.Speed,
            Spec = $"{mem.TotalGb:F0} GB {mem.Type}".Trim(),
            Detail = new List<string[]>
            {
                new[] { "容量", mem.TotalGb.ToString("F1") + " GB" },
                new[] { "类型", string.IsNullOrEmpty(mem.Type) ? "—" : mem.Type },
                new[] { "频率", string.IsNullOrEmpty(mem.Speed) ? "—" : mem.Speed },
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
                Sub = $"已用 {d.UsedGb:F1} / {d.TotalGb:F1} GB",
                Util = d.Util, Linev = Math.Max(0, Math.Min(100, rwNum / 8)),
                Cur1Lbl = "占用", Cur1Val = d.Util.ToString("F0") + "%",
                Cur2Lbl = "读写", Cur2Val = string.IsNullOrEmpty(d.Rw) ? "—" : d.Rw,
                Spec = $"{d.TotalGb:F0} GB {d.Media}".Trim(),
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
            Sub = $"链路 {net.Link:F0} Mbps",
            Util = net.Util,
            Linev = Math.Max(0, Math.Min(100, net.Up / (net.Link > 0 ? net.Link : 1000) * 400)),
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
            var old = _devices.ToDictionary(d => d.Key, d => d.Hist.ToList());
            foreach (var d in list)
            {
                if (old.TryGetValue(d.Key, out var h)) foreach (var v in h) d.Push(v);
                else d.Push(d.Util);
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
            }
        }
        foreach (var d in _devices)
        {
            d.Push(d.Util);
            d.Notify();          // HistTick 已随 Push 自增，走势图会跟着重绘
        }
    }

    /* ---- 页1：概览数字（综合占用 = 0.4×CPU + 0.6×内存） ---- */

    void UpdateOverview()
    {
        double comp = Math.Max(0, Math.Min(100, _sysCpu * 0.4 + _memPct * 0.6));
        double actual = comp / 100 * _maxOcc;
        gauge.Comp = comp;
        bigNum.Text = Math.Round(actual).ToString();
        actualVal.Text = Math.Round(actual).ToString();
        ofMax.Text = "/ " + Math.Round(_maxOcc);
        maxVal.Text = Math.Round(_maxOcc).ToString();
        tagCpu.Text = $"CPU {_sysCpu:F0}%";
        tagMem.Text = $"MEM {_memPct:F0}%";
        tagComp.Text = $"综合 {comp:F1}%";
    }

    void TickUi()
    {
        var up = TimeSpan.FromMilliseconds(_since.ElapsedMilliseconds);
        uptime.Text = $"{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";
        double sec = (DateTime.Now - _lastRefresh).TotalSeconds;
        lastRefresh.Text = sec < 3 ? "刚刚" : $"{(int)sec} 秒前";
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (_page != 0 || !IsVisible) return;
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

    /* ---- 进程表 ---- */

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
                _procLogged = true;
                Diag.Log($"面板：进程表已加载 {list.Count} 个进程，最大内存 "
                         + (list.Count > 0 ? list[0].Name + " " + list[0].MemVal + list[0].MemUnit : "—"));
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
        double maxCpu = Math.Max(5.0, _procAll.Count > 0 ? _procAll.Max(r => r.Cpu) : 1.0);
        double maxMem = Math.Max(200.0, _procAll.Count > 0 ? _procAll.Max(r => r.MemMb) : 1.0);
        foreach (var r in _procAll)
        {
            r.CpuBar = Math.Min(86.0, Math.Max(2.0, r.Cpu / maxCpu * 86.0));
            r.MemBar = Math.Min(86.0, Math.Max(2.0, r.MemMb / maxMem * 86.0));
        }
        Func<ProcRow, object> key = _sortPath switch
        {
            "Name" => r => r.Name,
            "Cpu" => r => r.Cpu,
            "Threads" => r => r.Threads,
            _ => r => r.MemMb,
        };
        var ordered = (_sortDesc ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToList();
        int? keep = (procList.SelectedItem as ProcRow)?.Pid;
        procList.ItemsSource = ordered;
        if (keep != null)
        {
            var again = ordered.FirstOrDefault(r => r.Pid == keep);
            if (again != null) procList.SelectedItem = again;
        }
        string arrow = _sortDesc ? " ▾" : " ▴";
        headName.Text = "进程名称" + (_sortPath == "Name" ? arrow : "");
        headCpu.Text = "CPU 占用" + (_sortPath == "Cpu" ? arrow : "");
        headMem.Text = "内存占用" + (_sortPath == "Mem" ? arrow : "");
        procCount.Text = q.Length > 0
            ? $"筛选出 {ordered.Count} / {_procAll.Count} 个进程"
            : $"{_procAll.Count} 个进程 · 每 2 秒刷新（CPU% 是这两秒的均值，第一次打开都是 0）";
    }

    void Sort_Name(object sender, MouseButtonEventArgs e) => SetSort("Name");
    void Sort_Cpu(object sender, MouseButtonEventArgs e) => SetSort("Cpu");
    void Sort_Mem(object sender, MouseButtonEventArgs e) => SetSort("Mem");

    void SetSort(string path)
    {
        _sortDesc = path == _sortPath ? !_sortDesc : path != "Name";
        _sortPath = path;
        RefreshGrid();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => RefreshGrid();

    async void RefreshProc_Click(object sender, RoutedEventArgs e) => await LoadProcsAsync();

    async void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (_killing) return;
        if (procList.SelectedItem is not ProcRow row)
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