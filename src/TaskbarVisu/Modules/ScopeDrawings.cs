using TaskbarVisu.Dsp;

namespace TaskbarVisu.Modules;

/// <summary>
/// Dessins d'oscilloscope (« oscilloscope music ») : par moments, le tracé du son se change en dessin (étoile, cœur,
/// personnage…) puis redevient le son. Un seul faisceau : ce sont les échantillons eux-mêmes qui glissent du nuage
/// vers le dessin, le parcourent (le son le fait trembler) et repartent. Le moment (souvent un temps fort),
/// l'endroit, la taille et le dessin sont tirés au hasard.
/// </summary>
internal sealed class ScopeDrawer
{
    /// <summary>Un dessin : un ou plusieurs tracés (x, y entre -1 et 1, y vers le haut) ; plusieurs variantes = animation au rythme.</summary>
    private sealed record Shape(float[][][] Variants);

    /// <summary>Un tracé prêt à parcourir : ses segments bout à bout (le faisceau saute d'un trait à l'autre sans tracer).</summary>
    private sealed class Path
    {
        public readonly float[] X, Y, DX, DY, Length;
        public readonly float Total;

        public Path(float[][] strokes)
        {
            var segments = new List<(float X, float Y, float DX, float DY, float L)>();
            foreach (var s in strokes)
                for (int i = 2; i < s.Length; i += 2)
                {
                    float dx = s[i] - s[i - 2], dy = s[i + 1] - s[i - 1], length = MathF.Sqrt(dx * dx + dy * dy);
                    if (length > 1e-5f) segments.Add((s[i - 2], s[i - 1], dx, dy, length));
                }
            X = segments.Select(x => x.X).ToArray();
            Y = segments.Select(x => x.Y).ToArray();
            DX = segments.Select(x => x.DX).ToArray();
            DY = segments.Select(x => x.DY).ToArray();
            Length = segments.Select(x => x.L).ToArray();
            Total = Length.Sum();
        }
    }

    private static readonly Shape[] Shapes = BuildShapes();
    private static readonly Path[][] Paths = Shapes.Select(s => s.Variants.Select(v => new Path(v)).ToArray()).ToArray();

    /// <summary>Passage du son au dessin, puis du dessin au son (secondes).</summary>
    private const double MorphIn = 0.22, MorphOut = 0.4;

    private readonly Random _random = new();
    private readonly List<int> _bag = new();
    private int _shape = -1, _beat;
    private bool _active;
    private double _clock, _lastOnset = -1, _lastSound = -10, _start, _end, _nextAllowed = 1;
    private float _slow, _pulse, _u, _v, _size;
    private float[] _x = Array.Empty<float>(), _y = Array.Empty<float>();

    /// <summary>0 = le son seul, 1 = le dessin seul ; entre les deux, chaque point glisse de l'un à l'autre.</summary>
    public float Morph { get; private set; }

    /// <summary>Amplitude (pixels) dont le son fait trembler le dessin.</summary>
    public float Jitter { get; private set; }

    /// <summary>Un dessin est en cours (apparition, tenue ou disparition).</summary>
    public bool Active => _active;

    /// <summary>Position sur le dessin de chaque échantillon de l'image, en pixels (quand <see cref="Morph"/> &gt; 0).</summary>
    public ReadOnlySpan<float> X => _x;

    /// <inheritdoc cref="X"/>
    public ReadOnlySpan<float> Y => _y;

