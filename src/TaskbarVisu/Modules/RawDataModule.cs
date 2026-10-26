using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Données brutes, pur expérimental : l'audio tel que la carte son le voit.
/// - Hexadécimal : échantillons 16 bits en hexa, lignes qui défilent vers le haut, teintées par leur niveau ;
/// - Bits : chaque colonne est un échantillon, ses 16 bits en matrice de points (motifs « glitch »).
/// </summary>
internal sealed class RawDataModule : VisualizerModule
{
    private const int MaxLines = 8;

    private readonly List<(string Text, float Level)> _lines = new();
    private SKBitmap? _bits;
    private int _writeColumn;
    private double _carry;
    private float _level;
    private bool _active;
    private readonly uint[] _bitColors = new uint[16];
    private (int Version, int Phase) _colorKey = (-1, -1);

    public RawDataModule() : base("rawdata") { }

    private bool BitsMode => OptI("mode") == 1;

    public override bool IsIdle => !_active;

    protected override void OnResize()
    {
        _bits?.Dispose();
        _bits = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _bits.Erase(SKColors.Transparent);
        _writeColumn = 0;
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        int n = audio.Count;
        _active = audio.SilentSeconds < 1;
        float peak = Math.Max(audio.PeakL, audio.PeakR);
        _level += (peak - _level) * 0.3f;

        double speed = Opt("speed");
        _carry += dt * (BitsMode ? speed * 4 : speed / 6);
        int steps = (int)_carry;
        if (steps <= 0) return;
        _carry -= steps;

        if (BitsMode) WriteColumns(audio, Math.Min(steps, Width));
        else
        {
            for (int s = 0; s < Math.Min(steps, MaxLines); s++)
            {
                _lines.Add((HexLine(audio, s), _level));
                if (_lines.Count > MaxLines) _lines.RemoveAt(0);
            }
        }
    }

    private static short ToShort(float v) => (short)Math.Clamp(v * 32767f, -32768f, 32767f);

    /// <summary>Une ligne d'échantillons 16 bits en hexadécimal, pris dans le bloc reçu à cette image.</summary>
    private string HexLine(AudioAnalyzer audio, int offset)
    {
        int n = audio.Count;
        if (n == 0 || !_active) return "0000 0000 0000 0000 0000";
        Span<char> buffer = stackalloc char[5 * 5];
        int pos = 0;
        for (int i = 0; i < 5; i++)
        {
            int index = Math.Min(n - 1, (offset * 7 + i * (n / 5 + 1)) % n);
            ushort v = (ushort)ToShort(audio.Left[index]);
            if (i > 0) buffer[pos++] = ' ';
            v.TryFormat(buffer[pos..], out int written, "X4");
            pos += written;
        }
        return new string(buffer[..pos]);
    }

    /// <summary>Mode Bits : de nouvelles colonnes de 16 bits (bit de poids fort en haut) dans l'image circulaire.</summary>
    private void WriteColumns(AudioAnalyzer audio, int columns)
    {
        if (_bits is null || _bitColors[15] == 0) return;
        int n = audio.Count;
        unsafe
        {
            uint* pixels = (uint*)_bits.GetPixels();
            for (int c = 0; c < columns; c++)
            {
                ushort v = n == 0 || !_active ? (ushort)0 : (ushort)ToShort(audio.Left[(c * 37) % n]);
                int x = _writeColumn;
                for (int y = 0; y < Height; y++)
                {
                    int bit = 15 - Math.Min(15, y * 16 / Height);
                    pixels[y * Width + x] = (v >> bit & 1) != 0 ? _bitColors[bit] : 0;
                }
                _writeColumn = (_writeColumn + 1) % Width;
            }
        }
        _bits.NotifyPixelsChanged();
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        var key = (t.Version, t.Animated ? (int)(t.Phase * 128) : 0);
        if (key != _colorKey)
        {
            _colorKey = key;
            for (int b = 0; b < 16; b++)
            {
                var c = t.Spectral(b / 15f);
                _bitColors[b] = 0xFFu << 24 | (uint)c.Red << 16 | (uint)c.Green << 8 | c.Blue;
            }
        }

        if (BitsMode)
        {
            if (_bits is null) return;
            // Colonnes les plus anciennes à gauche, les plus récentes à droite.
            int older = Width - _writeColumn;
            if (older > 0)
                canvas.DrawBitmap(_bits, new SKRect(_writeColumn, 0, Width, Height), new SKRect(rect.Left, rect.Top, rect.Left + older, rect.Bottom));
            if (_writeColumn > 0)
                canvas.DrawBitmap(_bits, new SKRect(0, 0, _writeColumn, Height), new SKRect(rect.Left + older, rect.Top, rect.Right, rect.Bottom));
        }
        else
        {
            // Lignes de texte du bas (la plus récente) vers le haut, qui s'estompent en remontant.
            float lineH = ctx.Labels.CapHeight + 2 * p;
            int visible = Math.Max(1, (int)((rect.Height + 2 * p) / lineH));
            float x = MathF.Round(rect.Left + Math.Max(p, ctx.EdgeInset));
            for (int i = 0; i < Math.Min(visible, _lines.Count); i++)
            {
                var (text, level) = _lines[_lines.Count - 1 - i];
                float y = MathF.Round(rect.Bottom - (i + 1) * lineH + p);
                byte alpha = (byte)(255 - i * 180 / Math.Max(1, visible));
                ctx.Labels.Draw(canvas, text, x, y, (i == 0 ? t.Spectral(Math.Clamp(level * 1.4f, 0.1f, 1f)) : t.Ink).WithAlpha(alpha));
            }
        }

        ctx.ApplyScanlines(canvas, rect);
    }

    public override void Dispose()
    {
        _bits?.Dispose();
    }
}
