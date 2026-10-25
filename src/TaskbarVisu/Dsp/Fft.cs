namespace TaskbarVisu.Dsp;

/// <summary>FFT complexe radix-2 en place, tables précalculées (aucune allocation par appel).</summary>
internal sealed class Fft
{
    private readonly int _n;
    private readonly int[] _reverse;
    private readonly float[] _cos;
    private readonly float[] _sin;

    public Fft(int n)
    {
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("Taille en puissance de 2 attendue", nameof(n));
        _n = n;
        _reverse = new int[n];
        int bits = (int)Math.Log2(n);
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _reverse[i] = r;
        }

        _cos = new float[n / 2];
        _sin = new float[n / 2];
        for (int k = 0; k < n / 2; k++)
        {
            double a = 2 * Math.PI * k / n;
            _cos[k] = (float)Math.Cos(a);
            _sin[k] = (float)-Math.Sin(a);
        }
    }

    public int Size => _n;

    public void Transform(float[] re, float[] im)
    {
        int n = _n;
        for (int i = 0; i < n; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1;
            int step = n / size;
            for (int start = 0; start < n; start += size)
            {
                for (int j = 0, k = 0; j < half; j++, k += step)
                {
                    float wr = _cos[k], wi = _sin[k];
                    int a = start + j, b = a + half;
                    float tr = wr * re[b] - wi * im[b];
                    float ti = wr * im[b] + wi * re[b];
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