    /// <summary>
    /// Fait vivre les dessins (apparition au hasard, tenue, retour au son) et place chaque échantillon de l'image sur le
    /// dessin courant, dans la zone du tracé (<paramref name="x0"/>, <paramref name="w"/>, <paramref name="height"/>).
    /// </summary>
    public void Update(AudioAnalyzer audio, double dt, bool often, float wobble, bool spin, int x0, int w, int height)
    {
        _clock += dt;
        float energy = (audio.RmsL * audio.RmsL + audio.RmsR * audio.RmsR) / 2;
        float ratio = energy / Math.Max(_slow, 1e-9f);
        _slow += (energy - _slow) * (float)(1 - Math.Exp(-dt / 0.6));
        bool sound = energy > 3e-6f; // ≈ -55 dB
        if (sound) _lastSound = _clock;
        bool onset = sound && ratio > 1.8f && _clock - _lastOnset > 0.18;
        if (onset)
        {
            _lastOnset = _clock;
            _beat++;
            _pulse = 1;
        }
        _pulse *= (float)Math.Exp(-dt / 0.15);

        if (!_active)
        {
            // Pas à chaque coup : un temps fort sur deux environ (plus souvent en mode « Souvent ») ; sans percussions,
            // un dessin finit quand même par venir.
            bool due = sound && _clock >= _nextAllowed;
            if (due && ((onset && _random.NextDouble() < (often ? 0.8 : 0.5)) || _clock >= _nextAllowed + (often ? 1.0 : 2.5)))
                Begin(often);
        }
        if (_active)
        {
            if (_clock - _lastSound > 0.4) _end = Math.Min(_end, _clock); // plus de son : le dessin repart
            float m = Smooth((_clock - _start) / MorphIn) * (1 - Smooth((_clock - _end) / MorphOut));
            if (_clock - _end >= MorphOut)
            {
                _active = false;
                m = 0;
                _nextAllowed = _clock + (often ? Between(0.3, 1.5) : Between(1.5, 5));
            }
            Morph = m;
        }
        else
        {
            Morph = 0;
        }
        if (Morph <= 0) return;

        int n = audio.Count;
        if (_x.Length < n)
        {
            _x = new float[n];
            _y = new float[n];
        }
        if (n == 0) return;

        // Place et taille tirées au départ ; le dessin reste entier dans la zone du tracé.
        float half = Math.Min(w, height) / 2f * _size;
        float cx = x0 + half + _u * Math.Max(0, w - 2 * half), cy = half + _v * Math.Max(0, height - 2 * half);
        float r = half * (1 + 0.12f * _pulse);
        float angle = spin ? (float)(_clock * 0.6) : 0.08f * MathF.Sin((float)_clock * 1.7f);
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        Jitter = wobble * 0.25f * half;

        // Les échantillons parcourent le dessin d'un bout à l'autre pendant l'image, à partir d'un point au hasard :
        // d'une image à l'autre, le phosphore le remplit tout entier.
        var path = Paths[_shape][_beat % Paths[_shape].Length];
        int last = path.Length.Length - 1, j = 0;
        float step = path.Total / n, s = (float)(_random.NextDouble() * path.Total), acc = 0;
        for (int i = 0; i < n; i++)
        {
            while (j < last && acc + path.Length[j] < s)
            {
                acc += path.Length[j];
                j++;
            }
            float t = Math.Clamp((s - acc) / path.Length[j], 0, 1);
            float ux = path.X[j] + path.DX[j] * t, uy = path.Y[j] + path.DY[j] * t;
            _x[i] = cx + (ux * cos - uy * sin) * r;
            _y[i] = cy - (ux * sin + uy * cos) * r;
            s += step;
            if (s >= path.Total)
            {
                s -= path.Total;
                j = 0;
                acc = 0;
            }
        }
    }

    private void Begin(bool often)
    {
        _shape = Next();
        _active = true;
        _start = _clock;
        _end = _clock + MorphIn + (often ? Between(0.9, 2.0) : Between(0.7, 1.5));
        _u = (float)_random.NextDouble();
        _v = (float)_random.NextDouble();
        _size = (float)Between(0.6, 0.95);
    }

    private double Between(double min, double max) => min + _random.NextDouble() * (max - min);

    private static float Smooth(double t)
    {
        float x = (float)Math.Clamp(t, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private int Next()
    {
        if (_bag.Count == 0)
        {
            for (int i = 0; i < Shapes.Length; i++) if (i != _shape) _bag.Add(i);
        }
        int pick = _random.Next(_bag.Count);
        int shape = _bag[pick];
        _bag.RemoveAt(pick);
        return shape;
    }

    // ------------------------------------------------------------ Bibliothèque

    private static Shape[] BuildShapes()
    {
        var list = new List<Shape>
        {
            One(Star(12, 0.5f)),
            One(Star(5, 0.42f)),
            One(Heart()),
            One(BrokenHeart()),
            One(Sun()),
            One(Smiley()),
            One(Eye()),
            One(Notes()),
            One(Bolt()),
            One(Spiral()),
            One(Flower()),
            new Shape(new[] { Dancer(false), Dancer(true) }),
            One(Cat()),
            One(Gem()),
            One(Infinity()),
            One(Planet()),
            new Shape(new[] { Ghost(0), Ghost(1) }),
            One(Fish()),
        };
        return list.ToArray();
    }

    private static Shape One(float[][] strokes) => new(new[] { strokes });

    /// <summary>Ramène les tracés dans [-1, 1] en gardant les proportions, centrés.</summary>
    private static float[][] Fit(List<List<(float X, float Y)>> strokes)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var s in strokes)
        foreach (var (x, y) in s)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2, half = Math.Max(maxX - minX, maxY - minY) / 2;
        return strokes.Select(s => s.SelectMany(p => new[] { (p.X - cx) / half, (p.Y - cy) / half }).ToArray()).ToArray();
    }

