using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Anneau polaire : le spectre enroulé en cercle (graves en bas, aigus en haut, en miroir gauche / droite).
/// Il respire sur les basses, donne un coup de poing sur chaque temps fort en lâchant une onde, et tourne du côté
/// où penche la stéréo. Trois styles :
/// <list type="bullet">
/// <item>Rayons : rayons LED par bande avec leur crête maintenue, cœur qui tourne en sens inverse ;</item>
/// <item>Écho : chaque instant laisse son contour, qui s'éloigne en grandissant et s'éteint (un tunnel de spectres) ;</item>
/// <item>Orbite 3D : l'anneau couché en perspective sur toute la largeur, des barres qui montent vers le haut,
/// l'avant plus grand et plus vif que l'arrière.</item>
/// </list>
/// </summary>
internal sealed class RingModule : VisualizerModule
{
    private const int Points = 48, MaxEchoes = 4, Trail = 10;

    private readonly SpectrumMapper _mapper = new();
    private readonly float[] _db = new float[Points / 2], _v = new float[Points / 2], _peak = new float[Points / 2];
    private float _bass, _slow, _angle, _balance, _ceiling = -40, _punch;
    private readonly float[] _echoAge = new float[MaxEchoes], _echoSize = new float[MaxEchoes];
    private int _echoes;
    // Style Écho : contours passés (valeurs par bande et rayon de base au moment de la prise).
    private readonly float[][] _trail = Enumerable.Range(0, Trail).Select(_ => new float[Points / 2]).ToArray();
    private readonly float[] _trailBase = new float[Trail], _trailAngle = new float[Trail];
    private int _trailHead, _trailCount;
    private double _trailCarry;
    private bool _quiet = true;
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPath _path = new();

    public RingModule() : base("ring") { }

    private int Style => OptI("style");

    public override bool IsIdle => _quiet && _echoes == 0 && _bass < 0.01f && _peak[0] < 0.01f;

