using System.Globalization;
using SkiaSharp;
using TaskbarVisu.Core;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Accordeur en deux modes, appuyé sur l'analyse haute résolution partagée (<see cref="KeyAnalyzer"/>) :
/// - Note : note dominante en grand, fréquence et jauge de justesse ±50 cents ;
/// - Tonalité : tonalité du morceau (avec confiance), sa gamme sur un mini-piano.
/// Les notes jouées en détail (accords) sont l'affaire du module Piano roll.
/// </summary>
internal sealed class TunerModule : VisualizerModule
{
    private const int Ticks = 21; // -50 … +50 par pas de 5 cents
    private static readonly int[] WhiteKeys = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly (int PitchClass, int AfterWhite)[] BlackKeys = { (1, 0), (3, 1), (6, 3), (8, 4), (10, 5) };

    private KeyAnalyzer? _analyzer;

    // Mode note
    private int _pc = -1, _octave;
    private double _cents;
    private float _freq;
    private double _hold;

    private int _loggedKey = -1;

    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly LayerCache _gauge = new();
    private readonly LabelCache _big = new();
    private readonly LabelCache _small = new();
    private readonly LabelCache _small2 = new();
    private readonly LabelCache _left = new();
    private readonly LabelCache _right = new();

    public TunerModule() : base("tuner") { }

    private int Mode => OptI("mode");
    private int Notation => OptI("notation");
    private bool Latin => Notation == 1;

    public override bool IsIdle => Mode != 0 || (_hold <= 0 && _pc < 0);

    public override string? Diagnostics => _analyzer is not { KeyTonic: >= 0 } a
        ? $"tuner key=-- note={_pc}"
        : $"tuner key={Harmony.KeyName(a.KeyTonic, a.KeyMinor, false)}{(a.KeyMinor ? "m" : string.Empty)} " +
          $"conf={a.KeyConfidence:0.00} tuning={a.TuningCents:0.0} pitch={a.PitchHz:0.0} active={a.KeyActive} music={a.MusicPresence:0.00} " +
          $"centre={(a.Centre < 0 ? "--" : Harmony.NoteName(a.Centre, false) + " " + Harmony.ModeName(a.Centre, a.KeyTonic, a.KeyMinor))}";

    public override void Update(AudioAnalyzer audio, double dt)
    {
        _hold -= dt;
        _analyzer = audio.Harmony;
        _analyzer.KeyMemorySeconds = Opt("keyMemory");
        if (!_analyzer.Update(audio, dt)) return;

        UpdateNote(_analyzer);
        LogKeyChanges(_analyzer);
    }

    private void UpdateNote(KeyAnalyzer analyzer)
    {
        if (analyzer.HasPitch)
        {
            var (pc, octave, cents) = Pitch.Analyze(analyzer.PitchHz, OptF("reference"));
            if (pc == _pc && octave == _octave)
            {
                // Même note : on lisse la justesse pour une jauge stable.
                _cents += (cents - _cents) * 0.4;
                _freq += (analyzer.PitchHz - _freq) * 0.4f;
            }
            else
            {
                (_pc, _octave, _cents, _freq) = (pc, octave, cents, analyzer.PitchHz);
            }
            _hold = 1.2;
        }
        else if (_hold <= 0)
        {
            _pc = -1;
        }
    }

