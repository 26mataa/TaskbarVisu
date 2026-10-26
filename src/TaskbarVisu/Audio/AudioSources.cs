using System.Diagnostics;
using NAudio.CoreAudioApi;
using TaskbarVisu.Core;

namespace TaskbarVisu.Audio;

internal sealed record AudioEndpoint(string Id, string Name);

/// <param name="ProcessName">Nom du processus sans « .exe » (ce qui est enregistré dans les réglages).</param>
internal sealed record AudioApp(string ProcessName, string DisplayName);

/// <summary>Listes proposées dans les réglages : sorties, entrées, et applications qui utilisent le son.</summary>
internal static class AudioSources
{
    public static List<AudioEndpoint> Outputs() => Endpoints(DataFlow.Render);

    public static List<AudioEndpoint> Inputs() => Endpoints(DataFlow.Capture);

    private static List<AudioEndpoint> Endpoints(DataFlow flow)
    {
        var list = new List<AudioEndpoint>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) list.Add(new AudioEndpoint(device.ID, device.FriendlyName));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Liste des périphériques audio", ex);
        }
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Applications qui ont une session audio (qui jouent ou ont joué du son) sur une sortie active.</summary>
    public static List<AudioApp> Apps()
    {
        var apps = new Dictionary<string, AudioApp>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.IsSystemSoundsSession || session.GetProcessID == 0) continue;
                        if (Describe((int)session.GetProcessID) is { } app) apps.TryAdd(app.ProcessName, app);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Liste des applications audio", ex);
        }
        return apps.Values.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static AudioApp? Describe(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            string name = process.ProcessName;
            string description = name;
            try
            {
                if (process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d) description = d;
            }
            catch { /* processus protégé ou d'une autre architecture : on garde son nom */ }
            return new AudioApp(name, string.Equals(description, name, StringComparison.OrdinalIgnoreCase) ? name : $"{description} ({name})");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Processus principal d'une application (le plus ancien de ce nom) : sa capture inclut ses processus enfants
    /// (navigateurs, Spotify… qui jouent le son depuis un sous-processus). Null si elle n'est pas lancée.
    /// </summary>
    public static int? FindProcess(string processName)
    {
        int? best = null;
        DateTime bestStart = DateTime.MaxValue;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var start = process.StartTime;
                    if (start < bestStart) (bestStart, best) = (start, process.Id);
                }
                catch
                {
                    best ??= process.Id;
                }
            }
        }
        return best;
    }

    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
