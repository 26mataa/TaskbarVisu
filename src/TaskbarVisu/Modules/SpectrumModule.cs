using System.Globalization;
using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Analyseur de spectre logarithmique. Style par défaut « relief » : une bande verticale par colonne,
/// colorée selon son propre niveau, contour fin, spectre des côtés en filigrane et lecture du pic
/// (niveau, fréquence, note). Autres styles : barres, points LED, courbe remplie, ligne.
/// </summary>
internal sealed class SpectrumModule : VisualizerModule
{
    private const int Relief = 0;
    private static readonly (float F, string Label)[] GridMajor = { (100, "100"), (1000, "1K"), (10000, "10K") };
    private static readonly float[] GridMinor = { 50, 200, 500, 2000, 5000 };

    private readonly SpectrumMapper _mapper = new();
    private float[] _raw = Array.Empty<float>(), _disp = Array.Empty<float>(), _peak = Array.Empty<float>();
    private float[] _sideBins = Array.Empty<float>(), _sideRaw = Array.Empty<float>(), _side = Array.Empty<float>();
    private int _columns;

    // Lecture du pic : variantes de la plus complète à la plus courte, choisie au rendu selon la place.
    private readonly string[] _readout = { string.Empty, string.Empty, string.Empty };
    private float _readoutFreq;
    private double _noteTimer;

    private readonly SKPath _path = new();
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _bar = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _grid = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _fade = new() { IsAntialias = false, BlendMode = SKBlendMode.DstIn };
    private readonly LayerCache _gridLayer = new();
    private readonly LabelCache _note = new();
    private SKShader? _shader, _fadeShader;
    private object? _shaderKey, _fadeKey;

    public SpectrumModule() : base("spectrum") { }

    private int Style => OptI("style");
    private bool Discrete => Style is 1 or 2;

    public override bool IsIdle
    {
        get
        {
            float floor = OptF("floor");
            for (int i = 0; i < _columns; i++)
                if (_disp[i] > floor || _peak[i] > floor || _side[i] > floor) return false;
            return true;
        }
    }

    protected override void OnConfigure() => Allocate();
    protected override void OnResize() => Allocate();

    private void Allocate()
    {
        if (Width <= 1) return;
        // Relief : une colonne par « pixel » ; barres : 2 px + 1 px d'espace ; courbe : un point tous les ~2,5 px.
        int count = Style == Relief ? Math.Max(16, Width / Px)
            : Discrete ? Math.Max(8, (Width + Px) / (3 * Px))
            : Math.Max(16, (int)(Width / (2.5f * Scale)));
        if (count == _columns) return;
        _columns = count;
        _raw = new float[count];
        _sideRaw = new float[count];
        _disp = Enumerable.Repeat(-200f, count).ToArray();
        _peak = Enumerable.Repeat(-200f, count).ToArray();
        _side = Enumerable.Repeat(-200f, count).ToArray();
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        if (_columns == 0) return;
        bool relief = Style == Relief;
        _mapper.Ensure(_columns, OptF("minFreq"), OptF("maxFreq"), audio.SampleRate, audio.FftSize, OptF("tilt"));
        _mapper.MapDb(audio.SpectrumDb, _raw, smooth: relief);

        float fall = OptF("release") * (float)dt;
        float peakFall = fall * 0.35f;
        // Relief : montée très brève (~20 ms) pour calmer le scintillement des aigus détaillés.
        float rise = relief ? 1f - MathF.Exp(-(float)dt / 0.02f) : 1f;
        for (int i = 0; i < _columns; i++)
        {
            float r = _raw[i];
            _disp[i] = r >= _disp[i] ? _disp[i] + (r - _disp[i]) * rise : Math.Max(r, _disp[i] - fall);
            _peak[i] = r >= _peak[i] ? r : Math.Max(r, _peak[i] - peakFall);
        }

        if (relief && Flag("side"))
        {
            var sidePow = audio.SidePower;
            if (_sideBins.Length != sidePow.Length) _sideBins = new float[sidePow.Length];
            for (int k = 0; k < sidePow.Length; k++) _sideBins[k] = 10f * MathF.Log10(sidePow[k] + 1e-20f);
            _mapper.MapDb(_sideBins, _sideRaw, smooth: true);
            for (int i = 0; i < _columns; i++)
            {
                float r = _sideRaw[i];
                _side[i] = r >= _side[i] ? _side[i] + (r - _side[i]) * rise : Math.Max(r, _side[i] - fall);
            }
        }
        else if (_side[0] > -200f) Array.Fill(_side, -200f);

        _noteTimer -= dt;
        if (_noteTimer <= 0)
        {
            _noteTimer = 0.12;
            BuildReadout(audio);
        }
    }

