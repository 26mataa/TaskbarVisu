using System.Diagnostics;

namespace TaskbarVisu.Audio;

/// <summary>
/// Tampon circulaire stéréo (float entrelacé) à un producteur / un consommateur.
/// Le thread de capture écrit, le thread de rendu lit à son rythme.
/// </summary>
internal sealed class SampleRing
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _write;
    private long _lastWriteTimestamp;
    private int _generation;
    private int _sampleRate = 48000;

    public SampleRing(int capacityPow2)
    {
        if ((capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentException("Capacité en puissance de 2 attendue");
        Capacity = capacityPow2;
        _mask = capacityPow2 - 1;
        _buffer = new float[capacityPow2 * 2];
    }

    public int Capacity { get; }

    /// <summary>Nombre total de trames stéréo écrites depuis le démarrage.</summary>
    public long WriteIndex => Volatile.Read(ref _write);

    public int SampleRate => Volatile.Read(ref _sampleRate);

    /// <summary>Change à chaque (re)démarrage de capture : le lecteur doit se resynchroniser.</summary>
    public int Generation => Volatile.Read(ref _generation);

    public double SecondsSinceLastWrite =>
        (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastWriteTimestamp)) / (double)Stopwatch.Frequency;

    public void Reset(int sampleRate)
    {
        Volatile.Write(ref _sampleRate, sampleRate);
        Interlocked.Increment(ref _generation);
    }

    public void Write(ReadOnlySpan<float> interleavedStereo, int frames)
    {
        long w = _write;
        for (int i = 0; i < frames; i++)
        {
            int idx = (int)((w + i) & _mask) << 1;
            _buffer[idx] = interleavedStereo[i << 1];
            _buffer[idx + 1] = interleavedStereo[(i << 1) + 1];
        }
        Volatile.Write(ref _write, w + frames);
        Volatile.Write(ref _lastWriteTimestamp, Stopwatch.GetTimestamp());
    }

    public void Read(long from, int frames, Span<float> left, Span<float> right)
    {
        for (int i = 0; i < frames; i++)
        {
            int idx = (int)((from + i) & _mask) << 1;
            left[i] = _buffer[idx];
            right[i] = _buffer[idx + 1];
        }
    }
}
