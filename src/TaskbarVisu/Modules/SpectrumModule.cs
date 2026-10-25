using System.Globalization;
using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Analyseur de spectre logarithmique : barres, points LED, courbe remplie ou ligne,
/// couleurs selon la fréquence, crêtes maintenues et lecture de la note dominante.
/// </summary>
internal sealed class SpectrumModule : VisualizerModule
{
    private static readonly (float F, string Label)[] GridMajor = { (100, "100"), (1000, "1K"), (10000, "10K") };
    private static readonly float[] GridMinor = { 50, 200, 500, 2000, 5000 };

    private readonly SpectrumMapper _mapper = new();
    private float[] _raw = Array.Empty<float>(), _disp = Array.Empty<float>(), _peak = Array.Empty<float>();
    private int _columns;
    private string _noteText = string.Empty;
    private double _noteTimer;

    private readonly SKPath _path = new();
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _bar = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _grid = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly LayerCache _gridLayer = new();
    private readonly LabelCache _note = new();
    private SKShader? _shader;
    private object? _shaderKey;

    public SpectrumModule() : base("spectrum") { }

    private bool Discrete => OptI("style") <= 1;

    public override bool IsIdle
    {
        get
        {
            float floor = OptF("floor");
            for (int i = 0; i < _columns; i++)
                if (_disp[i] > floor || _peak[i] > floor) return false;
            return true;
        }
    }

    protected override void OnConfigure() => Allocate();
    protected override void OnResize() => Allocate();

    private void Allocate()
    {
        if (Width <= 1) return;
        // Barres : 2 px + 1 px d'espace ; courbe : un point tous les ~2,5 px.
        int count = Discrete ? Math.Max(8, (Width + Px) / (3 * Px)) : Math.Max(16, (int)(Width / (2.5f * Scale)));
        if (count == _columns) return;
        _columns = count;
        _raw = new float[count];
        _disp = Enumerable.Repeat(-200f, count).ToArray();
        _peak = Enumerable.Repeat(-200f, count).ToArray();
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        if (_columns == 0) return;
        _mapper.Ensure(_columns, OptF("minFreq"), OptF("maxFreq"), audio.SampleRate, audio.FftSize, OptF("tilt"));
        _mapper.MapDb(audio.SpectrumDb, _raw);

        float fall = OptF("release") * (float)dt;
        float peakFall = fall * 0.35f;
        for (int i = 0; i < _columns; i++)
        {
            float r = _raw[i];
            _disp[i] = r >= _disp[i] ? r : Math.Max(r, _disp[i] - fall);
            _peak[i] = r >= _peak[i] ? r : Math.Max(r, _peak[i] - peakFall);
        }

        _noteTimer -= dt;
        if (_noteTimer <= 0)
        {
            _noteTimer = 0.12;
            _noteText = BuildNoteText(audio);
        }
    }

