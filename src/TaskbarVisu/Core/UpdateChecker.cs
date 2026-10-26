using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TaskbarVisu.Core;

/// <param name="InstallerUrl">Installeur de la release (null s'il manque : on ouvre alors la page).</param>
/// <param name="InstallerSha256">Empreinte publiée par GitHub pour l'installeur, vérifiée après le téléchargement.</param>
internal sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? InstallerUrl, string? InstallerSha256);

/// <summary>
/// Mises à jour : dernière release publiée sur GitHub. Version installée → téléchargement de l'installeur et
/// installation silencieuse (l'application se relance ensuite) ; version portable → ouverture de la page.
/// </summary>
internal static class UpdateChecker
{
    public const string Repository = "26mataa/TaskbarVisu";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    public static Version Current { get; } = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);

    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";

    /// <summary>Installée par l'installeur (dossier Programs de l'utilisateur), et non lancée en portable.</summary>
    public static bool IsInstalled =>
        !Paths.Portable && AppContext.BaseDirectory.StartsWith(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>La release la plus récente si elle est plus récente que cette version, sinon null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var http = Client();
        string json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        string? installer = null, sha256 = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (!name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                installer = asset.GetProperty("browser_download_url").GetString();
                sha256 = asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d
                         && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
            }
        }
        var current = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
        var latest = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        return latest > current
            ? new UpdateInfo(latest, tag, root.GetProperty("html_url").GetString() ?? ReleasesPage, installer, sha256)
            : null;
    }

    /// <summary>Télécharge et lance l'installeur en mode silencieux. Renvoie false si on a seulement ouvert la page.</summary>
    public static async Task<bool> InstallAsync(UpdateInfo update)
    {
        if (!IsInstalled || update.InstallerUrl is null)
        {
            OpenPage(update.PageUrl);
            return false;
        }
        string file = Path.Combine(Path.GetTempPath(), $"TaskbarVisu-{update.Version}-setup.exe");
        using (var http = Client())
        using (var stream = await http.GetStreamAsync(update.InstallerUrl).ConfigureAwait(false))
        using (var output = File.Create(file))
        {
            await stream.CopyToAsync(output).ConfigureAwait(false);
        }
        if (update.InstallerSha256 is { } expected)
        {
            string actual;
            using (var check = File.OpenRead(file))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check).ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                throw new InvalidDataException($"Empreinte de l'installeur incorrecte ({actual}, attendu {expected})");
            }
        }
        Log.Info($"Mise à jour {update.Version} téléchargée, installation…");
        // /RELAUNCH=1 : l'installeur relance TaskbarVisu à la fin (pas lors d'une installation silencieuse par winget).
        Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });
        return true;
    }

    public static void OpenPage(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"TaskbarVisu/{CurrentText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