    private void LogKeyChanges(KeyAnalyzer analyzer)
    {
        int key = analyzer.KeyTonic < 0 ? -1 : analyzer.KeyTonic + (analyzer.KeyMinor ? 12 : 0);
        if (key == _loggedKey || key < 0) return;
        _loggedKey = key;
        Log.Info($"Accordeur : tonalité {Harmony.KeyName(analyzer.KeyTonic, analyzer.KeyMinor, false)} " +
                 $"{(analyzer.KeyMinor ? "mineur" : "majeur")} ({Harmony.Camelot(analyzer.KeyTonic, analyzer.KeyMinor)}), " +
                 $"confiance {analyzer.KeyConfidence:P0}, accordage {analyzer.TuningCents:+0;-0} cents");
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        switch (Mode)
        {
            case 1:
            {
                // Rien de musical depuis quelques secondes (silence, voix, toux…) : affichage neutre.
                int tonic = _analyzer is { KeyActive: true } a ? a.KeyTonic : -1;
                bool minor = _analyzer?.KeyMinor ?? false;
                bool unsure = _analyzer?.ModeAmbiguous ?? false;
                // Mode incertain (pas de tierce nette) : on garde le meilleur choix, suivi d'un « ? ».
                string quality = (minor ? "MIN" : "MAJ") + (unsure ? "?" : string.Empty);
                string big, small;
                if (tonic < 0)
                {
                    big = "--";
                    small = string.Empty;
                }
                else if (Notation == 2)
                {
                    big = Harmony.Camelot(tonic, minor);
                    small = $"{Harmony.KeyName(tonic, minor, false)} {quality}";
                }
                else
                {
                    big = Harmony.KeyName(tonic, minor, Latin);
                    small = quality;
                }
                // Morceau modal : la note où la musique se pose remplace la confiance, « ◆F MIX » = centré sur Fa, mixolydien.
                int centre = tonic < 0 ? -1 : _analyzer!.Centre;
                // Historique (option) : « <C <AM » = tonalités précédentes du morceau, la plus récente d'abord.
                var history = tonic >= 0 && Flag("history") ? _analyzer!.KeyHistory : Array.Empty<int>();
                string? detail = tonic < 0 ? null
                    : centre >= 0
                        ? $"◆{Harmony.NoteName(centre, Latin, Harmony.KeyUsesFlats(tonic, minor))} {Harmony.ModeName(centre, tonic, minor)}"
                    : history.Count > 0
                        ? string.Join(" ", history.Take(2).Select(k => "<" + Harmony.KeyName(k % 12, k >= 12, Latin) + (k >= 12 ? "ᵐ" : "")))
                        : ((int)Math.Round(_analyzer!.KeyConfidence * 100)).ToString(CultureInfo.InvariantCulture) + "%";
                var scale = tonic < 0 ? Array.Empty<int>() : Harmony.Scale(tonic, minor);
                // Les notes qui changent entre majeur et mineur (tierce, sixte, septième) sont atténuées si le mode est incertain.
                var uncertain = unsure && tonic >= 0
                    ? scale.Except(Harmony.Scale(tonic, !minor)).ToArray()
                    : Array.Empty<int>();
                RenderHarmony(canvas, rect, ctx, big, small, detail, centre >= 0, scale, tonic, centre, _pc, uncertain);
                break;
            }
            default:
                RenderNote(canvas, rect, ctx);
                break;
        }
    }

    // ------------------------------------------------------------------ Mode note

