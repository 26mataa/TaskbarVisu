using TaskbarVisu.Audio;

namespace TaskbarVisu.Dsp;

/// <summary>
/// Analyse partagée par tous les modules, exécutée sur le thread de rendu.
/// Chaque image : on consomme les échantillons arrivés (avec lissage de gigue pour un défilement régulier),
/// puis crêtes, RMS, LUFS, et un spectre FFT calculé à la demande une seule fois par image.
/// </summary>
internal sealed class AudioAnalyzer
{
    public const int MaxFrame = 1 << 15;
    private const int HistorySize = 1 << 15;
    private const double TargetLatency = 0.035;

    private readonly SampleRing _ring;
    private long _read;
    private int _generation = -1;
    private double _carry;

    private readonly float[] _histL = new float[HistorySize];
    private readonly float[] _histR = new float[HistorySize];
    private int _histPos;

    private Fft _fft = new(4096);
    private float[] _window = Array.Empty<float>();
    private float[] _re = Array.Empty<float>(), _im = Array.Empty<float>();
    private float[] _midPow = Array.Empty<float>(), _sidePow = Array.Empty<float>(), _sumDb = Array.Empty<float>();
    private float[] _leftPow = Array.Empty<float>(), _rightPow = Array.Empty<float>();
    private float _windowNorm;
    private bool _spectrumDirty = true;
    private int _samplesSinceFft;

    public AudioAnalyzer(SampleRing ring)
    {
        _ring = ring;
        SetFftSize(4096);
    }

    public int SampleRate { get; private set; } = 48000;

    /// <summary>Échantillons consommés pendant cette image.</summary>
    public float[] Left { get; } = new float[MaxFrame];
    public float[] Right { get; } = new float[MaxFrame];
    public int Count { get; private set; }

    public float PeakL { get; private set; }
    public float PeakR { get; private set; }
    public float RmsL { get; private set; }
    public float RmsR { get; private set; }

    /// <summary>Durée depuis laquelle le signal est inaudible (sous ~ -100 dBFS).</summary>
    public double SilentSeconds { get; private set; }

    public LoudnessMeter Loudness { get; } = new();

    public int FftSize => _fft.Size;
    public float BinHz => (float)SampleRate / _fft.Size;

    /// <summary>Numéro de l'image analysée (incrémenté à chaque Process).</summary>
    public long Frame { get; private set; }

    private KeyAnalyzer? _harmony;

    /// <summary>
    /// Analyse harmonique haute résolution (accordeur, piano roll) : créée au premier module qui la demande,
    /// partagée ensuite, et calculée une seule fois par image quel que soit le nombre de modules.
    /// </summary>
    public KeyAnalyzer Harmony => _harmony ??= new KeyAnalyzer();

