using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Imageur stéréo pensé pour l'horizontal :
/// - vectorscope polaire en demi-cercle (mono = vertical, L / R à 45°, hors phase sur les côtés),
///   points colorés par bande ;
/// - seconde vue au choix : panorama par fréquence (graves en bas, aigus en haut, gauche → droite)
///   ou largeur stéréo par fréquence ;
/// - corrélation de phase en axe central coloré (ou en barre).
/// </summary>
internal sealed class StereoImagerModule : VisualizerModule
{
    private const int Frames = 4;
    private const int Bands = 3;
    private const float PanMinF = 40, PanMaxF = 16000;
    private static readonly SKPoint Offscreen = new(-10000, -10000);
    private static readonly float[] BandTone = { 0.05f, 0.5f, 0.95f };

    // Sous ~-70 dBFS (bruit de fond, dither), l'imageur se tait : la normalisation amplifierait ce bruit
    // en nuage de points et le module ne passerait jamais au repos.
    private const float Gate = 3e-4f;
    // Gain maximal de la normalisation : il faut au moins ~-50 dBFS pour remplir le demi-cercle.
    private const float EnvFloor = 3e-3f;

    // Points normalisés (x ∈ [-1, 1], y ∈ [0, 1]) par bande et par image, pour la rémanence.
    private readonly SKPoint[,][] _points = new SKPoint[Bands, Frames][];
    private readonly int[,] _counts = new int[Bands, Frames];
    private int _head;
    private SKPoint[] _screen = Array.Empty<SKPoint>();
    private int _screenUsed;
    private float _env = EnvFloor;
    private double _quiet; // secondes passées sous le seuil Gate

    // Séparation en 3 bandes (filtres à un pôle à 250 Hz et 2,5 kHz) pour chaque canal.
    private int _filterRate;
    private float _k1, _k2, _l1, _l2, _r1, _r2;

    private double _sLR, _sLL, _sRR;
    private float _corr, _corrAlpha;

    // Panorama par fréquence : grille lignes (fréquence) × colonnes (position gauche → droite).
    private int _panCols, _panRows;
    private float[] _panAcc = Array.Empty<float>(), _pan = Array.Empty<float>();
    private float _panMax;

    // Largeur par fréquence.
    private readonly SpectrumMapper _mapper = new();
    private int _columns;
    private float[] _mid = Array.Empty<float>(), _side = Array.Empty<float>();
    private float[] _width = Array.Empty<float>(), _alpha = Array.Empty<float>();

