using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TaskbarVisu.Core;

namespace TaskbarVisu.UI;

/// <summary>
/// Éditeur de dégradé personnalisé : barre d'aperçu (un clic ajoute une couleur, 8 au plus), repères à glisser
/// (clic droit = supprimer), couleur du repère choisi avec le sélecteur habituel, inversion,
/// et départ depuis une palette existante.
/// </summary>
internal sealed class GradientEditor : StackPanel
{
    private const double HandleWidth = 12, HandleHeight = 18, BarHeight = 24;

    private readonly List<ColorStop> _stops;
    private readonly Action<List<ColorStop>> _changed;
    private readonly Border _bar;
    private readonly Canvas _canvas;
    private readonly ColorPickerButton _picker;
    private readonly Button _remove;
    private ColorStop? _selected;
    private ColorStop? _dragging;

    /// <param name="stops">Dégradé de départ (copié).</param>
    /// <param name="changed">Reçoit une copie du dégradé à chaque modification.</param>
    /// <param name="palettes">Palettes proposées dans « Partir de » (aperçu + nom).</param>
    /// <param name="template">Présentation des palettes dans la liste.</param>
    /// <param name="fromPalette">Dégradé correspondant à la palette n° i de <paramref name="palettes"/>.</param>
    public GradientEditor(List<ColorStop> stops, Action<List<ColorStop>> changed,
        System.Collections.IList palettes, DataTemplate template, Func<int, List<ColorStop>> fromPalette)
    {
        _stops = Gradients.Copy(stops);
        _changed = changed;
        MaxWidth = 460;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        // Barre + repères : la barre est décalée d'une demi-poignée pour que 0 et 1 tombent sur ses bords.
        _bar = new Border
        {
            Height = BarHeight,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(HandleWidth / 2, 0, HandleWidth / 2, 0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
            Cursor = Cursors.Cross,
            ToolTip = Loc.T("Cliquez pour ajouter une couleur"),
        };
        _canvas = new Canvas { Height = HandleHeight + 2, Background = Brushes.Transparent, ClipToBounds = false };
        var track = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        track.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        track.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        track.Children.Add(_bar);
        Grid.SetRow(_canvas, 1);
        track.Children.Add(_canvas);
        Children.Add(track);

        _bar.MouseLeftButtonDown += (_, e) =>
        {
            if (_stops.Count >= Gradients.MaxStops) return;
            double pos = Math.Clamp(e.GetPosition(_bar).X / Math.Max(1, _bar.ActualWidth), 0, 1);
            var stop = new ColorStop(Math.Round(pos, 3), Gradients.At(_stops, pos));
            _stops.Add(stop);
            Sort();
            Select(stop);
            Commit();
        };
        _canvas.SizeChanged += (_, _) => Refresh();
        _canvas.MouseMove += (_, e) =>
        {
            if (_dragging is null || e.LeftButton != MouseButtonState.Pressed) return;
            double width = Math.Max(1, _canvas.ActualWidth - HandleWidth);
            _dragging.Pos = Math.Round(Math.Clamp((e.GetPosition(_canvas).X - HandleWidth / 2) / width, 0, 1), 3);
            Sort();
            Refresh();
            Commit();
        };
        _canvas.MouseLeftButtonUp += (_, _) => EndDrag();
        _canvas.LostMouseCapture += (_, _) => _dragging = null;

        // Couleur du repère choisi, suppression, inversion, palette de départ.
        _picker = new ColorPickerButton(_stops[0].Color);
        _picker.ColorChanged += hex =>
        {
            if (_selected is null) return;
            _selected.Color = hex;
            Refresh();
            Commit();
        };
        _remove = new Button { Content = Loc.T("Retirer"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        _remove.Click += (_, _) => RemoveSelected();
        var reverse = new Button { Content = Loc.T("Inverser"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        reverse.Click += (_, _) =>
        {
            foreach (var s in _stops) s.Pos = Math.Round(1 - s.Pos, 3);
            Sort();
            Refresh();
            Commit();
        };
        Children.Add(new WrapPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children = { _picker, _remove, reverse },
        });

        var start = new ComboBox { ItemsSource = palettes, ItemTemplate = template, MinWidth = 220, Margin = new Thickness(8, 0, 0, 0) };
        start.SelectionChanged += (_, _) =>
        {
            if (start.SelectedIndex < 0) return;
            _stops.Clear();
            _stops.AddRange(fromPalette(start.SelectedIndex));
            Sort();
            Select(_stops[0]);
            Commit();
            start.SelectedIndex = -1; // la liste sert d'action : elle redevient vide
        };
        Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { new TextBlock { Text = Loc.T("Partir de :"), VerticalAlignment = VerticalAlignment.Center }, start },
        });

        Select(_stops[0]);
    }

    private void Sort() => _stops.Sort((a, b) => a.Pos.CompareTo(b.Pos));

    private void Select(ColorStop stop)
    {
        _selected = stop;
        _picker.SetHex(stop.Color);
        Refresh();
    }

    private void RemoveSelected()
    {
        if (_selected is null || _stops.Count <= 2) return;
        int index = _stops.IndexOf(_selected);
        _stops.Remove(_selected);
        Select(_stops[Math.Clamp(index, 0, _stops.Count - 1)]);
        Commit();
    }

    private void EndDrag()
    {
        if (_dragging is null) return;
        _dragging = null;
        _canvas.ReleaseMouseCapture();
    }

    private void Commit() => _changed(Gradients.Copy(_stops));

    /// <summary>Redessine l'aperçu et replace les poignées.</summary>
    private void Refresh()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        foreach (var s in _stops) brush.GradientStops.Add(new GradientStop(ToColor(s.Color), s.Pos));
        brush.Freeze();
        _bar.Background = brush;
        _remove.IsEnabled = _stops.Count > 2;

        _canvas.Children.Clear();
        double width = Math.Max(1, _canvas.ActualWidth - HandleWidth);
        bool light = Rendering.Theme.AppsUseLightTheme();
        foreach (var stop in _stops)
        {
            bool selected = ReferenceEquals(stop, _selected);
            var handle = new Border
            {
                Width = HandleWidth,
                Height = HandleHeight,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(ToColor(stop.Color)),
                BorderThickness = new Thickness(selected ? 2.5 : 1),
                BorderBrush = selected
                    ? (light ? Brushes.Black : Brushes.White)
                    : new SolidColorBrush(Color.FromArgb(0x90, 0x80, 0x80, 0x80)),
                Cursor = Cursors.SizeWE,
                ToolTip = Loc.T("Glisser pour déplacer · clic droit pour retirer"),
            };
            Canvas.SetLeft(handle, stop.Pos * width);
            Canvas.SetTop(handle, 2);
            handle.MouseLeftButtonDown += (_, e) =>
            {
                Select(stop);
                _dragging = stop;
                _canvas.CaptureMouse();
                e.Handled = true;
            };
            handle.MouseRightButtonUp += (_, e) =>
            {
                Select(stop);
                RemoveSelected();
                e.Handled = true;
            };
            _canvas.Children.Add(handle);
        }
    }

    private static Color ToColor(string hex) =>
        Gradients.Parse(hex) is { } c ? Color.FromRgb((byte)c.R, (byte)c.G, (byte)c.B) : Colors.White;
}
