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
/// 悬浮球窗口。M0 状态与 Web 版一致：单击 = 整理动画（**演示**，真清理在 M1）、拖拽 = 移动窗口、
/// 右键/双击 = 打开面板、取不到采集端就回落演示数据。
/// </summary>
public partial class BallWindow : Window
{
    const double CooldownMs = 30000;    // 冷却 30s
    const double DragThreshold = 4;     // 位移超过 4px 算拖拽，否则算单击
    const double CleanMs = 900, PulseMs = 700;

    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Brush _warn = new SolidColorBrush(Ring.Hex("#c2703a"));

    // 取数状态
    bool _live;
    double _memPct, _availMb, _commitPct, _cacheMb;
    double _demoPct = 62;
    bool _polling;

    // 帧循环
    TimeSpan _lastFrame = TimeSpan.MinValue, _now;
    long _fpsFrames;
    TimeSpan _fpsMark;

    // 交互
    bool _hover, _pressed, _dragged, _busy, _quitting;
    Point _pressScreen;
    Point _dragFrom;
    long _lastCleanTick;

    CleanAnim? _anim;

    sealed class CleanAnim
    {
        public double From, Target, FreedGb;
        public TimeSpan T0;
    }

    public BallWindow()
    {
        InitializeComponent();
        _warn.Freeze();
        Poll();
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        CompositionTarget.Rendering += OnFrame;
        Loaded += (_, _) => { Render(); Diag.LogBallFacts(this); };
    }

    /// <summary>粒子团实测帧率（面板上显示，用来盯常驻开销）。</summary>
    public double BlobFps { get; private set; }

    public void PrepareQuit() => _quitting = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Win32.HideFromTaskbar(new System.Windows.Interop.WindowInteropHelper(this).Handle);
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
            var s = await MemoryApi.GetAsync();
            if (s != null)
            {
                _live = true;
                _memPct = s.Pct;
                _availMb = s.AvailMb;
                _commitPct = s.CommitPct;
                _cacheMb = s.SystemCacheMb;
            }
            else
            {
                _live = false;
                _demoPct = Math.Max(6, Math.Min(96, _demoPct + (Random.Shared.NextDouble() - 0.5) * 1.4));
                _memPct = _demoPct;
                _availMb = (32 - 32 * _memPct / 100) * 1024;
                _commitPct = 0;
                _cacheMb = 0;
            }
            if (!_busy) Render();
        }
        finally { _polling = false; }
    }

    void Render()
    {
        double p = Math.Max(0, Math.Min(100, _memPct));
        Visual.Pct = p;
        PctNum.Text = Math.Round(p).ToString();

        SubText.Inlines.Clear();
        if (_live)
        {
            SubText.Inlines.Add(new Run("可用 " + (_availMb / 1024).ToString("F1") + "G"));
            if (_commitPct >= 85)      // 提交额度才是真会把程序打崩的东西，超 85% 标出来
                SubText.Inlines.Add(new Run(" · 提交 " + _commitPct.ToString("F0") + "%") { Foreground = _warn });
        }
        else
        {
            SubText.Inlines.Add(new Run("演示数据"));
        }
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

        if (_anim != null) StepClean();
    }

    /* ---------------- 单击：整理动画（M0 演示） ---------------- */

    void DoClean()
    {
        if (_busy) return;
        long now = Environment.TickCount64;
        if (_lastCleanTick != 0 && now - _lastCleanTick < CooldownMs)
        {
            ShowFloat("冷却中 " + (long)Math.Ceiling((CooldownMs - (now - _lastCleanTick)) / 1000.0) + "s");
            return;
        }
        _lastCleanTick = now;
        _busy = true;
        ScaleTo(1.09, 120);

        double from = _memPct;
        // 结果文案刻意写"整理 N GB 缓存页"而不是"释放内存"：待命列表被清掉后系统还会按需缓存回来
        _anim = new CleanAnim
        {
            From = from,
            Target = Math.Max(8, from - (6 + Random.Shared.NextDouble() * 10)),
            FreedGb = _live && _cacheMb > 0 ? _cacheMb * 0.35 / 1024 : 0.8 + Random.Shared.NextDouble() * 1.6,
            T0 = _now,
        };
        Diag.Log($"球被点击 → 整理动画（M0 演示）from={from:F1}%");
    }

    void StepClean()
    {
        var a = _anim!;
        double k = (_now - a.T0).TotalMilliseconds / CleanMs;

        if (k >= 1)
        {
            _anim = null;
            _busy = false;
            Visual.SetBreathe(1, 1);
            Render();                                   // 落回真实占用
            ScaleTo(_hover ? 1.05 : 1, 160);
            ShowFloat("演示：整理 " + a.FreedGb.ToString("F1") + " GB 缓存页");
            return;
        }

        double p;
        if (k < 0.45) p = a.From + (100 - a.From) * (k / 0.45);
        else
        {
            double u = (k - 0.45) / 0.55, ease = 1 - Math.Pow(1 - u, 3);
            p = 100 + (a.Target - 100) * ease;
        }
        Visual.Pct = p;
        PctNum.Text = Math.Round(p).ToString();

        double pk = k * CleanMs / PulseMs;              // 粒子团"吸一下再回弹"
        if (pk < 1)
        {
            double sq, am;
            if (pk < 0.45) { double u = pk / 0.45; sq = 1 - 0.26 * u; am = 1 - 0.5 * u; }
            else { double u = (pk - 0.45) / 0.55; sq = 1 + 0.10 * Math.Sin(u * Math.PI) - 0.26 * (1 - u); am = 0.5 + 0.5 * u; }
            Visual.SetBreathe(sq, am);
        }
        else Visual.SetBreathe(1, 1);
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
        _dragFrom = new Point(Left, Top);
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

        // Left/Top 是 DIP，鼠标位移是物理像素，按当前 DPI 折算
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = _dragFrom.X + dx / dpi;
        Top = _dragFrom.Y + dy / dpi;
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

    void Stage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        (Application.Current as App)?.OpenPanel();
    }

    /* ---------------- 浮字提示 ---------------- */

    void ShowFloat(string text)
    {
        FloatText.Text = text;
        FloatShift.BeginAnimation(TranslateTransform.YProperty, null);
        FloatShift.Y = 4;
        FloatText.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
        FloatShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-10, TimeSpan.FromMilliseconds(450))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1700) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            FloatText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            FloatShift.BeginAnimation(TranslateTransform.YProperty, null);
            FloatShift.Y = 4;
        };
        t.Start();
    }
}