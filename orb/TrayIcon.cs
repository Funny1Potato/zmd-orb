using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZmdOrb;

/// <summary>
/// 把我们的托盘图标"固定到任务栏"（Win11 默认把新图标塞进「^」溢出面板）。
/// 做法就是用户手动拖出来时系统自己写的那套：HKCU\Control Panel\NotifyIconSettings\&lt;hash&gt;
/// 下按 ExecutablePath 找到我们那条，写 IsPromoted=1。**只做一次**（UiSettings.TrayPromoted 记住），
/// 之后用户要是手动拖回溢出，我们不再抢。
/// 需要重启一次资源管理器（或重新登录）生效——这条由用户决定，程序不擅自重启 explorer。
/// </summary>
static class TrayPromote
{
    const string Root = @"Control Panel\NotifyIconSettings";

    /// <summary>返回 true 表示这次真的写了（下次就不写了）。</summary>
    public static bool TryPromote()
    {
        if (UiSettings.TrayPromoted) return false;
        try
        {
            string me = Autostart.ExePath;
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Root);
            if (root == null) return false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name, true);
                if (k?.GetValue("ExecutablePath") is not string path) continue;
                if (!string.Equals(path, me, StringComparison.OrdinalIgnoreCase)) continue;
                k.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                UiSettings.TrayPromoted = true;
                UiSettings.Save();
                Diag.Log($"托盘：已把图标设为常驻任务栏（NotifyIconSettings\\{name} IsPromoted=1）；"
                         + "重启一次资源管理器或重新登录生效");
                return true;
            }
            Diag.Log("托盘：还没找到我们的 NotifyIconSettings 条目（等系统建好后下次启动再试）");
        }
        catch (Exception e)
        {
            Diag.Log("托盘：设置常驻任务栏失败：" + e.Message);
        }
        return false;
    }
}

/// <summary>
/// 托盘模式（M4）：不占桌面，只在通知区显示一个**能看出占用率**的小圆环 + 数字。
/// 图标每次占用率（取整）变化时重画一次：16 逻辑像素（按 DPI 放大到 20/24/32）里画环 + 数字，
/// 数字用 GDI+ 的抗锯齿渲染，环用和球面同一套配色（&lt;70 亮黄 / &lt;88 琥珀 / 否则橙红）。
/// 右键菜单：打开面板 / 轻度整理 / 切到球模式 / 退出。
/// </summary>
sealed class TrayIcon : IDisposable
{
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    const int SM_CXSMICON = 49, SM_CYSMICON = 50;   // 通知区的小图标尺寸（已按 DPI 算好）

    readonly NotifyIcon _icon;
    readonly ContextMenuStrip _menu;
    readonly Action _openPanel, _clean, _toBall, _quit;
    int _lastPct = int.MinValue;
    IntPtr _handle = IntPtr.Zero;
    bool _disposed;

    public TrayIcon(Action openPanel, Action clean, Action toBall, Action quit)
    {
        _openPanel = openPanel;
        _clean = clean;
        _toBall = toBall;
        _quit = quit;

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Items.Add(Item("打开面板", () => _openPanel()));
        _menu.Items.Add(Item("轻度整理（免提权）", () => _clean()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("切到球模式", () => _toBall()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("退出", () => _quit()));

        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "终末地加速球",
            ContextMenuStrip = _menu,
            Icon = Render("—", 0),
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _openPanel();
        };
    }

    static ToolStripMenuItem Item(string text, Action onClick)
    {
        var it = new ToolStripMenuItem(text);
        it.Click += (_, _) => onClick();
        return it;
    }

    /// <summary>更新图标与提示文案；占用率取整没变就不重画（别每帧都建 HICON）。
    /// pct 传 NaN 表示没有实时数据（图标显示"——"）。</summary>
    public void Update(double pct, string tip)
    {
        if (_disposed) return;
        bool live = !double.IsNaN(pct);
        int p = live ? (int)Math.Round(pct) : -1;
        if (p == _lastPct) return;
        _lastPct = p;
        var old = _icon.Icon;
        _icon.Icon = Render(live ? p.ToString() : "—", live ? pct : 0);
        _icon.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
        if (old != null) old.Dispose();
    }

    /// <summary>气泡提示（托盘菜单里做完整理后回话）。</summary>
    public void Notify(string title, string text)
    {
        if (_disposed) return;
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text.Length > 250 ? text.Substring(0, 250) : text;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            Diag.Log("托盘气泡失败：" + ex.Message);
        }
    }

