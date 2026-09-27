using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ZmdOrb;

public partial class App : Application
{
    BackendProcess? _backend;
    BallWindow? _ball;
    PanelWindow? _panel;
    TrayIcon? _tray;
    DispatcherTimer? _trayPoll;
    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    bool _trayBusy;

    // 单实例用的名字：第二次启动只负责"通知已有实例打开面板"，自己立刻退出
    const string MutexName = @"Local\zmd-orb-single";
    const string ShowEventName = @"Local\zmd-orb-show";

    /// <summary>当前形态：ball = 桌面悬浮球（默认）；tray = 只在托盘画占用率圆环。</summary>
    public string Mode { get; private set; } = "ball";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiSettings.Load();
        Diag.Log("=== 壳启动 ===" + (e.Args.Length > 0 ? " 参数：" + string.Join(" ", e.Args) : ""));

        /* 维护用（面板那两个开关走的是同一套代码）：
           zmd-orb.exe --autostart on|off / --lite on|off —— 只改设置然后退出，脚本/安装包也能这么调。 */
        for (int i = 0; i + 1 < e.Args.Length; i++)
        {
            if (e.Args[i] == "--lite")
            {
                // 不能叫 on：外层（同一个 for 体）还有一个 bool on（CS0136）
                bool liteOn = e.Args[i + 1] == "on";
                UiSettings.Lite = liteOn;
                UiSettings.Save();
                Diag.Log($"命令行：轻量模式 {(liteOn ? "on（球不逐帧重画、采集按需）" : "off")}");
                Shutdown();
                return;
            }
            if (e.Args[i] != "--autostart") continue;
            bool on = e.Args[i + 1] == "on";
            bool ok = on ? Autostart.Enable() : Autostart.Disable();
            Diag.Log($"命令行：开机自启 {(on ? "on" : "off")} → {(ok ? "成功" : "失败")}｜{Autostart.ExePath}");
            Shutdown();
            return;
        }

        /* 单实例：没有这层保护时双击两次 exe 会出现两个球、两个采集端抢 8910。
           第二个实例把"打开面板"的信号发给第一个（命名事件），然后自己退出。 */
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            Diag.Log("已有实例在运行 → 通知它打开面板，然后退出");
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch (Exception ex)
            {
                Diag.Log("通知已有实例失败：" + ex.Message);
            }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _ = Task.Run(() =>
        {
            while (true)
            {
                _showEvent.WaitOne();
                Dispatcher.Invoke(() => OpenPanel());
            }
        });

        _backend = BackendProcess.Start();
        _ball = new BallWindow();
        // 面板不在这里构造：那棵视觉树（四页 + 六张卡 + 走势图）不打开就不该占内存，见 OpenPanel()

        // --mode tray|ball 优先于落盘的设置（安装包/开机自启都靠它决定形态）
        string mode = UiSettings.Mode;
        for (int i = 0; i + 1 < e.Args.Length; i++)
            if (e.Args[i] == "--mode") mode = e.Args[i + 1];
        SetMode(mode);
    }

    /// <summary>切换形态：ball = 桌面悬浮球；tray = 收起球、只在托盘显示占用率圆环。落盘记住。</summary>
    public void SetMode(string mode)
    {
        mode = mode == "tray" ? "tray" : "ball";
        Mode = mode;
        UiSettings.Mode = mode;
        UiSettings.Save();

        if (mode == "tray")
        {
            _ball?.Hide();
            if (_tray == null)
            {
                _tray = new TrayIcon(OpenPanel, CleanNow, () => SetMode("ball"), QuitApp);
                // 轻量模式放宽到 3 秒：托盘图标本来就是"取整变了才重画"，1 秒一次没必要
                _trayPoll = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(UiSettings.Lite ? 3 : 1),
                };
                _trayPoll.Tick += async (_, _) => await PollTrayAsync();
                _trayPoll.Start();
                // 顺手把图标固定到任务栏（只做一次；系统要先建好 NotifyIconSettings 条目，
                // 首次可能还没有 → 等 3 秒再试一次，还不行就下次启动再说）
                _ = Task.Delay(300).ContinueWith(_ => Dispatcher.Invoke(() => TrayPromote.TryPromote()));
                _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => TrayPromote.TryPromote()));
            }
            _ = PollTrayAsync();
        }
        else
        {
            _trayPoll?.Stop();
            _trayPoll = null;
            _tray?.Dispose();
            _tray = null;
            _ball?.Show();
        }
        Diag.Log($"*** 形态 = {(mode == "tray" ? "托盘（不显示桌面球）" : "桌面悬浮球")}");
    }

    /// <summary>托盘自己的取数（托盘模式下球窗口是收起的，它那份轮询会停）。</summary>
    async Task PollTrayAsync()
    {
        if (_trayBusy || _tray == null) return;
        _trayBusy = true;
        try
        {
            var s = await MemoryApi.GetAsync("none");   // 托盘也只要那两个数
            if (s == null)
            {
                _tray.Update(double.NaN, "终末地加速球 · 采集端未启动");
                return;
            }
            double commit = s.CommitLimitMb > 0 ? s.CommittedMb / s.CommitLimitMb * 100 : double.NaN;
            _tray.Update(s.Pct, $"终末地加速球 · 内存 {s.Pct:F0}%"
                              + (double.IsNaN(commit) ? "" : $" · 提交 {commit:F0}%"));
        }
        finally { _trayBusy = false; }
    }

    /// <summary>托盘菜单里的"轻度整理"：直接跑 l1（免提权），结果用气泡提示。</summary>
    async void CleanNow()
    {
        try
        {
            var r = await MemoryApi.CleanAsync("l1");
            string msg = r.Ok
                ? (string.IsNullOrEmpty(r.Summary) ? "已整理" : r.Summary)
                : (string.IsNullOrEmpty(r.Error) ? "整理失败" : r.Error);
            Diag.Log($"托盘：轻度整理 → {msg}");
            _tray?.Notify("终末地加速球", msg);
        }
        catch (Exception ex)
        {
            Diag.Log("托盘：轻度整理异常 → " + ex.Message);
            _tray?.Notify("终末地加速球", "整理失败：" + ex.Message);
        }
    }

    /// <summary>轻量模式开关的即时生效（面板勾选后调这里，不用重启）。</summary>
    public void ApplyLite()
    {
        bool lite = UiSettings.Lite;
        _ball?.SetLite(lite);
        if (_trayPoll != null) _trayPoll.Interval = TimeSpan.FromSeconds(lite ? 3 : 1);
        Diag.Log($"*** 轻量模式 = {(lite ? "开（球静止不重画、采集按需）" : "关")}");
    }

    public void OpenPanel()
    {
        if (_panel == null)                        // 懒构造：不打开就不建那棵视觉树
        {
            _panel = new PanelWindow { BallFps = () => _ball?.BlobFps ?? 0 };
            Diag.Log("面板：首次打开，按需构造");
        }
        _panel.Show();
        _panel.Activate();
    }

    public void QuitApp()
    {
        _panel?.PrepareQuit();
        _ball?.PrepareQuit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayPoll?.Stop();
        _tray?.Dispose();
        _showEvent?.Dispose();
        _mutex?.Dispose();
        _backend?.Dispose();
        Diag.Log("=== 壳退出 ===");
        base.OnExit(e);
    }
}