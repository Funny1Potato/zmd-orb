using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 图标库：24×24 描边图标，取自官方 Lucide 图标集（ISC 许可；许可全文与署名见仓库根目录的
/// THIRD-PARTY-LICENSES.txt）。路径数据按官方发布原样使用、只压成一行。
///
/// 用一个小小的形状解析器把 path/line/polyline/polygon/circle/rect/ellipse 转成 WPF Geometry，
/// 由 IconView 统一按 24×24 缩放后描边渲染（圆头圆角），与界面其余部分的观感一致。
/// </summary>
static class IconFactory
{
    static readonly Dictionary<string, string> Src = new()
    {
        ["activity"] = @"<path d=""M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2""/>",
        ["app"] = @"<rect width=""7"" height=""7"" x=""3"" y=""3"" rx=""1""/><rect width=""7"" height=""7"" x=""14"" y=""3"" rx=""1""/><rect width=""7"" height=""7"" x=""14"" y=""14"" rx=""1""/><rect width=""7"" height=""7"" x=""3"" y=""14"" rx=""1""/>",
        ["arrow"] = @"<path d=""M5 12h14""/><path d=""m12 5 7 7-7 7""/>",
        ["browser"] = @"<circle cx=""12"" cy=""12"" r=""10""/><path d=""M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20""/><path d=""M2 12h20""/>",
        ["chat"] = @"<path d=""M22 17a2 2 0 0 1-2 2H6.828a2 2 0 0 0-1.414.586l-2.202 2.202A.71.71 0 0 1 2 21.286V5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2z""/>",
        ["chevron"] = @"<path d=""m6 9 6 6 6-6""/>",
        ["code"] = @"<path d=""m16 18 6-6-6-6""/><path d=""m8 6-6 6 6 6""/>",
        ["cog"] = @"<path d=""M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915""/><circle cx=""12"" cy=""12"" r=""3""/>",
        ["cpu"] = @"<path d=""M12 20v2""/><path d=""M12 2v2""/><path d=""M17 20v2""/><path d=""M17 2v2""/><path d=""M2 12h2""/><path d=""M2 17h2""/><path d=""M2 7h2""/><path d=""M20 12h2""/><path d=""M20 17h2""/><path d=""M20 7h2""/><path d=""M7 20v2""/><path d=""M7 2v2""/><rect x=""4"" y=""4"" width=""16"" height=""16"" rx=""2""/><rect x=""8"" y=""8"" width=""8"" height=""8"" rx=""1""/>",
        ["db"] = @"<ellipse cx=""12"" cy=""5"" rx=""9"" ry=""3""/><path d=""M3 5V19A9 3 0 0 0 21 19V5""/><path d=""M3 12A9 3 0 0 0 21 12""/>",
        ["disk"] = @"<path d=""M10 16h.01""/><path d=""M2.212 11.577a2 2 0 0 0-.212.896V18a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-5.527a2 2 0 0 0-.212-.896L18.55 5.11A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z""/><path d=""M21.946 12.013H2.054""/><path d=""M6 16h.01""/>",
        ["folder"] = @"<path d=""M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z""/>",
        ["gauge"] = @"<path d=""m12 14 4-4""/><path d=""M3.34 19a10 10 0 1 1 17.32 0""/>",
        ["gpu"] = @"<rect width=""20"" height=""14"" x=""2"" y=""3"" rx=""2""/><line x1=""8"" x2=""16"" y1=""21"" y2=""21""/><line x1=""12"" x2=""12"" y1=""17"" y2=""21""/>",
        ["grid"] = @"<path d=""M12 3v18""/><path d=""M3 12h18""/><rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/>",
        ["layers"] = @"<path d=""M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z""/><path d=""M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12""/><path d=""M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17""/>",
        ["mem"] = @"<path d=""M12 12v-2""/><path d=""M12 18v-2""/><path d=""M16 12v-2""/><path d=""M16 18v-2""/><path d=""M2 11h1.5""/><path d=""M20 18v-2""/><path d=""M20.5 11H22""/><path d=""M4 18v-2""/><path d=""M8 12v-2""/><path d=""M8 18v-2""/><rect x=""2"" y=""6"" width=""20"" height=""10"" rx=""2""/>",
        ["more"] = @"<circle cx=""12"" cy=""12"" r=""1""/><circle cx=""19"" cy=""12"" r=""1""/><circle cx=""5"" cy=""12"" r=""1""/>",
        ["music"] = @"<path d=""M9 18V5l12-2v13""/><circle cx=""6"" cy=""18"" r=""3""/><circle cx=""18"" cy=""16"" r=""3""/>",
        ["net"] = @"<path d=""M12 20h.01""/><path d=""M2 8.82a15 15 0 0 1 20 0""/><path d=""M5 12.859a10 10 0 0 1 14 0""/><path d=""M8.5 16.429a5 5 0 0 1 7 0""/>",
        ["refresh"] = @"<path d=""M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8""/><path d=""M21 3v5h-5""/><path d=""M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16""/><path d=""M8 16H3v5""/>",
        ["terminal"] = @"<path d=""M12 19h8""/><path d=""m4 17 6-6-6-6""/>",
        ["video"] = @"<path d=""m16 13 5.223 3.482a.5.5 0 0 0 .777-.416V7.87a.5.5 0 0 0-.752-.432L16 10.5""/><rect x=""2"" y=""6"" width=""14"" height=""12"" rx=""2""/>",
        ["x"] = @"<path d=""M18 6 6 18""/><path d=""m6 6 12 12""/>",
    };

