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

    // 设备页（照搬 zmd-manager 的字段）
    public CpuInfo Cpu = new();
    public GpuInfo Gpu = new();
    public MemInfo Mem = new();
    public NetInfo Net = new();
    public List<DiskInfo> Disks = new();
    public List<AppInfo> Procs = new();
    public SysInfo Sys = new();

    // 自动整理（M2）
    public bool AutoEnabled;
    public double AutoThresholdMb;
    public double AutoCheckSecs, AutoMinGapSecs, AutoAppLimit;
    public double AutoCount;
    public double AutoLastUnix;
    public string AutoReason = "";
}

sealed class CpuInfo
{
    public string Name = "", Full = "";
    public double Cores, Threads, Util, Freq, Base, Max, SeenMax, PerfPct;
}

sealed class GpuInfo
{
    public string Name = "", Full = "";
    public double Util, Freq = double.NaN, MemUsed = double.NaN, MemTotal;
    public bool Ok;
}

sealed class MemInfo
{
    public double UsedGb, TotalGb, Pct, FreeZeroMb, StandbyMb, CommittedGb, CommitLimitGb;
    public string Speed = "", Type = "", Part = "", Vendor = "";
    public double Modules, PerGb;
}

sealed class DiskInfo
{
    public string Name = "", Rw = "", Media = "", Model = "";
    public double UsedGb, TotalGb, Pct, Util;
}

sealed class NetInfo
{
    public string Name = "", Desc = "";
    public double Down, Up, Link, Util;
}

/// <summary>应用概况列表里的一项。</summary>
sealed class AppInfo
{
    public int Pid;
    public string Name = "", Display = "", Title = "";
    public double Cpu, MemMb;
    public double CommitMb;        // 已提交（提交大小）
}

/// <summary>双指标走势图的数据来源（面板与设备行的走势都用它）。</summary>
interface ITrendRow
{
    List<double> Hist { get; }     // 主指标
    List<double> Hist2 { get; }    // 次指标
    int HistTick { get; }          // 每次采样自增，绑定的图表靠它重绘
    /// <summary>两条指标类型不同（如 占用率 vs 频率）时各自一套纵轴：左=主、右=次。</summary>
    bool DualAxis { get; }
    string AxisUnit1 { get; }
    string AxisUnit2 { get; }
}

/// <summary>"应用内存"页的一行：按应用名聚合（同名多进程合并）。</summary>
sealed class AppMemRow : RowBase, ITrendRow
{
    public string Key { get; set; } = "";        // 应用名
    public string IconKey { get; set; } = "app";
    public string Name { get; set; } = "";
    public string Sub { get; set; } = "";        // "N 个进程"
    public double MemMb { get; set; }            // 已占用（工作集合计）
    public double CommitMb { get; set; }         // 已提交（提交大小合计）
    public double MemPct { get; set; }           // 占物理内存 %
    public double CommitPct { get; set; }        // 占提交额度 %
    public string MemVal { get; set; } = "";
    public string CommitVal { get; set; } = "";
    public string PctText { get; set; } = "";
    public List<double> Hist { get; } = new();
    public List<double> Hist2 { get; } = new();
    public int HistTick { get; private set; }
    // 应用内存页两条线都是"占某个总额的百分比"（类型相同）→ 共用一套纵轴
    public bool DualAxis => false;
    public string AxisUnit1 => "%";
    public string AxisUnit2 => "%";

    public void Push(double a, double b)
    {
        Hist.Add(a);
        Hist2.Add(b);
        while (Hist.Count > 180) { Hist.RemoveAt(0); Hist2.RemoveAt(0); }
        HistTick++;
    }
}

/// <summary>设备性能页的一行（在壳里按快照组装，逻辑对应参考的 applySnapshot）。
/// 注意：WPF 绑定只认**属性**不认字段——这些必须是属性，否则界面上全是空白。</summary>
sealed class DeviceRow : RowBase, ITrendRow
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "";
    public string IconKey { get; set; } = "app";
    public string Name { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Spec { get; set; } = "";
    public double Util { get; set; }              // 主指标（占用率）
    public double Linev { get; set; }             // 次指标（频率/显存/读写速率…按各自设备的真实单位）
    public bool DualAxis { get; set; }            // 两条线类型不同 → 左右各一套纵轴
    public string AxisUnit1 { get; set; } = "%";
    public string AxisUnit2 { get; set; } = "%";
    public string Cur1Lbl { get; set; } = "";
    public string Cur1Val { get; set; } = "";
    public string Cur2Lbl { get; set; } = "";
    public string Cur2Val { get; set; } = "";
    public List<string[]> Detail { get; set; } = new();
    /// <summary>两条指标的历史（最多 180 点）。</summary>
    public List<double> Hist { get; } = new();
    public List<double> Hist2 { get; } = new();
    /// <summary>每次采样自增，绑定的走势图靠它重绘。</summary>
    public int HistTick { get; private set; }

    public void Push(double a, double b)
    {
        Hist.Add(a);
        Hist2.Add(b);
        while (Hist.Count > 180) { Hist.RemoveAt(0); Hist2.RemoveAt(0); }
        HistTick++;
    }
}

