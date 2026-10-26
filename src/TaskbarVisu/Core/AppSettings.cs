using System.Text.Json;
using System.Text.Json.Serialization;
using TaskbarVisu.Modules;

namespace TaskbarVisu.Core;

internal enum ThemeChoice { Auto, Dark, Light }

/// <summary>Couleur des tracés et textes (« encre »).</summary>
internal enum ColorChoice { Default, Accent, Custom, Palette }

/// <summary>Palette « fréquences » (grave → aigu). Les noms sont enregistrés dans settings.json.</summary>
internal enum PaletteChoice
{
    Magma, Brasier, Spectral, Phosphore, Glace, Accent, Custom,
    Rainbow, RainbowAnimated, Synthwave, Vaporwave, Toxic, Monochrome, Chrome,
    Vfd, Lcd, Nixie,
}

internal enum ZoneAlign { Start, Center, End }

/// <summary>Fond des modules ; « Lcd » = écran à cristaux liquides rétroéclairé (fond clair olive).</summary>
internal enum BackgroundChoice { None, Dark, Black, Lcd }

/// <summary>Ce que TaskbarVisu écoute.</summary>
internal enum AudioSourceKind
{
    /// <summary>Tout le son du PC (sortie par défaut, en loopback).</summary>
    SystemOutput,
    /// <summary>Une sortie audio précise (casque, enceintes…), en loopback.</summary>
    OutputDevice,
    /// <summary>Une seule application (FL Studio, Spotify…), sans les autres sons.</summary>
    Application,
    /// <summary>Une entrée : micro, entrée ligne, carte son.</summary>
    Input,
}

/// <summary>Icônes de la barre alignées à gauche : un seul grand espace libre.</summary>
internal enum SingleGapMode { Split, Center }

/// <summary>Raccourci clavier global pour masquer / afficher les visualiseurs.</summary>
internal enum HotkeyChoice { None, CtrlAltV, CtrlShiftV, CtrlAltH }

/// <summary>Couleurs d'un module : celles de l'apparence générale, une autre palette, ou une couleur unie.</summary>
internal enum ModuleColorMode { Global, Palette, Solid }

/// <summary>Réglages d'un module : activation, largeur préférée (en DIP), couleurs et options propres.</summary>
internal sealed class ModuleConfig
{
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Width { get; set; }
    public ModuleColorMode Colors { get; set; } = ModuleColorMode.Global;
    public PaletteChoice ColorPalette { get; set; } = PaletteChoice.Synthwave;
    public string ColorSolid { get; set; } = "#00E5FF";
    /// <summary>Dégradé propre au module quand sa palette est « Personnalisée » (null = celui de l'apparence générale).</summary>
    public List<ColorStop>? Gradient { get; set; }
    public Dictionary<string, double> Options { get; set; } = new();

    public double Get(string key)
    {
        if (Options.TryGetValue(key, out var v)) return v;
        return ModuleCatalog.Find(Id)?.Option(key)?.Default ?? 0;
    }
}

internal sealed class AppSettings
{
    public const int CurrentVersion = 5;
    public const int MaxModuleWidth = 400;

    public int Version { get; set; } = CurrentVersion;

    // Général
    public Language Language { get; set; } = Language.Auto;
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public HotkeyChoice Hotkey { get; set; } = HotkeyChoice.CtrlAltV;
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Version dont l'utilisateur a refusé la mise à jour (plus de rappel pour celle-là).</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>Petite animation « auto-test » des segments au lancement.</summary>
    public bool BootAnimation { get; set; } = true;

    // Source audio
    public AudioSourceKind AudioSource { get; set; } = AudioSourceKind.SystemOutput;
    /// <summary>Identifiant du périphérique choisi (sortie ou entrée), et son nom pour l'afficher s'il est débranché.</summary>
    public string? AudioDeviceId { get; set; }
    public string? AudioDeviceName { get; set; }
    /// <summary>Nom du processus écouté (sans « .exe »), par exemple « FL64 ».</summary>
    public string? AudioApp { get; set; }

    // Affichage
    public string? MonitorDevice { get; set; }
    /// <summary>Visualiseurs sur la barre des tâches de chaque écran.</summary>
    public bool AllMonitors { get; set; }
    public int Fps { get; set; } = 60;
    public bool HideWhenFullscreen { get; set; } = true;
    public double Opacity { get; set; } = 1.0;