    private void BuildReadout(AudioAnalyzer audio)
    {
        Array.Fill(_readout, string.Empty);
        if (!Flag("note") || audio.SilentSeconds > 0.2) return;
        if (!Pitch.FindPeak(audio.SpectrumDb, audio.BinHz, 25, 5000, out float freq, out float level) || level < -72)
            return;

        var (note, cents) = Pitch.ToNote(freq);
        var ci = CultureInfo.InvariantCulture;
        string c = cents.ToString("+0;-0;+0", ci);
        string hz = freq.ToString(freq < 100 ? "0.00" : freq < 1000 ? "0.0" : "0", ci);
        _readoutFreq = freq;
        if (Style == Relief)
        {
            _readout[0] = $"{level.ToString("0.0", ci)}DB | {hz}HZ | {note} {c}";
            _readout[1] = $"{freq.ToString("0.0", ci)}HZ {note}{c}";
        }
        else
        {
            _readout[0] = $"{freq.ToString("0.0", ci)}HZ {note} {c}";
        }
        _readout[2] = $"{note}{c}";
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        if (_columns == 0) return;
        var t = ctx.Theme;
        int p = ctx.P;
        float floor = OptF("floor"), ceiling = Math.Max(floor + 6, OptF("ceiling"));
        float minF = OptF("minFreq"), maxF = Math.Max(minF * 2, OptF("maxFreq"));
        float h = rect.Height, bottom = rect.Bottom;
        int style = Style;

        var axis = new LevelAxis(floor, ceiling, bottom, h);

        var box = LayoutReadout(rect, ctx, minF, maxF, out string readout, out float peakX);
        if (Flag("grid")) DrawGrid(canvas, rect, ctx, minF, maxF, box);

        if (style == Relief)
        {
            DrawRelief(canvas, rect, ctx, axis);
        }
        else if (Discrete)
        {
            float step = 3 * p, barW = 2 * p;
            float x0 = MathF.Round(rect.Left + (rect.Width - (_columns * step - p)) / 2);
            for (int i = 0; i < _columns; i++)
            {
                float top = MathF.Round(axis.Y(_disp[i]));
                float x = x0 + i * step;
                _bar.Color = t.Spectral(i / (float)(_columns - 1)).WithAlpha(0xF0);
                if (bottom - top >= 1)
                {
                    if (style == 1)
                    {
                        canvas.DrawRect(x, top, barW, bottom - top, _bar);
                    }
                    else
                    {
                        // Points LED : la barre est découpée en pastilles de 2 px.
                        for (float y = bottom - barW; y >= top - 0.5f; y -= step)
                            canvas.DrawRect(x, y, barW, barW, _bar);
                    }
                }
            }
            ctx.ApplyScanlines(canvas, rect);

            if (Flag("peaks"))
            {
                _bar.Color = t.Ink;
                for (int i = 0; i < _columns; i++)
                {
                    if (_peak[i] <= floor) continue;
                    canvas.DrawRect(x0 + i * step, MathF.Round(axis.Y(_peak[i])) - p, barW, p, _bar);
                }
            }
        }
        else
        {
            float colW = rect.Width / _columns;
            _path.Rewind();
            if (style == 3) _path.MoveTo(rect.Left, bottom);
            for (int i = 0; i < _columns; i++)
            {
                float x = rect.Left + (i + 0.5f) * colW, y = axis.Y(_disp[i]);
                if (style == 4 && i == 0) _path.MoveTo(x, y);
                else _path.LineTo(x, y);
            }
            if (style == 3)
            {
                _path.LineTo(rect.Right, bottom);
                _path.Close();
                _fill.Shader = FillShader(rect, t);
                canvas.DrawPath(_path, _fill);
                ctx.ApplyScanlines(canvas, rect);
                // Contour net par-dessus le remplissage.
                Polyline(_disp, rect, axis);
            }
            StrokeThin(canvas, 1.1f * ctx.Scale, t.Ink);

            if (Flag("peaks"))
            {
                Polyline(_peak, rect, axis);
                StrokeThin(canvas, 0.8f * ctx.Scale, t.Ink.WithAlpha(0x60));
            }
        }

        if (readout.Length > 0) DrawReadout(canvas, rect, ctx, box, readout, peakX, axis);
    }

