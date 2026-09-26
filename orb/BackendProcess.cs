using System;
using System.Diagnostics;
using System.IO;

namespace ZmdOrb;

/// <summary>
/// 采集端子进程（Python）。退出时连同整棵进程树一起回收：
/// PyInstaller onefile 会 fork 子进程承载实际逻辑，只杀直接子进程会留下占着 8910 的孤儿。
/// </summary>
sealed class BackendProcess : IDisposable
{
    Process? _proc;

    BackendProcess(Process? p) => _proc = p;

    public static BackendProcess Start()
    {
        // 壳被强杀（或上次崩溃）时采集端会成为孤儿继续占着 8910；能复用就直接复用，
        // 否则新起的实例绑不上端口，球会没来由地回落演示数据。
        if (Reusable()) return new BackendProcess(null);

        string dir = AppContext.BaseDirectory;

        // 发布版：backend.exe 与主程序同目录
        string exe = Path.Combine(dir, "backend.exe");
        if (File.Exists(exe)) return new BackendProcess(Spawn(exe, "--no-gui"));

        // 源码运行：从 exe 所在处向上找仓库里的采集端（发布目录在 bin/Release/<tfm>/<rid>/publish，
        // 从那里到仓库根有 7 层，所以给到 8 层余量）
        var d = new DirectoryInfo(dir);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            string script = Path.Combine(d.FullName, "collector", "speed_collector.py");
            if (File.Exists(script))
            {
                string py = Environment.GetEnvironmentVariable("ZMD_ORB_PYTHON") is { Length: > 0 } p ? p : "python";
                return new BackendProcess(Spawn(py, $"\"{script}\" --no-gui"));
            }
        }

        Diag.Log("未找到 backend.exe 或采集端源码，球将回落演示数据");
        return new BackendProcess(null);
    }

    static bool Reusable()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(600) };
            string body = http.GetStringAsync("http://127.0.0.1:8910/health").GetAwaiter().GetResult();
            if (body.Contains("zmd-orb-collector"))
            {
                Diag.Log("8910 上已有采集端在跑，复用它（不重复拉起）");
                return true;
            }
            Diag.Log("8910 被别的程序占着：" + body.Substring(0, Math.Min(80, body.Length)));
        }
        catch
        {
            // 没人在听，正常情况
        }
        return false;
    }

    static Process? Spawn(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,          // 不弹黑框
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,  // 重定向到日志：直接继承句柄的话 python 一 print 就崩
                RedirectStandardError = true,
                // 采集端把 stdio 钉成 UTF-8（见 use_utf8_stdio），这里按同一口径解，
                // 否则中文日志会按系统区域解成乱码
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            var p = Process.Start(psi);
            if (p == null)
            {
                Diag.Log($"spawn 失败（返回 null）：{file} {args}");
                return null;
            }
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Diag.Log(e.Data); };     // 采集端自己带 [采集端] 前缀
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Diag.Log("采集端异常| " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Diag.Log($"已拉起采集端 {file} {args} pid={p.Id}");
            return p;
        }
        catch (Exception ex)
        {
            Diag.Log($"spawn 异常 {file} {args}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        var p = _proc;
        _proc = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                Process.Start(new ProcessStartInfo("taskkill", $"/PID {p.Id} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(4000);
                Diag.Log($"已回收采集端 pid={p.Id}");
            }
        }
        catch (Exception ex)
        {
            Diag.Log("回收采集端异常: " + ex.Message);
        }
        try { p.Dispose(); } catch { }
    }
}