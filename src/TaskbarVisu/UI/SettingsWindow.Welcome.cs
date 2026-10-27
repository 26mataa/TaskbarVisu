using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using TaskbarVisu.Core;
using TaskbarVisu.Modules;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.UI;

/// <summary>
/// Guide de démarrage, au premier lancement (et depuis Général) : cinq étapes qui agissent en direct sur la vraie
/// barre. 1. les zones libres clignotent sur la barre ; 2. un style en un clic ; 3. une sélection de modules ;
/// 4. source audio, démarrage, raccourci ; 5. une flèche montre l'icône de la zone de notification.
/// « Passer » reste toujours visible.
/// </summary>
public partial class SettingsWindow
{
    private const int WelcomeSteps = 5;

    /// <summary>Étape affichée, ou -1 quand le guide est fermé.</summary>
    private int _welcomeStep = -1;

    /// <summary>« Je choisis moi-même » : le guide se termine sur l'onglet Disposition.</summary>
    private bool _welcomeOwnLayout;

    private TextBlock? _welcomeZones;

    /// <summary>Spectre type des aperçus de style (niveaux de 0 à 1, du grave à l'aigu).</summary>
    private static readonly double[] PreviewShape =
    {
        0.62, 0.78, 0.9, 0.84, 0.7, 0.76, 0.86, 0.72, 0.58, 0.64, 0.7, 0.55, 0.46, 0.52, 0.42, 0.36, 0.4, 0.3, 0.24, 0.27, 0.18, 0.12,
    };

    internal void ShowWelcome()
    {
        _welcomeOwnLayout = false;
        Tabs.Visibility = Visibility.Collapsed;
        AppHeader.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        WelcomeRoot.Visibility = Visibility.Visible;
        GoToWelcomeStep(0);
    }

    /// <summary>Affiche une étape (diagnostic : captures de chaque étape).</summary>
    internal void GoToWelcomeStep(int step)
    {
        _welcomeStep = Math.Clamp(step, 0, WelcomeSteps - 1);
        StopPreviews();
        _app.ShowGuideMark(_welcomeStep switch { 0 => GuideMark.Zones, 4 => GuideMark.Tray, _ => GuideMark.None });
        BuildWelcome();
    }

    private void CloseWelcome()
    {
        if (_welcomeStep < 0) return;
        _welcomeStep = -1;
        _welcomeZones = null;
        _app.ShowGuideMark(GuideMark.None);
        WelcomeRoot.Children.Clear();
        WelcomeRoot.Visibility = Visibility.Collapsed;
        Tabs.Visibility = Visibility.Visible;
        AppHeader.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Visible;
        // Les onglets reflètent ce qui a été choisi pendant le guide.
        RebuildAll();
        Tabs.SelectedItem = _welcomeOwnLayout ? TabLayout : TabGeneral;
        RequestPreviews();
    }

    private void BuildWelcome()
    {
        WelcomeRoot.Children.Clear();
        _welcomeZones = null;
        bool light = App.UiIsLight;
        var accent = AccentBrush(light);
        var (tag, title, subtitle) = _welcomeStep switch
        {
            0 => ("Zones", "Bienvenue dans TaskbarVisu",
                "Des visualiseurs audio qui vivent dans les espaces vides de votre barre des tâches. " +
                "Regardez-la : les zones qui clignotent sont celles où se placent les modules."),
            1 => ("Style", "Choisissez une ambiance",
                "Un clic, et la barre change aussitôt. Tout reste réglable ensuite dans l'onglet Apparence."),
            2 => ("Modules", "Que voulez-vous voir ?",
                "Une sélection pour commencer. Vous pourrez ajouter, retirer et déplacer les modules dans l'onglet Disposition."),
            3 => ("Réglages", "Derniers réglages", "Ce que TaskbarVisu écoute, et comment il démarre."),
            _ => ("Icône", "Vos réglages sont ici",
                "TaskbarVisu tourne en arrière-plan, dans la zone de notification : la flèche sur votre barre montre son icône."),
        };

        var grid = new Grid { Margin = new Thickness(36, 22, 36, 18) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });

