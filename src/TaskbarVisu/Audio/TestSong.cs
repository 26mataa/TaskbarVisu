namespace TaskbarVisu.Audio;

/// <summary>
/// Diagnostic : un accord tenu, rejoué toutes les 2 s, au timbre de piano (harmoniques décroissantes,
/// légère inharmonicité) ou de synthé en dents de scie. Formats :
/// "chord:C:MIN:5[:i1|:i2][:b][:saw]" (fondamentale, qualité, octave, renversement, basse ajoutée deux octaves
/// plus bas, timbre) ou "notes:C2,C4,D#4,G4[:saw]" (notes libres). Octaves scientifiques : Do4 = 261,6 Hz
/// (FL Studio numérote une octave plus haut : son C5 est ce Do4).
/// </summary>
internal sealed class TestChord
{
    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly Dictionary<string, int[]> Qualities = new()
    {
        ["MAJ"] = new[] { 0, 4, 7 }, ["MIN"] = new[] { 0, 3, 7 }, ["7"] = new[] { 0, 4, 7, 10 },
        ["MAJ7"] = new[] { 0, 4, 7, 11 }, ["MIN7"] = new[] { 0, 3, 7, 10 }, ["SUS2"] = new[] { 0, 2, 7 },
        ["SUS4"] = new[] { 0, 5, 7 }, ["DIM"] = new[] { 0, 3, 6 }, ["AUG"] = new[] { 0, 4, 8 }, ["5"] = new[] { 0, 7 },
    };

    private readonly double[] _freqs;
    private readonly bool _saw;

    private TestChord(int[] midi, bool saw)
    {
        Midi = midi.Order().ToArray();
        _freqs = midi.Select(m => 440 * Math.Pow(2, (m - 69) / 12.0)).ToArray();
        _saw = saw;
    }

    /// <summary>Notes jouées (numéros MIDI, du grave à l'aigu).</summary>
    public int[] Midi { get; }

    public static TestChord? Parse(string spec)
    {
        var parts = spec.ToUpperInvariant().Replace("♭", "B").Split(':');
        if (parts.Length >= 2 && parts[0] == "NOTES")
        {
            var midi = parts[1].Split(',').Select(ParseNote).ToArray();
            return midi.Any(m => m < 0) ? null : new TestChord(midi, parts.Skip(2).Contains("SAW"));
        }
        if (parts.Length < 4 || parts[0] != "CHORD") return null;
        int root = Array.IndexOf(Names, parts[1]);
        if (root < 0 || !Qualities.TryGetValue(parts[2], out var intervals) || !int.TryParse(parts[3], out int octave)) return null;

        var notes = intervals.Select(i => 12 * (octave + 1) + root + i).ToList();
        var options = parts.Skip(4).ToHashSet();
        if (options.Contains("I1")) notes[0] += 12;
        if (options.Contains("I2")) { notes[0] += 12; notes[1] += 12; }
        if (options.Contains("B")) notes.Add(12 * (octave + 1) + root - 24);
        return new TestChord(notes.ToArray(), options.Contains("SAW"));
    }

    /// <summary>"D#4" → 63 ; -1 si illisible.</summary>
    public static int ParseNote(string note)
    {
        note = note.Trim().ToUpperInvariant();
        int split = note.Length > 1 && note[1] == '#' ? 2 : 1;
        int pc = Array.IndexOf(Names, note[..Math.Min(split, note.Length)]);
        return pc >= 0 && int.TryParse(note[split..], out int octave) ? 12 * (octave + 1) + pc : -1;
    }

    public static string NoteName(int midi) => Names[midi % 12] + (midi / 12 - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public (float L, float R) Sample(double t)
    {
        double inNote = t % 2.0;
        double sum = 0;
        foreach (var f in _freqs)
        {
            for (int h = 1; h <= 10; h++)
            {
                double fh = h * f * Math.Sqrt(1 + 0.0003 * h * h);
                if (fh > 20000) break;
                double amp = _saw ? 1.0 / h : 1.0 / Math.Pow(h, 1.2);
                double env = _saw ? 1 : Math.Exp(-inNote * (0.8 + 0.35 * h));
                sum += Math.Sin(2 * Math.PI * fh * t + h * 0.7) * amp * env;
            }
        }
        double attack = 1 - Math.Exp(-inNote * 300);
        float v = (float)(sum * 0.08 * attack);
        return (v, v);
    }
}

/// <summary>
/// Diagnostic : sons « non musicaux » qui ne doivent pas faire afficher de tonalité.
/// "voice:discord" (phrases parlées entrecoupées de longs silences), "voice:talk" (bavardage continu),
/// "voice:cough" (toux), "voice:ding" (son de notification court). La voix est synthétisée : impulsions
/// glottiques à hauteur glissante, filtrées par les formants d'une voyelle tirée au hasard à chaque syllabe.
/// Les échantillons doivent être demandés dans l'ordre (la phase de la voix est cumulée).
/// </summary>
internal sealed class TestSounds
{
    private const int Rate = 48000;
    private readonly string _kind;
    private readonly List<(double Start, double End, double F0, double Glide, double F1, double F2)> _syllables = new();
    private readonly Random _noise = new(11);
    private double _phase, _lastT = -1, _lowpass;
    private int _cursor;

