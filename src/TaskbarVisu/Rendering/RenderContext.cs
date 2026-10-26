using SkiaSharp;

namespace TaskbarVisu.Rendering;

/// <summary>Étiquettes et lectures : police pixel 5×7 ou monospace lissée, même hauteur de capitales.</summary>
internal sealed class LabelPainter : IDisposable
{
    private readonly SKTypeface _mono;
    private readonly SKPaint _paint = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _smoothPaint = new() { IsAntialias = true };
    private SKFont? _font;

    public LabelPainter(SKTypeface mono) => _mono = mono;

    public bool Pixel { get; private set; } = true;

    /// <summary>Taille d'un pixel de la police matricielle, en pixels écran.</summary>
    public int P { get; private set; } = 1;

    /// <summary>Hauteur des capitales (= hauteur d'une ligne de texte).</summary>
    public float CapHeight { get; private set; } = 7;

    /// <summary>Change quand le rendu des textes change (sert de clé aux caches).</summary>
    public int Key { get; private set; }

    public void Configure(bool pixel, float scale)
    {
        int p = Math.Max(1, (int)MathF.Round(scale * 0.85f));
        float cap = pixel ? PixelFont.GlyphHeight * p : MathF.Round(7 * scale);
        if (pixel == Pixel && p == P && cap == CapHeight && _font is not null) return;
        Pixel = pixel;
        P = p;
        CapHeight = cap;
        _font?.Dispose();
        _font = new SKFont(_mono, cap / 0.72f) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.Slight };
        _bigFont?.Dispose();
        _bigFont = null;
        Key = HashCode.Combine(pixel, p, cap);
    }

    private SKFont? _bigFont;

    /// <summary>Police lissée agrandie (×2), créée à la demande.</summary>
    private SKFont FontFor(int factor) => factor <= 1
        ? _font!
        : _bigFont ??= new SKFont(_mono, CapHeight * 2 / 0.72f) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.Slight };

    /// <summary>Hauteur des capitales pour un texte agrandi (facteur 1 ou 2).</summary>
    public float CapHeightFor(int factor) => CapHeight * Math.Clamp(factor, 1, 2);

    public float Measure(string text, int factor = 1)
    {
        factor = Math.Clamp(factor, 1, 2);
        return Pixel ? PixelFont.Measure(text, P * factor) : FontFor(factor).Measure(Smooth(text));
    }

    /// <summary>Dessine le texte avec son coin supérieur gauche en (x, top).</summary>
    public void Draw(SKCanvas canvas, string text, float x, float top, SKColor color, int factor = 1)
    {
        factor = Math.Clamp(factor, 1, 2);
        if (Pixel)
        {
            _paint.Color = color;
            PixelFont.Draw(canvas, text, x, top, P * factor, _paint);
        }
        else
        {
            _smoothPaint.Color = color;
            canvas.DrawText(Smooth(text), x, top + CapHeightFor(factor), FontFor(factor), _smoothPaint);
        }
    }

    /// <summary>Texte pour la police lissée : majuscules, et « b » à la place du symbole bémol.</summary>
    private static string Smooth(string text) => text.ToUpperInvariant().Replace("♭", "b").Replace("◆", ">").Replace("ᵐ", "m");

    public void Dispose()
    {
        _font?.Dispose();
        _bigFont?.Dispose();
        _paint.Dispose();
        _smoothPaint.Dispose();
    }
}

internal static class SkiaText
{
    [ThreadStatic] private static SKPaint? _measurePaint;

    /// <summary>Largeur d'un texte (SKFont 2.88 ne mesure que des glyphes).</summary>
    public static float Measure(this SKFont font, string text)
    {
        var paint = _measurePaint ??= new SKPaint { SubpixelText = true };
        paint.Typeface = font.Typeface;
        paint.TextSize = font.Size;
        return paint.MeasureText(text);
    }
}

/// <summary>Ce dont un module a besoin pour se dessiner : thème, échelle DPI, textes, options de style.</summary>
internal sealed class RenderContext : IDisposable
{
    public RenderContext()
    {
        Mono = PickTypeface("Cascadia Mono", "Consolas", "Courier New");
        Labels = new LabelPainter(Mono);
    }

    public Theme Theme { get; set; } = null!;

    /// <summary>Facteur DPI de l'écran de la barre des tâches (1 = 100 %).</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Largeur (px) du fondu des bords : les textes se placent en dehors.</summary>
    public float EdgeInset { get; set; }

    /// <summary>Graduations, étiquettes et repères techniques.</summary>
    public bool ShowScales { get; set; } = true;

    /// <summary>Effet de lignes de balayage (une ligne sur deux atténuée).</summary>
    public bool Scanlines { get; set; } = true;

    public SKTypeface Mono { get; }
    public LabelPainter Labels { get; }

    /// <summary>Taille d'un « pixel » de dessin technique (traits fins, segments).</summary>
    public int P => Labels.P;

    private SKPaint? _scanlinePaint;
    private SKShader? _scanlineShader;
    private int _scanlineP;

    /// <summary>
    /// Atténue une ligne de pixels sur deux dans <paramref name="rect"/> (effet écran à balayage),
    /// à appliquer juste après avoir dessiné les données d'un module.
    /// </summary>
    public void ApplyScanlines(SKCanvas canvas, SKRect rect)
    {
        if (!Scanlines) return;
        if (_scanlinePaint is null || _scanlineP != P)
        {
            _scanlineP = P;
            _scanlineShader?.Dispose();
            _scanlinePaint?.Dispose();
            using var bitmap = new SKBitmap(new SKImageInfo(1, 2 * P, SKColorType.Bgra8888, SKAlphaType.Premul));
            for (int y = 0; y < 2 * P; y++)
                bitmap.SetPixel(0, y, y < P ? SKColors.Black : new SKColor(0, 0, 0, 0x7A));
            _scanlineShader = SKShader.CreateBitmap(bitmap, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
            _scanlinePaint = new SKPaint { Shader = _scanlineShader, BlendMode = SKBlendMode.DstIn };
        }
        canvas.DrawRect(rect, _scanlinePaint);
    }

    private static SKTypeface PickTypeface(params string[] families)
    {
        foreach (var family in families)
        {
            var tf = SKTypeface.FromFamilyName(family);
            if (tf is not null && string.Equals(tf.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                return tf;
            tf?.Dispose();
        }
        return SKTypeface.Default;
    }

    public void Dispose()
    {
        _scanlinePaint?.Dispose();
        _scanlineShader?.Dispose();
        Labels.Dispose();
        Mono.Dispose();
    }
}
