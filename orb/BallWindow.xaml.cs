using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ZmdOrb;

/// <summary>
/// 悬浮球窗口。单击 = 轻度整理（免提权）、拖拽 = 移动窗口（整球限制在某台显示器工作区内）、
/// 双击 = 打开面板、右键 = 菜单（打开面板 / 轻度整理 / 切到托盘模式 / 退出）、
/// 取不到采集端就回落演示数据。
/// </summary>
public partial class BallWindow : Window
{
    const double CooldownMs = 30000;    // 冷却 30s（与采集端的 THROTTLE 保持一致）
    const double DragThreshold = 4;     // 位移超过 4px 算拖拽，否则算单击
    const double SweepMs = 600, SettleMs = 520;
    const string Tier = "l1";           // 单击只跑轻度：免提权、不弹 UAC

    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    static readonly Brush Ink = Frozen("#4a4a46");   // 有实时数据
    static readonly Brush Dim = Frozen("#a2a29b");   // 回落演示数据：数字调暗，免得把编的数值当真

    // 取数状态
    bool _live;
    double _memPct, _commitPct;      // 内存占用率（球面数字 + 左侧橙条）/ 提交额度占用率（右侧蓝条）
    double _demoPct = 62;
    bool _polling;

    // 帧循环
    TimeSpan _lastFrame = TimeSpan.MinValue, _now;
    long _fpsFrames;
    TimeSpan _fpsMark;

    // 交互
    bool _hover, _pressed, _dragged, _busy, _quitting;
    Point _pressScreen;              // 按下时的鼠标屏幕坐标（物理像素）
    int _dragX, _dragY;              // 按下时窗口左上角（物理像素）
    long _lastCleanTick;

    CleanAnim? _anim;

    sealed class CleanAnim
    {
        public bool Settling;          // false = 向 0 扫（等采集端结果）；true = 从 0 回涨到真实新值
        public double From, Target;
        public double FromCommit, TargetCommit;   // 右侧蓝条（提交额度）跟着做同一个手势
        public long T0;                // Environment.TickCount64：动画不依赖"有没有帧循环在跑"
    }

    bool _frames;                      // 帧循环是否挂着

    public BallWindow()
    {
        InitializeComponent();
        Poll();
        _poll.Tick += (_, _) => Poll();
        IsVisibleChanged += (_, _) => Ball_IsVisibleChanged();
        Loaded += (_, _) => { Render(); Diag.LogBallFacts(this); };
        SyncFrames();
    }

    /// <summary>帧循环该不该跑：动画在跑就必须跑（不然动画推不动、`_busy` 会一直卡着）；
    /// 其余情况只有"常规模式 + 球看得见"才跑。常驻的逐帧重画是这块最大的开销
    /// （实测 +50 MB 私有、12% 单核），所以藏起来、或轻量模式下静止时都停掉。</summary>
    void SyncFrames()
    {
        bool want = _anim != null || (!UiSettings.Lite && IsVisible);
        if (_frames == want) return;
        _frames = want;
        if (want) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>面板上勾/取消"轻量模式"时立刻生效。</summary>
    public void SetLite(bool lite)
    {
        SyncFrames();
        Visual.InvalidateVisual();   // 粒子团/辉光是从"画不画"上关的，得让球重画一次才看得出来
        Diag.Log(lite ? "球：轻量模式开（不画粒子团/辉光，静止时不逐帧重画）"
                      : "球：轻量模式关，粒子团/辉光与常驻动画都回来");
    }

    void Ball_IsVisibleChanged()
    {
        // 藏起来（托盘模式）就别轮询、也别逐帧重画了：托盘自己那份轮询照旧，
        // 两处各拉一次是白花的。托盘模式下球从头到尾没显示过，这个事件不会来。
        if (IsVisible) _poll.Start();
        else _poll.Stop();
        SyncFrames();
    }

    /// <summary>粒子团实测帧率（面板上显示，用来盯常驻开销）。</summary>
    public double BlobFps { get; private set; }

    public void PrepareQuit() => _quitting = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Win32.HideFromTaskbar(h);
        // 起始位置（XAML 里的 1200,220）在小屏幕或改过缩放后可能落到屏外，开局先夹一次
        if (Win32.GetWindowRect(h, out var r))
        {
            int beforeX = r.Left, beforeY = r.Top;
            MoveClamped(r.Left, r.Top);
            if (Win32.GetWindowRect(h, out var r2) && (r2.Left != beforeX || r2.Top != beforeY))
                Diag.Log($"球起始位置被夹回屏内：({beforeX},{beforeY}) → ({r2.Left},{r2.Top})");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitting) return;      // 真退出就放行，否则关窗只是藏起来
        e.Cancel = true;
        Hide();
    }