    // Apparence
    public ThemeChoice Theme { get; set; } = ThemeChoice.Auto;
    public ColorChoice ColorMode { get; set; } = ColorChoice.Default;
    public string CustomColor { get; set; } = "#D4DCFF";
    public PaletteChoice Palette { get; set; } = PaletteChoice.Magma;
    /// <summary>Palette « Personnalisée » : dégradé de 2 à 8 couleurs.</summary>
    public List<ColorStop> Gradient { get; set; } = Gradients.Default();
    // Anciens réglages (v0.8) : deux couleurs grave / aigu, converties en dégradé par MigrateTo5.
    public string PaletteLow { get; set; } = "#3A0CA3";
    public string PaletteHigh { get; set; } = "#FFD60A";
    public BackgroundChoice Background { get; set; } = BackgroundChoice.None;
    public bool PixelFont { get; set; } = true;
    public bool ShowScales { get; set; } = true;
    public bool Scanlines { get; set; } = true;

    // Disposition
    public bool FillZones { get; set; }
    public SingleGapMode SingleGap { get; set; } = SingleGapMode.Split;
    public ZoneAlign LeftAlign { get; set; } = ZoneAlign.Start;
    public ZoneAlign RightAlign { get; set; } = ZoneAlign.End;
    public int EdgeFade { get; set; } = 8;
    public int Spacing { get; set; } = 12;
    public int VerticalPadding { get; set; } = 3;

    // Performances
    public int FftSize { get; set; } = 4096;
    public bool PowerSaving { get; set; } = true;
    /// <summary>Vérification périodique de secours ; la détection est surtout déclenchée par événements.</summary>
    public int DetectionIntervalMs { get; set; } = 3000;

    // L'ordre des listes est l'ordre d'affichage, de gauche à droite.
    public List<ModuleConfig> Left { get; set; } = new();
    public List<ModuleConfig> Right { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<ModuleConfig> AllModules => Left.Concat(Right);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings();
        s.ResetLayout();
        return s;
    }

    /// <summary>Remet la disposition par défaut (modules, ordre, côtés, tailles, alignements) sans toucher au reste.</summary>
    public void ResetLayout()
    {
        var previous = AllModules.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        Left = new();
        Right = new();
        foreach (var id in new[] { "levels", "spectrogram", "spectrum" })
            Left.Add(DefaultLayoutConfig(id, previous));
        foreach (var id in new[] { "imager", "tuner", "waveform" })
            Right.Add(DefaultLayoutConfig(id, previous));

        // Modules disponibles mais masqués par défaut : il suffit de les cocher.
        foreach (var (id, zone) in new[]
                 {
                     ("vu", Left), ("xyscope", Left),
                     ("loudness", Right), ("pianoroll", Right), ("rawdata", Right),
                 })
        {
            var hidden = DefaultLayoutConfig(id, previous);
            hidden.Enabled = false;
            zone.Add(hidden);
        }

        FillZones = false;
        SingleGap = SingleGapMode.Split;
        LeftAlign = ZoneAlign.Start;
        RightAlign = ZoneAlign.End;
        EdgeFade = 8;
        Spacing = 12;
        VerticalPadding = 3;
    }

