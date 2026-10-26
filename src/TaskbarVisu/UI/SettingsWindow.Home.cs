using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TaskbarVisu.Core;
using TaskbarVisu.Modules;

namespace TaskbarVisu.UI;

/// <summary>
/// Page d'accueil : l'état de TaskbarVisu en un coup d'œil (son, barre, visualiseurs), le style en un clic,
/// les modules affichés, et un lien vers chaque page pour aller plus loin.
/// </summary>
public partial class SettingsWindow
{
    private TextBlock? _homeAudio, _homeBar, _homeState;
    private Button? _homeHide, _homePause;

    private void BuildHome()
    {
        var p = HomePanel;
        p.Children.Clear();
        PageHeader(p, "Accueil", "TaskbarVisu en un coup d'œil. Tout se règle en direct : regardez votre barre des tâches.");

        // 1. État : trois tuiles qui se mettent à jour chaque seconde.
        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 14, -8, 0) };
        _homeAudio = Tile(tiles, "", "Son");
        _homeBar = Tile(tiles, "", "Barre des tâches");
        _homeState = Tile(tiles, "", "Visualiseurs");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _homeHide = new Button { MinWidth = 96, Margin = new Thickness(0, 0, 6, 0) };
        _homeHide.Click += (_, _) =>
        {
            _app.ToggleVisuals();
            RefreshHome();
        };
        _homePause = new Button { MinWidth = 96 };
        _homePause.Click += (_, _) =>
        {
            _app.TogglePause();
            RefreshHome();
        };
        actions.Children.Add(_homeHide);
        actions.Children.Add(_homePause);
        ((StackPanel)_homeState.Parent).Children.Add(actions);
        p.Children.Add(tiles);

        // 2. Style en un clic.
        p.Children.Add(Section("Style"));
        p.Children.Add(PresetGrid(4, null));
        p.Children.Add(Link("Couleurs, animation et effets", () => GoTo(TabAppearance)));

        // 3. Modules affichés : un clic ouvre le module sur la page Disposition.
        p.Children.Add(Section("Sur la barre"));
        var shown = S.AllModules.Where(m => m.Enabled).ToList();
        if (shown.Count == 0)
        {
            ActionCard(p, "La barre est vide", "Ajouter des modules", () => GoTo(TabLayout),
                "Choisissez les visualiseurs à afficher : spectre, oscilloscope, vumètre…", "");
        }
        else
        {
            var chips = new WrapPanel();
            foreach (var cfg in shown)
            {
                var info = ModuleCatalog.Find(cfg.Id)!;
                var chip = new Button
                {
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            new System.Windows.Shapes.Rectangle
                            {
                                Width = 14, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Margin = new Thickness(0, 0, 8, 0),
                                VerticalAlignment = VerticalAlignment.Center,
                                Fill = cfg.Colors == ModuleColorMode.Solid
                                    ? new System.Windows.Media.SolidColorBrush(ToColor(cfg.ColorSolid))
                                    : PaletteBrush(cfg.Colors == ModuleColorMode.Palette ? cfg.ColorPalette : S.Palette,
                                        cfg.Colors == ModuleColorMode.Palette ? cfg.Gradient : S.Gradient),
                            },
                            new TextBlock { Text = Loc.T(info.Name) },
                        },
                    },
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 0, 6, 6),
                    ToolTip = Loc.T(info.Description),
                };
                chip.Click += (_, _) =>
                {
                    _selected = cfg;
                    BuildLayout();
                    GoTo(TabLayout);
                };
                chips.Children.Add(chip);
            }
            p.Children.Add(chips);
            p.Children.Add(Link("Ajouter, déplacer ou retirer des modules", () => GoTo(TabLayout)));
        }

        // 4. Raccourcis utiles.
        p.Children.Add(Section("Raccourcis"));
        if (S.Hotkey != HotkeyChoice.None)
        {
            var hotkey = Card(string.Empty, null, "", "Fonctionne partout, même en jeu. Se change dans la page Général.");
            ((TextBlock)((CardParts)hotkey.Tag).Texts.Children[0]).Text = Loc.F("{0} : masquer ou afficher les visualiseurs", _app.HotkeyText);
            p.Children.Add(hotkey);
        }
        p.Children.Add(Card("Icône de la zone de notification", null, "",
            "Clic : ces réglages. Clic droit : masquer, pause, mise à jour, quitter."));
        ActionCard(p, "Guide de démarrage", "Revoir le guide", ShowWelcome,
            "Les cinq étapes du premier lancement, à rejouer quand vous voulez.", "");

        RefreshHome();
    }

    /// <summary>Tuile d'état : icône et titre, puis une valeur mise à jour en direct.</summary>
    private static TextBlock Tile(Panel tiles, string glyph, string title)
    {
        var icon = Glyph(glyph, 20);
        icon.Margin = new Thickness(0, 0, 0, 10);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        var value = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var label = Description(Loc.T(title));
        label.Margin = new Thickness(0);
        var stack = new StackPanel { Children = { icon, label, value } };
        var card = CardFrame(stack, new Thickness(16, 14, 16, 14));
        card.Margin = new Thickness(0, 0, 8, 8);
        tiles.Children.Add(card);
        return value;
    }

    private void RefreshHome()
    {
        if (_homeAudio is null || _homeBar is null || _homeState is null || _homeHide is null || _homePause is null) return;
        _homeAudio.Text = _app.Audio.StatusText;
        var bar = _app.Tracker.Current;
        _homeBar.Text = !bar.Valid || !bar.Supported ? Loc.T(bar.Message)
            : CentreMode(bar) ? Loc.F("Espace libre au centre : {0} px", bar.Spanning.Width)
            : Loc.F("Zones libres : gauche {0} px · droite {1} px", bar.Left.Width, bar.Right.Width);
        int count = S.AllModules.Count(m => m.Enabled);
        _homeState.Text = _app.VisualsHidden ? Loc.T("Masqués")
            : _app.IsPaused ? Loc.T("En pause")
            : count == 0 ? Loc.T("Aucun module affiché")
            : Loc.F("{0} module(s) affiché(s)", count);
        _homeHide.Content = Loc.T(_app.VisualsHidden ? "Afficher" : "Masquer");
        _homePause.Content = Loc.T(_app.IsPaused ? "Reprendre" : "Pause");
    }
}
