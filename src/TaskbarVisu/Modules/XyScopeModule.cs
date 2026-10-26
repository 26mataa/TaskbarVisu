using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Oscilloscope XY façon écran cathodique : chaque échantillon allume un point du « phosphore », qui s'éteint
/// lentement (rémanence). Goniomètre (son mono = trait vertical, stéréo large = nuage) ou Lissajous gauche / droite.
/// Par moments, le tracé se change en dessin puis redevient le son (<see cref="ScopeDrawer"/>).
/// </summary>
internal sealed class XyScopeModule : VisualizerModule
{
    private float[] _phosphor = Array.Empty<float>();
    private SKBitmap? _bitmap;
    private uint[] _lut = Array.Empty<uint>();
    private (int Version, int Phase) _lutKey = (-1, -1);
    private float _gain = 1;
    private bool _lit;
    private readonly SKPaint _grid = new() { IsAntialias = false };
    private readonly LayerCache _graticule = new();
    private readonly ScopeDrawer _drawer = new();
    // Par échantillon : écart dans le nuage, position à l'écran, éclat et lueur (que les dessins modifient).
    private float[] _sx = Array.Empty<float>(), _sy = Array.Empty<float>(), _fx = Array.Empty<float>(), _fy = Array.Empty<float>();
    private float[] _weight = Array.Empty<float>(), _glow = Array.Empty<float>();

    public XyScopeModule() : base("xyscope") { }

    public override bool IsIdle => !_lit;

    protected override void OnResize()
    {
        _phosphor = new float[Width * Height];
        _bitmap?.Dispose();
        _bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _bitmap.Erase(SKColors.Transparent);
    }

    /// <summary>Zone du tracé : carrée au centre, ou étirée sur toute la largeur (option).</summary>
    private (int X0, int W) PlotArea()
    {
        if (Flag("stretch")) return (0, Width);
        int w = Math.Min(Width, Height);
        return ((Width - w) / 2, w);
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        if (_bitmap is null || _lut.Length == 0) return;

        // Rémanence : chaque point s'éteint exponentiellement (plus lentement pendant une explosion).
        int drawings = OptI("drawings");
        double persistence = Math.Max(0.02, Opt("persistence")) * (1 + (drawings > 0 ? _drawer.Afterglow : 0));
        float decay = (float)Math.Exp(-dt / persistence);
        bool lit = false;
        for (int i = 0; i < _phosphor.Length; i++)
        {
            float v = _phosphor[i] * decay;
            _phosphor[i] = v < 0.004f ? 0 : v;
            lit |= v > 0;
        }

        // Gain automatique : la crête récente occupe ~90 % du cadre.
        float peak = Math.Max(audio.PeakL, audio.PeakR);
        _gain = peak > 1e-4f ? Math.Min(_gain * 1.02f, 0.9f / peak) : _gain;
        if (peak * _gain > 1) _gain = 0.95f / peak;
        _gain = Math.Clamp(_gain, 0.5f, 400f);

        // Place de chaque échantillon dans le nuage du son.
        var (x0, w) = PlotArea();
        bool gonio = OptI("mode") == 0;
        float hx = (w - 1) / 2f, hy = (Height - 1) / 2f;
        int n = audio.Count;
        if (_fx.Length < n)
        {
            _sx = new float[n];
            _sy = new float[n];
            _fx = new float[n];
            _fy = new float[n];
            _weight = new float[n];
            _glow = new float[n];
        }
        for (int i = 0; i < n; i++)
        {
            float l = audio.Left[i] * _gain, r = audio.Right[i] * _gain;
            var (sx, sy) = gonio ? ((r - l) * 0.7071f, (l + r) * 0.7071f) : (l, r);
            _sx[i] = sx;
            _sy[i] = sy;
            _fx[i] = x0 + hx + sx * hx;
            _fy[i] = hy - sy * hy;
            _weight[i] = 1;
            _glow[i] = 0;
        }

        // Dessins : les mêmes échantillons quittent le nuage pour tracer le dessin, ses étincelles et ses ondes de choc.
        if (drawings > 0)
        {
            _drawer.Apply(audio, dt, drawings == 2, Flag("fireworks"), OptF("wobble") / 100f,
                _sx, _sy, _fx, _fy, _weight, _glow, x0, w, Height);
            lit |= _drawer.Active;
        }

        float hit = Math.Clamp(1800f / Math.Max(1, n), 0.08f, 1f) * 0.35f;
        for (int i = 0; i < n; i++)
        {
            if (_weight[i] <= 0) continue;
            int px = (int)MathF.Round(_fx[i]), py = (int)MathF.Round(_fy[i]);
            if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) continue;
            int at = py * Width + px;
            float dose = hit * _weight[i];
            _phosphor[at] = Math.Min(1.5f, _phosphor[at] + dose);
            if (_glow[i] > 0)
            {
                // Lueur du phosphore autour du trait.
                float halo = dose * _glow[i];
                if (px > 0) _phosphor[at - 1] = Math.Min(1.5f, _phosphor[at - 1] + halo);
                if (px < Width - 1) _phosphor[at + 1] = Math.Min(1.5f, _phosphor[at + 1] + halo);
                if (py > 0) _phosphor[at - Width] = Math.Min(1.5f, _phosphor[at - Width] + halo);
                if (py < Height - 1) _phosphor[at + Width] = Math.Min(1.5f, _phosphor[at + Width] + halo);
            }
            lit = true;
        }
        _lit = lit;

        unsafe
        {
            uint* pixels = (uint*)_bitmap.GetPixels();
            for (int i = 0; i < _phosphor.Length; i++)
                pixels[i] = _phosphor[i] <= 0 ? 0 : _lut[(int)Math.Min(255, _phosphor[i] * 255)];
        }
        _bitmap.NotifyPixelsChanged();
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        var key = (t.Version, t.Animated ? (int)(t.Phase * 128) : 0);
        if (key != _lutKey)
        {
            _lutKey = key;
            _lut = t.BuildLut();
        }
        if (_bitmap is null) return;

        // Réticule : axes, et repères G / D du goniomètre.
        if (ctx.ShowScales)
        {
            var (x0, w) = PlotArea();
            bool gonio = OptI("mode") == 0;
            var gk = (t.Version, ctx.Labels.Key, x0, w, gonio, rect.Width, rect.Height);
            if (_graticule.Begin((int)rect.Width, (int)rect.Height, gk) is { } c)
            {
                int p = ctx.P;
                _grid.Color = t.GridFaint;
                float cx = x0 + w / 2f, cy = Height / 2f;
                for (float x = x0; x < x0 + w; x += 2 * p) c.DrawRect(x, MathF.Round(cy), p, p, _grid);
                for (float y = 0; y < Height; y += 2 * p) c.DrawRect(MathF.Round(cx), y, p, p, _grid);
                if (gonio && w >= 6 * ctx.Labels.CapHeight)
                {
                    ctx.Labels.Draw(c, "L", x0 + p, p, t.InkDim);
                    ctx.Labels.Draw(c, "R", x0 + w - ctx.Labels.Measure("R") - p, p, t.InkDim);
                }
            }
            _graticule.Draw(canvas, rect.Left, rect.Top);
        }
        canvas.DrawBitmap(_bitmap, rect.Left, rect.Top);
        ctx.ApplyScanlines(canvas, rect);
    }

    public override void Dispose()
    {
        _bitmap?.Dispose();
        _grid.Dispose();
        _graticule.Dispose();
    }
}
