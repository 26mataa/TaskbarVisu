using System.Globalization;

namespace TaskbarVisu.Core;

internal enum Language { Auto, French, English, Spanish, German }

/// <summary>
/// Traductions de l'interface. Le texte français sert de clé : <c>Loc.T("Fermer")</c> renvoie
/// la traduction dans la langue courante (ou le français si elle manque).
/// </summary>
internal static class Loc
{
    private static int _column = -1; // -1 = français, sinon index dans le tableau des traductions

    /// <summary>Code de la langue active : fr, en, es ou de.</summary>
    public static string Code { get; private set; } = "fr";

    public static event Action? Changed;

    /// <summary>Clés demandées sans traduction (diagnostic).</summary>
    public static HashSet<string> Missing { get; } = new();

    public static void Set(Language language)
    {
        Code = language switch
        {
            Language.French => "fr",
            Language.English => "en",
            Language.Spanish => "es",
            Language.German => "de",
            _ => Detect(),
        };
        _column = Code switch { "en" => 0, "es" => 1, "de" => 2, _ => -1 };
        Changed?.Invoke();
    }

    /// <summary>Langue de Windows si elle est disponible, sinon l'anglais.</summary>
    public static string Detect()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return two is "fr" or "en" or "es" or "de" ? two : "en";
    }

    public static string T(string french)
    {
        if (_column < 0 || string.IsNullOrEmpty(french)) return french;
        if (Table.TryGetValue(french, out var row)) return row[_column];
        lock (Missing) Missing.Add(french);
        return french;
    }

    public static string F(string frenchFormat, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(frenchFormat), args);

    private static readonly Dictionary<string, string[]> Table = Build();

    private static Dictionary<string, string[]> Build()
    {
        // Français | English | Español | Deutsch
        string[][] rows =
        {
            // --- Fenêtre, onglets, pied
            new[] { "TaskbarVisu — Paramètres", "TaskbarVisu — Settings", "TaskbarVisu — Ajustes", "TaskbarVisu — Einstellungen" },
            new[] { "Général", "General", "General", "Allgemein" },
            new[] { "Apparence", "Appearance", "Apariencia", "Darstellung" },
            new[] { "Disposition", "Layout", "Disposición", "Anordnung" },
            new[] { "Modules", "Modules", "Módulos", "Module" },
            new[] { "Tout réinitialiser", "Reset everything", "Restablecer todo", "Alles zurücksetzen" },
            new[] { "Fermer", "Close", "Cerrar", "Schließen" },
            new[] { "Remettre tous les réglages et la disposition par défaut ?", "Restore all settings and the layout to their defaults?", "¿Restablecer todos los ajustes y la disposición predeterminados?", "Alle Einstellungen und die Anordnung auf die Standardwerte zurücksetzen?" },
            new[] { "Audio : {0}", "Audio: {0}", "Audio: {0}", "Audio: {0}" },
            new[] { "En pause · ", "Paused · ", "En pausa · ", "Pausiert · " },
            new[] { "Zones libres : gauche {0} px · droite {1} px", "Free areas: left {0} px · right {1} px", "Zonas libres: izquierda {0} px · derecha {1} px", "Freie Bereiche: links {0} px · rechts {1} px" },

            // --- Général
            new[] { "Démarrage", "Startup", "Inicio", "Start" },
            new[] { "Démarrer avec Windows", "Start with Windows", "Iniciar con Windows", "Mit Windows starten" },
            new[] { "Fermer cette fenêtre laisse TaskbarVisu tourner dans la zone de notification", "Closing this window keeps TaskbarVisu running in the notification area", "Cerrar esta ventana deja TaskbarVisu funcionando en el área de notificación", "Beim Schließen dieses Fensters läuft TaskbarVisu im Infobereich weiter" },
            new[] { "Affichage", "Display", "Visualización", "Anzeige" },
            new[] { "Langue", "Language", "Idioma", "Sprache" },
            new[] { "Automatique (langue du système)", "Automatic (system language)", "Automático (idioma del sistema)", "Automatisch (Systemsprache)" },
            new[] { "Écran", "Screen", "Pantalla", "Bildschirm" },
            new[] { "Automatique (écran principal)", "Automatic (main screen)", "Automático (pantalla principal)", "Automatisch (Hauptbildschirm)" },
            new[] { "Écran {0} — {1} × {2}", "Screen {0} — {1} × {2}", "Pantalla {0} — {1} × {2}", "Bildschirm {0} — {1} × {2}" },
            new[] { " (principal)", " (main)", " (principal)", " (Haupt)" },
            new[] { "Masquer quand une application est en plein écran", "Hide when an application is in full screen", "Ocultar cuando una aplicación está en pantalla completa", "Ausblenden, wenn eine Anwendung im Vollbild läuft" },
            new[] { "Performances", "Performance", "Rendimiento", "Leistung" },
            new[] { "Images par seconde", "Frames per second", "Imágenes por segundo", "Bilder pro Sekunde" },
            new[] { "{0} i/s", "{0} fps", "{0} fps", "{0} fps" },
            new[] { "Résolution de l'analyse (FFT)", "Analysis resolution (FFT)", "Resolución del análisis (FFT)", "Analyseauflösung (FFT)" },
            new[] { "2048 points (le plus léger)", "2048 points (lightest)", "2048 puntos (el más ligero)", "2048 Punkte (am leichtesten)" },
            new[] { "4096 points (recommandé)", "4096 points (recommended)", "4096 puntos (recomendado)", "4096 Punkte (empfohlen)" },
            new[] { "8192 points (graves plus précis)", "8192 points (more precise lows)", "8192 puntos (graves más precisos)", "8192 Punkte (präzisere Bässe)" },
            new[] { "Économie d'énergie : ne plus redessiner pendant les silences", "Power saving: stop redrawing during silence", "Ahorro de energía: no redibujar durante los silencios", "Energiesparen: bei Stille nicht neu zeichnen" },
            new[] { "Vérification de la barre des tâches", "Taskbar check", "Comprobación de la barra de tareas", "Taskleisten-Prüfung" },
            new[] { "Les zones libres sont recalculées dès qu'une application s'ouvre ou se ferme ; cette vérification périodique rattrape le reste (icônes de notification…). Plus long = plus économe.", "Free areas are recomputed as soon as an app opens or closes; this periodic check catches the rest (notification icons…). Longer = lighter.", "Las zonas libres se recalculan en cuanto se abre o cierra una aplicación; esta comprobación periódica cubre el resto (iconos de notificación…). Más largo = más ligero.", "Freie Bereiche werden neu berechnet, sobald eine App geöffnet oder geschlossen wird; diese regelmäßige Prüfung erfasst den Rest (Infobereich-Symbole…). Länger = sparsamer." },
            new[] { "Dossier", "Folder", "Carpeta", "Ordner" },
            new[] { "Ouvrir le dossier des réglages et du journal", "Open the settings and log folder", "Abrir la carpeta de ajustes y registro", "Ordner mit Einstellungen und Protokoll öffnen" },

            // --- Apparence
            new[] { "Préréglages", "Presets", "Preajustes", "Voreinstellungen" },
            new[] { "Un préréglage change la palette, la police, les effets, le fond et les couleurs. Tout reste ajustable ci-dessous ; les couleurs propres à un module sont conservées.", "A preset changes the palette, font, effects, background and colours. Everything stays adjustable below; per-module colours are kept.", "Un preajuste cambia la paleta, la fuente, los efectos, el fondo y los colores. Todo sigue siendo ajustable abajo; se conservan los colores propios de cada módulo.", "Eine Voreinstellung ändert Palette, Schrift, Effekte, Hintergrund und Farben. Alles bleibt unten anpassbar; modulspezifische Farben bleiben erhalten." },
            new[] { "Labo", "Lab", "Laboratorio", "Labor" },
            new[] { "CRT Phosphore", "CRT Phosphor", "CRT Fósforo", "CRT Phosphor" },
            new[] { "Toxique", "Toxic", "Tóxico", "Toxisch" },
            new[] { "Style", "Style", "Estilo", "Stil" },
            new[] { "Police pixel (matrice de points 5×7, façon afficheur)", "Pixel font (5×7 dot matrix, display style)", "Fuente de píxeles (matriz de puntos 5×7, estilo pantalla)", "Pixelschrift (5×7-Punktmatrix, Display-Stil)" },
            new[] { "Graduations, étiquettes et repères techniques", "Scales, labels and technical markers", "Escalas, etiquetas y marcas técnicas", "Skalen, Beschriftungen und technische Markierungen" },
            new[] { "Effet écran à balayage (scanlines)", "Scanline effect", "Efecto de líneas de barrido (scanlines)", "Scanline-Effekt" },
            new[] { "Fond des modules", "Module background", "Fondo de los módulos", "Modul-Hintergrund" },
            new[] { "Aucun (fondu dans la barre)", "None (blends into the taskbar)", "Ninguno (fundido con la barra)", "Keiner (verschmilzt mit der Leiste)" },
            new[] { "Sombre translucide", "Translucent dark", "Oscuro translúcido", "Dunkel durchscheinend" },
            new[] { "Noir", "Black", "Negro", "Schwarz" },
            new[] { "Opacité", "Opacity", "Opacidad", "Deckkraft" },
            new[] { "Couleurs", "Colours", "Colores", "Farben" },
            new[] { "Thème", "Theme", "Tema", "Design" },
            new[] { "Automatique (suit la barre des tâches)", "Automatic (follows the taskbar)", "Automático (sigue la barra de tareas)", "Automatisch (folgt der Taskleiste)" },
            new[] { "Sombre", "Dark", "Oscuro", "Dunkel" },
            new[] { "Clair", "Light", "Claro", "Hell" },
            new[] { "Palette générale", "Main palette", "Paleta general", "Hauptpalette" },
            new[] { "Palette personnalisée (grave → aigu)", "Custom palette (low → high)", "Paleta personalizada (grave → agudo)", "Eigene Palette (tief → hoch)" },
            new[] { "La palette colore les données (fréquences, niveaux) de tous les modules. Chaque module peut avoir ses propres couleurs dans l'onglet Modules.", "The palette colours the data (frequencies, levels) of every module. Each module can have its own colours in the Modules tab.", "La paleta colorea los datos (frecuencias, niveles) de todos los módulos. Cada módulo puede tener sus propios colores en la pestaña Módulos.", "Die Palette färbt die Daten (Frequenzen, Pegel) aller Module. Jedes Modul kann im Reiter Module eigene Farben haben." },
            new[] { "Textes et repères", "Text and markers", "Textos y marcas", "Texte und Markierungen" },
            new[] { "Blanc lavande (par défaut)", "Lavender white (default)", "Blanco lavanda (predeterminado)", "Lavendelweiß (Standard)" },
            new[] { "Teintée par la palette", "Tinted by the palette", "Teñido por la paleta", "Von der Palette getönt" },
            new[] { "Accentuation Windows", "Windows accent", "Acento de Windows", "Windows-Akzentfarbe" },
            new[] { "Personnalisée", "Custom", "Personalizado", "Benutzerdefiniert" },
            new[] { "Couleur personnalisée", "Custom colour", "Color personalizado", "Eigene Farbe" },
            new[] { "Textes et repères : chiffres, lettres, graduations, grilles et crêtes maintenues. Les données (barres, courbes, points) suivent la palette.", "Text and markers: numbers, letters, scales, grids and held peaks. Data (bars, curves, dots) follows the palette.", "Textos y marcas: cifras, letras, escalas, rejillas y picos retenidos. Los datos (barras, curvas, puntos) siguen la paleta.", "Texte und Markierungen: Zahlen, Buchstaben, Skalen, Raster und gehaltene Spitzen. Die Daten (Balken, Kurven, Punkte) folgen der Palette." },
            new[] { "Brasier", "Ember", "Brasa", "Glut" },
            new[] { "Spectrale", "Spectral", "Espectral", "Spektral" },
            new[] { "Phosphore", "Phosphor", "Fósforo", "Phosphor" },
            new[] { "Glace", "Ice", "Hielo", "Eis" },
            new[] { "Arc-en-ciel RGB", "RGB rainbow", "Arcoíris RGB", "RGB-Regenbogen" },
            new[] { "Arc-en-ciel animé", "Animated rainbow", "Arcoíris animado", "Animierter Regenbogen" },
            new[] { "Monochrome", "Monochrome", "Monocromo", "Monochrom" },

            // --- Disposition
            new[] { "Les espaces libres à gauche et à droite des icônes de la barre des tâches sont détectés automatiquement (y compris les widgets comme FluentFlyout). Cochez les modules à afficher, glissez-les pour changer l'ordre ou le côté.", "The free space left and right of the taskbar icons is detected automatically (including widgets such as FluentFlyout). Tick the modules to show and drag them to change their order or side.", "Los espacios libres a la izquierda y a la derecha de los iconos de la barra de tareas se detectan automáticamente (incluidos widgets como FluentFlyout). Marca los módulos que quieres mostrar y arrástralos para cambiar el orden o el lado.", "Die freien Bereiche links und rechts der Taskleisten-Symbole werden automatisch erkannt (auch Widgets wie FluentFlyout). Hake die anzuzeigenden Module an und ziehe sie, um Reihenfolge oder Seite zu ändern." },
            new[] { "Zone gauche", "Left area", "Zona izquierda", "Linker Bereich" },
            new[] { "Zone droite", "Right area", "Zona derecha", "Rechter Bereich" },
            new[] { "Monter", "Move up", "Subir", "Nach oben" },
            new[] { "Descendre", "Move down", "Bajar", "Nach unten" },
            new[] { "Vers la droite ▶", "To the right ▶", "A la derecha ▶", "Nach rechts ▶" },
            new[] { "◀ Vers la gauche", "◀ To the left", "◀ A la izquierda", "◀ Nach links" },
            new[] { "Sélectionnez un module pour régler sa largeur.", "Select a module to adjust its width.", "Selecciona un módulo para ajustar su anchura.", "Wähle ein Modul, um seine Breite einzustellen." },
            new[] { "Largeur préférée", "Preferred width", "Anchura preferida", "Bevorzugte Breite" },
            new[] { "Avec « Étirer les modules », la largeur sert de proportion entre les modules d'une même zone. S'il manque de place, les modules rétrécissent jusqu'à leur minimum puis les derniers sont masqués.", "With “Stretch modules”, the width is used as a proportion between modules of the same area. If space runs out, modules shrink to their minimum, then the last ones are hidden.", "Con «Estirar los módulos», la anchura sirve de proporción entre los módulos de una misma zona. Si falta espacio, los módulos se encogen hasta su mínimo y luego se ocultan los últimos.", "Mit „Module strecken“ dient die Breite als Verhältnis zwischen den Modulen eines Bereichs. Fehlt Platz, schrumpfen die Module bis zum Minimum, danach werden die letzten ausgeblendet." },
            new[] { "Ajustements", "Adjustments", "Ajustes", "Feineinstellungen" },
            new[] { "Réinitialiser la disposition", "Reset the layout", "Restablecer la disposición", "Anordnung zurücksetzen" },
            new[] { "Étirer les modules pour remplir chaque zone", "Stretch modules to fill each area", "Estirar los módulos para llenar cada zona", "Module strecken, um jeden Bereich zu füllen" },
            new[] { "Zone gauche : placer les modules", "Left area: place modules", "Zona izquierda: colocar los módulos", "Linker Bereich: Module platzieren" },
            new[] { "Zone droite : placer les modules", "Right area: place modules", "Zona derecha: colocar los módulos", "Rechter Bereich: Module platzieren" },
            new[] { "Contre le bord de l'écran", "Against the screen edge", "Junto al borde de la pantalla", "Am Bildschirmrand" },
            new[] { "Au centre de la zone", "Centred in the area", "En el centro de la zona", "In der Mitte des Bereichs" },
            new[] { "Contre les icônes", "Against the icons", "Junto a los iconos", "An den Symbolen" },
            new[] { "Contre la zone de notification", "Against the notification area", "Junto al área de notificación", "Am Infobereich" },
            new[] { "Le placement s'applique quand les modules ne sont pas étirés.", "Placement applies when modules are not stretched.", "La colocación se aplica cuando los módulos no están estirados.", "Die Platzierung gilt, wenn die Module nicht gestreckt sind." },
            new[] { "Espacement entre les modules", "Spacing between modules", "Espacio entre módulos", "Abstand zwischen Modulen" },
            new[] { "Fondu des bords", "Edge fade", "Difuminado de los bordes", "Randausblendung" },
            new[] { "Marge verticale", "Vertical margin", "Margen vertical", "Vertikaler Rand" },
            new[] { "Les tailles sont exprimées à 100 % d'échelle et suivent automatiquement le DPI de l'écran.", "Sizes are given at 100 % scale and automatically follow the screen DPI.", "Los tamaños se expresan a escala del 100 % y siguen automáticamente el DPI de la pantalla.", "Größen gelten für 100 % Skalierung und folgen automatisch der Bildschirm-DPI." },

            // --- Onglet Modules
            new[] { "Réglages propres à chaque module. Les changements s'appliquent immédiatement sur la barre des tâches.", "Settings for each module. Changes apply immediately on the taskbar.", "Ajustes propios de cada módulo. Los cambios se aplican de inmediato en la barra de tareas.", "Einstellungen pro Modul. Änderungen werden sofort auf der Taskleiste übernommen." },
            new[] { "zone gauche", "left area", "zona izquierda", "linker Bereich" },
            new[] { "zone droite", "right area", "zona derecha", "rechter Bereich" },
            new[] { "masqué", "hidden", "oculto", "ausgeblendet" },
            new[] { "Plus d'options", "More options", "Más opciones", "Weitere Optionen" },
            new[] { "Réinitialiser ce module", "Reset this module", "Restablecer este módulo", "Dieses Modul zurücksetzen" },
            new[] { "Comme l'apparence générale", "Same as the general appearance", "Como la apariencia general", "Wie die allgemeine Darstellung" },
            new[] { "Autre palette", "Another palette", "Otra paleta", "Andere Palette" },
            new[] { "Couleur unie", "Solid colour", "Color sólido", "Einfarbig" },
            new[] { "Palette du module", "Module palette", "Paleta del módulo", "Modul-Palette" },
            new[] { "Couleur du module", "Module colour", "Color del módulo", "Modulfarbe" },

            // --- Icône de notification
            new[] { "Paramètres…", "Settings…", "Ajustes…", "Einstellungen…" },
            new[] { "Mettre en pause", "Pause", "Pausar", "Pausieren" },
            new[] { "Reprendre", "Resume", "Reanudar", "Fortsetzen" },
            new[] { "Quitter", "Quit", "Salir", "Beenden" },
            new[] { "TaskbarVisu (en pause)", "TaskbarVisu (paused)", "TaskbarVisu (en pausa)", "TaskbarVisu (pausiert)" },
            new[] { "TaskbarVisu continue de tourner", "TaskbarVisu keeps running", "TaskbarVisu sigue funcionando", "TaskbarVisu läuft weiter" },
            new[] { "Les visualiseurs restent actifs. Cliquez sur l'icône de la zone de notification pour rouvrir les réglages.", "The visualizers stay active. Click the notification area icon to reopen the settings.", "Los visualizadores siguen activos. Haz clic en el icono del área de notificación para volver a abrir los ajustes.", "Die Visualisierungen bleiben aktiv. Klicke auf das Symbol im Infobereich, um die Einstellungen erneut zu öffnen." },

            // --- États (audio, barre des tâches)
            new[] { "Arrêté", "Stopped", "Detenido", "Gestoppt" },
            new[] { "En pause", "Paused", "En pausa", "Pausiert" },
            new[] { "Aucune sortie audio", "No audio output", "Sin salida de audio", "Keine Audioausgabe" },
            new[] { "Capture audio impossible (nouvel essai…)", "Audio capture failed (retrying…)", "No se puede capturar el audio (reintentando…)", "Audioaufnahme fehlgeschlagen (neuer Versuch…)" },
            new[] { "Capture interrompue (nouvel essai…)", "Capture interrupted (retrying…)", "Captura interrumpida (reintentando…)", "Aufnahme unterbrochen (neuer Versuch…)" },
            new[] { "Signal de test", "Test signal", "Señal de prueba", "Testsignal" },
            new[] { "Recherche de la barre des tâches…", "Looking for the taskbar…", "Buscando la barra de tareas…", "Taskleiste wird gesucht…" },
            new[] { "Barre des tâches introuvable", "Taskbar not found", "Barra de tareas no encontrada", "Taskleiste nicht gefunden" },
            new[] { "Barre des tâches verticale : non prise en charge", "Vertical taskbar: not supported", "Barra de tareas vertical: no compatible", "Vertikale Taskleiste: nicht unterstützt" },
            new[] { "Barre des tâches masquée", "Taskbar hidden", "Barra de tareas oculta", "Taskleiste ausgeblendet" },
            new[] { "Analyse de la barre des tâches impossible pour le moment", "Taskbar analysis unavailable for now", "Análisis de la barra de tareas no disponible por ahora", "Taskleisten-Analyse derzeit nicht möglich" },

            // --- Modules : noms et descriptions
            new[] { "Spectrogramme", "Spectrogram", "Espectrograma", "Spektrogramm" },
            new[] { "Historique des fréquences qui défile : graves en bas, aigus en haut, intensité codée par la palette.", "Scrolling frequency history: lows at the bottom, highs at the top, intensity shown by the palette.", "Historial de frecuencias en desplazamiento: graves abajo, agudos arriba, intensidad según la paleta.", "Laufender Frequenzverlauf: Bässe unten, Höhen oben, Intensität über die Palette." },
            new[] { "Forme d'onde", "Waveform", "Forma de onda", "Wellenform" },
            new[] { "Amplitude du son dans le temps, colorée selon ses fréquences (graves → aigus), ou oscilloscope.", "Sound amplitude over time, coloured by its frequencies (lows → highs), or an oscilloscope.", "Amplitud del sonido en el tiempo, coloreada según sus frecuencias (graves → agudos), u osciloscopio.", "Lautstärkeverlauf über die Zeit, nach Frequenzen gefärbt (tief → hoch), oder Oszilloskop." },
            new[] { "Niveaux crête + LUFS", "Peak levels + LUFS", "Niveles de pico + LUFS", "Spitzenpegel + LUFS" },
            new[] { "Crête-mètre gauche / droite et sonie (LUFS), la mesure de volume perçu utilisée par les plateformes de streaming.", "Left / right peak meter and loudness (LUFS), the perceived-volume measure used by streaming platforms.", "Medidor de picos izquierda / derecha y sonoridad (LUFS), la medida de volumen percibido de las plataformas de streaming.", "Spitzenpegel links / rechts und Lautheit (LUFS), das Maß für wahrgenommene Lautstärke der Streaming-Plattformen." },
            new[] { "VU-mètre", "VU meter", "Vúmetro", "VU-Meter" },
            new[] { "Niveau moyen avec une inertie de ~300 ms, proche du volume perçu.", "Average level with ~300 ms inertia, close to perceived volume.", "Nivel medio con una inercia de ~300 ms, cercano al volumen percibido.", "Mittlerer Pegel mit ~300 ms Trägheit, nah an der wahrgenommenen Lautstärke." },
            new[] { "Analyseur de spectre", "Spectrum analyzer", "Analizador de espectro", "Spektrumanalysator" },
            new[] { "Répartition du son par fréquence en temps réel, avec la note dominante.", "Real-time distribution of the sound by frequency, with the dominant note.", "Reparto del sonido por frecuencia en tiempo real, con la nota dominante.", "Echtzeit-Verteilung des Klangs nach Frequenz, mit dem dominanten Ton." },
            new[] { "Imageur stéréo", "Stereo imager", "Imagen estéreo", "Stereo-Imager" },
            new[] { "Où se place le son entre gauche et droite. Demi-cercle : mono au centre, gauche / droite sur les diagonales.", "Where the sound sits between left and right. Half-circle: mono in the middle, left / right on the diagonals.", "Dónde se sitúa el sonido entre izquierda y derecha. Semicírculo: mono en el centro, izquierda / derecha en las diagonales.", "Wo der Klang zwischen links und rechts liegt. Halbkreis: Mono in der Mitte, links / rechts auf den Diagonalen." },
            new[] { "Historique de sonie", "Loudness history", "Historial de sonoridad", "Lautheitsverlauf" },
            new[] { "Courbe de la sonie (LUFS) sur les dernières secondes, avec la cible : on voit si le son est régulier ou s'il « pompe ».", "Loudness (LUFS) over the last seconds, with the target: you can see whether the sound is steady or “pumping”.", "Curva de sonoridad (LUFS) de los últimos segundos, con el objetivo: se ve si el sonido es regular o si «bombea».", "Lautheit (LUFS) der letzten Sekunden mit Zielwert: man sieht, ob der Klang gleichmäßig ist oder „pumpt“." },
            new[] { "Accordeur", "Tuner", "Afinador", "Stimmgerät" },
            new[] { "Note jouée et justesse, ou tonalité (gamme) du morceau sur un mini-piano.", "Played note and tuning, or the song's key (scale) on a mini piano.", "Nota tocada y afinación, o tonalidad (escala) de la canción en un mini piano.", "Gespielter Ton und Stimmung oder Tonart (Skala) des Stücks auf einem Mini-Klavier." },
            new[] { "Piano roll", "Piano roll", "Piano roll", "Piano-Roll" },
            new[] { "Les notes entendues, octave comprise, s'allument sur un clavier ; leur historique défile au-dessus. Les harmoniques sont écartées.", "The notes you hear, octave included, light up on a keyboard; their history scrolls above. Harmonics are filtered out.", "Las notas que se oyen, con su octava, se iluminan en un teclado; su historial se desplaza encima. Los armónicos se descartan.", "Die gehörten Töne leuchten samt Oktave auf einer Klaviatur auf; ihr Verlauf läuft darüber. Obertöne werden herausgefiltert." },

            // --- Options des modules
            new[] { "Vitesse de défilement", "Scroll speed", "Velocidad de desplazamiento", "Laufgeschwindigkeit" },
            new[] { "Plancher", "Floor", "Mínimo", "Untergrenze" },
            new[] { "Ce qui est plus faible reste transparent.", "Anything quieter stays transparent.", "Lo que sea más débil queda transparente.", "Alles Leisere bleibt transparent." },
            new[] { "Plafond", "Ceiling", "Máximo", "Obergrenze" },
            new[] { "Niveau qui atteint la couleur la plus vive.", "Level that reaches the brightest colour.", "Nivel que alcanza el color más vivo.", "Pegel, der die hellste Farbe erreicht." },
            new[] { "Pente", "Tilt", "Pendiente", "Neigung" },
            new[] { "Remonte les aigus pour équilibrer l'image.", "Lifts the highs to balance the picture.", "Realza los agudos para equilibrar la imagen.", "Hebt die Höhen an, um das Bild auszugleichen." },
            new[] { "Remonte les aigus pour équilibrer l'affichage.", "Lifts the highs to balance the display.", "Realza los agudos para equilibrar la visualización.", "Hebt die Höhen an, um die Anzeige auszugleichen." },
            new[] { "Fréquence min", "Min frequency", "Frecuencia mín.", "Min. Frequenz" },
            new[] { "Fréquence max", "Max frequency", "Frecuencia máx.", "Max. Frequenz" },
            new[] { "Défilement", "Scrolling", "Desplazamiento", "Laufrichtung" },
            new[] { "Vers la gauche", "To the left", "Hacia la izquierda", "Nach links" },
            new[] { "Vers la droite", "To the right", "Hacia la derecha", "Nach rechts" },
            new[] { "Affichage", "Display", "Visualización", "Anzeige" },
            new[] { "Miroir (centré)", "Mirror (centred)", "Espejo (centrado)", "Gespiegelt (zentriert)" },
            new[] { "Enveloppe (depuis le bas)", "Envelope (from the bottom)", "Envolvente (desde abajo)", "Hüllkurve (von unten)" },
            new[] { "Oscilloscope", "Oscilloscope", "Osciloscopio", "Oszilloskop" },
            new[] { "Échelle", "Scale", "Escala", "Skala" },
            new[] { "Linéaire", "Linear", "Lineal", "Linear" },
            new[] { "Logarithmique", "Logarithmic", "Logarítmica", "Logarithmisch" },
            new[] { "Logarithmique : les passages calmes restent bien visibles.", "Logarithmic: quiet passages stay clearly visible.", "Logarítmica: los pasajes tranquilos siguen bien visibles.", "Logarithmisch: leise Passagen bleiben gut sichtbar." },
            new[] { "Gain d'affichage", "Display gain", "Ganancia de visualización", "Anzeigeverstärkung" },
            new[] { "Mesure LUFS", "LUFS measure", "Medida LUFS", "LUFS-Messung" },
            new[] { "Court terme (3 s)", "Short term (3 s)", "Corto plazo (3 s)", "Kurzzeit (3 s)" },
            new[] { "Momentanée (400 ms)", "Momentary (400 ms)", "Momentánea (400 ms)", "Momentan (400 ms)" },
            new[] { "Cible LUFS", "LUFS target", "Objetivo LUFS", "LUFS-Zielwert" },
            new[] { "Repère sur la barre de sonie (-14 = Spotify, YouTube).", "Marker on the loudness bar (-14 = Spotify, YouTube).", "Marca en la barra de sonoridad (-14 = Spotify, YouTube).", "Markierung auf der Lautheitsleiste (-14 = Spotify, YouTube)." },
            new[] { "Segments LED", "LED segments", "Segmentos LED", "LED-Segmente" },
            new[] { "Plage affichée", "Displayed range", "Rango mostrado", "Angezeigter Bereich" },
            new[] { "Retombée", "Fall-off", "Caída", "Rückfall" },
            new[] { "Maintien des crêtes", "Peak hold", "Retención de picos", "Spitzen halten" },
            new[] { "Barre LUFS", "LUFS bar", "Barra LUFS", "LUFS-Leiste" },
            new[] { "Lecture LUFS", "LUFS readout", "Lectura LUFS", "LUFS-Anzeige" },
            new[] { "LED (arc à segments)", "LED (segmented arc)", "LED (arco segmentado)", "LED (Segmentbogen)" },
            new[] { "Analogique (aiguille)", "Analogue (needle)", "Analógico (aguja)", "Analog (Nadel)" },
            new[] { "Canaux", "Channels", "Canales", "Kanäle" },
            new[] { "Mono", "Mono", "Mono", "Mono" },
            new[] { "Stéréo (gauche + droite)", "Stereo (left + right)", "Estéreo (izquierda + derecha)", "Stereo (links + rechts)" },
            new[] { "Référence 0 VU", "0 VU reference", "Referencia 0 VU", "0-VU-Referenz" },
            new[] { "Niveau numérique qui correspond à 0 VU (-18 dBFS est la norme studio).", "Digital level matching 0 VU (-18 dBFS is the studio standard).", "Nivel digital que corresponde a 0 VU (-18 dBFS es el estándar de estudio).", "Digitaler Pegel, der 0 VU entspricht (-18 dBFS ist Studiostandard)." },
            new[] { "Échelle secondaire en %", "Secondary % scale", "Escala secundaria en %", "Zweitskala in %" },
            new[] { "Voyant de crête", "Peak LED", "Indicador de pico", "Spitzen-LED" },
            new[] { "Couleur de l'aiguille", "Needle colour", "Color de la aguja", "Nadelfarbe" },
            new[] { "Style analogique uniquement.", "Analogue style only.", "Solo estilo analógico.", "Nur analoger Stil." },
            new[] { "Barres", "Bars", "Barras", "Balken" },
            new[] { "Points LED", "LED dots", "Puntos LED", "LED-Punkte" },
            new[] { "Courbe remplie", "Filled curve", "Curva rellena", "Gefüllte Kurve" },
            new[] { "Ligne", "Line", "Línea", "Linie" },
            new[] { "Note détectée", "Detected note", "Nota detectada", "Erkannter Ton" },
            new[] { "Crêtes maintenues", "Held peaks", "Picos retenidos", "Gehaltene Spitzen" },
            new[] { "Grille des fréquences", "Frequency grid", "Rejilla de frecuencias", "Frequenzraster" },
            new[] { "Seconde vue", "Second view", "Segunda vista", "Zweite Ansicht" },
            new[] { "Panorama par fréquence", "Panorama by frequency", "Panorama por frecuencia", "Panorama nach Frequenz" },
            new[] { "Largeur par fréquence", "Width by frequency", "Anchura por frecuencia", "Breite nach Frequenz" },
            new[] { "Aucune", "None", "Ninguna", "Keine" },
            new[] { "Panorama : graves en bas, aigus en haut, gauche → droite.", "Panorama: lows at the bottom, highs at the top, left → right.", "Panorama: graves abajo, agudos arriba, izquierda → derecha.", "Panorama: Bässe unten, Höhen oben, links → rechts." },
            new[] { "Points colorés par bande (graves / médiums / aigus)", "Dots coloured by band (lows / mids / highs)", "Puntos coloreados por banda (graves / medios / agudos)", "Punkte nach Band gefärbt (Tiefen / Mitten / Höhen)" },
            new[] { "Corrélation de phase", "Phase correlation", "Correlación de fase", "Phasenkorrelation" },
            new[] { "Axe central coloré", "Coloured centre axis", "Eje central coloreado", "Farbige Mittelachse" },
            new[] { "Barre", "Bar", "Barra", "Leiste" },
            new[] { "Masquée", "Hidden", "Oculta", "Ausgeblendet" },
            new[] { "Rouge = son en opposition de phase (il s'annule en mono).", "Red = out-of-phase sound (it cancels out in mono).", "Rojo = sonido en oposición de fase (se anula en mono).", "Rot = gegenphasiger Klang (löscht sich in Mono aus)." },
            new[] { "Rémanence", "Persistence", "Persistencia", "Nachleuchten" },
            new[] { "Densité de points", "Dot density", "Densidad de puntos", "Punktdichte" },
            new[] { "Durée affichée", "Time shown", "Duración mostrada", "Angezeigte Dauer" },
            new[] { "Barres LED", "LED bars", "Barras LED", "LED-Balken" },
            new[] { "Mode", "Mode", "Modo", "Modus" },
            new[] { "Note (justesse)", "Note (tuning)", "Nota (afinación)", "Ton (Stimmung)" },
            new[] { "Tonalité (gamme)", "Key (scale)", "Tonalidad (escala)", "Tonart (Skala)" },
            new[] { "Note : 100 cents = un demi-ton. Tonalité : estimée sur les dernières secondes de musique ; ◆ = note où se pose un morceau modal (◆F MIX = Fa mixolydien).", "Note: 100 cents = one semitone. Key: estimated over the last seconds of music; ◆ = the note a modal song rests on (◆F MIX = F Mixolydian).", "Nota: 100 cents = un semitono. Tonalidad: estimada sobre los últimos segundos de música; ◆ = nota en la que se apoya una canción modal (◆F MIX = Fa mixolidio).", "Ton: 100 Cent = ein Halbton. Tonart: geschätzt über die letzten Sekunden Musik; ◆ = Ton, auf dem ein modales Stück ruht (◆F MIX = F-Mixolydisch)." },
            new[] { "Notation", "Notation", "Notación", "Notation" },
            new[] { "Anglaise (C D E)", "English (C D E)", "Inglesa (C D E)", "Englisch (C D E)" },
            new[] { "Latine (Do Ré Mi)", "Solfège (Do Re Mi)", "Latina (Do Re Mi)", "Solmisation (Do Re Mi)" },
            new[] { "Mémoire de la tonalité", "Key memory", "Memoria de la tonalidad", "Tonart-Gedächtnis" },
            new[] { "Camelot (8A, 8B)", "Camelot (8A, 8B)", "Camelot (8A, 8B)", "Camelot (8A, 8B)" },
            new[] { "Camelot : la roue des DJ pour mixer des morceaux compatibles (mode tonalité).", "Camelot: the DJ wheel for mixing compatible tracks (key mode).", "Camelot: la rueda de los DJ para mezclar temas compatibles (modo tonalidad).", "Camelot: das DJ-Rad zum Mixen harmonisch passender Tracks (Tonart-Modus)." },
            new[] { "Plus long = plus stable, plus court = suit plus vite les changements de tonalité.", "Longer = steadier, shorter = follows key changes faster.", "Más largo = más estable, más corto = sigue antes los cambios de tonalidad.", "Länger = stabiler, kürzer = folgt Tonartwechseln schneller." },
            new[] { "La de référence", "Reference A", "La de referencia", "Kammerton A" },
            new[] { "Tolérance « juste »", "“In tune” tolerance", "Tolerancia «afinado»", "Toleranz „gestimmt“" },
            new[] { "Étendue", "Range", "Extensión", "Umfang" },
            new[] { "3 octaves (Do3 – Si5)", "3 octaves (C3 – B5)", "3 octavas (Do3 – Si5)", "3 Oktaven (C3 – H5)" },
            new[] { "4 octaves (Do2 – Si5)", "4 octaves (C2 – B5)", "4 octavas (Do2 – Si5)", "4 Oktaven (C2 – H5)" },
            new[] { "5 octaves (Do2 – Si6)", "5 octaves (C2 – B6)", "5 octavas (Do2 – Si6)", "5 Oktaven (C2 – H6)" },
            new[] { "6 octaves (Do1 – Si6)", "6 octaves (C1 – B6)", "6 octavas (Do1 – Si6)", "6 Oktaven (C1 – H6)" },
            new[] { "Plus d'octaves = touches plus fines ; élargissez le module si besoin.", "More octaves = thinner keys; widen the module if needed.", "Más octavas = teclas más finas; ensancha el módulo si hace falta.", "Mehr Oktaven = schmalere Tasten; Modul bei Bedarf verbreitern." },
            new[] { "Couleur des notes", "Note colours", "Color de las notas", "Notenfarben" },
            new[] { "Selon la hauteur (grave → aigu)", "By pitch (low → high)", "Según la altura (grave → agudo)", "Nach Tonhöhe (tief → hoch)" },
            new[] { "Selon l'intensité", "By intensity", "Según la intensidad", "Nach Intensität" },
            new[] { "Clavier", "Keyboard", "Teclado", "Klaviatur" },
            new[] { "En bas", "At the bottom", "Abajo", "Unten" },
            new[] { "En haut", "At the top", "Arriba", "Oben" },
            new[] { "En haut : l'historique descend, pratique avec une barre des tâches en haut de l'écran.", "At the top: the history scrolls down, handy with a taskbar at the top of the screen.", "Arriba: el historial baja, práctico con la barra de tareas en la parte superior.", "Oben: der Verlauf läuft nach unten, praktisch mit einer Taskleiste am oberen Bildschirmrand." },
            new[] { "Sensibilité", "Sensitivity", "Sensibilidad", "Empfindlichkeit" },
            new[] { "Plus haut = les notes discrètes s'affichent aussi.", "Higher = quiet notes show up too.", "Más alto = también se muestran las notas suaves.", "Höher = auch leise Töne werden angezeigt." },
        };

        var table = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in rows) table[row[0]] = new[] { row[1], row[2], row[3] };
        return table;
    }
}