    public override void Update(AudioAnalyzer audio, double dt)
    {
        _quiet = audio.SilentSeconds > 0.3;
        _mapper.Ensure(_db.Length, 40, 14000, audio.SampleRate, audio.FftSize, 3);
        _mapper.MapDb(audio.SpectrumDb, _db, smooth: true);
        // Orbite : les barres retombent plus vite, pour que chaque coup se détache.
        float k = 1 - (float)Math.Exp(-dt / 0.06), fall = (float)dt * (Style == 2 ? 2.2f : 1.2f);
        // Plafond : suit la bande la plus forte (vite en montée, 6 dB/s en descente), pour garder du relief.
        float loudest = -120;
        foreach (float d in _db) loudest = Math.Max(loudest, d);
        _ceiling = loudest > _ceiling ? loudest : Math.Max(loudest, _ceiling - (float)dt * 6);
        float bass = 0;
        for (int i = 0; i < _db.Length; i++)
        {
            float rel = Math.Clamp(1 + (_db[i] - _ceiling) / 36, 0, 1);
            float v = _quiet ? 0 : rel * rel;
            _v[i] = v > _v[i] ? _v[i] + (v - _v[i]) * k * 2 : Math.Max(v, _v[i] - fall);
            _peak[i] = Math.Max(_v[i], _peak[i] - (float)dt * 0.5f);
            if (i < 4) bass += _v[i] / 4;
        }
        _bass += (bass - _bass) * (1 - (float)Math.Exp(-dt / 0.05));

        // Rotation : du côté le plus fort (balance gauche / droite lissée).
        float l = audio.RmsL, r = audio.RmsR;
        float balance = l + r > 1e-4f ? (r - l) / (r + l) : 0;
        _balance += (balance - _balance) * (1 - (float)Math.Exp(-dt / 0.4));
        // Orbite : chaque temps fort donne aussi un élan à la rotation.
        float push = Style == 2 ? 1.6f * _punch : 0;
        _angle = (_angle + (float)dt * (0.15f + 2.5f * _balance + push) * (float)Opt("spin")) % MathF.Tau;

        // Temps fort : coup de poing (l'anneau grossit d'un coup) et une onde qui part. Seulement quand les graves
        // montent nettement au-dessus de leur moyenne récente : un coup de grosse caisse, pas une syllabe de voix.
        if (!_quiet && _bass > 0.3f && _bass > _slow + 0.12f && (_echoes == 0 || _echoAge[_echoes - 1] > 0.15f))
        {
            if (_echoes == MaxEchoes)
            {
                Array.Copy(_echoAge, 1, _echoAge, 0, MaxEchoes - 1);
                Array.Copy(_echoSize, 1, _echoSize, 0, MaxEchoes - 1);
                _echoes--;
            }
            _echoAge[_echoes] = 0;
            _echoSize[_echoes] = 0.6f + 0.4f * _bass;
            _echoes++;
            _punch = 1;
        }
        _slow += (_bass - _slow) * (1 - (float)Math.Exp(-dt / 0.4));
        _punch *= (float)Math.Exp(-dt / 0.12);
        for (int i = 0; i < _echoes; i++)
        {
            _echoAge[i] += (float)dt;
            if (_echoAge[i] < 0.6f) continue;
            Array.Copy(_echoAge, i + 1, _echoAge, i, _echoes - i - 1);
            Array.Copy(_echoSize, i + 1, _echoSize, i, _echoes - i - 1);
            _echoes--;
            i--;
        }

        // Écho : un contour pris 12 fois par seconde.
        _trailCarry += dt * 12;
        if (_trailCarry >= 1)
        {
            _trailCarry -= Math.Floor(_trailCarry);
            _trailHead = (_trailHead + 1) % Trail;
            Array.Copy(_v, _trail[_trailHead], _v.Length);
            _trailBase[_trailHead] = BaseRadius;
            _trailAngle[_trailHead] = _angle;
            _trailCount = Math.Min(Trail, _trailCount + 1);
        }
    }

