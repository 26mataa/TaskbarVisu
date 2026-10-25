using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Forme d'onde défilante (un couple min / max par colonne de pixels), colorée selon le contenu
/// fréquentiel de chaque colonne, ou oscilloscope déclenché sur passage à zéro.
/// </summary>
internal sealed class WaveformModule : VisualizerModule
{
    private const int ScopeSize = 8192;

    private float[] _max = Array.Empty<float>(), _min = Array.Empty<float>(), _tone = Array.Empty<float>();
    private int _write;
    private double _sampleCarry;
    private float _curMax, _curMin;
    private int _silentColumns = int.MaxValue / 2;

    // Filtres à un pôle pour estimer graves / médiums / aigus de chaque colonne.
    private int _filterRate;
    private float _k1, _k2, _lp1, _lp2;
    private double _eLow, _eMid, _eHigh;

    // Oscilloscope : derniers échantillons mono.
    private readonly float[] _scope = new float[ScopeSize];
    private int _scopePos;
    private int _scopeSilent = ScopeSize;
    private SKPoint[] _scopePoints = Array.Empty<SKPoint>();

    private readonly SKPaint _bar = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _hair = new() { IsAntialias = false, Style = SKPaintStyle.Fill };

    public WaveformModule() : base("waveform") { }

    private bool Oscilloscope => OptI("mode") == 2;

    public override bool IsIdle => Oscilloscope ? _scopeSilent >= ScopeSize : _silentColumns >= Width;

    protected override void OnResize()
    {
        _max = new float[Width];
        _min = new float[Width];
        _tone = new float[Width];
        _write = 0;
        _silentColumns = Width;
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        if (_max.Length == 0) return;

        int sr = audio.SampleRate;
        if (sr != _filterRate)
        {
            _filterRate = sr;
            _k1 = 1f - MathF.Exp(-2f * MathF.PI * 250f / sr);
            _k2 = 1f - MathF.Exp(-2f * MathF.PI * 2500f / sr);
        }

        double samplesPerColumn = sr / Math.Max(1, Opt("speed"));
        float gain = MathF.Pow(10, OptF("gain") / 20f);
        var left = audio.Left;
        var right = audio.Right;

        for (int i = 0; i < audio.Count; i++)
        {
            float l = left[i], r = right[i];
            float m = (l + r) * 0.5f;
            if (m > _curMax) _curMax = m;
            if (m < _curMin) _curMin = m;

            _lp1 += _k1 * (m - _lp1);
            _lp2 += _k2 * (m - _lp2);
            float lo = _lp1, mi = _lp2 - _lp1, hi = m - _lp2;
            _eLow += lo * lo;
            _eMid += mi * mi;
            _eHigh += hi * hi;

            _scope[_scopePos] = m * gain;
            _scopePos = (_scopePos + 1) & (ScopeSize - 1);
            _scopeSilent = Math.Abs(m) > 1e-4f ? 0 : _scopeSilent + 1;

            _sampleCarry += 1;
            if (_sampleCarry >= samplesPerColumn)
            {
                _sampleCarry -= samplesPerColumn;
                PushColumn(gain);
            }
        }
    }

    private void PushColumn(float gain)
    {
        _max[_write] = _curMax * gain;
        _min[_write] = _curMin * gain;
        // Teinte : 0 = surtout des graves, 1 = surtout des aigus.
        double lo = Math.Sqrt(_eLow), mi = Math.Sqrt(_eMid), hi = Math.Sqrt(_eHigh);
        double sum = lo + mi + hi;
        _tone[_write] = sum > 1e-9 ? (float)Math.Pow((mi * 0.5 + hi) / sum, 0.8) : 0;
        _silentColumns = Math.Max(Math.Abs(_curMax), Math.Abs(_curMin)) * gain < 1e-4f ? _silentColumns + 1 : 0;
        _write = (_write + 1) % _max.Length;
        _curMax = _curMin = 0;
        _eLow = _eMid = _eHigh = 0;
    }

