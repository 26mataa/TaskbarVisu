using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Hyperespace : un champ d'étoiles qui fonce vers nous. Chaque bande de fréquence fait naître des étoiles
/// à proportion de son énergie, de sa couleur (graves → début de la palette, aigus → fin) ;
/// la vitesse suit le volume et bondit sur les temps forts, les étoiles s'étirent en traînées.
/// </summary>
internal sealed class StarfieldModule : VisualizerModule
{
    private const int MaxStars = 400, Bands = 8;
    private const float Near = 0.06f;

    private readonly float[] _x = new float[MaxStars], _y = new float[MaxStars], _z = new float[MaxStars];
    private readonly float[] _pz = new float[MaxStars], _tone = new float[MaxStars];
    private int _count;
    private readonly SpectrumMapper _mapper = new();
    private readonly float[] _bandDb = new float[Bands];
    private readonly float[] _spawn = new float[Bands];
    private readonly Random _random = new();
    private float _speed, _slow, _kick;

    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _trail = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Square };

    public StarfieldModule() : base("starfield") { }

    public override bool IsIdle => _count == 0;

    public override void Update(AudioAnalyzer audio, double dt)
    {
        bool quiet = audio.SilentSeconds > 0.3;
        _mapper.Ensure(Bands, 40, 12000, audio.SampleRate, audio.FftSize, 3);
        _mapper.MapDb(audio.SpectrumDb, _bandDb);

        float energy = quiet ? 0 : (audio.RmsL + audio.RmsR) / 2;
        if (energy > _slow * 1.6f && energy > 0.01f) _kick = 1;
        _slow += (energy - _slow) * (1 - (float)Math.Exp(-dt / 0.5));
        _kick *= (float)Math.Exp(-dt / 0.25);
        float target = quiet ? 0.05f : 0.25f + Math.Min(1.2f, energy * 5);
        _speed += (target - _speed) * (1 - (float)Math.Exp(-dt / 0.25));
        float speed = (_speed + 1.8f * _kick) * (float)Opt("speed");

        // Naissances : chaque bande sème des étoiles selon son niveau.
        float density = OptF("density");
        for (int b = 0; b < Bands && !quiet; b++)
        {
            float level = Math.Clamp((_bandDb[b] + 75) / 55, 0, 1);
            _spawn[b] += (float)dt * density * level * level;
            while (_spawn[b] >= 1 && _count < MaxStars)
            {
                _spawn[b] -= 1;
                int i = _count++;
                // Position au hasard dans le tunnel, loin au fond.
                float a = _random.NextSingle() * MathF.Tau, r = 0.15f + 0.85f * MathF.Sqrt(_random.NextSingle());
                _x[i] = MathF.Cos(a) * r;
                _y[i] = MathF.Sin(a) * r;
                _z[i] = _pz[i] = 0.7f + 0.3f * _random.NextSingle();
                _tone[i] = (b + _random.NextSingle()) / Bands;
            }
            _spawn[b] = Math.Min(_spawn[b], 1);
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

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float cx = rect.MidX, cy = rect.MidY;
        // Projection : le tunnel remplit la largeur ; l'écran étant plat, on écrase la hauteur.
        float fx = rect.Width * 0.5f * Near * 1.6f, fy = rect.Height * 0.5f * Near * 1.6f;
        bool trails = Flag("trails");
        _trail.StrokeWidth = p;
        for (int i = 0; i < _count; i++)
        {
            float z = _z[i];
            float x = cx + _x[i] * fx / z, y = cy + _y[i] * fy / z;
            if (x < rect.Left - 4 || x > rect.Right + 4 || y < rect.Top - 4 || y > rect.Bottom + 4) continue;
            // Plus proche = plus claire et plus grosse.
            float closeness = Math.Clamp(1 - (z - Near) / 0.9f, 0, 1);
            var color = t.Spectral(_tone[i]).WithAlpha((byte)(70 + 185 * closeness));
            if (trails)
            {
                // Traînée : où était l'étoile quelques images plus tôt.
                float zt = Math.Min(z * 1.45f, z + (_pz[i] - z) * 3);
                float px = cx + _x[i] * fx / zt, py = cy + _y[i] * fy / zt;
                if (MathF.Abs(px - x) + MathF.Abs(py - y) >= p)
                {
                    _trail.Color = color.WithAlpha((byte)(color.Alpha * 0.5f));
                    canvas.DrawLine(MathF.Round(px), MathF.Round(py), MathF.Round(x), MathF.Round(y), _trail);
                }
            }
            float size = closeness > 0.75f ? 2 * p : p;
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
