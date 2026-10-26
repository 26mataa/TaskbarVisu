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
        new("MiniMeters", PaletteChoice.Brasier, false, false, BackgroundChoice.None, ColorChoice.Default),
        new("CRT Phosphore", PaletteChoice.Phosphore, true, true, BackgroundChoice.None, ColorChoice.Palette),
        new("Synthwave", PaletteChoice.Synthwave, true, true, BackgroundChoice.None, ColorChoice.Palette),
        new("Vaporwave", PaletteChoice.Vaporwave, false, true, BackgroundChoice.None, ColorChoice.Palette),
        new("Chrome Y2K", PaletteChoice.Chrome, true, false, BackgroundChoice.None, ColorChoice.Default),
        new("Toxique", PaletteChoice.Toxic, true, true, BackgroundChoice.None, ColorChoice.Palette),
        new("RGB", PaletteChoice.RainbowAnimated, true, true, BackgroundChoice.None, ColorChoice.Default),
        // Afficheurs d'appareils.
        new("VFD", PaletteChoice.Vfd, true, true, BackgroundChoice.None, ColorChoice.Palette),
        new("LCD", PaletteChoice.Lcd, true, false, BackgroundChoice.Lcd, ColorChoice.Palette, ThemeChoice.Light),
        new("Nixie", PaletteChoice.Nixie, false, false, BackgroundChoice.None, ColorChoice.Palette),
    };

    /// <summary>Le préréglage qui correspond au style actuel, s'il y en a un.</summary>
    public static StylePreset? Current(AppSettings s) => All.FirstOrDefault(p =>
        s.Palette == p.Palette && s.PixelFont == p.PixelFont && s.Scanlines == p.Scanlines
        && s.Background == p.Background && s.ColorMode == p.Ink && s.Theme == p.Theme);

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
