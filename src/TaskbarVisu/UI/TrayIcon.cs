using System.Windows;
using TaskbarVisu.Core;
using Forms = System.Windows.Forms;

namespace TaskbarVisu.UI;

/// <summary>Icône de la zone de notification : clic = réglages, menu = pause / quitter.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _settingsItem, _pauseItem, _quitItem;
    private readonly System.Drawing.Icon? _iconImage;
    private bool _paused;

    public TrayIcon(App app)
    {
        var menu = new Forms.ContextMenuStrip();
        _settingsItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => app.ShowSettings());
        _pauseItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => app.TogglePause());
        _quitItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => app.ExitApp());
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_quitItem);

        _iconImage = LoadIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _iconImage ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) app.ShowSettings();
        };

        Localize();
        Loc.Changed += Localize;
    }

    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TaskbarVisu.ico"));
            if (info is null) return null;
            using var stream = info.Stream;
            return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch
        {
            return null;
        }
    }

    private void Localize()
    {
        _settingsItem.Text = Loc.T("Paramètres…");
        _pauseItem.Text = Loc.T(_paused ? "Reprendre" : "Mettre en pause");
        _quitItem.Text = Loc.T("Quitter");
        _icon.Text = _paused ? Loc.T("TaskbarVisu (en pause)") : "TaskbarVisu";
    }

    public void UpdatePauseState(bool paused)
    {
        _paused = paused;
        Localize();
    }

    public void ShowHint(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        Loc.Changed -= Localize;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _iconImage?.Dispose();
    }
}
