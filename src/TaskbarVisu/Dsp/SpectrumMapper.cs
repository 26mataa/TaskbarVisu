namespace TaskbarVisu.Dsp;

/// <summary>
/// Projette les cases FFT (linéaires) sur N colonnes / lignes en échelle logarithmique.
/// Interpole dans les graves (moins d'une case par colonne), prend le max / la somme dans les aigus.
/// </summary>
internal sealed class SpectrumMapper
{
    private int _count, _sampleRate, _fftSize;
    private float _minF, _maxF, _tilt;
    private int[] _lo = Array.Empty<int>(), _hi = Array.Empty<int>();
    private float[] _frac = Array.Empty<float>(), _tiltDb = Array.Empty<float>(), _freq = Array.Empty<float>();
    private bool[] _interp = Array.Empty<bool>();

    public int Count => _count;
    public ReadOnlySpan<float> Frequencies => _freq;

    /// <summary>Reconstruit les tables si un paramètre a changé. Renvoie true si c'est le cas.</summary>
    public bool Ensure(int count, float minF, float maxF, int sampleRate, int fftSize, float tiltDbPerOctave)
    {
        if (count == _count && minF == _minF && maxF == _maxF && sampleRate == _sampleRate
            && fftSize == _fftSize && tiltDbPerOctave == _tilt)
            return false;

        _count = count; _minF = minF; _maxF = Math.Max(maxF, minF * 2); _sampleRate = sampleRate;
        _fftSize = fftSize; _tilt = tiltDbPerOctave;
        _lo = new int[count]; _hi = new int[count];
        _frac = new float[count]; _tiltDb = new float[count]; _freq = new float[count];
        _interp = new bool[count];

        float binHz = (float)sampleRate / fftSize;
        int lastBin = fftSize / 2;
        double ratio = _maxF / _minF;
        for (int i = 0; i < count; i++)
        {
            double f0 = _minF * Math.Pow(ratio, (double)i / count);
            double f1 = _minF * Math.Pow(ratio, (double)(i + 1) / count);
            double fc = Math.Sqrt(f0 * f1);
            _freq[i] = (float)fc;
            _tiltDb[i] = (float)(tiltDbPerOctave * Math.Log2(fc / 1000.0));

            double b0 = f0 / binHz, b1 = f1 / binHz;
            if (b1 - b0 < 1.0)
            {
                double pos = Math.Clamp(fc / binHz, 0, lastBin);
                int lo = Math.Min((int)Math.Floor(pos), lastBin - 1);
                _lo[i] = lo;
                _hi[i] = lo + 1;
                _frac[i] = (float)(pos - lo);
                _interp[i] = true;
            }
            else
            {
                int lo = Math.Clamp((int)Math.Ceiling(b0), 0, lastBin);
                int hi = Math.Clamp((int)Math.Floor(b1), lo, lastBin);
                _lo[i] = lo;
                _hi[i] = hi;
            }
        }
        return true;
    }

    /// <summary>Valeurs en dB : interpolation ou max, + pente (tilt).</summary>
    public void MapDb(ReadOnlySpan<float> db, Span<float> dst, bool applyTilt = true)
    {
        for (int i = 0; i < _count; i++)
        {
            float v;
            if (_interp[i])
            {
                float a = db[_lo[i]], b = db[_hi[i]];
                v = a + (b - a) * _frac[i];
            }
            else
            {
                v = db[_lo[i]];
                for (int k = _lo[i] + 1; k <= _hi[i]; k++)
                    if (db[k] > v) v = db[k];
            }
            dst[i] = applyTilt ? v + _tiltDb[i] : v;
        }
    }

    /// <summary>Puissances linéaires : interpolation ou somme.</summary>
    public void MapPower(ReadOnlySpan<float> pow, Span<float> dst)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_interp[i])
            {
                float a = pow[_lo[i]], b = pow[_hi[i]];
                dst[i] = a + (b - a) * _frac[i];
            }
            else
            {
                float sum = 0;
                for (int k = _lo[i]; k <= _hi[i]; k++) sum += pow[k];
                dst[i] = sum;
            }
        }
    }
}
