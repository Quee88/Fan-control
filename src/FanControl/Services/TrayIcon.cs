using System.Windows;
using WinForms = System.Windows.Forms;

namespace FanControl.Services;

/// <summary>Ikona v oznamovací oblasti (vedle hodin) s menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public TrayIcon(Action open, Action resetAll, Action exit)
    {
        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Otevřít Fan Control", null, (_, _) => open())
        {
            Font = new System.Drawing.Font(WinForms.Control.DefaultFont, System.Drawing.FontStyle.Bold),
        };
        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Vrátit všechny větráky na automatiku", null, (_, _) => resetAll()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Ukončit", null, (_, _) => exit()));

        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/fan.ico"))!.Stream;
        _icon = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize),
            Text = "Fan Control",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) open();
        };
    }

    public void SetText(string text)
    {
        try { _icon.Text = text.Length > 127 ? text[..127] : text; }
        catch { /* příliš dlouhý text na starších systémech */ }
    }

    public void ShowHint(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
