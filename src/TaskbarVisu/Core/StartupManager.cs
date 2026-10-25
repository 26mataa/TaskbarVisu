using Microsoft.Win32;

namespace TaskbarVisu.Core;

/// <summary>Démarrage avec Windows via HKCU\...\Run (aucun droit admin requis).</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TaskbarVisu";

    private static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static void Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            var current = key.GetValue(ValueName) as string;
            if (enabled)
            {
                if (!string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, Command);
            }
            else if (current is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mise à jour du démarrage automatique impossible", ex);
        }
    }
}