    private static List<(float, float)> Arc(float cx, float cy, float rx, float ry, float from, float to, int n)
    {
        var points = new List<(float, float)>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            float t = from + (to - from) * i / n;
            points.Add((cx + rx * MathF.Cos(t), cy + ry * MathF.Sin(t)));
        }
        return points;
    }

    private static List<(float, float)> Circle(float cx, float cy, float r, int n = 28) => Arc(cx, cy, r, r, 0, MathF.Tau, n);

    private static List<(float, float)> Poly(params float[] xy)
    {
        var points = new List<(float, float)>(xy.Length / 2);
        for (int i = 0; i < xy.Length; i += 2) points.Add((xy[i], xy[i + 1]));
        return points;
    }

    private static float[][] Star(int spikes, float inner)
    {
        var s = new List<(float, float)>();
        for (int i = 0; i <= 2 * spikes; i++)
        {
            float a = MathF.PI / 2 + i * MathF.PI / spikes, r = i % 2 == 0 ? 1 : inner;
            s.Add((r * MathF.Cos(a), r * MathF.Sin(a)));
        }
        return Fit(new() { s });
    }

    private static List<(float, float)> HeartCurve()
    {
        var s = new List<(float, float)>();
        for (int i = 0; i <= 40; i++)
        {
            float t = MathF.Tau * i / 40;
            float x = 16 * MathF.Pow(MathF.Sin(t), 3);
            float y = 13 * MathF.Cos(t) - 5 * MathF.Cos(2 * t) - 2 * MathF.Cos(3 * t) - MathF.Cos(4 * t);
            s.Add((x, y));
        }
        return s;
    }

    private static float[][] Heart() => Fit(new() { HeartCurve() });

    private static float[][] BrokenHeart() =>
        Fit(new() { HeartCurve(), Poly(0, 5, -3, 0, 2.5f, -5, -2, -10, 0, -17) });

    private static float[][] Sun()
    {
        var strokes = new List<List<(float, float)>> { Circle(0, 0, 0.5f) };
        for (int i = 0; i < 12; i++)
        {
            float a = i * MathF.Tau / 12;
            strokes.Add(Poly(0.65f * MathF.Cos(a), 0.65f * MathF.Sin(a), MathF.Cos(a), MathF.Sin(a)));
        }
        return Fit(strokes);
    }

    private static float[][] Smiley() => Fit(new()
    {
        Circle(0, 0, 1, 32),
        Circle(-0.35f, 0.3f, 0.12f, 10),
        Circle(0.35f, 0.3f, 0.12f, 10),
        Arc(0, 0, 0.55f, 0.55f, MathF.PI * 1.15f, MathF.PI * 1.85f, 12),
    });

    private static float[][] Eye()
    {
        var upper = new List<(float, float)>();
        var lower = new List<(float, float)>();
        for (int i = 0; i <= 16; i++)
        {
            float x = -1 + i / 8f, y = 0.5f * MathF.Pow(1 - x * x, 0.8f);
            upper.Add((x, y));
            lower.Add((x, -y));
        }
        return Fit(new() { upper, lower, Circle(0, 0, 0.33f, 18), Circle(0, 0, 0.1f, 8) });
    }

    private static float[][] Notes() => Fit(new()
    {
        Arc(-0.5f, -0.62f, 0.28f, 0.2f, 0, MathF.Tau, 14),
        Arc(0.42f, -0.45f, 0.28f, 0.2f, 0, MathF.Tau, 14),
        Poly(-0.22f, -0.6f, -0.22f, 0.75f, 0.7f, 0.92f, 0.7f, -0.43f),
        Poly(-0.22f, 0.55f, 0.7f, 0.72f),
    });

    private static float[][] Bolt() =>
        Fit(new() { Poly(0.2f, 1, -0.5f, -0.1f, 0, -0.1f, -0.3f, -1, 0.55f, 0.25f, 0.05f, 0.25f, 0.4f, 1, 0.2f, 1) });

    private static float[][] Spiral()
    {
        var s = new List<(float, float)>();
        for (int i = 0; i <= 90; i++)
        {
            float t = i / 90f, a = t * 3 * MathF.Tau, r = 0.08f + 0.92f * t;
            s.Add((r * MathF.Cos(a), r * MathF.Sin(a)));
        }
        return Fit(new() { s });
    }

    private static float[][] Flower()
    {
        var s = new List<(float, float)>();
        for (int i = 0; i <= 80; i++)
        {
            float a = MathF.PI * i / 80, r = MathF.Cos(5 * a);
            s.Add((r * MathF.Cos(a), r * MathF.Sin(a)));
        }
        return Fit(new() { s, Circle(0, 0, 0.15f, 10) });
    }

    /// <summary>Petit personnage au chapeau pointu (comme dans les dessins d'oscilloscope) ; deux poses qui alternent au rythme.</summary>
    private static float[][] Dancer(bool flip)
    {
        float f = flip ? -1 : 1;
        return Fit(new()
        {
            Circle(0, 0.35f, 0.16f, 14),
            Poly(-0.15f, 0.45f, 0.06f * f, 1, 0.15f, 0.45f),
            Poly(0, 0.19f, 0, -0.35f),
            Poly(-0.45f * f, 0.45f, 0, 0.08f, 0.42f * f, -0.12f),
            Poly(-0.3f * f, -1, 0, -0.35f, 0.22f * f, -0.68f, 0.42f * f, -0.9f),
        });
    }

    private static float[][] Cat() => Fit(new()
    {
        Arc(0, -0.15f, 0.75f, 0.65f, MathF.PI * 0.18f, MathF.PI * 0.82f + MathF.PI * 1.36f, 30),
        Poly(-0.62f, 0.2f, -0.58f, 0.95f, -0.15f, 0.48f),
        Poly(0.62f, 0.2f, 0.58f, 0.95f, 0.15f, 0.48f),
        Circle(-0.28f, -0.02f, 0.09f, 8),
        Circle(0.28f, -0.02f, 0.09f, 8),
        Poly(-0.07f, -0.25f, 0.07f, -0.25f, 0, -0.34f, -0.07f, -0.25f),
        Poly(-0.22f, -0.32f, -0.95f, -0.2f),
        Poly(-0.22f, -0.38f, -0.95f, -0.5f),
        Poly(0.22f, -0.32f, 0.95f, -0.2f),
        Poly(0.22f, -0.38f, 0.95f, -0.5f),
    });

    private static float[][] Gem() => Fit(new()
    {
        Poly(-0.5f, 0.6f, 0.5f, 0.6f, 1, 0.15f, 0, -1, -1, 0.15f, -0.5f, 0.6f),
        Poly(-1, 0.15f, 1, 0.15f),
        Poly(-0.5f, 0.6f, -0.25f, 0.15f, 0, 0.6f, 0.25f, 0.15f, 0.5f, 0.6f),
        Poly(-0.25f, 0.15f, 0, -1, 0.25f, 0.15f),
    });

    private static float[][] Infinity()
    {
        var s = new List<(float, float)>();
        for (int i = 0; i <= 60; i++)
        {
            float t = MathF.Tau * i / 60, d = 1 + MathF.Sin(t) * MathF.Sin(t);
            s.Add((MathF.Cos(t) / d, MathF.Sin(t) * MathF.Cos(t) / d));
        }
        return Fit(new() { s });
    }

    private static float[][] Planet()
    {
        var ring = new List<(float, float)>();
        float tilt = -0.35f;
        for (int i = 0; i <= 36; i++)
        {
            float t = MathF.Tau * i / 36, x = MathF.Cos(t), y = 0.28f * MathF.Sin(t);
            ring.Add((x * MathF.Cos(tilt) - y * MathF.Sin(tilt), x * MathF.Sin(tilt) + y * MathF.Cos(tilt)));
        }
        return Fit(new() { Circle(0, 0, 0.55f), ring });
    }

    /// <summary>Fantôme dont le bas ondule (deux variantes qui alternent au rythme).</summary>
    private static float[][] Ghost(int phase)
    {
        var body = Arc(0, 0.3f, 0.6f, 0.6f, 0, MathF.PI, 16);
        body.Add((-0.6f, -0.75f));
        for (int i = 1; i <= 12; i++)
        {
            float x = -0.6f + 1.2f * i / 12;
            body.Add((x, -0.75f + 0.12f * MathF.Sin(i * MathF.PI / 2 + phase * MathF.PI)));
        }
        body.Add((0.6f, 0.3f));
        return Fit(new() { body, Circle(-0.22f, 0.32f, 0.1f, 8), Circle(0.22f, 0.32f, 0.1f, 8) });
    }

    private static float[][] Fish() => Fit(new()
    {
        Arc(-0.15f, 0, 0.65f, 0.38f, 0, MathF.Tau, 28),
        Poly(0.48f, 0, 1, 0.4f, 1, -0.4f, 0.48f, 0),
        Circle(-0.5f, 0.08f, 0.07f, 8),
    });
}
