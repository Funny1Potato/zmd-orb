using System;
using System.Runtime.InteropServices;

namespace ZmdOrb;

/// <summary>壳层需要的少量 Win32 调用：诊断与命中测试（窗口行为本身交给 WPF）。</summary>
static class Win32
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int SM_CMONITORS = 80;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                      SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    public const uint GA_ROOT = 2;

    public static string ExStyleText(IntPtr hWnd)
    {
        int ex = GetWindowLongW(hWnd, GWL_EXSTYLE);
        var parts = new System.Collections.Generic.List<string>();
        if ((ex & WS_EX_TOPMOST) != 0) parts.Add("TOPMOST");
        if ((ex & WS_EX_TOOLWINDOW) != 0) parts.Add("TOOLWINDOW");
        if ((ex & WS_EX_LAYERED) != 0) parts.Add("LAYERED");
        if ((ex & WS_EX_APPWINDOW) != 0) parts.Add("APPWINDOW");
        if ((ex & WS_EX_NOACTIVATE) != 0) parts.Add("NOACTIVATE");
        return $"0x{ex & 0xFFFFFFFF:X8} [{string.Join("|", parts)}]";
    }

    /// <summary>这个点会被哪个顶层窗口接走；用来验证透明像素是否真的点击穿透。</summary>
    public static string HitTestText(int x, int y)
    {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        return h == IntPtr.Zero ? "无窗口" : $"0x{h.ToInt64():X}";
    }

    /* ---- 显示器工作区（拖拽时把球限制在屏内） ---- */

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO mi);

    /// <summary>窗口所在显示器的工作区（物理像素，已排除任务栏）；拿不到就给虚拟屏幕范围。
    /// 这里一律用物理像素算，避开多显示器不同缩放时 DIP 换算的坑。</summary>
    public static bool WorkArea(IntPtr hWnd, out RECT work)
    {
        work = default;
        var mon = MonitorFromWindow(hWnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
        if (mon != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(mon, ref mi))
            {
                work = mi.rcWork;
                return true;
            }
        }
        int vx = GetSystemMetrics(76 /*SM_XVIRTUALSCREEN*/), vy = GetSystemMetrics(77);
        work = new RECT
        {
            Left = vx, Top = vy,
            Right = vx + GetSystemMetrics(SM_CXVIRTUALSCREEN),
            Bottom = vy + GetSystemMetrics(SM_CYVIRTUALSCREEN),
        };
        return false;
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    /// <summary>所有显示器的工作区（物理像素，各自排除自己的任务栏）。
    /// 拖拽时要在**每台**显示器的合法范围里挑一个最接近目标的位置，球才能跨屏移动。</summary>
    public static System.Collections.Generic.List<RECT> WorkAreas()
    {
        var list = new System.Collections.Generic.List<RECT>(4);
        MonitorEnumProc cb = (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(mon, ref mi)) list.Add(mi.rcWork);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list;
    }

    /// <summary>
    /// 让球不进任务栏/Alt-Tab。WPF 的 ShowInTaskbar=false 实测只做到"不加 WS_EX_APPWINDOW"
    /// （2026-09-26 装机实测 exstyle=0x00080008，没有 WS_EX_TOOLWINDOW），
    /// 而一个无属主、非 TOOLWINDOW 的顶层窗口照样占任务栏按钮，所以这里自己改扩展样式。
    /// </summary>
    public static void HideFromTaskbar(IntPtr hWnd)
    {
        int old = GetWindowLongW(hWnd, GWL_EXSTYLE);
        int now = (old | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongW(hWnd, GWL_EXSTYLE, now);
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        Diag.Log($"去任务栏: exstyle 0x{old & 0xFFFFFFFF:X8} → 0x{now & 0xFFFFFFFF:X8}");
    }
}