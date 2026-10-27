using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Horizon synthwave : une grille au sol file vers l'horizon, la chaîne de montagnes au loin est le spectre
/// (graves au centre, aigus sur les côtés), un soleil rayé se lève derrière et grossit avec les basses.
/// La grille défile plus vite quand le son est fort, et donne un à-coup sur chaque temps fort.
/// </summary>
internal sealed class HorizonModule : VisualizerModule
{
    private readonly SpectrumMapper _mapper = new();
    private float[] _db = Array.Empty<float>(), _peaks = Array.Empty<float>();
    private double _scroll;
    private float _speed, _bass, _slow, _kick, _ceiling = -40;
    private bool _quiet = true;

    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke };
    private readonly SKPath _path = new();

    public HorizonModule() : base("horizon") { }

    public override bool IsIdle => _quiet && _speed < 0.02f;

    protected override void OnResize()
    {
        int half = Math.Max(8, Width / (2 * 2 * Px));
        _db = new float[half];
        _peaks = new float[half];
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        if (_db.Length == 0) return;
        _mapper.Ensure(_db.Length, 40, 14000, audio.SampleRate, audio.FftSize, 3);
        _mapper.MapDb(audio.SpectrumDb, _db, smooth: true);

        float k = 1 - (float)Math.Exp(-dt / 0.08), fall = (float)dt * 0.9f;
        _quiet = audio.SilentSeconds > 0.5;
        // Plafond : suit la bande la plus forte (vite en montée, 6 dB/s en descente), pour garder du relief.
        float loudest = -120;
        foreach (float d in _db) loudest = Math.Max(loudest, d);
        _ceiling = loudest > _ceiling ? loudest : Math.Max(loudest, _ceiling - (float)dt * 6);
        float bass = 0;
        for (int i = 0; i < _db.Length; i++)
        {
            float rel = Math.Clamp(1 + (_db[i] - _ceiling) / 42, 0, 1);
            float v = _quiet ? 0 : rel * rel;
            _peaks[i] = v > _peaks[i] ? _peaks[i] + (v - _peaks[i]) * k * 3 : Math.Max(v, _peaks[i] - fall);
            if (i < _db.Length / 6) bass += v;
        }
        bass /= Math.Max(1, _db.Length / 6);
        _bass += (bass - _bass) * k;

        // Temps fort : l'énergie dépasse nettement sa moyenne récente.
        float energy = _quiet ? 0 : (audio.RmsL + audio.RmsR) / 2;
        if (energy > _slow * 1.6f && energy > 0.01f) _kick = 1;
        _slow += (energy - _slow) * (1 - (float)Math.Exp(-dt / 0.5));
        _kick *= (float)Math.Exp(-dt / 0.18);

        float target = _quiet ? 0 : (0.35f + Math.Min(1, energy * 4)) * (float)Opt("speed");
        _speed += (target - _speed) * (1 - (float)Math.Exp(-dt / 0.3));
        _scroll = (_scroll + dt * (_speed + 1.5f * _kick * (float)Opt("speed"))) % 1.0;
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float cx = MathF.Round(rect.MidX);
        float horizon = MathF.Round(rect.Top + rect.Height * 0.48f);
        float ground = rect.Bottom - horizon;

        // Soleil : demi-disque rayé derrière les montagnes, qui grossit avec les basses.
        if (Flag("sun"))
        {
            float radius = MathF.Round(rect.Height * (0.34f + 0.16f * _bass + 0.05f * _kick));
            for (float y = horizon - radius; y < horizon; y += p)
            {
                float dy = horizon - y;
                // Rayures de plus en plus larges vers le bas, comme sur les pochettes.
                int band = (int)((dy / radius) * 6);
                if (band < 3 && ((int)(y / p) % (band + 2) == 0)) continue;
                float half = MathF.Round(MathF.Sqrt(radius * radius - dy * dy));
                _fill.Color = t.Spectral(0.55f + 0.45f * dy / radius).WithAlpha(0xE0);
                canvas.DrawRect(cx - half, y, 2 * half, p, _fill);
            }
        }

        // Montagnes : le spectre en miroir (graves au centre), en silhouette pleine puis en crête claire.
        int n = _peaks.Length;
        float step = rect.Width / 2 / n;
        float mountain = (horizon - rect.Top - p) * 0.8f;
        _path.Rewind();
        _path.MoveTo(rect.Left, horizon);
        for (int i = n - 1; i >= 0; i--) _path.LineTo(MathF.Round(cx - (i + 0.5f) * step), MathF.Round(horizon - _peaks[i] * mountain));
        for (int i = 0; i < n; i++) _path.LineTo(MathF.Round(cx + (i + 0.5f) * step), MathF.Round(horizon - _peaks[i] * mountain));
        _path.LineTo(rect.Right, horizon);
        _path.Close();
        _fill.Color = Theme.Mix(t.Spectral(0.1f), SKColors.Black, 0.35f).WithAlpha(0xF0);
        canvas.DrawPath(_path, _fill);
        _stroke.StrokeWidth = p;
        _stroke.Color = t.Spectral(0.95f);
        canvas.DrawPath(_path, _stroke);

        // Sol : lignes de fuite vers le centre de l'horizon, et lignes transversales qui arrivent vers nous.
        var gridColor = t.Spectral(0.7f);
        _fill.Color = gridColor.WithAlpha(0xC0);
        int rays = 7;
        for (int r = -rays; r <= rays; r++)
        {
            float far = cx + r * rect.Width * 0.035f, near = cx + r * rect.Width * 0.16f;
            _path.Rewind();
            _path.MoveTo(MathF.Round(far), horizon);
            _path.LineTo(MathF.Round(near), rect.Bottom);
            _stroke.Color = gridColor.WithAlpha(0x90);
            canvas.DrawPath(_path, _stroke);
        }
        const int Rows = 7;
        for (int j = 0; j < Rows; j++)
        {
            // Profondeur z de 1 (premier plan) à Rows : la ligne monte vers l'horizon en se resserrant.
            float z = 1 + j + (1 - (float)_scroll);
            float y = MathF.Round(horizon + ground / z);
            if (y >= rect.Bottom) continue;
            byte alpha = (byte)Math.Clamp(255 * (1.2f - z / (Rows + 1)), 40, 255);
            _fill.Color = gridColor.WithAlpha(alpha);
            canvas.DrawRect(rect.Left, y, rect.Width, p, _fill);
        }
        _fill.Color = t.Spectral(1f);
        canvas.DrawRect(rect.Left, horizon, rect.Width, p, _fill);
        ctx.ApplyScanlines(canvas, rect);
    }

    public override void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _path.Dispose();
    }
}
