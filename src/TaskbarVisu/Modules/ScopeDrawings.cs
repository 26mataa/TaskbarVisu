using TaskbarVisu.Dsp;

namespace TaskbarVisu.Modules;

/// <summary>
/// Dessins d'oscilloscope (« oscilloscope music ») : par moments, le faisceau quitte le son et écrit d'un trait un dessin
/// (étoile, cœur, personnage…) au cœur du tracé, puis le dessin redevient le son. Il suit la stéréo comme le nuage du
/// goniomètre : il glisse et penche du côté le plus fort, s'affine en mono, s'élargit en stéréo large.
/// Feux d'artifice : plus la stéréo est intense, plus le dessin se déforme et envahit le module ; un coup de stéréo le fait
/// exploser en étincelles (qui refroidissent, se divisent en vol, avec une onde de choc) ou fait éclore une nouvelle forme.
/// Tout est tracé par les échantillons du son eux-mêmes : <see cref="Apply"/> déplace chacun d'eux.
/// </summary>
internal sealed class ScopeDrawer
{
    /// <summary>Un dessin : un ou plusieurs tracés (x, y entre -1 et 1, y vers le haut) ; plusieurs variantes = animation au rythme.</summary>
    private sealed record Shape(float[][][] Variants);

    /// <summary>Un tracé prêt à parcourir : ses segments bout à bout (le faisceau saute d'un trait à l'autre sans tracer).</summary>
    private sealed class Path
    {
        public readonly float[] X, Y, DX, DY, Length, Start;
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
            Start = new float[Length.Length];
            for (int i = 1; i < Start.Length; i++) Start[i] = Start[i - 1] + Length[i - 1];
            Total = Length.Sum();
        }

