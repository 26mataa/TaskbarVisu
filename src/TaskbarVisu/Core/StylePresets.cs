namespace TaskbarVisu.Core;

/// <summary>Un style complet appliqué en un clic : palette, police, effets, fond, couleur des textes et thème.</summary>
internal sealed record StylePreset(
    string Name,
    PaletteChoice Palette,
    bool PixelFont,
    bool Scanlines,
    BackgroundChoice Background,
    ColorChoice Ink,
    ThemeChoice Theme = ThemeChoice.Auto);

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
        // Afficheurs d'appareils.
        new("VFD", PaletteChoice.Vfd, true, true, BackgroundChoice.Black, ColorChoice.Palette),
        new("LCD", PaletteChoice.Lcd, true, false, BackgroundChoice.Lcd, ColorChoice.Palette, ThemeChoice.Light),
        new("Nixie", PaletteChoice.Nixie, false, false, BackgroundChoice.Black, ColorChoice.Palette),
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
        s.Theme = preset.Theme;
    }
}
