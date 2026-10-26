using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TaskbarVisu.Core;

/// <summary>
/// Partage d'un style : apparence générale (dégradé personnalisé compris) + couleurs propres à chaque module, en fichier (.tbvstyle, JSON lisible)
/// ou en code court à coller (« TBV1: » + JSON compressé en base64), par exemple sur Discord.
/// La disposition et les options des modules ne sont pas concernées.
/// </summary>
internal static class StyleCode
{
    private const string Prefix = "TBV1:";

    private sealed class ModuleStyle
    {
        public string Id { get; set; } = string.Empty;
        public ModuleColorMode Colors { get; set; }
        public PaletteChoice ColorPalette { get; set; }
        public string ColorSolid { get; set; } = string.Empty;
        public List<ColorStop>? Gradient { get; set; }
    }

    private sealed class Style
    {
        public PaletteChoice Palette { get; set; }
        public List<ColorStop>? Gradient { get; set; }
        public bool PixelFont { get; set; }
        public bool ShowScales { get; set; }
        public bool Scanlines { get; set; }
        public BackgroundChoice Background { get; set; }
        // Absents des styles partagés avant la 0.9.1.
        public ColorAnimation? Animation { get; set; }
        public double? AnimationSeconds { get; set; }
        public double Opacity { get; set; }
        public ThemeChoice Theme { get; set; }
        public ColorChoice ColorMode { get; set; }
        public string CustomColor { get; set; } = string.Empty;
        public List<ModuleStyle> Modules { get; set; } = new();
    }

    public static string ToJson(AppSettings s) => JsonSerializer.Serialize(new Style
    {
        Palette = s.Palette,
        Gradient = s.Gradient,
        PixelFont = s.PixelFont,
        ShowScales = s.ShowScales,
        Scanlines = s.Scanlines,
        Background = s.Background,
        Animation = s.Animation,
        AnimationSeconds = s.AnimationSeconds,
        Opacity = s.Opacity,
        Theme = s.Theme,
        ColorMode = s.ColorMode,
        CustomColor = s.CustomColor,
        Modules = s.AllModules.Where(m => m.Colors != ModuleColorMode.Global)
            .Select(m => new ModuleStyle { Id = m.Id, Colors = m.Colors, ColorPalette = m.ColorPalette, ColorSolid = m.ColorSolid, Gradient = m.Gradient })
            .ToList(),
    }, AppSettings.Json);

    public static string ToCode(AppSettings s)
    {
        using var doc = JsonDocument.Parse(ToJson(s));
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc.RootElement)); // JSON compact avant compression
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) deflate.Write(raw);
        return Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Vrai si le style actuel est exactement celui de ce code (ordre des modules indifférent).</summary>
    public static bool Matches(AppSettings s, string code)
    {
        var copy = s.Clone();
        return TryApply(code, copy) && ToJson(copy) == ToJson(s);
    }

    /// <summary>Applique un code (« TBV1:… ») ou le contenu JSON d'un fichier de style. False s'il n'est pas reconnu.</summary>
    public static bool TryApply(string text, AppSettings s)
    {
        try
        {
            text = text.Trim();
            string json;
            if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                string b64 = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                using var input = new DeflateStream(new MemoryStream(Convert.FromBase64String(b64)), CompressionMode.Decompress);
                using var reader = new StreamReader(input, Encoding.UTF8);
                json = reader.ReadToEnd();
            }
            else
            {
                json = text;
            }

            var style = JsonSerializer.Deserialize<Style>(json, AppSettings.Json);
            if (style is null) return false;
            s.Palette = style.Palette == PaletteChoice.Rainbow ? PaletteChoice.RainbowAnimated : style.Palette;
            if (Gradients.Clean(style.Gradient) is { } gradient) s.Gradient = gradient;
            s.PixelFont = style.PixelFont;
            s.ShowScales = style.ShowScales;
            s.Scanlines = style.Scanlines;
            s.Background = AppSettings.CleanBackground(style.Background);
            s.Animation = style.Animation ?? (style.Palette == PaletteChoice.RainbowAnimated ? ColorAnimation.Flow : ColorAnimation.Still);
            if (style.AnimationSeconds is { } seconds) s.AnimationSeconds = Math.Clamp(seconds, 2, 60);
            s.Opacity = Math.Clamp(style.Opacity <= 0 ? 1 : style.Opacity, 0.2, 1);
            s.Theme = style.Theme;
            s.ColorMode = style.ColorMode;
            s.CustomColor = style.CustomColor.Length > 0 ? style.CustomColor : s.CustomColor;
            foreach (var m in s.AllModules)
            {
                var shared = style.Modules.FirstOrDefault(x => x.Id == m.Id);
                m.Colors = shared?.Colors ?? ModuleColorMode.Global;
                if (shared is null) continue;
                m.ColorPalette = shared.ColorPalette;
                if (shared.ColorSolid.Length > 0) m.ColorSolid = shared.ColorSolid;
                m.Gradient = Gradients.Clean(shared.Gradient);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Style non reconnu : {ex.Message}");
            return false;
        }
    }
}