    private static ModuleConfig DefaultLayoutConfig(string id, Dictionary<string, ModuleConfig> previous)
    {
        var cfg = ModuleCatalog.CreateConfig(id);
        // On conserve les options et couleurs propres au module, seule la disposition est réinitialisée.
        if (previous.TryGetValue(id, out var old))
        {
            cfg.Options = old.Options;
            cfg.Colors = old.Colors;
            cfg.ColorPalette = old.ColorPalette;
            cfg.ColorSolid = old.ColorSolid;
        }
        return cfg;
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>Garantit une config cohérente : chaque module connu présent une seule fois, valeurs bornées.</summary>
    public void Normalize()
    {
        if (Version < 2)
        {
            // v0.1 → v0.2 : modules compacts (125 px) et nouveau style ; les anciennes options n'ont plus le même sens.
            foreach (var m in AllModules) m.Options.Clear();
            ResetLayout();
            if (CustomColor == "#7FB2FF") CustomColor = "#D4DCFF";
        }
        if (Version < 3) MigrateTo3();
        if (Version < 4) MigrateTo4();
        if (Version < 5) MigrateTo5();
        Version = CurrentVersion;

        var seen = new HashSet<string>();
        Left = Clean(Left, seen);
        Right = Clean(Right, seen);
        foreach (var info in ModuleCatalog.All)
        {
            if (seen.Contains(info.Id)) continue;
            var cfg = ModuleCatalog.CreateConfig(info.Id);
            cfg.Enabled = false;
            (Left.Count <= Right.Count ? Left : Right).Add(cfg);
        }

        Fps = Math.Clamp(Fps, 15, 240);
        Opacity = Math.Clamp(Opacity, 0.2, 1.0);
        EdgeFade = Math.Clamp(EdgeFade, 0, 60);
        Spacing = Math.Clamp(Spacing, 0, 60);
        VerticalPadding = Math.Clamp(VerticalPadding, 0, 12);
        DetectionIntervalMs = Math.Clamp(DetectionIntervalMs, 500, 10000);
        if (FftSize is not (2048 or 4096 or 8192)) FftSize = 4096;
        Gradient = Gradients.Clean(Gradient) ?? Gradients.Default();
        foreach (var m in AllModules) m.Gradient = Gradients.Clean(m.Gradient);
    }

    /// <summary>v0.2 → v0.3 : les couleurs des modules passent par le système commun « Couleurs ».</summary>
    private void MigrateTo3()
    {
        foreach (var m in AllModules)
        {
            if (m.Id == "spectrogram" && m.Options.Remove("palette", out var p) && p >= 1)
            {
                m.Colors = ModuleColorMode.Palette;
                m.ColorPalette = (int)p switch
                {
                    1 => PaletteChoice.Magma,
                    2 => PaletteChoice.Brasier,
                    3 => PaletteChoice.Spectral,
                    4 => PaletteChoice.Phosphore,
                    _ => PaletteChoice.Glace,
                };
            }
            if (m.Id is "waveform" or "spectrum" && m.Options.Remove("color", out var c) && c >= 1)
            {
                m.Colors = ModuleColorMode.Solid;
                m.ColorSolid = CustomColor;
            }
            if (m.Id == "imager")
            {
                // Le vectorscope est toujours affiché ; la seconde vue et la corrélation ont de nouveaux choix.
                m.Options.Remove("scope");
                m.Options.Remove("correlation");
                if (m.Options.Remove("width", out var w) && w < 0.5) m.Options["view"] = 2;
            }
        }
    }

    /// <summary>v0.7 → v0.8 : l'accordeur perd son mode accord, remplacé par le module Piano roll (masqué, à droite).</summary>
    private void MigrateTo4()
    {
        foreach (var m in AllModules)
        {
            if (m.Id == "tuner" && m.Options.TryGetValue("mode", out var mode))
                m.Options["mode"] = mode >= 2 ? 1 : 0; // Note = 0, Accord = 1 → Note, Tonalité = 2 → 1
        }
        if (AllModules.All(m => m.Id != "pianoroll"))
        {
            var roll = ModuleCatalog.CreateConfig("pianoroll");
            roll.Enabled = false;
            Right.Add(roll);
        }
    }

    /// <summary>
    /// v0.8 → v0.9 : nouveau style « relief » en tête de l'analyseur de spectre (les autres styles se décalent) ;
    /// la palette personnalisée à deux couleurs devient un dégradé.
    /// </summary>
    private void MigrateTo5()
    {
        foreach (var m in AllModules)
        {
            if (m.Id == "spectrum" && m.Options.TryGetValue("style", out var style))
                m.Options["style"] = style + 1;
        }
        // Couleurs par défaut jamais modifiées : on garde le nouveau dégradé proposé.
        if (PaletteLow != "#3A0CA3" || PaletteHigh != "#FFD60A")
            Gradient = Gradients.FromLowHigh(PaletteLow, PaletteHigh);
    }

    private static List<ModuleConfig> Clean(List<ModuleConfig>? list, HashSet<string> seen)
    {
        var result = new List<ModuleConfig>();
        foreach (var cfg in list ?? new())
        {
            var info = ModuleCatalog.Find(cfg.Id);
            if (info is null || !seen.Add(cfg.Id)) continue;
            cfg.Options ??= new();
            if (cfg.Width <= 0) cfg.Width = info.DefaultWidth;
            cfg.Width = Math.Clamp(cfg.Width, info.MinWidth, MaxModuleWidth);
            foreach (var opt in info.Options)
            {
                if (cfg.Options.TryGetValue(opt.Key, out var v))
                    cfg.Options[opt.Key] = opt.Clamp(v);
            }
            result.Add(cfg);
        }
        return result;
    }
}