    private TestSounds(string kind)
    {
        _kind = kind;
        if (kind is not ("DISCORD" or "TALK")) return;

        // Partition de la parole : phrases de syllabes, séparées par des silences (longs sur Discord).
        var rng = new Random(5);
        double t = 0.5;
        while (t < 120)
        {
            double baseF0 = 100 + rng.NextDouble() * 110;
            int count = 4 + rng.Next(8);
            for (int i = 0; i < count; i++)
            {
                double length = 0.12 + rng.NextDouble() * 0.18;
                double f1 = 300 + rng.NextDouble() * 500, f2 = 900 + rng.NextDouble() * 1400;
                _syllables.Add((t, t + length, baseF0 * (0.85 + rng.NextDouble() * 0.35), -0.25 + rng.NextDouble() * 0.45, f1, f2));
                t += length + 0.03 + rng.NextDouble() * 0.08;
            }
            t += kind == "DISCORD" ? 1 + rng.NextDouble() * 4 : 0.15 + rng.NextDouble() * 0.4;
        }
    }

    public static TestSounds? Parse(string spec)
    {
        var parts = spec.ToUpperInvariant().Split(':');
        return parts.Length == 2 && parts[0] == "VOICE" && parts[1] is "DISCORD" or "TALK" or "COUGH" or "DING"
            ? new TestSounds(parts[1]) : null;
    }

    public (float L, float R) Sample(double t)
    {
        double v = _kind switch
        {
            "COUGH" => Cough(t),
            "DING" => Ding(t),
            _ => Voice(t),
        };
        _lastT = t;
        return ((float)v, (float)v);
    }

    /// <summary>Toux : bouffée de bruit filtré (~300 ms) toutes les 3 s.</summary>
    private double Cough(double t)
    {
        double local = (t + 1) % 3.0;
        double env = local < 0.01 ? local / 0.01 : Math.Exp(-(local - 0.01) / 0.09);
        _lowpass += (_noise.NextDouble() * 2 - 1 - _lowpass) * 0.25;
        return _lowpass * env * 0.6;
    }

    /// <summary>Notification : « ding » de 0,4 s (Do6 et son octave) toutes les 4 s.</summary>
    private static double Ding(double t)
    {
        double local = (t + 1) % 4.0;
        if (local > 0.6) return 0;
        double env = Math.Min(1, local / 0.005) * Math.Exp(-local / 0.15);
        return (Math.Sin(2 * Math.PI * 1046.5 * local) + 0.3 * Math.Sin(2 * Math.PI * 2093 * local)) * env * 0.25;
    }

    private double Voice(double t)
    {
        while (_cursor < _syllables.Count && _syllables[_cursor].End < t) _cursor++;
        if (_cursor >= _syllables.Count || t < _syllables[_cursor].Start)
        {
            _phase = 0;
            return 0;
        }

        var s = _syllables[_cursor];
        double u = (t - s.Start) / (s.End - s.Start);
        double f0 = s.F0 * (1 + s.Glide * u) * (1 + 0.01 * Math.Sin(2 * Math.PI * 5.5 * t));
        double dt = _lastT < 0 ? 1.0 / Rate : t - _lastT;
        _phase += 2 * Math.PI * f0 * dt;
        double env = Math.Min(1, Math.Min(u, 1 - u) * (s.End - s.Start) / 0.02);

        double sum = 0;
        for (int k = 1; k * f0 < 4000; k++)
        {
            double f = k * f0;
            double formants = 1 / (1 + Math.Pow((f - s.F1) / 90, 2)) + 0.7 / (1 + Math.Pow((f - s.F2) / 120, 2)) + 0.05;
            sum += Math.Sin(k * _phase) * formants / k;
        }
        return sum * env * 0.35;
    }
}

/// <summary>
/// Diagnostic : petit morceau synthétique dans une tonalité donnée (accords, basse, mélodie, batterie),
/// pour mesurer la précision de la détection de tonalité. Exemples : "C", "F#", "Bb", "Am", "C#m", "Ebm:h".
/// Suffixe ":h" en mineur = progression avec dominante majeure (i iv V i) ; sinon i VI III VII (cas difficile).
/// </summary>
internal sealed class TestSong
{
    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly string[] FlatNames = { "C", "DB", "D", "EB", "E", "F", "GB", "G", "AB", "A", "BB", "B" };

    private readonly int _tonic;
    private readonly bool _minor;
    private readonly (int Root, bool Minor)[] _chords;
    private readonly int[] _scale;
    private readonly double _detune;
    private readonly Random _rng = new(7);