    /// <summary>任务栏现在是浅色还是深色——跟系统的 Windows 模式走（`SystemUsesLightTheme`，
    /// 任务栏/通知区就是它管的；读不到再看 App 模式）。数字颜色据此取深灰或白：
    /// 浅色任务栏上白字看不清，深色任务栏上深灰同理。</summary>
    static bool LightTaskbar()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("SystemUsesLightTheme") is int sys) return sys != 0;
            if (k?.GetValue("AppsUseLightTheme") is int app) return app != 0;
        }
        catch (Exception e)
        {
            Diag.Log("读系统主题失败：" + e.Message);
        }
        return true;      // 读不到就按浅色（Windows 默认）
    }

    /// <summary>画一个小图标：进度弧 + 中间数字（不放底衬轨道——用户要求去掉那圈浅色"描边"，
    /// 也不要阴影，弧是唯一元素）。</summary>
    static Icon Render(string text, double pct)
    {
        /* 尺寸上限是**系统的**：托盘图标就画在 SM_CXSMICON 那个槽里（本机 125% 缩放 = 20px）。
           实测按 32px 渲染也没用——系统会缩回 19~20px，所以想要"更大"只能在 20px 里做文章：
           边距 0（外径吃满 20）、线宽 21%（约 4.2px，内圈还剩 11.6px 给数字）。 */
        int side = Math.Max(16, GetSystemMetrics(SM_CXSMICON));      // 125% 缩放时是 20
        using var bmp = new Bitmap(side, side, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            // 环吃满整格、线再粗一档（用户连着说"太小"）：边距 0、线宽 21%
            float pad = 0f;
            float w = Math.Max(2.2f, side * 0.21f);                    // 环线宽（20px 里约 4.2px）
            var rect = new RectangleF(pad + w / 2, pad + w / 2,
                                      side - pad * 2 - w, side - pad * 2 - w);

            // 进度弧：从 12 点顺时针，配色跟球面一致
            double p = Math.Max(0, Math.Min(100, pct));
            var col = p < 70 ? Color.FromArgb(0xFF, 0xE2, 0x3D)
                    : p < 88 ? Color.FromArgb(0xEC, 0xB0, 0x63)
                             : Color.FromArgb(0xE8, 0x70, 0x3A);
            if (p > 0.5)
            {
                float sweep = (float)Math.Min(359.9, p * 3.6);
                using (var arc = new Pen(col, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, rect, -90, sweep);
            }
            // 中间数字：颜色跟系统主题走（浅色任务栏上用深灰、深色任务栏上用白），
            // 字号按位数自适应（环粗了，内圈只剩 ~11.6px）
            float fontPx = side * (text.Length >= 3 ? 0.34f : text.Length == 2 ? 0.42f : 0.48f);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            using var brush = new SolidBrush(LightTaskbar()
                ? Color.FromArgb(0x3F, 0x3F, 0x3C)      // 浅色模式：深灰
                : Color.White);                          // 深色模式：白
            g.DrawString(text, font, brush, new RectangleF(0, 0, side, side), fmt);
        }

        IntPtr h = bmp.GetHicon();                 // GetHicon 返回的句柄要自己 DestroyIcon
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        if (_handle != IntPtr.Zero) DestroyIcon(_handle);
    }
}