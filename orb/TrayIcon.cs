using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZmdOrb;

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

    /// <summary>画一个小图标：底衬圆环 + 进度弧 + 中间数字。</summary>
    static Icon Render(string text, double pct)
    {
        int side = Math.Max(16, GetSystemMetrics(SM_CXSMICON));      // 125% 缩放时是 20
        using var bmp = new Bitmap(side, side, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            float pad = Math.Max(1.2f, side * 0.075f);
            float w = Math.Max(1.6f, side * 0.13f);                   // 环线宽
            var rect = new RectangleF(pad + w / 2, pad + w / 2,
                                      side - pad * 2 - w, side - pad * 2 - w);
            // 底衬（浅色）——深色任务栏上也看得清
            using (var track = new Pen(Color.FromArgb(230, 0xF2, 0xF1, 0xEC), w))
                g.DrawEllipse(track, rect);

            // 进度弧：从 12 点顺时针，配色跟球面一致
            double p = Math.Max(0, Math.Min(100, pct));
            var col = p < 70 ? Color.FromArgb(0xFF, 0xE2, 0x3D)
                    : p < 88 ? Color.FromArgb(0xEC, 0xB0, 0x63)
                             : Color.FromArgb(0xE8, 0x70, 0x3A);
            using (var arc = new Pen(col, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (p > 0.5)
                    g.DrawArc(arc, rect, -90, (float)(Math.Min(359.9, p * 3.6)));
            }

            // 中间数字：按位数自适应字号，保证 100% 也能塞进圆里
            float fontPx = side * (text.Length >= 3 ? 0.36f : text.Length == 2 ? 0.44f : 0.50f);
            using var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
            var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            using var brush = new SolidBrush(Color.FromArgb(0x3F, 0x3F, 0x3C));
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