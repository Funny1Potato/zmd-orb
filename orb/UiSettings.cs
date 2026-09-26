using System;
using System.IO;
using System.Text.Json;

namespace ZmdOrb;

/// <summary>
/// 壳自己的显示设置（%LOCALAPPDATA%/zmd-orb/ui.json）。
/// 采集端那边的设置（自动整理阈值、应用概况条数）走 /auto，存在采集端的 auto.json 里。
/// </summary>
static class UiSettings
{
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "zmd-orb", "ui.json");

    /// <summary>综合占用 100% 对应的 MB 上限（分母）的出厂默认值。0 仍表示"跟随提交额度上限"。</summary>
    public const double DefaultMaxOccMb = 325799;

    const double DefWCpu = 0.4, DefWMem = 0.6;

    /// <summary>综合占用的分母（MB）：综合占用 100% 对应这么多 MB。0 = 跟随提交额度上限。</summary>
    public static double MaxOccMb { get; set; } = DefaultMaxOccMb;

    /// <summary>综合占用 里 CPU 占用率的权重（默认 0.4）。</summary>
    public static double WCpu { get; set; } = DefWCpu;

    /// <summary>综合占用 里 内存占用率的权重（默认 0.6）。</summary>
    public static double WMem { get; set; } = DefWMem;

    /// <summary>面板轮询间隔（秒）。</summary>
    public static double PollSecs { get; set; } = 1.0;

    sealed class Dto
    {
        public double max_occ_mb { get; set; } = DefaultMaxOccMb;
        public double w_cpu { get; set; } = DefWCpu;
        public double w_mem { get; set; } = DefWMem;
        public double poll_secs { get; set; } = 1.0;
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(Path)) return;
            var d = JsonSerializer.Deserialize<Dto>(File.ReadAllText(Path));
            if (d == null) return;
            MaxOccMb = d.max_occ_mb;
            // 两个权重加起来必须 >0（旧的 ui.json 里没有这两项，会按上面 Dto 的默认值补上）
            if (d.w_cpu + d.w_mem > 0) { WCpu = d.w_cpu; WMem = d.w_mem; }
            PollSecs = d.poll_secs > 0.2 ? d.poll_secs : 1.0;
        }
        catch (Exception e)
        {
            Diag.Log("读显示设置失败：" + e.Message);
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(
                new Dto
                {
                    max_occ_mb = MaxOccMb,
                    w_cpu = WCpu,
                    w_mem = WMem,
                    poll_secs = PollSecs,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Diag.Log("写显示设置失败：" + e.Message);
        }
    }
}