using Microsoft.Win32;
using SkiaSharp;
using TaskbarVisu.Core;

namespace TaskbarVisu.Rendering;

/// <summary>
/// Couleurs du style « labo » : une encre (tracés, textes), un accent (aiguille, repères),
/// et une palette de fréquences grave → aigu. Un module peut avoir son propre thème
/// (autre palette ou couleur unie) dérivé du thème général.
/// </summary>
internal sealed class Theme
{
    private static int _versionCounter;

    /// <summary>Unique pour chaque thème construit : sert de clé aux caches de rendu.</summary>
    public int Version { get; } = Interlocked.Increment(ref _versionCounter);
    public bool Light { get; private init; }

    /// <summary>Couleur des tracés et des textes.</summary>
    public SKColor Ink { get; private set; }
    public SKColor InkDim { get; private set; }
    public SKColor Grid { get; private set; }
    public SKColor GridFaint { get; private set; }
    public SKColor Warn { get; private init; }
    public SKColor Hot { get; private init; }

    /// <summary>Case de lecture « inversée » (fond encre, texte sombre), façon afficheur.</summary>
    public SKColor ReadoutBack { get; private set; }
    public SKColor ReadoutText { get; private set; }

    /// <summary>Case de lecture discrète posée sur un tracé.</summary>
    public SKColor OverlayBack { get; private init; }

    /// <summary>Palette qui tourne dans le temps (arc-en-ciel animé).</summary>
    public bool Animated { get; private set; }

    /// <summary>Décalage de l'animation, de 0 à 1 (mis à jour à chaque image par le rendu).</summary>
    public float Phase { get; set; }

    /// <summary>Façon dont la palette animée bouge.</summary>
    public ColorAnimation Effect { get; private set; }

    private bool _cyclic;

    private (float T, SKColor C)[] _palette = Array.Empty<(float, SKColor)>();
    private readonly SKColor[] _spectral = new SKColor[256];
    private AppSettings _settings = null!;
    private List<ColorStop>? _gradient;

    /// <summary>Début de la partie des palettes intégrées utilisée pour les données (voir <see cref="Spectral"/>).</summary>
    private const float VisibleStart = 0.22f;

    private static readonly SKColor DefaultInkDark = new(0xD4, 0xDC, 0xFF);
    private static readonly SKColor DefaultInkLight = new(0x1D, 0x24, 0x33);

    public static Theme Build(AppSettings s) => Build(s, null, null);

    /// <param name="palette">Palette propre à un module (sinon celle des réglages).</param>
    /// <param name="solid">Couleur unie propre à un module : palette monochrome de cette teinte.</param>
    /// <param name="gradient">Dégradé propre à un module, quand sa palette est « Personnalisée ».</param>
    public static Theme Build(AppSettings s, PaletteChoice? palette, SKColor? solid, List<ColorStop>? gradient = null)
    {
        bool light = s.Theme switch
        {
            ThemeChoice.Light => true,
            ThemeChoice.Dark => false,
            _ => SystemUsesLightTheme(),
        };

        var theme = new Theme
        {
            Light = light,
            Warn = light ? new SKColor(0xC7, 0x78, 0x00) : new SKColor(0xFF, 0xB0, 0x20),
            Hot = light ? new SKColor(0xD9, 0x20, 0x20) : new SKColor(0xFF, 0x3B, 0x30),
            OverlayBack = light ? new SKColor(0xF4, 0xF4, 0xF4, 0xEC) : new SKColor(0x05, 0x05, 0x08, 0xE8),
        };
        theme._settings = s;
        theme._gradient = gradient;

        // L'encre « par défaut » sert aussi de base aux palettes monochromes.
        var baseInk = s.ColorMode switch
        {
            ColorChoice.Accent => AccentColor(light) ?? (light ? DefaultInkLight : DefaultInkDark),
            ColorChoice.Custom => Parse(s.CustomColor) ?? (light ? DefaultInkLight : DefaultInkDark),
            _ => light ? DefaultInkLight : DefaultInkDark,
        };
        theme.SetInk(baseInk);

        var choice = palette ?? s.Palette;
        // La palette RGB est un cercle chromatique (la fin rejoint le début) : elle passe toujours par le chemin animé.
        theme._cyclic = choice == PaletteChoice.RainbowAnimated;
        theme.Effect = s.Animation;
        theme.Animated = solid is null && (theme._cyclic || s.Animation != ColorAnimation.Still);
        theme._palette = solid is { } c ? MonoStops(c) : theme.Stops(choice);
        // Le bas des palettes intégrées (très sombre) ne sert qu'aux fonds du spectrogramme ;
        // un dégradé personnalisé est visible en entier, comme dans son éditeur.
        float start = solid is null && choice == PaletteChoice.Custom ? 0f : VisibleStart;
        for (int i = 0; i < 256; i++)
        {
            float t = theme._cyclic && solid is null ? i / 256f : start + (1 - start) * i / 255f;
            theme._spectral[i] = Sample(theme._palette, t);
        }

        // Encre teintée par la palette (ou par la couleur unie du module).
        if (s.ColorMode == ColorChoice.Palette || solid is not null)
            theme.SetInk(Mix(Sample(theme._palette, 0.9f), light ? SKColors.Black : SKColors.White, light ? 0.35f : 0.4f));

        // La case de lecture prend la couleur haute de la palette : cohérente avec les données.
        theme.ReadoutBack = theme.Animated ? theme.Ink : theme.Spectral(0.92f);
        var rb = theme.ReadoutBack;
        float luminance = (0.2126f * rb.Red + 0.7152f * rb.Green + 0.0722f * rb.Blue) / 255f;
        theme.ReadoutText = luminance > 0.5f ? new SKColor(0x07, 0x08, 0x0C) : SKColors.White;
        return theme;
    }