    private string BuildNoteText(AudioAnalyzer audio)
    {
        if (!Flag("note") || audio.SilentSeconds > 0.2) return string.Empty;
        if (!Pitch.FindPeak(audio.SpectrumDb, audio.BinHz, 25, 5000, out float freq, out float level) || level < -72)
            return string.Empty;

        var (note, cents) = Pitch.ToNote(freq);
        var ci = CultureInfo.InvariantCulture;
        string c = cents.ToString("+0;-0;0", ci);
        return Width >= 200 * Scale
            ? $"{freq.ToString("0.0", ci)}HZ {note} {c}"
            : $"{note} {c}";
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        if (_columns == 0) return;
        var t = ctx.Theme;
        int p = ctx.P;
        float floor = OptF("floor"), ceiling = Math.Max(floor + 6, OptF("ceiling"));
        float minF = OptF("minFreq"), maxF = Math.Max(minF * 2, OptF("maxFreq"));
        float h = rect.Height, bottom = rect.Bottom;
        int style = OptI("style");
        const bool spectral = true; // les couleurs viennent du thème du module (palette ou couleur unie)

        float Y(float db) => bottom - Math.Clamp((db - floor) / (ceiling - floor), 0, 1) * h;

        if (Flag("grid")) DrawGrid(canvas, rect, ctx, minF, maxF);

        if (style <= 1)
        {
            float step = 3 * p, barW = 2 * p;
            float x0 = MathF.Round(rect.Left + (rect.Width - (_columns * step - p)) / 2);
            _bar.Color = t.Ink.WithAlpha(0xE6);
            for (int i = 0; i < _columns; i++)
            {
                float top = MathF.Round(Y(_disp[i]));
                float x = x0 + i * step;
                if (spectral) _bar.Color = t.Spectral(i / (float)(_columns - 1)).WithAlpha(0xF0);
                if (bottom - top >= 1)
                {
                    if (style == 0)
                    {
                        canvas.DrawRect(x, top, barW, bottom - top, _bar);
                    }
                    else
                    {
                        // Points LED : la barre est découpée en pastilles de 2 px.
                        for (float y = bottom - barW; y >= top - 0.5f; y -= step)
                            canvas.DrawRect(x, y, barW, barW, _bar);
                    }
                }
            }
            ctx.ApplyScanlines(canvas, rect);

            if (Flag("peaks"))
            {
                _bar.Color = t.Ink;
                for (int i = 0; i < _columns; i++)
                {
                    if (_peak[i] <= floor) continue;
                    canvas.DrawRect(x0 + i * step, MathF.Round(Y(_peak[i])) - p, barW, p, _bar);
                }
            }
        }
        else
        {
            float XCol(int i) => rect.Left + (i + 0.5f) / _columns * rect.Width;
            _path.Rewind();
            if (style == 2) _path.MoveTo(rect.Left, bottom);
            for (int i = 0; i < _columns; i++)
            {
                float x = XCol(i), y = Y(_disp[i]);
                if (style == 3 && i == 0) _path.MoveTo(x, y);
                else _path.LineTo(x, y);
            }
            if (style == 2)
            {
                _path.LineTo(rect.Right, bottom);
                _path.Close();
                _fill.Shader = FillShader(rect, t, spectral);
                canvas.DrawPath(_path, _fill);
                ctx.ApplyScanlines(canvas, rect);
                // Contour net par-dessus le remplissage.
                _path.Rewind();
                for (int i = 0; i < _columns; i++)
                {
                    if (i == 0) _path.MoveTo(XCol(i), Y(_disp[i]));
                    else _path.LineTo(XCol(i), Y(_disp[i]));
                }
            }
            _line.StrokeWidth = Math.Max(1f, 1.1f * ctx.Scale);
            _line.Color = t.Ink;
            canvas.DrawPath(_path, _line);

            if (Flag("peaks"))
            {
                _path.Rewind();
                for (int i = 0; i < _columns; i++)
                {
                    if (i == 0) _path.MoveTo(XCol(i), Y(_peak[i]));
                    else _path.LineTo(XCol(i), Y(_peak[i]));
                }
                _line.StrokeWidth = Math.Max(1f, ctx.Scale * 0.8f);
                _line.Color = t.Ink.WithAlpha(0x60);
                canvas.DrawPath(_path, _line);
            }
        }

        if (_noteText.Length > 0)
        {
            var size = LabelCache.Size(ctx, _noteText, 2 * p, p);
            float x = rect.Right - Math.Max(p, ctx.EdgeInset) - size.Width;
            _note.Draw(canvas, ctx, _noteText, Math.Max(rect.Left, x), rect.Top + p, t.Ink, t.OverlayBack, 2 * p, p);
        }
    }

    private void DrawGrid(SKCanvas canvas, SKRect rect, RenderContext ctx, float minF, float maxF)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        bool labels = ctx.ShowScales && rect.Height >= 22 * p;
        // La note s'affiche en haut à droite : on n'y met pas d'étiquette pour éviter les chevauchements.
        bool note = Flag("note");
        var key = (t.Version, ctx.Labels.Key, minF, maxF, labels, note, rect.Width, rect.Height);
        _gridLayer.Draw(canvas, rect.Left, rect.Top, (int)rect.Width, (int)rect.Height, key, c =>
        {
            float X(float f) => MathF.Round(MathF.Log(f / minF) / MathF.Log(maxF / minF) * rect.Width);
            _grid.Color = t.GridFaint;
            foreach (var f in GridMinor)
                if (f > minF && f < maxF) c.DrawRect(X(f), 0, p, rect.Height, _grid);
            foreach (var (f, label) in GridMajor)
            {
                if (f <= minF || f >= maxF) continue;
                float x = X(f);
                _grid.Color = t.Grid;
                c.DrawRect(x, 0, p, rect.Height, _grid);
                if (labels && !(note && x > rect.Width * 0.55f)) ctx.Labels.Draw(c, label, x + 2 * p, p, t.InkDim);
            }
        });
    }

    /// <summary>Dégradé horizontal grave → aigu de la palette (ou encre), plus transparent vers le bas.</summary>
    private SKShader FillShader(SKRect rect, Theme t, bool spectral)
    {
        var key = (rect.Left, rect.Width, t.Version, t.Animated ? (int)(t.Phase * 128) : 0);
        if (_shader is not null && Equals(key, _shaderKey)) return _shader;
        _shaderKey = key;
        _shader?.Dispose();
        const int stops = 8;
        var colors = new SKColor[stops];
        for (int i = 0; i < stops; i++)
            colors[i] = (spectral ? t.Spectral(i / (stops - 1f)) : t.Ink).WithAlpha(0xA8);
        _shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, 0), new SKPoint(rect.Right, 0), colors, SKShaderTileMode.Clamp);
        return _shader;
    }

    public override void Dispose()
    {
        _path.Dispose();
        _fill.Dispose();
        _bar.Dispose();
        _line.Dispose();
        _grid.Dispose();
        _gridLayer.Dispose();
        _note.Dispose();
        _shader?.Dispose();
    }
}