    // Points sans anticrénelage : bien plus rapide et invisible à cette taille.
    private readonly SKPaint _dots = new() { IsAntialias = false, StrokeCap = SKStrokeCap.Square, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _guide = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPath _path = new();
    private readonly LayerCache _guides = new();
    private readonly LayerCache _corrTrack = new();

    public StereoImagerModule() : base("imager")
    {
        for (int b = 0; b < Bands; b++)
            for (int f = 0; f < Frames; f++)
                _points[b, f] = Array.Empty<SKPoint>();
    }

    private int View => OptI("view");

    public override bool IsIdle
    {
        get
        {
            foreach (var c in _counts) if (c > 0) return false;
            if (_corrAlpha > 0.01f) return false;
            foreach (var a in _alpha) if (a > 0.01f) return false;
            return _panMax < 0.01f;
        }
    }

    protected override void OnResize()
    {
        _columns = Math.Max(6, (int)(Width * 0.4f / (3 * Px)));
        _mid = new float[_columns];
        _side = new float[_columns];
        _width = new float[_columns];
        _alpha = new float[_columns];

        // Cellules de 3 px ; nombre de colonnes impair pour avoir une colonne « centre ».
        int panWidth = (int)(Width * 0.42f);
        _panCols = Math.Max(7, panWidth / (3 * Px)) | 1;
        _panRows = Math.Max(5, (Height - 2 * Px) / (3 * Px));
        _panAcc = new float[_panCols * _panRows];
        _pan = new float[_panCols * _panRows];
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        int n = audio.Count;
        var left = audio.Left;
        var right = audio.Right;

        if (audio.SampleRate != _filterRate)
        {
            _filterRate = audio.SampleRate;
            _k1 = 1f - MathF.Exp(-2f * MathF.PI * 250f / _filterRate);
            _k2 = 1f - MathF.Exp(-2f * MathF.PI * 2500f / _filterRate);
        }

        // Corrélation (moyenne glissante ~300 ms).
        double lr = 0, ll = 0, rr = 0;
        for (int i = 0; i < n; i++)
        {
            float l = left[i], r = right[i];
            lr += l * r; ll += l * l; rr += r * r;
        }
        double k = Math.Exp(-dt / 0.3);
        _sLR = _sLR * k + lr;
        _sLL = _sLL * k + ll;
        _sRR = _sRR * k + rr;
        _quiet = Math.Max(audio.PeakL, audio.PeakR) < Gate ? _quiet + dt : 0;
        bool audible = _quiet < 0.3 && _sLL + _sRR > 1e-7;
        float target = audible ? (float)(_sLR / Math.Sqrt(_sLL * _sRR + 1e-20)) : 0;
        float smooth = (float)Math.Min(1, dt * 10);
        _corr += (Math.Clamp(target, -1, 1) - _corr) * smooth;
        _corrAlpha += ((audible ? 1 : 0) - _corrAlpha) * smooth;

        UpdateScope(audio, dt, n, left, right);

        switch (View)
        {
            case 0:
                UpdatePanorama(audio, dt);
                Array.Clear(_alpha);
                break;
            case 1:
                UpdateWidth(audio, dt);
                DecayPanorama(dt);
                break;
            default:
                Array.Clear(_alpha);
                DecayPanorama(dt);
                break;
        }
    }

    private void UpdateScope(AudioAnalyzer audio, double dt, int n, float[] left, float[] right)
    {
        bool bands = Flag("bands");
        int bandCount = bands ? Bands : 1;
        int capacity = Math.Max(16, OptI("dots") / bandCount);
        _head = (_head + 1) % Frames;
        for (int b = 0; b < Bands; b++)
        {
            if (_points[b, _head].Length != capacity) _points[b, _head] = new SKPoint[capacity];
            _counts[b, _head] = 0;
        }

        float peak = MathF.Sqrt(audio.PeakL * audio.PeakL + audio.PeakR * audio.PeakR);
        _env = Math.Max(Math.Max(peak, EnvFloor), _env * (float)Math.Exp(-dt / 2.0));
        bool gated = _quiet > 0;
        int step = Math.Max(1, (n + capacity - 1) / capacity);

        for (int i = 0; i < n; i++)
        {
            float l = left[i], r = right[i];
            _l1 += _k1 * (l - _l1); _l2 += _k2 * (l - _l2);
            _r1 += _k1 * (r - _r1); _r2 += _k2 * (r - _r2);
            if (gated || i % step != 0) continue;

            if (!bands)
            {
                Add(0, l, r);
                continue;
            }
            Add(0, _l1, _r1);
            Add(1, _l2 - _l1, _r2 - _r1);
            Add(2, l - _l2, r - _r2);
        }
    }

    private void Add(int band, float l, float r)
    {
        int count = _counts[band, _head];
        var pts = _points[band, _head];
        if (count >= pts.Length) return;
        float x = (r - l) * 0.70710677f, y = (l + r) * 0.70710677f;
        if (y < 0) { x = -x; y = -y; }
        float radius = MathF.Sqrt(x * x + y * y);
        if (radius < 1e-5f) return;
        float rn = MathF.Sqrt(Math.Min(1f, radius / _env));
        pts[count] = new SKPoint(x / radius * rn, y / radius * rn);
        _counts[band, _head] = count + 1;
    }

    /// <summary>
    /// Chaque case FFT est déposée dans la ligne de sa fréquence, à la colonne de sa position gauche / droite
    /// (rapport des amplitudes L et R), puis la grille retombe doucement.
    /// </summary>
    private void UpdatePanorama(AudioAnalyzer audio, double dt)
    {
        if (_pan.Length == 0) return;
        Array.Clear(_panAcc);
        var lp = audio.LeftPower;
        var rp = audio.RightPower;
        float binHz = audio.BinHz;
        int k0 = Math.Max(1, (int)MathF.Ceiling(PanMinF / binHz));
        int k1 = Math.Min(lp.Length - 1, (int)(PanMaxF / binHz));
        float logSpan = MathF.Log(PanMaxF / PanMinF);

        for (int k = k0; k <= k1; k++)
        {
            float pl = lp[k], pr = rp[k];
            float e = pl + pr;
            if (e < 1e-12f) continue;
            int row = Math.Clamp((int)(MathF.Log(k * binHz / PanMinF) / logSpan * _panRows), 0, _panRows - 1);
            float al = MathF.Sqrt(pl), ar = MathF.Sqrt(pr);
            float pan = (ar - al) / (ar + al);
            float colF = (pan + 1) * 0.5f * (_panCols - 1);
            int c0 = (int)colF;
            float frac = colF - c0;
            int baseIdx = row * _panCols;
            _panAcc[baseIdx + c0] += e * (1 - frac);
            if (c0 + 1 < _panCols) _panAcc[baseIdx + c0 + 1] += e * frac;
        }

        float fall = (float)dt * 1.6f;
        _panMax = 0;
        for (int row = 0; row < _panRows; row++)
        {
            // +3 dB/octave vers les aigus, pour une image équilibrée.
            float freq = PanMinF * MathF.Exp((row + 0.5f) / _panRows * logSpan);
            float tilt = 3f * MathF.Log2(freq / 1000f);
            for (int c = 0; c < _panCols; c++)
            {
                int i = row * _panCols + c;
                float db = 10f * MathF.Log10(_panAcc[i] + 1e-20f) + tilt;
                float v = Math.Clamp((db + 78f) / 54f, 0, 1);
                _pan[i] = v >= _pan[i] ? v : Math.Max(v, _pan[i] - fall);
                if (_pan[i] > _panMax) _panMax = _pan[i];
            }
        }
    }

    private void DecayPanorama(double dt)
    {
        if (_panMax <= 0) return;
        float fall = (float)dt * 1.6f;
        _panMax = 0;
        for (int i = 0; i < _pan.Length; i++)
        {
            _pan[i] = Math.Max(0, _pan[i] - fall);
            if (_pan[i] > _panMax) _panMax = _pan[i];
        }
    }

    private void UpdateWidth(AudioAnalyzer audio, double dt)
    {
        if (_columns == 0) return;
        _mapper.Ensure(_columns, 30, 16000, audio.SampleRate, audio.FftSize, 0);
        _mapper.MapPower(audio.MidPower, _mid);
        _mapper.MapPower(audio.SidePower, _side);
        float kk = 1f - (float)Math.Exp(-dt / 0.12);
        for (int i = 0; i < _columns; i++)
        {
            float total = _mid[i] + _side[i];
            float w = total > 1e-14f ? _side[i] / total : 0;
            float a = Math.Clamp((10f * MathF.Log10(total + 1e-20f) + 80f) / 30f, 0, 1);
            _width[i] += (w - _width[i]) * kk;
            _alpha[i] += (a - _alpha[i]) * kk;
        }
    }

    // ------------------------------------------------------------------ Rendu

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        int p = ctx.P;
        int view = View;
        bool corrBar = OptI("correlation") == 1;
        float corrH = corrBar ? Math.Max(3 * p, MathF.Round(rect.Height * 0.14f)) : 0;
        float corrGap = corrBar ? 2 * p : 0;

        SKRect scopeRect, sideRect = SKRect.Empty, corrRect = SKRect.Empty;
        if (view == 2)
        {
            scopeRect = new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom - corrH - corrGap);
            if (corrBar) corrRect = new SKRect(rect.Left, rect.Bottom - corrH, rect.Right, rect.Bottom);
        }
        else
        {
            float scopeW = MathF.Round(Math.Min(rect.Width * 0.56f, rect.Height * 2.2f));
            scopeRect = new SKRect(rect.Left, rect.Top, rect.Left + scopeW, rect.Bottom);
            float sideRight = rect.Right - MathF.Ceiling(Math.Max(p, ctx.EdgeInset * 0.5f));
            sideRect = new SKRect(scopeRect.Right + 4 * p, rect.Top + p, sideRight, rect.Bottom - corrH - corrGap);
            if (corrBar) corrRect = new SKRect(sideRect.Left, rect.Bottom - corrH, sideRect.Right, rect.Bottom);
        }