/// <summary>自动整理的状态（/auto 的返回）。</summary>
sealed class AutoStatus
{
    public bool Enabled;
    public double ThresholdMb, CheckSecs, MinGapSecs, AppLimit, Count, LastUnix;
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

/// <summary>面板"系统信息"页用的机器与软件环境（采集端只在启动时采一次）。</summary>
sealed class SysInfo
{
    public string Host = "", Os = "", Kernel = "", Arch = "", NetAddrs = "";
    public double BootUnix;
}

static class MemoryApi
{
    const string BaseUrl = "http://127.0.0.1:8910";
    const string ShellToken = "zmd-orb-shell";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1500) };
    // 整理要等用户点 UAC（采集端的提权等待上限是 90s），所以这条链路的超时放宽
    static readonly HttpClient HttpAction = new() { Timeout = TimeSpan.FromSeconds(150) };

    /// <summary>写操作要带这个头：采集端据此拒绝"网页脚本偷偷发过来的"写请求。</summary>
    static HttpRequestMessage Req(HttpMethod method, string path)
    {
        var r = new HttpRequestMessage(method, BaseUrl + path);
        r.Headers.Add("X-Zmd-Orb", ShellToken);
        return r;
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
                s.AutoCheckSecs = Num(au, "check_secs");
                s.AutoMinGapSecs = Num(au, "min_gap_secs");
                s.AutoAppLimit = Num(au, "app_limit");
                s.AutoCount = Num(au, "count");
                s.AutoLastUnix = Num(au, "last");
                s.AutoReason = Str(au, "reason");
            }

            // 设备页（照搬 zmd-manager 的快照字段）
            if (root.TryGetProperty("cpu", out var cp))
            {
                s.Cpu = new CpuInfo
                {
                    Name = Str(cp, "name"), Full = Str(cp, "full"), Cores = Num(cp, "cores"),
                    Threads = Num(cp, "threads"), Util = Num(cp, "util"), Freq = Num(cp, "freq"),
                    Base = Num(cp, "base"), Max = Num(cp, "max"), SeenMax = Num(cp, "seen_max"),
                    PerfPct = NumOrNaN(cp, "perf_pct"),
                };
            }
            if (root.TryGetProperty("gpu", out var g))
            {
                s.Gpu = new GpuInfo
                {
                    Name = Str(g, "name"), Full = Str(g, "full"), Util = Num(g, "util"),
                    Freq = NumOrNaN(g, "freq"), MemUsed = NumOrNaN(g, "mem_used"),
                    MemTotal = Num(g, "mem_total"),
                    Ok = g.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True,
                };
            }
            s.Mem = new MemInfo
            {
                UsedGb = Num(m, "used"), TotalGb = Num(m, "total"), Pct = Num(m, "pct"),
                FreeZeroMb = Num(m, "free_zero_mb"), StandbyMb = Num(m, "standby_mb"),
                CommittedGb = Num(m, "committed_mb") / 1024.0,
                CommitLimitGb = Num(m, "commit_limit_mb") / 1024.0,
                Speed = Str(m, "speed"), Type = Str(m, "type"), Modules = Num(m, "modules"),
                PerGb = Num(m, "per_gb"), Part = Str(m, "part"), Vendor = Str(m, "vendor"),
            };
            if (root.TryGetProperty("net", out var nt))
            {
                s.Net = new NetInfo
                {
                    Name = Str(nt, "name"), Desc = Str(nt, "desc"), Down = Num(nt, "down"),
                    Up = Num(nt, "up"), Link = Num(nt, "link"), Util = Num(nt, "util"),
                };
            }
            if (root.TryGetProperty("disks", out var dk) && dk.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in dk.EnumerateArray())
                    s.Disks.Add(new DiskInfo
                    {
                        Name = Str(d, "name"), UsedGb = Num(d, "used"), TotalGb = Num(d, "total"),
                        Pct = Num(d, "pct"), Util = Num(d, "util"), Rw = Str(d, "rw"),
                        Media = Str(d, "media"), Model = Str(d, "model"),
                    });
            }
            if (root.TryGetProperty("procs", out var pr) && pr.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pr.EnumerateArray())
                    s.Procs.Add(new AppInfo
                    {
                        Pid = (int)Num(p, "pid"), Name = Str(p, "name"), Display = Str(p, "display"),
                        Title = Str(p, "title"), Cpu = Num(p, "cpu"), MemMb = Num(p, "mem"),
                        CommitMb = Num(p, "commit"),
                    });
            }
            if (root.TryGetProperty("sys", out var sy) && sy.ValueKind == JsonValueKind.Object)
            {
                s.Sys = new SysInfo
                {
                    Host = Str(sy, "host"), Os = Str(sy, "os"), Kernel = Str(sy, "kernel"),
                    Arch = Str(sy, "arch"), NetAddrs = Str(sy, "net_addrs"),
                    BootUnix = Num(sy, "boot"),
                };
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

    /// <summary>改自动整理/显示设置（只传要改的项；不传 on 就只改参数）。</summary>
    public static async Task<AutoStatus?> SetAutoAsync(bool? enabled = null, int? thresholdMb = null,
        int? checkSecs = null, int? minGapSecs = null, int? appLimit = null)
    {
        var q = new System.Text.StringBuilder();
        if (enabled.HasValue) q.Append("on=").Append(enabled.Value ? 1 : 0);
        void Add(string k, int? v)
        {
            if (!v.HasValue) return;
            if (q.Length > 0) q.Append('&');
            q.Append(k).Append('=').Append(v.Value);
        }
        Add("threshold_mb", thresholdMb);
        Add("check_secs", checkSecs);
        Add("min_gap_secs", minGapSecs);
        Add("app_limit", appLimit);
        if (q.Length == 0) return null;
        try
        {
            using var resp = await HttpAction.SendAsync(Req(HttpMethod.Post, "/auto?" + q));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return new AutoStatus
            {
                Enabled = root.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True,
                ThresholdMb = Num(root, "threshold_mb"),
                CheckSecs = Num(root, "check_secs"),
                MinGapSecs = Num(root, "min_gap_secs"),
                AppLimit = Num(root, "app_limit"),
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

    /// <summary>可能不存在的数值（GPU 频率/已用显存这类字段，缺了返回 NaN 让界面显示 "—"）。</summary>
    static double NumOrNaN(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : double.NaN;
}