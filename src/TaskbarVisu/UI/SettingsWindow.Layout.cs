using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TaskbarVisu.Core;
using TaskbarVisu.Modules;
using TaskbarVisu.Native;
using TaskbarVisu.Rendering;
using TaskbarVisu.Taskbar;

namespace TaskbarVisu.UI;

/// <summary>
/// Onglet Disposition : la barre des tâches en miniature, à l'échelle, avec seulement les modules affichés
/// (clic = régler, glisser = déplacer), le module choisi, la galerie « Ajouter un module » et les réglages fins repliés.
/// </summary>
public partial class SettingsWindow
{
    private const double MiniHeight = 50;
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    /// <summary>Une zone de la miniature : modules affichés, emplacements calculés comme sur la vraie barre, et ceux qui ne rentrent pas.</summary>
    private sealed record MiniZone(string Caption, Zone Zone, List<ModuleConfig> Shown, List<(float X, float Width)> Slots, List<ModuleConfig> Overflow);

    private Canvas? _mini, _captions;
    private StackPanel? _selectionPanel;
    private TextBlock? _overflowText;
    private TaskbarState? _miniBar;
    private List<MiniZone> _zones = new();
    private readonly List<(ModuleConfig Cfg, Border Chip, int Zone)> _chips = new();

    // Glisser un module dans la miniature.
    private ModuleConfig? _press;
    private Point _pressAt;
    private bool _dragging;
    private Rectangle? _caret;
    private (int Zone, ModuleConfig? Before) _dropTarget;

    /// <summary>Icônes alignées à gauche et modules groupés au centre : une seule zone.</summary>
    private bool CentreMode(TaskbarState bar) => S.SingleGap == SingleGapMode.Center && bar.Valid && !bar.Spanning.IsEmpty;

    private void BuildLayout()
    {
        var p = LayoutRoot;
        p.Children.Clear();
        _press = null;
        _dragging = false;
        if (_selected is not null && !S.AllModules.Contains(_selected)) _selected = null;

        var bar = _app.Tracker.Current;
        PageHeader(p, "Disposition", "Votre barre des tâches en miniature, avec les modules affichés à l'échelle. " +
                                     "Cliquez sur un module pour le régler, glissez-le pour le déplacer.");
        p.Children.Add(Section("Votre barre"));

        // Icônes alignées à gauche : un seul grand espace, le choix principal se fait ici.
        if (bar.Valid && !bar.Spanning.IsEmpty)
        {
            AddCombo(p, "Icônes à gauche : placer les modules", new[] { "Dans deux zones", "Tous au centre de la barre" },
                (int)S.SingleGap, i =>
                {
                    S.SingleGap = (SingleGapMode)i;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(BuildLayout));
                }, description: "Vos icônes sont alignées à gauche : il n'y a qu'un grand espace libre.", icon: "");
        }