    /// <summary>
    /// Une bande par colonne, couleur selon son propre niveau (sombre quand c'est faible, vif sur les pics),
    /// une colonne sur deux un peu plus sombre (texture rayée), fondu vers le bas, contour fin par-dessus.
    /// </summary>
    private void DrawRelief(SKCanvas canvas, SKRect rect, RenderContext ctx, LevelAxis axis)
    {
        var t = ctx.Theme;
        float colW = rect.Width / _columns;
        float bottom = rect.Bottom;

        canvas.SaveLayer(rect, null);
        for (int i = 0; i < _columns; i++)
        {
            float v = axis.Norm(_disp[i]);
            if (v <= 0) continue;
            float x0 = MathF.Round(rect.Left + i * colW), x1 = MathF.Round(rect.Left + (i + 1) * colW);
            float top = MathF.Round(axis.Y(_disp[i]));
            if (bottom - top < 1 || x1 <= x0) continue;
            // Faible : bas de la palette (sombre) ; moyen : milieu ; pics : haut de la palette.
            _bar.Color = t.Spectral(1.09f * v - 0.09f).WithAlpha((i & 1) == 0 ? (byte)0xFF : (byte)0xC0);
            canvas.DrawRect(x0, top, x1 - x0, bottom - top, _bar);
        }
        _fade.Shader = FadeShader(rect);
        canvas.DrawRect(rect, _fade);
        canvas.Restore();
        ctx.ApplyScanlines(canvas, rect);

        // Spectre des côtés (ce qui diffère entre gauche et droite) : ligne fine en filigrane.
        if (Flag("side"))
        {
            Polyline(_side, rect, axis);
            StrokeThin(canvas, 0.8f * ctx.Scale, t.Ink.WithAlpha(0x58));
        }

        if (Flag("peaks"))
        {
            Polyline(_peak, rect, axis);
            StrokeThin(canvas, 0.8f * ctx.Scale, t.Ink.WithAlpha(0x30));
        }

        Polyline(_disp, rect, axis);
        StrokeThin(canvas, ctx.Scale, t.Ink.WithAlpha(0xE8));
    }

    /// <summary>Échelle verticale : niveau en dB vers hauteur dans le module (struct : aucune allocation par image).</summary>
    private readonly struct LevelAxis(float floor, float ceiling, float bottom, float height)
    {
        public float Norm(float db) => Math.Clamp((db - floor) / (ceiling - floor), 0, 1);
        public float Y(float db) => bottom - Norm(db) * height;
    }

    /// <summary>Courbe des valeurs, un point au centre de chaque colonne, dans _path.</summary>
    private void Polyline(float[] values, SKRect rect, LevelAxis axis)
    {
        _path.Rewind();
        float colW = rect.Width / _columns;
        for (int i = 0; i < _columns; i++)
        {
            float x = rect.Left + (i + 0.5f) * colW, y = axis.Y(values[i]);
            if (i == 0) _path.MoveTo(x, y);
            else _path.LineTo(x, y);
        }
    }

    /// <summary>
    /// Trace _path en lignes de 1 px, que Skia dessine dix fois plus vite qu'un trait antialiasé plus épais
    /// (0,01 ms au lieu de 0,12 ms par courbe) ; aux grandes échelles, des lignes décalées d'un pixel vers
    /// le bas rendent l'épaisseur demandée.
    /// </summary>
    private void StrokeThin(SKCanvas canvas, float width, SKColor color)
    {
        _line.StrokeWidth = 1;
        _line.Color = color;
        int passes = Math.Max(1, (int)MathF.Round(width));
        canvas.DrawPath(_path, _line);
        for (int i = 1; i < passes; i++)
        {
            canvas.Translate(0, 1);
            canvas.DrawPath(_path, _line);
        }
        if (passes > 1) canvas.Translate(0, 1 - passes);
    }

    /// <summary>
    /// Place la lecture du pic : la variante la plus complète qui tient (« -7.4DB | 43.71HZ | F1 +2 »,
    /// puis « 43.7HZ F1+2 », puis « F1+2 »). Relief : case à côté du pic ; autres styles : en haut à droite.
    /// </summary>
    private SKRect LayoutReadout(SKRect rect, RenderContext ctx, float minF, float maxF, out string text, out float peakX)
    {
        int p = ctx.P;
        float inset = Math.Max(p, ctx.EdgeInset);
        float room = rect.Width - 2 * inset;
        text = string.Empty;
        peakX = float.NaN;
        SKSize size = default;
        foreach (var candidate in _readout)
        {
            if (candidate.Length == 0) continue;
            size = LabelCache.Size(ctx, candidate, 2 * p, p);
            text = candidate;
            if (size.Width <= room * (Style == Relief ? 0.62f : 1f)) break;
        }
        if (text.Length == 0 || size.Width > room)
        {
            text = string.Empty;
            return SKRect.Empty;
        }

        float bx;
        if (Style == Relief)
        {
            peakX = rect.Left + MathF.Log(_readoutFreq / minF) / MathF.Log(maxF / minF) * rect.Width;
            // À droite du pic, ou à gauche s'il n'y a pas la place.
            bx = peakX + 3 * p;
            if (bx + size.Width > rect.Right - inset) bx = peakX - 3 * p - size.Width;
            bx = Math.Clamp(bx, rect.Left + inset, rect.Right - inset - size.Width);
        }
        else
        {
            bx = Math.Max(rect.Left, rect.Right - inset - size.Width);
        }
        bx = MathF.Round(bx);
        return SKRect.Create(bx, MathF.Round(rect.Top + p), size.Width, size.Height);
    }