        // En-tête : numéro d'étape, barre de progression, « Passer ».
        var header = new DockPanel { LastChildFill = false };
        var skip = new Button { Content = Loc.T("Passer"), IsCancel = true, MinWidth = 80 };
        skip.Click += (_, _) => CloseWelcome();
        DockPanel.SetDock(skip, Dock.Right);
        header.Children.Add(skip);
        var progress = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        for (int i = 0; i < WelcomeSteps; i++)
        {
            progress.Children.Add(new Border
            {
                Width = 34, Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 6, 0),
                Background = i <= _welcomeStep ? accent : new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            });
        }
        header.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = $"{_welcomeStep + 1:00} / {WelcomeSteps:00}  ·  {Loc.T(tag).ToUpperInvariant()}",
                    FontFamily = MonoFont, FontSize = 12, Opacity = 0.7,
                },
                progress,
            },
        });
        grid.Children.Add(header);

        var titleText = new TextBlock
        {
            Text = Loc.T(title), FontSize = 30, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 22, 0, 6),
        };
        Grid.SetRow(titleText, 1);
        grid.Children.Add(titleText);
        var subtitleText = Hint(subtitle);
        subtitleText.FontSize = 14;
        subtitleText.MaxWidth = 680;
        subtitleText.HorizontalAlignment = HorizontalAlignment.Left;
        subtitleText.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(subtitleText, 2);
        grid.Children.Add(subtitleText);

        var body = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
        switch (_welcomeStep)
        {
            case 0: BuildWelcomeZones(body); break;
            case 1: BuildWelcomeStyles(body); break;
            case 2: BuildWelcomePacks(body, accent); break;
            case 3: BuildWelcomeSettings(body); break;
            default: BuildWelcomeTray(body); break;
        }
        MergeHints(body);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };
        Grid.SetRow(scroll, 3);
        grid.Children.Add(scroll);

        // Pied : retour / suivant (ou terminer).
        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        bool last = _welcomeStep == WelcomeSteps - 1;
        var next = new Button
        {
            Content = Loc.T(last ? "Terminer" : "Suivant") + (last ? string.Empty : "  ▶"),
            MinWidth = 120, IsDefault = true,
            Style = TryFindResource("AccentButtonStyle") as Style,
        };
        next.Click += (_, _) =>
        {
            if (last) CloseWelcome();
            else GoToWelcomeStep(_welcomeStep + 1);
        };
        DockPanel.SetDock(next, Dock.Right);
        footer.Children.Add(next);
        if (_welcomeStep > 0)
        {
            var back = new Button { Content = "◀  " + Loc.T("Retour"), MinWidth = 100 };
            back.Click += (_, _) => GoToWelcomeStep(_welcomeStep - 1);
            footer.Children.Add(back);
        }
        Grid.SetRow(footer, 4);
        grid.Children.Add(footer);

        WelcomeRoot.Children.Add(grid);
    }

    // ------------------------------------------------------------ 1. Zones

    private void BuildWelcomeZones(Panel p)
    {
        p.Children.Add(ZonesMiniature());
        _welcomeZones = new TextBlock { FontFamily = MonoFont, FontSize = 15, LineHeight = 24, Margin = new Thickness(0, 4, 0, 12) };
        p.Children.Add(InfoBox(_welcomeZones));
        RefreshWelcomeZones();
        p.Children.Add(Hint("Les zones suivent la barre toutes seules : icônes ouvertes ou fermées, autre écran, autre taille. " +
                            "Icônes alignées à gauche ? Les modules peuvent aussi se grouper au centre (onglet Disposition)."));
    }

    /// <summary>La barre en miniature, à l'échelle : zones libres aux couleurs de la palette, qui clignotent avec la vraie.</summary>
    private FrameworkElement ZonesMiniature()
    {
        bool light = App.TaskbarIsLight;
        var ink = light ? Color.FromRgb(0x20, 0x20, 0x28) : Colors.White;
        var canvas = new Canvas { Height = 44, ClipToBounds = true };
        void Draw()
        {
            canvas.Children.Clear();
            var bar = MiniBar();
            double width = canvas.ActualWidth;
            if (width < 10) return;
            double k = width / Math.Max(1, bar.Taskbar.Width);
            double X(int px) => (px - bar.Taskbar.Left) * k;

            var zones = CentreMode(bar) ? new[] { bar.Spanning } : new[] { bar.Left, bar.Right };
            foreach (var zone in zones.Where(z => !z.IsEmpty))
            {
                var veil = new Rectangle { Width = zone.Width * k, Height = 32, RadiusX = 4, RadiusY = 4, Fill = PaletteBrush(S.Palette, S.Gradient) };
                veil.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.25, 0.6, TimeSpan.FromSeconds(0.65))
                {
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                });
                Place(canvas, veil, X(zone.X0), 6);
                Place(canvas, new Rectangle
                {
                    Width = zone.Width * k, Height = 32, RadiusX = 4, RadiusY = 4, StrokeThickness = 1,
                    Stroke = new SolidColorBrush(Color.FromArgb(0xC0, ink.R, ink.G, ink.B)), StrokeDashArray = new DoubleCollection { 3, 2 },
                }, X(zone.X0), 6);
            }

            // Icônes de la barre et horloge, pour se repérer.
            bool iconsLeft = !bar.Spanning.IsEmpty;
            double from = iconsLeft ? X(bar.Taskbar.Left) + 6 : X(bar.Left.X1), to = iconsLeft ? X(bar.Spanning.X0) : X(bar.Right.X0);
            int count = (int)Math.Clamp((to - from - 4) / 20, 1, 9);
            double start = iconsLeft ? from : (from + to) / 2 - count * 10;
            for (int i = 0; i < count; i++)
            {
                Place(canvas, new Rectangle
                {
                    Width = 14, Height = 14, RadiusX = 3, RadiusY = 3,
                    Fill = new SolidColorBrush(Color.FromArgb(i == 0 ? (byte)0x70 : (byte)0x38, ink.R, ink.G, ink.B)),
                }, start + i * 20 + 3, 15);
            }
            Place(canvas, new TextBlock
            {
                Text = DateTime.Now.ToString("HH:mm"), FontFamily = MonoFont, FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x90, ink.R, ink.G, ink.B)),
            }, width - 40, 14);
        }

        canvas.SizeChanged += (_, _) => Draw();
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(light ? Color.FromRgb(0xEE, 0xEE, 0xF0) : Color.FromRgb(0x1C, 0x1C, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Child = canvas,
        };
    }

    /// <summary>Largeurs détectées, mises à jour chaque seconde (une application ouverte change les zones).</summary>
    private void RefreshWelcomeZones()
    {
        if (_welcomeZones is null) return;
        var bar = _app.Tracker.Current;
        if (!bar.Valid || !bar.Supported)
        {
            _welcomeZones.Text = Loc.T(bar.Message);
            return;
        }
        _welcomeZones.Text = CentreMode(bar)
            ? $"{Loc.T("Zone centrale").ToUpperInvariant()}   {bar.Spanning.Width} PX"
            : $"{Loc.T("Zone gauche").ToUpperInvariant()}   {bar.Left.Width} PX\n{Loc.T("Zone droite").ToUpperInvariant()}   {bar.Right.Width} PX";
    }

    // ------------------------------------------------------------ 2. Style

    private void BuildWelcomeStyles(Panel p)
    {
        // Grille fixe et sélection mise à jour sur place : les cartes ne bougent jamais quand on en choisit une.
        // Sous la grille, l'animation des couleurs, qui suit le style choisi (le RGB défile).
        var animation = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        void ShowAnimation()
        {
            animation.Children.Clear();
            AddAnimationControls(animation);
            MergeHints(animation);
        }
        p.Children.Add(PresetGrid(4, ShowAnimation));
        p.Children.Add(animation);
        ShowAnimation();
    }

    /// <summary>Met le liseré d'accentuation sur <paramref name="selected"/> et l'enlève des autres cartes, sans rien déplacer.</summary>
    private static void SelectCard(Panel cards, Button? selected, Brush accent)
    {
        foreach (var card in cards.Children.OfType<Button>())
            card.BorderBrush = ReferenceEquals(card, selected) ? accent : CardBorder;
    }

    /// <summary>Aperçu d'un préréglage : un petit spectre à ses couleurs, sur son fond et avec ses effets.</summary>
    private FrameworkElement PresetPreview(StylePreset preset, double width, double height)
    {
        var preview = S.Clone();
        StylePresets.Apply(preview, preset);
        return StylePreview(preview, width, height);
    }

    /// <summary>Aperçu d'un style enregistré par l'utilisateur.</summary>
    private FrameworkElement SavedStylePreview(SavedStyle style, double width, double height)
    {
        var preview = S.Clone();
        StyleCode.TryApply(style.Code, preview);
        return StylePreview(preview, width, height);
    }

    /// <summary>Aperçu d'un style complet (réglages <paramref name="preview"/>, modifiables).</summary>
    private FrameworkElement StylePreview(AppSettings preview, double width, double height)
    {
        // Fond de l'aperçu = la barre des tâches (claire ou sombre), et l'encre qui va avec.
        bool light = preview.Theme switch { ThemeChoice.Light => true, ThemeChoice.Dark => false, _ => App.TaskbarIsLight };
        preview.Theme = light ? ThemeChoice.Light : ThemeChoice.Dark;
        var theme = Theme.Build(preview);
        var canvas = new Canvas
        {
            Width = width, Height = height,
            Background = new SolidColorBrush(light ? Color.FromRgb(0xEE, 0xEE, 0xF0) : Color.FromRgb(0x1C, 0x1C, 0x20)),
            Clip = new RectangleGeometry(new Rect(0, 0, width, height), 4, 4),
            SnapsToDevicePixels = true,
        };
        if (preview.Background == BackgroundChoice.Lcd)
            Place(canvas, new Rectangle { Width = width, Height = height, Fill = new SolidColorBrush(Color.FromArgb(0xF2, 0x9A, 0xA5, 0x7E)) }, 0, 0);

        // Barres du grave à l'aigu ; la police pixel donne des segments d'afficheur.
        int count = PreviewShape.Length;
        double gap = 2, left = 6, floor = height - 5, top = 13;
        double barWidth = (width - 2 * left - gap * (count - 1)) / count;
        for (int i = 0; i < count; i++)
        {
            var c = theme.Spectral(i / (count - 1f));
            var brush = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
            double barHeight = Math.Round(PreviewShape[i] * (floor - top));
            double x = Math.Round(left + i * (barWidth + gap));
            if (preview.PixelFont)
            {
                for (double y = floor - 2; y > floor - barHeight; y -= 3)
                    Place(canvas, new Rectangle { Width = Math.Max(1, Math.Round(barWidth)), Height = 2, Fill = brush }, x, y);
            }
            else
            {
                Place(canvas, new Rectangle { Width = Math.Max(1, Math.Round(barWidth)), Height = barHeight, Fill = brush }, x, floor - barHeight);
            }
        }

        // Lecture façon appareil, à l'encre du style.
        var ink = theme.Ink;
        Place(canvas, new TextBlock
        {
            Text = "-6 DB · 440 HZ", FontFamily = MonoFont, FontSize = 8.5,
            Foreground = new SolidColorBrush(Color.FromRgb(ink.Red, ink.Green, ink.Blue)),
        }, left, 1);

        if (preview.Scanlines)
            for (double y = 0; y < height; y += 2)
                Place(canvas, new Rectangle { Width = width, Height = 1, Fill = new SolidColorBrush(Color.FromArgb(light ? (byte)0x18 : (byte)0x38, 0, 0, 0)) }, 0, y);
        return canvas;
    }

    private static void Place(Canvas canvas, UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        canvas.Children.Add(element);
    }

    // ------------------------------------------------------------ 3. Modules

    private void BuildWelcomePacks(Panel p, Brush accent)
    {
        var current = _welcomeOwnLayout ? null : ModulePacks.Current(S);
        var grid = new UniformGrid { Columns = 2 };
        foreach (var pack in ModulePacks.All)
        {
            var card = PackCard(pack.Name, pack.Description, pack, ReferenceEquals(pack, current), accent);
            card.Click += (_, _) =>
            {
                _welcomeOwnLayout = false;
                ModulePacks.Apply(S, pack);
                Changed();
                SelectCard(grid, card, accent);
            };
            grid.Children.Add(card);
        }
        var own = PackCard("Je choisis moi-même", "La barre reste vide : vous ajoutez vos modules un par un, le guide se termine sur l'onglet Disposition.",
            null, _welcomeOwnLayout, accent);
        own.Click += (_, _) =>
        {
            // On part d'une barre vide : rien n'apparaît tant qu'on n'a pas choisi ses modules.
            _welcomeOwnLayout = true;
            foreach (var module in S.AllModules) module.Enabled = false;
            Changed();
            SelectCard(grid, own, accent);
        };
        grid.Children.Add(own);
        p.Children.Add(grid);
    }

    private Button PackCard(string name, string description, ModulePack? pack, bool selected, Brush accent)
    {
        var content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = Loc.T(name).ToUpperInvariant(), FontFamily = MonoFont, FontSize = 15, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = Loc.T(description), TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 10), MinHeight = 38 },
                PackSchematic(pack),
            },
        };
        var card = CardButton(content, selected, accent);
        card.Margin = new Thickness(0, 0, 10, 10);
        return card;
    }

    /// <summary>Pictogramme de la barre : modules à gauche, icônes au milieu, modules à droite (emplacements vides sinon).</summary>
    private FrameworkElement PackSchematic(ModulePack? pack)
    {
        bool light = App.TaskbarIsLight;
        var ink = light ? Color.FromRgb(0x20, 0x20, 0x28) : Colors.White;
        var row = new Grid { Height = 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        StackPanel Side(IEnumerable<string> ids, int empty, HorizontalAlignment align)
        {
            var side = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = align, Margin = new Thickness(4, 0, 4, 0) };
            foreach (var id in ids)
            {
                var cfg = S.AllModules.FirstOrDefault(m => m.Id == id);
                var palette = cfg is { Colors: ModuleColorMode.Palette } ? cfg.ColorPalette : S.Palette;
                double width = Math.Clamp((cfg?.Width ?? ModuleCatalog.Find(id)?.MinWidth ?? 120) / 4.0, 18, 44);
                side.Children.Add(new Border
                {
                    Width = width, Height = 18, Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(3),
                    Background = ChipFill(),
                    BorderBrush = ChipEdge(), BorderThickness = new Thickness(1),
                    ToolTip = ModuleCatalog.Find(id) is { } info ? Loc.T(info.Name) : null,
                    Child = new Border { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = PaletteBrush(palette) },
                });
            }
            for (int i = 0; i < empty; i++)
            {
                side.Children.Add(new Border
                {
                    Width = 30, Height = 18, Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(3),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, ink.R, ink.G, ink.B)), BorderThickness = new Thickness(1),
                    Child = new TextBlock { Text = "+", FontFamily = MonoFont, FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
            }
            return side;
        }

        var left = Side(pack?.Left ?? Array.Empty<string>(), pack is null ? 2 : 0, HorizontalAlignment.Left);
        var right = Side(pack?.Right ?? Array.Empty<string>(), pack is null ? 2 : 0, HorizontalAlignment.Right);
        var icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 4; i++)
        {
            icons.Children.Add(new Rectangle
            {
                Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Margin = new Thickness(2, 0, 2, 0),
                Fill = new SolidColorBrush(Color.FromArgb(i == 0 ? (byte)0x70 : (byte)0x38, ink.R, ink.G, ink.B)),
            });
        }
        Grid.SetColumn(icons, 1);
        Grid.SetColumn(right, 2);
        row.Children.Add(left);
        row.Children.Add(icons);
        row.Children.Add(right);
        return new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(0, 2, 0, 2),
            Background = new SolidColorBrush(light ? Color.FromRgb(0xEE, 0xEE, 0xF0) : Color.FromRgb(0x1C, 0x1C, 0x20)),
            Child = row,
        };
    }

    // ------------------------------------------------------------ 4. Réglages

    private void BuildWelcomeSettings(Panel p)
    {
        BuildAudioSource(p);
        p.Children.Add(Section("Démarrage"));
        AddToggle(p, "Démarrer avec Windows", S.StartWithWindows, v =>
        {
            S.StartWithWindows = v;
            StartupManager.Sync(v);
        });
        AddCombo(p, "Masquer / afficher les visualiseurs", HotkeyManager.Names, (int)S.Hotkey, i => S.Hotkey = (HotkeyChoice)i);
        p.Children.Add(Hint("Raccourci clavier : fonctionne partout, même en jeu."));
    }

    // ------------------------------------------------------------ 5. Icône

    private void BuildWelcomeTray(Panel p)
    {
        void Row(string key, string text)
        {
            var keycap = new Border
            {
                MinWidth = 120, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 14, 0), CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = key.ToUpperInvariant(), FontFamily = MonoFont, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
            };
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(keycap, Dock.Left);
            line.Children.Add(keycap);
            line.Children.Add(new TextBlock { Text = Loc.T(text), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(line);
        }

        Row(Loc.T("Clic"), "Ouvre ces réglages.");
        Row(Loc.T("Clic droit"), "Masquer les visualiseurs, pause, mise à jour, quitter.");
        if (S.Hotkey != HotkeyChoice.None)
            Row(_app.HotkeyText, "Masque ou réaffiche les visualiseurs, partout, même en jeu.");
        Row("^", "Icône introuvable ? Windows range les nouvelles icônes derrière la flèche ^ de la barre : " +
                 "glissez-la à côté de l'horloge pour l'avoir toujours sous la main.");
        p.Children.Add(Hint("Ce guide se rejoue depuis l'onglet Général."));
    }

    // ------------------------------------------------------------ Cartes

    private static Border InfoBox(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 10, 16, 6),
        Margin = new Thickness(0, 0, 0, 10),
        Background = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
        BorderThickness = new Thickness(1),
        Child = child,
    };

    private static readonly Brush CardBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Carte cliquable (clavier compris) ; la carte choisie a un liseré d'accentuation.
    /// Bordure toujours de 2 px : choisir une carte ne change pas sa taille, donc rien ne se décale.
    /// </summary>
    private static Button CardButton(UIElement content, bool selected, Brush accent) => new()
    {
        Content = content,
        Padding = new Thickness(11, 9, 11, 9),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Top,
        BorderThickness = new Thickness(2),
        BorderBrush = selected ? accent : CardBorder,
    };
}
