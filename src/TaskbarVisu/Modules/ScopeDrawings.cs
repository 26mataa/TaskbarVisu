using TaskbarVisu.Dsp;

namespace TaskbarVisu.Modules;

/// <summary>
/// Dessins d'oscilloscope (« oscilloscope music ») : des formes vectorielles que le faisceau trace dans le phosphore
/// de l'oscilloscope XY, en plus du son. Un nouveau dessin à chaque temps fort, une pulsation sur les coups,
/// et le son lui-même fait trembler le trait. Le rendu (couleurs, rémanence) est celui de l'oscilloscope.
/// </summary>
internal sealed class ScopeDrawer
{
    /// <summary>Un dessin : un ou plusieurs tracés (x, y entre -1 et 1, y vers le haut) ; plusieurs variantes = animation au rythme.</summary>
    private sealed record Shape(float[][][] Variants);

    private static readonly Shape[] Shapes = BuildShapes();

    /// <summary>Fondu « fumée » entre deux dessins, et durée de la boule hérissée des gros coups (secondes).</summary>
    private const double Dissolve = 0.12, Burst = 0.4;

    private readonly Random _random = new();
    private readonly List<int> _bag = new();
    private int _shape = -1, _previous = -1, _beat;
    private bool _single;
    private double _clock, _lastOnset = -1, _lastChange = -10, _burstAt = -10;
    private float _slow, _pulse, _visible, _twin = 1;

    /// <summary>Rien à tracer (silence, ou dessin éteint).</summary>
    public bool Idle => _visible < 0.01f && _clock - _burstAt > Burst;

    /// <summary>
    /// Suit le rythme puis trace le dessin courant dans <paramref name="phosphor"/> (largeur <paramref name="width"/>),
    /// dans la zone du tracé (<paramref name="x0"/>, <paramref name="w"/>). <paramref name="deposit"/> : énergie déposée
    /// par pixel de trait, calculée pour atteindre la luminosité voulue malgré la rémanence.
    /// </summary>
    public void Update(AudioAnalyzer audio, double dt, bool continuous, bool twins, float wobble, bool spin, float gain,
        float[] phosphor, int width, int height, int x0, int w, float deposit)
    {
        _clock += dt;
        float energy = (audio.RmsL * audio.RmsL + audio.RmsR * audio.RmsR) / 2;
        float ratio = energy / Math.Max(_slow, 1e-9f);
        _slow += (energy - _slow) * (float)(1 - Math.Exp(-dt / 0.6));
        bool sound = energy > 3e-6f; // ≈ -55 dB
        bool onset = sound && ratio > 1.8f && _clock - _lastOnset > 0.18;
        if (onset)
        {
            _lastOnset = _clock;
            _beat++;
            _pulse = 1;
            // Un dessin reste au moins 0,45 s : le temps de le reconnaître.
            if (_shape < 0 || _clock - _lastChange > 0.45)
            {
                // Très gros coup, pas trop souvent : boule hérissée (le bruit d'une caisse claire à l'oscilloscope).
                if (ratio > 3.2f && _clock - _burstAt > 2.5) _burstAt = _clock;
                Change(twins);
            }
        }
        else if (continuous && sound && (_shape < 0 || _clock - _lastChange > 4))
        {
            Change(twins);
        }

        _pulse *= (float)Math.Exp(-dt / 0.15);
        // Sur les temps forts : le dessin s'allume au coup puis s'efface ; en continu : tant qu'il y a du son.
        float target = continuous ? (sound ? 1 : 0) : (onset ? 1 : 0);
        _visible = target > _visible ? target : _visible * (float)Math.Exp(-dt / (continuous ? 0.25 : 0.9));
        // Les deux copies glissent l'une vers l'autre quand le dessin passe seul, et se séparent au suivant.
        _twin += ((_single ? 0 : 1) - _twin) * (float)(1 - Math.Exp(-dt / 0.06));
        if (_shape < 0 || Idle) return;

        Span<(float X, float Y, float R)> cells = stackalloc (float, float, float)[2];
        int count = Layout(x0, w, height, twins, cells, out float apart);
        float mix = (float)Math.Clamp((_clock - _lastChange) / Dissolve, 0, 1);
        float burst = (float)Math.Clamp(1 - (_clock - _burstAt) / Burst, 0, 1);
        burst *= burst;
        float scale = 0.86f + 0.14f * _pulse;
        float angle = spin ? (float)(_clock * 0.6) : 0.08f * MathF.Sin((float)_clock * 1.7f);
        // Deux copies confondues valent une seule : pas de saut de luminosité quand elles fusionnent.
        float amount = deposit * _visible * (count == 2 ? 0.5f + 0.5f * apart : 1);
        foreach (var cell in cells[..count])
        {
            var pen = new Pen(cell, scale, angle, wobble, gain, audio, phosphor, width, height);
            Trace(Variant(_shape), pen, 1 - mix, amount * mix * (1 - burst));
            if (mix < 1 && _previous >= 0) Trace(Variant(_previous), pen, mix, amount * (1 - mix) * (1 - burst));
            if (burst > 0) Spikes(pen, burst, deposit);
        }
    }

