using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// VU-mètre analogique adapté à une barre très basse : le pivot de l'aiguille est placé
/// sous le module, on ne voit que le haut d'un grand arc aplati. Balistique VU (≈300 ms).
/// </summary>
internal sealed class VuMeterModule : VisualizerModule
{
    private static readonly float[] Major = { -20, -10, -5, -3, 0, 3 };
    private static readonly float[] Minor = { -7, -2, -1, 1, 2 };
    private static readonly float MaxLinear = MathF.Pow(10, 3f / 20f);

    // Système masse-ressort amorti (ζ ≈ 0.8, léger dépassement comme un vrai galvanomètre).
    private const float Omega = 16f;
    private const float Damping = 0.8f;

    private float _posL, _velL, _posR, _velR;
    private double _ledTimer;

    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt };
    private readonly SKPaint _needle = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPath _arc = new();
    private readonly LayerCache _scale = new();

    public VuMeterModule() : base("vu") { }

    public override bool IsIdle =>
        _posL < 0.002f && _posR < 0.002f && Math.Abs(_velL) < 0.002f && Math.Abs(_velR) < 0.002f && _ledTimer <= 0;

    private static float Position(float vu) => Math.Clamp(MathF.Pow(10, vu / 20f) / MaxLinear, 0f, 1.06f);

    public override void Update(AudioAnalyzer audio, double dt)
    {
        float reference = OptF("reference");
        bool stereo = OptI("mode") == 1;
        float rmsL = audio.RmsL, rmsR = audio.RmsR;
        if (!stereo) rmsL = rmsR = MathF.Sqrt((rmsL * rmsL + rmsR * rmsR) * 0.5f);

        // RMS + 3 dB : un sinus à la référence affiche 0 VU.
        float vuL = ToDb(rmsL) + 3.01f - reference;
        float vuR = ToDb(rmsR) + 3.01f - reference;
        float targetL = rmsL < 1e-6f ? 0 : Position(vuL);
        float targetR = rmsR < 1e-6f ? 0 : Position(vuR);

        if (Math.Max(vuL, vuR) > 3f) _ledTimer = 0.6;
        else _ledTimer -= dt;

        // Intégration par petits pas pour rester stable quel que soit le FPS.
        float remaining = (float)dt;
        while (remaining > 0)
        {
            float h = Math.Min(remaining, 0.002f);
            Integrate(ref _posL, ref _velL, targetL, h);
            Integrate(ref _posR, ref _velR, targetR, h);
            remaining -= h;
        }
    }

    private static void Integrate(ref float pos, ref float vel, float target, float h)
    {
        float acc = Omega * Omega * (target - pos) - 2 * Damping * Omega * vel;
        vel += acc * h;
        pos += vel * h;
        if (pos < 0) { pos = 0; vel = Math.Max(0, vel); }
        if (pos > 1.08f) { pos = 1.08f; vel = Math.Min(0, vel); }
    }

    private readonly record struct Geometry(float Cx, float PivotY, float Radius, float ThetaMax, float Band, float Tick, float Top);

    private static Geometry Compute(SKRect r, RenderContext ctx, bool labels)
    {
        int p = ctx.P;
        float h = r.Height;
        float top = r.Top + p;
        float labelH = labels ? ctx.Labels.CapHeight + 2 * p : 0;
        float band = Math.Max(2 * p, MathF.Round(h * 0.09f));
        float tick = Math.Max(2 * p, MathF.Round(h * 0.1f));
        float apex = top + labelH + tick + band / 2;
        float sagitta = Math.Max(2 * p, (r.Bottom - apex) * 0.55f);
        float chord = r.Width * 0.84f;
        float radius = (chord * chord / 4 + sagitta * sagitta) / (2 * sagitta);
        float theta = MathF.Asin(Math.Min(1f, chord / 2 / radius));
        return new Geometry(r.MidX, apex + radius, radius, theta, band, tick, top);
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        bool labels = ctx.ShowScales && rect.Height >= 20 * p;
        var g = Compute(rect, ctx, labels);

        if (OptI("style") == 0)
        {
            RenderLed(canvas, rect, ctx, g, labels);
            DrawPeakLed(canvas, rect, ctx);
            return;
        }

        var key = (t.Version, ctx.Labels.Key, labels, Flag("percent"), ctx.ShowScales, rect.Width, rect.Height);
        if (_scale.Begin((int)rect.Width, (int)rect.Height, key) is { } c)
        {
            c.Translate(-rect.Left, -rect.Top);
            DrawScale(c, rect, ctx, g, labels);
        }
        _scale.Draw(canvas, rect.Left, rect.Top);

        // Aiguille(s) : du pivot (hors champ, sous le module) jusqu'au-dessus de la graduation.
        bool stereo = OptI("mode") == 1;
        float length = g.Radius + g.Band / 2 + g.Tick;
        _needle.StrokeWidth = Math.Max(1f, 1.4f * ctx.Scale);
        var needle = OptColor("needle");
        DrawNeedle(canvas, g, Angle(g, _posL), length, stereo ? t.Ink : needle);
        if (stereo) DrawNeedle(canvas, g, Angle(g, _posR), length, needle);
        DrawPeakLed(canvas, rect, ctx);
    }

    private void DrawPeakLed(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        if (!Flag("led")) return;
        int p = ctx.P;
        float s = 3 * p;
        _fill.Color = _ledTimer > 0 ? ctx.Theme.Hot : ctx.Theme.Grid;
        canvas.DrawRect(MathF.Round(rect.Right - Math.Max(p, ctx.EdgeInset) - s), MathF.Round(rect.Bottom - s - p), s, s, _fill);
    }

    // ------------------------------------------------------------------ Style LED

    /// <summary>Segments de l'arc LED : un ou deux anneaux (stéréo), même géométrie que le style analogique.</summary>
    private readonly record struct LedRing(float Radius, float Thickness);

    private LedRing[] LedRings(in Geometry g, int p)
    {
        float thick = Math.Max(3 * p, g.Band * 1.7f);
        if (OptI("mode") == 0) return new[] { new LedRing(g.Radius, thick) };
        float t2 = Math.Max(2 * p, MathF.Round(thick * 0.6f));
        return new[] { new LedRing(g.Radius + t2 / 2 + p / 2f, t2), new LedRing(g.Radius - t2 / 2 - p / 2f, t2) };
    }

    private int LedCount(in Geometry g, int p) =>
        Math.Clamp((int)(2 * g.ThetaMax * g.Radius / (4 * p)), 10, 48);

    private void RenderLed(SKCanvas canvas, SKRect rect, RenderContext ctx, Geometry g, bool labels)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        var rings = LedRings(g, p);
        int count = LedCount(g, p);
        float zero = Position(0);

        // Partie statique : segments éteints, graduations chiffrées, légende.
        var key = (t.Version, ctx.Labels.Key, labels, OptI("mode"), rect.Width, rect.Height, "led");
        if (_scale.Begin((int)rect.Width, (int)rect.Height, key) is { } c)
        {
            c.Translate(-rect.Left, -rect.Top);
            foreach (var ring in rings)
                for (int i = 0; i < count; i++)
                {
                    float mid = (i + 0.5f) / count;
                    DrawSegment(c, g, ring, i, count, mid > zero ? t.Hot.WithAlpha(0x40) : t.Grid, p);
                }
            DrawLabels(c, rect, ctx, g, labels, rings[0].Radius + rings[0].Thickness / 2);
        }
        _scale.Draw(canvas, rect.Left, rect.Top);

        // Segments allumés : traînée atténuée jusqu'à la tête, tête pleine intensité (rouge au-delà de 0 VU).
        float[] positions = { _posL, _posR };
        for (int r = 0; r < rings.Length; r++)
        {
            float pos = Math.Clamp(positions[r], 0, 1);
            if (pos <= 0.001f) continue;
            int head = Math.Min(count - 1, (int)(pos * count));
            for (int i = 0; i <= head; i++)
            {
                float mid = (i + 0.5f) / count;
                int behind = head - i;
                byte alpha = behind switch { 0 => 255, 1 => 0xD0, 2 => 0xA0, _ => 0x60 };
                var color = mid > zero ? t.Hot : t.Spectral(mid);
                DrawSegment(canvas, g, rings[r], i, count, color.WithAlpha(alpha), p);
            }
        }
    }

    private void DrawSegment(SKCanvas c, in Geometry g, LedRing ring, int index, int count, SKColor color, int p)
    {
        float Deg(float a) => a * 180f / MathF.PI - 90f;
        float a0 = Angle(g, index / (float)count), a1 = Angle(g, (index + 1) / (float)count);
        float gap = p / ring.Radius; // 1 px d'espace entre deux segments
        var oval = new SKRect(g.Cx - ring.Radius, g.PivotY - ring.Radius, g.Cx + ring.Radius, g.PivotY + ring.Radius);
        _stroke.StrokeWidth = ring.Thickness;
        _stroke.Color = color;
        _arc.Rewind();
        _arc.AddArc(oval, Deg(a0 + gap / 2), (a1 - a0 - gap) * 180f / MathF.PI);
        c.DrawPath(_arc, _stroke);
    }

    /// <summary>Chiffres au-dessus de l'arc et légende « VU » (communs aux deux styles).</summary>
    private void DrawLabels(SKCanvas c, SKRect r, RenderContext ctx, Geometry g, bool labels, float outer)
    {
        if (!labels) return;
        var t = ctx.Theme;
        int p = ctx.P;
        var drawn = new List<(float A, float B)>();
        foreach (var v in Major)
        {
            var tip = At(g, Angle(g, Position(v)), outer + g.Tick);
            string label = v > 0 ? $"+{v:0}" : $"{Math.Abs(v):0}";
            float w = ctx.Labels.Measure(label);
            float x = MathF.Round(tip.X - w / 2);
            if (drawn.Any(d => x < d.B + p && x + w > d.A - p)) continue;
            drawn.Add((x, x + w));
            ctx.Labels.Draw(c, label, x, MathF.Round(g.Top), v > 0 ? t.Hot : t.InkDim);
        }

        float vw = ctx.Labels.Measure("VU");
        float y = r.Bottom - ctx.Labels.CapHeight - p;
        if (y > g.PivotY - g.Radius + g.Band + 2 * p)
            ctx.Labels.Draw(c, "VU", MathF.Round(g.Cx - vw / 2), MathF.Round(y), t.InkDim);
    }

    private static float Angle(in Geometry g, float pos) => -g.ThetaMax + 2 * g.ThetaMax * pos;

    private static SKPoint At(in Geometry g, float angle, float rho) =>
        new(g.Cx + rho * MathF.Sin(angle), g.PivotY - rho * MathF.Cos(angle));

    private void DrawScale(SKCanvas c, SKRect r, RenderContext ctx, Geometry g, bool labels)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float Deg(float a) => a * 180f / MathF.PI - 90f;
        var arcRect = new SKRect(g.Cx - g.Radius, g.PivotY - g.Radius, g.Cx + g.Radius, g.PivotY + g.Radius);
        float a0 = Angle(g, 0), aZero = Angle(g, Position(0)), a1 = Angle(g, 1);

        // Bande épaisse : gris jusqu'à 0 VU, rouge au-delà.
        _stroke.StrokeWidth = g.Band;
        _stroke.Color = t.Ink.WithAlpha(0x6A);
        _arc.Rewind();
        _arc.AddArc(arcRect, Deg(a0), Deg(aZero) - Deg(a0));
        c.DrawPath(_arc, _stroke);
        _stroke.Color = t.Hot;
        _arc.Rewind();
        _arc.AddArc(arcRect, Deg(aZero), Deg(a1) - Deg(aZero));
        c.DrawPath(_arc, _stroke);

        // Filet intérieur.
        float inner = g.Radius - g.Band / 2 - 2 * p;
        _stroke.StrokeWidth = p;
        _stroke.Color = t.Grid;
        _arc.Rewind();
        _arc.AddArc(new SKRect(g.Cx - inner, g.PivotY - inner, g.Cx + inner, g.PivotY + inner), Deg(a0), Deg(a1) - Deg(a0));
        c.DrawPath(_arc, _stroke);

        // Graduations.
        float outer = g.Radius + g.Band / 2;
        _stroke.StrokeWidth = p;
        foreach (var v in Minor)
        {
            float a = Angle(g, Position(v));
            _stroke.Color = v > 0 ? t.Hot.WithAlpha(0xC0) : t.InkDim;
            c.DrawLine(At(g, a, outer), At(g, a, outer + g.Tick * 0.55f), _stroke);
        }
        var drawn = new List<(float A, float B)>();
        foreach (var v in Major)
        {
            float a = Angle(g, Position(v));
            _stroke.Color = v > 0 ? t.Hot : t.Ink.WithAlpha(0xC8);
            var tip = At(g, a, outer + g.Tick);
            c.DrawLine(At(g, a, g.Radius - g.Band / 2), tip, _stroke);

            if (!labels) continue;
            string label = v > 0 ? $"+{v:0}" : $"{Math.Abs(v):0}";
            float w = ctx.Labels.Measure(label);
            float x = MathF.Round(tip.X - w / 2);
            if (drawn.Any(d => x < d.B + p && x + w > d.A - p)) continue;
            drawn.Add((x, x + w));
            ctx.Labels.Draw(c, label, x, MathF.Round(g.Top), v > 0 ? t.Hot : t.InkDim);
        }

        // Échelle secondaire en % (points), à l'intérieur de l'arc.
        if (Flag("percent"))
        {
            _fill.Color = t.InkDim;
            float rho = inner - 2 * p;
            for (int pct = 0; pct <= 100; pct += 20)
            {
                var pt = At(g, Angle(g, pct / 100f / MaxLinear), rho);
                c.DrawRect(MathF.Round(pt.X - p / 2f), MathF.Round(pt.Y), p, p, _fill);
            }
        }

        // Légende « VU » sous le sommet de l'arc.
        if (labels)
        {
            float w = ctx.Labels.Measure("VU");
            float y = r.Bottom - ctx.Labels.CapHeight - p;
            if (y > g.PivotY - g.Radius + g.Band + 2 * p)
                ctx.Labels.Draw(c, "VU", MathF.Round(g.Cx - w / 2), MathF.Round(y), t.InkDim);
        }
    }

    private void DrawNeedle(SKCanvas canvas, in Geometry g, float angle, float length, SKColor color)
    {
        _needle.Color = color;
        canvas.DrawLine(new SKPoint(g.Cx, g.PivotY), At(g, angle, length), _needle);
    }

    public override void Dispose()
    {
        _stroke.Dispose();
        _needle.Dispose();
        _fill.Dispose();
        _arc.Dispose();
        _scale.Dispose();
    }
}