        if (scopeRect.Width > 4) DrawScope(canvas, scopeRect, ctx);
        if (view == 0 && sideRect.Width > 4) DrawPanorama(canvas, sideRect, ctx);
        if (view == 1 && sideRect.Width > 4) DrawWidthSpectrum(canvas, sideRect, ctx);
        if (corrBar && corrRect.Width > 4) DrawCorrelation(canvas, corrRect, ctx);
    }

    /// <summary>Couleur de l'axe « mono » selon la corrélation : normale, orange si faible, rouge si négative.</summary>
    private SKColor AxisColor(Theme t)
    {
        if (OptI("correlation") != 0 || _corrAlpha < 0.05f) return t.Grid;
        var c = _corr < 0 ? t.Hot : _corr < 0.3f ? t.Warn : t.InkDim;
        return c.WithAlpha((byte)(Math.Clamp(_corrAlpha, 0, 1) * c.Alpha));
    }

    private void DrawScope(SKCanvas canvas, SKRect r, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float cx = MathF.Round(r.MidX);
        float baseY = r.Bottom - p;
        float halfW = r.Width / 2 - 2 * p;
        float plotH = Math.Min(r.Height - 2 * p, halfW);
        halfW = Math.Min(halfW, plotH * 2.2f);

        // Repères statiques : demi-cercle, anneaux, diagonales L / R, secteurs hors phase, lettres.
        var key = (t.Version, ctx.Labels.Key, ctx.ShowScales, r.Width, r.Height);
        if (_guides.Begin((int)MathF.Ceiling(r.Width), (int)MathF.Ceiling(r.Height), key) is { } c)
        {
            c.Translate(-r.Left, -r.Top);
            var outer = new SKRect(cx - halfW, baseY - plotH, cx + halfW, baseY + plotH);
            var dl = new SKPoint(cx - 0.7071f * halfW, baseY - 0.7071f * plotH);
            var dr = new SKPoint(cx + 0.7071f * halfW, baseY - 0.7071f * plotH);

            // Secteurs « hors phase » (au-delà des diagonales L / R).
            _fill.Color = t.GridFaint;
            _path.Rewind();
            _path.MoveTo(cx, baseY);
            _path.ArcTo(outer, 180, 45, false);
            _path.Close();
            _path.MoveTo(cx, baseY);
            _path.ArcTo(outer, 315, 45, false);
            _path.Close();
            c.DrawPath(_path, _fill);

            _guide.StrokeWidth = p;
            _guide.Color = t.Grid;
            _path.Rewind();
            _path.AddArc(outer, 180, 180);
            _path.MoveTo(cx - halfW, baseY);
            _path.LineTo(cx + halfW, baseY);
            _path.MoveTo(cx, baseY);
            _path.LineTo(dl);
            _path.MoveTo(cx, baseY);
            _path.LineTo(dr);
            c.DrawPath(_path, _guide);

            _guide.Color = t.GridFaint;
            for (int ring = 1; ring <= 2; ring++)
            {
                float k = ring / 3f;
                c.DrawArc(new SKRect(cx - halfW * k, baseY - plotH * k, cx + halfW * k, baseY + plotH * k), 180, 180, false, _guide);
            }

            if (ctx.ShowScales && r.Height >= 20 * p)
            {
                float cap = ctx.Labels.CapHeight;
                ctx.Labels.Draw(c, "L", MathF.Round(dl.X - ctx.Labels.Measure("L") - 2 * p), MathF.Round(dl.Y - cap), t.InkDim);
                ctx.Labels.Draw(c, "R", MathF.Round(dr.X + 2 * p), MathF.Round(dr.Y - cap), t.InkDim);
            }
        }
        _guides.Draw(canvas, r.Left, r.Top);

        // Axe mono : sa couleur indique la corrélation de phase.
        _fill.Color = AxisColor(t);
        canvas.DrawRect(cx, baseY - plotH, p, plotH, _fill);

        float persistence = OptF("persistence");
        bool bands = Flag("bands");
        _dots.StrokeWidth = Math.Max(1f, MathF.Round(1.3f * ctx.Scale));
        for (int age = Frames - 1; age >= 0; age--)
        {
            int f = (_head - age + Frames) % Frames;
            float alpha = age == 0 ? 0.95f : persistence * 0.65f * (1f - age / (float)Frames);
            if (alpha < 0.03f) continue;
            for (int b = 0; b < (bands ? Bands : 1); b++)
            {
                int count = _counts[b, f];
                if (count == 0) continue;
                var color = bands ? t.Spectral(BandTone[b]) : t.Spectral(0.85f);
                DrawPoints(canvas, _points[b, f], count, cx, baseY, halfW, plotH, color.WithAlpha((byte)(alpha * 255)));
            }
        }
    }

    private void DrawPoints(SKCanvas canvas, SKPoint[] pts, int count, float cx, float baseY, float halfW, float plotH, SKColor color)
    {
        if (_screen.Length != pts.Length)
        {
            _screen = new SKPoint[pts.Length];
            Array.Fill(_screen, Offscreen);
            _screenUsed = 0;
        }
        for (int i = 0; i < count; i++)
            _screen[i] = new SKPoint(cx + pts[i].X * halfW, baseY - pts[i].Y * plotH);
        // Les cases non utilisées sont renvoyées hors champ (évite d'allouer un tableau par image).
        for (int i = count; i < _screenUsed; i++) _screen[i] = Offscreen;
        _screenUsed = count;
        _dots.Color = color;
        canvas.DrawPoints(SKPointMode.Points, _screen, _dots);
    }

    /// <summary>Carte de chaleur en points LED : position gauche → droite en largeur, graves en bas, aigus en haut.</summary>
    private void DrawPanorama(SKCanvas canvas, SKRect r, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float cellW = r.Width / _panCols, cellH = r.Height / _panRows;
        float dotW = Math.Max(p, MathF.Floor(cellW) - p), dotH = Math.Max(p, MathF.Floor(cellH) - p);

        // Colonne centrale (mono) en repère, colorée par la corrélation.
        int center = _panCols / 2;
        _fill.Color = AxisColor(t) == t.Grid ? t.GridFaint : AxisColor(t);
        canvas.DrawRect(MathF.Round(r.Left + center * cellW + dotW / 2 - p / 2f), r.Top, p, r.Height, _fill);

        for (int row = 0; row < _panRows; row++)
        {
            float y = MathF.Round(r.Bottom - (row + 1) * cellH);
            for (int c = 0; c < _panCols; c++)
            {
                float v = _pan[row * _panCols + c];
                if (v < 0.04f) continue;
                var color = t.Spectral(v);
                _fill.Color = color.WithAlpha((byte)(Math.Min(1f, v * 1.5f) * 255));
                canvas.DrawRect(MathF.Round(r.Left + c * cellW), y, dotW, dotH, _fill);
            }
        }
        ctx.ApplyScanlines(canvas, r);
    }

    private void DrawWidthSpectrum(SKCanvas canvas, SKRect r, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        // Ligne de référence : signal décorrélé (Side = Mid).
        _fill.Color = t.GridFaint;
        canvas.DrawRect(r.Left, MathF.Round(r.Bottom - MathF.Sqrt(0.5f) * r.Height), r.Width, p, _fill);

        float step = r.Width / _columns;
        float barW = Math.Max(p, MathF.Floor(step) - p);
        for (int i = 0; i < _columns; i++)
        {
            float a = _alpha[i];
            if (a < 0.02f) continue;
            float w = Math.Clamp(_width[i], 0, 1);
            float barH = MathF.Round(MathF.Sqrt(w) * r.Height);
            if (barH < 1) continue;
            _fill.Color = t.Spectral(i / (float)(_columns - 1)).WithAlpha((byte)(a * 0.95f * 255));
            canvas.DrawRect(MathF.Round(r.Left + i * step), r.Bottom - barH, barW, barH, _fill);
        }
        ctx.ApplyScanlines(canvas, r);
    }

    private void DrawCorrelation(SKCanvas canvas, SKRect r, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        var key = (t.Version, r.Width, r.Height, p);
        if (_corrTrack.Begin((int)MathF.Ceiling(r.Width), (int)MathF.Ceiling(r.Height), key) is { } c)
        {
            // Piste à segments + repères -1 / 0 / +1.
            _fill.Color = t.GridFaint;
            for (float sx = 0; sx < r.Width; sx += 3 * p) c.DrawRect(sx, 0, 2 * p, r.Height, _fill);
            _fill.Color = t.InkDim;
            c.DrawRect(MathF.Round(r.Width / 2), -p, p, r.Height + 2 * p, _fill);
            c.DrawRect(0, 0, p, r.Height, _fill);
            c.DrawRect(r.Width - p, 0, p, r.Height, _fill);
        }
        _corrTrack.Draw(canvas, r.Left, r.Top);

        if (_corrAlpha <= 0.02f) return;
        float cx = r.MidX;
        float x = r.Left + (Math.Clamp(_corr, -1, 1) + 1) / 2 * r.Width;
        var color = _corr < 0 ? t.Hot : _corr < 0.3f ? t.Warn : t.Spectral(0.85f);
        _fill.Color = color.WithAlpha((byte)(_corrAlpha * 0.45f * 255));
        canvas.DrawRect(Math.Min(cx, x), r.Top, Math.Abs(x - cx), r.Height, _fill);
        _fill.Color = color.WithAlpha((byte)(_corrAlpha * 255));
        float mw = 2 * p;
        canvas.DrawRect(MathF.Round(x - mw / 2), r.Top - p, mw, r.Height + 2 * p, _fill);
    }

    public override void Dispose()
    {
        _dots.Dispose();
        _guide.Dispose();
        _fill.Dispose();
        _path.Dispose();
        _guides.Dispose();
        _corrTrack.Dispose();
    }
}