    static readonly Dictionary<string, Geometry> Cache = new();
    static readonly Regex ElementRe = new(
        @"<(path|line|polyline|polygon|circle|rect|ellipse)\b([^>]*?)/?>", RegexOptions.Compiled);
    static readonly Regex AttrRe = new(@"([\w-]+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    public static Geometry Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var group = new GeometryGroup();
        if (Src.TryGetValue(key, out var src))
        {
            foreach (Match m in ElementRe.Matches(src))
            {
                var attrs = new Dictionary<string, string>();
                foreach (Match a in AttrRe.Matches(m.Groups[2].Value))
                    attrs[a.Groups[1].Value] = a.Groups[2].Value;
                var g = Shape(m.Groups[1].Value, attrs);
                if (g != null) group.Children.Add(g);
            }
        }
        group.Freeze();
        Cache[key] = group;
        return group;
    }

    static Geometry? Shape(string tag, Dictionary<string, string> a)
    {
        double N(string k, double d = 0) =>
            a.TryGetValue(k, out var v) && double.TryParse(v, out var r) ? r : d;
        switch (tag)
        {
            case "path":
                if (!a.TryGetValue("d", out var d)) return null;
                try { return PathGeometry.Parse(d); } catch { return null; }
            case "line":
                return new LineGeometry(new Point(N("x1"), N("y1")), new Point(N("x2"), N("y2")));
            case "polyline":
                return FromPoints(a.GetValueOrDefault("points"), false);
            case "polygon":
                return FromPoints(a.GetValueOrDefault("points"), true);
            case "circle":
                return new EllipseGeometry(new Point(N("cx"), N("cy")), N("r"), N("r"));
            case "ellipse":
                return new EllipseGeometry(new Point(N("cx"), N("cy")), N("rx"), N("ry"));
            case "rect":
                var r = new RectangleGeometry(new Rect(N("x"), N("y"), N("width"), N("height")),
                                              N("rx"), N("ry", N("rx")));
                return r;
        }
        return null;
    }

    static Geometry? FromPoints(string? pts, bool close)
    {
        if (string.IsNullOrWhiteSpace(pts)) return null;
        var nums = new List<double>();
        foreach (var t in pts.Split(new[] { ' ', ',', '\n', '\r', '\t' },
                                    StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(t, out var v)) nums.Add(v);
        if (nums.Count < 4) return null;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(nums[0], nums[1]), close, close);
            for (int i = 2; i + 1 < nums.Count; i += 2)
                c.LineTo(new Point(nums[i], nums[i + 1]), true, false);
        }
        g.Freeze();
        return g;
    }
}