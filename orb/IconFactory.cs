using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace ZmdOrb;

/// <summary>
/// 图标库（照搬 zmd-manager 的 ICONS 表：Feather 风格 24×24 描边图标）。
/// 参考里是一段段内联 SVG，这里用一个极小的形状解析器把 path/line/polyline/polygon/
/// circle/rect/ellipse 转成 WPF Geometry —— 描边渲染、圆头圆角，与参考的观感一致。
/// </summary>
static class IconFactory
{
    // 数据照抄 index.html 的 ICONS（只加了换行便于阅读，内容未改）
    static readonly Dictionary<string, string> Src = new()
    {
        ["cpu"] = @"<rect x=""6"" y=""6"" width=""12"" height=""12"" rx=""2""/><rect x=""10"" y=""10"" width=""4"" height=""4""/><path d=""M9 2v4M15 2v4M9 18v4M15 18v4M2 9h4M2 15h4M18 9h4M18 15h4""/>",
        ["gpu"] = @"<rect x=""2"" y=""4"" width=""20"" height=""13"" rx=""2""/><path d=""M8 21h8M12 17v4""/>",
        ["mem"] = @"<rect x=""2"" y=""7"" width=""20"" height=""10"" rx=""2""/><path d=""M7 17v3M12 17v3M17 17v3""/><path d=""M7 11h.01M11 11h.01M15 11h.01M19 11h.01""/>",
        ["disk"] = @"<line x1=""22"" y1=""12"" x2=""2"" y2=""12""/><path d=""M5.45 5.11A2 2 0 0 1 7.4 4h9.2a2 2 0 0 1 1.95 1.11L22 12""/><path d=""M2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6""/><line x1=""6"" y1=""18"" x2=""6.01"" y2=""18""/><line x1=""18"" y1=""18"" x2=""18.01"" y2=""18""/>",
        ["net"] = @"<path d=""M5 12.55a11 11 0 0 1 14.08 0""/><path d=""M1.42 9a16 16 0 0 1 21.16 0""/><path d=""M8.53 16.11a6 6 0 0 1 6.95 0""/><line x1=""12"" y1=""20"" x2=""12.01"" y2=""20""/>",
        ["browser"] = @"<circle cx=""12"" cy=""12"" r=""10""/><line x1=""2"" y1=""12"" x2=""22"" y2=""12""/><path d=""M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z""/>",
        ["code"] = @"<polyline points=""16 18 22 12 16 6""/><polyline points=""8 6 2 12 8 18""/>",
        ["layers"] = @"<polygon points=""12 2 22 8.5 12 15 2 8.5 12 2""/><polyline points=""2 15.5 12 22 22 15.5""/>",
        ["video"] = @"<polygon points=""23 7 16 12 23 17 23 7""/><rect x=""1"" y=""5"" width=""15"" height=""14"" rx=""2""/>",
        ["db"] = @"<ellipse cx=""12"" cy=""5"" rx=""9"" ry=""3""/><path d=""M21 12c0 1.66-4 3-9 3s-9-1.34-9-3""/><path d=""M3 5v14c0 1.66 4 3 9 3s9-1.34 9-3V5""/>",
        ["chat"] = @"<path d=""M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z""/>",
        ["music"] = @"<path d=""M9 18V5l12-2v13""/><circle cx=""6"" cy=""18"" r=""3""/><circle cx=""18"" cy=""16"" r=""3""/>",
        ["terminal"] = @"<polyline points=""4 17 10 11 4 5""/><line x1=""12"" y1=""19"" x2=""20"" y2=""19""/>",
        ["gauge"] = @"<path d=""M20.2 15.5a8.5 8.5 0 1 0-16.4 0""/><path d=""M12 13l3.5-3.5""/><circle cx=""12"" cy=""13"" r=""1.4""/>",
        ["app"] = @"<rect x=""3"" y=""3"" width=""18"" height=""18"" rx=""2""/><line x1=""3"" y1=""9"" x2=""21"" y2=""9""/><path d=""M8 21V9""/>",
        ["activity"] = @"<polyline points=""22 12 18 12 15 21 9 3 6 12 2 12""/>",
        ["grid"] = @"<rect x=""3"" y=""3"" width=""7"" height=""7"" rx=""1""/><rect x=""14"" y=""3"" width=""7"" height=""7"" rx=""1""/><rect x=""3"" y=""14"" width=""7"" height=""7"" rx=""1""/><rect x=""14"" y=""14"" width=""7"" height=""7"" rx=""1""/>",
        ["refresh"] = @"<path d=""M21 2v6h-6""/><path d=""M3 12a9 9 0 0 1 15-6.7L21 8""/><path d=""M3 22v-6h6""/><path d=""M21 12a9 9 0 0 1-15 6.7L3 16""/>",
        ["arrow"] = @"<line x1=""4"" y1=""12"" x2=""20"" y2=""12""/><polyline points=""15 6 21 12 15 18""/>",
        ["chevron"] = @"<polyline points=""6 9 12 15 18 9""/>",
        ["x"] = @"<line x1=""18"" y1=""6"" x2=""6"" y2=""18""/><line x1=""6"" y1=""6"" x2=""18"" y2=""18""/>",
        ["folder"] = @"<path d=""M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z""/>",
        ["cog"] = @"<circle cx=""12"" cy=""12"" r=""3""/><path d=""M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9c.14.35.42.64.78.78H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z""/>",
        ["more"] = @"<circle cx=""5"" cy=""12"" r=""1.2""/><circle cx=""12"" cy=""12"" r=""1.2""/><circle cx=""19"" cy=""12"" r=""1.2""/>",
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