    /* ---------------- 取数与呈现 ---------------- */

    async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await MemoryApi.GetAsync("none");   // 球只要占用率/提交率两个数：别让采集端顺带采进程与显卡
            if (s != null)
            {
                _live = true;
                _memPct = s.Pct;
                _commitPct = s.CommitPct;
            }
            else
            {
                _live = false;
                _demoPct = Math.Max(6, Math.Min(96, _demoPct + (Random.Shared.NextDouble() - 0.5) * 1.4));
                _memPct = _demoPct;
                _commitPct = 0;
            }
            if (!_busy) Render();
        }
        finally { _polling = false; }
    }

    /// <summary>球面只显示占用率一个数；提交额度由右侧蓝条表示，不再拿它给数字换色
    /// （球面那个数是内存占用率，跟提交额度不是一个口径）。</summary>
    void Render()
    {
        double p = Math.Max(0, Math.Min(100, _memPct));
        Visual.Pct = p;
        Visual.CommitPct = Math.Max(0, Math.Min(100, _commitPct));
        PctNum.Text = Math.Round(p).ToString();
        PctText.Foreground = _live ? Ink : Dim;
    }

    /* ---------------- 帧循环（30fps 封顶） ---------------- */

    void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs re) return;
        if (_lastFrame == TimeSpan.MinValue) { _lastFrame = re.RenderingTime; _fpsMark = re.RenderingTime; return; }

        double dt = (re.RenderingTime - _lastFrame).TotalSeconds;
        if (dt < 1.0 / Ring.Fps) return;
        if (dt > 0.5) dt = 1.0 / Ring.Fps;      // 长时间没渲染（窗口被藏过），别让粒子瞬移
        _lastFrame = re.RenderingTime;
        _now = re.RenderingTime;
        Visual.Advance(dt);

        _fpsFrames++;
        double span = (_now - _fpsMark).TotalSeconds;
        if (span >= 2.0)
        {
            BlobFps = _fpsFrames / span;
            _fpsFrames = 0;
            _fpsMark = _now;
        }

        if (_anim != null) StepAnim();
    }

    /* ---------------- 单击：真的整理（M1，轻度档） ---------------- */

    string _pendingFloat = "";

    async void DoClean()
    {
        if (_busy) return;
        long now = Environment.TickCount64;
        if (_lastCleanTick != 0 && now - _lastCleanTick < CooldownMs)
        {
            ShowFloat("冷却中 " + (long)Math.Ceiling((CooldownMs - (now - _lastCleanTick)) / 1000.0) + "s");
            return;
        }
        _lastCleanTick = now;              // 先占住冷却：请求在飞的时候再点不该重入
        _busy = true;
        ScaleTo(1.09, 120);
        _anim = new CleanAnim { From = _memPct, FromCommit = _commitPct, T0 = now };   // 环与两条计量条一起向 0 扫，等结果
        SyncFrames();                      // 动画起来了 → 帧循环开（轻量模式/藏起来时也是，动画期间必须跑）
        Diag.Log($"球被点击 → 轻度整理（tier={Tier}）");

        CleanResult res;
        try { res = await MemoryApi.CleanAsync(Tier); }
        catch (Exception ex) { res = new CleanResult { Ok = false, Error = ex.Message }; }

        double? retry = res.RetryAfter;
        if (!res.Ok)
        {
            if (retry == null) _lastCleanTick = 0;      // 连接类失败不占冷却，用户可以立刻再点
            ResetAnim(retry != null ? "冷却中 " + (long)Math.Ceiling(retry.Value) + "s"
                                    : (string.IsNullOrEmpty(res.Error) ? "整理失败" : res.Error));
            return;
        }

        if (!double.IsNaN(res.PctAfter)) _memPct = res.PctAfter;
        _anim = new CleanAnim
        {
            Settling = true,
            Target = double.IsNaN(res.PctAfter) ? _memPct : res.PctAfter,
            TargetCommit = _commitPct,      // 提交额度由轮询持续更新，落回最新值
            T0 = Environment.TickCount64,
        };
        // 整理结果照旧弹字（带深色底，12pt，见 FloatBox）；口径与明细在日志/面板里
        _pendingFloat = BallLine(res);
        Diag.Log($"整理完成 {res.Tier}：{res.Summary}｜{res.Detail}");
    }

    /// <summary>球上的短文案。球面只有 160px 宽（窗口 180 是为了给放大留余量），长文案会被裁掉。</summary>
    static string BallLine(CleanResult r) => r.Tier == "l1"
        ? "换出 " + r.MovedGb.ToString("F1") + "G 工作集"
        : "整理 " + r.PurgedGb.ToString("F1") + "G 缓存页";

    void ResetAnim(string text)
    {
        _anim = null;
        _busy = false;
        Visual.SetBreathe(1, 1);
        Render();
        ScaleTo(_hover ? 1.05 : 1, 160);
        ShowFloat(text);
        SyncFrames();                          // 动画收场：轻量模式/藏起来时就把帧循环关回去
    }

    void StepAnim()
    {
        var a = _anim!;
        if (!a.Settling)
        {
            // 先清零：环与两条计量条一起从当前值扫到 0 后停住等结果（深度档要等用户点 UAC，会停得久一些）
            double k = Math.Min(1.0, (Environment.TickCount64 - a.T0) / SweepMs);
            Visual.Pct = a.From * (1 - k);
            Visual.CommitPct = a.FromCommit * (1 - k);
            PctNum.Text = Math.Round(Visual.Pct).ToString();
            Visual.SetBreathe(1 - 0.22 * k, 1 - 0.5 * k);   // 被"吸住"的观感
            return;
        }

        double k2 = (Environment.TickCount64 - a.T0) / SettleMs;
        if (k2 >= 1)
        {
            _anim = null;
            _busy = false;
            Visual.SetBreathe(1, 1);
            Render();                              // 落回真实占用（真实值已在 DoClean 里更新过）
            ScaleTo(_hover ? 1.05 : 1, 160);
            ShowFloat(_pendingFloat);
            SyncFrames();                          // 动画完了就不再逐帧重画（轻量模式/藏起来时）
            return;
        }
        double e = 1 - Math.Pow(1 - k2, 3);
        Visual.Pct = a.Target * e;                 // 从 0 回涨到整理后的真实占用
        Visual.CommitPct = a.TargetCommit * e;     // 蓝条同步回涨到最新提交额度占用率
        PctNum.Text = Math.Round(Visual.Pct).ToString();
        Visual.SetBreathe(1, 1);
    }

    /* ---------------- 悬停 / 缩放 ---------------- */

    void ScaleTo(double to, int ms)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void Stage_MouseEnter(object sender, MouseEventArgs e)
    {
        _hover = true;
        if (!_busy && !_dragged) ScaleTo(1.05, 160);
    }

    void Stage_MouseLeave(object sender, MouseEventArgs e)
    {
        _hover = false;
        if (!_busy && !_dragged) ScaleTo(1, 160);
    }

    /* ---------------- 拖拽 vs 单击 ---------------- */

    void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)      // 双击 = 打开面板
        {
            (Application.Current as App)?.OpenPanel();
            return;
        }
        _pressed = true;
        _dragged = false;
        // 用屏幕坐标算位移：PointToScreen 与位置无关，窗口自己移动也不会产生反馈
        _pressScreen = Stage.PointToScreen(e.GetPosition(Stage));
        if (Win32.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var r))
        {
            _dragX = r.Left;                    // 记物理像素：拖动全程都在物理像素里算
            _dragY = r.Top;
        }
        Stage.CaptureMouse();
        Diag.Log($"球收到点击 @屏幕({_pressScreen.X:F0},{_pressScreen.Y:F0})");
    }

    void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        Point cur = Stage.PointToScreen(e.GetPosition(Stage));
        double dx = cur.X - _pressScreen.X, dy = cur.Y - _pressScreen.Y;

        if (!_dragged)
        {
            if (Math.Sqrt(dx * dx + dy * dy) <= DragThreshold) return;
            _dragged = true;
            _hover = false;
            if (!_busy) ScaleTo(1, 120);        // 拖动开始就收掉悬停放大
            Cursor = Cursors.SizeAll;
        }

        /* 拖动全程用**物理像素** + SetWindowPos：Left/Top 是 DIP，混着算在 125% 缩放下会有取整漂移，
           多显示器不同缩放时更是对不上。同时把整球限制在某一台显示器的工作区内（可跨屏）。 */
        MoveClamped(_dragX + (int)Math.Round(dx), _dragY + (int)Math.Round(dy));
    }

    /// <summary>把球移到指定位（物理像素），整球限制在某台显示器的工作区内。
    /// 多显示器时在**每台**显示器的合法范围里挑一个离目标最近的——这样既能跨屏拖，
    /// 又不会掉到桌面外（按"当前窗口所在显示器"夹会永远跨不过去，早先就是这么卡的）。</summary>
    void MoveClamped(int x, int y)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        if (Win32.GetWindowRect(h, out var r))
        {
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            var areas = Win32.WorkAreas();
            if (areas.Count > 0)
            {
                int bx = x, by = y;
                long best = long.MaxValue;
                foreach (var a in areas)
                {
                    int cx = Math.Min(Math.Max(x, a.Left), Math.Max(a.Left, a.Right - w));
                    int cy = Math.Min(Math.Max(y, a.Top), Math.Max(a.Top, a.Bottom - ht));
                    long d = (long)(cx - x) * (cx - x) + (long)(cy - y) * (cy - y);
                    if (d < best) { best = d; bx = cx; by = cy; }
                }
                x = bx;
                y = by;
            }
        }
        Win32.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0,
                           Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }

    void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        Stage.ReleaseMouseCapture();

        if (_dragged)
        {
            _dragged = false;
            Cursor = null;
            _hover = Stage.IsMouseOver;
            if (!_busy) ScaleTo(_hover ? 1.05 : 1, 160);
        }
        else
        {
            DoClean();
        }
    }

    /* ---------------- 右键菜单（M4）：形态切换、打开面板、整理、退出 ---------------- */

    void Menu_Panel(object sender, RoutedEventArgs e) => (Application.Current as App)?.OpenPanel();

    void Menu_Clean(object sender, RoutedEventArgs e) => DoClean();

    void Menu_Lite(object sender, RoutedEventArgs e)
    {
        // IsCheckable 的菜单项，WPF 在 Click 之前已经把 IsChecked 翻好了 —— 直接信它
        bool want = miLite.IsChecked;
        (Application.Current as App)?.SetLite(want);
        Diag.Log($"球菜单：轻量模式 → {(want ? "开" : "关")}");
    }

    /// <summary>菜单弹出前把勾对齐当前设置（面板里、托盘菜单里都能改它，这里不能各说各话）。</summary>
    void Menu_Opened(object sender, RoutedEventArgs e) => miLite.IsChecked = UiSettings.Lite;

    void Menu_Mode(object sender, RoutedEventArgs e) => (Application.Current as App)?.SetMode("tray");

    void Menu_Quit(object sender, RoutedEventArgs e) => (Application.Current as App)?.QuitApp();

    /* ---------------- 浮字提示（只剩"冷却中/失败"这类短提示） ---------------- */

    void ShowFloat(string text)
    {
        FloatText.Text = text;
        FloatShift.BeginAnimation(TranslateTransform.YProperty, null);
        FloatShift.Y = 4;
        FloatBox.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
        FloatShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-10, TimeSpan.FromMilliseconds(450))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            FloatBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            FloatShift.BeginAnimation(TranslateTransform.YProperty, null);
            FloatShift.Y = 4;
        };
        t.Start();
    }

    static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush(Ring.Hex(hex));
        b.Freeze();
        return b;
    }
}