    public void SetFftSize(int size)
    {
        if (size == _fft.Size && _window.Length == size) return;
        _fft = new Fft(size);
        _window = new float[size];
        double sum = 0;
        for (int i = 0; i < size; i++)
        {
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1)));
            sum += _window[i];
        }
        _windowNorm = (float)(2.0 / sum);
        _re = new float[size];
        _im = new float[size];
        _midPow = new float[size / 2 + 1];
        _sidePow = new float[size / 2 + 1];
        _sumDb = new float[size / 2 + 1];
        _leftPow = new float[size / 2 + 1];
        _rightPow = new float[size / 2 + 1];
        _spectrumDirty = true;
    }

    /// <summary>Copie les derniers échantillons mono ((L+R)/2), du plus ancien au plus récent.</summary>
    public void CopyMono(Span<float> destination)
    {
        int n = Math.Min(destination.Length, HistorySize);
        int start = (_histPos - n) & (HistorySize - 1);
        for (int i = 0; i < n; i++)
        {
            int idx = (start + i) & (HistorySize - 1);
            destination[i] = (_histL[idx] + _histR[idx]) * 0.5f;
        }
    }

    /// <summary>Rattrape le flux en direct (après une pause ou un masquage).</summary>
    public void Resync()
    {
        _read = _ring.WriteIndex - (long)(TargetLatency * SampleRate);
        _carry = 0;
    }

    public void Process(double dt)
    {
        Frame++;
        if (_ring.Generation != _generation)
        {
            _generation = _ring.Generation;
            SampleRate = _ring.SampleRate;
            Loudness.Reset(SampleRate);
            Array.Clear(_histL);
            Array.Clear(_histR);
            Resync();
        }

        long write = _ring.WriteIndex;
        long available = write - _read;
        long latencySamples = (long)(TargetLatency * SampleRate);
        if (available > _ring.Capacity / 2 || available < 0)
        {
            _read = write - latencySamples;
            available = latencySamples;
        }

        double wanted = dt * SampleRate;
        int real, pad = 0;
        if (_ring.SecondsSinceLastWrite > 0.12)
        {
            // Plus rien n'arrive (aucun son joué) : on vide le reste et on complète par du silence
            // pour que les modules qui défilent continuent d'avancer.
            real = (int)Math.Clamp(available, 0, MaxFrame);
            pad = (int)Math.Clamp(wanted - real, 0, MaxFrame - real);
            _carry = 0;
        }
        else
        {
            // Lecture au rythme du temps réel, corrigée doucement vers la latence cible (anti-gigue).
            _carry += wanted + (available - latencySamples) * 0.12;
            real = (int)Math.Clamp(Math.Floor(_carry), 0, Math.Min(available, MaxFrame));
            _carry = Math.Clamp(_carry - real, -wanted, wanted);
        }

        if (real > 0)
        {
            _ring.Read(_read, real, Left, Right);
            _read += real;
        }
        if (pad > 0)
        {
            Array.Clear(Left, real, pad);
            Array.Clear(Right, real, pad);
        }

        int n = real + pad;
        Count = n;
        Analyze(n, dt);
    }

    private void Analyze(int n, double dt)
    {
        float pl = 0, pr = 0;
        double sl = 0, sr = 0;
        for (int i = 0; i < n; i++)
        {
            float l = Left[i], r = Right[i];
            float al = Math.Abs(l), ar = Math.Abs(r);
            if (al > pl) pl = al;
            if (ar > pr) pr = ar;
            sl += l * l;
            sr += r * r;
            _histL[_histPos] = l;
            _histR[_histPos] = r;
            _histPos = (_histPos + 1) & (HistorySize - 1);
        }

        PeakL = pl;
        PeakR = pr;
        RmsL = n > 0 ? (float)Math.Sqrt(sl / n) : 0;
        RmsR = n > 0 ? (float)Math.Sqrt(sr / n) : 0;
        SilentSeconds = Math.Max(pl, pr) > 1e-5f ? 0 : SilentSeconds + dt;

        if (n > 0) Loudness.Process(Left.AsSpan(0, n), Right.AsSpan(0, n));

        // Nouvelle FFT seulement après un quart de fenêtre de nouveaux échantillons (~47 fois/s à 4096 points).
        _samplesSinceFft += n;
        if (_samplesSinceFft >= _fft.Size / 4)
        {
            _samplesSinceFft = 0;
            _spectrumDirty = true;
        }
    }

    /// <summary>Puissance du canal Mid (L+R)/2 par case FFT (1.0 = sinus pleine échelle).</summary>
    public ReadOnlySpan<float> MidPower { get { EnsureSpectrum(); return _midPow; } }

    /// <summary>Puissance du canal Side (L-R)/2 par case FFT.</summary>
    public ReadOnlySpan<float> SidePower { get { EnsureSpectrum(); return _sidePow; } }

    /// <summary>Puissance du canal gauche par case FFT.</summary>
    public ReadOnlySpan<float> LeftPower { get { EnsureSpectrum(); return _leftPow; } }

    /// <summary>Puissance du canal droit par case FFT.</summary>
    public ReadOnlySpan<float> RightPower { get { EnsureSpectrum(); return _rightPow; } }

    /// <summary>Puissance moyenne des deux canaux en dB (insensible à la phase), par case FFT.</summary>
    public ReadOnlySpan<float> SpectrumDb { get { EnsureSpectrum(); return _sumDb; } }

    private void EnsureSpectrum()
    {
        if (!_spectrumDirty) return;
        _spectrumDirty = false;

        int size = _fft.Size;
        int start = (_histPos - size) & (HistorySize - 1);
        for (int i = 0; i < size; i++)
        {
            int idx = (start + i) & (HistorySize - 1);
            float l = _histL[idx], r = _histR[idx], w = _window[i];
            // Deux signaux réels dans une seule FFT complexe : Mid en partie réelle, Side en partie imaginaire.
            _re[i] = (l + r) * 0.5f * w;
            _im[i] = (l - r) * 0.5f * w;
        }

        _fft.Transform(_re, _im);

        float norm2 = _windowNorm * _windowNorm;
        int half = size / 2;
        for (int k = 0; k <= half; k++)
        {
            int j = (size - k) & (size - 1);
            float zr = _re[k], zi = _im[k], cr = _re[j], ci = -_im[j];
            float mr = (zr + cr) * 0.5f, mi = (zi + ci) * 0.5f;
            float sr = (zi - ci) * 0.5f, si = -(zr - cr) * 0.5f;
            float mp = (mr * mr + mi * mi) * norm2;
            float sp = (sr * sr + si * si) * norm2;
            _midPow[k] = mp;
            _sidePow[k] = sp;
            // L = M + S, R = M - S (en complexe).
            float lr = mr + sr, li = mi + si, rr = mr - sr, ri = mi - si;
            _leftPow[k] = (lr * lr + li * li) * norm2;
            _rightPow[k] = (rr * rr + ri * ri) * norm2;
            _sumDb[k] = 10f * MathF.Log10(mp + sp + 1e-20f);
        }
    }
}
