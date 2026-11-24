using System.Windows;
using WinForms = System.Windows.Forms;

namespace FanControl.Services;

/// <summary>Ikona v oznamovací oblasti (vedle hodin) s menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripSeparator _profilesEnd = new();
    private readonly List<WinForms.ToolStripItem> _profileItems = [];

    public TrayIcon(Action open, Action resetAll, Action exit)
    {
        var menu = _menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Otevřít Fan Control", null, (_, _) => open())
        {
            Font = new System.Drawing.Font(WinForms.Control.DefaultFont, System.Drawing.FontStyle.Bold),
        };
        menu.Items.Add(openItem);
        menu.Items.Add(_profilesEnd);
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

    /// <summary>Profily jako položky menu se zaškrtnutím aktivního – přepnutí jedním kliknutím.</summary>
    public void SetProfiles(IReadOnlyList<(string Id, string Name, bool IsActive)> profiles, Action<string> select)
    {
        foreach (var item in _profileItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _profileItems.Clear();
        if (profiles.Count == 0) return;

        var index = _menu.Items.IndexOf(_profilesEnd);
        var items = new List<WinForms.ToolStripItem> { new WinForms.ToolStripSeparator() };
        items.Add(new WinForms.ToolStripLabel("Profil") { ForeColor = System.Drawing.SystemColors.GrayText });
        foreach (var (id, name, isActive) in profiles)
        {
            // „&“ by WinForms bralo jako klávesovou zkratku.
            items.Add(new WinForms.ToolStripMenuItem(name.Replace("&", "&&"), null, (_, _) => select(id)) { Checked = isActive });
        }
        foreach (var item in items)
        {
            _menu.Items.Insert(index++, item);
            _profileItems.Add(item);
        }
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
