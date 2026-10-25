using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Spectrogramme défilant. L'historique est une image circulaire : à chaque nouvelle colonne
/// on n'écrit qu'une colonne de pixels (pas de décalage de toute l'image).
/// </summary>
internal sealed class SpectrogramModule : VisualizerModule
{
    private static readonly float[] Marks = { 100, 1000, 10000 };

    private readonly SpectrumMapper _mapper = new();
    private readonly LayerCache _scale = new();
    private readonly SKPaint _tick = new() { IsAntialias = false };
    private SKBitmap? _bitmap;
    private uint[] _lut = Array.Empty<uint>();
    private (int Theme, int Palette) _lutKey = (-1, -1);
    private float[] _rows = Array.Empty<float>();
    private uint[] _column = Array.Empty<uint>();
    private int _writeColumn;
    private double _columnCarry;
    private int _silentColumns = int.MaxValue / 2;

    public SpectrogramModule() : base("spectrogram") { }

    public override bool IsIdle => _silentColumns >= Width;

    protected override void OnResize()
    {
        _bitmap?.Dispose();
        _bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _bitmap.Erase(SKColors.Transparent);
        _rows = new float[Height];
        _column = new uint[Height];
        _writeColumn = 0;
        _silentColumns = Width;
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        if (_bitmap is null || _lut.Length == 0) return;

        _columnCarry += dt * Opt("speed");
        int newColumns = (int)_columnCarry;
        if (newColumns <= 0) return;
        _columnCarry -= newColumns;
        newColumns = Math.Min(newColumns, Width);

        _mapper.Ensure(Height, OptF("minFreq"), OptF("maxFreq"), audio.SampleRate, audio.FftSize, OptF("tilt"));
        _mapper.MapDb(audio.SpectrumDb, _rows);

        float floor = OptF("floor"), ceiling = Math.Max(floor + 6, OptF("ceiling"));
        float k = 255f / (ceiling - floor);
        bool silent = true;
        for (int y = 0; y < Height; y++)
        {
            // Ligne 0 = haut de l'image = fréquences les plus aiguës.
            int idx = (int)Math.Clamp((_rows[Height - 1 - y] - floor) * k, 0, 255);
            _column[y] = _lut[idx];
            if (idx > 4) silent = false;
        }
        _silentColumns = silent ? _silentColumns + newColumns : 0;

        unsafe
        {
            uint* pixels = (uint*)_bitmap.GetPixels();
            for (int c = 0; c < newColumns; c++)
            {
                int x = _writeColumn;
                for (int y = 0; y < Height; y++) pixels[y * Width + x] = _column[y];
                _writeColumn = (_writeColumn + 1) % Width;
            }
        }
        _bitmap.NotifyPixelsChanged();
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        // Palette animée : la table est recalculée au fil de la rotation (256 couleurs, coût négligeable).
        var key = (ctx.Theme.Version, ctx.Theme.Animated ? (int)(ctx.Theme.Phase * 128) : 0);
        if (key != _lutKey)
        {
            _lutKey = key;
            _lut = ctx.Theme.BuildLut();
        }
        if (_bitmap is null) return;

        canvas.Save();
        if (OptI("direction") == 1)
        {
            // Défilement vers la droite : on dessine en miroir horizontal.
            canvas.Translate(rect.Left + rect.Right, 0);
            canvas.Scale(-1, 1);
        }

        // Colonnes les plus anciennes = à partir de _writeColumn ; les plus récentes à droite.
        int older = Width - _writeColumn;
        if (older > 0)
            canvas.DrawBitmap(_bitmap, new SKRect(_writeColumn, 0, Width, Height),
                new SKRect(rect.Left, rect.Top, rect.Left + older, rect.Top + Height));
        if (_writeColumn > 0)
            canvas.DrawBitmap(_bitmap, new SKRect(0, 0, _writeColumn, Height),
                new SKRect(rect.Left + older, rect.Top, rect.Left + Width, rect.Top + Height));
        canvas.Restore();

        ctx.ApplyScanlines(canvas, rect);
        if (ctx.ShowScales) DrawScale(canvas, rect, ctx);
    }

    /// <summary>Repères de fréquence (100 Hz, 1 kHz, 10 kHz) en petites encoches sur le bord intérieur.</summary>
    private void DrawScale(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        float minF = OptF("minFreq"), maxF = Math.Max(minF * 2, OptF("maxFreq"));
        bool atRight = OptI("direction") == 0;
        int p = ctx.P;
        float inset = MathF.Ceiling(ctx.EdgeInset);
        var key = (ctx.Theme.Version, minF, maxF, atRight, p, inset, rect.Width, rect.Height);
        _scale.Draw(canvas, rect.Left, rect.Top, (int)rect.Width, (int)rect.Height, key, c =>
        {
            _tick.Color = ctx.Theme.InkDim;
            foreach (var f in Marks)
            {
                if (f <= minF || f >= maxF) continue;
                float y = MathF.Round(rect.Height - MathF.Log(f / minF) / MathF.Log(maxF / minF) * rect.Height);
                // Encoches du côté des colonnes récentes, hors de la zone de fondu.
                float x = atRight ? rect.Width - inset - 3 * p : inset;
                c.DrawRect(x, y, 3 * p, p, _tick);
            }
        });
    }

    public override void Dispose()
    {
        _bitmap?.Dispose();
        _scale.Dispose();
        _tick.Dispose();
    }
}
