namespace TaskbarVisu.Dsp;

/// <summary>
/// Notes jouées pour le piano roll, plus réactives et plus justes que celles de l'analyse de tonalité :
/// 1. deux résolutions, toutes deux recalculées toutes les 40 ms : FFT de 16384 points sous 150 Hz (demi-tons
///    serrés), 8192 au-dessus (fenêtre de 170 ms, deux fois plus courte que celle de l'analyse de tonalité).
///    Une FFT plus courte encore dans l'aigu confondait les partiels voisins d'un accord et les laissait passer
///    pour des notes ;
/// 2. fréquence de chaque pic par réallocation (fenêtre de Hann et sa dérivée, calculées dans une seule FFT
///    complexe) : précise à quelques cents près même entre deux cases ;
/// 3. pics retenus seulement s'ils dépassent nettement leur voisinage (le bruit et la batterie n'en ont pas) ;
/// 4. harmoniques écartées par le crible partagé avec l'analyse de tonalité (<see cref="HarmonicSieve"/>) ;
/// 5. hystérésis et durée minimale : une note s'allume après deux analyses au-dessus du seuil et ne s'éteint
///    qu'après deux analyses 6 dB plus bas, sans clignoter.
/// </summary>
internal sealed class NoteTracker
{
    private const int MaxPeaks = 96;
    private const double Interval = 0.04;
    private const float FloorAmp = 1e-4f; // -80 dBFS

    /// <summary>Une résolution : taille de FFT et bande de fréquences couverte.</summary>
    private sealed class Band
    {
        public readonly int Size;
        public readonly float LowHz, HighHz;
        public readonly Fft Fft;
        public readonly float[] Window, Deriv, Re, Im, Mag, Mono;
        public readonly float Norm;
        public readonly float[] PeakFreq = new float[MaxPeaks], PeakAmp = new float[MaxPeaks];
        public int PeakCount;

        public Band(int size, float lowHz, float highHz)
        {
            Size = size;
            LowHz = lowHz;
            HighHz = highHz;
            Fft = new Fft(size);
            Window = new float[size];
            Deriv = new float[size];
            Re = new float[size];
            Im = new float[size];
            Mag = new float[size / 2 + 1];
            Mono = new float[size];
            double sum = 0, w = 2 * Math.PI / (size - 1);
            for (int i = 0; i < size; i++)
            {
                Window[i] = (float)(0.5 - 0.5 * Math.Cos(w * i));
                Deriv[i] = (float)(0.5 * w * Math.Sin(w * i)); // dérivée de la fenêtre, par échantillon
                sum += Window[i];
            }
            Norm = (float)(2.0 / sum);
        }
    }

    private readonly Band[] _bands =
    {
        new(16384, 40, 150),
        new(8192, 150, 4500),
    };

    private readonly float[] _peakFreq = new float[MaxPeaks * 2], _peakAmp = new float[MaxPeaks * 2];
    private readonly HarmonicSieve _sieve = new(MaxPeaks * 2);
    private readonly float[] _raw = new float[128];
    private readonly int[] _above = new int[128], _below = new int[128];
    private readonly bool[] _on = new bool[128];
    private readonly Func<float, float> _binWidth;
    private int _rate = 48000;
    private double _elapsed, _tuneSin, _tuneCos = 1;
    private float _gain = 3e-4f;
    private long _audioFrame = -1;
    private bool _analyzed;

    public NoteTracker()
    {
        _binWidth = f =>
        {
            foreach (var band in _bands)
                if (f < band.HighHz) return (float)_rate / band.Size;
            return (float)_rate / _bands[^1].Size;
        };
    }

    /// <summary>Amplitude de chaque note allumée (numéro MIDI) ; 0 = éteinte.</summary>
    public float[] Notes { get; } = new float[128];

    /// <summary>Écart (dB) sous la note la plus forte des dernières secondes en dessous duquel une note ne s'allume pas.</summary>
    public float SensitivityDb { get; set; } = 30;