    private void RenderNote(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float inset = MathF.Ceiling(Math.Max(p, ctx.EdgeInset));
        bool active = _pc >= 0;
        bool inTune = active && Math.Abs(_cents) <= OptF("tolerance");
        string note = active ? Harmony.NoteName(_pc, Latin) + _octave.ToString(CultureInfo.InvariantCulture) : "--";

        // Note en grand, à gauche.
        int factor = rect.Height >= 24 * p ? 2 : 1;
        float noteW = Math.Max(ctx.Labels.Measure(Latin ? "SOL#8" : "G#8", factor), ctx.Labels.Measure(note, factor));
        var size = LabelCache.Size(ctx, note, 0, 0, factor);
        float noteX = MathF.Round(rect.Left + inset);
        _big.Draw(canvas, ctx, note, noteX, MathF.Round(rect.MidY - size.Height / 2),
            inTune ? t.Spectral(1f) : active ? t.Ink : t.InkDim, factor: factor);

        // Jauge de justesse à droite.
        float gx0 = noteX + noteW + 4 * p;
        float gx1 = rect.Right - inset;
        if (gx1 - gx0 < Ticks * 2 * p) return;
        float gaugeTop = MathF.Round(rect.Top + ctx.Labels.CapHeight + 3 * p);
        float gaugeH = Math.Max(3 * p, rect.Bottom - p - gaugeTop);
        float step = (gx1 - gx0) / (Ticks - 1);

        var key = (t.Version, ctx.Labels.Key, gx0 - rect.Left, gx1 - rect.Left, gaugeTop, gaugeH, "note");
        _gauge.Draw(canvas, rect.Left, rect.Top, (int)rect.Width, (int)rect.Height, key, c =>
        {
            c.Translate(-rect.Left, -rect.Top);
            for (int i = 0; i < Ticks; i++)
            {
                float h = i % 5 == 0 ? gaugeH : gaugeH * 0.55f;
                _fill.Color = i == Ticks / 2 ? t.InkDim : t.Grid;
                c.DrawRect(MathF.Round(gx0 + i * step), MathF.Round(gaugeTop + (gaugeH - h) / 2), p, h, _fill);
            }
        });

        if (!active) return;

        // Indicateur : segment lumineux à la position des cents, traînée vers le centre.
        float pos = (float)Math.Clamp((_cents + 50) / 100, 0, 1) * (Ticks - 1);
        int idx = (int)MathF.Round(pos);
        int center = Ticks / 2;
        var color = inTune ? t.Spectral(1f) : Math.Abs(_cents) > 25 ? t.Hot : t.Warn;
        for (int i = Math.Min(idx, center); i <= Math.Max(idx, center); i++)
        {
            _fill.Color = color.WithAlpha(i == idx ? (byte)255 : (byte)0x50);
            float w = i == idx ? 2 * p : p;
            canvas.DrawRect(MathF.Round(gx0 + i * step - (w - p) / 2), gaugeTop, w, gaugeH, _fill);
        }

        // Fréquence à gauche, écart en cents à droite, au-dessus de la jauge.
        var ci = CultureInfo.InvariantCulture;
        string freqText = _freq.ToString(_freq >= 1000 ? "0" : "0.0", ci) + "HZ";
        string centsText = ((int)Math.Round(_cents)).ToString("+0;-0;0", ci) + "C";
        _left.Draw(canvas, ctx, freqText, gx0, rect.Top + p, t.InkDim);
        _right.Draw(canvas, ctx, centsText, gx1 - ctx.Labels.Measure(centsText), rect.Top + p, inTune ? t.Spectral(1f) : t.Ink);
    }

    // ------------------------------------------------------------------ Mode tonalité

    /// <summary>
    /// Nom en grand à gauche, qualité et détail (confiance, ou note centrale si le morceau est modal) en petit
    /// à côté, mini-piano avec les notes allumées à droite.
    /// </summary>
    private void RenderHarmony(SKCanvas canvas, SKRect rect, RenderContext ctx, string big, string small,
        string? detail, bool detailStrong, int[] tones, int highlight, int centre, int currentNote, int[] dimmed)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float inset = MathF.Ceiling(Math.Max(p, ctx.EdgeInset));
        bool active = big != "--";
        int factor = rect.Height >= 24 * p ? 2 : 1;

        float bigW = Math.Max(ctx.Labels.Measure(Latin ? "SOL#" : Notation == 2 ? "12A" : "G#", factor), ctx.Labels.Measure(big, factor));
        float smallW = Math.Max(Math.Max(ctx.Labels.Measure("MAJ?"), ctx.Labels.Measure(small)),
            detail is null ? 0 : ctx.Labels.Measure(detail));
        float x = MathF.Round(rect.Left + inset);
        var bigSize = LabelCache.Size(ctx, big, 0, 0, factor);
        float bigY = MathF.Round(rect.MidY - bigSize.Height / 2);
        _big.Draw(canvas, ctx, big, x, bigY, active ? t.Spectral(1f) : t.InkDim, factor: factor);

