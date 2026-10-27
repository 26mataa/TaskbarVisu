using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Anneau polaire : le spectre enroulé en cercle (graves en bas, aigus en haut, en miroir gauche / droite).
/// L'anneau respire sur les basses, tourne du côté où penche la stéréo, et chaque temps fort laisse
/// une onde qui s'élargit en s'éteignant.
/// </summary>
internal sealed class RingModule : VisualizerModule
{
    private const int Points = 48, MaxEchoes = 4;

    private readonly SpectrumMapper _mapper = new();
    private readonly float[] _db = new float[Points / 2], _v = new float[Points / 2];
    private float _bass, _slow, _angle, _balance, _ceiling = -40;
    private readonly float[] _echoAge = new float[MaxEchoes], _echoSize = new float[MaxEchoes];
    private int _echoes;
    private bool _quiet = true;
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPath _path = new();

    public RingModule() : base("ring") { }

    public override bool IsIdle => _quiet && _echoes == 0 && _bass < 0.01f;

    public override void Update(AudioAnalyzer audio, double dt)
    {
        _quiet = audio.SilentSeconds > 0.3;
        _mapper.Ensure(_db.Length, 40, 14000, audio.SampleRate, audio.FftSize, 3);
        _mapper.MapDb(audio.SpectrumDb, _db, smooth: true);
        float k = 1 - (float)Math.Exp(-dt / 0.06), fall = (float)dt * 1.2f;
        // Plafond : suit la bande la plus forte (vite en montée, 6 dB/s en descente), pour garder du relief.
        float loudest = -120;
        foreach (float d in _db) loudest = Math.Max(loudest, d);
        _ceiling = loudest > _ceiling ? loudest : Math.Max(loudest, _ceiling - (float)dt * 6);
        float bass = 0;
        for (int i = 0; i < _db.Length; i++)
        {
            float rel = Math.Clamp(1 + (_db[i] - _ceiling) / 36, 0, 1);
            float v = _quiet ? 0 : rel * rel;
            _v[i] = v > _v[i] ? _v[i] + (v - _v[i]) * k * 2 : Math.Max(v, _v[i] - fall);
            if (i < 4) bass += _v[i] / 4;
        }
        _bass += (bass - _bass) * (1 - (float)Math.Exp(-dt / 0.05));

        // Rotation : du côté le plus fort (balance gauche / droite lissée).
        float l = audio.RmsL, r = audio.RmsR;
        float balance = l + r > 1e-4f ? (r - l) / (r + l) : 0;
        _balance += (balance - _balance) * (1 - (float)Math.Exp(-dt / 0.4));
        _angle = (_angle + (float)dt * (0.15f + 2.5f * _balance) * (float)Opt("spin")) % MathF.Tau;

        // Onde sur chaque temps fort.
        float energy = _quiet ? 0 : (l + r) / 2;
        if (energy > _slow * 1.6f && energy > 0.01f && (_echoes == 0 || _echoAge[_echoes - 1] > 0.15f))
        {
            if (_echoes == MaxEchoes)
            {
                Array.Copy(_echoAge, 1, _echoAge, 0, MaxEchoes - 1);
                Array.Copy(_echoSize, 1, _echoSize, 0, MaxEchoes - 1);
                _echoes--;
            }
            _echoAge[_echoes] = 0;
            _echoSize[_echoes] = 0.6f + 0.4f * _bass;
            _echoes++;
        }
        _slow += (energy - _slow) * (1 - (float)Math.Exp(-dt / 0.5));
        for (int i = 0; i < _echoes; i++)
        {
            _echoAge[i] += (float)dt;
            if (_echoAge[i] < 0.6f) continue;
            Array.Copy(_echoAge, i + 1, _echoAge, i, _echoes - i - 1);
            Array.Copy(_echoSize, i + 1, _echoSize, i, _echoes - i - 1);
            _echoes--;
            i--;
        }
    }

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float cx = rect.MidX, cy = rect.MidY;
        // Rayons : un cercle au centre, ou une ellipse sur toute la largeur.
        float ry = rect.Height / 2 - p;
        float rx = Flag("stretch") ? rect.Width / 2 - Math.Max(p, ctx.EdgeInset) : ry;
        float baseR = 0.32f + 0.18f * _bass;
        float amp = 1 - baseR;
        _stroke.StrokeWidth = p;

        // Ondes des temps forts : elles partent de l'anneau et s'élargissent en s'éteignant.
        for (int e = 0; e < _echoes; e++)
        {
            float age = _echoAge[e] / 0.6f;
            float s = baseR + (1.15f - baseR) * age * _echoSize[e];
            _stroke.Color = t.Spectral(0.6f).WithAlpha((byte)(170 * (1 - age)));
            canvas.DrawOval(cx, cy, rx * s, ry * s, _stroke);
        }

        // Anneau : chaque point s'écarte du cercle de base selon sa bande, miroir gauche / droite.
        _path.Rewind();
        int half = _v.Length;
        for (int j = 0; j <= Points; j++)
        {
            int i = j % Points;
            // Bande : 0 (graves) en bas, la plus aiguë en haut, des deux côtés.
            int band = i < half ? i : Points - 1 - i;
            float a = _angle + MathF.PI / 2 + MathF.Tau * i / Points;
            float radius = baseR + amp * _v[Math.Clamp(band, 0, half - 1)];
            float x = MathF.Round(cx + MathF.Cos(a) * rx * radius), y = MathF.Round(cy + MathF.Sin(a) * ry * radius);
            if (j == 0) _path.MoveTo(x, y);
            else _path.LineTo(x, y);
        }
        _fill.Color = t.Spectral(0.2f + 0.3f * _bass).WithAlpha(0x50);
        canvas.DrawPath(_path, _fill);
        _stroke.Color = t.Spectral(0.85f + 0.15f * _bass);
        canvas.DrawPath(_path, _stroke);

        // Cœur : cercle de base en pointillés.
        _fill.Color = t.Spectral(1f);
        for (int i = 0; i < 16; i++)
        {
            float a = _angle + MathF.Tau * i / 16;
            canvas.DrawRect(MathF.Round(cx + MathF.Cos(a) * rx * baseR * 0.55f), MathF.Round(cy + MathF.Sin(a) * ry * baseR * 0.55f), p, p, _fill);
        }
        ctx.ApplyScanlines(canvas, rect);
    }

    public override void Dispose()
    {
        _stroke.Dispose();
        _fill.Dispose();
        _path.Dispose();
    }
}
