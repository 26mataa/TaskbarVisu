using SkiaSharp;

namespace TaskbarVisu.Rendering;

/// <summary>
/// Calque mis en cache : la partie statique d'un module (graduations, grille, texte…)
/// n'est redessinée que lorsque sa clé change, puis simplement recopiée à chaque image.
/// </summary>
internal sealed class LayerCache : IDisposable
{
    private SKSurface? _surface;
    private SKImage? _image;
    private object? _key;
    private int _width, _height;

    /// <param name="paint">Dessine le calque en coordonnées locales (0, 0) - (width, height).</param>
    public void Draw(SKCanvas canvas, float x, float y, int width, int height, object key, Action<SKCanvas> paint)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_image is null || width != _width || height != _height || !Equals(key, _key))
        {
            _image?.Dispose();
            if (_surface is null || width != _width || height != _height)
            {
                _surface?.Dispose();
                _surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                _width = width;
                _height = height;
            }
            // La surface est réutilisée d'une reconstruction à l'autre : on repart toujours d'une
            // transformation vierge, sinon les décalages (Translate) des dessins précédents s'accumulent.
            var c = _surface.Canvas;
            c.ResetMatrix();
            c.Clear(SKColors.Transparent);
            int saved = c.Save();
            paint(c);
            c.RestoreToCount(saved);
            c.Flush();
            _image = _surface.Snapshot();
            _key = key;
        }
        canvas.DrawImage(_image, x, y);
    }

    public void Dispose()
    {
        _image?.Dispose();
        _surface?.Dispose();
        _image = null;
        _surface = null;
    }
}

/// <summary>
/// Lecture dynamique (valeur, note…) rendue une seule fois tant qu'elle ne change pas,
/// avec une case de fond optionnelle façon afficheur.
/// </summary>
internal sealed class LabelCache : IDisposable
{
    private readonly LayerCache _layer = new();
    private readonly SKPaint _box = new() { IsAntialias = false };

    public static SKSize Size(RenderContext ctx, string text, float padX = 0, float padY = 0, int factor = 1) =>
        new(MathF.Ceiling(ctx.Labels.Measure(text, factor) + padX * 2), MathF.Ceiling(ctx.Labels.CapHeightFor(factor) + padY * 2));

    /// <summary>Dessine la case avec son coin supérieur gauche en (x, y). Renvoie sa taille.</summary>
    public SKSize Draw(SKCanvas canvas, RenderContext ctx, string text, float x, float y, SKColor fg,
        SKColor? background = null, float padX = 0, float padY = 0, int factor = 1)
    {
        var size = Size(ctx, text, padX, padY, factor);
        var key = (text, fg, background, ctx.Labels.Key, padX, padY, factor);
        _layer.Draw(canvas, MathF.Round(x), MathF.Round(y), (int)size.Width, (int)size.Height, key, c =>
        {
            if (background is { } bg)
            {
                _box.Color = bg;
                c.DrawRect(0, 0, size.Width, size.Height, _box);
            }
            ctx.Labels.Draw(c, text, padX, padY, fg, factor);
        });
        return size;
    }

    public void Dispose()
    {
        _layer.Dispose();
        _box.Dispose();
    }
}
