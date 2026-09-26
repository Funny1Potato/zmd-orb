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

    /// <summary>最大占用（MB）。0 = 跟随提交额度上限（默认）。</summary>
    public static double MaxOccMb { get; set; }

    /// <summary>面板轮询间隔（秒）。</summary>
    public static double PollSecs { get; set; } = 1.0;

    sealed class Dto
    {
        public double max_occ_mb { get; set; }
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
                new Dto { max_occ_mb = MaxOccMb, poll_secs = PollSecs },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Diag.Log("写显示设置失败：" + e.Message);
        }
    }
}