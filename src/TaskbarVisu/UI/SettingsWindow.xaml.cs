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
    private ModuleConfig? _selected;
    private ModuleConfig? _dragCandidate;
    private Point _dragStart;

    private AppSettings S => _app.Settings;

    public SettingsWindow()
    {
        InitializeComponent();
        RebuildAll();

        SetupList(LeftList);
        SetupList(RightList);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
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
        LayoutIntro.Text = Loc.T("Les espaces libres à gauche et à droite des icônes de la barre des tâches sont détectés " +
                                 "automatiquement (y compris les widgets comme FluentFlyout). Cochez les modules à afficher, " +
                                 "glissez-les pour changer l'ordre ou le côté.");
        LeftHeader.Text = Loc.T("Zone gauche");
        RightHeader.Text = Loc.T("Zone droite");
        LeftUpButton.ToolTip = RightUpButton.ToolTip = Loc.T("Monter");
        LeftDownButton.ToolTip = RightDownButton.ToolTip = Loc.T("Descendre");
        MoveRightButton.Content = Loc.T("Vers la droite ▶");
        MoveLeftButton.Content = Loc.T("◀ Vers la gauche");
        ResetAllButton.Content = Loc.T("Tout réinitialiser");
        CloseButton.Content = Loc.T("Fermer");

        BuildGeneral();
        BuildAppearance();
        BuildLayoutAdjustments();
        RefreshLists();
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

        var bars = TaskbarLocator.FindAll();
        var screens = new List<string> { Loc.T("Automatique (écran principal)") };
        screens.AddRange(bars.Select((b, i) => b.DisplayName(i)));
        int screenIndex = S.MonitorDevice is null ? 0 : bars.FindIndex(b => b.DeviceName == S.MonitorDevice) + 1;
        AddCombo(p, "Écran", screens, Math.Max(0, screenIndex),
            i => S.MonitorDevice = i == 0 ? null : bars[i - 1].DeviceName, translate: false);

        AddToggle(p, "Masquer quand une application est en plein écran", S.HideWhenFullscreen, v => S.HideWhenFullscreen = v);

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
                        new Border { Width = 110, Height = 10, CornerRadius = new CornerRadius(2), Background = PaletteBrush(preset.Palette) },
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
        AddCombo(p, "Fond des modules", new[] { "Aucun (fondu dans la barre)", "Sombre translucide", "Noir" },
            (int)S.Background, i => S.Background = (BackgroundChoice)i);
        AddSlider(p, "Opacité", 20, 100, 5, S.Opacity * 100, v => $"{v:0} %", v => S.Opacity = v / 100);

        p.Children.Add(Section("Couleurs"));
        AddCombo(p, "Thème", new[] { "Automatique (suit la barre des tâches)", "Sombre", "Clair" },
            (int)S.Theme, i => S.Theme = (ThemeChoice)i);

        // Palette des fréquences (grave → aigu), avec aperçu du dégradé.
        var lowPicker = new ColorPickerButton(S.PaletteLow);
        var highPicker = new ColorPickerButton(S.PaletteHigh);
        var customPaletteRow = Labeled("Palette personnalisée (grave → aigu)", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { lowPicker, new TextBlock { Text = "→", Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }, highPicker },
        });
        void ShowCustomPalette() => customPaletteRow.Visibility =
            S.Palette == PaletteChoice.Custom ? Visibility.Visible : Visibility.Collapsed;
        AddCombo(p, "Palette générale", PaletteOptions(), PaletteIndex(S.Palette), i =>
        {
            S.Palette = Palettes[i].Choice;
            ShowCustomPalette();
        }, PaletteTemplate());
        lowPicker.ColorChanged += hex => { S.PaletteLow = hex; Changed(); };
        highPicker.ColorChanged += hex => { S.PaletteHigh = hex; Changed(); };
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
    }

    private sealed record PaletteOption(string Name, Brush Preview);

    private List<PaletteOption> PaletteOptions() =>
        Palettes.Select(x => new PaletteOption(Loc.T(x.Name), PaletteBrush(x.Choice))).ToList();

    /// <summary>Aperçu horizontal d'une palette (grave → aigu).</summary>
    private Brush PaletteBrush(PaletteChoice palette)
    {
        var theme = Rendering.Theme.Build(S, palette, null);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 8; i++)
        {
            var c = theme.Spectral(i / 8f);
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(c.Red, c.Green, c.Blue), i / 8.0));
        }
        brush.Freeze();
        return brush;
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

    // ---------------------------------------------------------------- Disposition

    private void BuildLayoutAdjustments()
    {
        var p = LayoutPanel;
        p.Children.Clear();
        p.Children.Add(Section("Ajustements"));

        var reset = new Button { Content = Loc.T("Réinitialiser la disposition"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        reset.Click += (_, _) =>
        {
            S.ResetLayout();
            _selected = null;
            RefreshLists();
            BuildModules();
            BuildLayoutAdjustments();
            Changed();
        };
        p.Children.Add(reset);

        AddToggle(p, "Étirer les modules pour remplir chaque zone", S.FillZones, v => S.FillZones = v);
        AddCombo(p, "Zone gauche : placer les modules", new[] { "Contre le bord de l'écran", "Au centre de la zone", "Contre les icônes" },
            (int)S.LeftAlign, i => S.LeftAlign = (ZoneAlign)i);
        AddCombo(p, "Zone droite : placer les modules", new[] { "Contre les icônes", "Au centre de la zone", "Contre la zone de notification" },
            (int)S.RightAlign, i => S.RightAlign = (ZoneAlign)i);
        p.Children.Add(Hint("Le placement s'applique quand les modules ne sont pas étirés."));
        AddSlider(p, "Espacement entre les modules", 0, 48, 1, S.Spacing, v => $"{v:0} px", v => S.Spacing = (int)v);
        AddSlider(p, "Fondu des bords", 0, 40, 1, S.EdgeFade, v => $"{v:0} px", v => S.EdgeFade = (int)v);
        AddSlider(p, "Marge verticale", 0, 10, 1, S.VerticalPadding, v => $"{v:0} px", v => S.VerticalPadding = (int)v);
        p.Children.Add(Hint("Les tailles sont exprimées à 100 % d'échelle et suivent automatiquement le DPI de l'écran."));
    }

    private void RefreshLists()
    {
        FillList(LeftList, S.Left);
        FillList(RightList, S.Right);
        BuildSelectionPanel();
    }

    private void FillList(ListBox list, List<ModuleConfig> configs)
    {
        list.Items.Clear();
        foreach (var cfg in configs)
        {
            var info = ModuleCatalog.Find(cfg.Id)!;
            var check = new CheckBox { Content = Loc.T(info.Name), IsChecked = cfg.Enabled, VerticalAlignment = VerticalAlignment.Center };
            check.Checked += (_, _) => { cfg.Enabled = true; Changed(); BuildModules(); };
            check.Unchecked += (_, _) => { cfg.Enabled = false; Changed(); BuildModules(); };
            var item = new ListBoxItem { Content = check, Tag = cfg, Padding = new Thickness(8, 4, 8, 4) };
            list.Items.Add(item);
            if (ReferenceEquals(cfg, _selected)) item.IsSelected = true;
        }
    }

    private void SetupList(ListBox list)
    {
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: ModuleConfig cfg }) return;
            var other = ReferenceEquals(list, LeftList) ? RightList : LeftList;
            other.SelectedIndex = -1;
            if (ReferenceEquals(_selected, cfg)) return;
            _selected = cfg;
            BuildSelectionPanel();
        };

        // Glisser-déposer : réordonner dans une zone ou changer de zone.
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _dragCandidate = ItemFrom(e.OriginalSource as DependencyObject)?.Tag as ModuleConfig;
        };
        list.PreviewMouseMove += (_, e) =>
        {
            if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(this) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var dragged = _dragCandidate;
            _dragCandidate = null;
            DragDrop.DoDragDrop(list, new DataObject(typeof(ModuleConfig), dragged), DragDropEffects.Move);
        };
        list.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(ModuleConfig)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        list.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(ModuleConfig)) is not ModuleConfig cfg) return;
            var target = ReferenceEquals(list, LeftList) ? S.Left : S.Right;
            var overItem = ItemFrom(e.OriginalSource as DependencyObject);
            int index = target.Count;
            if (overItem is not null)
            {
                index = list.Items.IndexOf(overItem);
                if (e.GetPosition(overItem).Y > overItem.ActualHeight / 2) index++;
            }

            int oldIndex = target.IndexOf(cfg);
            if (oldIndex >= 0 && oldIndex < index) index--;
            S.Left.Remove(cfg);
            S.Right.Remove(cfg);
            target.Insert(Math.Clamp(index, 0, target.Count), cfg);
            _selected = cfg;
            AfterLayoutChange();
        };
    }

    private static ListBoxItem? ItemFrom(DependencyObject? d)
    {
        while (d is not null and not ListBoxItem)
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    private void BuildSelectionPanel()
    {
        var p = SelectionPanel;
        p.Children.Clear();
        if (_selected is null)
        {
            p.Children.Add(Hint("Sélectionnez un module pour régler sa largeur."));
            return;
        }

        var cfg = _selected;
        var info = ModuleCatalog.Find(cfg.Id)!;
        p.Children.Add(new TextBlock { Text = Loc.T(info.Name), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        AddSlider(p, "Largeur préférée", info.MinWidth, AppSettings.MaxModuleWidth, 5, cfg.Width, v => $"{v:0} px", v => cfg.Width = (int)v);
        p.Children.Add(Hint("Avec « Étirer les modules », la largeur sert de proportion entre les modules d'une même zone. " +
                            "S'il manque de place, les modules rétrécissent jusqu'à leur minimum puis les derniers sont masqués."));
    }

    private void MoveWithin(List<ModuleConfig> list, int delta)
    {
        if (_selected is null) return;
        int i = list.IndexOf(_selected);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        AfterLayoutChange();
    }

    private void MoveAcross(List<ModuleConfig> from, List<ModuleConfig> to)
    {
        if (_selected is null || !from.Remove(_selected)) return;
        to.Add(_selected);
        AfterLayoutChange();
    }

    private void AfterLayoutChange()
    {
        RefreshLists();
        BuildModules();
        Changed();
    }

    private void OnLeftUp(object sender, RoutedEventArgs e) => MoveWithin(S.Left, -1);
    private void OnLeftDown(object sender, RoutedEventArgs e) => MoveWithin(S.Left, +1);
    private void OnRightUp(object sender, RoutedEventArgs e) => MoveWithin(S.Right, -1);
    private void OnRightDown(object sender, RoutedEventArgs e) => MoveWithin(S.Right, +1);
    private void OnMoveToRight(object sender, RoutedEventArgs e) => MoveAcross(S.Left, S.Right);
    private void OnMoveToLeft(object sender, RoutedEventArgs e) => MoveAcross(S.Right, S.Left);

    // ---------------------------------------------------------------- Modules

    private void BuildModules()
    {
        var p = ModulesPanel;
        p.Children.Clear();
        p.Children.Add(Hint("Réglages propres à chaque module. Les changements s'appliquent immédiatement sur la barre des tâches."));

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
            p.Children.Add(expander);
        }
    }

    /// <summary>Ligne « Couleurs » d'un module : générales, une autre palette, ou une couleur unie.</summary>
    private void AddModuleColors(Panel panel, ModuleConfig cfg)
    {
        var palettePanel = new StackPanel();
        AddCombo(palettePanel, "Palette du module", PaletteOptions(), PaletteIndex(cfg.ColorPalette),
            i => cfg.ColorPalette = Palettes[i].Choice, PaletteTemplate());

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
        string barText = bar.Valid && bar.Supported
            ? Loc.F("Zones libres : gauche {0} px · droite {1} px", bar.Left.Width, bar.Right.Width)
            : Loc.T(bar.Message);
        StatusText.Text = (_app.IsPaused ? Loc.T("En pause · ") : string.Empty)
                          + Loc.F("Audio : {0}", Loc.T(_app.Audio.Status)) + "   ·   " + barText;
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