    /// <summary>Accordage estimé (cents par rapport à La 440 Hz).</summary>
    public double TuningCents => Math.Atan2(_tuneSin, _tuneCos) / (2 * Math.PI) * 100;

    /// <summary>À appeler à chaque image ; plusieurs modules peuvent l'appeler, seul le premier appel fait le travail.</summary>
    public bool Update(AudioAnalyzer audio, double dt)
    {
        if (audio.Frame == _audioFrame) return _analyzed;
        _audioFrame = audio.Frame;
        _analyzed = Analyze(audio, dt);
        return _analyzed;
    }

    private bool Analyze(AudioAnalyzer audio, double dt)
    {
        _elapsed += dt;
        if (_elapsed < Interval) return false;
        double frameDt = Math.Min(_elapsed, 0.5);
        _elapsed = 0;

        if (audio.SilentSeconds > 0.2)
        {
            Array.Clear(Notes);
            Array.Clear(_on);
            Array.Clear(_above);
            return true;
        }

        _rate = audio.SampleRate;

        // Pics de toutes les résolutions, chacune dans sa bande.
        int n = 0;
        foreach (var band in _bands)
        {
            Analyze(audio, band);
            Array.Copy(band.PeakFreq, 0, _peakFreq, n, band.PeakCount);
            Array.Copy(band.PeakAmp, 0, _peakAmp, n, band.PeakCount);
            n += band.PeakCount;
        }

        UpdateTuning(n, frameDt);
        double reference = 440 * Math.Pow(2, TuningCents / 1200);
        _sieve.Build(_peakFreq, _peakAmp, n, _raw, reference, _binWidth);
        UpdateStates(frameDt);
        return true;
    }

    /// <summary>FFT d'une résolution, pics nets de sa bande, fréquence par réallocation.</summary>
    private void Analyze(AudioAnalyzer audio, Band band)
    {
        audio.CopyMono(band.Mono);
        // Partie réelle : signal × fenêtre ; partie imaginaire : signal × dérivée de la fenêtre.
        for (int i = 0; i < band.Size; i++)
        {
            band.Re[i] = band.Mono[i] * band.Window[i];
            band.Im[i] = band.Mono[i] * band.Deriv[i];
        }
        band.Fft.Transform(band.Re, band.Im);

        int size = band.Size, half = size / 2;
        float binHz = (float)_rate / size;
        int k0 = Math.Max(3, (int)(band.LowHz / binHz) - 2);
        int k1 = Math.Min(half - 9, (int)(band.HighHz / binHz) + 2);
        for (int k = Math.Max(1, k0 - 8); k <= Math.Min(half, k1 + 8); k++)
        {
            // Séparation des deux spectres réels (fenêtre, dérivée) contenus dans la FFT complexe.
            int j = size - k;
            float ar = (band.Re[k] + band.Re[j]) * 0.5f, ai = (band.Im[k] - band.Im[j]) * 0.5f;
            band.Mag[k] = MathF.Sqrt(ar * ar + ai * ai) * band.Norm;
        }

        float max = 0;
        for (int k = k0; k <= k1; k++) max = Math.Max(max, band.Mag[k]);
        float threshold = Math.Max(max * 0.001f, FloorAmp); // -60 dB sous le plus fort pic de la bande

        Span<float> local = stackalloc float[17];
        band.PeakCount = 0;
        for (int k = k0; k <= k1; k++)
        {
            float m = band.Mag[k];
            if (m < threshold || m <= band.Mag[k - 1] || m < band.Mag[k + 1] || m <= band.Mag[k - 2] || m < band.Mag[k + 2])
                continue;

            // Pic net : au moins 6× (16 dB) le plancher de son voisinage (quart le plus bas de ±8 cases) ; pas la
            // médiane, que les lobes des notes voisines d'un accord serré font monter.
            for (int d = -8; d <= 8; d++) local[d + 8] = band.Mag[k + d];
            local.Sort();
            if (m < 6 * local[4]) continue;

            // Fréquence réallouée : ω̂ = ω − Im(X_dérivée · conj(X_fenêtre)) / |X_fenêtre|².
            int j = size - k;
            float ar = (band.Re[k] + band.Re[j]) * 0.5f, ai = (band.Im[k] - band.Im[j]) * 0.5f;
            float br = (band.Im[k] + band.Im[j]) * 0.5f, bi = -(band.Re[k] - band.Re[j]) * 0.5f;
            float power = ar * ar + ai * ai;
            if (power <= 0) continue;
            float shift = (bi * ar - br * ai) / power; // radians par échantillon
            float freq = (k - shift * size / (2 * MathF.PI)) * binHz;
            if (MathF.Abs(freq - k * binHz) > 1.5f * binHz) freq = k * binHz; // estimation aberrante (pics mêlés)
            if (freq < band.LowHz || freq >= band.HighHz) continue;

            // Amplitude : interpolation parabolique en dB.
            float a = Db(band.Mag[k - 1]), b = Db(m), c = Db(band.Mag[k + 1]);
            float denom = a - 2 * b + c;
            float p = Math.Abs(denom) > 1e-9f ? Math.Clamp(0.5f * (a - c) / denom, -0.5f, 0.5f) : 0;
            float amp = MathF.Pow(10, (b - 0.25f * (a - c) * p) / 20f);
            AddPeak(band, freq, amp);
        }
    }

