using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ZmdOrb;

/// <summary>采集端 /snapshot 的一份读数（拿不到就返回 null，调用方回落演示数据）。</summary>
sealed class MemSnapshot
{
    public double TotalMb, AvailMb, UsedMb, Pct;
    public double SystemCacheMb, CommittedMb, CommitLimitMb, CommitPct;
    public double FreeZeroMb, StandbyMb, ModifiedMb;
    public double CpuUtil;
    public bool Admin;
    public double HardFaultRate = double.NaN;      // 页/秒，≥0

    // 自动整理（M2）
    public bool AutoEnabled;
    public double AutoThresholdMb;
    public double AutoCount;
    public double AutoLastUnix;
    public string AutoReason = "";
}

/// <summary>自动整理的状态（/auto 的返回）。</summary>
sealed class AutoStatus
{
    public bool Enabled;
    public double ThresholdMb, CheckSecs, MinGapSecs, Count, LastUnix;
    public string Reason = "";
}

/// <summary>一次整理的结果（对应采集端 /clean 的返回）。</summary>
sealed class CleanResult
{
    public bool Ok;
    public string Tier = "", Summary = "", Detail = "", Error = "";
    public bool NeedAdmin;
    public double? RetryAfter;
    // 整理后的真实读数：动画最后要落到这个值上，而不是等下一次轮询
    public double PctAfter = double.NaN;
    public double FreeZeroAfterMb = double.NaN;
    public double StandbyAfterMb = double.NaN;
    // 前后差（MB）：球上的短文案直接用这个，不解析采集端的长文案
    public double FreeZeroDeltaMb, StandbyDeltaMb, ModifiedDeltaMb, CommittedDeltaMb;
    /// <summary>没成功的步骤（"步骤名：结果"）——l3 可能部分成功，别笼统报成失败。</summary>
    public readonly System.Collections.Generic.List<string> FailedSteps = new();

    /// <summary>换出去/清掉的总量（GB）—— l1 看工作集（待命+已修改的增量），深层看清掉的待命。</summary>
    public double MovedGb => (Math.Abs(StandbyDeltaMb) + Math.Abs(ModifiedDeltaMb)) / 1024.0;
    public double PurgedGb => Math.Max(0, -StandbyDeltaMb) / 1024.0;
}

/// <summary>进程表里的一行（对应采集端 /processes 的一条）。</summary>
sealed class ProcRow
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double MemMb { get; set; }
    public double Cpu { get; set; }
    public int Threads { get; set; }
    public bool Guarded { get; set; }
    public string MemText => MemMb >= 1024 ? (MemMb / 1024).ToString("F1") + " GB"
                                           : MemMb.ToString("F0") + " MB";
    public string CpuText => Cpu <= 0.05 ? "" : Cpu.ToString("F1") + "%";
}

/// <summary>写操作的结果（结束进程等）。</summary>
sealed class ActionResult
{
    public bool Ok;
    public string Summary = "", Error = "";
}

