using System.Windows;
using TaskbarVisu.Core;
using Forms = System.Windows.Forms;

namespace TaskbarVisu.UI;

/// <summary>Icône de la zone de notification : clic = réglages, menu = masquer / pause / mise à jour / quitter.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _settingsItem, _hideItem, _pauseItem, _updateItem, _quitItem;
    private readonly System.Drawing.Icon? _iconImage;
    private bool _paused, _hidden;
    private UpdateInfo? _update;

    public TrayIcon(App app)
    {
        _app = app;
        var menu = new Forms.ContextMenuStrip();
        _settingsItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => app.ShowSettings());
        _hideItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => app.ToggleVisuals());
        _pauseItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => app.TogglePause());
        _updateItem = new Forms.ToolStripMenuItem(string.Empty, null, async (_, _) => await app.InstallUpdateAsync()) { Visible = false };
        _quitItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => app.ExitApp());
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_hideItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_updateItem);
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
        _hideItem.Text = Loc.T(_hidden ? "Afficher les visualiseurs" : "Masquer les visualiseurs");
        _hideItem.ShortcutKeyDisplayString = _app.HotkeyText;
        _pauseItem.Text = Loc.T(_paused ? "Reprendre" : "Mettre en pause");
        _updateItem.Text = _update is null ? string.Empty : Loc.F("Installer la version {0}", _update.Version);
        _updateItem.Visible = _update is not null;
        _quitItem.Text = Loc.T("Quitter");
        _icon.Text = _paused ? Loc.T("TaskbarVisu (en pause)") : "TaskbarVisu";
    }

    public void UpdatePauseState(bool paused)
    {
        _paused = paused;
        Localize();
    }

    public void UpdateHiddenState(bool hidden)
    {
        _hidden = hidden;
        Localize();
    }

    public void UpdateUpdateState(UpdateInfo? update)
    {
        _update = update;
        Localize();
    }

    public void ShowHint(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Id;
        public Guid Guid;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Native.RECT rect);

    /// <summary>Emplacement de l'icône à l'écran (pixels physiques), ou null si Windows ne le donne pas.</summary>
    public Native.RECT? IconRect()
    {
        try
        {
            // NotifyIcon ne publie ni sa fenêtre ni son numéro d'icône : lus dans ses champs privés.
            var fields = typeof(Forms.NotifyIcon).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var window = fields.Select(f => f.GetValue(_icon)).OfType<Forms.NativeWindow>().FirstOrDefault();
            var id = fields.FirstOrDefault(f => f.Name is "_id" or "id" && (f.FieldType == typeof(uint) || f.FieldType == typeof(int)));
            if (window is null || window.Handle == IntPtr.Zero || id is null) return null;
            var identifier = new NotifyIconIdentifier
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NotifyIconIdentifier>(),
                Hwnd = window.Handle,
                Id = Convert.ToUInt32(id.GetValue(_icon)),
            };
            return Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 && !rect.IsEmpty ? rect : null;
        }
        catch (Exception ex)
        {
            Log.Error("Emplacement de l'icône de notification", ex);
            return null;
        }
    }

    public void Dispose()
    {
        Loc.Changed -= Localize;
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _iconImage?.Dispose();
    }
}
