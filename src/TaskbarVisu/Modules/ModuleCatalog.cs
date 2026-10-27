using TaskbarVisu.Core;

namespace TaskbarVisu.Modules;

internal enum OptionKind { Toggle, Slider, Choice, Color }

/// <summary>
/// Description d'une option de module ; l'interface de réglages est générée à partir de ça.
/// Les options « avancées » sont rangées dans « Plus d'options » ; <see cref="Help"/> s'affiche en petit dessous.
/// </summary>
internal sealed record OptionDef(
    string Key,
    string Label,
    OptionKind Kind,
    double Default,
    double Min = 0,
    double Max = 1,
    double Step = 1,
    string? Unit = null,
    string[]? Choices = null,
    bool Advanced = false,
    string? Help = null)
{
    public double Clamp(double v) => Kind switch
    {
        OptionKind.Toggle => v >= 0.5 ? 1 : 0,
        OptionKind.Choice => Math.Clamp(Math.Round(v), 0, (Choices?.Length ?? 1) - 1),
        OptionKind.Color => Math.Clamp(Math.Round(v), 0, 0xFFFFFF),
        _ => Math.Clamp(v, Min, Max),
    };
}

internal sealed record ModuleInfo(
    string Id,
    string Name,
    string Description,
    int DefaultWidth,
    int MinWidth,
    OptionDef[] Options,
    Func<VisualizerModule> Create)
{
    /// <summary>Option par clé. Appelé à chaque image : une boucle, sans lambda ni allocation.</summary>
    public OptionDef? Option(string key)
    {
        foreach (var option in Options)
            if (option.Key == key) return option;
        return null;
    }
}

/// <summary>Famille de modules, pour ranger la galerie : nom, phrase d'explication et modules (ids) dans l'ordre.</summary>
internal sealed record ModuleCategory(string Name, string Blurb, string[] Ids);

internal static class ModuleCatalog
{
    /// <summary>Largeur par défaut d'un module (DIP, avant mise à l'échelle DPI).</summary>
    public const int DefaultModuleWidth = 125;

    private static OptionDef Toggle(string key, string label, bool def, bool advanced = false, string? help = null) =>
        new(key, label, OptionKind.Toggle, def ? 1 : 0, Advanced: advanced, Help: help);

    private static OptionDef Slider(string key, string label, double def, double min, double max, double step,
        string? unit = null, bool advanced = false, string? help = null) =>
        new(key, label, OptionKind.Slider, def, min, max, step, unit, Advanced: advanced, Help: help);

    private static OptionDef Choice(string key, string label, int def, string[] choices, bool advanced = false, string? help = null) =>
        new(key, label, OptionKind.Choice, def, 0, choices.Length - 1, 1, null, choices, advanced, help);

    /// <summary>Couleur stockée en 0xRRGGBB.</summary>
    private static OptionDef Color(string key, string label, int rgb, bool advanced = false, string? help = null) =>
        new(key, label, OptionKind.Color, rgb, 0, 0xFFFFFF, 1, Advanced: advanced, Help: help);

    private static readonly string[] Directions = { "Vers la gauche", "Vers la droite" };

