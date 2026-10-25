using TaskbarVisu.Native;

namespace TaskbarVisu.Taskbar;

internal sealed record TaskbarHandle(IntPtr Hwnd, IntPtr Monitor, string DeviceName, bool Primary, RECT MonitorRect)
{
    public string DisplayName(int index) =>
        Core.Loc.F("Écran {0} — {1} × {2}", index + 1, MonitorRect.Width, MonitorRect.Height)
        + (Primary ? Core.Loc.T(" (principal)") : string.Empty);
}

/// <summary>Trouve la barre principale (Shell_TrayWnd) et celles des écrans secondaires.</summary>
internal static class TaskbarLocator
{
    public const string PrimaryClass = "Shell_TrayWnd";
    public const string SecondaryClass = "Shell_SecondaryTrayWnd";

    public static List<TaskbarHandle> FindAll()
    {
        var result = new List<TaskbarHandle>();
        var main = Win32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, PrimaryClass, null);
        if (main != IntPtr.Zero) Add(result, main);

        IntPtr h = IntPtr.Zero;
        while ((h = Win32.FindWindowEx(IntPtr.Zero, h, SecondaryClass, null)) != IntPtr.Zero)
            Add(result, h);

        return result
            .OrderByDescending(t => t.Primary)
            .ThenBy(t => t.MonitorRect.Left)
            .ThenBy(t => t.MonitorRect.Top)
            .ToList();
    }

    private static void Add(List<TaskbarHandle> list, IntPtr hwnd)
    {
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (!Win32.TryGetMonitor(monitor, out var info)) return;
        bool primary = (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0;
        list.Add(new TaskbarHandle(hwnd, monitor, info.szDevice, primary, info.rcMonitor));
    }
}
