namespace TaskbarVisu.Dsp;

internal static class Pitch
{
    private static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    /// <summary>Pic dominant du spectre (dB) entre minF et maxF, affiné par interpolation parabolique.</summary>
    public static bool FindPeak(ReadOnlySpan<float> db, float binHz, float minF, float maxF, out float freq, out float level)
    {
        freq = 0;
        level = float.NegativeInfinity;
        int lo = Math.Max(1, (int)(minF / binHz));
        int hi = Math.Min(db.Length - 2, (int)(maxF / binHz));
        int best = -1;
        for (int k = lo; k <= hi; k++)
        {
            if (db[k] > level) { level = db[k]; best = k; }
        }
        if (best < 0) return false;

        float a = db[best - 1], b = db[best], c = db[best + 1];
        float denom = a - 2 * b + c;
        float p = Math.Abs(denom) > 1e-9f ? 0.5f * (a - c) / denom : 0;
        p = Math.Clamp(p, -0.5f, 0.5f);
        freq = (best + p) * binHz;
        level = b - 0.25f * (a - c) * p;
        return freq > 0;
    }

    public static (string Note, int Cents) ToNote(float freq, float reference = 440f)
    {
        var (note, cents) = ToNoteExact(freq, reference);
        return (note, (int)Math.Round(cents));
    }

    /// <summary>Classe de hauteur (0 = C), octave et écart en cents (non arrondi) de la note la plus proche.</summary>
    public static (int PitchClass, int Octave, double Cents) Analyze(float freq, float reference = 440f)
    {
        double midi = 69 + 12 * Math.Log2(freq / reference);
        int n = (int)Math.Round(midi);
        return (((n % 12) + 12) % 12, (int)Math.Floor(n / 12.0) - 1, (midi - n) * 100);
    }

    /// <summary>Note la plus proche et écart en cents (non arrondi), pour un accordeur.</summary>
    public static (string Note, double Cents) ToNoteExact(float freq, float reference = 440f)
    {
        double midi = 69 + 12 * Math.Log2(freq / reference);
        int n = (int)Math.Round(midi);
        int octave = (int)Math.Floor(n / 12.0) - 1;
        return ($"{Names[((n % 12) + 12) % 12]}{octave}", (midi - n) * 100);
    }
}
