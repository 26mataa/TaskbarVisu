using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Hyperespace : un champ d'étoiles qui fonce vers nous. Chaque bande de fréquence fait naître des étoiles
/// à proportion de son énergie, de sa couleur (graves → début de la palette, aigus → fin), et elles brillent
/// quand leur bande sonne. La vitesse suit le volume ; sur chaque coup de basse, saut en vitesse lumière :
/// traînées étirées et anneau d'étoiles qui jaillit du centre. Le champ roule du côté où penche la stéréo.
/// </summary>
internal sealed class StarfieldModule : VisualizerModule
{
    private const int MaxStars = 700, Bands = 8;
    private const float Near = 0.06f;

    private readonly float[] _x = new float[MaxStars], _y = new float[MaxStars], _z = new float[MaxStars];
    private readonly float[] _pz = new float[MaxStars], _tone = new float[MaxStars];
    private int _count;
    private readonly SpectrumMapper _mapper = new();
    private readonly float[] _bandDb = new float[Bands], _level = new float[Bands];
    private readonly float[] _spawn = new float[Bands];
    private readonly Random _random = new();
    private float _speed, _kick, _bassSlow, _sinceKick = 9, _roll, _balance;

    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _trail = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Square };

    public StarfieldModule() : base("starfield") { }

    public override bool IsIdle => _count == 0;

    public override void Update(AudioAnalyzer audio, double dt)
    {
        bool quiet = audio.SilentSeconds > 0.3;
        _mapper.Ensure(Bands, 40, 12000, audio.SampleRate, audio.FftSize, 3);
        _mapper.MapDb(audio.SpectrumDb, _bandDb);
        float fast = 1 - (float)Math.Exp(-dt / 0.04);
        for (int b = 0; b < Bands; b++)
        {
            float v = quiet ? 0 : Math.Clamp((_bandDb[b] + 75) / 55, 0, 1);
            _level[b] = v > _level[b] ? _level[b] + (v - _level[b]) * fast : Math.Max(v, _level[b] - (float)dt * 2.5f);
        }

        // Coup de basse : les deux premières bandes montent nettement au-dessus de leur moyenne récente.
        float bass = (_level[0] + _level[1]) / 2;
        _sinceKick += (float)dt;
        bool kick = !quiet && bass > 0.3f && bass > _bassSlow + 0.08f && _sinceKick > 0.12f;
        if (kick)
        {
            _kick = Math.Min(1.5f, Math.Max(_kick, 0.6f + (bass - _bassSlow) * 4));
            _sinceKick = 0;
        }
        _bassSlow += (bass - _bassSlow) * (1 - (float)Math.Exp(-dt / 0.35));
        _kick *= (float)Math.Exp(-dt / 0.22);

        // Vitesse : monte vite avec le volume, retombe en douceur.
        float energy = quiet ? 0 : (audio.RmsL + audio.RmsR) / 2;
        float target = quiet ? 0.05f : 0.2f + Math.Min(1.4f, energy * 6);
        _speed += (target - _speed) * (1 - (float)Math.Exp(-dt / (target > _speed ? 0.06 : 0.35)));
        float speed = (_speed + 2.5f * _kick) * (float)Opt("speed");

        // Roulis : le champ tourne du côté le plus fort.
        float l = audio.RmsL, r = audio.RmsR;
        float balance = l + r > 1e-4f ? (r - l) / (r + l) : 0;
        _balance += (balance - _balance) * (1 - (float)Math.Exp(-dt / 0.3));
        _roll = (_roll + (float)dt * _balance * 2.5f) % MathF.Tau;

        // Naissances : chaque bande sème des étoiles selon son niveau.
        float density = OptF("density");
        for (int b = 0; b < Bands && !quiet; b++)
        {
            _spawn[b] += (float)dt * density * _level[b] * _level[b] * (1 + _kick);
            while (_spawn[b] >= 1 && _count < MaxStars)
            {
                _spawn[b] -= 1;
                float a = _random.NextSingle() * MathF.Tau, radius = 0.15f + 0.85f * MathF.Sqrt(_random.NextSingle());
                Add(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0.7f + 0.3f * _random.NextSingle(), (b + _random.NextSingle()) / Bands);
            }
            _spawn[b] = Math.Min(_spawn[b], 1);
        }

        // Saut : un anneau d'étoiles part du centre, toutes à la même profondeur, et s'ouvre d'un coup.
        if (kick)
        {
            int ring = (int)(12 + density * 0.3f);
            float z = 0.45f + 0.1f * _random.NextSingle(), offset = _random.NextSingle() * MathF.Tau;
            for (int i = 0; i < ring && _count < MaxStars; i++)
            {
                float a = offset + MathF.Tau * i / ring;
                Add(MathF.Cos(a) * 0.35f, MathF.Sin(a) * 0.35f, z, 0.85f + 0.15f * _random.NextSingle());
            }
        }

        // Avancée : z diminue, l'étoile disparaît une fois passée devant nous.
        float dz = (float)dt * speed;
        for (int i = 0; i < _count; i++)
        {
            _pz[i] = _z[i];
            _z[i] -= dz;
            if (_z[i] > Near) continue;
            int last = --_count;
            _x[i] = _x[last]; _y[i] = _y[last]; _z[i] = _z[last]; _pz[i] = _pz[last]; _tone[i] = _tone[last];
            i--;
        }
    }

    private void Add(float x, float y, float z, float tone)
    {
        int i = _count++;
        _x[i] = x;
        _y[i] = y;
        _z[i] = _pz[i] = z;
        _tone[i] = tone;
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float cx = rect.MidX, cy = rect.MidY;
        // Projection : le tunnel remplit la largeur ; l'écran étant plat, on écrase la hauteur.
        float fx = rect.Width * 0.5f * Near * 1.6f, fy = rect.Height * 0.5f * Near * 1.6f;
        float cos = MathF.Cos(_roll), sin = MathF.Sin(_roll);
        bool trails = Flag("trails");
        // Traînées : courtes au repos, très étirées pendant un saut.
        float warp = Math.Min(1, _kick);
        float stretch = 1.3f + 1.2f * warp, factor = 2.5f + 5 * warp;
        _trail.StrokeWidth = p;
        for (int i = 0; i < _count; i++)
        {
            float z = _z[i];
            float sx = _x[i] * cos - _y[i] * sin, sy = _x[i] * sin + _y[i] * cos;
            float x = cx + sx * fx / z, y = cy + sy * fy / z;
            if (x < rect.Left - 4 || x > rect.Right + 4 || y < rect.Top - 4 || y > rect.Bottom + 4) continue;
            // Plus proche = plus claire et plus grosse ; une étoile brille aussi quand sa bande sonne.
            float closeness = Math.Clamp(1 - (z - Near) / 0.9f, 0, 1);
            float glow = _level[Math.Min(Bands - 1, (int)(_tone[i] * Bands))];
            float tone = Math.Clamp(_tone[i] * 0.8f + 0.2f * glow, 0, 1);
            var color = t.Spectral(tone).WithAlpha((byte)Math.Clamp(25 + 175 * closeness * closeness + 70 * glow, 0, 255));
            if (trails)
            {
                float zt = Math.Min(z * stretch, z + (_pz[i] - z) * factor);
                float px = cx + sx * fx / zt, py = cy + sy * fy / zt;
                if (MathF.Abs(px - x) + MathF.Abs(py - y) >= p)
                {
                    _trail.Color = color.WithAlpha((byte)(color.Alpha * (0.45f + 0.25f * warp)));
                    canvas.DrawLine(MathF.Round(px), MathF.Round(py), MathF.Round(x), MathF.Round(y), _trail);
                }
            }
            float size = closeness > 0.75f - 0.25f * warp ? 2 * p : p;
            _fill.Color = color;
            canvas.DrawRect(MathF.Round(x - size / 2), MathF.Round(y - size / 2), size, size, _fill);
        }
        ctx.ApplyScanlines(canvas, rect);
    }

    public override void Dispose()
    {
        _fill.Dispose();
        _trail.Dispose();
    }
}
