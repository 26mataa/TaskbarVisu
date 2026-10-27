using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Hyperespace : un champ d'étoiles qui fonce vers nous. Chaque bande de fréquence fait naître des étoiles
/// à proportion de son énergie, de sa couleur (graves → début de la palette, aigus → fin), et elles brillent
/// quand leur bande sonne. La vitesse suit le volume ; sur chaque coup de basse, saut en vitesse lumière :
/// traînées étirées et anneau d'étoiles qui jaillit du centre. Le champ roule du côté où penche la stéréo.
/// Dans les cas extrêmes (le son dépasse de loin sa moyenne), distorsion de vitesse : l'espace se creuse,
/// une onde parcourt le tunnel, les couleurs se séparent et l'image tremble.
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
    // Moyenne longue de départ : un niveau de musique typique, pour ne pas partir en distorsion au premier morceau.
    private float _long = 0.12f, _drive, _time;

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

        // Distorsion de vitesse : seulement dans les cas extrêmes (le son dépasse de loin sa moyenne des
        // dernières secondes, ou un coup de basse énorme). Elle monte d'un coup et se dissipe lentement.
        float surge = _long > 1e-4f ? energy / _long : 0;
        float extreme = Flag("warp") && !quiet && energy > 0.02f
            ? Math.Clamp(Math.Max((surge - 1.6f) / 0.8f, (_kick - 1.1f) / 0.4f), 0, 1) : 0;
        _drive += (extreme - _drive) * (1 - (float)Math.Exp(-dt / (extreme > _drive ? 0.08 : 0.8)));
        // Moyenne longue : figée dans les silences (un morceau qui démarre n'est pas un cas extrême).
        if (!quiet) _long += (energy - _long) * (1 - (float)Math.Exp(-dt / 4));
        _time += (float)dt;

        float speed = (_speed + 2.5f * _kick + 3 * _drive) * (float)Opt("speed");

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
        float d = _drive;
        // Distorsion : l'image tremble d'un pixel technique.
        float shake = d > 0.3f ? MathF.Round(MathF.Sin(_time * 61) * p * d) : 0;
        float cx = rect.MidX + shake, cy = rect.MidY;
        float hw = rect.Width / 2, hh = rect.Height / 2;
        // Projection : le tunnel remplit la largeur ; l'écran étant plat, on écrase la hauteur.
        float fx = rect.Width * 0.5f * Near * 1.6f, fy = rect.Height * 0.5f * Near * 1.6f;
        float cos = MathF.Cos(_roll), sin = MathF.Sin(_roll);
        bool trails = Flag("trails");
        // Traînées : courtes au repos, très étirées pendant un saut, interminables dans la distorsion.
        float warp = Math.Min(1, _kick + d);
        float stretch = 1.3f + 1.2f * warp + 1.5f * d, factor = 2.5f + 5 * warp + 6 * d;
        _trail.StrokeWidth = p;

        // Parois du tunnel : anneaux pointillés qui filent vers nous pendant la distorsion.
        if (d > 0.05f)
        {
            for (int ring = 0; ring < 5; ring++)
            {
                float r = (_time * 1.6f + ring / 5f) % 1f;
                r *= r;
                _fill.Color = t.Spectral(0.3f + 0.7f * r).WithAlpha((byte)(140 * d * r));
                for (int k = 0; k < 28; k++)
                {
                    float a = _roll + MathF.Tau * k / 28;
                    canvas.DrawRect(MathF.Round(cx + MathF.Cos(a) * hw * r * 1.1f), MathF.Round(cy + MathF.Sin(a) * hh * r * 1.1f), p, p, _fill);
                }
            }
        }

        for (int i = 0; i < _count; i++)
        {
            float z = _z[i];
            float sx = _x[i] * cos - _y[i] * sin, sy = _x[i] * sin + _y[i] * cos;
            var (x, y) = Warp(cx + sx * fx / z, cy + sy * fy / z, cx, cy, hw, hh, d);
            if (x < rect.Left - 4 || x > rect.Right + 4 || y < rect.Top - 4 || y > rect.Bottom + 4) continue;
            // Plus proche = plus claire et plus grosse ; une étoile brille aussi quand sa bande sonne.
            float closeness = Math.Clamp(1 - (z - Near) / 0.9f, 0, 1);
            float glow = _level[Math.Min(Bands - 1, (int)(_tone[i] * Bands))];
            float tone = Math.Clamp(_tone[i] * 0.8f + 0.2f * glow + 0.3f * d, 0, 1);
            var color = t.Spectral(tone).WithAlpha((byte)Math.Clamp(25 + 175 * closeness * closeness + 70 * glow + 60 * d, 0, 255));
            if (trails)
            {
                float zt = Math.Min(z * stretch, z + (_pz[i] - z) * factor);
                var (px, py) = Warp(cx + sx * fx / zt, cy + sy * fy / zt, cx, cy, hw, hh, d);
                if (MathF.Abs(px - x) + MathF.Abs(py - y) >= p)
                {
                    // Distorsion : les couleurs se séparent de part et d'autre de la traînée.
                    if (d > 0.15f)
                    {
                        float o = MathF.Round(p * (1 + 2 * d));
                        byte a = (byte)(color.Alpha * 0.55f * d);
                        _trail.Color = t.Spectral(0).WithAlpha(a);
                        canvas.DrawLine(MathF.Round(px) - o, MathF.Round(py), MathF.Round(x) - o, MathF.Round(y), _trail);
                        _trail.Color = t.Spectral(1).WithAlpha(a);
                        canvas.DrawLine(MathF.Round(px) + o, MathF.Round(py), MathF.Round(x) + o, MathF.Round(y), _trail);
                    }
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

    /// <summary>
    /// Déformation de l'espace pendant la distorsion : le centre se creuse (les étoiles sont repoussées vers les
    /// bords, comme une lentille) et une onde parcourt le tunnel vers l'extérieur.
    /// </summary>
    private (float X, float Y) Warp(float x, float y, float cx, float cy, float hw, float hh, float d)
    {
        if (d < 0.01f) return (x, y);
        float dx = (x - cx) / hw, dy = (y - cy) / hh;
        float r = MathF.Max(0.02f, MathF.Min(1.5f, MathF.Sqrt(dx * dx + dy * dy)));
        // Le centre se vide : chaque étoile est repoussée d'une distance fixe vers le bord (trou de 0,4 au plus fort).
        float pushed = r + 0.4f * d * MathF.Max(0, 1 - r);
        float k = pushed / r * (1 + d * 0.12f * MathF.Sin(10 * r - _time * 18));
        return (cx + (x - cx) * k, cy + (y - cy) * k);
    }

    public override void Dispose()
    {
        _fill.Dispose();
        _trail.Dispose();
    }
}
