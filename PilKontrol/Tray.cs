using System;
using System.Windows.Threading;
using PilKontrol.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PilKontrol;

/// <summary>Sistem tepsisi ikonu: üstünde pil yüzdesi yazar, rengi duruma göre değişir.</summary>
public sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly DispatcherTimer timer;
    readonly Forms.ToolStripMenuItem[] modeItems;
    IntPtr lastHandle;

    public Tray(Action show, Action quit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Paneli aç", null, (_, _) => show());

        var modeMenu = new Forms.ToolStripMenuItem("Mod");
        modeItems = new[]
        {
            Item("Otomatik", Engine.Auto), Item("Her zaman tasarruf", Engine.Always), Item("Kapalı", Engine.Off),
        };
        modeMenu.DropDownItems.AddRange(modeItems);
        menu.Items.Add(modeMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış (normal plana dön)", null, (_, _) => quit());
        menu.Opening += (_, _) => UpdateChecks();

        icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "PilKontrol" };
        icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) show(); };

        Update();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) => Update();
        timer.Start();
    }

    Forms.ToolStripMenuItem Item(string text, string mode) =>
        new(text, null, (_, _) => { App.Engine.SetMode(mode); UpdateChecks(); }) { Tag = mode };

    void UpdateChecks()
    {
        foreach (var i in modeItems) i.Checked = (string?)i.Tag == App.Engine.Mode;
    }

    void Update()
    {
        var b = Telemetry.GetBattery();
        var e = App.Engine;
        var color = e.ActiveName == PowerPlans.Ultra ? Drawing.Color.FromArgb(255, 176, 32)
            : e.Saving ? Drawing.Color.FromArgb(61, 220, 132)
            : b.OnAc ? Drawing.Color.FromArgb(201, 206, 214) : Drawing.Color.FromArgb(77, 163, 255);
        SetIcon(b.Percent, color);
        var text = $"PilKontrol · %{b.Percent} · {(e.Saving ? "tasarruf" : "normal")}";
        icon.Text = text.Length > 63 ? text[..63] : text;
    }

    void SetIcon(int? pct, Drawing.Color color)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new Drawing.Drawing2D.GraphicsPath();
            const int r = 8;
            var rect = new Drawing.Rectangle(1, 5, 30, 22);
            path.AddArc(rect.X, rect.Y, r, r, 180, 90);
            path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
            path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
            path.CloseFigure();
            using var fill = new Drawing.SolidBrush(color);
            g.FillPath(fill, path);
            var text = pct?.ToString() ?? "?";
            using var font = new Drawing.Font("Segoe UI", text.Length > 2 ? 11f : 15f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var fmt = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            g.DrawString(text, font, Drawing.Brushes.Black, new Drawing.RectangleF(0, 5, 32, 23), fmt);
        }
        var handle = bmp.GetHicon();
        icon.Icon = Drawing.Icon.FromHandle(handle);
        if (lastHandle != IntPtr.Zero) Native.DestroyIcon(lastHandle);
        lastHandle = handle;
    }

    public void Dispose()
    {
        timer.Stop();
        icon.Visible = false;
        icon.Dispose();
    }
}
