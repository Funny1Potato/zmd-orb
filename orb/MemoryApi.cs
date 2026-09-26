using System;
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

    /// <summary>换出去/清掉的总量（GB）—— l1 看工作集（待命+已修改的增量），深层看清掉的待命。</summary>
    public double MovedGb => (Math.Abs(StandbyDeltaMb) + Math.Abs(ModifiedDeltaMb)) / 1024.0;
    public double PurgedGb => Math.Max(0, -StandbyDeltaMb) / 1024.0;
}

static class MemoryApi
{
    const string BaseUrl = "http://127.0.0.1:8910";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1200) };
    // 整理可能要等用户点 UAC（采集端的提权等待上限是 90s），所以这条链路的超时放宽
    static readonly HttpClient HttpClean = new() { Timeout = TimeSpan.FromSeconds(150) };

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
            using var resp = await HttpClean.PostAsync(BaseUrl + "/clean?tier=" + tier, null);
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
        return r;
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;
}