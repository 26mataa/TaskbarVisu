using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TaskbarVisu.Core;

/// <summary>Raccourci clavier global (RegisterHotKey) reçu par une fenêtre message invisible du thread d'interface.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0x5456;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private bool _registered;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("TaskbarVisu.Hotkey")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE : fenêtre de messages, jamais affichée
        });
        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    /// <summary>Texte affiché pour chaque choix (dans l'ordre de <see cref="HotkeyChoice"/>).</summary>
    public static readonly string[] Names = { "Aucun", "Ctrl + Alt + V", "Ctrl + Maj + V", "Ctrl + Alt + H" };

    /// <summary>Enregistre le raccourci ; false s'il est déjà pris par une autre application.</summary>
    public bool Set(HotkeyChoice choice)
    {
        if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        if (choice == HotkeyChoice.None) return true;

        var (modifiers, key) = choice switch
        {
            HotkeyChoice.CtrlShiftV => (ModControl | ModShift, 0x56u),
            HotkeyChoice.CtrlAltH => (ModControl | ModAlt, 0x48u),
            _ => (ModControl | ModAlt, 0x56u),
        };
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers | ModNoRepeat, key);
        if (!_registered) Log.Info($"Raccourci {Names[(int)choice]} déjà utilisé par une autre application");
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
