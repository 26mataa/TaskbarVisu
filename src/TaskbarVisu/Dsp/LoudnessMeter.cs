namespace TaskbarVisu.Dsp;

/// <summary>
/// Sonie ITU-R BS.1770 / EBU R128 : filtre K (shelf + passe-haut), blocs de 100 ms,
/// momentanée = 400 ms, court terme = 3 s.
/// </summary>
internal sealed class LoudnessMeter
{
    private struct Biquad
    {
        public double B0, B1, B2, A1, A2, Z1, Z2;

        public double Process(double x)
        {
            double y = B0 * x + Z1;
            Z1 = B1 * x - A1 * y + Z2;
            Z2 = B2 * x - A2 * y;
            return y;
        }
    }

    public const double Silence = -200;

    private Biquad _shelfL, _shelfR, _hpL, _hpR;
    private readonly double[] _blocks = new double[30];
    private int _blockIndex, _blocksFilled, _blockSize, _blockCount;
    private double _blockSum;

    public double Momentary { get; private set; } = Silence;
    public double ShortTerm { get; private set; } = Silence;

    public void Reset(int sampleRate)
    {
        // Coefficients recalculés pour la fréquence d'échantillonnage (même méthode que libebur128).
        double fs = sampleRate;
        double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        double k = Math.Tan(Math.PI * f0 / fs);
        double vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + k / q + k * k;
        var shelf = new Biquad
        {
            B0 = (vh + vb * k / q + k * k) / a0,
            B1 = 2 * (k * k - vh) / a0,
            B2 = (vh - vb * k / q + k * k) / a0,
            A1 = 2 * (k * k - 1) / a0,
            A2 = (1 - k / q + k * k) / a0,
        };

        f0 = 38.13547087602444; q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / fs);
        double d = 1 + k / q + k * k;
        var hp = new Biquad { B0 = 1, B1 = -2, B2 = 1, A1 = 2 * (k * k - 1) / d, A2 = (1 - k / q + k * k) / d };

        _shelfL = _shelfR = shelf;
        _hpL = _hpR = hp;
        _blockSize = Math.Max(1, sampleRate / 10);
        _blockCount = 0;
        _blockSum = 0;
        _blockIndex = 0;
        _blocksFilled = 0;
        Array.Clear(_blocks);
        Momentary = ShortTerm = Silence;
    }

    public void Process(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (_blockSize == 0) Reset(48000);
        for (int i = 0; i < left.Length; i++)
        {
            double l = _hpL.Process(_shelfL.Process(left[i]));
            double r = _hpR.Process(_shelfR.Process(right[i]));
            _blockSum += l * l + r * r;
            if (++_blockCount >= _blockSize)
            {
                _blocks[_blockIndex] = _blockSum / _blockSize;
                _blockIndex = (_blockIndex + 1) % _blocks.Length;
                if (_blocksFilled < _blocks.Length) _blocksFilled++;
                _blockCount = 0;
                _blockSum = 0;
                Recompute();
            }
        }
    }

    private void Recompute()
    {
        Momentary = ToLufs(Mean(4));
        ShortTerm = ToLufs(Mean(30));
    }

    private double Mean(int count)
    {
        int n = Math.Min(count, _blocksFilled);
        if (n == 0) return 0;
        double sum = 0;
        for (int i = 1; i <= n; i++)
            sum += _blocks[(_blockIndex - i + _blocks.Length) % _blocks.Length];
        return sum / n;
    }

    private static double ToLufs(double meanSquare) =>
        meanSquare <= 1e-15 ? Silence : -0.691 + 10 * Math.Log10(meanSquare);
}
