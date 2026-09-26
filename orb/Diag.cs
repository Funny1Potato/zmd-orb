using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>诊断日志：%TEMP%/zmd_orb_shell.log —— 后端起不来、窗口样式不对时定位用。</summary>
static class Diag
{
    public static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zmd_orb_shell.log");

    static readonly object Gate = new();

    public static void Log(string s)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"[{DateTime.Now:HH:mm:ss.fff}] {s}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* 日志写不进去也不该影响使用 */ }
    }

    public static void LogBallFacts(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        Log($"球窗口 hwnd=0x{hwnd.ToInt64():X} visible={Win32.IsWindowVisible(hwnd)} exstyle={Win32.ExStyleText(hwnd)}");
        Log($"显示器数={Win32.GetSystemMetrics(Win32.SM_CMONITORS)} " +
            $"虚拟桌面={Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN)}x{Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN)} " +
            $"DPI={VisualTreeHelper.GetDpi(w).PixelsPerDip:F2} " +
            $"位置=({w.Left:F0},{w.Top:F0}) 尺寸={w.Width:F0}x{w.Height:F0}DIP");

        if (!Win32.GetWindowRect(hwnd, out var rc)) return;
        int cx = (rc.Left + rc.Right) / 2, cy = (rc.Top + rc.Bottom) / 2;
        int r = (rc.Right - rc.Left) / 2;
        // 四角理论上落在透明像素上（应穿透给下面的窗口），圆心必须归本窗口
        Log($"命中测试 左上={Win32.HitTestText(rc.Left + 3, rc.Top + 3)}");
        Log($"命中测试 右上={Win32.HitTestText(rc.Right - 4, rc.Top + 3)}");
        Log($"命中测试 左下={Win32.HitTestText(rc.Left + 3, rc.Bottom - 4)}");
        Log($"命中测试 右下={Win32.HitTestText(rc.Right - 4, rc.Bottom - 4)}");
        Log($"命中测试 环上={Win32.HitTestText(cx, rc.Top + (int)(r * 0.35))}");
        Log($"命中测试 圆心={Win32.HitTestText(cx, cy)}（本窗口 0x{hwnd.ToInt64():X}）");
    }
}