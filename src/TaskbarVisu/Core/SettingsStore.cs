using System.Text.Json;

namespace TaskbarVisu.Core;

/// <summary>Chargement / sauvegarde (différée) de settings.json.</summary>
internal sealed class SettingsStore : IDisposable
{
    private readonly Timer _saveTimer;
    private readonly object _gate = new();
    private string? _pendingJson;

    public AppSettings Current { get; private set; }
    public bool IsFirstRun { get; }

    /// <summary>Fichier de réglages ; remplaçable (diagnostic) par la variable d'environnement TASKBARVISU_SETTINGS.</summary>
    public static string FilePath =>
        Environment.GetEnvironmentVariable("TASKBARVISU_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Paths.DataDir, "settings.json");

    private SettingsStore(AppSettings settings, bool firstRun)
    {
        Current = settings;
        IsFirstRun = firstRun;
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static SettingsStore Load()
    {
        AppSettings? settings = null;
        bool firstRun = !File.Exists(FilePath);
        if (!firstRun)
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), AppSettings.Json);
            }
            catch (Exception ex)
            {
                Log.Error("Lecture des réglages impossible, réglages par défaut utilisés", ex);
                try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { }
            }
        }

        settings ??= AppSettings.CreateDefault();
        settings.Normalize();
        var store = new SettingsStore(settings, firstRun);
        if (firstRun)
        {
            store.SaveSoon();
            store.Flush();
        }
        return store;
    }

    public void Replace(AppSettings settings)
    {
        settings.Normalize();
        Current = settings;
        SaveSoon();
    }

    /// <summary>
    /// À appeler depuis le thread qui modifie <see cref="Current"/> (l'interface) :
    /// la sérialisation est faite tout de suite, l'écriture disque est différée.
    /// </summary>
    public void SaveSoon()
    {
        var json = JsonSerializer.Serialize(Current, AppSettings.Json);
        lock (_gate) _pendingJson = json;
        _saveTimer.Change(400, Timeout.Infinite);
    }

    public void Flush()
    {
        string? json;
        lock (_gate)
        {
            json = _pendingJson;
            _pendingJson = null;
        }
        if (json is null) return;

        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Sauvegarde des réglages impossible", ex);
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }
}