    private void DrawReadout(SKCanvas canvas, SKRect rect, RenderContext ctx, SKRect box, string text, float peakX, LevelAxis axis)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        // Repère sur la courbe à la fréquence du pic : petit carré creux.
        if (peakX >= rect.Left && peakX <= rect.Right)
        {
            int col = Math.Clamp((int)((peakX - rect.Left) / rect.Width * _columns), 0, _columns - 1);
            float py = MathF.Round(axis.Y(_disp[col]));
            if (py < rect.Bottom - p)
            {
                _bar.Color = t.Ink;
                float s = 3 * p, x0 = MathF.Round(peakX - s / 2), y0 = Math.Max(rect.Top, py - s / 2);
                canvas.DrawRect(x0, y0, s, p, _bar);
                canvas.DrawRect(x0, y0 + s - p, s, p, _bar);
                canvas.DrawRect(x0, y0, p, s, _bar);
                canvas.DrawRect(x0 + s - p, y0, p, s, _bar);
            }
        }
        _note.Draw(canvas, ctx, text, box.Left, box.Top, t.Ink, t.OverlayBack, 2 * p, p);
    }

    /// <summary>Grille des fréquences (calque en cache) et ses étiquettes, sauf celles que cache la lecture du pic.</summary>
    private void DrawGrid(SKCanvas canvas, SKRect rect, RenderContext ctx, float minF, float maxF, SKRect readout)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float X(float f) => MathF.Round(MathF.Log(f / minF) / MathF.Log(maxF / minF) * rect.Width);
        var key = (t.Version, minF, maxF, rect.Width, rect.Height);
        if (_gridLayer.Begin((int)rect.Width, (int)rect.Height, key) is { } c)
        {
            _grid.Color = t.GridFaint;
            foreach (var f in GridMinor)
                if (f > minF && f < maxF) c.DrawRect(X(f), 0, p, rect.Height, _grid);
            _grid.Color = t.Grid;
            foreach (var (f, _) in GridMajor)
                if (f > minF && f < maxF) c.DrawRect(X(f), 0, p, rect.Height, _grid);
        }
        _gridLayer.Draw(canvas, rect.Left, rect.Top);

        if (!ctx.ShowScales || rect.Height < 22 * p) return;
        foreach (var (f, label) in GridMajor)
        {
            if (f <= minF || f >= maxF) continue;
            float lx = rect.Left + X(f) + 2 * p;
            var area = SKRect.Create(lx - 2 * p, rect.Top, ctx.Labels.Measure(label) + 4 * p, ctx.Labels.CapHeight + 2 * p);
            if (!readout.IsEmpty && area.IntersectsWith(readout)) continue;
            ctx.Labels.Draw(canvas, label, lx, rect.Top + p, t.InkDim);
        }
    }

    /// <summary>Dégradé horizontal grave → aigu de la palette, un peu transparent (style « courbe remplie »).</summary>
    private SKShader FillShader(SKRect rect, Theme t)
    {
        var key = (rect.Left, rect.Width, t.Version, t.Animated ? (int)(t.Phase * 128) : 0);
        if (_shader is not null && Equals(key, _shaderKey)) return _shader;
        _shaderKey = key;
        _shader?.Dispose();
        const int stops = 8;
        var colors = new SKColor[stops];
        for (int i = 0; i < stops; i++)
            colors[i] = t.Spectral(i / (stops - 1f)).WithAlpha(0xA8);
        _shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, 0), new SKPoint(rect.Right, 0), colors, SKShaderTileMode.Clamp);
        return _shader;
    }

    /// <summary>Masque vertical du relief : opaque en haut, plus transparent vers le bas.</summary>
    private SKShader FadeShader(SKRect rect)
    {
        var key = (rect.Top, rect.Bottom);
        if (_fadeShader is not null && Equals(key, _fadeKey)) return _fadeShader;
        _fadeKey = key;
        _fadeShader?.Dispose();
        _fadeShader = SKShader.CreateLinearGradient(new SKPoint(0, rect.Top), new SKPoint(0, rect.Bottom),
            new[] { SKColors.White, SKColors.White.WithAlpha(0x90) }, new[] { 0.3f, 1f }, SKShaderTileMode.Clamp);
        return _fadeShader;
    }

    public override void Dispose()
    {
        _path.Dispose();
        _fill.Dispose();
        _bar.Dispose();
        _line.Dispose();
        _grid.Dispose();
        _fade.Dispose();
        _gridLayer.Dispose();
        _note.Dispose();
        _shader?.Dispose();
        _fadeShader?.Dispose();
    }
}