    public static readonly ModuleInfo[] All =
    {
        new("spectrogram", "Spectrogramme",
            "Historique des fréquences qui défile : graves en bas, aigus en haut, intensité codée par la palette.",
            DefaultModuleWidth, 50,
            new[]
            {
                Slider("speed", "Vitesse de défilement", 50, 10, 240, 5, "px/s"),
                Slider("floor", "Plancher", -84, -120, -40, 1, "dB", advanced: true, help: "Ce qui est plus faible reste transparent."),
                Slider("ceiling", "Plafond", -12, -40, 0, 1, "dB", advanced: true, help: "Niveau qui atteint la couleur la plus vive."),
                Slider("tilt", "Pente", 3, 0, 6, 0.5, "dB/oct", advanced: true, help: "Remonte les aigus pour équilibrer l'image."),
                Slider("minFreq", "Fréquence min", 30, 20, 200, 5, "Hz", advanced: true),
                Slider("maxFreq", "Fréquence max", 16000, 2000, 20000, 500, "Hz", advanced: true),
                Choice("direction", "Défilement", 0, Directions, advanced: true),
            },
            () => new SpectrogramModule()),

        new("waveform", "Forme d'onde",
            "Amplitude du son dans le temps, colorée selon ses fréquences (graves → aigus), ou oscilloscope.",
            DefaultModuleWidth, 50,
            new[]
            {
                Choice("mode", "Affichage", 0, new[] { "Miroir (centré)", "Enveloppe (depuis le bas)", "Oscilloscope" }),
                Slider("speed", "Vitesse de défilement", 60, 10, 400, 5, "px/s"),
                Choice("scale", "Échelle", 0, new[] { "Linéaire", "Logarithmique" }, advanced: true,
                    help: "Logarithmique : les passages calmes restent bien visibles."),
                Slider("gain", "Gain d'affichage", 0, 0, 24, 1, "dB", advanced: true),
                Choice("direction", "Défilement", 0, Directions, advanced: true),
            },
            () => new WaveformModule()),

        new("levels", "Niveaux crête + LUFS",
            "Crête-mètre gauche / droite et sonie (LUFS), la mesure de volume perçu utilisée par les plateformes de streaming.",
            DefaultModuleWidth, 80,
            new[]
            {
                Choice("lufsMode", "Mesure LUFS", 0, new[] { "Court terme (3 s)", "Momentanée (400 ms)" }),
                Slider("target", "Cible LUFS", -14, -30, -6, 1, "LUFS", help: "Repère sur la barre de sonie (-14 = Spotify, YouTube)."),
                Toggle("segments", "Segments LED", true, advanced: true),
                Slider("range", "Plage affichée", 48, 24, 96, 6, "dB", advanced: true),
                Slider("release", "Retombée", 24, 5, 80, 1, "dB/s", advanced: true),
                Slider("hold", "Maintien des crêtes", 1.5, 0, 5, 0.5, "s", advanced: true),
                Toggle("showLufsBar", "Barre LUFS", true, advanced: true),
                Toggle("showText", "Lecture LUFS", true, advanced: true),
            },
            () => new LevelsModule()),

        new("vu", "VU-mètre",
            "Niveau moyen avec une inertie de ~300 ms, proche du volume perçu.",
            DefaultModuleWidth, 70,
            new[]
            {
                Choice("style", "Style", 0, new[] { "LED (arc à segments)", "Analogique (aiguille)" }),
                Choice("mode", "Canaux", 0, new[] { "Mono", "Stéréo (gauche + droite)" }),
                Slider("reference", "Référence 0 VU", -18, -24, -8, 1, "dBFS", advanced: true,
                    help: "Niveau numérique qui correspond à 0 VU (-18 dBFS est la norme studio)."),
                Toggle("percent", "Échelle secondaire en %", true, advanced: true),
                Toggle("led", "Voyant de crête", true, advanced: true),
                Color("needle", "Couleur de l'aiguille", 0xFF7A1A, advanced: true, help: "Style analogique uniquement."),
            },
            () => new VuMeterModule()),

        new("spectrum", "Analyseur de spectre",
            "Répartition du son par fréquence en temps réel, avec la note dominante.",
            DefaultModuleWidth, 70,
            new[]
            {
                Choice("style", "Style", 0, new[] { "Relief (couleur selon le niveau)", "Barres", "Points LED", "Courbe remplie", "Ligne" }),
                Toggle("note", "Lecture du pic (niveau, fréquence, note)", true),
                Toggle("side", "Spectre stéréo en filigrane", true, advanced: true,
                    help: "Style relief : ligne fine du canal « côtés », ce qui diffère entre gauche et droite."),
                Toggle("peaks", "Crêtes maintenues", true, advanced: true),
                Toggle("grid", "Grille des fréquences", true, advanced: true),
                Slider("floor", "Plancher", -84, -120, -40, 1, "dB", advanced: true),
                Slider("ceiling", "Plafond", -12, -40, 0, 1, "dB", advanced: true),
                Slider("tilt", "Pente", 4.5, 0, 6, 0.5, "dB/oct", advanced: true, help: "Remonte les aigus pour équilibrer l'affichage."),
                Slider("release", "Retombée", 36, 6, 120, 2, "dB/s", advanced: true),
                Toggle("smooth", "Mouvement fluide", true, advanced: true,
                    help: "Les barres montent et retombent en courbes douces. Désactivez pour la réponse la plus directe, comme un instrument de mesure."),
                Slider("minFreq", "Fréquence min", 20, 20, 200, 5, "Hz", advanced: true),
                Slider("maxFreq", "Fréquence max", 20000, 2000, 20000, 500, "Hz", advanced: true),
            },
            () => new SpectrumModule()),

        new("imager", "Imageur stéréo",
            "Où se place le son entre gauche et droite, fréquence par fréquence : graves en bas, aigus en haut, mono au centre.",
            DefaultModuleWidth, 60,
            new[]
            {
                Choice("view", "Vue", 0, new[] { "Panorama par fréquence", "Largeur par fréquence" },
                    help: "Panorama : graves en bas, aigus en haut, gauche → droite."),
                Choice("correlation", "Corrélation de phase", 0, new[] { "Axe central coloré", "Barre", "Masquée" }, advanced: true,
                    help: "Rouge = son en opposition de phase (il s'annule en mono)."),
            },
            () => new StereoImagerModule()),

        new("loudness", "Historique de sonie",
            "Courbe de la sonie (LUFS) sur les dernières secondes, avec la cible : on voit si le son est régulier ou s'il « pompe ».",
            DefaultModuleWidth, 60,
            new[]
            {
                Choice("window", "Durée affichée", 1, new[] { "15 s", "30 s", "1 min", "5 min" }),
                Choice("style", "Style", 0, new[] { "Barres LED", "Courbe remplie" }),
                Choice("lufsMode", "Mesure LUFS", 0, new[] { "Court terme (3 s)", "Momentanée (400 ms)" }, advanced: true),
                Slider("target", "Cible LUFS", -14, -30, -6, 1, "LUFS", advanced: true),
                Slider("range", "Plancher", -42, -60, -20, 1, "LUFS", advanced: true),
            },
            () => new LoudnessHistoryModule()),

        new("tuner", "Accordeur",
            "Note jouée et justesse, ou tonalité (gamme) du morceau sur un mini-piano.",
            DefaultModuleWidth, 70,
            new[]
            {
                Choice("mode", "Mode", 0, new[] { "Note (justesse)", "Tonalité (gamme)" },
                    help: "Note : 100 cents = un demi-ton. Tonalité : estimée sur les dernières secondes de musique ; ◆ = note où se pose un morceau modal (◆F MIX = Fa mixolydien)."),
                Choice("notation", "Notation", 0, new[] { "Anglaise (C D E)", "Latine (Do Ré Mi)", "Camelot (8A, 8B)" },
                    help: "Camelot : la roue des DJ pour mixer des morceaux compatibles (mode tonalité)."),
                Toggle("noteOnly", "Note seule", false,
                    help: "Seulement la note (ou la tonalité) en grand, au centre : sans jauge de justesse ni mini-piano."),
                Slider("keyMemory", "Mémoire de la tonalité", 30, 5, 90, 1, "s", advanced: true,
                    help: "Plus long = plus stable, plus court = suit plus vite les changements de tonalité."),
                Slider("reference", "La de référence", 440, 415, 466, 1, "Hz", advanced: true),
                Slider("tolerance", "Tolérance « juste »", 5, 1, 20, 1, "cents", advanced: true),
                Toggle("history", "Historique des tonalités du morceau", false, advanced: true,
                    help: "Mode tonalité : « <C <Aᵐ » = tonalités précédentes, à la place du pourcentage de confiance."),
            },
            () => new TunerModule()),

        new("pianoroll", "Piano roll",
            "Les notes entendues, octave comprise, s'allument sur un clavier ; leur historique défile au-dessus. Les harmoniques sont écartées.",
            DefaultModuleWidth, 80,
            new[]
            {
                Choice("range", "Étendue", 1, new[] { "3 octaves (Do3 – Si5)", "4 octaves (Do2 – Si5)", "5 octaves (Do2 – Si6)", "6 octaves (Do1 – Si6)" },
                    help: "Plus d'octaves = touches plus fines ; élargissez le module si besoin."),
                Choice("colors", "Couleur des notes", 0, new[] { "Selon la hauteur (grave → aigu)", "Selon l'intensité" }),
                Slider("speed", "Vitesse de défilement", 15, 4, 60, 1, "px/s"),
                Choice("keyboard", "Clavier", 0, new[] { "En bas", "En haut" }, advanced: true,
                    help: "En haut : l'historique descend, pratique avec une barre des tâches en haut de l'écran."),
                Slider("sensitivity", "Sensibilité", 30, 12, 48, 1, "dB", advanced: true,
                    help: "Plus haut = les notes discrètes s'affichent aussi."),
            },
            () => new PianoRollModule()),

        new("xyscope", "Oscilloscope XY",
            "Le son tracé en points lumineux qui s'éteignent lentement, comme un écran cathodique. Mono = trait vertical, stéréo = nuage.",
            DefaultModuleWidth, 50,
            new[]
            {
                Choice("mode", "Tracé", 0, new[] { "Goniomètre (milieu / côtés)", "Lissajous (gauche / droite)" }),
                Slider("persistence", "Rémanence", 0.35, 0.05, 1.5, 0.05, "s"),
                Toggle("stretch", "Étirer sur toute la largeur", true, advanced: true),
            },
            () => new XyScopeModule()),

        new("rawdata", "Données brutes",
            "L'audio tel que la carte son le voit : échantillons en hexadécimal ou en matrice de bits. Purement expérimental.",
            DefaultModuleWidth, 60,
            new[]
            {
                Choice("mode", "Affichage", 0, new[] { "Hexadécimal", "Bits (matrice)" }),
                Slider("speed", "Vitesse de défilement", 30, 5, 120, 5),
            },
            () => new RawDataModule()),

        new("waterfall", "Cascade",
            "Une ligne de spectre par instant, empilées en perspective comme des montagnes qui reculent (façon « Unknown Pleasures »).",
            DefaultModuleWidth, 60,
            new[]
            {
                Slider("speed", "Lignes par seconde", 12, 3, 30, 1),
                Slider("lines", "Nombre de lignes", 10, 6, 24, 1),
                Toggle("perspective", "Perspective", true),
                Slider("floor", "Plancher", -80, -110, -50, 1, "dB", advanced: true, help: "Ce qui est plus faible reste plat."),
            },
            () => new WaterfallModule()),

        new("starfield", "Hyperespace",
            "Un champ d'étoiles qui fonce vers vous : chaque fréquence fait naître ses étoiles, la vitesse suit le volume.",
            DefaultModuleWidth, 50,
            new[]
            {
                Slider("speed", "Vitesse", 1, 0.2, 3, 0.1, "×"),
                Slider("density", "Densité d'étoiles", 60, 10, 200, 5),
                Toggle("trails", "Traînées", true),
                Toggle("warp", "Distorsion de vitesse", true,
                    help: "Sur les montées les plus intenses : traînées très longues, couleurs qui se séparent, anneaux qui partent du centre."),
            },
            () => new StarfieldModule()),

        new("melody", "Mélodie",
            "La note dominante laisse une traînée lumineuse sur une grille de notes : la mélodie se dessine, glissés compris.",
            DefaultModuleWidth, 60,
            new[]
            {
                Slider("speed", "Vitesse de défilement", 40, 10, 150, 5, "px/s"),
                Choice("range", "Étendue", 0, new[] { "2 octaves", "3 octaves" }),
            },
            () => new MelodyModule()),

        new("ring", "Anneau",
            "Le spectre enroulé en cercle : il respire sur les basses, tourne du côté de la stéréo, et chaque temps fort laisse une onde.",
            DefaultModuleWidth, 30,
            new[]
            {
                Choice("style", "Style", 2,new[] { "Rayons", "Écho", "Orbite 3D" },
                    help: "Rayons : le spectre en rayons autour du cercle. Écho : chaque instant laisse son contour, qui s'éloigne. Orbite 3D : l'anneau couché en perspective, sur toute la largeur."),
                Slider("spin", "Rotation", 1, 0, 3, 0.1, "×"),
                Toggle("stretch", "Étirer sur toute la largeur", false),
            },
            () => new RingModule()),
    };