static class MemoryApi
{
    const string BaseUrl = "http://127.0.0.1:8910";
    const string ShellToken = "zmd-orb-shell";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1500) };
    // 整理/结束进程可能要等用户点 UAC（采集端的提权等待上限是 90s），所以这两条链路的超时放宽
    static readonly HttpClient HttpAction = new() { Timeout = TimeSpan.FromSeconds(150) };

    /// <summary>写操作要带这个头：采集端据此拒绝"网页脚本偷偷发过来的"写请求。</summary>
    static HttpRequestMessage Req(HttpMethod method, string path)
    {
        var r = new HttpRequestMessage(method, BaseUrl + path);
        r.Headers.Add("X-Zmd-Orb", ShellToken);
        return r;
    }

    public static async Task<List<ProcRow>?> ProcessesAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(BaseUrl + "/processes");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("procs", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var list = new System.Collections.Generic.List<ProcRow>(512);
            foreach (var p in arr.EnumerateArray())
            {
                list.Add(new ProcRow
                {
                    Pid = (int)Num(p, "pid"),
                    Name = Str(p, "name"),
                    MemMb = Num(p, "mem_mb"),
                    Cpu = Num(p, "cpu"),
                    Threads = (int)Num(p, "threads"),
                    Guarded = p.TryGetProperty("guarded", out var g) && g.ValueKind == JsonValueKind.True,
                });
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>结束进程；tree=true 连子孙一起（采集端在权限不足时会按需提权再试）。</summary>
    public static async Task<ActionResult> KillAsync(int pid, bool tree)
    {
        try
        {
            using var resp = await HttpAction.SendAsync(
                Req(HttpMethod.Post, $"/kill?pid={pid}&tree={(tree ? 1 : 0)}"));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return new ActionResult
            {
                Ok = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                Summary = Str(root, "summary"),
                Error = Str(root, "error"),
            };
        }
        catch (Exception ex)
        {
            return new ActionResult { Ok = false, Error = "连不上采集端（" + ex.GetType().Name + "）" };
        }
    }

    public static async Task<MemSnapshot?> GetAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(BaseUrl + "/snapshot");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("live", out var live) || !live.GetBoolean()) return null;
            if (!root.TryGetProperty("mem", out var m)) return null;
            var s = new MemSnapshot
            {
                TotalMb = Num(m, "total_mb"),
                AvailMb = Num(m, "avail_mb"),
                UsedMb = Num(m, "used_mb"),
                Pct = Num(m, "pct"),
                SystemCacheMb = Num(m, "system_cache_mb"),
                CommittedMb = Num(m, "committed_mb"),
                CommitLimitMb = Num(m, "commit_limit_mb"),
                CommitPct = Num(m, "commit_pct"),
                FreeZeroMb = Num(m, "free_zero_mb"),
                StandbyMb = Num(m, "standby_mb"),
                ModifiedMb = Num(m, "modified_mb"),
            };
            if (root.TryGetProperty("cpu", out var c)) s.CpuUtil = Num(c, "util");
            s.Admin = root.TryGetProperty("admin", out var ad) && ad.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("hard_fault_rate", out var hf) && hf.ValueKind == JsonValueKind.Number)
                s.HardFaultRate = hf.GetDouble();
            if (root.TryGetProperty("auto", out var au) && au.ValueKind == JsonValueKind.Object)
            {
                s.AutoEnabled = au.TryGetProperty("enabled", out var ae) && ae.ValueKind == JsonValueKind.True;
                s.AutoThresholdMb = Num(au, "threshold_mb");
                s.AutoCount = Num(au, "count");
                s.AutoLastUnix = Num(au, "last");
                s.AutoReason = Str(au, "reason");
            }
            return s;
        }
        catch
        {
            return null;    // 采集端没起来 / 超时 / JSON 变了，都按"没有实时数据"处理
        }
    }

    /// <summary>tier: l1 轻度（免提权）/ l2 深度、l3 全部（采集端会按需弹 UAC）。</summary>
    public static async Task<CleanResult> CleanAsync(string tier)
    {
        try
        {
            using var resp = await HttpAction.SendAsync(Req(HttpMethod.Post, "/clean?tier=" + tier));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var r = ParseClean(doc.RootElement);
            r.Tier = string.IsNullOrEmpty(r.Tier) ? tier : r.Tier;
            return r;
        }
        catch (Exception ex)
        {
            return new CleanResult { Tier = tier, Ok = false, Error = "连不上采集端（" + ex.GetType().Name + "）" };
        }
    }

    /// <summary>最近一次整理的结果（面板打开时回填；没整理过则返回 null）。</summary>
    public static async Task<CleanResult?> LastCleanAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(BaseUrl + "/clean/result");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("tier", out var t) || t.ValueKind != JsonValueKind.String) return null;
            return ParseClean(root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>开关自动整理（阈值等参数在采集端配置里；面板只切开关）。</summary>
    public static async Task<AutoStatus?> SetAutoAsync(bool enabled)
    {
        try
        {
            using var resp = await HttpAction.SendAsync(
                Req(HttpMethod.Post, "/auto?on=" + (enabled ? "1" : "0")));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return new AutoStatus
            {
                Enabled = root.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True,
                ThresholdMb = Num(root, "threshold_mb"),
                CheckSecs = Num(root, "check_secs"),
                MinGapSecs = Num(root, "min_gap_secs"),
                Count = Num(root, "count"),
                LastUnix = Num(root, "last"),
                Reason = Str(root, "reason"),
            };
        }
        catch
        {
            return null;
        }
    }

    static CleanResult ParseClean(JsonElement root)
    {
        var r = new CleanResult
        {
            Ok = root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
            Tier = Str(root, "tier"),
            Summary = Str(root, "summary"),
            Detail = Str(root, "detail"),
            Error = Str(root, "error"),
        };
        r.NeedAdmin = root.TryGetProperty("need_admin", out var na) && na.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("retry_after", out var ra) && ra.ValueKind == JsonValueKind.Number)
            r.RetryAfter = ra.GetDouble();
        if (root.TryGetProperty("after", out var after))
        {
            r.PctAfter = Num(after, "pct");
            r.FreeZeroAfterMb = Num(after, "free_zero_mb");
            r.StandbyAfterMb = Num(after, "standby_mb");
        }
        if (root.TryGetProperty("delta", out var d))
        {
            r.FreeZeroDeltaMb = Num(d, "free_zero_mb");
            r.StandbyDeltaMb = Num(d, "standby_mb");
            r.ModifiedDeltaMb = Num(d, "modified_mb");
            r.CommittedDeltaMb = Num(d, "committed_mb");
        }
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in steps.EnumerateArray())
            {
                // 不能叫 ok：外层对象初始化里的 out var ok 作用域覆盖整个方法（CS0136）
                bool stepOk = st.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                if (!stepOk) r.FailedSteps.Add(Str(st, "step") + "：" + Str(st, "result"));
            }
        }
        return r;
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;
}