        float smallX = x + ctx.Labels.Measure(big, factor) + 2 * p;
        if (active && small.Length > 0) _small.Draw(canvas, ctx, small, smallX, bigY, t.Ink);
        if (active && detail is not null)
        {
            float y2 = bigY + ctx.Labels.CapHeight + 2 * p;
            if (y2 + ctx.Labels.CapHeight <= rect.Bottom) _small2.Draw(canvas, ctx, detail, smallX, y2, detailStrong ? t.Ink : t.InkDim);
        }

        // Mini-piano.
        float px0 = x + bigW + smallW + 5 * p;
        float px1 = rect.Right - inset;
        if (px1 - px0 < 7 * 3 * p) return;
        float top = rect.Top + 2 * p, bottom = rect.Bottom - 2 * p;
        DrawPiano(canvas, ctx, px0, top, px1 - px0, bottom - top, tones, highlight, centre, currentNote, dimmed);
    }

    private void DrawPiano(SKCanvas canvas, RenderContext ctx, float x0, float top, float width, float height,
        int[] lit, int highlight, int centre, int current, int[] dimmed)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float whiteW = MathF.Floor(width / 7);
        float blackW = Math.Max(2 * p, MathF.Round(whiteW * 0.6f));
        float blackH = MathF.Round(height * 0.6f);

        SKColor KeyColor(int pc, bool black)
        {
            if (pc == highlight && lit.Contains(pc)) return t.Spectral(1f);
            if (dimmed.Contains(pc)) return t.Spectral(0.25f + 0.6f * pc / 11f).WithAlpha(0x55);
            if (lit.Contains(pc)) return t.Spectral(0.25f + 0.6f * pc / 11f).WithAlpha(0xE0);
            return black ? new SKColor(0, 0, 0, 0xD8) : t.Grid;
        }

        for (int i = 0; i < 7; i++)
        {
            _fill.Color = KeyColor(WhiteKeys[i], false);
            canvas.DrawRect(MathF.Round(x0 + i * whiteW), top, whiteW - p, height, _fill);
        }
        foreach (var (pc, after) in BlackKeys)
        {
            float bx = MathF.Round(x0 + (after + 1) * whiteW - blackW / 2 - p / 2f);
            _fill.Color = KeyColor(pc, true);
            canvas.DrawRect(bx, top, blackW, blackH, _fill);
            if (!lit.Contains(pc))
            {
                // Liseré pour distinguer les touches noires éteintes sur fond sombre.
                _fill.Color = t.Grid;
                canvas.DrawRect(bx, top + blackH - p, blackW, p, _fill);
            }
        }

        // Note centrale d'un morceau modal : curseur au-dessus de la touche, dans la marge du haut.
        if (centre >= 0)
        {
            int whiteIndex = Array.IndexOf(WhiteKeys, centre);
            float cx, cw;
            if (whiteIndex >= 0) (cx, cw) = (MathF.Round(x0 + whiteIndex * whiteW), whiteW - p);
            else (cx, cw) = (MathF.Round(x0 + (BlackKeys.First(b => b.PitchClass == centre).AfterWhite + 1) * whiteW - blackW / 2 - p / 2f), blackW);
            _fill.Color = t.Ink;
            canvas.DrawRect(cx, top - 2 * p, cw, p, _fill);
        }

        // Repère de la note jouée en ce moment (point sous la touche).
        if (current >= 0)
        {
            float cx;
            int whiteIndex = Array.IndexOf(WhiteKeys, current);
            if (whiteIndex >= 0) cx = x0 + whiteIndex * whiteW + (whiteW - p) / 2;
            else cx = x0 + (BlackKeys.First(b => b.PitchClass == current).AfterWhite + 1) * whiteW - p / 2f;
            float dotY = whiteIndex < 0 ? top + blackH - 3 * p : top + height - 3 * p;
            _fill.Color = t.Ink;
            canvas.DrawRect(MathF.Round(cx - p), MathF.Round(dotY), 2 * p, 2 * p, _fill);
        }
    }

    public override void Dispose()
    {
        _fill.Dispose();
        _gauge.Dispose();
        _big.Dispose();
        _small.Dispose();
        _small2.Dispose();
        _left.Dispose();
        _right.Dispose();
    }
}