    private TestSong(int tonic, bool minor, (int, bool)[] chords, double detuneCents)
    {
        _tonic = tonic;
        _minor = minor;
        _chords = chords;
        _scale = minor ? new[] { 0, 2, 3, 5, 7, 8, 10 } : new[] { 0, 2, 4, 5, 7, 9, 11 };
        _detune = Math.Pow(2, detuneCents / 1200);
    }

    public static TestSong? Parse(string spec, double detuneCents)
    {
        var parts = spec.Trim().ToUpperInvariant().Replace("♭", "B").Split(':');
        string key = parts[0];
        string variant = parts.Length > 1 ? parts[1] : string.Empty;
        bool minor = key.EndsWith('M') && key.Length > 1;
        if (minor) key = key[..^1];
        int tonic = Array.IndexOf(Names, key);
        if (tonic < 0) tonic = Array.IndexOf(FlatNames, key);
        if (tonic < 0) return null;

        (int, bool)[] chords = (minor, variant) switch
        {
            (true, "H") => new[] { (0, true), (5, true), (7, false), (0, true) },     // i iv V i (harmonique)
            (true, "N") => new[] { (0, true), (5, true), (7, true), (0, true) },      // i iv v i (naturel)
            (true, "S") => new[] { (0, true), (8, false), (3, false), (10, false) },  // i VI III VII (symétrique, ambigu)
            (true, _) => new[] { (0, true), (8, false), (10, false), (0, true) },     // i VI VII i
            (false, "B") => new[] { (0, false), (5, false), (0, false), (7, false) }, // I IV I V
            _ => new[] { (0, false), (7, false), (9, true), (5, false) },             // I V vi IV
        };
        return new TestSong(tonic, minor, chords, detuneCents);
    }

    public string Description => $"{Names[_tonic]}{(_minor ? "m" : string.Empty)}";

    /// <summary>Niveau de bruit large bande ajouté (0 = aucun).</summary>
    public double Noise { get; set; }

    private double Freq(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0) * _detune;

    /// <summary>Échantillon stéréo au temps t (secondes).</summary>
    public (float L, float R) Sample(double t)
    {
        const double bar = 2.0; // 120 BPM, une mesure par accord
        int barIndex = (int)(t / bar);
        double inBar = t - barIndex * bar;
        var (offset, chordMinor) = _chords[barIndex % _chords.Length];
        int root = (_tonic + offset) % 12;

        // Accord tenu (3 notes, timbre riche) vers l'octave 4.
        double pad = 0;
        int[] triad = { 0, chordMinor ? 3 : 4, 7 };
        foreach (var interval in triad)
        {
            double f = Freq(60 + (root + interval) % 12);
            for (int h = 1; h <= 6; h++) pad += Math.Sin(2 * Math.PI * f * h * t + h) / (h * 1.4);
        }
        pad *= 0.045 * (0.6 + 0.4 * Math.Exp(-inBar * 1.5));

        // Basse : fondamentale de l'accord, octave 2, sur chaque temps.
        double beat = inBar % 0.5;
        double fb = Freq(36 + root);
        double bass = (Math.Sin(2 * Math.PI * fb * t) + 0.4 * Math.Sin(4 * Math.PI * fb * t)) * 0.22 * Math.Exp(-beat * 2.5);

        // Mélodie : notes de l'accord et de la gamme ; la boucle se termine sur la tonique.
        int noteIndex = (int)(inBar / 0.5);
        int degree = noteIndex switch
        {
            0 => offset,
            1 => offset + triad[1],
            2 => offset + 7,
            _ => barIndex % _chords.Length == _chords.Length - 1 ? 12 : offset + _scale[(barIndex + 1) % 7],
        };
        double fm = Freq(72 + (_tonic + degree) % 12);
        double melody = (Math.Sin(2 * Math.PI * fm * t) + 0.3 * Math.Sin(4 * Math.PI * fm * t)) * 0.07 * Math.Exp(-beat * 3);

        // Batterie : kick sur les temps, charleston sur les contretemps.
        double kick = Math.Sin(2 * Math.PI * (50 + 100 * Math.Exp(-beat * 35)) * beat) * Math.Exp(-beat * 10) * 0.5;
        double off = (inBar + 0.25) % 0.5;
        double hat = (_rng.NextDouble() * 2 - 1) * Math.Exp(-off * 70) * 0.12;

        // Stress (Noise > 0) : bruit large bande et batterie bien plus forte.
        double noise = Noise > 0 ? (_rng.NextDouble() * 2 - 1) * Noise : 0;
        double drums = 1 + Noise * 6;
        double mono = pad + bass + kick * drums + noise;
        return ((float)(mono + melody * 0.8 + hat * 0.5 * drums), (float)(mono + melody * 1.2 + hat * drums));
    }
}
