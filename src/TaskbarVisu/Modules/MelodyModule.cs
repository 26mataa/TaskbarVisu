using System.Globalization;
using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Trace de mélodie : la note dominante (celle de l'accordeur) laisse une traînée lumineuse sur une grille de notes
/// qui défile ; on voit la mélodie se dessiner, glissés de voix et vibratos compris. La grille suit la tessiture
/// du moment (deux ou trois octaves) ; les lignes marquent les Do.
/// </summary>
internal sealed class MelodyModule : VisualizerModule
{
    private float[] _pitch = Array.Empty<float>(), _level = Array.Empty<float>();
    private int _head;
    private double _carry;
    private KeyAnalyzer? _analyzer;
    private float _current = float.NaN, _currentLevel;
    private double _sinceNote = 9;
    private float _centre = 64;
    private int _empty = int.MaxValue / 2;
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly string[] _octaveNames = Enumerable.Range(0, 10).Select(o => "C" + o.ToString(CultureInfo.InvariantCulture)).ToArray();

    public MelodyModule() : base("melody") { }

    private float Span => OptI("range") == 1 ? 36 : 24;

    public override bool IsIdle => _empty >= _pitch.Length;

    protected override void OnResize()
    {
        _pitch = new float[Width];
        _level = new float[Width];
        Array.Fill(_pitch, float.NaN);
        _head = 0;
        _empty = Width;
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        if (_pitch.Length == 0) return;
        _analyzer = audio.Harmony;
        _sinceNote += dt;
        if (_analyzer.Update(audio, dt))
        {
            if (_analyzer.HasPitch && audio.SilentSeconds < 0.3)
            {
                float midi = 69 + 12 * MathF.Log2(_analyzer.PitchHz / 440f);
                // Saut d'octave isolé (erreur de détection) : on garde la note précédente.
                if (!float.IsNaN(_current) && MathF.Abs(midi - _current) is > 11.5f and < 12.5f && _sinceNote < 0.2) midi = _current;
                _current = midi;
                _currentLevel = Math.Clamp((_analyzer.PitchDb + 60) / 45, 0.15f, 1);
                _sinceNote = 0;
            }
        }
        // Petits trous (consonne, attaque) comblés ; au-delà, la traînée s'interrompt.
        if (_sinceNote > 0.25) _current = float.NaN;
        if (!float.IsNaN(_current)) _centre += (_current - _centre) * (1 - (float)Math.Exp(-dt / 2.5));

        _carry += dt * Opt("speed");
        int steps = Math.Min((int)_carry, _pitch.Length);
        _carry -= (int)_carry;
        for (int s = 0; s < steps; s++)
        {
            _head = (_head + 1) % _pitch.Length;
            _pitch[_head] = _current;
            _level[_head] = _currentLevel;
            _empty = float.IsNaN(_current) ? _empty + 1 : 0;
        }
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        int n = _pitch.Length;
        if (n == 0) return;
        float span = Span;
        float low = _centre - span / 2;
        float top = rect.Top + p, height = rect.Height - 2 * p;
        float Y(float midi) => MathF.Round(top + height - (midi - low) / span * height);

        // Grille : une ligne par Do, et le nom de l'octave à gauche.
        int firstC = (int)MathF.Ceiling(low / 12) * 12;
        float inset = MathF.Ceiling(Math.Max(p, ctx.EdgeInset));
        for (int c = firstC; c <= low + span; c += 12)
        {
            float y = Y(c);
            _fill.Color = t.GridFaint;
            for (float x = rect.Left; x < rect.Right; x += 2 * p) canvas.DrawRect(x, y, p, p, _fill);
            int octave = c / 12 - 1;
            if (ctx.ShowScales && octave is >= 0 and <= 9 && y - ctx.Labels.CapHeight - p > rect.Top)
                ctx.Labels.Draw(canvas, _octaveNames[octave], rect.Left + inset, y - ctx.Labels.CapHeight - p, t.InkDim);
        }

        // Traînée : de la plus ancienne (à gauche, éteinte) à la plus récente (à droite, vive).
        float prevY = float.NaN;
        for (int age = n - 1; age >= 0; age--)
        {
            float midi = _pitch[(_head - age + n) % n];
            float x = rect.Right - 1 - age;
            if (float.IsNaN(midi)) { prevY = float.NaN; continue; }
            float y = Y(midi);
            float fresh = 1 - age / (float)n;
            float level = _level[(_head - age + n) % n];
            var color = t.Spectral(0.4f + 0.6f * level * (0.5f + 0.5f * fresh));
            _fill.Color = color.WithAlpha((byte)(90 + 165 * fresh));
            // Relie au point précédent quand la note glisse (pas pour un saut de plus d'une quarte).
            float y0 = y, y1 = y;
            if (!float.IsNaN(prevY) && MathF.Abs(prevY - y) <= height * 5 / span) { y0 = Math.Min(prevY, y); y1 = Math.Max(prevY, y); }
            canvas.DrawRect(x, y0, 1, y1 - y0 + p, _fill);
            prevY = y;
        }

        // Tête : la note qui sonne, en point lumineux.
        if (!float.IsNaN(_current))
        {
            float y = Y(_current);
            _fill.Color = t.Spectral(1f).WithAlpha(0x60);
            canvas.DrawRect(rect.Right - 4 * p, y - p, 4 * p, 3 * p, _fill);
            _fill.Color = t.Spectral(1f);
            canvas.DrawRect(rect.Right - 2 * p, y, 2 * p, p, _fill);
        }
        ctx.ApplyScanlines(canvas, rect);
    }

    public override void Dispose() => _fill.Dispose();
}