    private float BaseRadius => (0.3f + 0.12f * _bass) * (1 + 0.15f * _punch);

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        _stroke.StrokeWidth = ctx.P;
        switch (Style)
        {
            case 1: RenderEcho(canvas, rect, ctx); break;
            case 2: RenderOrbit(canvas, rect, ctx); break;
            default: RenderRays(canvas, rect, ctx); break;
        }
        ctx.ApplyScanlines(canvas, rect);
    }

    /// <summary>Rayons du cercle (ou de l'ellipse si l'anneau est étiré sur toute la largeur).</summary>
    private (float Rx, float Ry) Radii(SKRect rect, RenderContext ctx)
    {
        float ry = rect.Height / 2 - ctx.P;
        float rx = Flag("stretch") ? rect.Width / 2 - Math.Max(ctx.P, ctx.EdgeInset) : ry;
        return (rx, ry);
    }

    private static int BandOf(int i, int half) => Math.Clamp(i < half ? i : Points - 1 - i, 0, half - 1);

    private void DrawEchoes(SKCanvas canvas, float cx, float cy, float rx, float ry, float baseR, Theme t)
    {
        for (int e = 0; e < _echoes; e++)
        {
            float age = _echoAge[e] / 0.6f;
            float s = baseR + (1.2f - baseR) * age * _echoSize[e];
            _stroke.Color = t.Spectral(0.5f + 0.5f * (1 - age)).WithAlpha((byte)(190 * (1 - age)));
            canvas.DrawOval(cx, cy, rx * s, ry * s, _stroke);
        }
    }

    private void RenderRays(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float cx = rect.MidX, cy = rect.MidY;
        var (rx, ry) = Radii(rect, ctx);
        float baseR = BaseRadius, amp = 1 - baseR;
        DrawEchoes(canvas, cx, cy, rx, ry, baseR, t);

        int half = _v.Length;
        _path.Rewind();
        for (int j = 0; j <= Points; j++)
        {
            int i = j % Points, band = BandOf(i, half);
            float a = _angle + MathF.PI / 2 + MathF.Tau * i / Points;
            float ca = MathF.Cos(a), sa = MathF.Sin(a);
            float v = _v[band], tip = baseR + amp * v;
            float x0 = MathF.Round(cx + ca * rx * baseR), y0 = MathF.Round(cy + sa * ry * baseR);
            float x1 = MathF.Round(cx + ca * rx * tip), y1 = MathF.Round(cy + sa * ry * tip);
            if (j == 0) _path.MoveTo(x1, y1);
            else _path.LineTo(x1, y1);
            if (j == Points) break;
            if (v > 0.02f)
            {
                _stroke.Color = t.Spectral(0.25f + 0.75f * v).WithAlpha((byte)(150 + 105 * v));
                canvas.DrawLine(x0, y0, x1, y1, _stroke);
            }
            // Crête maintenue : un point qui retombe lentement.
            float pk = baseR + amp * _peak[band];
            if (_peak[band] > 0.05f)
            {
                _fill.Color = t.Spectral(Math.Min(1, 0.6f + 0.4f * _peak[band]));
                canvas.DrawRect(MathF.Round(cx + ca * rx * pk), MathF.Round(cy + sa * ry * pk), p, p, _fill);
            }
        }
        // Contour des pointes, en filigrane, et voile coloré à l'intérieur.
        _fill.Color = t.Spectral(0.2f + 0.3f * _bass).WithAlpha((byte)(0x30 + 0x40 * _punch));
        canvas.DrawPath(_path, _fill);
        _stroke.Color = t.Spectral(0.9f).WithAlpha(0x70);
        canvas.DrawPath(_path, _stroke);
        DrawCore(canvas, cx, cy, rx, ry, baseR, p, t);
    }

    /// <summary>Cœur : cercle en pointillés qui tourne en sens inverse, plus vif sur les temps forts.</summary>
    private void DrawCore(SKCanvas canvas, float cx, float cy, float rx, float ry, float baseR, int p, Theme t)
    {
        _fill.Color = t.Spectral(1f).WithAlpha((byte)(150 + 105 * _punch));
        float core = baseR * 0.6f;
        for (int i = 0; i < 12; i++)
        {
            float a = -_angle * 1.7f + MathF.Tau * i / 12;
            canvas.DrawRect(MathF.Round(cx + MathF.Cos(a) * rx * core), MathF.Round(cy + MathF.Sin(a) * ry * core), p, p, _fill);
        }
    }

    private void Outline(float[] values, float baseR, float angle, float scale, float cx, float cy, float rx, float ry)
    {
        int half = values.Length;
        float amp = 1 - baseR;
        _path.Rewind();
        for (int j = 0; j <= Points; j++)
        {
            int i = j % Points;
            float a = angle + MathF.PI / 2 + MathF.Tau * i / Points;
            float radius = (baseR + amp * values[BandOf(i, half)]) * scale;
            float x = MathF.Round(cx + MathF.Cos(a) * rx * radius), y = MathF.Round(cy + MathF.Sin(a) * ry * radius);
            if (j == 0) _path.MoveTo(x, y);
            else _path.LineTo(x, y);
        }
    }

    private void RenderEcho(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        float cx = rect.MidX, cy = rect.MidY;
        var (rx, ry) = Radii(rect, ctx);
        // Les contours passés s'éloignent en grandissant, du plus ancien (dehors, éteint) au plus récent.
        float slide = (float)_trailCarry;
        for (int age = _trailCount - 1; age >= 1; age--)
        {
            int index = (_trailHead - age + Trail) % Trail;
            float pos = (age - 1 + slide) / (Trail - 1);
            float scale = 1 + pos * 1.6f;
            Outline(_trail[index], _trailBase[index], _trailAngle[index], scale, cx, cy, rx, ry);
            float fresh = 1 - pos;
            _stroke.Color = t.Spectral(0.25f + 0.75f * fresh).WithAlpha((byte)(230 * fresh * fresh));
            canvas.DrawPath(_path, _stroke);
        }
        // Le contour du moment, plein, devant.
        float baseR = BaseRadius;
        Outline(_v, baseR, _angle, 1, cx, cy, rx, ry);
        _fill.Color = t.Spectral(0.2f + 0.3f * _bass).WithAlpha((byte)(0x40 + 0x40 * _punch));
        canvas.DrawPath(_path, _fill);
        _stroke.Color = t.Spectral(1f);
        canvas.DrawPath(_path, _stroke);
        DrawCore(canvas, cx, cy, rx, ry, baseR, ctx.P, t);
    }

    private void RenderOrbit(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        // L'anneau est couché : une ellipse large et plate en bas du module, les barres montent vers le haut.
        float inset = Math.Max(p, ctx.EdgeInset * 0.6f);
        float cx = rect.MidX;
        // Temps fort : l'anneau saute (le sol remonte, s'ouvre) et les barres grandissent un instant.
        float floorY = rect.Top + rect.Height * (0.7f - 0.06f * _punch);
        float rx = rect.Width / 2 - inset - p, ry = rect.Height * 0.2f * (1 + 0.3f * _punch);
        float tall = rect.Height * 0.62f * (1 + 0.15f * _punch);
        const int Count = 64;
        int half = _v.Length;

        // Ondes sur le sol, autour de l'anneau.
        for (int e = 0; e < _echoes; e++)
        {
            float age = _echoAge[e] / 0.6f, s = 1 + 0.5f * age * _echoSize[e];
            _stroke.Color = t.Spectral(0.5f + 0.5f * (1 - age)).WithAlpha((byte)(150 * (1 - age)));
            canvas.DrawOval(cx, floorY, rx * s, ry * s, _stroke);
        }

        // Deux passes : l'arrière (loin, petit, sombre) puis l'avant (près, grand, vif).
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < Count; i++)
            {
                float a = _angle + MathF.Tau * i / Count;
                float sa = MathF.Sin(a);
                if ((pass == 0) != (sa < 0)) continue;
                float depth = (sa + 1) / 2; // 0 au fond, 1 devant
                // Bande : graves devant au centre, aigus au fond, en miroir.
                int mirrored = i < Count / 2 ? i : Count - 1 - i;
                int band = Math.Clamp(mirrored * half / (Count / 2), 0, half - 1);
                float v = _v[band];
                float x = MathF.Round(cx + MathF.Cos(a) * rx), y = MathF.Round(floorY + sa * ry);
                float h = MathF.Round(v * tall * (0.45f + 0.55f * depth));
                byte alpha = (byte)(70 + 185 * depth);
                // Point du sol : l'anneau reste visible même sans son.
                _fill.Color = t.Spectral(0.3f + 0.4f * depth).WithAlpha((byte)(alpha * 0.6f));
                canvas.DrawRect(x, y, p, p, _fill);
                if (h >= p)
                {
                    _fill.Color = t.Spectral(Math.Min(1, 0.25f + 0.75f * v + 0.2f * _punch)).WithAlpha(alpha);
                    float w = depth > 0.75f ? 2 * p : p;
                    canvas.DrawRect(x, y - h, w, h, _fill);
                }
                // Crête maintenue au-dessus de la barre.
                float pk = MathF.Round(_peak[band] * tall * (0.45f + 0.55f * depth));
                if (_peak[band] > 0.05f)
                {
                    _fill.Color = t.Spectral(Math.Min(1, 0.6f + 0.4f * _peak[band])).WithAlpha(alpha);
                    canvas.DrawRect(x, y - pk - p, p, p, _fill);
                }
            }
        }
    }

    public override void Dispose()
    {
        _stroke.Dispose();
        _fill.Dispose();
        _path.Dispose();
    }
}
