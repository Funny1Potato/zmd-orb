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
    const double CooldownMs = 30000;    // 冷却 30s（与采集端的 THROTTLE 保持一致）
    const double DragThreshold = 4;     // 位移超过 4px 算拖拽，否则算单击
    const double SweepMs = 600, SettleMs = 520;
    const string Tier = "l1";           // 单击只跑轻度：免提权、不弹 UAC

    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    static readonly Brush Ink = Frozen("#4a4a46");   // 有实时数据
    static readonly Brush Dim = Frozen("#a2a29b");   // 回落演示数据：数字调暗，免得把编的数值当真

    // 取数状态
    bool _live;
    double _memPct;
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
        public bool Settling;          // false = 向上扫（等采集端结果）；true = 落回真实新值
        public double From, Target;
        public TimeSpan T0;
    }

    public BallWindow()
    {
        InitializeComponent();
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
            }
            else
            {
                _live = false;
                _demoPct = Math.Max(6, Math.Min(96, _demoPct + (Random.Shared.NextDouble() - 0.5) * 1.4));
                _memPct = _demoPct;
            }
            if (!_busy) Render();
        }
        finally { _polling = false; }
    }

    /// <summary>球面只显示占用率一个数（原来的"可用 xx G / 提交 xx%"那行太小看不清，去掉了）。</summary>
    void Render()
    {
        double p = Math.Max(0, Math.Min(100, _memPct));
        Visual.Pct = p;
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
        _anim = new CleanAnim { From = _memPct, T0 = _now };   // 环向上扫，等采集端结果
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
            T0 = _now,
        };
        // 整理成功不再弹文案：球窗口只有 160px，短文案也读不清，明细留给面板与日志
        Diag.Log($"整理完成 {res.Tier}：{res.Summary}｜{res.Detail}");
    }

    void ResetAnim(string text)
    {
        _anim = null;
        _busy = false;
        Visual.SetBreathe(1, 1);
        Render();
        ScaleTo(_hover ? 1.05 : 1, 160);
        ShowFloat(text);
    }

    void StepAnim()
    {
        var a = _anim!;
        if (!a.Settling)
        {
            // 向上扫到 100 后停住等结果（深度档可能要等用户点 UAC，会停得久一些）
            double k = Math.Min(1.0, (_now - a.T0).TotalMilliseconds / SweepMs);
            Visual.Pct = a.From + (100 - a.From) * k;
            PctNum.Text = Math.Round(Visual.Pct).ToString();
            Visual.SetBreathe(0.78, 0.5);         // 被"吸住"的观感
            return;
        }

        double k2 = (_now - a.T0).TotalMilliseconds / SettleMs;
        if (k2 >= 1)
        {
            _anim = null;
            _busy = false;
            Visual.SetBreathe(1, 1);
            Render();                              // 落回真实占用（真实值已在 DoClean 里更新过）
            ScaleTo(_hover ? 1.05 : 1, 160);
            return;
        }
        double e = 1 - Math.Pow(1 - k2, 3);
        Visual.Pct = 100 + (a.Target - 100) * e;
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