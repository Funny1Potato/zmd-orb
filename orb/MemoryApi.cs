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
    public double CpuUtil;
}

static class MemoryApi
{
    const string BaseUrl = "http://127.0.0.1:8910";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1200) };

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
            };
            if (root.TryGetProperty("cpu", out var c)) s.CpuUtil = Num(c, "util");
            return s;
        }
        catch
        {
            return null;    // 采集端没起来 / 超时 / JSON 变了，都按"没有实时数据"处理
        }
    }

    static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;
}