using System.Windows;

namespace ZmdOrb;

public partial class App : Application
{
    BackendProcess? _backend;
    BallWindow? _ball;
    PanelWindow? _panel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Diag.Log("=== 壳启动 ===");
        _backend = BackendProcess.Start();

        _ball = new BallWindow();
        _panel = new PanelWindow { BallFps = () => _ball?.BlobFps ?? 0 };
        _ball.Show();
    }

    public void OpenPanel()
    {
        if (_panel == null) return;
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
        _backend?.Dispose();
        Diag.Log("=== 壳退出 ===");
        base.OnExit(e);
    }
}