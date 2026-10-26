using System.Globalization;
using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Crête-mètre L / R horizontal à segments (style LED), graduations en dB,
/// barre de sonie avec repère de cible et lecture LUFS dans une case façon afficheur.
/// </summary>
internal sealed class LevelsModule : VisualizerModule
{
    private float _dispL = -200, _dispR = -200, _holdL = -200, _holdR = -200;
    private double _holdTimerL, _holdTimerR, _clipTimer, _textTimer;
    private float _lufs = -200;
    private string _text = "--.-";

    private readonly LayerCache _static = new();
    private readonly LabelCache _value = new();
    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _staticPaint = new() { IsAntialias = false };

    public LevelsModule() : base("levels") { }

    public override bool IsIdle
    {
        get
        {
            float floor = -OptF("range") - 1;
            return _dispL < floor && _dispR < floor && _holdL < floor && _holdR < floor && _clipTimer <= 0 && _lufs < -70;
        }
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        float release = OptF("release");
        double hold = Opt("hold");
        Step(ref _dispL, ref _holdL, ref _holdTimerL, ToDb(audio.PeakL), dt, release, hold);
        Step(ref _dispR, ref _holdR, ref _holdTimerR, ToDb(audio.PeakR), dt, release, hold);

        if (Math.Max(audio.PeakL, audio.PeakR) >= 0.9995f) _clipTimer = 2.0;
        else _clipTimer -= dt;

        _lufs = (float)(OptI("lufsMode") == 0 ? audio.Loudness.ShortTerm : audio.Loudness.Momentary);
        _textTimer -= dt;
        if (_textTimer <= 0)
        {
            _textTimer = 0.15;
            _text = _lufs < -70 ? "--.-" : _lufs.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    private static void Step(ref float disp, ref float hold, ref double timer, float db, double dt, float release, double holdTime)
    {
        disp = db >= disp ? db : Math.Max(db, disp - release * (float)dt);
        if (db >= hold)
        {
            hold = db;
            timer = holdTime;
        }
        else
        {
            timer -= dt;
            if (timer <= 0) hold = Math.Max(disp, hold - release * (float)dt);
        }
    }

    /// <summary>Géométrie commune au calque statique et au dynamique.</summary>
    private readonly record struct Layout(
        float LettersX, float BarsLeft, float BarsRight, float BarH, float YL, float YR,
        float LufsH, float YLufs, float YScale, bool ScaleRow, bool Text, float BoxX, float BoxY, float BoxW, float BoxH,
        bool Caption, int Segments, float Step, float SegW);

    private Layout Compute(SKRect r, RenderContext ctx)
    {
        int p = ctx.P;
        float cap = ctx.Labels.CapHeight;
        bool scales = ctx.ShowScales;
        bool text = Flag("showText");
        float inset = MathF.Ceiling(Math.Max(p, ctx.EdgeInset));

        var box = LabelCache.Size(ctx, "-00.0", 2 * p, 2 * p);
        float textRight = r.Right - inset;
        float boxX = MathF.Round(textRight - box.Width);

        float lettersX = MathF.Round(r.Left + inset);
        float barsLeft = lettersX + (scales ? ctx.Labels.Measure("L") + 2 * p : 0);
        float barsRight = text ? boxX - 3 * p : textRight;
        if (barsRight - barsLeft < 16 * p)
        {
            text = false;
            barsRight = textRight;
        }

        float h = r.Height;
        float barH = Math.Clamp(MathF.Round(h * 0.2f), 3 * p, 6 * p);
        float gap = Math.Max(p, MathF.Round(h * 0.06f));
        float lufsH = Flag("showLufsBar") ? Math.Max(p, MathF.Round(barH * 0.4f)) : 0;
        float block = barH * 2 + gap + (lufsH > 0 ? gap + lufsH : 0);
        bool scaleRow = scales && block + gap + cap <= h - 2 * p;
        if (scaleRow) block += gap + cap;

        float top = MathF.Round(r.MidY - block / 2);
        float yL = top, yR = yL + barH + gap;
        float yLufs = yR + barH + gap;
        float yScale = (lufsH > 0 ? yLufs + lufsH : yR + barH) + gap;

        float boxY = MathF.Round(r.MidY - box.Height / 2);
        bool caption = text && scales && boxY - r.Top >= cap + p;
        if (caption) boxY = MathF.Round(r.MidY - (box.Height - cap - p) / 2);

        float step = 3 * p, segW = 2 * p;
        int segments = Math.Max(1, (int)((barsRight - barsLeft + p) / step));
        if (!Flag("segments")) { step = (barsRight - barsLeft) / segments; segW = step; }

        return new Layout(lettersX, barsLeft, barsRight, barH, yL, yR, lufsH, yLufs, yScale, scaleRow, text,
            boxX, boxY, box.Width, box.Height, caption, segments, step, segW);
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float range = OptF("range");
        var L = Compute(rect, ctx);
        bool segmented = Flag("segments");

        // Partie statique : lettres, segments éteints, graduations, cible LUFS, légende.
        var key = (t.Version, ctx.Labels.Key, rect.Width, rect.Height, rect.Left, range, segmented,
            Flag("showText"), Flag("showLufsBar"), OptF("target"), ctx.ShowScales, ctx.EdgeInset);
        if (_static.Begin((int)rect.Width, (int)rect.Height, key) is { } c)
        {
            c.Translate(-rect.Left, -rect.Top);
            DrawStatic(c, rect, ctx, L, range);
        }
        _static.Draw(canvas, rect.Left, rect.Top);

        // Segments allumés.
        DrawBar(canvas, L, L.YL, _dispL, _holdL, range, t);
        DrawBar(canvas, L, L.YR, _dispR, _holdR, range, t);

        if (L.LufsH > 0)
        {
            float x = X(L, _lufs, range);
            float width = L.BarsRight - L.BarsLeft;
            float chunk = 3 * p;
            for (float cx = L.BarsLeft; cx < x; cx += chunk)
            {
                _fill.Color = t.Spectral((cx - L.BarsLeft) / width);
                canvas.DrawRect(cx, L.YLufs, Math.Min(chunk, x - cx), L.LufsH, _fill);
            }
        }

        if (L.Text)
        {
            _fill.Color = t.ReadoutBack;
            canvas.DrawRect(L.BoxX, L.BoxY, L.BoxW, L.BoxH, _fill);
            float textW = ctx.Labels.Measure(_text);
            _value.Draw(canvas, ctx, _text, L.BoxX + L.BoxW - 2 * p - textW, L.BoxY + 2 * p, t.ReadoutText);
        }
    }

    private static float X(in Layout l, float db, float range) =>
        l.BarsLeft + Math.Clamp((db + range) / range, 0, 1) * (l.BarsRight - l.BarsLeft);

    private void DrawStatic(SKCanvas c, SKRect r, RenderContext ctx, Layout L, float range)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float cap = ctx.Labels.CapHeight;

        if (ctx.ShowScales)
        {
            ctx.Labels.Draw(c, "L", L.LettersX, MathF.Round(L.YL + (L.BarH - cap) / 2), t.InkDim);
            ctx.Labels.Draw(c, "R", L.LettersX, MathF.Round(L.YR + (L.BarH - cap) / 2), t.InkDim);
        }

        _staticPaint.Color = t.Grid;
        for (int i = 0; i < L.Segments; i++)
        {
            float x = MathF.Round(L.BarsLeft + i * L.Step);
            c.DrawRect(x, L.YL, L.SegW, L.BarH, _staticPaint);
            c.DrawRect(x, L.YR, L.SegW, L.BarH, _staticPaint);
        }

        if (L.LufsH > 0)
        {
            _staticPaint.Color = t.GridFaint;
            c.DrawRect(L.BarsLeft, L.YLufs, L.BarsRight - L.BarsLeft, L.LufsH, _staticPaint);
            _staticPaint.Color = t.Ink;
            float tx = MathF.Round(X(L, OptF("target"), range));
            c.DrawRect(tx, L.YLufs - p, p, L.LufsH + 2 * p, _staticPaint);
        }

        if (L.ScaleRow)
        {
            // Graduations : les valeurs qui ne se chevauchent pas, de la plus forte à la plus faible.
            var marks = new[] { 0f, -6f, -12f, -24f, -range / 2, -range }.Distinct().Where(m => m >= -range).ToArray();
            float rightLimit = L.BarsRight + p;
            var drawn = new List<(float A, float B)>();
            foreach (var m in marks)
            {
                string label = ((int)Math.Abs(m)).ToString(CultureInfo.InvariantCulture);
                float w = ctx.Labels.Measure(label);
                float x = MathF.Round(Math.Clamp(X(L, m, range) - w / 2, L.BarsLeft, rightLimit - w));
                if (drawn.Any(d => x < d.B + 2 * p && x + w > d.A - 2 * p)) continue;
                drawn.Add((x, x + w));
                ctx.Labels.Draw(c, label, x, L.YScale, m >= -0.5f ? t.Hot.WithAlpha(0xC0) : t.InkDim);
            }
        }

        if (L.Caption) ctx.Labels.Draw(c, "LUFS", L.BoxX, L.BoxY - cap - p, t.InkDim);
    }

    private void DrawBar(SKCanvas canvas, in Layout L, float y, float level, float hold, float range, Theme t)
    {
        float frac = Math.Clamp((level + range) / range, 0, 1);
        int lit = (int)MathF.Ceiling(frac * L.Segments - 0.001f);
        int holdSeg = hold > -range ? Math.Min(L.Segments - 1, (int)(Math.Clamp((hold + range) / range, 0, 1) * L.Segments)) : -1;
        int clipFrom = _clipTimer > 0 ? Math.Max(0, L.Segments - 2) : int.MaxValue;

        for (int i = 0; i < L.Segments; i++)
        {
            bool on = i < lit;
            if (!on && i != holdSeg && i < clipFrom) continue;
            float pos = (i + 0.5f) / L.Segments;
            float db = -range + pos * range;
            // Dégradé de la palette sur toute la barre ; rouge seulement tout près de 0 dB.
            _fill.Color = i >= clipFrom ? t.Hot
                : i == holdSeg && !on ? t.Ink
                : db > -1.5f ? t.Hot : t.Spectral(pos);
            canvas.DrawRect(MathF.Round(L.BarsLeft + i * L.Step), y, L.SegW, L.BarH, _fill);
        }
    }

    public override void Dispose()
    {
        _static.Dispose();
        _value.Dispose();
        _fill.Dispose();
        _staticPaint.Dispose();
    }
}
