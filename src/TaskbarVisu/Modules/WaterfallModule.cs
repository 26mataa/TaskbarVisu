using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Cascade façon « Unknown Pleasures » : une ligne de spectre par instant, empilées en perspective.
/// La plus récente est devant (en bas, sur toute la largeur), les plus anciennes reculent vers le haut
/// en rétrécissant et en s'assombrissant ; chaque ligne cache celles qui sont derrière elle.
/// </summary>
internal sealed class WaterfallModule : VisualizerModule
{
    private const int MaxLines = 24;

    private readonly SpectrumMapper _mapper = new();
    private float[] _db = Array.Empty<float>();
    // Historique circulaire : MaxLines lignes de _points valeurs (0..1).
    private float[][] _history = Array.Empty<float[]>();
    private int _head, _filled;
    private double _carry;
    private int _quietLines = MaxLines;

    private readonly SKPaint _line = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Miter };
    private readonly SKPath _path = new();
    private readonly SKPath _shape = new();

    public WaterfallModule() : base("waterfall") { }

    private int Lines => Math.Clamp(OptI("lines"), 6, MaxLines);

    public override bool IsIdle => _quietLines >= Lines;

    protected override void OnResize()
    {
        // Un point tous les 2 pixels techniques : les crêtes restent fines.
        int points = Math.Max(16, Width / (2 * Px));
        _db = new float[points];
        _history = new float[MaxLines][];
        for (int i = 0; i < MaxLines; i++) _history[i] = new float[points];
        _filled = 0;
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        if (_db.Length == 0) return;
        _carry += dt * Opt("speed");
        if (_carry < 1) return;
        _carry -= Math.Floor(_carry);

        _mapper.Ensure(_db.Length, 40, 14000, audio.SampleRate, audio.FftSize, 3);
        _mapper.MapDb(audio.SpectrumDb, _db, smooth: true);
        _head = (_head + 1) % MaxLines;
        var line = _history[_head];
        float floor = OptF("floor"), range = 60;
        bool quiet = true;
        for (int i = 0; i < line.Length; i++)
        {
            // Bords amortis : les crêtes montent au centre, comme sur la pochette.
            float edge = MathF.Sin(MathF.PI * (i + 0.5f) / line.Length);
            float v = Math.Clamp((_db[i] - floor) / range, 0, 1);
            line[i] = v * v * (0.35f + 0.65f * edge);
            if (line[i] > 0.02f) quiet = false;
        }
        _filled = Math.Min(MaxLines, _filled + 1);
        _quietLines = quiet ? _quietLines + 1 : 0;
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        int lines = Math.Min(Lines, _filled);
        if (lines == 0 || _db.Length == 0) return;
        bool perspective = Flag("perspective");
        float inset = MathF.Ceiling(Math.Max(p, ctx.EdgeInset * 0.5f));
        float left = rect.Left + inset, width = rect.Width - 2 * inset;
        // Les lignes occupent le bas ; la hauteur des crêtes déborde vers le haut sur les lignes de derrière.
        float spacing = Math.Max(p, MathF.Floor(rect.Height * 0.62f / Lines));
        float front = rect.Bottom - p;
        float peak = rect.Height * 0.55f;
        _line.StrokeWidth = p;

        // De l'avant vers l'arrière : chaque ligne tracée retire de la zone de dessin ce qu'elle cache
        // (sans effacer le fond, ce qui trouerait le fond LCD).
        canvas.Save();
        for (int age = 0; age < lines; age++)
        {
            var values = _history[(_head - age + MaxLines) % MaxLines];
            float depth = age / (float)Lines;
            float shrink = perspective ? 1 - 0.38f * depth : 1;
            float lw = width * shrink, lx = left + (width - lw) / 2;
            float baseY = MathF.Round(front - age * spacing);
            float amp = peak * (perspective ? 1 - 0.3f * depth : 1);
            int n = values.Length;

            _path.Rewind();
            for (int i = 0; i < n; i++)
            {
                float x = MathF.Round(lx + lw * i / (n - 1));
                float y = MathF.Round(baseY - values[i] * amp);
                if (i == 0) _path.MoveTo(x, y);
                else _path.LineTo(x, y);
            }

            // Cache des lignes de derrière : tout ce qui est sous la crête, jusqu'au bas du module.
            _shape.Rewind();
            _shape.AddPath(_path);
            _shape.LineTo(MathF.Round(lx + lw), rect.Bottom);
            _shape.LineTo(MathF.Round(lx), rect.Bottom);
            _shape.Close();
            // Devant : vive et claire ; derrière : plus sombre, comme dans la brume.
            float tone = 0.35f + 0.65f * (1 - depth);
            _line.Color = t.Spectral(tone).WithAlpha((byte)(255 * (0.35f + 0.65f * (1 - depth))));
            canvas.DrawPath(_path, _line);
            canvas.ClipPath(_shape, SKClipOperation.Difference);
        }
        canvas.Restore();
        ctx.ApplyScanlines(canvas, rect);
    }

    public override void Dispose()
    {
        _line.Dispose();
        _path.Dispose();
        _shape.Dispose();
    }
}
