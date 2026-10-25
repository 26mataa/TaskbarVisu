namespace TaskbarVisu.Core;

/// <summary>Journal minimaliste dans %APPDATA%\TaskbarVisu\log.txt (taille plafonnée).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string FilePath => Path.Combine(Paths.DataDir, "log.txt");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex is null ? message : $"{message} : {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Le journal ne doit jamais faire tomber l'application.
        }
    }
}

internal static class Paths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarVisu");
}
