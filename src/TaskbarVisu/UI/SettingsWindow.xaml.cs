using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using TaskbarVisu.Core;
using TaskbarVisu.Modules;
using TaskbarVisu.Taskbar;

namespace TaskbarVisu.UI;

/// <summary>
/// Fenêtre de réglages (style Windows 11 via le thème Fluent de WPF).
/// Les options des modules sont générées depuis <see cref="ModuleCatalog"/> ; tout s'applique en direct.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly int[] FpsChoices = { 30, 60, 75, 120, 144 };
    private static readonly int[] FftChoices = { 2048, 4096, 8192 };

    private readonly App _app = App.Instance;
    private readonly DispatcherTimer _statusTimer;
    private readonly HashSet<string> _expanded = new();
    private readonly Dictionary<string, Expander> _moduleExpanders = new();
    private ModuleConfig? _selected;
    private Action? _updateHandler;

    private AppSettings S => _app.Settings;

    public SettingsWindow()
    {
        InitializeComponent();
        RebuildAll();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            if (_welcomeStep >= 0) _app.ShowGuideMark(Rendering.GuideMark.None);
            if (_updateHandler is not null) _app.UpdateStateChanged -= _updateHandler;
        };
        UpdateStatus();
    }

    private void Changed() => _app.OnSettingsChanged();

    /// <summary>(Re)construit tout le contenu de la fenêtre dans la langue courante.</summary>
    private void RebuildAll()
    {
        Title = Loc.T("TaskbarVisu — Paramètres");
        TabGeneral.Header = Loc.T("Général");
        TabAppearance.Header = Loc.T("Apparence");
        TabLayout.Header = Loc.T("Disposition");
        TabModules.Header = Loc.T("Modules");
        ResetAllButton.Content = Loc.T("Tout réinitialiser");
        CloseButton.Content = Loc.T("Fermer");

        BuildGeneral();
        BuildAppearance();
        BuildLayout();
        BuildModules();
        UpdateStatus();
    }

    // ---------------------------------------------------------------- Général

    private void BuildGeneral()
    {
        var p = GeneralPanel;
        p.Children.Clear();

        p.Children.Add(Section("Démarrage"));
        AddToggle(p, "Démarrer avec Windows", S.StartWithWindows, v =>
        {
            S.StartWithWindows = v;
            StartupManager.Sync(v);
        });
        AddToggle(p, "Fermer cette fenêtre laisse TaskbarVisu tourner dans la zone de notification",
            S.CloseToTray, v => S.CloseToTray = v);
        AddToggle(p, "Animation d'allumage des modules au lancement", S.BootAnimation, v => S.BootAnimation = v);
        var guide = new Button { Content = Loc.T("Revoir le guide de démarrage"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 4) };
        guide.Click += (_, _) => ShowWelcome();
        p.Children.Add(guide);

        BuildAudioSource(p);

        p.Children.Add(Section("Affichage"));
        // Les noms des langues restent dans leur propre langue.
        var languages = new List<string> { Loc.T("Automatique (langue du système)"), "Français", "English", "Español", "Deutsch" };
        AddCombo(p, "Langue", languages, (int)S.Language, i =>
        {
            S.Language = (Language)i;
            Loc.Set(S.Language);
            // Reconstruit toute la fenêtre dans la nouvelle langue, après la fin de l'événement en cours.
            Dispatcher.BeginInvoke(RebuildAll);
        }, translate: false);

        // Écran : principal, tous, ou un écran précis.
        var bars = TaskbarLocator.FindAll();
        var screens = new List<string> { Loc.T("Automatique (écran principal)"), Loc.T("Tous les écrans") };
        screens.AddRange(bars.Select((b, i) => b.DisplayName(i)));
        int screenIndex = S.AllMonitors ? 1 : S.MonitorDevice is null ? 0 : bars.FindIndex(b => b.DeviceName == S.MonitorDevice) + 2;
        AddCombo(p, "Écran", screens, Math.Max(0, screenIndex), i =>
        {
            S.AllMonitors = i == 1;
            S.MonitorDevice = i >= 2 ? bars[i - 2].DeviceName : null;
        }, translate: false);
        if (bars.Count > 1)
            p.Children.Add(Hint("« Tous les écrans » : les mêmes modules sur la barre des tâches de chaque écran."));

        AddToggle(p, "Masquer quand une application est en plein écran", S.HideWhenFullscreen, v => S.HideWhenFullscreen = v);

        p.Children.Add(Section("Raccourci clavier"));
        AddCombo(p, "Masquer / afficher les visualiseurs", HotkeyManager.Names, (int)S.Hotkey, i =>
        {
            S.Hotkey = (HotkeyChoice)i;
            // L'aide dit si le raccourci est libre : on la met à jour une fois le raccourci appliqué.
            Dispatcher.BeginInvoke(BuildGeneral, DispatcherPriority.Background);
        });
        var hotkeyHint = Hint(_app.HotkeyAvailable
            ? "Fonctionne partout, même en jeu. Aussi dans le menu de l'icône de la zone de notification."
            : "Ce raccourci est déjà utilisé par une autre application : choisissez-en un autre.");
        p.Children.Add(hotkeyHint);

        BuildUpdates(p);

        p.Children.Add(Section("Performances"));
        AddCombo(p, "Images par seconde", FpsChoices.Select(f => Loc.F("{0} i/s", f)).ToList(),
            NearestIndex(FpsChoices, S.Fps), i => S.Fps = FpsChoices[i], translate: false);
        AddCombo(p, "Résolution de l'analyse (FFT)",
            new[] { "2048 points (le plus léger)", "4096 points (recommandé)", "8192 points (graves plus précis)" },
            NearestIndex(FftChoices, S.FftSize), i => S.FftSize = FftChoices[i]);
        AddToggle(p, "Économie d'énergie : ne plus redessiner pendant les silences", S.PowerSaving, v => S.PowerSaving = v);
        AddSlider(p, "Vérification de la barre des tâches", 1000, 10000, 500, S.DetectionIntervalMs,
            v => $"{v / 1000:0.#} s", v => S.DetectionIntervalMs = (int)v);
        p.Children.Add(Hint("Les zones libres sont recalculées dès qu'une application s'ouvre ou se ferme ; " +
                            "cette vérification périodique rattrape le reste (icônes de notification…). Plus long = plus économe."));

        p.Children.Add(Section("Dossier"));
        var open = new Button { Content = Loc.T("Ouvrir le dossier des réglages et du journal"), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(Paths.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataDir}\"") { UseShellExecute = true });
        };
        p.Children.Add(open);
        p.Children.Add(Hint(Paths.Portable
            ? "Mode portable : réglages et journal sont dans le dossier « data » à côté de TaskbarVisu.exe."
            : "Astuce : un fichier vide « portable.txt » à côté de TaskbarVisu.exe garde les réglages dans son dossier (clé USB)."));
    }

    /// <summary>Source audio : un choix principal, et un second menu seulement quand il sert (sortie, application, entrée).</summary>
    private void BuildAudioSource(Panel p)
    {
        p.Children.Add(Section("Source audio"));
        var detail = new StackPanel();
        var help = Hint(string.Empty);

        void ShowDetail()
        {
            detail.Children.Clear();
            switch (S.AudioSource)
            {
                case AudioSourceKind.OutputDevice:
                    AddDeviceChoice(detail, "Sortie", Audio.AudioSources.Outputs());
                    help.Text = Loc.T("Utile avec plusieurs cartes son : TaskbarVisu écoute celle-ci, même si ce n'est pas la sortie par défaut.");
                    break;
                case AudioSourceKind.Input:
                    AddDeviceChoice(detail, "Entrée", Audio.AudioSources.Inputs());
                    help.Text = Loc.T("Micro, guitare, platine… branchés sur l'entrée de la carte son.");
                    break;
                case AudioSourceKind.Application:
                    AddAppChoice(detail);
                    help.Text = Loc.T("Seulement le son de cette application (FL Studio sans Discord, par exemple). Une application " +
                                      "apparaît dans la liste dès qu'elle a joué du son ; si elle est fermée, TaskbarVisu l'attend.");
                    break;
                default:
                    help.Text = Loc.T("Tout ce que vous entendez, sur la sortie par défaut (suit le changement de casque ou d'enceintes).");
                    break;
            }
        }

        AddCombo(p, "Écouter", new[] { "Tout le son du PC", "Une sortie audio précise", "Une seule application", "Une entrée (micro, ligne)" },
            (int)S.AudioSource, i =>
            {
                S.AudioSource = (AudioSourceKind)i;
                ShowDetail();
            });
        p.Children.Add(detail);
        p.Children.Add(help);
        ShowDetail();
    }

    private void AddDeviceChoice(Panel panel, string label, List<Audio.AudioEndpoint> devices)
    {
        // Le périphérique enregistré reste proposé même débranché.
        if (S.AudioDeviceId is { } id && devices.All(d => d.Id != id))
            devices.Insert(0, new Audio.AudioEndpoint(id, Loc.F("{0} (débranché)", S.AudioDeviceName ?? "?")));
        if (devices.Count == 0)
        {
            panel.Children.Add(Hint("Aucun périphérique trouvé."));
            return;
        }
        int index = Math.Max(0, devices.FindIndex(d => d.Id == S.AudioDeviceId));
        if (S.AudioDeviceId is null || devices.All(d => d.Id != S.AudioDeviceId))
        {
            S.AudioDeviceId = devices[index].Id;
            S.AudioDeviceName = devices[index].Name;
            Changed();
        }
        AddCombo(panel, label, devices.Select(d => d.Name).ToList(), index, i =>
        {
            S.AudioDeviceId = devices[i].Id;
            S.AudioDeviceName = devices[i].Name;
        }, translate: false);
    }

    private void AddAppChoice(Panel panel)
    {
        var apps = Audio.AudioSources.Apps();
        // L'application enregistrée reste proposée même si elle n'est pas lancée.
        if (S.AudioApp is { } saved && apps.All(a => !a.ProcessName.Equals(saved, StringComparison.OrdinalIgnoreCase)))
            apps.Insert(0, new Audio.AudioApp(saved, Loc.F("{0} (pas lancée)", saved)));

        var row = new StackPanel();
        if (apps.Count == 0)
        {
            row.Children.Add(Hint("Aucune application n'utilise le son pour l'instant : lancez de la musique puis cliquez sur Actualiser."));
        }
        else
        {
            int index = Math.Max(0, apps.FindIndex(a => a.ProcessName.Equals(S.AudioApp, StringComparison.OrdinalIgnoreCase)));
            if (!apps[index].ProcessName.Equals(S.AudioApp, StringComparison.OrdinalIgnoreCase))
            {
                S.AudioApp = apps[index].ProcessName;
                Changed();
            }
            AddCombo(row, "Application", apps.Select(a => a.DisplayName).ToList(), index,
                i => S.AudioApp = apps[i].ProcessName, translate: false);
        }
        var refresh = new Button { Content = Loc.T("Actualiser la liste"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(250, 2, 0, 4) };
        refresh.Click += (_, _) =>
        {
            panel.Children.Clear();
            AddAppChoice(panel);
        };
        row.Children.Add(refresh);
        panel.Children.Add(row);
    }

    /// <summary>Mises à jour : état, vérification à la demande, installation en un clic.</summary>
    private void BuildUpdates(Panel p)
    {
        p.Children.Add(Section("Mises à jour"));
        AddToggle(p, "Vérifier les nouvelles versions (GitHub)", S.CheckUpdates, v => S.CheckUpdates = v);
        var status = new TextBlock { Margin = new Thickness(0, 4, 0, 6), TextWrapping = TextWrapping.Wrap };
        var buttons = new WrapPanel();
        p.Children.Add(status);
        p.Children.Add(buttons);

        void Refresh()
        {
            status.Text = _app.UpdateStatus.Length > 0 ? _app.UpdateStatus : Loc.F("Version installée : {0}", UpdateChecker.CurrentText);
            buttons.Children.Clear();
            var check = new Button { Content = Loc.T("Vérifier maintenant"), Margin = new Thickness(0, 0, 8, 0) };
            check.Click += async (_, _) => await _app.CheckForUpdatesAsync(manual: true);
            buttons.Children.Add(check);
            if (_app.AvailableUpdate is { } update)
            {
                var install = new Button
                {
                    Content = UpdateChecker.IsInstalled ? Loc.F("Installer la version {0}", update.Version) : Loc.T("Ouvrir la page de téléchargement"),
                    Margin = new Thickness(0, 0, 8, 0),
                };
                install.Click += async (_, _) => await _app.InstallUpdateAsync();
                var skip = new Button { Content = Loc.T("Ignorer cette version") };
                skip.Click += (_, _) =>
                {
                    S.SkippedVersion = update.Tag;
                    Changed();
                    skip.IsEnabled = false;
                };
                buttons.Children.Add(install);
                buttons.Children.Add(skip);
            }
        }

        Refresh();
        // Une seule abonnement à la fois (l'onglet est reconstruit au changement de langue).
        if (_updateHandler is not null) _app.UpdateStateChanged -= _updateHandler;
        _updateHandler = () => Dispatcher.BeginInvoke(Refresh);
        _app.UpdateStateChanged += _updateHandler;
        p.Children.Add(Hint(UpdateChecker.IsInstalled
            ? "La mise à jour s'installe toute seule puis TaskbarVisu redémarre. Vos réglages sont conservés."
            : "Version portable : la mise à jour ouvre la page de téléchargement."));
    }

    // ---------------------------------------------------------------- Apparence

    /// <summary>Palettes dans l'ordre d'affichage.</summary>
    private static readonly (PaletteChoice Choice, string Name)[] Palettes =
    {
        (PaletteChoice.Magma, "Magma"),
        (PaletteChoice.Brasier, "Brasier"),
        (PaletteChoice.Spectral, "Spectrale"),
        (PaletteChoice.Phosphore, "Phosphore"),
        (PaletteChoice.Glace, "Glace"),
        (PaletteChoice.Rainbow, "Arc-en-ciel RGB"),
        (PaletteChoice.RainbowAnimated, "Arc-en-ciel animé"),
        (PaletteChoice.Synthwave, "Synthwave"),
        (PaletteChoice.Vaporwave, "Vaporwave"),
        (PaletteChoice.Toxic, "Toxique"),
        (PaletteChoice.Chrome, "Chrome Y2K"),
        (PaletteChoice.Vfd, "VFD (tube fluorescent)"),
        (PaletteChoice.Lcd, "LCD (pour fond LCD)"),
        (PaletteChoice.Nixie, "Nixie (tubes orange)"),
        (PaletteChoice.Monochrome, "Monochrome"),
        (PaletteChoice.Accent, "Accentuation Windows"),
        (PaletteChoice.Custom, "Personnalisée"),
    };

    private static int PaletteIndex(PaletteChoice choice) => Math.Max(0, Array.FindIndex(Palettes, x => x.Choice == choice));

    /// <summary>Choix de l'encre, dans l'ordre d'affichage.</summary>
    private static readonly (ColorChoice Choice, string Name)[] Inks =
    {
        (ColorChoice.Default, "Blanc lavande (par défaut)"),
        (ColorChoice.Palette, "Teintée par la palette"),
        (ColorChoice.Accent, "Accentuation Windows"),
        (ColorChoice.Custom, "Personnalisée"),
    };

    private void BuildAppearance()
    {
        var p = AppearancePanel;
        p.Children.Clear();

        // Préréglages : un clic règle tout le style d'un coup.
        p.Children.Add(Section("Préréglages"));
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var preset in StylePresets.All)
        {
            var button = new Button
            {
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 8, 10, 8),
                Content = new StackPanel
                {
                    Children =
                    {
                        PresetPreview(preset, 116, 36),
                        new TextBlock { Text = Loc.T(preset.Name), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            button.Click += (_, _) =>
            {
                StylePresets.Apply(S, preset);
                BuildAppearance();
                Changed();
            };
            presets.Children.Add(button);
        }
        p.Children.Add(presets);
        p.Children.Add(Hint("Un préréglage change la palette, la police, les effets, le fond et les couleurs. " +
                            "Tout reste ajustable ci-dessous ; les couleurs propres à un module sont conservées."));

        p.Children.Add(Section("Style"));
        AddToggle(p, "Police pixel (matrice de points 5×7, façon afficheur)", S.PixelFont, v => S.PixelFont = v);
        AddToggle(p, "Graduations, étiquettes et repères techniques", S.ShowScales, v => S.ShowScales = v);
        AddToggle(p, "Effet écran à balayage (scanlines)", S.Scanlines, v => S.Scanlines = v);
        AddCombo(p, "Fond des modules", new[] { "Aucun (fondu dans la barre)", "Sombre translucide", "Noir", "Écran LCD (fond clair olive)" },
            (int)S.Background, i => S.Background = (BackgroundChoice)i);
        AddSlider(p, "Opacité", 20, 100, 5, S.Opacity * 100, v => $"{v:0} %", v => S.Opacity = v / 100);

        p.Children.Add(Section("Couleurs"));
        AddCombo(p, "Thème", new[] { "Automatique (suit la barre des tâches)", "Sombre", "Clair" },
            (int)S.Theme, i => S.Theme = (ThemeChoice)i);

        // Palette des fréquences (grave → aigu), avec aperçu du dégradé ; « Personnalisée » ouvre l'éditeur de dégradé.
        var customPaletteRow = GradientRow("Dégradé personnalisé (grave → aigu)", S.Gradient, g => S.Gradient = g);
        void ShowCustomPalette() => customPaletteRow.Visibility =
            S.Palette == PaletteChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
        AddCombo(p, "Palette générale", PaletteOptions(), PaletteIndex(S.Palette), i =>
        {
            S.Palette = Palettes[i].Choice;
            ShowCustomPalette();
        }, PaletteTemplate());
        ShowCustomPalette();
        p.Children.Add(customPaletteRow);
        p.Children.Add(Hint("La palette colore les données (fréquences, niveaux) de tous les modules. " +
                            "Chaque module peut avoir ses propres couleurs dans l'onglet Modules."));

        var inkPicker = new ColorPickerButton(S.CustomColor);
        inkPicker.ColorChanged += hex => { S.CustomColor = hex; Changed(); };
        var inkRow = Labeled("Couleur personnalisée", inkPicker);
        void ShowInk() => inkRow.Visibility = S.ColorMode == ColorChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
        AddCombo(p, "Textes et repères", Inks.Select(x => x.Name).ToList(),
            Math.Max(0, Array.FindIndex(Inks, x => x.Choice == S.ColorMode)), i =>
            {
                S.ColorMode = Inks[i].Choice;
                ShowInk();
            });
        ShowInk();
        p.Children.Add(inkRow);
        p.Children.Add(Hint("Textes et repères : chiffres, lettres, graduations, grilles et crêtes maintenues. " +
                            "Les données (barres, courbes, points) suivent la palette."));

        BuildStyleSharing(p);
    }

    /// <summary>Partager un style : code court à coller (Discord…) ou fichier .tbvstyle.</summary>
    private void BuildStyleSharing(Panel p)
    {
        p.Children.Add(Section("Partager un style"));
        var buttons = new WrapPanel();
        var feedback = new TextBlock { Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };

        void Applied(bool ok)
        {
            if (ok)
            {
                BuildAppearance();
                BuildModules();
                Changed();
            }
            else
            {
                feedback.Text = Loc.T("Ce n'est pas un style TaskbarVisu valide.");
            }
        }

        Button Make(string text, Action action)
        {
            var b = new Button { Content = Loc.T(text), Margin = new Thickness(0, 0, 8, 6) };
            b.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // Presse-papiers occupé par une autre application, fichier illisible…
                    Log.Error("Partage du style", ex);
                    feedback.Text = Loc.T("Opération impossible pour le moment, réessayez.");
                }
            };
            buttons.Children.Add(b);
            return b;
        }

        Make("Copier le code du style", () =>
        {
            Clipboard.SetText(StyleCode.ToCode(S));
            feedback.Text = Loc.T("Code copié : collez-le où vous voulez (Discord, message…).");
        });
        Make("Coller un code", () => Applied(Clipboard.ContainsText() && StyleCode.TryApply(Clipboard.GetText(), S)));
        Make("Enregistrer dans un fichier…", () =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = Loc.T("Style TaskbarVisu") + " (*.tbvstyle)|*.tbvstyle", FileName = "style.tbvstyle" };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, StyleCode.ToJson(S));
            feedback.Text = Loc.T("Style enregistré.");
        });
        Make("Ouvrir un fichier…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Style TaskbarVisu") + " (*.tbvstyle)|*.tbvstyle|*.*|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            Applied(StyleCode.TryApply(File.ReadAllText(dialog.FileName), S));
        });
        p.Children.Add(buttons);
        p.Children.Add(feedback);
        p.Children.Add(Hint("Le style comprend la palette, les effets, le fond et les couleurs de chaque module (pas la disposition)."));
    }

    private sealed record PaletteOption(string Name, Brush Preview);

    private List<PaletteOption> PaletteOptions() =>
        Palettes.Select(x => new PaletteOption(Loc.T(x.Name), PaletteBrush(x.Choice))).ToList();

    /// <summary>Aperçu horizontal d'une palette (grave → aigu).</summary>
    private Brush PaletteBrush(PaletteChoice palette, List<ColorStop>? gradient = null)
    {
        var theme = Rendering.Theme.Build(S, palette, null, gradient);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 8; i++)
        {
            var c = theme.Spectral(i / 8f);
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(c.Red, c.Green, c.Blue), i / 8.0));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>Ligne « dégradé personnalisé » : éditeur + mode d'emploi en une ligne.</summary>
    private FrameworkElement GradientRow(string label, List<ColorStop> stops, Action<List<ColorStop>> set)
    {
        var sources = Palettes.Where(x => x.Choice != PaletteChoice.Custom).ToArray();
        var options = sources.Select(x => new PaletteOption(Loc.T(x.Name), PaletteBrush(x.Choice))).ToList();
        var editor = new GradientEditor(stops, g =>
        {
            set(g);
            Changed();
        }, options, PaletteTemplate(), i => Rendering.Theme.ToGradient(S, sources[i].Choice));
        var body = new StackPanel { Children = { editor } };
        body.Children.Add(Hint("Cliquez sur la barre pour ajouter une couleur (8 au plus), glissez les repères pour les déplacer, clic droit pour en retirer."));
        var row = Labeled(label, body);
        if (row.Children[0] is TextBlock text)
        {
            text.VerticalAlignment = VerticalAlignment.Top;
            text.Margin = new Thickness(0, 6, 12, 0);
        }
        return row;
    }

    /// <summary>Élément de liste : aperçu du dégradé + nom.</summary>
    private static DataTemplate PaletteTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var swatch = new FrameworkElementFactory(typeof(Border));
        swatch.SetValue(WidthProperty, 90.0);
        swatch.SetValue(HeightProperty, 12.0);
        swatch.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        swatch.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        swatch.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(PaletteOption.Preview)));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.MarginProperty, new Thickness(10, 0, 0, 0));
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PaletteOption.Name)));
        panel.AppendChild(swatch);
        panel.AppendChild(text);
        return new DataTemplate { VisualTree = panel };
    }

    // ---------------------------------------------------------------- Modules

    private void BuildModules()
    {
        var p = ModulesPanel;
        p.Children.Clear();
        p.Children.Add(Hint("Réglages propres à chaque module. Les changements s'appliquent immédiatement sur la barre des tâches."));
        _moduleExpanders.Clear();

        var ordered = S.Left.Select(c => (Cfg: c, Side: "zone gauche")).Concat(S.Right.Select(c => (Cfg: c, Side: "zone droite")));
        foreach (var (cfg, side) in ordered)
        {
            var info = ModuleCatalog.Find(cfg.Id)!;
            var body = new StackPanel { Margin = new Thickness(8, 4, 0, 10) };
            body.Children.Add(Hint(info.Description));
            AddModuleColors(body, cfg);
            foreach (var opt in info.Options.Where(o => !o.Advanced)) AddOption(body, cfg, opt);

            // Les réglages fins sont rangés à part pour garder l'essentiel lisible.
            var advanced = info.Options.Where(o => o.Advanced).ToList();
            if (advanced.Count > 0)
            {
                var more = new StackPanel { Margin = new Thickness(4, 2, 0, 0) };
                foreach (var opt in advanced) AddOption(more, cfg, opt);
                string moreKey = cfg.Id + ".more";
                var moreExpander = new Expander
                {
                    Header = Loc.T("Plus d'options"),
                    Content = more,
                    IsExpanded = _expanded.Contains(moreKey),
                    Margin = new Thickness(0, 6, 0, 0),
                };
                moreExpander.Expanded += (_, _) => _expanded.Add(moreKey);
                moreExpander.Collapsed += (_, _) => _expanded.Remove(moreKey);
                body.Children.Add(moreExpander);
            }

            var reset = new Button { Content = Loc.T("Réinitialiser ce module"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
            reset.Click += (_, _) =>
            {
                cfg.Options.Clear();
                cfg.Colors = ModuleColorMode.Global;
                cfg.Gradient = null;
                BuildModules();
                Changed();
            };
            body.Children.Add(reset);

            var expander = new Expander
            {
                Header = $"{Loc.T(info.Name)}   ·   {Loc.T(side)}{(cfg.Enabled ? string.Empty : "   ·   " + Loc.T("masqué"))}",
                Content = body,
                IsExpanded = _expanded.Contains(cfg.Id),
                Margin = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            expander.Expanded += (_, _) => _expanded.Add(cfg.Id);
            expander.Collapsed += (_, _) => _expanded.Remove(cfg.Id);
            _moduleExpanders[cfg.Id] = expander;
            p.Children.Add(expander);
        }
    }

    /// <summary>Ligne « Couleurs » d'un module : générales, une autre palette, ou une couleur unie.</summary>
    private void AddModuleColors(Panel panel, ModuleConfig cfg)
    {
        var palettePanel = new StackPanel();
        // Palette « Personnalisée » : dégradé propre au module (au départ, celui de l'apparence générale).
        var gradientRow = GradientRow("Dégradé du module", cfg.Gradient ?? S.Gradient, g => cfg.Gradient = g);
        void ShowGradient() => gradientRow.Visibility =
            cfg.ColorPalette == PaletteChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
        AddCombo(palettePanel, "Palette du module", PaletteOptions(), PaletteIndex(cfg.ColorPalette), i =>
        {
            cfg.ColorPalette = Palettes[i].Choice;
            ShowGradient();
        }, PaletteTemplate());
        palettePanel.Children.Add(gradientRow);
        ShowGradient();

        var picker = new ColorPickerButton(cfg.ColorSolid);
        picker.ColorChanged += hex => { cfg.ColorSolid = hex; Changed(); };
        var solidRow = Labeled("Couleur du module", picker);

        void Show()
        {
            palettePanel.Visibility = cfg.Colors == ModuleColorMode.Palette ? Visibility.Visible : Visibility.Collapsed;
            solidRow.Visibility = cfg.Colors == ModuleColorMode.Solid ? Visibility.Visible : Visibility.Collapsed;
        }

        AddCombo(panel, "Couleurs", new[] { "Comme l'apparence générale", "Autre palette", "Couleur unie" },
            (int)cfg.Colors, i =>
            {
                cfg.Colors = (ModuleColorMode)i;
                Show();
            });
        panel.Children.Add(palettePanel);
        panel.Children.Add(solidRow);
        Show();
    }

    private void AddOption(Panel panel, ModuleConfig cfg, OptionDef opt)
    {
        switch (opt.Kind)
        {
            case OptionKind.Toggle:
                AddToggle(panel, opt.Label, cfg.Get(opt.Key) >= 0.5, v => cfg.Options[opt.Key] = v ? 1 : 0);
                break;
            case OptionKind.Choice:
                AddCombo(panel, opt.Label, opt.Choices ?? Array.Empty<string>(), (int)Math.Round(cfg.Get(opt.Key)),
                    i => cfg.Options[opt.Key] = i);
                break;
            case OptionKind.Color:
                int rgb = (int)Math.Round(cfg.Get(opt.Key));
                var picker = new ColorPickerButton($"#{rgb:X6}");
                picker.ColorChanged += hex =>
                {
                    cfg.Options[opt.Key] = Convert.ToInt32(hex.TrimStart('#'), 16);
                    Changed();
                };
                panel.Children.Add(Labeled(opt.Label, picker));
                break;
            default:
                AddSlider(panel, opt.Label, opt.Min, opt.Max, opt.Step, cfg.Get(opt.Key),
                    v => FormatValue(v, opt), v => cfg.Options[opt.Key] = v);
                break;
        }
        if (opt.Help is not null)
        {
            var help = Hint(opt.Help);
            help.Margin = new Thickness(opt.Kind == OptionKind.Toggle ? 28 : 0, -2, 0, 6);
            help.FontSize = 12;
            panel.Children.Add(help);
        }
    }

    private static string FormatValue(double v, OptionDef opt)
    {
        string number = opt.Step < 1 ? v.ToString("0.0#", CultureInfo.CurrentCulture) : v.ToString("0", CultureInfo.CurrentCulture);
        return opt.Unit is null ? number : $"{number} {opt.Unit}";
    }

    // ---------------------------------------------------------------- Pied de fenêtre

    private void UpdateStatus()
    {
        var bar = _app.Tracker.Current;
        string barText = !bar.Valid || !bar.Supported ? Loc.T(bar.Message)
            : S.SingleGap == SingleGapMode.Center && !bar.Spanning.IsEmpty ? Loc.F("Espace libre au centre : {0} px", bar.Spanning.Width)
            : Loc.F("Zones libres : gauche {0} px · droite {1} px", bar.Left.Width, bar.Right.Width);
        if (_app.ScreenCount > 1) barText += "   ·   " + Loc.F("{0} écrans", _app.ScreenCount);
        if (_app.VisualsHidden) barText = Loc.T("Masqués (raccourci) · ") + barText;
        RefreshMiniIfBarChanged();
        RefreshWelcomeZones();
        StatusText.Text = (_app.IsPaused ? Loc.T("En pause · ") : string.Empty)
                          + Loc.F("Audio : {0}", _app.Audio.StatusText) + "   ·   " + barText;
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, Loc.T("Remettre tous les réglages et la disposition par défaut ?"),
            "TaskbarVisu", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var fresh = AppSettings.CreateDefault();
        fresh.StartWithWindows = S.StartWithWindows;
        fresh.CloseToTray = S.CloseToTray;
        fresh.Language = S.Language;
        _app.Store.Replace(fresh);
        _selected = null;
        RebuildAll();
        Changed();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- Fabrique de contrôles

    private static TextBlock Section(string title) => new()
    {
        Text = Loc.T(title),
        FontSize = 18,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 6),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = Loc.T(text),
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 8),
    };

    private static Grid Labeled(string label, FrameworkElement control, FrameworkElement? trailing = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(trailing is null ? 0 : 90) });

        var text = new TextBlock
        {
            Text = Loc.T(label),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
        };
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }
        return grid;
    }

    private void AddToggle(Panel panel, string label, bool value, Action<bool> set)
    {
        var check = new CheckBox { Content = Loc.T(label), IsChecked = value, Margin = new Thickness(0, 5, 0, 5) };
        check.Checked += (_, _) => { set(true); Changed(); };
        check.Unchecked += (_, _) => { set(false); Changed(); };
        panel.Children.Add(check);
    }

    private void AddSlider(Panel panel, string label, double min, double max, double step, double value,
        Func<double, string> format, Action<double> set)
    {
        var valueText = new TextBlock
        {
            Text = format(value),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Opacity = 0.8,
        };
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            SmallChange = step,
            LargeChange = step * 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = format(e.NewValue);
            set(e.NewValue);
            Changed();
        };
        panel.Children.Add(Labeled(label, slider, valueText));
    }

    private void AddCombo(Panel panel, string label, System.Collections.IList items, int index, Action<int> set,
        DataTemplate? template = null, bool translate = true)
    {
        // Les listes de textes sont traduites ici (sauf celles déjà mises en forme par l'appelant).
        if (translate && items is IEnumerable<string> texts) items = texts.Select(Loc.T).ToList();
        var combo = new ComboBox
        {
            ItemsSource = items,
            ItemTemplate = template,
            SelectedIndex = Math.Clamp(index, 0, Math.Max(0, items.Count - 1)),
            MinWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            set(combo.SelectedIndex);
            Changed();
        };
        panel.Children.Add(Labeled(label, combo));
    }

    private static int NearestIndex(int[] values, int value)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
            if (Math.Abs(values[i] - value) < Math.Abs(values[best] - value)) best = i;
        return best;
    }
}