        _mini = new Canvas { Height = MiniHeight, ClipToBounds = true, Background = Brushes.Transparent };
        _captions = new Canvas { Height = 18, ClipToBounds = true, Margin = new Thickness(0, 4, 0, 0) };
        bool light = App.TaskbarIsLight;
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(light ? Color.FromRgb(0xEE, 0xEE, 0xF0) : Color.FromRgb(0x1C, 0x1C, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0),
            Child = _mini,
        };
        _mini.SizeChanged += (_, _) => DrawMini();
        _mini.MouseMove += OnMiniMouseMove;
        _mini.MouseLeftButtonUp += OnMiniMouseUp;
        _mini.LostMouseCapture += (_, _) => EndMiniDrag(apply: false);
        p.Children.Add(frame);
        p.Children.Add(_captions);
        _overflowText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed, Margin = new Thickness(0, 2, 0, 0) };
        p.Children.Add(_overflowText);

        _selectionPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        p.Children.Add(_selectionPanel);
        BuildSelection();

        BuildGallery(p);
        BuildFineTuning(p);
        MergeHints(p);
        DrawMini();
    }

    /// <summary>La miniature suit la vraie barre (changement de taille, d'écran, d'alignement des icônes…).</summary>
    private void RefreshMiniIfBarChanged()
    {
        var bar = _app.Tracker.Current;
        if (_mini is null || _miniBar is null || bar.SameLayout(_miniBar)) return;
        bool wasSingle = !_miniBar.Spanning.IsEmpty;
        if (wasSingle != !bar.Spanning.IsEmpty || CentreMode(bar) != CentreMode(_miniBar)) BuildLayout();
        else DrawMini();
    }

    private void LayoutChanged()
    {
        Changed();
        DrawMini();
        BuildSelection();
        BuildModules();
    }

    // ------------------------------------------------------------ Miniature

    /// <summary>Barre affichée : la vraie, ou une barre type 1920 px si elle n'est pas (encore) détectée.</summary>
    private TaskbarState MiniBar()
    {
        var bar = _app.Tracker.Current;
        if (bar.Valid && bar.Supported && bar.Taskbar.Width > 0) return bar;
        return new TaskbarState
        {
            Valid = true,
            Taskbar = new RECT(0, 0, 1920, 48),
            Scale = 1,
            Left = new Zone(12, 700),
            Right = new Zone(1220, 1690),
        };
    }

    private List<MiniZone> ComputeZones(TaskbarState bar)
    {
        MiniZone Make(string caption, Zone zone, IEnumerable<ModuleConfig> source, ZoneAlign align)
        {
            var shown = source.Where(c => c.Enabled && ModuleCatalog.Find(c.Id) is not null).ToList();
            var slots = zone.IsEmpty || shown.Count == 0
                ? new List<(float X, float Width)>()
                : ModuleLayout.Compute(shown.Select(c => (c.Width * bar.Scale, ModuleCatalog.Find(c.Id)!.MinWidth * bar.Scale)).ToList(),
                    zone.Width, S.Spacing * bar.Scale, S.FillZones, align);
            return new MiniZone(caption, zone, shown, slots, shown.Skip(slots.Count).ToList());
        }

        if (CentreMode(bar))
            return new() { Make("Centre", bar.Spanning, S.Left.Concat(S.Right), ZoneAlign.Center) };
        return new()
        {
            Make("Zone gauche", bar.Left, S.Left, S.LeftAlign),
            Make("Zone droite", bar.Right, S.Right, S.RightAlign),
        };
    }

    private void DrawMini()
    {
        if (_mini is null || _captions is null || _overflowText is null) return;
        var bar = MiniBar();
        _miniBar = _app.Tracker.Current;
        _zones = ComputeZones(bar);
        _mini.Children.Clear();
        _captions.Children.Clear();
        _chips.Clear();
        _caret = null;

        double width = _mini.ActualWidth;
        if (width < 10) return;
        double k = width / Math.Max(1, bar.Taskbar.Width);
        double X(int px) => (px - bar.Taskbar.Left) * k;
        bool light = App.TaskbarIsLight;
        var ink = light ? Color.FromRgb(0x20, 0x20, 0x28) : Colors.White;

        // Icônes de la barre (Démarrer en premier) et zone de notification, juste pour se repérer.
        bool iconsLeft = !bar.Spanning.IsEmpty;
        double iconsFrom = iconsLeft ? X(bar.Taskbar.Left) + 6 : X(bar.Left.X1);
        double iconsTo = iconsLeft ? X(bar.Spanning.X0) : X(bar.Right.X0);
        int count = (int)Math.Clamp((iconsTo - iconsFrom - 4) / 20, 1, 9);
        double start = iconsLeft ? iconsFrom : (iconsFrom + iconsTo) / 2 - count * 10;
        for (int i = 0; i < count; i++)
        {
            var icon = new Rectangle
            {
                Width = 14, Height = 14, RadiusX = 3, RadiusY = 3,
                Fill = new SolidColorBrush(Color.FromArgb(i == 0 ? (byte)0x70 : (byte)0x38, ink.R, ink.G, ink.B)),
            };
            Canvas.SetLeft(icon, start + i * 20 + 3);
            Canvas.SetTop(icon, (MiniHeight - 14) / 2);
            _mini.Children.Add(icon);
        }
        var clock = new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"), FontFamily = MonoFont, FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(0x90, ink.R, ink.G, ink.B)),
        };
        Canvas.SetLeft(clock, width - 40);
        Canvas.SetTop(clock, (MiniHeight - 14) / 2);
        _mini.Children.Add(clock);

        var accent = AccentBrush(light);
        var overflow = new List<string>();
        for (int z = 0; z < _zones.Count; z++)
        {
            var zone = _zones[z];
            if (zone.Zone.IsEmpty) continue;
            double zx = X(zone.Zone.X0), zw = zone.Zone.Width * k;
            var outline = new Rectangle
            {
                Width = Math.Max(1, zw), Height = MiniHeight - 8, RadiusX = 4, RadiusY = 4,
                Stroke = new SolidColorBrush(Color.FromArgb(0x50, ink.R, ink.G, ink.B)), StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            };
            Canvas.SetLeft(outline, zx);
            Canvas.SetTop(outline, 4);
            _mini.Children.Add(outline);

            for (int i = 0; i < zone.Slots.Count; i++)
            {
                var cfg = zone.Shown[i];
                var (sx, sw) = zone.Slots[i];
                var chip = MakeChip(cfg, Math.Max(6, sw * k - 1), ReferenceEquals(cfg, _selected), accent);
                Canvas.SetLeft(chip, zx + sx * k);
                Canvas.SetTop(chip, 7);
                _mini.Children.Add(chip);
                _chips.Add((cfg, chip, z));
            }

            // Légende sous la zone : nom, nombre de modules et place occupée.
            float used = zone.Slots.Count == 0 ? 0 : zone.Slots[^1].X + zone.Slots[^1].Width - zone.Slots[0].X;
            var caption = new TextBlock
            {
                Text = Loc.F("{0} · {1} / {2} px", Loc.T(zone.Caption), (int)used, zone.Zone.Width).ToUpperInvariant(),
                FontFamily = MonoFont, FontSize = 10.5, Opacity = 0.65,
            };
            Canvas.SetLeft(caption, zx);
            _captions.Children.Add(caption);
            overflow.AddRange(zone.Overflow.Select(c => Loc.T(ModuleCatalog.Find(c.Id)!.Name)));
        }

        _overflowText.Text = overflow.Count == 0 ? string.Empty
            : Loc.F("Pas assez de place pour : {0}. Réduisez des largeurs ou changez-les de côté.", string.Join(", ", overflow));
        _overflowText.Visibility = overflow.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private Border MakeChip(ModuleConfig cfg, double width, bool selected, Brush accent)
    {
        var info = ModuleCatalog.Find(cfg.Id)!;
        var palette = cfg.Colors == ModuleColorMode.Palette ? cfg.ColorPalette : S.Palette;
        var strip = new Border
        {
            Height = 3, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, 3, 3),
            Background = cfg.Colors == ModuleColorMode.Solid
                ? new SolidColorBrush(ToColor(cfg.ColorSolid))
                : PaletteBrush(palette, cfg.Colors == ModuleColorMode.Palette ? cfg.Gradient : null),
        };
        var name = new TextBlock
        {
            Text = Loc.T(info.Name).ToUpperInvariant(),
            FontFamily = MonoFont, FontSize = 10,
            Foreground = ChipText(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(5, 0, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chip = new Border
        {
            Width = width, Height = MiniHeight - 14,
            CornerRadius = new CornerRadius(4),
            Background = ChipFill(),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? accent : ChipEdge(),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T(info.Name),
            Child = new Grid { Children = { name, strip } },
        };
        chip.MouseLeftButtonDown += (_, e) =>
        {
            _press = cfg;
            _pressAt = e.GetPosition(_mini);
            _dragging = false;
            _mini!.CaptureMouse();
            e.Handled = true;
        };
        return chip;
    }

    private void OnMiniMouseMove(object sender, MouseEventArgs e)
    {
        if (_press is null || _mini is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(_mini);
        if (!_dragging && Math.Abs(pos.X - _pressAt.X) < SystemParameters.MinimumHorizontalDragDistance) return;
        _dragging = true;

        var (_, chip, _) = _chips.FirstOrDefault(c => ReferenceEquals(c.Cfg, _press));
        if (chip is not null)
        {
            chip.Opacity = 0.75;
            chip.RenderTransform = new TranslateTransform(pos.X - _pressAt.X, 0);
        }

        // Point d'insertion : la zone sous la souris (ou la plus proche), entre deux modules.
        var bar = MiniBar();
        double k = _mini.ActualWidth / Math.Max(1, bar.Taskbar.Width);
        double px = bar.Taskbar.Left + pos.X / k;
        int zone = 0;
        double best = double.MaxValue;
        for (int z = 0; z < _zones.Count; z++)
        {
            var zz = _zones[z].Zone;
            if (zz.IsEmpty) continue;
            double d = px < zz.X0 ? zz.X0 - px : px > zz.X1 ? px - zz.X1 : 0;
            if (d < best) { best = d; zone = z; }
        }
        var others = _chips.Where(c => c.Zone == zone && !ReferenceEquals(c.Cfg, _press)).ToList();
        var before = others.FirstOrDefault(c => Canvas.GetLeft(c.Chip) + c.Chip.Width / 2 > pos.X);
        _dropTarget = (zone, before.Cfg);

        double caretX = before.Chip is not null ? Canvas.GetLeft(before.Chip) - 2
            : others.Count > 0 ? Canvas.GetLeft(others[^1].Chip) + others[^1].Chip.Width + 1
            : (_zones[zone].Zone.X0 - bar.Taskbar.Left) * k + 2;
        if (_caret is null)
        {
            _caret = new Rectangle { Width = 2, Height = MiniHeight - 8, Fill = AccentBrush(App.UiIsLight), IsHitTestVisible = false };
            Canvas.SetTop(_caret, 4);
            _mini.Children.Add(_caret);
        }
        Canvas.SetLeft(_caret, caretX);
    }

    private void OnMiniMouseUp(object sender, MouseButtonEventArgs e) => EndMiniDrag(apply: true);

    private void EndMiniDrag(bool apply)
    {
        var cfg = _press;
        bool dragged = _dragging;
        _press = null;
        _dragging = false;
        if (_mini?.IsMouseCaptured == true) _mini.ReleaseMouseCapture();
        if (cfg is null || !apply) return;

        if (dragged)
        {
            var (zone, before) = _dropTarget;
            List<ModuleConfig> target = before is not null ? (S.Left.Contains(before) ? S.Left : S.Right)
                : _zones.Count == 1 ? S.Right : zone == 0 ? S.Left : S.Right;
            Place(cfg, target, before);
        }
        _selected = cfg;
        if (dragged) LayoutChanged();
        else
        {
            DrawMini();
            BuildSelection();
        }
    }

    /// <summary>Met <paramref name="cfg"/> juste avant <paramref name="before"/> dans <paramref name="target"/> (à la fin si null).</summary>
    private void Place(ModuleConfig cfg, List<ModuleConfig> target, ModuleConfig? before)
    {
        S.Left.Remove(cfg);
        S.Right.Remove(cfg);
        int index = before is null ? -1 : target.IndexOf(before);
        target.Insert(index < 0 ? target.Count : index, cfg);
    }

    private void PlaceAfter(ModuleConfig cfg, ModuleConfig after)
    {
        S.Left.Remove(cfg);
        S.Right.Remove(cfg);
        var list = S.Left.Contains(after) ? S.Left : S.Right;
        list.Insert(list.IndexOf(after) + 1, cfg);
    }

    // ------------------------------------------------------------ Module choisi

    private void BuildSelection()
    {
        var p = _selectionPanel;
        if (p is null) return;
        p.Children.Clear();
        var cfg = _selected;
        if (cfg is null || !cfg.Enabled)
        {
            p.Children.Add(Card(S.AllModules.Any(m => m.Enabled)
                ? "Cliquez sur un module de la barre pour régler sa largeur, le déplacer ou le retirer."
                : "La barre est vide : ajoutez un premier module ci-dessous.", null, ""));
            return;
        }

        var info = ModuleCatalog.Find(cfg.Id)!;
        var bar = MiniBar();
        bool centre = CentreMode(bar);
        bool left = S.Left.Contains(cfg);
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = centre ? Loc.T(info.Name) : $"{Loc.T(info.Name)}   ·   {Loc.T(left ? "zone gauche" : "zone droite")}",
            FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 0, 0, 2),
        });
        var about = Description(Loc.T(info.Description));
        about.Margin = new Thickness(0, 0, 0, 10);
        body.Children.Add(about);
        AddSlider(body, "Largeur", info.MinWidth, AppSettings.MaxModuleWidth, 5, cfg.Width, v => $"{v:0} px", v =>
        {
            cfg.Width = (int)v;
            DrawMini();
        }, icon: "");

        // Ordre d'affichage de la zone du module (les deux listes à la suite au centre).
        var sequence = _zones.FirstOrDefault(z => z.Shown.Contains(cfg))?.Shown ?? new List<ModuleConfig>();
        int index = sequence.IndexOf(cfg);
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button Add(string text, bool enabled, Action action)
        {
            var b = new Button { Content = Loc.T(text), IsEnabled = enabled, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 5, 12, 5) };
            b.Click += (_, _) => action();
            buttons.Children.Add(b);
            return b;
        }

        Add("◀ Décaler", index > 0, () =>
        {
            var previous = sequence[index - 1];
            Place(cfg, S.Left.Contains(previous) ? S.Left : S.Right, previous);
            LayoutChanged();
        });
        Add("Décaler ▶", index >= 0 && index < sequence.Count - 1, () =>
        {
            PlaceAfter(cfg, sequence[index + 1]);
            LayoutChanged();
        });
        if (!centre)
        {
            // Le module « saute » les icônes : il arrive du côté des icônes dans l'autre zone.
            Add(left ? "Passer à droite" : "Passer à gauche", true, () =>
            {
                if (left) Place(cfg, S.Right, S.Right.FirstOrDefault(c => !ReferenceEquals(c, cfg)));
                else Place(cfg, S.Left, null);
                LayoutChanged();
            });
        }
        Add("Retirer de la barre", true, () =>
        {
            cfg.Enabled = false;
            _selected = null;
            LayoutChanged();
            BuildLayout(); // la galerie le propose de nouveau
        });
        Add("Options du module…", true, () =>
        {
            _expanded.Add(cfg.Id);
            BuildModules();
            Tabs.SelectedItem = TabModules;
            if (_moduleExpanders.TryGetValue(cfg.Id, out var target))
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(target.BringIntoView));
        });
        body.Children.Add(buttons);

        // Liseré d'accentuation : c'est le module entouré sur la miniature.
        var frame = CardFrame(body, new Thickness(16, 12, 16, 6));
        frame.BorderBrush = AccentBrush(App.UiIsLight);
        p.Children.Add(frame);
    }

    // ------------------------------------------------------------ Galerie et réglages fins

    private void BuildGallery(Panel p)
    {
        p.Children.Add(Section("Ajouter un module"));
        var hidden = ModuleCatalog.All
            .Select(info => (Info: info, Cfg: S.AllModules.FirstOrDefault(c => c.Id == info.Id)))
            .Where(x => x.Cfg is { Enabled: false })
            .ToList();
        if (hidden.Count == 0)
        {
            p.Children.Add(Card("Tous les modules sont déjà sur la barre.", null, ""));
            return;
        }

        bool centre = CentreMode(MiniBar());
        var cards = new WrapPanel();
        foreach (var (info, cfg) in hidden)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            void AddTo(string text, List<ModuleConfig> target)
            {
                var b = new Button { Content = Loc.T(text), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 4, 10, 4) };
                b.Click += (_, _) =>
                {
                    cfg!.Enabled = true;
                    Place(cfg, target, null);
                    _selected = cfg;
                    Changed();
                    BuildModules();
                    BuildLayout();
                };
                buttons.Children.Add(b);
            }
            if (centre) AddTo("+ Ajouter", S.Right);
            else
            {
                AddTo("+ Gauche", S.Left);
                AddTo("+ Droite", S.Right);
            }

            var about = Description(Loc.T(info.Description));
            about.TextTrimming = TextTrimming.WordEllipsis;
            about.Height = 48;
            about.Margin = new Thickness(0, 4, 0, 0);
            var card = CardFrame(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = Loc.T(info.Name), FontWeight = FontWeights.SemiBold, FontSize = 14 },
                    about,
                    buttons,
                },
            }, new Thickness(14, 12, 14, 12));
            card.Width = 236;
            card.Margin = new Thickness(0, 0, 8, 8);
            card.ToolTip = Loc.T(info.Description);
            cards.Children.Add(card);
        }
        p.Children.Add(cards);
    }

    private void BuildFineTuning(Panel p)
    {
        var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        bool centre = CentreMode(MiniBar());
        AddToggle(body, "Étirer les modules pour remplir chaque zone", S.FillZones, v =>
        {
            S.FillZones = v;
            DrawMini();
        }, "Les modules s'élargissent pour occuper toute la place libre.", "");
        if (!centre)
        {
            AddCombo(body, "Zone gauche : placer les modules", new[] { "Contre le bord de l'écran", "Au centre de la zone", "Contre les icônes" },
                (int)S.LeftAlign, i =>
                {
                    S.LeftAlign = (ZoneAlign)i;
                    DrawMini();
                }, icon: "");
            AddCombo(body, "Zone droite : placer les modules", new[] { "Contre les icônes", "Au centre de la zone", "Contre la zone de notification" },
                (int)S.RightAlign, i =>
                {
                    S.RightAlign = (ZoneAlign)i;
                    DrawMini();
                }, icon: "");
            body.Children.Add(Hint("Le placement s'applique quand les modules ne sont pas étirés."));
        }
        AddSlider(body, "Espacement entre les modules", 0, 48, 1, S.Spacing, v => $"{v:0} px", v =>
        {
            S.Spacing = (int)v;
            DrawMini();
        }, icon: "");
        AddSlider(body, "Fondu des bords", 0, 40, 1, S.EdgeFade, v => $"{v:0} px", v => S.EdgeFade = (int)v,
            "Les bords des modules s'estompent dans la barre.", "");
        AddSlider(body, "Marge verticale", 0, 10, 1, S.VerticalPadding, v => $"{v:0} px", v => S.VerticalPadding = (int)v,
            "Les tailles sont exprimées à 100 % d'échelle et suivent automatiquement le DPI de l'écran.", "");
        ActionCard(body, "Réinitialiser la disposition", "Réinitialiser", () =>
        {
            S.ResetLayout();
            _selected = null;
            Changed();
            BuildModules();
            BuildLayout();
        }, "Remet les modules et leur placement par défaut.", "");

        p.Children.Add(Folded("layout.fine", "Réglages fins", body, new Thickness(0, 22, 0, 0)));
    }

    /// <summary>Auto-test : choisit le premier module affiché et déplie les réglages fins, pour la capture.</summary>
    internal void PrepareLayoutSnapshot()
    {
        _selected = S.AllModules.FirstOrDefault(c => c.Enabled);
        _expanded.Add("layout.fine");
        BuildLayout();
    }

    private static Brush AccentBrush(bool light)
    {
        var c = Theme.AccentColor(light);
        return new SolidColorBrush(c is { } a ? Color.FromRgb(a.Red, a.Green, a.Blue) : Color.FromRgb(0x4C, 0xC2, 0xFF));
    }

    // Modules dessinés sur une barre en miniature : sombres sur une barre sombre, clairs sur une barre claire.
    private static Brush ChipFill() => new SolidColorBrush(App.TaskbarIsLight ? Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xD8, 0x14, 0x14, 0x1A));
    private static Brush ChipEdge() => new SolidColorBrush(App.TaskbarIsLight ? Color.FromArgb(0x38, 0, 0, 0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static Brush ChipText() => new SolidColorBrush(App.TaskbarIsLight ? Color.FromRgb(0x20, 0x20, 0x28) : Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));

    private static Color ToColor(string hex) =>
        Gradients.Parse(hex) is { } c ? Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B) : Colors.White;
}