    private static void AddPeak(Band band, float freq, float amp)
    {
        if (band.PeakCount < MaxPeaks)
        {
            band.PeakFreq[band.PeakCount] = freq;
            band.PeakAmp[band.PeakCount] = amp;
            band.PeakCount++;
            return;
        }
        int weakest = 0;
        for (int i = 1; i < MaxPeaks; i++) if (band.PeakAmp[i] < band.PeakAmp[weakest]) weakest = i;
        if (amp <= band.PeakAmp[weakest]) return;
        band.PeakFreq[weakest] = freq;
        band.PeakAmp[weakest] = amp;
    }

    /// <summary>Accordage : moyenne circulaire lente des écarts des pics à la grille tempérée.</summary>
    private void UpdateTuning(int n, double dt)
    {
        double decay = Math.Exp(-dt / 10.0);
        _tuneSin *= decay;
        _tuneCos *= decay;
        for (int i = 0; i < n; i++)
        {
            double cents = 1200 * Math.Log2(_peakFreq[i] / 440.0);
            double angle = (cents - 100 * Math.Round(cents / 100)) / 100 * 2 * Math.PI;
            double w = _peakAmp[i] * dt;
            _tuneSin += w * Math.Sin(angle);
            _tuneCos += w * Math.Cos(angle);
        }
    }

    /// <summary>Allumage après deux analyses au-dessus du seuil, extinction après deux analyses 6 dB plus bas.</summary>
    private void UpdateStates(double dt)
    {
        float loudest = 0;
        for (int m = 0; m < 128; m++) loudest = Math.Max(loudest, _raw[m]);
        _gain = Math.Max(Math.Max(_gain * (float)Math.Exp(-dt / 3.0), 3e-4f), loudest);
        float on = _gain * MathF.Pow(10, -SensitivityDb / 20), off = on * 0.5f;
        for (int m = 0; m < 128; m++)
        {
            float v = _raw[m];
            if (_on[m])
            {
                _below[m] = v < off ? _below[m] + 1 : 0;
                if (_below[m] >= 2) _on[m] = false;
            }
            else
            {
                _above[m] = v >= on ? _above[m] + 1 : 0;
                if (_above[m] >= 2)
                {
                    _on[m] = true;
                    _below[m] = 0;
                }
            }
            Notes[m] = _on[m] ? Math.Max(v, off) : 0;
        }
    }

    private static float Db(float linear) => 20f * MathF.Log10(Math.Max(linear, 1e-10f));
}
