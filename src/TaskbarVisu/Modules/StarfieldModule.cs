using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Hyperespace : un champ d'étoiles qui fonce vers nous. Toutes les étoiles naissent au loin, au centre, et
/// filent tout droit vers les bords. Chaque bande de fréquence fait naître ses étoiles à proportion de son énergie,
/// de sa couleur (graves → début de la palette, aigus → fin), et elles brillent quand leur bande sonne.
/// La vitesse suit le volume en douceur ; un vrai coup de grosse caisse (graves profonds seulement, pas la voix)
/// donne un bond et étire les traînées, un coup de charleston une petite poussée. Sur un morceau puissant, la vitesse
/// suit son relief : les creux ralentissent, les gros moments foncent à toute allure. Dans les cas extrêmes (une montée qui dépasse de loin la moyenne, avec des
/// basses), distorsion de vitesse : traînées très longues, couleurs qui se séparent, anneaux qui partent du centre.
/// </summary>
internal sealed class StarfieldModule : VisualizerModule
{
    private const int MaxStars = 600, Bands = 8;
    private const float Near = 0.06f;

    private readonly float[] _x = new float[MaxStars], _y = new float[MaxStars], _z = new float[MaxStars];
    private readonly float[] _pz = new float[MaxStars], _tone = new float[MaxStars];
    private int _count;
    private readonly SpectrumMapper _mapper = new();
    private readonly float[] _bandDb = new float[Bands], _level = new float[Bands];
    private readonly float[] _spawn = new float[Bands];
    private readonly Random _random = new();
    private float _speed, _kick, _kickSlowDb = -60, _sinceKick = 9;
    // Moyenne longue de départ : un niveau de musique typique, pour ne pas partir en distorsion au premier morceau.
    private float _long = 0.12f, _drive, _time, _over, _power;
    private float _fastDb = -60, _avgDb = -20, _hat, _hatSlowDb = -60;

    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _trail = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Square };

    public StarfieldModule() : base("starfield") { }

    public override bool IsIdle => _count == 0;

    public override void Update(AudioAnalyzer audio, double dt)
    {
        bool quiet = audio.SilentSeconds > 0.3;
        _mapper.Ensure(Bands, 40, 12000, audio.SampleRate, audio.FftSize, 3);
        _mapper.MapDb(audio.SpectrumDb, _bandDb);
        float rise = 1 - (float)Math.Exp(-dt / 0.06);
        for (int b = 0; b < Bands; b++)
        {
            float v = quiet ? 0 : Math.Clamp((_bandDb[b] + 75) / 55, 0, 1);
            _level[b] = v > _level[b] ? _level[b] + (v - _level[b]) * rise : Math.Max(v, _level[b] - (float)dt * 1.5f);
        }

        // Coup de grosse caisse : la bande la plus grave (40–80 Hz, que la voix n'atteint presque pas) saute
        // de quelques dB au-dessus de sa moyenne récente. En dB bruts : sur un son mixé à fond, les niveaux
        // affichés saturent et ne laisseraient plus voir les coups.
        float bass = _level[0], kickDb = _bandDb[0];
        _sinceKick += (float)dt;
        bool kick = !quiet && bass > 0.4f && kickDb > _kickSlowDb + 5 && _sinceKick > 0.15f;
        if (kick)
        {
            _kick = Math.Min(1, Math.Max(_kick, 0.4f + (kickDb - _kickSlowDb - 5) * 0.08f));
            _sinceKick = 0;
        }
        _kickSlowDb += (kickDb - _kickSlowDb) * (1 - (float)Math.Exp(-dt / 0.4));
        _kick *= (float)Math.Exp(-dt / 0.25);

        // Vitesse : suit le volume en douceur (monte en 0,15 s, retombe en 0,5 s).
        float energy = quiet ? 0 : (audio.RmsL + audio.RmsR) / 2;
        float target = quiet ? 0.05f : 0.25f + Math.Min(1f, energy * 4);
        _speed += (target - _speed) * (1 - (float)Math.Exp(-dt / (target > _speed ? 0.15 : 0.5)));

        // Puissance du morceau (sonie perçue : rien sous −14 LUFS, à fond vers −3) : elle ne fait pas aller vite
        // à elle seule, elle fixe jusqu'où la vitesse peut monter. La voix reste en dessous.
        float power = quiet ? 0 : Math.Clamp(((float)audio.Loudness.Momentary + 14) / 11, 0, 1);
        _power += (power - _power) * (1 - (float)Math.Exp(-dt / 1.0));

        // Relief : le niveau de l'instant (en dB) face à la moyenne des dernières secondes du morceau.
        // Les creux et les pauses ralentissent, les gros moments foncent.
        float db = energy > 1e-5f ? 20 * MathF.Log10(energy) : -100;
        _fastDb += (db - _fastDb) * (1 - (float)Math.Exp(-dt / (db > _fastDb ? 0.04 : 0.15)));
        if (!quiet) _avgDb += (_fastDb - _avgDb) * (1 - (float)Math.Exp(-dt / 3));
        float lift = Math.Clamp((_fastDb - _avgDb + 3) / 6, 0, 1);

        // Charleston : la bande la plus aiguë qui claque de quelques dB au-dessus de sa moyenne récente.
        float hatDb = _bandDb[Bands - 1];
        if (!quiet && hatDb > -55 && hatDb > _hatSlowDb + 4)
            _hat = Math.Max(_hat, Math.Min(1, 0.4f + (hatDb - _hatSlowDb - 4) * 0.1f));
        _hatSlowDb += (hatDb - _hatSlowDb) * (1 - (float)Math.Exp(-dt / 0.3));
        _hat *= (float)Math.Exp(-dt / 0.08);

        // Surpuissance : puissance × relief, qui suit vite (monte en 0,08 s, retombe en 0,3 s).
        float over = _power * _power * lift;
        _over += (over - _over) * (1 - (float)Math.Exp(-dt / (over > _over ? 0.08 : 0.3)));

        // Distorsion de vitesse : seulement dans les cas extrêmes, quand le son dépasse de loin sa moyenne des
        // dernières secondes ET que les graves cognent (une montée de musique, pas quelqu'un qui parle fort).
        float surge = _long > 1e-4f ? energy / _long : 0;
        float extreme = Flag("warp") && !quiet && energy > 0.02f
            ? Math.Clamp((surge - 1.9f) / 0.8f, 0, 1) * Math.Clamp((bass - 0.45f) / 0.25f, 0, 1) : 0;
        _drive += (extreme - _drive) * (1 - (float)Math.Exp(-dt / (extreme > _drive ? 0.15 : 0.9)));
        // Moyenne longue : figée dans les silences (un morceau qui démarre n'est pas un cas extrême).
        if (!quiet) _long += (energy - _long) * (1 - (float)Math.Exp(-dt / 4));
        _time += (float)dt;

        // Les coups de grosse caisse et de charleston poussent d'autant plus fort que le morceau est puissant.
        float speed = (_speed + 1.5f * _kick * (1 + _power) + 0.8f * _hat * _power + 1.5f * _drive + 4 * _over)
            * (float)Opt("speed");

        // Naissances : chaque bande sème des étoiles selon son niveau, au loin, tout près de l'axe
        // (plus vite quand ça fonce, sinon les étoiles passent trop vite pour remplir le ciel).
        float density = OptF("density");
        for (int b = 0; b < Bands && !quiet; b++)
        {
            _spawn[b] += (float)dt * density * _level[b] * _level[b] * (1 + 0.5f * _kick + 2 * _over);
            while (_spawn[b] >= 1 && _count < MaxStars)
            {
                _spawn[b] -= 1;
                float a = _random.NextSingle() * MathF.Tau, radius = 0.1f + 0.9f * MathF.Sqrt(_random.NextSingle());
                Add(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0.9f + 0.4f * _random.NextSingle(), (b + _random.NextSingle()) / Bands);
            }
            _spawn[b] = Math.Min(_spawn[b], 1);
        }

        // Gros coup : une gerbe d'étoiles part du centre et s'ouvre d'un coup.
        if (kick && _kick > 0.8f)
        {
            int ring = (int)(8 + density * 0.15f);
            float z = 0.8f, offset = _random.NextSingle() * MathF.Tau;
            for (int i = 0; i < ring && _count < MaxStars; i++)
            {
                float a = offset + MathF.Tau * i / ring;
                Add(MathF.Cos(a) * 0.3f, MathF.Sin(a) * 0.3f, z, 0.85f + 0.15f * _random.NextSingle());
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
        float cx = rect.MidX, cy = rect.MidY;
        float hw = rect.Width / 2, hh = rect.Height / 2;
        // Projection : le tunnel remplit la largeur ; l'écran étant plat, on écrase la hauteur.
        float fx = rect.Width * 0.5f * Near * 1.6f, fy = rect.Height * 0.5f * Near * 1.6f;
        bool trails = Flag("trails");
        // Traînées : courtes au repos, étirées sur un coup ou en surpuissance, très longues dans la distorsion.
        float warp = Math.Min(1, _kick + d + 0.6f * _over);
        float stretch = 1.35f + 0.6f * warp + 0.8f * d + 0.6f * _over, factor = 2.5f + 3 * warp + 3 * d;
        _trail.StrokeWidth = p;

        // Distorsion : anneaux pointillés qui naissent au centre et filent vers les bords.
        if (d > 0.05f)
        {
            for (int ring = 0; ring < 4; ring++)
            {
                float r = (_time * 1.2f + ring / 4f) % 1f;
                r *= r;
                _fill.Color = t.Spectral(0.3f + 0.7f * r).WithAlpha((byte)(100 * d * r));
                for (int k = 0; k < 24; k++)
                {
                    float a = MathF.Tau * k / 24;
                    canvas.DrawRect(MathF.Round(cx + MathF.Cos(a) * hw * r), MathF.Round(cy + MathF.Sin(a) * hh * r), p, p, _fill);
                }
            }
        }

        for (int i = 0; i < _count; i++)
        {
            float z = _z[i];
            float x = cx + _x[i] * fx / z, y = cy + _y[i] * fy / z;
            if (x < rect.Left - 4 || x > rect.Right + 4 || y < rect.Top - 4 || y > rect.Bottom + 4) continue;
            // Plus proche = plus claire et plus grosse ; une étoile brille aussi quand sa bande sonne.
            float closeness = Math.Clamp(1 - (z - Near) / 1.2f, 0, 1);
            // Les étoiles aiguës scintillent sur chaque coup de charleston.
            float glow = _level[Math.Min(Bands - 1, (int)(_tone[i] * Bands))] + (_tone[i] > 0.75f ? _hat * _power : 0);
            float tone = Math.Clamp(_tone[i] * 0.85f + 0.15f * glow, 0, 1);
            var color = t.Spectral(tone).WithAlpha((byte)Math.Clamp(25 + 180 * closeness * closeness + 50 * glow, 0, 255));
            if (trails)
            {
                // Traînée : où était l'étoile un peu plus tôt, sur la même droite qui part du centre.
                float zt = Math.Min(z * stretch, z + (_pz[i] - z) * factor);
                float px = cx + _x[i] * fx / zt, py = cy + _y[i] * fy / zt;
                if (MathF.Abs(px - x) + MathF.Abs(py - y) >= p)
                {
                    // Distorsion : les couleurs se séparent de part et d'autre de la traînée.
                    if (d > 0.2f)
                    {
                        float o = p;
                        byte a = (byte)(color.Alpha * 0.45f * d);
                        _trail.Color = t.Spectral(0).WithAlpha(a);
                        canvas.DrawLine(MathF.Round(px) - o, MathF.Round(py), MathF.Round(x) - o, MathF.Round(y), _trail);
                        _trail.Color = t.Spectral(1).WithAlpha(a);
                        canvas.DrawLine(MathF.Round(px) + o, MathF.Round(py), MathF.Round(x) + o, MathF.Round(y), _trail);
                    }
                    _trail.Color = color.WithAlpha((byte)(color.Alpha * (0.5f + 0.2f * warp)));
                    canvas.DrawLine(MathF.Round(px), MathF.Round(py), MathF.Round(x), MathF.Round(y), _trail);
                }
            }
            float size = closeness > 0.8f ? 2 * p : p;
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
