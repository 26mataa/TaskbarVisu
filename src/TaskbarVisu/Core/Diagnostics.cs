using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarVisu.Core;

/// <summary>Consommation de l'application, mesurée comme le Gestionnaire des tâches : processeur et mémoire.</summary>
internal sealed class UsageMeter
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private long _lastTicks;

    /// <summary>Processeur en % de toute la machine depuis la mesure précédente, et mémoire de travail en Mo.</summary>
    public (double CpuPercent, double MemoryMb) Sample()
    {
        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        long now = Stopwatch.GetTimestamp();
        double percent = 0;
        if (_lastTicks != 0)
        {
            double wall = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (wall > 0) percent = (cpu - _lastCpu).TotalSeconds / wall / Environment.ProcessorCount * 100;
        }
        _lastCpu = cpu;
        _lastTicks = now;
        return (Math.Max(0, percent), _process.WorkingSet64 / (1024.0 * 1024.0));
    }
}

/// <summary>
/// Rapport pour signaler un problème : version, Windows, écrans, réglages et fin du journal. Le nom d'utilisateur
/// et le dossier personnel sont masqués ; le rapport ne quitte le PC que si l'utilisateur le colle lui-même.
/// </summary>
internal static class BugReport
{
    public static string Build(string usage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```");
        sb.AppendLine($"TaskbarVisu {UpdateChecker.CurrentText} ({(UpdateChecker.IsInstalled ? "installed" : Paths.Portable ? "portable" : "dev build")})");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}, UI {CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine($"CPU cores {Environment.ProcessorCount}, usage {usage}");
        try
        {
            var bars = Taskbar.TaskbarLocator.FindAll();
            for (int i = 0; i < bars.Count; i++) sb.AppendLine($"Taskbar {i + 1}: {bars[i].DisplayName(i)}");
        }
        catch (Exception ex) { sb.AppendLine($"Taskbar: {ex.GetType().Name}"); }
        sb.AppendLine("```");

        sb.AppendLine("<details><summary>settings.json</summary>");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(ReadTail(SettingsStore.FilePath, int.MaxValue));
        sb.AppendLine("```");
        sb.AppendLine("</details>");
        sb.AppendLine("<details><summary>log.txt</summary>");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(ReadTail(Log.FilePath, 80));
        sb.AppendLine("```");
        sb.AppendLine("</details>");
        return Anonymize(sb.ToString());
    }

    private static string ReadTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "(absent)";
            // Le journal peut être ouvert en écriture par l'application elle-même.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines))).TrimEnd();
        }
        catch (Exception ex) { return $"({ex.GetType().Name})"; }
    }

    /// <summary>Masque le dossier personnel et le nom d'utilisateur (chemins, noms de fichiers).</summary>
    private static string Anonymize(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(profile.Replace("\\", "\\\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        string user = Environment.UserName;
        if (user.Length >= 3) text = text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
