using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarVisu.Core;
using TaskbarVisu.Modules;
using TaskbarVisu.Taskbar;

namespace TaskbarVisu.UI;

/// <summary>
/// Fenêtre de réglages façon Paramètres de Windows 11 (thème Fluent de WPF) : navigation à gauche
/// (Accueil, Apparence, Disposition, Modules, Général), réglages en cartes. Tout s'applique en direct.
/// Les options des modules sont générées depuis <see cref="ModuleCatalog"/>.
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
        Tabs.SelectionChanged += OnPageChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        // Windows passe en clair ou en sombre (ou change de couleur d'accentuation) : les aperçus et liserés suivent.
        _themeKey = ThemeKey();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _statusTimer.Stop();
            if (_welcomeStep >= 0) _app.ShowGuideMark(Rendering.GuideMark.None);
            if (_updateHandler is not null) _app.UpdateStateChanged -= _updateHandler;
        };
        UpdateStatus();
    }

    private void Changed() => _app.OnSettingsChanged();

    private string _themeKey = string.Empty;

    private static string ThemeKey() =>
        $"{App.UiIsLight}/{App.TaskbarIsLight}/{Rendering.Theme.AccentColor(App.UiIsLight)}";

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            // L'événement arrive pour beaucoup d'autres réglages : on ne reconstruit que si le thème a changé.
            var key = ThemeKey();
            if (key == _themeKey) return;
            _themeKey = key;
            RebuildAll();
            if (_welcomeStep >= 0) BuildWelcome();
        }, DispatcherPriority.Background);

    /// <summary>(Re)construit tout le contenu de la fenêtre dans la langue courante.</summary>
    private void RebuildAll()
    {
        Title = Loc.T("TaskbarVisu — Paramètres");
        VersionText.Text = Loc.F("Version {0}", UpdateChecker.CurrentText);
        TabHome.Header = NavHeader("", "Accueil");
        TabAppearance.Header = NavHeader("", "Apparence");
        TabLayout.Header = NavHeader("", "Disposition");
        TabModules.Header = NavHeader("", "Modules");
        TabGeneral.Header = NavHeader("", "Général");

        BuildHome();
        BuildGeneral();
        BuildAppearance();
        BuildLayout();
        BuildModules();
        UpdateStatus();
    }

    private static StackPanel NavHeader(string glyph, string text)
    {
        var icon = Glyph(glyph, 16);
        icon.Margin = new Thickness(0, 0, 14, 0);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { icon, new TextBlock { Text = Loc.T(text), FontSize = 14, VerticalAlignment = VerticalAlignment.Center } },
        };
    }

    /// <summary>Une page reflète toujours les réglages actuels quand on l'ouvre (un autre onglet a pu les changer).</summary>
    private void OnPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedItem == TabHome) BuildHome();
        else if (Tabs.SelectedItem == TabAppearance) BuildAppearance();
        else if (Tabs.SelectedItem == TabModules) BuildModules();
        else if (Tabs.SelectedItem == TabLayout) BuildLayout();
    }

    private void GoTo(TabItem page) => Tabs.SelectedItem = page;

    // ---------------------------------------------------------------- Général

    private void BuildGeneral()
    {
        var p = GeneralPanel;
        p.Children.Clear();
        PageHeader(p, "Général", "Ce que TaskbarVisu écoute, son démarrage, son affichage et ses mises à jour.");

        // Du plus utile au plus technique ; les réglages fins sont repliés en bas.
        BuildAudioSource(p);

        p.Children.Add(Section("Démarrage"));
        AddToggle(p, "Démarrer avec Windows", S.StartWithWindows, v =>
        {
            S.StartWithWindows = v;
            StartupManager.Sync(v);
        }, "TaskbarVisu se lance discrètement à l'ouverture de la session.", "");
        AddToggle(p, "Continuer en arrière-plan", S.CloseToTray, v => S.CloseToTray = v,
            "Fermer cette fenêtre laisse les visualiseurs tourner ; l'icône reste dans la zone de notification.", "");
        AddToggle(p, "Animation d'allumage", S.BootAnimation, v => S.BootAnimation = v,
            "Petit auto-test des segments au lancement.", "");
        ActionCard(p, "Guide de démarrage", "Revoir le guide", ShowWelcome, "Les cinq étapes du premier lancement, à rejouer quand vous voulez.", "");

        p.Children.Add(Section("Affichage"));
        // Les noms des langues restent dans leur propre langue.
        var languages = new List<string> { Loc.T("Automatique (langue du système)"), "Français", "English", "Español", "Deutsch" };
        AddCombo(p, "Langue", languages, (int)S.Language, i =>
        {
            S.Language = (Language)i;
            Loc.Set(S.Language);
            // Reconstruit toute la fenêtre dans la nouvelle langue, après la fin de l'événement en cours.
            Dispatcher.BeginInvoke(RebuildAll);
        }, translate: false, icon: "");

        // Écran : principal, tous, ou un écran précis.
        var bars = TaskbarLocator.FindAll();
        var screens = new List<string> { Loc.T("Automatique (écran principal)"), Loc.T("Tous les écrans") };
        screens.AddRange(bars.Select((b, i) => b.DisplayName(i)));
        int screenIndex = S.AllMonitors ? 1 : S.MonitorDevice is null ? 0 : bars.FindIndex(b => b.DeviceName == S.MonitorDevice) + 2;
        AddCombo(p, "Écran", screens, Math.Max(0, screenIndex), i =>
        {
            S.AllMonitors = i == 1;
            S.MonitorDevice = i >= 2 ? bars[i - 2].DeviceName : null;
        }, translate: false, icon: "");
        if (bars.Count > 1)
            p.Children.Add(Hint("« Tous les écrans » : les mêmes modules sur la barre des tâches de chaque écran."));

        AddToggle(p, "Masquer en plein écran", S.HideWhenFullscreen, v => S.HideWhenFullscreen = v,
            "Jeux, vidéos, présentations : les visualiseurs s'effacent tant qu'une application est en plein écran.", "");

        p.Children.Add(Section("Raccourci clavier"));
        AddCombo(p, "Masquer / afficher les visualiseurs", HotkeyManager.Names, (int)S.Hotkey, i =>
        {
            S.Hotkey = (HotkeyChoice)i;
            // L'aide dit si le raccourci est libre : on la met à jour une fois le raccourci appliqué.
            Dispatcher.BeginInvoke(() => RebuildKeepingScroll(GeneralPanel, BuildGeneral), DispatcherPriority.Background);
        }, icon: "");
        p.Children.Add(Hint(_app.HotkeyAvailable
            ? "Fonctionne partout, même en jeu. Aussi dans le menu de l'icône de la zone de notification."
            : "Ce raccourci est déjà utilisé par une autre application : choisissez-en un autre."));

        BuildUpdates(p);

        // Réglages techniques : repliés, les valeurs par défaut conviennent à presque tout le monde.
        var advanced = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        p.Children.Add(Folded("general.advanced", "Avancé : performances, dossier des réglages, réinitialisation", advanced, new Thickness(0, 22, 0, 0)));
        AddCombo(advanced, "Images par seconde", FpsChoices.Select(f => Loc.F("{0} i/s", f)).ToList(),
            NearestIndex(FpsChoices, S.Fps), i => S.Fps = FpsChoices[i], translate: false,
            description: "60 i/s est fluide ; plus haut seulement pour un écran rapide.", icon: "");
        AddCombo(advanced, "Résolution de l'analyse (FFT)",
            new[] { "2048 points (le plus léger)", "4096 points (recommandé)", "8192 points (graves plus précis)" },
            NearestIndex(FftChoices, S.FftSize), i => S.FftSize = FftChoices[i], icon: "");
        AddToggle(advanced, "Économie d'énergie", S.PowerSaving, v => S.PowerSaving = v,
            "Ne plus redessiner pendant les silences.", "");
        AddSlider(advanced, "Vérification de la barre des tâches", 1000, 10000, 500, S.DetectionIntervalMs,
            v => $"{v / 1000:0.#} s", v => S.DetectionIntervalMs = (int)v,
            "Les zones libres sont recalculées dès qu'une application s'ouvre ou se ferme ; cette vérification " +
            "périodique rattrape le reste (icônes de notification…). Plus long = plus économe.", "");
        ActionCard(advanced, "Dossier des réglages et du journal", "Ouvrir", () =>
        {
            Directory.CreateDirectory(Paths.DataDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataDir}\"") { UseShellExecute = true });
        }, Paths.Portable
            ? "Mode portable : réglages et journal sont dans le dossier « data » à côté de TaskbarVisu.exe."
            : "Astuce : un fichier vide « portable.txt » à côté de TaskbarVisu.exe garde les réglages dans son dossier (clé USB).", "");
        ActionCard(advanced, "Tout réinitialiser", "Réinitialiser…", ResetAll,
            "Remet tous les réglages et la disposition par défaut (la langue et le démarrage sont conservés).", "");

        MergeHints(p);
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
            MergeHints(detail);
        }

        AddCombo(p, "Écouter", new[] { "Tout le son du PC", "Une sortie audio précise", "Une seule application", "Une entrée (micro, ligne)" },
            (int)S.AudioSource, i =>
            {
                S.AudioSource = (AudioSourceKind)i;
                ShowDetail();
            }, icon: "");
        p.Children.Add(help);
        p.Children.Add(detail);
        ShowDetail();
    }

    private void AddDeviceChoice(Panel panel, string label, List<Audio.AudioEndpoint> devices)
    {
        // Le périphérique enregistré reste proposé même débranché.
        if (S.AudioDeviceId is { } id && devices.All(d => d.Id != id))
            devices.Insert(0, new Audio.AudioEndpoint(id, Loc.F("{0} (débranché)", S.AudioDeviceName ?? "?")));
        if (devices.Count == 0)
        {
            panel.Children.Add(Card("Aucun périphérique trouvé.", null, ""));
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
        }, translate: false, icon: "");
    }

    private void AddAppChoice(Panel panel)
    {
        var apps = Audio.AudioSources.Apps();
        // L'application enregistrée reste proposée même si elle n'est pas lancée.
        if (S.AudioApp is { } saved && apps.All(a => !a.ProcessName.Equals(saved, StringComparison.OrdinalIgnoreCase)))
            apps.Insert(0, new Audio.AudioApp(saved, Loc.F("{0} (pas lancée)", saved)));

        if (apps.Count > 0)
        {
            int index = Math.Max(0, apps.FindIndex(a => a.ProcessName.Equals(S.AudioApp, StringComparison.OrdinalIgnoreCase)));
            if (!apps[index].ProcessName.Equals(S.AudioApp, StringComparison.OrdinalIgnoreCase))
            {
                S.AudioApp = apps[index].ProcessName;
                Changed();
            }
            AddCombo(panel, "Application", apps.Select(a => a.DisplayName).ToList(), index,
                i => S.AudioApp = apps[i].ProcessName, translate: false, icon: "");
        }
        ActionCard(panel, apps.Count == 0 ? "Aucune application n'utilise le son pour l'instant" : "L'application n'est pas dans la liste ?",
            "Actualiser la liste", () =>
            {
                panel.Children.Clear();
                AddAppChoice(panel);
                MergeHints(panel);
            }, "Lancez de la musique dans l'application, puis actualisez.", "");
    }

    /// <summary>Mises à jour : état, vérification à la demande, installation en un clic.</summary>
    private void BuildUpdates(Panel p)
    {
        p.Children.Add(Section("Mises à jour"));
        AddToggle(p, "Vérifier automatiquement", S.CheckUpdates, v => S.CheckUpdates = v,
            "Cherche les nouvelles versions sur GitHub au lancement.", "");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var card = Card(string.Empty, buttons, "", UpdateChecker.IsInstalled
            ? "La mise à jour s'installe toute seule puis TaskbarVisu redémarre. Vos réglages sont conservés."
            : "Version portable : la mise à jour ouvre la page de téléchargement.");
        var title = (TextBlock)((CardParts)card.Tag).Texts.Children[0];
        p.Children.Add(card);

        void Refresh()
        {
            title.Text = _app.UpdateStatus.Length > 0 ? _app.UpdateStatus : Loc.F("Version installée : {0}", UpdateChecker.CurrentText);
            buttons.Children.Clear();
            if (_app.AvailableUpdate is { } update)
            {
                var install = new Button
                {
                    Content = UpdateChecker.IsInstalled ? Loc.F("Installer la version {0}", update.Version) : Loc.T("Ouvrir la page de téléchargement"),
                    Margin = new Thickness(0, 0, 8, 0),
                    Style = TryFindResource("AccentButtonStyle") as Style,
                };
                install.Click += async (_, _) => await _app.InstallUpdateAsync();
                var skip = new Button { Content = Loc.T("Ignorer cette version"), Margin = new Thickness(0, 0, 8, 0) };
                skip.Click += (_, _) =>
                {
                    S.SkippedVersion = update.Tag;
                    Changed();
                    skip.IsEnabled = false;
                };
                buttons.Children.Add(install);
                buttons.Children.Add(skip);
            }
            var check = new Button { Content = Loc.T("Vérifier maintenant") };
            check.Click += async (_, _) => await _app.CheckForUpdatesAsync(manual: true);
            buttons.Children.Add(check);
        }

        Refresh();
        // Un seul abonnement à la fois (l'onglet est reconstruit au changement de langue).
        if (_updateHandler is not null) _app.UpdateStateChanged -= _updateHandler;
        _updateHandler = () => Dispatcher.BeginInvoke(Refresh);
        _app.UpdateStateChanged += _updateHandler;
    }

    // ---------------------------------------------------------------- Apparence

    /// <summary>Palettes dans l'ordre d'affichage (une seule palette RGB : son animation se règle à part).</summary>
    private static readonly (PaletteChoice Choice, string Name)[] Palettes =
    {
        (PaletteChoice.Magma, "Magma"),
        (PaletteChoice.Brasier, "Brasier"),
        (PaletteChoice.Spectral, "Spectrale"),
        (PaletteChoice.Phosphore, "Phosphore"),
        (PaletteChoice.Glace, "Glace"),
        (PaletteChoice.RainbowAnimated, "RGB (arc-en-ciel)"),
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

    private static readonly string[] AnimationNames =
    {
        "Aucune (couleurs fixes)", "Défilement vers les aigus", "Défilement vers les graves", "Une couleur à la fois",
        "Va-et-vient", "Pulsation (respiration)",
    };

    private void BuildAppearance()
    {
        var p = AppearancePanel;
        p.Children.Clear();
        PageHeader(p, "Apparence", "Choisissez un style en un clic, puis ajustez les couleurs et les effets si vous le souhaitez.");

        // 1. Préréglages : un clic règle tout le style d'un coup.
        p.Children.Add(Section("Préréglages"));
        p.Children.Add(PresetGrid(4, () => RebuildKeepingScroll(AppearancePanel, BuildAppearance)));

        // 2. Couleurs : palette des données (avec son éditeur), animation, textes et thème.
        p.Children.Add(Section("Couleurs"));
        var customPaletteRow = GradientRow("Dégradé personnalisé (grave → aigu)", S.Gradient, g => S.Gradient = g);
        AddCombo(p, "Palette des données", PaletteOptions(), PaletteIndex(S.Palette), i =>
        {
            S.Palette = Palettes[i].Choice;
            customPaletteRow.Visibility = S.Palette == PaletteChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
        }, PaletteTemplate(), description: "Colore les barres, courbes et points de tous les modules, du grave à l'aigu. " +
                                            "Un module peut avoir ses propres couleurs (page Modules).", icon: "");
        customPaletteRow.Visibility = S.Palette == PaletteChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
        p.Children.Add(customPaletteRow);
        AddAnimationControls(p);

        var inkPicker = new ColorPickerButton(S.CustomColor);
        inkPicker.ColorChanged += hex => { S.CustomColor = hex; Changed(); };
        var inkRow = Card("Couleur personnalisée", inkPicker, "");
        void ShowInk() => inkRow.Visibility = S.ColorMode == ColorChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
        AddCombo(p, "Textes et graduations", Inks.Select(x => x.Name).ToList(),
            Math.Max(0, Array.FindIndex(Inks, x => x.Choice == S.ColorMode)), i =>
            {
                S.ColorMode = Inks[i].Choice;
                ShowInk();
            }, description: "Chiffres, lettres, graduations et grilles.", icon: "");
        ShowInk();
        p.Children.Add(inkRow);
        AddCombo(p, "Clair ou sombre", new[] { "Automatique (suit la barre des tâches)", "Sombre", "Clair" },
            (int)S.Theme, i => S.Theme = (ThemeChoice)i, icon: "");

        // 3. Effets visuels.
        p.Children.Add(Section("Effets"));
        AddToggle(p, "Police pixel", S.PixelFont, v => S.PixelFont = v, "Matrice de points 5×7, façon afficheur.", "");
        AddToggle(p, "Graduations et repères", S.ShowScales, v => S.ShowScales = v, "Étiquettes, grilles et repères techniques.", "");
        AddToggle(p, "Effet écran à balayage", S.Scanlines, v => S.Scanlines = v, "Une ligne sur deux atténuée, comme un vieil écran (scanlines).", "");
        AddToggle(p, "Fond écran LCD", S.Background == BackgroundChoice.Lcd,
            v => S.Background = v ? BackgroundChoice.Lcd : BackgroundChoice.None,
            "Fond clair olive derrière chaque module, pour le style LCD. Sinon, les modules se fondent dans la barre.", "");
        AddSlider(p, "Opacité", 20, 100, 5, S.Opacity * 100, v => $"{v:0} %", v => S.Opacity = v / 100, icon: "");

        BuildStyleSharing(p);
        MergeHints(p);
    }

    /// <summary>Animation des couleurs (toutes les palettes) : effet, puis durée d'un cycle quand il y a une animation.</summary>
    private void AddAnimationControls(Panel p)
    {
        Border? speed = null;
        AddCombo(p, "Animation des couleurs", AnimationNames, (int)S.Animation, i =>
        {
            S.Animation = (ColorAnimation)i;
            if (speed is not null) speed.Visibility = S.Animation == ColorAnimation.Still ? Visibility.Collapsed : Visibility.Visible;
        }, description: "Fait bouger les couleurs de la palette, quelle qu'elle soit (la palette RGB défile par défaut).", icon: "");
        speed = AddSlider(p, "Durée d'un cycle", 2, 60, 1, S.AnimationSeconds, v => $"{v:0} s", v => S.AnimationSeconds = v,
            "Plus court = plus rapide.", "");
        speed.Visibility = S.Animation == ColorAnimation.Still ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Grille des préréglages : cartes à taille fixe, la carte du style actif est entourée.
    /// Choisir un style ne déplace rien : seul le liseré change de carte.
    /// </summary>
    private System.Windows.Controls.Primitives.UniformGrid PresetGrid(int columns, Action? applied)
    {
        var accent = AccentBrush(App.UiIsLight);
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = columns, Margin = new Thickness(0, 0, -8, 0) };
        var current = StylePresets.Current(S);
        foreach (var preset in StylePresets.All)
        {
            var card = CardButton(new StackPanel
            {
                Children =
                {
                    PresetPreview(preset, 140, 38),
                    new TextBlock { Text = Loc.T(preset.Name), Margin = new Thickness(0, 7, 0, 0) },
                },
            }, ReferenceEquals(preset, current), accent);
            card.Margin = new Thickness(0, 0, 8, 8);
            card.Click += (_, _) =>
            {
                StylePresets.Apply(S, preset);
                Changed();
                SelectCard(grid, card, accent);
                applied?.Invoke();
            };
            grid.Children.Add(card);
        }
        return grid;
    }

    /// <summary>Partager un style : code court à coller (Discord…) ou fichier .tbvstyle.</summary>
    private void BuildStyleSharing(Panel page)
    {
        var p = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        page.Children.Add(Folded("appearance.share", "Partager un style (code à coller ou fichier)", p, new Thickness(0, 22, 0, 0)));
        var buttons = new WrapPanel();
        var feedback = new TextBlock { Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };

        void Applied(bool ok)
        {
            if (ok)
            {
                RebuildKeepingScroll(AppearancePanel, BuildAppearance);
                BuildModules();
                Changed();
            }
            else
            {
                feedback.Text = Loc.T("Ce n'est pas un style TaskbarVisu valide.");
            }
        }

        void Make(string text, Action action)
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
        var body = new StackPanel { Children = { buttons, feedback } };
        var card = WideCard("Le style comprend la palette, les effets, le fond et les couleurs de chaque module (pas la disposition).", body, "");
        p.Children.Add(card);
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

    /// <summary>Carte « dégradé personnalisé » : éditeur + mode d'emploi.</summary>
    private Border GradientRow(string label, List<ColorStop> stops, Action<List<ColorStop>> set)
    {
        var sources = Palettes.Where(x => x.Choice != PaletteChoice.Custom).ToArray();
        var options = sources.Select(x => new PaletteOption(Loc.T(x.Name), PaletteBrush(x.Choice))).ToList();
        var editor = new GradientEditor(stops, g =>
        {
            set(g);
            Changed();
        }, options, PaletteTemplate(), i => Rendering.Theme.ToGradient(S, sources[i].Choice));
        var body = new StackPanel { Children = { editor } };
        body.Children.Add(Description(Loc.T("Cliquez sur la barre pour ajouter une couleur (8 au plus), glissez les repères pour les déplacer, clic droit pour en retirer.")));
        return WideCard(label, body, "");
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
        PageHeader(p, "Modules", "Les réglages de chaque visualiseur. Cliquez sur un module pour les voir ; tout s'applique tout de suite.");
        _moduleExpanders.Clear();

        // D'abord les modules affichés, dans l'ordre de la barre, puis ceux qui sont masqués.
        var ordered = S.Left.Select(c => (Cfg: c, Side: "zone gauche")).Concat(S.Right.Select(c => (Cfg: c, Side: "zone droite"))).ToList();
        var shown = ordered.Where(x => x.Cfg.Enabled).ToList();
        var hidden = ordered.Where(x => !x.Cfg.Enabled).ToList();
        p.Children.Add(Section("Sur la barre"));
        if (shown.Count == 0) p.Children.Add(Card("Aucun module affiché : ajoutez-en depuis la page Disposition.", null, ""));
        foreach (var (cfg, side) in shown) AddModuleExpander(p, cfg, side);
        if (hidden.Count > 0)
        {
            p.Children.Add(Section("Masqués"));
            foreach (var (cfg, side) in hidden) AddModuleExpander(p, cfg, side);
        }
        MergeHints(p);
    }

    private void AddModuleExpander(Panel p, ModuleConfig cfg, string side)
    {
        var info = ModuleCatalog.Find(cfg.Id)!;
        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        var about = Description(Loc.T(info.Description));
        about.Margin = new Thickness(2, 0, 0, 10);
        body.Children.Add(about);
        if (!cfg.Enabled)
        {
            var show = new Button
            {
                Content = Loc.T("Afficher sur la barre"), HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10), Style = TryFindResource("AccentButtonStyle") as Style,
            };
            show.Click += (_, _) =>
            {
                cfg.Enabled = true;
                _selected = cfg;
                Changed();
                BuildLayout();
                RebuildKeepingScroll(ModulesPanel, BuildModules);
            };
            body.Children.Add(show);
        }
        AddModuleColors(body, cfg);
        foreach (var opt in info.Options.Where(o => !o.Advanced)) AddOption(body, cfg, opt);

        // Les réglages fins sont rangés à part pour garder l'essentiel lisible.
        var advanced = info.Options.Where(o => o.Advanced).ToList();
        if (advanced.Count > 0)
        {
            var more = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var opt in advanced) AddOption(more, cfg, opt);
            body.Children.Add(Folded(cfg.Id + ".more", "Plus d'options", more, new Thickness(0, 6, 0, 0)));
        }

        var reset = new Button { Content = Loc.T("Réinitialiser ce module"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        reset.Click += (_, _) =>
        {
            cfg.Options.Clear();
            cfg.Colors = ModuleColorMode.Global;
            cfg.Gradient = null;
            RebuildKeepingScroll(ModulesPanel, BuildModules);
            Changed();
        };
        body.Children.Add(reset);

        var expander = new Expander
        {
            Header = ModuleHeader(cfg, info, cfg.Enabled && !CentreMode(_app.Tracker.Current) ? side : null),
            Content = body,
            IsExpanded = _expanded.Contains(cfg.Id),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        expander.Expanded += (_, _) => _expanded.Add(cfg.Id);
        expander.Collapsed += (_, _) => _expanded.Remove(cfg.Id);
        _moduleExpanders[cfg.Id] = expander;
        p.Children.Add(expander);
    }

    /// <summary>En-tête d'un module : pastille à ses couleurs, nom, et zone de la barre.</summary>
    private FrameworkElement ModuleHeader(ModuleConfig cfg, ModuleInfo info, string? side)
    {
        var palette = cfg.Colors == ModuleColorMode.Palette ? cfg.ColorPalette : S.Palette;
        var swatch = new Border
        {
            Width = 28, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center, Opacity = cfg.Enabled ? 1 : 0.45,
            Background = cfg.Colors == ModuleColorMode.Solid
                ? new SolidColorBrush(ToColor(cfg.ColorSolid))
                : PaletteBrush(palette, cfg.Colors == ModuleColorMode.Palette ? cfg.Gradient : null),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(swatch);
        row.Children.Add(new TextBlock { Text = Loc.T(info.Name), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        if (side is not null)
        {
            var where = Description(Loc.T(side));
            where.Margin = new Thickness(12, 0, 0, 0);
            where.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(where);
        }
        return row;
    }

    /// <summary>Ligne « Couleurs » d'un module : générales, une autre palette, ou une couleur unie.</summary>
    private void AddModuleColors(Panel panel, ModuleConfig cfg)
    {
        var palettePanel = new StackPanel();
        // Palette « Personnalisée » : dégradé propre au module (au départ, celui de l'apparence générale).
        var gradientRow = GradientRow("Dégradé du module", cfg.Gradient ?? S.Gradient, g => cfg.Gradient = g);
        AddCombo(palettePanel, "Palette du module", PaletteOptions(), PaletteIndex(cfg.ColorPalette), i =>
        {
            cfg.ColorPalette = Palettes[i].Choice;
            gradientRow.Visibility = cfg.ColorPalette == PaletteChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
        }, PaletteTemplate(), icon: "");
        palettePanel.Children.Add(gradientRow);
        gradientRow.Visibility = cfg.ColorPalette == PaletteChoice.Custom ? Visibility.Visible : Visibility.Collapsed;

        var picker = new ColorPickerButton(cfg.ColorSolid);
        picker.ColorChanged += hex => { cfg.ColorSolid = hex; Changed(); };
        var solidRow = Card("Couleur du module", picker, "");

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
            }, icon: "");
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
                panel.Children.Add(Card(opt.Label, picker));
                break;
            default:
                AddSlider(panel, opt.Label, opt.Min, opt.Max, opt.Step, cfg.Get(opt.Key),
                    v => FormatValue(v, opt), v => cfg.Options[opt.Key] = v);
                break;
        }
        if (opt.Help is not null) panel.Children.Add(Hint(opt.Help));
    }

    private static string FormatValue(double v, OptionDef opt)
    {
        string number = opt.Step < 1 ? v.ToString("0.0#", CultureInfo.CurrentCulture) : v.ToString("0", CultureInfo.CurrentCulture);
        return opt.Unit is null ? number : $"{number} {opt.Unit}";
    }

    // ---------------------------------------------------------------- État

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
        RefreshHome();
        StatusText.Text = (_app.IsPaused ? Loc.T("En pause · ") : string.Empty)
                          + Loc.F("Audio : {0}", _app.Audio.StatusText) + "   ·   " + barText;
    }

    private void ResetAll()
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
}
