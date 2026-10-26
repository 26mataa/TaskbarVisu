namespace TaskbarVisu.Dsp;

/// <summary>
/// Notes réellement jouées, octave comprise, par soustraction des harmoniques : du grave à l'aigu, chaque pic
/// est d'abord « expliqué » par les harmoniques possibles des notes plus graves (au plus HarmonicCeiling(h)
/// de leur amplitude, notes jugées jouées seulement, et si leur série harmonique monte bien jusque-là, voir InSeries).
/// Seul le reste compte comme note jouée : une harmonique s'éteint, une note qui tombe sur une harmonique
/// (octave doublée, Mi4 au-dessus d'un La2) reste visible, et les harmoniques de plusieurs notes qui se
/// superposent (Sol5 = ×2 de Sol4 et ×3 de Do4) s'additionnent au lieu de passer pour une note.
/// Partagé par l'analyse de tonalité (<see cref="KeyAnalyzer"/>) et le piano roll (<see cref="NoteTracker"/>).
/// </summary>
internal sealed class HarmonicSieve
{
    private readonly int[] _order;
    private readonly float[] _keys, _residual;
    private float[] _freq = Array.Empty<float>(), _amp = Array.Empty<float>();
    private Func<float, float> _binWidth = _ => 1;

    public HarmonicSieve(int capacity)
    {
        _order = new int[capacity];
        _keys = new float[capacity];
        _residual = new float[capacity];
    }

    /// <summary>Fréquences des pics triées par <see cref="Build"/> (les <c>n</c> premières).</summary>
    public float[] SortedFreq => _keys;

    /// <summary>Indice d'origine de chaque pic trié.</summary>
    public int[] Order => _order;

    /// <summary>Diagnostic (--notetrace avec KEY_PEAKS=1) : pics de chaque image, « fréquence:amplitude:part non expliquée ».</summary>
    public static System.Text.StringBuilder? DebugPeaks;

    /// <param name="notes">Reçoit l'amplitude propre de chaque note (numéro MIDI), harmoniques retirées.</param>
    /// <param name="binWidth">Largeur d'une case FFT (Hz) autour d'une fréquence : plusieurs résolutions possibles.</param>
    public void Build(float[] freq, float[] amp, int n, float[] notes, double reference, Func<float, float> binWidth,
        float minF = 40, float maxF = 4500)
    {
        Array.Clear(notes);
        if (n == 0) return;
        _freq = freq;
        _amp = amp;
        _binWidth = binWidth;

        for (int i = 0; i < n; i++)
        {
            _order[i] = i;
            _keys[i] = freq[i];
        }
        Array.Sort(_keys, _order, 0, n);
        float maxAmp = 0;
        for (int i = 0; i < n; i++) maxAmp = Math.Max(maxAmp, amp[i]);

        for (int a = 0; a < n; a++)
        {
            int j = _order[a];
            float f = freq[j];
            float explained = 0;
            for (int b = 0; b < a; b++)
            {
                int i = _order[b];
                if (_residual[i] <= 0) continue;
                // Tri grossier (les harmoniques aiguës d'un piano sont un peu trop hautes), le test exact est dans InSeries.
                float ratio = f / freq[i];
                int h = (int)MathF.Round(ratio);
                if (h < 2 || h > 12 || ratio < h * 0.988f || ratio > h * (1.012f + 0.0004f * h * h)) continue;
                // Amplitude mesurée entière, pas le reste : un partiel presque à l'unisson d'une harmonique plus grave
                // (Mi3 contre le ×4 d'une basse Mi1) bat avec elle, et le reste creuserait au rythme des battements.
                if (InSeries(freq[i], h, f, n)) explained += amp[i] * HarmonicCeiling(h);
            }
            _residual[j] = amp[j] - explained;
            if (f < minF || f > maxF) continue;

            int midi = (int)Math.Round(69 + 12 * Math.Log2(f / reference));
            if (midi is >= 0 and < 128) notes[midi] = Math.Max(notes[midi], Math.Max(0, _residual[j]));

            DebugPeaks?.Append($"{f:F1}:{amp[j] / maxAmp:F3}:{Math.Max(0, _residual[j]) / amp[j]:F2} ");
        }
        DebugPeaks?.AppendLine();
    }

    /// <summary>
    /// Amplitude maximale attendue de l'harmonique h, relative à la fondamentale : un peu au-dessus de 1/h,
    /// le profil des timbres les plus riches courants (dents de scie ; piano ≈ 1/h^1,2, et décroît plus vite).
    /// </summary>
    private static float HarmonicCeiling(int h) => 1.15f / h;

    /// <summary>
    /// Vrai si le pic de fréquence f peut être l'harmonique h de f0 : toutes les harmoniques intermédiaires sont
    /// présentes et la fréquence colle. Une basse presque pure (sinus, sub) sans harmoniques 3 et 4 n'explique
    /// donc pas la tierce de l'accord qui tombe sur son ×5. L'inharmonicité (partiels du piano un peu trop hauts)
    /// est estimée sur la série elle-même, par une médiane qui résiste aux partiels confondus avec ceux d'une
    /// autre note de l'accord.
    /// </summary>
    private bool InSeries(float f0, int h, float f, int n)
    {
        Span<float> stretch = stackalloc float[h];
        Span<float> sorted = stackalloc float[h];
        int found = 0;
        for (int k = 2; k <= h; k++)
        {
            float inharmonicity = 0, upper = 0;
            if (found > 0)
            {
                stretch[..found].CopyTo(sorted);
                sorted[..found].Sort();
                inharmonicity = Math.Max(0, sorted[found / 2]);
                upper = Math.Max(inharmonicity, sorted[found * 3 / 4]);
            }
            float expected = k * f0 * MathF.Sqrt(1 + inharmonicity * k * k);
            if (k == h)
            {
                // Dans un accord, bien des partiels se confondent avec ceux d'autres notes (étirés autrement) et tirent
                // la médiane vers le bas : le quartile haut borne la fenêtre côté aigu.
                float high = k * f0 * MathF.Sqrt(1 + upper * k * k);
                return f >= expected * 0.988f && f <= high * 1.012f;
            }
            var (freq, a) = PeakNear(expected, n);
            if (a <= 0)
            {
                // Partiel non résolu car collé à une note voisine plus forte (moins de 4,5 cases d'écart) : supposé présent.
                if (!HasPeakWithin(expected, 4.5f * _binWidth(expected), n)) return false;
                continue;
            }
            float r = freq / (k * f0);
            stretch[found++] = (r * r - 1) / (k * k);
        }
        return false;
    }

    private bool HasPeakWithin(float target, float distance, int n)
    {
        int pos = Array.BinarySearch(_keys, 0, n, target - distance);
        if (pos < 0) pos = ~pos;
        return pos < n && _keys[pos] <= target + distance;
    }

    /// <summary>Plus fort pic à ±1,2 % de la fréquence donnée (amplitude 0 s'il n'y en a pas).</summary>
    private (float Freq, float Amp) PeakNear(float target, int n)
    {
        float tolerance = 0.012f * target, freq = target, best = 0;
        int pos = Array.BinarySearch(_keys, 0, n, target - tolerance);
        if (pos < 0) pos = ~pos;
        for (; pos < n && _keys[pos] <= target + tolerance; pos++)
        {
            float a = _amp[_order[pos]];
            if (a > best) { best = a; freq = _keys[pos]; }
        }
        return (freq, best);
    }
}
