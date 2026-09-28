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

    /// <summary>形态：ball = 桌面悬浮球（默认）；tray = 只在托盘显示一个占用率圆环，不占桌面。</summary>
    public static string Mode { get; set; } = "ball";

    /// <summary>有没有帮用户把托盘图标"固定到任务栏"过（Win11 默认收进溢出面板）。
    /// 只做一次：之后用户若手动拖回溢出，我们不再抢。</summary>
    public static bool TrayPromoted { get; set; }

    /// <summary>轻量模式：不画粒子团/辉光/磨砂点纹理（球与面板都一样，只留环与数字）、
    /// 球静止时不逐帧重画、托盘轮询放宽、采集端只在面板要用时才采重数据。
    /// 省的是常驻的内存与 CPU（实测那 30fps 重画值 ~50 MB 私有 + 12% 单核）。</summary>
    public static bool Lite { get; set; }

    /// <summary>球实际用的轮询间隔：轻量模式下至少 3 秒。
    /// 实测壳的写合并内存在"球窗口在桌面上"时**按轮询次数**增长（1 秒 → ~10 MB/分，
    /// 5 秒 → ~2.3 MB/分，灌到 40~70 MB 才停；托盘模式没有窗口则完全不涨），
    /// 而内存占用率的整数显示本来十几秒才跳一次，1 秒的粒度是白花的。</summary>
    public static double BallPollSecs => Lite ? Math.Max(3.0, PollSecs) : PollSecs;

    /// <summary>渲染方式开关："auto" | "on" | "off"。
    /// auto = **普通模式走硬件渲染、轻量模式走软件渲染**（这就是默认）。
    /// 软件渲染实测能把那口按轮询次数灌起来的写合并池直接归 0（稳态私有 123 → 67 MB），
    /// 代价是大窗口高频重绘时 CPU 翻倍（面板开着 3.3% → 7.5% 单核）——
    /// 所以只在"就是为了省内存"的轻量模式下默认用它。
    /// 只在**启动时**生效：换渲染模式会让 WPF 重建显示上下文，运行中改不可靠。</summary>
    public static string SwMode { get; set; } = "auto";

    /// <summary>这次到底用不用软件渲染。</summary>
    public static bool SwRender => SwMode == "on" || (SwMode == "auto" && Lite);

    sealed class Dto
    {
        public double max_occ_mb { get; set; } = DefaultMaxOccMb;
        public double w_cpu { get; set; } = DefWCpu;
        public double w_mem { get; set; } = DefWMem;
        public double poll_secs { get; set; } = 1.0;
        public string mode { get; set; } = "ball";
        public bool tray_promoted { get; set; }
        public bool lite { get; set; }
        // 用 JsonElement 接：这个键历史上是 bool（true/false），现在是 string（auto/on/off），
        // 直接声明成 string 会在读到老配置时抛异常、整个 Load 一起失败（其它设置全丢）
        public JsonElement? swrender { get; set; }
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
            Mode = d.mode == "tray" ? "tray" : "ball";
            TrayPromoted = d.tray_promoted;
            Lite = d.lite;
            SwMode = NormSw(d.swrender);
        }
        catch (Exception e)
        {
            Diag.Log("读显示设置失败：" + e.Message);
        }
    }

    /// <summary>把 swrender 归一成 "auto"/"on"/"off"：老配置里它是 bool（true=on / false=off），
    /// 没写过就是 auto。</summary>
    static string NormSw(JsonElement? e)
    {
        if (e == null) return "auto";
        var v = e.Value;
        if (v.ValueKind == JsonValueKind.True) return "on";
        if (v.ValueKind == JsonValueKind.False) return "off";
        if (v.ValueKind == JsonValueKind.String)
        {
            string? s = v.GetString();
            if (s == "on" || s == "off") return s;
        }
        return "auto";
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
                    mode = Mode,
                    tray_promoted = TrayPromoted,
                    lite = Lite,
                    swrender = JsonSerializer.SerializeToElement(SwMode),
                },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Diag.Log("写显示设置失败：" + e.Message);
        }
    }
}