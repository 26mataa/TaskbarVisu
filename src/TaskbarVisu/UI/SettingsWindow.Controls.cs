using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TaskbarVisu.Core;

namespace TaskbarVisu.UI;

/// <summary>
/// Fabrique de contrôles façon Paramètres de Windows 11 : titres de page, sections, et réglages en cartes
/// (icône, titre et explication à gauche, contrôle à droite). Une aide (<see cref="Hint"/>) posée juste après une carte
/// rejoint son explication (<see cref="MergeHints"/>) : chaque réglage porte son mode d'emploi.
/// </summary>
public partial class SettingsWindow
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Marque les aides, pour les rattacher à la carte qui les précède.</summary>
    private static readonly object HintTag = new();

    /// <summary>Ce qu'une carte expose : la colonne des textes, où viennent s'ajouter les aides.</summary>
    private sealed record CardParts(StackPanel Texts);

    private static void PageHeader(Panel p, string title, string subtitle)
    {
        p.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 2) });
        var sub = new TextBlock { Text = Loc.T(subtitle), FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 4) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        p.Children.Add(sub);
    }

    private static TextBlock Section(string title) => new()
    {
        Text = Loc.T(title),
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 22, 0, 8),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = Loc.T(text),
        Opacity = 0.7,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 2, 0, 8),
        Tag = HintTag,
    };

    private static TextBlock Description(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    private static TextBlock Glyph(string glyph, double size = 18) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Fond et bordure d'une carte, aux couleurs du thème (clair ou sombre).</summary>
    private static Border CardFrame(UIElement child, Thickness padding)
    {
        var border = new Border
        {
            Child = child,
            Padding = padding,
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }

    /// <summary>Carte de réglage : [icône] titre + explication, contrôle à droite.</summary>
    private static Border Card(string title, FrameworkElement? control, string? icon = null, string? description = null)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = Loc.T(title), TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        if (description is not null) texts.Children.Add(Description(Loc.T(description)));

        var grid = new Grid { MinHeight = 38 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon is not null)
        {
            var glyph = Glyph(icon);
            glyph.Margin = new Thickness(0, 0, 16, 0);
            grid.Children.Add(glyph);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (control is not null)
        {
            control.Margin = new Thickness(20, 0, 0, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }
        var card = CardFrame(grid, new Thickness(16, 11, 16, 11));
        card.Tag = new CardParts(texts);
        return card;
    }

    /// <summary>Carte dont le contenu prend toute la largeur, sous le titre (éditeur de dégradé…).</summary>
    private static Border WideCard(string title, FrameworkElement content, string? icon = null)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
        {
            var glyph = Glyph(icon);
            glyph.Margin = new Thickness(0, 0, 16, 0);
            header.Children.Add(glyph);
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = Loc.T(title), FontSize = 14 });
        header.Children.Add(texts);
        content.Margin = new Thickness(0, 10, 0, 0);
        var card = CardFrame(new StackPanel { Children = { header, content } }, new Thickness(16, 12, 16, 12));
        card.Tag = new CardParts(texts);
        return card;
    }

    /// <summary>Rattache chaque aide à la carte qui la précède (récursif : sections repliables, panneaux).</summary>
    private static void MergeHints(Panel panel)
    {
        for (int i = 0; i < panel.Children.Count; i++)
        {
            var child = panel.Children[i];
            if (i > 0 && child is TextBlock { Tag: var tag } hint && ReferenceEquals(tag, HintTag)
                && panel.Children[i - 1] is Border { Tag: CardParts parts })
            {
                panel.Children.RemoveAt(i--);
                hint.Opacity = 1;
                hint.FontSize = 12;
                hint.Margin = new Thickness(0, 2, 0, 0);
                hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                parts.Texts.Children.Add(hint);
                continue;
            }
            switch (child)
            {
                case Panel inner: MergeHints(inner); break;
                case Expander { Content: Panel content }: MergeHints(content); break;
                case Border { Tag: not CardParts, Child: Panel boxed }: MergeHints(boxed); break;
            }
        }
    }

    /// <summary>Lien discret (« Modifier la disposition → »).</summary>
    private static TextBlock Link(string text, Action action)
    {
        var link = new Hyperlink(new Run(Loc.T(text) + "  →")) { TextDecorations = null };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        link.Click += (_, _) => action();
        return new TextBlock(link) { Margin = new Thickness(0, 8, 0, 0), FontSize = 13 };
    }

    private Border AddToggle(Panel panel, string label, bool value, Action<bool> set, string? description = null, string? icon = null)
    {
        var toggle = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = value };
        AutomationProperties.SetName(toggle, Loc.T(label));
        toggle.Checked += (_, _) => { set(true); Changed(); };
        toggle.Unchecked += (_, _) => { set(false); Changed(); };
        var card = Card(label, toggle, icon, description);
        // Toute la carte est cliquable, comme dans les Paramètres de Windows.
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            toggle.IsChecked = toggle.IsChecked != true;
            e.Handled = true;
        };
        panel.Children.Add(card);
        return card;
    }

    private Border AddSlider(Panel panel, string label, double min, double max, double step, double value,
        Func<double, string> format, Action<double> set, string? description = null, string? icon = null)
    {
        var valueText = new TextBlock
        {
            Text = format(value),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            MinWidth = 64,
            Margin = new Thickness(0, 0, 12, 0),
        };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            SmallChange = step,
            LargeChange = step * 4,
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, Loc.T(label));
        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = format(e.NewValue);
            set(e.NewValue);
            Changed();
        };
        var card = Card(label, new StackPanel { Orientation = Orientation.Horizontal, Children = { valueText, slider } }, icon, description);
        panel.Children.Add(card);
        return card;
    }

    private Border AddCombo(Panel panel, string label, System.Collections.IList items, int index, Action<int> set,
        DataTemplate? template = null, bool translate = true, string? description = null, string? icon = null)
    {
        // Les listes de textes sont traduites ici (sauf celles déjà mises en forme par l'appelant).
        if (translate && items is IEnumerable<string> texts) items = texts.Select(Loc.T).ToList();
        var combo = new ComboBox
        {
            ItemsSource = items,
            ItemTemplate = template,
            SelectedIndex = Math.Clamp(index, 0, Math.Max(0, items.Count - 1)),
            MinWidth = 240,
            MaxWidth = 340,
        };
        AutomationProperties.SetName(combo, Loc.T(label));
        combo.SelectionChanged += (_, e) =>
        {
            e.Handled = true; // ne remonte pas jusqu'à la navigation
            if (combo.SelectedIndex < 0) return;
            set(combo.SelectedIndex);
            Changed();
        };
        var card = Card(label, combo, icon, description);
        panel.Children.Add(card);
        return card;
    }

    /// <summary>Carte avec un bouton d'action à droite.</summary>
    private static Border ActionCard(Panel panel, string label, string button, Action action, string? description = null, string? icon = null)
    {
        var b = new Button { Content = Loc.T(button), MinWidth = 120 };
        b.Click += (_, _) => action();
        var card = Card(label, b, icon, description);
        panel.Children.Add(card);
        return card;
    }

    /// <summary>Bloc repliable dont l'état (ouvert / fermé) survit aux reconstructions de la page.</summary>
    private Expander Folded(string key, string header, UIElement content, Thickness margin)
    {
        var expander = new Expander
        {
            Header = Loc.T(header),
            Content = content,
            IsExpanded = _expanded.Contains(key),
            Margin = margin,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        expander.Expanded += (_, _) => _expanded.Add(key);
        expander.Collapsed += (_, _) => _expanded.Remove(key);
        return expander;
    }

    /// <summary>Reconstruit une page sans la faire remonter tout en haut.</summary>
    private void RebuildKeepingScroll(Panel page, Action build)
    {
        var scroll = page.Parent as ScrollViewer;
        double offset = scroll?.VerticalOffset ?? 0;
        build();
        if (scroll is not null)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => scroll.ScrollToVerticalOffset(offset)));
    }

    private static int NearestIndex(int[] values, int value)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
            if (Math.Abs(values[i] - value) < Math.Abs(values[best] - value)) best = i;
        return best;
    }
}