    private static float Amp(float v, bool log)
    {
        v = Math.Abs(v);
        if (!log) return Math.Min(1f, v);
        return Math.Clamp((ToDb(v) + 48f) / 48f, 0f, 1f);
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        if (_max.Length == 0) return;
        var theme = ctx.Theme;
        int mode = OptI("mode");
        bool log = OptI("scale") == 1;
        const bool spectral = true; // les couleurs viennent du thème du module (palette ou couleur unie)
        float h = rect.Height;
        float midY = MathF.Round(rect.MidY);
        int p = ctx.P;

        if (ctx.ShowScales)
        {
            // Ligne de référence (zéro) en filet.
            _hair.Color = theme.Grid;
            float y = mode == 1 ? rect.Bottom - p : midY;
            canvas.DrawRect(rect.Left, y, rect.Width, p, _hair);
        }

        if (mode == 2)
        {
            DrawScope(canvas, rect, ctx, log);
            return;
        }

        canvas.Save();
        if (OptI("direction") == 1)
        {
            canvas.Translate(rect.Left + rect.Right, 0);
            canvas.Scale(-1, 1);
        }

        int w = _max.Length;
        _bar.Color = theme.Ink.WithAlpha(0xE6);
        for (int i = 0; i < w; i++)
        {
            int idx = (_write + i) % w;
            float top, bottom;
            if (mode == 1)
            {
                float a = Amp(Math.Max(Math.Abs(_max[idx]), Math.Abs(_min[idx])), log);
                top = rect.Bottom - a * h;
                bottom = rect.Bottom;
            }
            else
            {
                float a = Amp(Math.Max(Math.Abs(_max[idx]), Math.Abs(_min[idx])), log);
                top = midY - a * h * 0.5f;
                bottom = midY + a * h * 0.5f;
            }
            if (bottom - top < 0.5f) continue;
            if (spectral) _bar.Color = theme.Spectral(_tone[idx]).WithAlpha(0xEE);
            canvas.DrawRect(rect.Left + i, top, 1, bottom - top, _bar);
        }
        canvas.Restore();
        ctx.ApplyScanlines(canvas, rect);
    }

    /// <summary>Oscilloscope : fenêtre de ~4 colonnes par pixel, calée sur un passage à zéro montant.</summary>
    private void DrawScope(SKCanvas canvas, SKRect rect, RenderContext ctx, bool log)
    {
        int width = (int)rect.Width;
        int span = Math.Clamp(width * 6, 512, 4096);
        int searchFrom = (_scopePos - span - 2048 + ScopeSize) & (ScopeSize - 1);
        int start = (_scopePos - span + ScopeSize) & (ScopeSize - 1);
        for (int k = 0; k < 2048; k++)
        {
            int i = (searchFrom + k) & (ScopeSize - 1);
            int prev = (i - 1 + ScopeSize) & (ScopeSize - 1);
            if (_scope[prev] < 0 && _scope[i] >= 0) start = i;
        }

        if (_scopePoints.Length != width) _scopePoints = new SKPoint[width];
        float half = rect.Height * 0.5f - ctx.P;
        float midY = rect.MidY;
        for (int x = 0; x < width; x++)
        {
            float v = _scope[(start + x * span / width) & (ScopeSize - 1)];
            float a = log ? MathF.Sign(v) * Amp(v, true) : Math.Clamp(v, -1, 1);
            _scopePoints[x] = new SKPoint(rect.Left + x + 0.5f, midY - a * half);
        }

        _line.StrokeWidth = Math.Max(1f, 1.2f * ctx.Scale);
        _line.Color = ctx.Theme.Spectral(0.85f);
        canvas.DrawPoints(SKPointMode.Polygon, _scopePoints, _line);
    }

    public override void Dispose()
    {
        _bar.Dispose();
        _line.Dispose();
        _hair.Dispose();
    }
}