    /// <summary>
    /// Rangement des modules pour l'interface (galerie, page Modules), dans l'ordre d'affichage.
    /// Un module absent d'ici tombe dans la dernière catégorie.
    /// </summary>
    public static readonly ModuleCategory[] Categories =
    {
        new("Volume", "Combien ça tape : crêtes, volume perçu, sonie dans le temps.", new[] { "levels", "vu", "loudness" }),
        new("Fréquences", "Les graves, les médiums et les aigus, en direct ou dans le temps.", new[] { "spectrum", "spectrogram" }),
        new("Stéréo", "Comment le son se répartit entre gauche et droite.", new[] { "imager", "xyscope" }),
        new("Notes", "Ce qui se joue : la note, la tonalité, la mélodie.", new[] { "tuner", "pianoroll", "melody" }),
        new("Spectacle", "Pour le plaisir des yeux : des visuels qui dansent sur la musique.", new[] { "starfield", "ring", "waterfall" }),
        new("Signal brut", "Le son tel quel : l'onde et les échantillons.", new[] { "waveform", "rawdata" }),
    };

    public static ModuleCategory CategoryOf(string id)
    {
        foreach (var category in Categories)
            if (Array.IndexOf(category.Ids, id) >= 0) return category;
        return Categories[^1];
    }

    /// <summary>Icône du module (police Segoe Fluent Icons / MDL2).</summary>
    public static string Icon(string id) => id switch
    {
        "levels" => "",      // volume
        "vu" => "",          // volume, ondes
        "loudness" => "",    // historique
        "spectrum" => "",    // égaliseur
        "spectrogram" => "", // graphique en aires
        "imager" => "",      // haut-parleurs
        "xyscope" => "",     // cercle tracé
        "tuner" => "",       // note de musique
        "pianoroll" => "",   // clavier
        "melody" => "",      // audio
        "starfield" => "",   // étoile
        "ring" => "",        // cercle qui tourne
        "waterfall" => "",   // couches
        "waveform" => "",    // tracé d'onde
        "rawdata" => "",     // code
        _ => "",
    };

    public static ModuleInfo? Find(string id)
    {
        foreach (var info in All)
            if (info.Id == id) return info;
        return null;
    }

    public static ModuleConfig CreateConfig(string id)
    {
        var info = Find(id) ?? throw new ArgumentException($"Module inconnu : {id}");
        return new ModuleConfig { Id = id, Enabled = true, Width = info.DefaultWidth };
    }
}
