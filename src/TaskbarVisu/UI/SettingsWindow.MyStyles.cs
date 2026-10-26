using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarVisu.Core;

namespace TaskbarVisu.UI;

/// <summary>
/// « Mes styles » (page Apparence) : le style actuel (palette, animation, textes, effets, fond, couleurs des modules)
/// enregistré sous un nom et réappliqué en un clic, ici comme sur la page Accueil. Le menu « ⋯ » d'une carte
/// la met à jour, la renomme, copie son code ou la supprime.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>Message à afficher sous « Mes styles » une fois la page reconstruite (style enregistré, renommé…).</summary>
    private string? _styleNote;

    /// <summary>Dernier style enregistré ou appliqué : entouré en priorité si plusieurs styles identiques correspondent.</summary>
    private SavedStyle? _lastStyle;

    /// <summary>Le style enregistré qui correspond exactement au style actuel, s'il y en a un.</summary>
    private SavedStyle? CurrentSavedStyle() =>
        _lastStyle is not null && S.MyStyles.Contains(_lastStyle) && StyleCode.Matches(S, _lastStyle.Code)
            ? _lastStyle
            : S.MyStyles.FirstOrDefault(x => StyleCode.Matches(S, x.Code));

    private void BuildMyStyles(Panel page)
    {
        page.Children.Add(Section("Mes styles"));
        var accent = AccentBrush(App.UiIsLight);
        var current = CurrentSavedStyle();
        void Rebuild() => RebuildKeepingScroll(AppearancePanel, BuildAppearance);

        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8), Text = _styleNote ?? string.Empty };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        note.Visibility = _styleNote is null ? Visibility.Collapsed : Visibility.Visible;
        _styleNote = null;
        void Say(string text)
        {
            note.Text = text;
            note.Visibility = Visibility.Visible;
        }

        if (S.MyStyles.Count == 0)
        {
            var empty = Description(Loc.T("Réglez les couleurs et les effets à votre goût, puis enregistrez-les ici pour les retrouver en un clic."));
            empty.Margin = new Thickness(2, 0, 0, 8);
            page.Children.Add(empty);
        }

        // Nom du style, sous les cartes : pour enregistrer le style actuel ou renommer une carte.
        SavedStyle? renaming = null;
        var nameBox = new TextBox { Width = 260, MaxLength = 40, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(nameBox, Loc.T("Nom du style"));
        var save = new Button { Content = Loc.T("Enregistrer"), Margin = new Thickness(8, 0, 0, 0), Style = TryFindResource("AccentButtonStyle") as Style };
        var cancel = new Button { Content = Loc.T("Annuler"), Margin = new Thickness(8, 0, 0, 0) };
        var editor = WideCard("Nom du style", new WrapPanel { Children = { nameBox, save, cancel } }, "");
        editor.Visibility = Visibility.Collapsed;
        editor.Margin = new Thickness(0, 0, 0, 8);

        void OpenEditor(SavedStyle? style)
        {
            renaming = style;
            nameBox.Text = style?.Name ?? NextStyleName();
            editor.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(() =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            }, DispatcherPriority.Input);
        }

        void Commit()
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0) name = NextStyleName();
            if (renaming is not null)
            {
                renaming.Name = name;
                _styleNote = Loc.F("Style renommé : « {0} ».", name);
            }
            else if (S.MyStyles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)) is { } same)
            {
                // Même nom qu'un style existant : on le met à jour plutôt que d'avoir deux cartes identiques.
                same.Code = StyleCode.ToCode(S);
                _lastStyle = same;
                _styleNote = Loc.F("« {0} » mis à jour avec le style actuel.", name);
            }
            else
            {
                _lastStyle = new SavedStyle { Name = name, Code = StyleCode.ToCode(S) };
                S.MyStyles.Add(_lastStyle);
                _styleNote = Loc.F("Style « {0} » enregistré : il est aussi sur la page Accueil.", name);
            }
            Changed();
            Rebuild();
        }

        save.Click += (_, _) => Commit();
        cancel.Click += (_, _) => editor.Visibility = Visibility.Collapsed;
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
            else if (e.Key == Key.Escape) editor.Visibility = Visibility.Collapsed;
            else return;
            e.Handled = true;
        };

        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, -8, 0) };
        foreach (var style in S.MyStyles)
        {
            var more = new Button
            {
                Content = Glyph("", 14), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(4, 0, -4, 0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = Loc.T("Plus d'options"),
            };
            AutomationProperties.SetName(more, Loc.T("Plus d'options"));
            var footer = new DockPanel { Margin = new Thickness(0, 4, 0, -3) };
            DockPanel.SetDock(more, Dock.Right);
            footer.Children.Add(more);
            footer.Children.Add(new TextBlock { Text = style.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

            var card = CardButton(new StackPanel { Children = { SavedStylePreview(style, 140, 38), footer } }, ReferenceEquals(style, current), accent);
            card.Margin = new Thickness(0, 0, 8, 8);
            card.ToolTip = style.Name;
            card.Click += (_, _) =>
            {
                StyleCode.TryApply(style.Code, S);
                _lastStyle = style;
                Changed();
                BuildModules();
                Rebuild();
            };
            var menu = StyleMenu(style, () => OpenEditor(style), Rebuild, Say);
            card.ContextMenu = menu;
            more.Click += (_, e) =>
            {
                // Le clic sur « ⋯ » n'applique pas le style.
                e.Handled = true;
                menu.PlacementTarget = more;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            grid.Children.Add(card);
        }

        // Dernière carte : enregistrer le style actuel.
        bool full = S.MyStyles.Count >= AppSettings.MaxStyles;
        var plus = Glyph("", 16);
        plus.HorizontalAlignment = HorizontalAlignment.Center;
        var add = CardButton(new StackPanel
        {
            Children =
            {
                new Border { Height = 38, Child = plus },
                new TextBlock { Text = Loc.T("Enregistrer ce style"), Margin = new Thickness(0, 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
            },
        }, false, accent);
        add.Margin = new Thickness(0, 0, 8, 8);
        add.IsEnabled = !full;
        add.ToolTip = full ? Loc.F("{0} styles au maximum : supprimez-en un pour en enregistrer un autre.", AppSettings.MaxStyles)
            : Loc.T("Enregistrer le style actuel");
        AutomationProperties.SetName(add, Loc.T("Enregistrer le style actuel"));
        ToolTipService.SetShowOnDisabled(add, true);
        add.Click += (_, _) => OpenEditor(null);
        grid.Children.Add(add);

        page.Children.Add(grid);
        page.Children.Add(editor);
        page.Children.Add(note);
    }

    /// <summary>Menu d'une carte de « Mes styles » (clic droit ou bouton « ⋯ »).</summary>
    private ContextMenu StyleMenu(SavedStyle style, Action rename, Action rebuild, Action<string> say)
    {
        var menu = new ContextMenu();
        void Item(string text, string glyph, Action action)
        {
            var item = new MenuItem { Header = Loc.T(text), Icon = Glyph(glyph, 14) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Item("Mettre à jour avec le style actuel", "", () =>
        {
            style.Code = StyleCode.ToCode(S);
            _lastStyle = style;
            _styleNote = Loc.F("« {0} » mis à jour avec le style actuel.", style.Name);
            Changed();
            rebuild();
        });
        Item("Renommer…", "", rename);
        Item("Copier le code", "", () =>
        {
            try
            {
                Clipboard.SetText(style.Code);
                say(Loc.T("Code copié : collez-le où vous voulez (Discord, message…)."));
            }
            catch (Exception ex)
            {
                // Presse-papiers occupé par une autre application.
                Log.Error("Copie du code d'un style", ex);
                say(Loc.T("Opération impossible pour le moment, réessayez."));
            }
        });
        menu.Items.Add(new Separator());
        Item("Supprimer", "", () =>
        {
            var answer = MessageBox.Show(this, Loc.F("Supprimer le style « {0} » ?", style.Name), "TaskbarVisu",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            S.MyStyles.Remove(style);
            Changed();
            rebuild();
        });
        return menu;
    }

    private string NextStyleName()
    {
        for (int n = S.MyStyles.Count + 1; ; n++)
        {
            string name = Loc.F("Mon style {0}", n);
            if (!S.MyStyles.Any(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase))) return name;
        }
    }
}