        /// <summary>Point situé à la fraction <paramref name="u"/> de la longueur du tracé (en boucle).</summary>
        public (float X, float Y) At(float u)
        {
            float d = (u - MathF.Floor(u)) * Total;
            int j = Array.BinarySearch(Start, d);
            if (j < 0) j = Math.Max(0, ~j - 1);
            float t = Math.Clamp((d - Start[j]) / Length[j], 0, 1);
            return (X[j] + DX[j] * t, Y[j] + DY[j] * t);
        }
    }

    private enum Phase { Idle, Write, Bloom, Hold, Morph, Out, Burst }

    /// <summary>Onde de choc : un anneau qui s'élargit en s'éteignant.</summary>
    private struct Ring
    {
        public float X, Y, MaxX, MaxY, Strength;
        public double Start, Life;
    }

    private static readonly Shape[] Shapes = BuildShapes();
    private static readonly Path[][] Paths = Shapes.Select(s => s.Variants.Select(v => new Path(v)).ToArray()).ToArray();

    /// <summary>Durées (secondes) : écriture d'un trait, retour au son, passage d'un dessin à l'autre, éclosion.</summary>
    private const double WriteTime = 0.12, OutTime = 0.22, MorphTime = 0.3, BloomTime = 0.4;
    /// <summary>Étincelles d'une explosion ou d'une éclosion (chacune tracée par plusieurs échantillons, en traînée).</summary>
    private const int Sparks = 80;
    /// <summary>Lueur déposée autour des points du dessin et des étincelles.</summary>
    private const float Glow = 0.02f;

    private readonly Random _random = new();
    private readonly List<int> _bag = new();
    private Phase _phase;
    private int _shape = -1, _beat, _chain;
    private Path? _from;
    private double _clock, _t0, _holdEnd, _burstEnd, _nextAllowed = 1, _idleSince, _shownSince, _yaw;
    private double _lastOnset = -1, _lastHit = -10, _lastSound = -10, _lastRing = -10;
    private float _slow, _slowSide, _balance, _width, _intensity, _pulse, _shakeX, _shakeY, _boost;
    // Mise en place de l'image : centre, rayons, inclinaison, largeur, rotation 3D, déformation.
    private float _cx, _cy, _rx, _ry, _cos = 1, _sin, _aspect = 1, _turn = 1, _drift, _half, _unit = 1, _w, _h;
    private readonly float[] _ox = new float[Sparks], _oy = new float[Sparks], _vx = new float[Sparks], _vy = new float[Sparks];
    private readonly float[] _life = new float[Sparks], _pop = new float[Sparks], _popAngle = new float[Sparks], _popSpeed = new float[Sparks];
    private readonly float[] _bx = new float[Sparks], _by = new float[Sparks];
    private readonly Ring[] _rings = new Ring[4];
    private int _ringCount;

    /// <summary>Un dessin, une explosion ou une onde de choc est en cours.</summary>
    public bool Active => _phase != Phase.Idle || _ringCount > 0;

    /// <summary>Rémanence en plus pendant une explosion (multiplie la rémanence du module par 1 + cette valeur).</summary>
    public float Afterglow => 0.5f * _boost;

    private Path Current => Paths[_shape][_beat % Paths[_shape].Length];

    /// <summary>
    /// Fait vivre les dessins et déplace les échantillons de l'image : <paramref name="fx"/>, <paramref name="fy"/> arrivent
    /// à leur place dans le nuage du son et repartent à leur place sur le dessin ; <paramref name="weight"/> (1 au départ)
    /// dose leur éclat (0 = ne rien tracer), <paramref name="glow"/> (0 au départ) la lueur autour.
    /// <paramref name="sx"/>, <paramref name="sy"/> : écart de chaque échantillon dans le nuage (-1..1), qui fait trembler le dessin.
    /// </summary>
    public void Apply(AudioAnalyzer audio, double dt, bool often, bool fireworks, float wobble,
        ReadOnlySpan<float> sx, ReadOnlySpan<float> sy, Span<float> fx, Span<float> fy, Span<float> weight, Span<float> glow,
        int x0, int w, int height)
    {
        _clock += dt;
        int n = audio.Count;

        // Écoute : balance et largeur stéréo (lissées), coups (énergie totale) et coups de stéréo (énergie des côtés).
        float eL = 0, eR = 0, eM = 0, eS = 0;
        for (int i = 0; i < n; i++)
        {
            float l = audio.Left[i], r = audio.Right[i], m = (l + r) * 0.5f, s = (r - l) * 0.5f;
            eL += l * l;
            eR += r * r;
            eM += m * m;
            eS += s * s;
        }
        float follow = (float)(1 - Math.Exp(-dt / 0.12)), settle = (float)(1 - Math.Exp(-dt / 0.6));
        float wide = eS / (eM + eS + 1e-12f);
        _balance += ((eR - eL) / (eR + eL + 1e-12f) - _balance) * follow;
        _width += (wide - _width) * follow;
        float intensity = _intensity = fireworks ? Smooth((_width - 0.15) / 0.35) : 0;
        float energy = n > 0 ? (eL + eR) / (2 * n) : 0, side = n > 0 ? eS / n : 0;
        float ratio = energy / Math.Max(_slow, 1e-9f), sideRatio = side / Math.Max(_slowSide, 1e-9f);
        _slow += (energy - _slow) * settle;
        _slowSide += (side - _slowSide) * settle;
        bool sound = energy > 3e-6f; // ≈ -55 dB
        if (sound) _lastSound = _clock;
        bool onset = sound && ratio > 1.8f && _clock - _lastOnset > 0.18;
        bool hit = fireworks && sound && side > 2e-6f && sideRatio > 2.2f && wide > 0.22f && _clock - _lastHit > 1.2;

        _pulse *= (float)Math.Exp(-dt / 0.15);
        _boost *= (float)Math.Exp(-dt / 0.8);
        float settleShake = (float)Math.Exp(-dt / 0.1);
        _shakeX *= settleShake;
        _shakeY *= settleShake;
        _unit = height / 40f;
        if (onset)
        {
            _lastOnset = _clock;
            _beat++;
            _pulse = 1;
            _shakeX += Rand() * 3 * _unit * intensity;
            _shakeY += Rand() * 1.5f * _unit * intensity;
        }

        Layout(x0, w, height, dt);
        if (onset && _phase is Phase.Hold or Phase.Morph && intensity > 0.55f && _clock - _lastRing > 0.9)
        {
            _lastRing = _clock;
            AddRing(_rx * 1.15f + intensity * w * 0.22f, _ry * 1.25f + intensity * 6 * _unit, 0.3, 0.3f + 0.5f * intensity);
        }
        if (hit)
        {
            _lastHit = _clock;
            float power = Math.Clamp((sideRatio - 1) / 3, 0.6f, 1.5f);
            // Le dessin n'explose qu'une fois bien vu ; sans dessin, le coup en fait éclore un.
            if (_phase is Phase.Hold or Phase.Morph && _clock - _shownSince > 0.6) Burst(power);
            else if (_phase == Phase.Idle && _clock - _idleSince > 0.3) Bloom();
        }
        Advance(often, sound, onset, intensity);

        for (int i = 0; i < _ringCount; i++)
        {
            if (_clock - _rings[i].Start < _rings[i].Life) continue;
            _rings[i--] = _rings[--_ringCount];
        }
        if (Active && _shape >= 0) Plot(n, wobble, sx, sy, fx, fy, weight, glow);
    }

    /// <summary>Place le dessin : au centre, glissé et penché du côté le plus fort, plus ou moins large selon la stéréo.</summary>
    private void Layout(int x0, int w, int height, double dt)
    {
        float intensity = _intensity;
        _w = w;
        _h = height;
        _half = Math.Min(w, height) / 2f * 0.86f;
        float r = _half * (1 + (0.12f + 0.3f * intensity) * _pulse);
        _drift = Math.Clamp(_balance * 1.8f, -1, 1);
        _cx = x0 + (w - 1) / 2f + _drift * Math.Max(0, w / 2f - _half * 1.25f) * (1 - intensity) + _shakeX;
        _cy = (height - 1) / 2f + _shakeY;
        // Stéréo intense : le dessin grandit et s'élargit (jusqu'à deux fois plus large).
        _rx = r * (1 + intensity * Math.Clamp(w / (2.1f * r) - 1, 0, 1.2f));
        _ry = r * (1 + 0.18f * intensity);
        float lean = -_drift * 0.45f * (1 - 0.5f * intensity);
        _cos = MathF.Cos(lean);
        _sin = MathF.Sin(lean);
        _aspect = 0.8f + 0.35f * Math.Min(1, _width * 2.5f) * (1 - intensity);
        // Légère rotation 3D (le dessin tourne comme une pièce), plus vive quand la stéréo s'emballe.
        _yaw += dt * (1.1 + 3.5 * intensity);
        _turn = MathF.Cos((0.7f + 0.6f * intensity) * MathF.Sin((float)_yaw));
    }

    /// <summary>Enchaîne les phases : apparition (au hasard, souvent sur un temps fort), tenue, passage à un autre dessin, retour au son.</summary>
    private void Advance(bool often, bool sound, bool onset, float intensity)
    {
        double age = _clock - _t0;
        switch (_phase)
        {
            case Phase.Idle:
                if (sound && intensity > 0.5f && _clock - _idleSince > 0.35)
                {
                    Bloom();
                }
                else if (sound && _clock >= _nextAllowed
                         && ((onset && _random.NextDouble() < (often ? 0.8 : 0.5)) || _clock >= _nextAllowed + (often ? 1.0 : 2.5)))
                {
                    _shape = Next();
                    _chain = 0;
                    Enter(Phase.Write);
                }
                break;
            case Phase.Write when age >= WriteTime:
            case Phase.Bloom when age >= BloomTime * 1.4:
                Hold((often ? Between(1.4, 2.6) : Between(1, 2)) * (1 - 0.45 * intensity));
                break;
            case Phase.Hold when intensity > 0.6f && _clock >= _holdEnd:
                Burst(0.8f + 0.7f * intensity);
                break;
            case Phase.Hold when _clock - _lastSound > 0.4 || _clock >= _holdEnd:
                if (often && _chain < 2 && _clock - _lastSound <= 0.4 && _random.NextDouble() < 0.6)
                {
                    _from = Current;
                    _shape = Next();
                    _chain++;
                    Enter(Phase.Morph);
                }
                else
                {
                    Enter(Phase.Out);
                }
                break;
            case Phase.Morph when age >= MorphTime:
                Hold(Between(1, 2) * (1 - 0.45 * intensity));
                break;
            case Phase.Out when age >= OutTime:
                Rest(often ? Between(0.4, 1.6) : Between(1.5, 5));
                break;
            case Phase.Burst when _clock >= _burstEnd - 0.3 * intensity:
                if (sound && intensity > 0.35f) Bloom();
                else if (_clock >= _burstEnd) Rest(often ? Between(0.3, 1.1) : Between(1, 3));
                break;
        }
    }

    private void Enter(Phase phase)
    {
        _phase = phase;
        _t0 = _clock;
    }

    private void Hold(double seconds)
    {
        if (_phase != Phase.Morph) _shownSince = _clock; // le dessin vient d'apparaître (écrit ou éclos)
        Enter(Phase.Hold);
        _holdEnd = _clock + seconds;
    }

    private void Rest(double seconds)
    {
        Enter(Phase.Idle);
        _idleSince = _clock;
        _nextAllowed = _clock + seconds;
    }

    /// <summary>Feu d'artifice : les étincelles jaillissent du centre en tourbillon et se figent en dessin.</summary>
    private void Bloom()
    {
        _shape = Next();
        _chain = 0;
        var path = Current;
        for (int j = 0; j < Sparks; j++) (_bx[j], _by[j]) = path.At(j / (float)Sparks);
        Enter(Phase.Bloom);
        AddRing(_w * 0.3f + _intensity * _w * 0.25f, _h * 0.5f, 0.35, 0.9f);
    }

    /// <summary>Explosion : le contour vole en étincelles, poussées du côté le plus fort, avec une onde de choc.</summary>
    private void Burst(float power)
    {
        float intensity = _intensity, longest = 0;
        power *= 1 + 0.8f * intensity;
        var path = Current;
        for (int j = 0; j < Sparks; j++)
        {
            var (ux, uy) = path.At(j / (float)Sparks);
            var (px, py) = Place(ux, uy);
            _ox[j] = px;
            _oy[j] = py;
            float dx = (px - _cx) / _rx + Rand() * 0.5f, dy = (py - _cy) / _ry + Rand() * 0.5f;
            float speed = (0.5f + _random.NextSingle() * 0.9f) * power;
            _vx[j] = dx * speed * _w * 0.6f * (0.6f + _width * 1.6f) + _drift * 60 * _unit;
            _vy[j] = dy * speed * _h * 2 - 12 * _unit;
            _life[j] = (0.45f + _random.NextSingle() * 0.6f) * (0.8f + 0.3f * power);
            // Une étincelle sur trois ou quatre éclate encore en vol, en trois petites gerbes.
            _pop[j] = float.MaxValue;
            if (_random.NextSingle() < 0.4f)
            {
                _pop[j] = _life[j] * (0.3f + _random.NextSingle() * 0.25f);
                _popAngle[j] = _random.NextSingle() * MathF.Tau;
                _popSpeed[j] = (10 + _random.NextSingle() * 14) * (0.8f + 0.5f * intensity) * _unit;
                _life[j] = Math.Max(_life[j], _pop[j] + 0.5f);
            }
            longest = Math.Max(longest, _life[j]);
        }
        Enter(Phase.Burst);
        _burstEnd = _clock + longest + 0.1;
        _pulse = 1;
        _boost = 1;
        _shakeX = Rand() * 6 * _unit;
        _shakeY = Rand() * 3 * _unit;
        AddRing(_w * 0.62f, _h * 0.75f, 0.5, 1.6f);
    }

    private void AddRing(float maxX, float maxY, double life, float strength)
    {
        if (_ringCount == _rings.Length)
        {
            Array.Copy(_rings, 1, _rings, 0, _rings.Length - 1);
            _ringCount--;
        }
        _rings[_ringCount++] = new Ring { X = _cx, Y = _cy, MaxX = maxX, MaxY = maxY, Strength = strength, Start = _clock, Life = life };
    }

    /// <summary>Position à l'écran d'un point du dessin (coordonnées -1..1, y vers le haut).</summary>
    private (float X, float Y) Place(float ux, float uy)
    {
        float intensity = _intensity;
        if (intensity > 0)
        {
            // Stéréo intense : le dessin ondule comme un liquide.
            float t = (float)_clock;
            ux += MathF.Sin(uy * 3.1f + t * 7) * 0.18f * intensity;
            uy += MathF.Sin(ux * 2.7f - t * 5.3f) * 0.14f * intensity;
        }
        ux *= _aspect * _turn;
        return (_cx + (ux * _cos - uy * _sin) * _rx, _cy - (ux * _sin + uy * _cos) * _ry);
    }

    private void Plot(int n, float wobble, ReadOnlySpan<float> sx, ReadOnlySpan<float> sy, Span<float> fx, Span<float> fy,
        Span<float> weight, Span<float> glow)
    {
        float intensity = _intensity, age = (float)(_clock - _t0);
        float jitter = wobble * 0.22f * _half * (1 + 0.8f * _width) + 1.5f * _unit * intensity;
        int per = (n + Sparks - 1) / Sparks;
        float m = _phase switch
        {
            Phase.Idle => 0,
            Phase.Out => 1 - Smooth(age / OutTime),
            _ => 1,
        };
        float write = Smooth(age / WriteTime), morph = Smooth(age / MorphTime), offset = _random.NextSingle();
        var path = Current;
        for (int i = 0; i < n; i++)
        {
            // Une partie du faisceau trace les ondes de choc.
            if (_ringCount > 0 && i % 8 == 0)
            {
                ref var g = ref _rings[i / 8 % _ringCount];
                float p = (float)((_clock - g.Start) / g.Life), e = EaseOut(p), a = _random.NextSingle() * MathF.Tau;
                fx[i] = g.X + MathF.Cos(a) * g.MaxX * e;
                fy[i] = g.Y + MathF.Sin(a) * g.MaxY * e;
                weight[i] = (1 - p) * g.Strength * 1.2f;
                glow[i] = Glow;
                continue;
            }

            int j = i % Sparks, k = i / Sparks;
            float trail = k / (float)per;
            switch (_phase)
            {
                case Phase.Burst:
                {
                    float t = age - 0.05f * trail;
                    if (t < 0 || t > _life[j]) break; // étincelle éteinte : l'échantillon retourne au son
                    float e = 1 - MathF.Exp(-t / 0.3f), x = _ox[j] + _vx[j] * 0.3f * e, y = _oy[j] + _vy[j] * 0.3f * e + 22 * _unit * t * t;
                    float heat;
                    if (t > _pop[j])
                    {
                        float a = _popAngle[j] + k % 3 * 2.094f, e2 = 1 - MathF.Exp(-(t - _pop[j]) / 0.18f);
                        x += MathF.Cos(a) * _popSpeed[j] * e2 * 2.2f;
                        y += MathF.Sin(a) * _popSpeed[j] * e2;
                        heat = 1 - (t - _pop[j]) / (_life[j] - _pop[j]);
                    }
                    else
                    {
                        heat = 1 - t / _life[j];
                    }
                    // Les étincelles naissent blanches et refroidissent (la palette descend), en scintillant à la fin.
                    fx[i] = x;
                    fy[i] = y;
                    weight[i] = heat < 0.35f && _random.NextSingle() < 0.4f ? 0 : (0.2f + 1.5f * MathF.Pow(Math.Max(0, heat), 1.5f)) * (1 - 0.7f * trail);
                    glow[i] = Glow;
                    break;
                }
                case Phase.Bloom:
                {
                    float p = EaseOut(age / BloomTime - 0.35f * trail), a = (1 - p) * 3, c = MathF.Cos(a), s = MathF.Sin(a);
                    var (qx, qy) = Place(_bx[j] * c - _by[j] * s, _bx[j] * s + _by[j] * c);
                    fx[i] = _cx + (qx - _cx) * p + sx[i] * jitter * 0.5f;
                    fy[i] = _cy + (qy - _cy) * p - sy[i] * jitter * 0.5f;
                    weight[i] = _random.NextSingle() < 0.25f ? 0 : (0.5f + 1.6f * (1 - p)) * (1 - 0.5f * trail) + 0.4f;
                    glow[i] = Glow;
                    break;
                }
                case Phase.Write or Phase.Hold or Phase.Morph or Phase.Out:
                {
                    // Écriture : le faisceau parcourt le dessin du début jusqu'à la plume ; ensuite il le parcourt
                    // tout entier à chaque image, à partir d'un point au hasard.
                    float u = _phase == Phase.Write ? write * i / n * 0.9999f : offset + i / (float)n;
                    var (ux, uy) = path.At(u);
                    if (_phase == Phase.Morph && _from is { } from)
                    {
                        var (ax, ay) = from.At(u);
                        ux = ax + (ux - ax) * morph;
                        uy = ay + (uy - ay) * morph;
                    }
                    var (tx, ty) = Place(ux, uy);
                    fx[i] += (tx + sx[i] * jitter - fx[i]) * m;
                    fy[i] += (ty - sy[i] * jitter - fy[i]) * m;
                    if (m > 0.5f) glow[i] = Glow;
                    break;
                }
            }
        }
    }

    private double Between(double min, double max) => min + _random.NextDouble() * (max - min);

    /// <summary>Nombre au hasard entre -1 et 1.</summary>
    private float Rand() => _random.NextSingle() * 2 - 1;

    private static float Smooth(double t)
    {
        float x = (float)Math.Clamp(t, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private static float EaseOut(double t)
    {
        float x = 1 - (float)Math.Clamp(t, 0, 1);
        return 1 - x * x * x;
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
