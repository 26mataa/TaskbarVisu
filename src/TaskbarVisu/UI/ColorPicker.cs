using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TaskbarVisu.UI;

/// <summary>
/// Bouton-pastille qui ouvre un sélecteur de couleur : carré saturation / luminosité,
/// bande de teinte, couleurs prédéfinies et saisie hexadécimale.
/// </summary>
internal sealed class ColorPickerButton : StackPanel
{
    private static readonly string[] Presets =
    {
        "#D4DCFF", "#FFFFFF", "#7FB2FF", "#00E5FF", "#39FF14", "#C8EF34",
        "#FFE600", "#FFB020", "#FF7A1A", "#FF3B30", "#FF2D95", "#BF5AF2",
    };

    private readonly Border _swatch;
    private readonly TextBlock _label;
    private readonly Popup _popup;
    private readonly Rectangle _hueLayer;
    private readonly Canvas _svCanvas, _hueCanvas;
    private readonly Ellipse _svThumb;
    private readonly Rectangle _hueThumb;
    private readonly TextBox _hex;
    private readonly Border _preview;
    private double _h, _s, _v;
    private bool _updating;

    private const double SvWidth = 220, SvHeight = 140, HueHeight = 14;

    public ColorPickerButton(string hex)
    {
        Orientation = Orientation.Horizontal;

        _swatch = new Border
        {
            Width = 34, Height = 22, CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
        };
        var button = new Button
        {
            Padding = new Thickness(6, 4, 10, 4),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { _swatch, (_label = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") }) },
            },
        };
        Children.Add(button);

        // Carré saturation (horizontal) / luminosité (vertical) sur la teinte courante.
        _hueLayer = new Rectangle { Width = SvWidth, Height = SvHeight };
        var white = new Rectangle
        {
            Width = SvWidth, Height = SvHeight,
            Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
        };
        var black = new Rectangle
        {
            Width = SvWidth, Height = SvHeight,
            Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
        };
        _svThumb = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        _svCanvas = new Canvas { Width = SvWidth, Height = SvHeight, ClipToBounds = true, Background = Brushes.Transparent, Cursor = Cursors.Cross };
        _svCanvas.Children.Add(_hueLayer);
        _svCanvas.Children.Add(white);
        _svCanvas.Children.Add(black);
        _svCanvas.Children.Add(_svThumb);
        HookDrag(_svCanvas, pos =>
        {
            _s = Math.Clamp(pos.X / SvWidth, 0, 1);
            _v = 1 - Math.Clamp(pos.Y / SvHeight, 0, 1);
            Commit();
        });

        // Bande de teinte.
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));
        _hueThumb = new Rectangle { Width = 4, Height = HueHeight + 4, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
        _hueCanvas = new Canvas { Width = SvWidth, Height = HueHeight + 4, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var hueRect = new Rectangle { Width = SvWidth, Height = HueHeight, Fill = rainbow, RadiusX = 3, RadiusY = 3 };
        Canvas.SetTop(hueRect, 2);
        _hueCanvas.Children.Add(hueRect);
        _hueCanvas.Children.Add(_hueThumb);
        HookDrag(_hueCanvas, pos =>
        {
            _h = Math.Clamp(pos.X / SvWidth, 0, 1) * 360;
            Commit();
        });

        // Couleurs prédéfinies.
        var presets = new WrapPanel { Width = SvWidth, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var preset in Presets)
        {
            var chip = new Border
            {
                Width = 16, Height = 16, Margin = new Thickness(0, 0, 2.3, 2.3), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Parse(preset)!.Value), Cursor = Cursors.Hand, ToolTip = preset,
            };
            chip.MouseLeftButtonUp += (_, _) => SetColor(preset, raise: true);
            presets.Children.Add(chip);
        }

        _hex = new TextBox { Width = 110, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalAlignment = VerticalAlignment.Center };
        _hex.TextChanged += (_, _) =>
        {
            if (_updating) return;
            var text = _hex.Text.Trim();
            if (!text.StartsWith('#')) text = "#" + text;
            if (Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$")) SetColor(text, raise: true, keepText: true);
        };
        _preview = new Border { Width = 34, Height = 26, CornerRadius = new CornerRadius(4), Margin = new Thickness(10, 0, 0, 0) };
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { _hex, _preview } };

        bool light = App.UiIsLight;
        var content = new StackPanel { Children = { _svCanvas, new Border { Height = 10 }, _hueCanvas, presets, hexRow } };
        _popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Padding = new Thickness(14),
                Margin = new Thickness(0, 4, 0, 0),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(light ? Color.FromRgb(0xF9, 0xF9, 0xF9) : Color.FromRgb(0x2C, 0x2C, 0x2C)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                BorderThickness = new Thickness(1),
                Child = content,
            },
        };
        button.Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;

        SetColor(hex, raise: false);
    }

    /// <summary>Nouvelle couleur choisie, au format #RRVVBB.</summary>
    public event Action<string>? ColorChanged;

    public string Hex { get; private set; } = "#FFFFFF";

    /// <summary>Affiche une autre couleur sans déclencher <see cref="ColorChanged"/>.</summary>
    public void SetHex(string hex) => SetColor(hex, raise: false);

    /// <summary>Diagnostic : rend le contenu du sélecteur dans une image, sans ouvrir la fenêtre surgissante.</summary>
    public void RenderPreview(string path)
    {
        if (_popup.Child is not FrameworkElement child) return;
        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        child.Arrange(new Rect(child.DesiredSize));
        child.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(child.ActualWidth), (int)Math.Ceiling(child.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(child);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private void HookDrag(Canvas canvas, Action<Point> apply)
    {
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            canvas.CaptureMouse();
            apply(e.GetPosition(canvas));
        };
        canvas.MouseMove += (_, e) =>
        {
            if (canvas.IsMouseCaptured) apply(e.GetPosition(canvas));
        };
        canvas.MouseLeftButtonUp += (_, _) => canvas.ReleaseMouseCapture();
    }

    private void SetColor(string hex, bool raise, bool keepText = false)
    {
        var color = Parse(hex);
        if (color is null) return;
        (_h, _s, _v) = ToHsv(color.Value);
        Apply(color.Value, raise, keepText);
    }

    private void Commit() => Apply(FromHsv(_h, _s, _v), raise: true, keepText: false);

    private void Apply(Color color, bool raise, bool keepText)
    {
        Hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        var brush = new SolidColorBrush(color);
        _swatch.Background = brush;
        _preview.Background = brush;
        _label.Text = Hex;
        _hueLayer.Fill = new SolidColorBrush(FromHsv(_h, 1, 1));
        Canvas.SetLeft(_svThumb, _s * SvWidth - 6);
        Canvas.SetTop(_svThumb, (1 - _v) * SvHeight - 6);
        _svThumb.Stroke = _v > 0.55 && _s < 0.4 ? Brushes.Black : Brushes.White;
        Canvas.SetLeft(_hueThumb, _h / 360 * SvWidth - 2);
        if (!keepText)
        {
            _updating = true;
            _hex.Text = Hex;
            _updating = false;
        }
        if (raise) ColorChanged?.Invoke(Hex);
    }

    private static Color? Parse(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d + 6) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
