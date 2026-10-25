namespace TaskbarVisu.Core;

/// <summary>Un style complet appliqué en un clic : palette, police, effets, fond et couleur des textes.</summary>
internal sealed record StylePreset(
    string Name,
    PaletteChoice Palette,
    bool PixelFont,
    bool Scanlines,
    BackgroundChoice Background,
    ColorChoice Ink);

internal static class StylePresets
{
    public static readonly StylePreset[] All =
    {
        new("Labo", PaletteChoice.Magma, true, true, BackgroundChoice.None, ColorChoice.Default),
        new("MiniMeters", PaletteChoice.Brasier, false, false, BackgroundChoice.Black, ColorChoice.Default),
        new("CRT Phosphore", PaletteChoice.Phosphore, true, true, BackgroundChoice.Dark, ColorChoice.Palette),
        new("Synthwave", PaletteChoice.Synthwave, true, true, BackgroundChoice.None, ColorChoice.Palette),
        new("Vaporwave", PaletteChoice.Vaporwave, false, true, BackgroundChoice.None, ColorChoice.Palette),
        new("Chrome Y2K", PaletteChoice.Chrome, true, false, BackgroundChoice.Dark, ColorChoice.Default),
        new("Toxique", PaletteChoice.Toxic, true, true, BackgroundChoice.None, ColorChoice.Palette),
        new("RGB", PaletteChoice.RainbowAnimated, true, true, BackgroundChoice.None, ColorChoice.Default),
    };

    /// <summary>Applique le style ; les couleurs propres à chaque module sont conservées.</summary>
    public static void Apply(AppSettings s, StylePreset preset)
    {
        s.Palette = preset.Palette;
        s.PixelFont = preset.PixelFont;
        s.Scanlines = preset.Scanlines;
        s.ShowScales = true;
        s.Background = preset.Background;
        s.ColorMode = preset.Ink;
    }
}
