using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace ZmdOrb;

/// <summary>
/// 开机自启（M4）：写 HKCU 的 Run 键——不需要管理员，也不会有"启动文件夹里留死链接"的问题。
/// 命令里不带 --mode，用哪套形态跟着 ui.json 里记住的那套走（用户选了托盘就一直托盘）。
/// </summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "zmd-orb";

    /// <summary>当前 exe 的完整路径（自包含发布时就是 zmd-orb.exe 本身）。</summary>
    public static string ExePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? "";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch (Exception e)
            {
                Diag.Log("读自启注册表失败：" + e.Message);
                return false;
            }
        }
    }

    public static bool Enable()
    {
        try
        {
            string cmd = "\"" + ExePath + "\"";
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            k.SetValue(ValueName, cmd);
            Diag.Log("开机自启：已写入 " + cmd);
            return true;
        }
        catch (Exception e)
        {
            Diag.Log("写开机自启失败：" + e.Message);
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k?.GetValue(ValueName) != null)
            {
                k.DeleteValue(ValueName, false);
                Diag.Log("开机自启：已移除");
            }
            return true;
        }
        catch (Exception e)
        {
            Diag.Log("移除开机自启失败：" + e.Message);
            return false;
        }
    }
}