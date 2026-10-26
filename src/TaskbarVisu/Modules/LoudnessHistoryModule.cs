using System.Globalization;
using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Historique de sonie : la mesure LUFS défile sur une fenêtre de temps (15 s à 5 min),
/// en barres LED ou en courbe remplie, avec la ligne de cible et la valeur actuelle.
/// </summary>
internal sealed class LoudnessHistoryModule : VisualizerModule
{
    private static readonly double[] Windows = { 15, 30, 60, 300 };

    private float[] _history = Array.Empty<float>();
    private int _write;
    private double _carry;
    private float _current = -200, _live = -200;
    private string _text = string.Empty;
    private double _textTimer;

    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _area = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPath _path = new();
    private readonly LayerCache _static = new();
    private readonly LabelCache _value = new();
    private SKShader? _shader;
    private object? _shaderKey;

    public LoudnessHistoryModule() : base("loudness") { }

    private bool Bars => OptI("style") == 0;
    private float Floor => OptF("range");

    public override bool IsIdle
    {
        get
        {
            if (_live > -70 || _current > Floor) return false;
            foreach (var v in _history) if (v > Floor) return false;
            return true;
        }
    }

    protected override void OnConfigure() => Allocate();
    protected override void OnResize() => Allocate();

    private void Allocate()
    {
        if (Width <= 1) return;
        int count = Bars ? Math.Max(8, (Width + Px) / (3 * Px)) : Math.Max(8, Width);
        if (count == _history.Length) return;
        _history = Enumerable.Repeat(-200f, count).ToArray();
        _write = 0;
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        if (_history.Length == 0) return;
        _live = (float)(OptI("lufsMode") == 0 ? audio.Loudness.ShortTerm : audio.Loudness.Momentary);

        // Chaque colonne garde le maximum de la période qu'elle couvre.
        double period = Windows[Math.Clamp(OptI("window"), 0, Windows.Length - 1)] / _history.Length;
        if (_live > _current) _current = _live;
        _carry += dt;
        while (_carry >= period)
        {
            _carry -= period;
            _history[_write] = _current;
            _write = (_write + 1) % _history.Length;
            _current = _live;
        }

        _textTimer -= dt;
        if (_textTimer <= 0)
        {
            _textTimer = 0.2;
            _text = _live < -70 ? string.Empty : _live.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        if (_history.Length == 0) return;
        var t = ctx.Theme;
        int p = ctx.P;
        float floor = Floor;
        float top = rect.Top + p, bottom = rect.Bottom;
        float span = bottom - top;
        float Norm(float lufs) => Math.Clamp((lufs - floor) / -floor, 0, 1);
        float Y(float lufs) => bottom - Norm(lufs) * span;

        // Ligne de cible pointillée + sa valeur.
        float target = OptF("target");
        var key = (t.Version, ctx.Labels.Key, ctx.ShowScales, target, floor, rect.Width, rect.Height);
        if (_static.Begin((int)rect.Width, (int)rect.Height, key) is { } c)
        {
            c.Translate(-rect.Left, -rect.Top);
            float ty = MathF.Round(Y(target));
            _fill.Color = t.InkDim;
            for (float x = rect.Left; x < rect.Right; x += 3 * p) c.DrawRect(x, ty, p, p, _fill);
            if (ctx.ShowScales)
            {
                string label = ((int)target).ToString(CultureInfo.InvariantCulture);
                float ly = ty - ctx.Labels.CapHeight - p;
                if (ly < rect.Top) ly = ty + 2 * p;
                ctx.Labels.Draw(c, label, MathF.Round(rect.Left + Math.Max(p, ctx.EdgeInset)), MathF.Round(ly), t.InkDim);
            }
        }
        _static.Draw(canvas, rect.Left, rect.Top);

        int n = _history.Length;
        if (Bars)
        {
            float step = 3 * p, w = 2 * p;
            for (int i = 0; i < n; i++)
            {
                // La dernière colonne montre la période en cours.
                float v = i == n - 1 ? Math.Max(_current, _history[(_write + i) % n]) : _history[(_write + i) % n];
                if (v <= floor) continue;
                float x = rect.Left + i * step;
                float yTop = Y(v);
                // Barres découpées en points LED, colorées selon le niveau.
                for (float y = bottom - w; y >= yTop - 0.5f; y -= step)
                {
                    float level = (bottom - y) / span;
                    _fill.Color = t.Spectral(level);
                    canvas.DrawRect(x, MathF.Round(y), w, w, _fill);
                }
            }
        }
        else
        {
            _path.Rewind();
            _path.MoveTo(rect.Left, bottom);
            for (int i = 0; i < n; i++)
            {
                float v = _history[(_write + i) % n];
                _path.LineTo(rect.Left + i + 0.5f, v <= floor ? bottom : Y(v));
            }
            _path.LineTo(rect.Left + n, bottom);
            _path.Close();
            _area.Shader = AreaShader(top, bottom, t);
            canvas.DrawPath(_path, _area);

            _path.Rewind();
            bool started = false;
            for (int i = 0; i < n; i++)
            {
                float v = _history[(_write + i) % n];
                if (v <= floor) { started = false; continue; }
                if (!started) _path.MoveTo(rect.Left + i + 0.5f, Y(v));
                else _path.LineTo(rect.Left + i + 0.5f, Y(v));
                started = true;
            }
            _line.StrokeWidth = Math.Max(1f, ctx.Scale);
            _line.Color = t.Ink;
            canvas.DrawPath(_path, _line);
        }
        ctx.ApplyScanlines(canvas, rect);

        if (_text.Length > 0)
        {
            var size = LabelCache.Size(ctx, _text, 2 * p, p);
            float x = rect.Right - Math.Max(p, ctx.EdgeInset) - size.Width;
            _value.Draw(canvas, ctx, _text, x, rect.Top + p, t.ReadoutText, t.ReadoutBack, 2 * p, p);
        }
    }

    /// <summary>Dégradé vertical de la palette : bas = calme, haut = fort.</summary>
    private SKShader AreaShader(float top, float bottom, Theme t)
    {
        var key = (top, bottom, t.Version, t.Animated ? (int)(t.Phase * 128) : 0);
        if (_shader is not null && Equals(key, _shaderKey)) return _shader;
        _shaderKey = key;
        _shader?.Dispose();
        var colors = new SKColor[6];
        for (int i = 0; i < colors.Length; i++) colors[i] = t.Spectral(1 - i / 5f).WithAlpha(0xB0);
        _shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, bottom), colors, SKShaderTileMode.Clamp);
        return _shader;
    }

    public override void Dispose()
    {
        _fill.Dispose();
        _area.Dispose();
        _line.Dispose();
        _path.Dispose();
        _static.Dispose();
        _value.Dispose();
        _shader?.Dispose();
    }
}
