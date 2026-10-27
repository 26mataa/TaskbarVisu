using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Imageur stéréo pensé pour l'horizontal, sur toute la largeur du module :
/// - panorama par fréquence (graves en bas, aigus en haut, gauche → droite), en points LED ;
/// - ou largeur stéréo par fréquence ;
/// - corrélation de phase en axe central coloré (ou en barre).
/// </summary>
internal sealed class StereoImagerModule : VisualizerModule
{
    private const float PanMinF = 40, PanMaxF = 16000;

    // Sous ~-70 dBFS (bruit de fond, dither), l'imageur se tait : le module peut passer au repos.
    private const float Gate = 3e-4f;

    private double _quiet; // secondes passées sous le seuil Gate
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

    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly LayerCache _corrTrack = new();

    public StereoImagerModule() : base("imager") { }

    /// <summary>0 = panorama, 1 = largeur (l'ancien choix « Aucune », vectorscope seul, retombe sur le panorama).</summary>
    private int View => OptI("view") == 1 ? 1 : 0;

    public override bool IsIdle
    {
        get
        {
            if (_corrAlpha > 0.01f) return false;
            foreach (var a in _alpha) if (a > 0.01f) return false;
            return _panMax < 0.01f;
        }
    }

    protected override void OnResize()
    {
        _columns = Math.Max(6, Width / (3 * Px));
        _mid = new float[_columns];
        _side = new float[_columns];
        _width = new float[_columns];
        _alpha = new float[_columns];

        // Cellules de 3 px ; nombre de colonnes impair pour avoir une colonne « centre ».
        _panCols = Math.Max(7, (Width - 2 * Px) / (3 * Px)) | 1;
        if (_panCols * 3 * Px > Width) _panCols -= 2;
        _panRows = Math.Max(5, (Height - 2 * Px) / (3 * Px));
        _panAcc = new float[_panCols * _panRows];
        _pan = new float[_panCols * _panRows];
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        int n = audio.Count;
        var left = audio.Left;
        var right = audio.Right;

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

        if (View == 1)
        {
            UpdateWidth(audio, dt);
            DecayPanorama(dt);
        }
        else
        {
            if (_quiet > 0.3) DecayPanorama(dt);
            else UpdatePanorama(audio, dt);
            Array.Clear(_alpha);
        }
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
        bool corrBar = OptI("correlation") == 1;
        float corrH = corrBar ? Math.Max(3 * p, MathF.Round(rect.Height * 0.14f)) : 0;
        float corrGap = corrBar ? 2 * p : 0;
        float inset = MathF.Ceiling(Math.Max(p, ctx.EdgeInset * 0.5f));

        var view = new SKRect(rect.Left + inset, rect.Top + p, rect.Right - inset, rect.Bottom - corrH - corrGap);
        if (view.Width > 4)
        {
            if (View == 1) DrawWidthSpectrum(canvas, view, ctx);
            else DrawPanorama(canvas, view, ctx);
        }
        if (corrBar && view.Width > 4) DrawCorrelation(canvas, new SKRect(view.Left, rect.Bottom - corrH, view.Right, rect.Bottom), ctx);
    }

    /// <summary>Couleur de l'axe « mono » selon la corrélation : normale, orange si faible, rouge si négative.</summary>
    private SKColor AxisColor(Theme t)
    {
        if (OptI("correlation") != 0 || _corrAlpha < 0.05f) return t.GridFaint;
        var c = _corr < 0 ? t.Hot : _corr < 0.3f ? t.Warn : t.InkDim;
        return c.WithAlpha((byte)(Math.Clamp(_corrAlpha, 0, 1) * c.Alpha));
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
        _fill.Color = AxisColor(t);
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
        _fill.Dispose();
        _corrTrack.Dispose();
    }
}