    private float[][] Variant(int shape) => Shapes[shape].Variants[_beat % Shapes[shape].Variants.Length];

    private void Change(bool twins)
    {
        _previous = _shape;
        _shape = Next();
        _lastChange = _clock;
        // De temps en temps un dessin passe seul, plus grand, au centre (zone carrée seulement, voir Layout).
        _single = !twins || _random.Next(3) == 0;
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

    /// <summary>
    /// Une copie au centre, ou deux (<paramref name="apart"/> : 0 = confondues, 1 = séparées). Zone large : côte à côte,
    /// de part et d'autre du tracé du son, sans jamais fusionner devant lui. Zone carrée : en diagonale, qui se chevauchent
    /// (comme les dessins d'oscilloscope en stéréo), et qui glissent l'une vers l'autre quand un dessin passe seul.
    /// </summary>
    private int Layout(int x0, int w, int height, bool twins, Span<(float X, float Y, float R)> cells, out float apart)
    {
        float cy = (height - 1) / 2f, cx = x0 + (w - 1) / 2f;
        (float X, float Y, float R) single = (cx, cy, Math.Min(w, height) / 2f * 0.9f), a, b;
        if (w >= 1.7f * height)
        {
            float s = Math.Min(height, w / 2f), r = s / 2f * 0.9f;
            float d = Math.Min(Math.Max(s * 0.6f, Math.Min(w / 4f, s * 1.4f)), w / 2f - r);
            a = (cx - d, cy, r);
            b = (cx + d, cy, r);
            apart = twins ? 1 : 0;
        }
        else
        {
            float s = Math.Min(w, height), r = s / 2f * 0.66f;
            a = (cx - 0.15f * s, cy + 0.11f * s, r);
            b = (cx + 0.15f * s, cy - 0.11f * s, r);
            apart = _twin;
        }
        if (apart < 0.02f)
        {
            cells[0] = single;
            return 1;
        }
        cells[0] = Lerp(single, a, apart);
        cells[1] = Lerp(single, b, apart);
        return 2;
    }

    private static (float, float, float) Lerp((float X, float Y, float R) p, (float X, float Y, float R) q, float t) =>
        (p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t, p.R + (q.R - p.R) * t);

    /// <summary>Où et comment le faisceau trace une copie.</summary>
    private readonly record struct Pen((float X, float Y, float R) Cell, float Scale, float Angle, float Wobble, float Gain,
        AudioAnalyzer Audio, float[] Phosphor, int Width, int Height);

    /// <summary>
    /// Trace un dessin. <paramref name="smoke"/> (0 à 1) brouille chaque sommet au hasard : le dessin part en fumée
    /// pendant que le suivant en sort.
    /// </summary>
    private void Trace(float[][] strokes, in Pen pen, float smoke, float amount)
    {
        if (amount <= 0) return;
        float r = pen.Cell.R * pen.Scale, cos = MathF.Cos(pen.Angle), sin = MathF.Sin(pen.Angle);
        float jitter = pen.Wobble * 0.3f * pen.Cell.R, scatter = smoke * smoke * 0.45f * pen.Cell.R;
        var audio = pen.Audio;
        int n = Math.Max(1, audio.Count), sample = 0;
        foreach (var stroke in strokes)
        {
            float px = 0, py = 0;
            for (int i = 0; i < stroke.Length / 2; i++)
            {
                float x = stroke[2 * i], y = stroke[2 * i + 1];
                // Le son fait trembler chaque sommet : un dessin « joué » plutôt que posé.
                int k = (sample += 7) % n;
                float jx = audio.Count > 0 ? Math.Clamp(audio.Left[k] * pen.Gain, -1, 1) * jitter : 0;
                float jy = audio.Count > 0 ? Math.Clamp(audio.Right[k] * pen.Gain, -1, 1) * jitter : 0;
                if (scatter > 0)
                {
                    jx += (float)(_random.NextDouble() * 2 - 1) * scatter;
                    jy += (float)(_random.NextDouble() * 2 - 1) * scatter;
                }
                float qx = pen.Cell.X + (x * cos - y * sin) * r + jx;
                float qy = pen.Cell.Y - (x * sin + y * cos) * r + jy;
                // Le faisceau ralentit aux sommets : ils brillent un peu plus.
                Splat(pen.Phosphor, pen.Width, pen.Height, qx, qy, amount * 1.5f);
                if (i > 0) Line(pen.Phosphor, pen.Width, pen.Height, px, py, qx, qy, amount);
                px = qx;
                py = qy;
            }
        }
    }

    /// <summary>Boule hérissée : des pointes au hasard, redessinées à chaque image, qui gonflent au coup puis se résorbent.</summary>
    private void Spikes(in Pen pen, float level, float deposit)
    {
        float r = pen.Cell.R * (0.7f + 0.4f * level), amount = deposit * 0.4f * level;
        for (int i = 0; i < 48; i++)
        {
            float a = (float)(_random.NextDouble() * MathF.Tau), length = r * (0.3f + 0.7f * (float)_random.NextDouble());
            float ca = MathF.Cos(a), sa = MathF.Sin(a), px = pen.Cell.X, py = pen.Cell.Y;
            for (int s = 1; s <= 3; s++)
            {
                float d = length * s / 3, side = (float)(_random.NextDouble() - 0.5) * 0.25f * r;
                float qx = pen.Cell.X + ca * d - sa * side, qy = pen.Cell.Y + sa * d + ca * side;
                Line(pen.Phosphor, pen.Width, pen.Height, px, py, qx, qy, amount);
                px = qx;
                py = qy;
            }
        }
    }

    private static void Line(float[] phosphor, int width, int height, float x0, float y0, float x1, float y1, float amount)
    {
        float dx = x1 - x0, dy = y1 - y0;
        int steps = (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dy * dy) / 0.5f);
        float a = amount * 0.5f;
        for (int s = 1; s <= steps; s++)
        {
            float t = s / (float)steps;
            Splat(phosphor, width, height, x0 + dx * t, y0 + dy * t, a);
        }
    }

    /// <summary>Dépôt réparti sur les 4 pixels voisins : un trait fin mais lisse, même à 40 px de haut.</summary>
    private static void Splat(float[] phosphor, int width, int height, float x, float y, float amount)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        Add(phosphor, width, height, ix, iy, amount * (1 - fx) * (1 - fy));
        Add(phosphor, width, height, ix + 1, iy, amount * fx * (1 - fy));
        Add(phosphor, width, height, ix, iy + 1, amount * (1 - fx) * fy);
        Add(phosphor, width, height, ix + 1, iy + 1, amount * fx * fy);
    }

    private static void Add(float[] phosphor, int width, int height, int x, int y, float amount)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return;
        ref float cell = ref phosphor[y * width + x];
        cell = Math.Min(1.5f, cell + amount);
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
