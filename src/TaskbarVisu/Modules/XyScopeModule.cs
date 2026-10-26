using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Oscilloscope XY façon écran cathodique : chaque échantillon allume un point du « phosphore », qui s'éteint
/// lentement (rémanence). Goniomètre (son mono = trait vertical, stéréo large = nuage) ou Lissajous gauche / droite.
/// </summary>
internal sealed class XyScopeModule : VisualizerModule
{
    private float[] _phosphor = Array.Empty<float>();
    /// <summary>Couche des dessins, à rémanence courte.</summary>
    private float[] _ink = Array.Empty<float>();
    private SKBitmap? _bitmap;
    private uint[] _lut = Array.Empty<uint>();
    private (int Version, int Phase) _lutKey = (-1, -1);
    private float _gain = 1;
    private bool _lit;
    private readonly SKPaint _grid = new() { IsAntialias = false };
    private readonly LayerCache _graticule = new();
    private readonly ScopeDrawer _drawer = new();

    public XyScopeModule() : base("xyscope") { }

    public override bool IsIdle => !_lit;

    protected override void OnResize()
    {
        _phosphor = new float[Width * Height];
        _ink = new float[Width * Height];
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

        // Rémanence : chaque point s'éteint exponentiellement. Les dessins ont leur propre couche, qui s'éteint vite :
        // un dessin chasse le précédent en quelques images au lieu de se mélanger avec lui.
        double persistence = Math.Max(0.02, Opt("persistence"));
        float decay = (float)Math.Exp(-dt / persistence), inkDecay = (float)Math.Exp(-dt / Math.Min(0.08, persistence));
        bool lit = false;
        for (int i = 0; i < _phosphor.Length; i++)
        {
            float v = _phosphor[i] * decay, k = _ink[i] * inkDecay;
            _phosphor[i] = v < 0.004f ? 0 : v;
            _ink[i] = k < 0.004f ? 0 : k;
            lit |= v > 0 || k > 0;
        }

        // Gain automatique : la crête récente occupe ~90 % du cadre.
        float peak = Math.Max(audio.PeakL, audio.PeakR);
        _gain = peak > 1e-4f ? Math.Min(_gain * 1.02f, 0.9f / peak) : _gain;
        if (peak * _gain > 1) _gain = 0.95f / peak;
        _gain = Math.Clamp(_gain, 0.5f, 400f);

        var (x0, w) = PlotArea();
        bool gonio = OptI("mode") == 0;
        float hx = (w - 1) / 2f, hy = (Height - 1) / 2f;
        int n = audio.Count;
        float hit = Math.Clamp(1800f / Math.Max(1, n), 0.08f, 1f) * 0.35f;
        // Avec les dessins, le son reste en fond, plus discret : les dessins passent devant.
        int drawings = OptI("drawings");
        if (drawings > 0) hit *= 0.3f;
        for (int i = 0; i < n; i++)
        {
            float l = audio.Left[i] * _gain, r = audio.Right[i] * _gain;
            float sx, sy;
            if (gonio) (sx, sy) = ((r - l) * 0.7071f, (l + r) * 0.7071f);
            else (sx, sy) = (l, r);
            int px = x0 + (int)MathF.Round(hx + sx * hx);
            int py = (int)MathF.Round(hy - sy * hy);
            if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) continue;
            ref float cell = ref _phosphor[py * Width + px];
            cell = Math.Min(1.5f, cell + hit);
            lit = true;
        }

        // Dessins tracés par le même faisceau, mêmes couleurs que le son.
        if (drawings > 0)
        {
            // Énergie par pixel de trait, constante quelle que soit la cadence ; au-dessus de 1 car le trait lissé se répartit
            // sur deux pixels : il atteint ainsi le haut de la palette.
            float deposit = 1.4f * (1 - inkDecay);
            _drawer.Update(audio, dt, drawings == 2, Flag("double"), OptF("wobble") / 100f, Flag("spin"), _gain,
                _ink, Width, Height, x0, w, deposit);
            lit |= !_drawer.Idle;
        }
        _lit = lit;

        unsafe
        {
            uint* pixels = (uint*)_bitmap.GetPixels();
            for (int i = 0; i < _phosphor.Length; i++)
            {
                float v = _phosphor[i] + _ink[i];
                pixels[i] = v <= 0 ? 0 : _lut[(int)Math.Min(255, v * 255)];
            }
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