    private void SetInk(SKColor ink)
    {
        Ink = ink;
        InkDim = ink.WithAlpha(0x99);
        Grid = ink.WithAlpha(0x2C);
        GridFaint = ink.WithAlpha(0x16);
        ReadoutBack = ink.WithAlpha(0xF0);
    }

    /// <summary>Couleur de la palette pour une position grave (0) → aigu (1), toujours bien visible.</summary>
    public SKColor Spectral(float t)
    {
        t = Math.Clamp(t, 0, 1);
        if (Animated) return AnimatedColor(t);
        return _spectral[(int)(t * 255)];
    }

    /// <summary>
    /// Couleur animée. La palette RGB tourne en boucle ; les autres palettes (grave sombre → aigu clair)
    /// font l'aller-retour, pour bouger sans saut de couleur.
    /// </summary>
    private SKColor AnimatedColor(float t)
    {
        float span = _cyclic ? 0.85f : 1f, travel = _cyclic ? 1f : 2f;
        float u = Effect switch
        {
            ColorAnimation.FlowReverse => t * span - Phase * travel,
            ColorAnimation.Cycle => t * span * 0.12f + Phase * travel,
            ColorAnimation.Bounce => t * span + 0.3f * MathF.Sin(Phase * MathF.Tau),
            ColorAnimation.Pulse or ColorAnimation.Still => t * span,
            _ => t * span + Phase * travel,
        };
        float position;
        if (_cyclic)
        {
            position = u - MathF.Floor(u);
        }
        else
        {
            float m = u - 2 * MathF.Floor(u / 2);
            position = m > 1 ? 2 - m : m;
        }
        var c = _spectral[(int)(Math.Clamp(position, 0, 1) * 255.999f)];
        if (Effect != ColorAnimation.Pulse) return c;
        // Respiration : de 35 % à 100 % de luminosité.
        float k = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Cos(Phase * MathF.Tau));
        return Scale(c, k);
    }

    /// <summary>
    /// Table de 256 couleurs BGRA prémultipliées pour une échelle d'intensité (spectrogramme, panorama) ;
    /// les faibles intensités sont transparentes.
    /// </summary>
    public uint[] BuildLut()
    {
        var lut = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            float t = i / 255f;
            var c = Animated ? Spectral(t) : Sample(_palette, t);
            float a = t < 0.02f ? 0 : SmoothStep(0.02f, 0.45f, t);
            uint alpha = (uint)(a * 255 + 0.5f);
            uint r = (uint)(c.Red * a + 0.5f), g = (uint)(c.Green * a + 0.5f), b = (uint)(c.Blue * a + 0.5f);
            lut[i] = alpha << 24 | r << 16 | g << 8 | b;
        }
        return lut;
    }

    private (float, SKColor)[] Stops(PaletteChoice palette) => palette switch
    {
        PaletteChoice.Brasier => new[]
        {
            (0f, C(0x2A, 0x05, 0x05)), (0.3f, C(0x8E, 0x10, 0x10)), (0.55f, C(0xE3, 0x34, 0x1A)),
            (0.75f, C(0xFF, 0x7A, 0x1A)), (0.9f, C(0xFF, 0xC1, 0x4D)), (1f, C(0xFF, 0xF4, 0xD1)),
        },
        PaletteChoice.Spectral => new[]
        {
            (0f, C(0x30, 0x12, 0x3B)), (0.15f, C(0x46, 0x62, 0xD7)), (0.3f, C(0x36, 0xAA, 0xF9)),
            (0.45f, C(0x1A, 0xE4, 0xB6)), (0.6f, C(0x72, 0xFE, 0x5E)), (0.75f, C(0xC8, 0xEF, 0x34)),
            (0.87f, C(0xFA, 0xBA, 0x39)), (1f, C(0xF0, 0x4E, 0x1A)),
        },
        PaletteChoice.Phosphore => new[]
        {
            (0f, C(0x00, 0x1A, 0x08)), (0.4f, C(0x00, 0x87, 0x2B)), (0.75f, C(0x39, 0xFF, 0x14)), (1f, C(0xD9, 0xFF, 0xD0)),
        },
        PaletteChoice.Glace => new[]
        {
            (0f, C(0x0B, 0x1F, 0x4D)), (0.35f, C(0x1D, 0x4E, 0xD8)), (0.65f, C(0x38, 0xBD, 0xF8)),
            (0.9f, C(0xCF, 0xF4, 0xFF)), (1f, C(0xFF, 0xFF, 0xFF)),
        },
        PaletteChoice.Rainbow => new[]
        {
            (0f, C(0x7A, 0x00, 0x1E)), (0.15f, C(0xFF, 0x17, 0x44)), (0.3f, C(0xFF, 0x91, 0x00)), (0.45f, C(0xFF, 0xEA, 0x00)),
            (0.6f, C(0x00, 0xE6, 0x76)), (0.75f, C(0x00, 0xE5, 0xFF)), (0.88f, C(0x29, 0x79, 0xFF)), (1f, C(0xD5, 0x00, 0xF9)),
        },
        PaletteChoice.RainbowAnimated => new[]
        {
            // Cercle chromatique complet : la fin rejoint le début pour une rotation sans saut.
            (0f, C(0xFF, 0x17, 0x44)), (1 / 6f, C(0xFF, 0xB3, 0x00)), (2 / 6f, C(0xC6, 0xFF, 0x00)), (3 / 6f, C(0x00, 0xE6, 0x76)),
            (4 / 6f, C(0x00, 0xB0, 0xFF)), (5 / 6f, C(0xAA, 0x00, 0xFF)), (1f, C(0xFF, 0x17, 0x44)),
        },
        PaletteChoice.Synthwave => new[]
        {
            (0f, C(0x1A, 0x0B, 0x3D)), (0.3f, C(0x3D, 0x1A, 0x78)), (0.55f, C(0xB5, 0x17, 0x9E)),
            (0.75f, C(0xF7, 0x25, 0x85)), (0.9f, C(0x4C, 0xC9, 0xF0)), (1f, C(0xCF, 0xFA, 0xFE)),
        },
        PaletteChoice.Vaporwave => new[]
        {
            (0f, C(0x1B, 0x10, 0x35)), (0.3f, C(0x5B, 0x3E, 0x96)), (0.5f, C(0xFF, 0x71, 0xCE)),
            (0.7f, C(0xB9, 0x67, 0xFF)), (0.85f, C(0x01, 0xCD, 0xFE)), (1f, C(0x05, 0xFF, 0xA1)),
        },
        PaletteChoice.Toxic => new[]
        {
            (0f, C(0x0B, 0x1A, 0x00)), (0.35f, C(0x2E, 0x6B, 0x00)), (0.6f, C(0x7C, 0xFF, 0x00)),
            (0.85f, C(0xE5, 0xFF, 0x00)), (1f, C(0xFF, 0xFF, 0xB0)),
        },
        PaletteChoice.Chrome => new[]
        {
            (0f, C(0x1C, 0x22, 0x30)), (0.3f, C(0x4A, 0x55, 0x68)), (0.55f, C(0xA0, 0xAE, 0xC0)),
            (0.75f, C(0xE2, 0xE8, 0xF0)), (0.9f, C(0x7D, 0xD3, 0xFC)), (1f, C(0xF0, 0xF9, 0xFF)),
        },
        // Afficheurs d'appareils réels (sans marque) : tube fluorescent VFD, LCD rétroéclairé, tubes Nixie.
        PaletteChoice.Vfd => new[]
        {
            (0f, C(0x00, 0x1A, 0x17)), (0.35f, C(0x00, 0x59, 0x4F)), (0.7f, C(0x00, 0xE5, 0xC0)),
            (0.9f, C(0x7D, 0xFF, 0xE5)), (1f, C(0xE0, 0xFF, 0xF8)),
        },
        PaletteChoice.Lcd => new[]
        {
            // Sur fond olive clair : plus le signal est fort, plus le segment est sombre.
            (0f, C(0x80, 0x8C, 0x66)), (0.45f, C(0x4A, 0x55, 0x34)), (0.8f, C(0x22, 0x2B, 0x17)), (1f, C(0x0D, 0x12, 0x0A)),
        },
        PaletteChoice.Nixie => new[]
        {
            (0f, C(0x1A, 0x05, 0x00)), (0.3f, C(0x5C, 0x14, 0x00)), (0.6f, C(0xE0, 0x4A, 0x00)),
            (0.85f, C(0xFF, 0x8A, 0x2A)), (1f, C(0xFF, 0xE0, 0xB0)),
        },
        PaletteChoice.Monochrome => MonoStops(Ink),
        PaletteChoice.Accent => MonoStops(AccentColor(false) ?? DefaultInkDark),
        PaletteChoice.Custom => CustomStops(),
        _ => new[]
        {
            (0f, C(0x2C, 0x11, 0x5F)), (0.3f, C(0x72, 0x1F, 0x81)), (0.55f, C(0xB7, 0x37, 0x79)),
            (0.75f, C(0xF1, 0x60, 0x5D)), (0.9f, C(0xFE, 0xAF, 0x77)), (1f, C(0xFC, 0xFD, 0xBF)),
        },
    };

    private static (float, SKColor)[] MonoStops(SKColor c) =>
        new[] { (0f, Scale(c, 0.2f)), (0.45f, Scale(c, 0.65f)), (0.8f, c), (1f, Mix(c, SKColors.White, 0.6f)) };

    private (float, SKColor)[] CustomStops()
    {
        var stops = (Gradients.Clean(_gradient) ?? Gradients.Clean(_settings.Gradient) ?? Gradients.Default())
            .Select(s => ((float)s.Pos, Parse(s.Color) ?? SKColors.White))
            .ToArray();
        // Sample attend des repères de 0 à 1 : on prolonge les couleurs des extrémités.
        if (stops[0].Item1 > 0) stops = stops.Prepend((0f, stops[0].Item2)).ToArray();
        if (stops[^1].Item1 < 1) stops = stops.Append((1f, stops[^1].Item2)).ToArray();
        return stops;
    }

    /// <summary>
    /// Une palette sous forme de dégradé personnalisé (« partir d'une palette existante ») :
    /// seule la partie visible des palettes intégrées est reprise, en 8 couleurs au plus.
    /// </summary>
    public static List<ColorStop> ToGradient(AppSettings s, PaletteChoice palette)
    {
        var theme = Build(s, palette, null);
        if (palette == PaletteChoice.Custom) return Gradients.Copy(s.Gradient);
        float start = palette == PaletteChoice.RainbowAnimated ? 0 : VisibleStart;
        var result = new List<ColorStop> { new(0, Hex(Sample(theme._palette, start))) };
        foreach (var (t, c) in theme._palette)
            if (t > start + 0.02f) result.Add(new ColorStop(Math.Round((t - start) / (1 - start), 3), Hex(c)));
        while (result.Count > Gradients.MaxStops) result.RemoveAt(result.Count / 2);
        return result;
    }

    private static string Hex(SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    private static SKColor Sample((float T, SKColor C)[] stops, float t)
    {
        int s = 0;
        while (s < stops.Length - 2 && t > stops[s + 1].T) s++;
        var (t0, c0) = stops[s];
        var (t1, c1) = stops[s + 1];
        return Mix(c0, c1, Math.Clamp((t - t0) / Math.Max(1e-6f, t1 - t0), 0, 1));
    }

    /// <summary>La barre des tâches suit « SystemUsesLightTheme » (et non le thème des applications).</summary>
    public static bool SystemUsesLightTheme() => ReadFlag("SystemUsesLightTheme");

    /// <summary>Thème des applications (fenêtre de réglages).</summary>
    public static bool AppsUseLightTheme() => ReadFlag("AppsUseLightTheme");

    private static bool ReadFlag(string name)
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                name, 0) is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Variante de la couleur d'accentuation adaptée au fond (claire sur fond sombre et inversement).</summary>
    public static SKColor? AccentColor(bool lightBackground)
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                    "AccentPalette", null) is byte[] { Length: >= 32 } pal)
            {
                // Variante « claire 1 » sur fond sombre, « foncée 1 » sur fond clair : bien saturées.
                int i = (lightBackground ? 4 : 2) * 4;
                return new SKColor(pal[i], pal[i + 1], pal[i + 2]);
            }
        }
        catch { }
        return null;
    }

    public static SKColor? Parse(string? hex) =>
        hex is not null && SKColor.TryParse(hex, out var c) ? c.WithAlpha(255) : null;

    public static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    private static SKColor C(byte r, byte g, byte b) => new(r, g, b);

    private static SKColor Scale(SKColor c, float k) => new((byte)(c.Red * k), (byte)(c.Green * k), (byte)(c.Blue * k));

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
