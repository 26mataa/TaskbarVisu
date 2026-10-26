using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Oscilloscope façon Frutiger Aero (années Vista / 7) : bulle de verre brillante, trace lumineuse calée sur un
/// passage à zéro, bulles qui montent avec le volume, et symboles (♪, étincelles, gouttes) qui jaillissent sur
/// les temps forts ; un reflet balaie la bulle sur les coups les plus forts.
/// </summary>
internal sealed class AeroScopeModule : VisualizerModule
{
    private const int ScopeSize = 8192;
    private const int MaxParticles = 64;

    private enum Kind : byte { Bubble, Sparkle, Note, Drop }

    private struct Particle
    {
        public Kind Kind;
        public float X, Y, Vx, Vy, Size, Age, Life, Spin, Wobble;
    }

    private readonly float[] _scope = new float[ScopeSize];
    private int _scopePos;
    private int _scopeSilent = ScopeSize;
    private SKPoint[] _points = Array.Empty<SKPoint>();

    private readonly Particle[] _particles = new Particle[MaxParticles];
    private int _count;
    private float _bubbleCarry;
    private readonly Random _random = new(7);

    private float _peak = 0.05f, _level, _slowEnergy = 1e-6f, _cooldown;
    private float _sweep = -1;

    // Bulle de verre, recalculée quand la taille ou les couleurs changent.
    private SKRoundRect? _capsule, _gloss;
    private SKShader? _bodyShader, _glowShader, _glossShader;
    private (SKColor Top, SKColor Bottom, SKColor Glow, int W, int H) _shaderKey;

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _trace = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, StrokeJoin = SKStrokeJoin.Round };

    // Symboles dessinés en vecteurs (rayon 1), indépendants de la police.
    private static readonly SKPath SparklePath = BuildSparkle();
    private static readonly SKPath NotePath = BuildNote();
    private static readonly SKPath DropPath = BuildDrop();

    public AeroScopeModule() : base("aero") { }

    public override bool IsIdle => _count == 0 && _scopeSilent >= ScopeSize && _sweep < 0;

    protected override void OnResize()
    {
        _count = 0;
        _capsule?.Dispose();
        _gloss?.Dispose();
        float r = Math.Min(Height * 0.5f, 10 * Scale);
        _capsule = new SKRoundRect(new SKRect(0.5f, 0.5f, Width - 0.5f, Height - 0.5f), r, r);
        _gloss = new SKRoundRect(new SKRect(1.5f, 1.5f, Width - 1.5f, Height * 0.5f), Math.Max(1, r - 1), Math.Max(1, r - 1));
        _shaderKey = default;
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        float fdt = (float)Math.Min(dt, 0.1);
        int n = audio.Count;
        double energy = 0;
        float framePeak = 0;
        for (int i = 0; i < n; i++)
        {
            float m = (audio.Left[i] + audio.Right[i]) * 0.5f;
            _scope[_scopePos] = m;
            _scopePos = (_scopePos + 1) & (ScopeSize - 1);
            _scopeSilent = Math.Abs(m) > 1e-4f ? 0 : Math.Min(ScopeSize, _scopeSilent + 1);
            energy += m * m;
            framePeak = Math.Max(framePeak, Math.Abs(m));
        }
        float e = n > 0 ? (float)(energy / n) : 0;

        // Gain automatique : la crête récente remplit ~85 % de la hauteur.
        _peak = Math.Max(framePeak, _peak * MathF.Exp(-fdt / 1.5f));
        _peak = Math.Max(_peak, 0.01f);

        // Niveau 0..1 (sur ~48 dB) pour le nombre et la vitesse des bulles.
        float db = 10 * MathF.Log10(Math.Max(e, 1e-12f));
        float level = Math.Clamp((db + 54) / 48, 0, 1);
        _level += (level - _level) * (1 - MathF.Exp(-fdt / 0.08f));

        // Temps fort : l'énergie instantanée dépasse nettement la moyenne des dernières centaines de ms.
        _cooldown -= fdt;
        bool onset = e > 2e-6f && e > _slowEnergy * 1.9f && _cooldown <= 0;
        float strength = e / Math.Max(_slowEnergy, 1e-9f);
        _slowEnergy += (e - _slowEnergy) * (1 - MathF.Exp(-fdt / 0.6f));
        if (onset)
        {
            _cooldown = 0.14f;
            if (Flag("symbols"))
            {
                int burst = strength > 4 ? 3 : strength > 2.6f ? 2 : 1;
                for (int i = 0; i < burst; i++) SpawnSymbol();
            }
            if (Flag("shine") && strength > 3.2f && _sweep < 0) _sweep = 0;
        }

        // Bulles : de plus en plus nombreuses avec le volume.
        float rate = OptF("bubbles") * _level * _level * 22 * Math.Max(1, Width / (60 * Scale));
        _bubbleCarry += rate * fdt;
        while (_bubbleCarry >= 1)
        {
            _bubbleCarry -= 1;
            SpawnBubble();
        }

        // Mouvement : les bulles montent en ondulant, les symboles flottent puis s'effacent.
        float lift = 0.6f + 1.2f * _level;
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _particles[i];
            p.Age += fdt;
            p.Y -= p.Vy * fdt * (p.Kind == Kind.Bubble ? lift : 1);
            p.X += p.Vx * fdt + (p.Kind == Kind.Bubble ? MathF.Sin(p.Age * 4 + p.Wobble) * 6 * Scale * fdt : 0);
            p.Spin += fdt * 40;
            if (p.Age >= p.Life || p.Y < -p.Size * 2)
            {
                _particles[i] = _particles[--_count];
                i--;
            }
        }

        if (_sweep >= 0)
        {
            _sweep += fdt / 0.7f;
            if (_sweep > 1) _sweep = -1;
        }
    }

    private void SpawnBubble()
    {
        if (_count >= MaxParticles) return;
        float size = (1.4f + (float)_random.NextDouble() * 2.6f) * Scale;
        _particles[_count++] = new Particle
        {
            Kind = Kind.Bubble,
            X = (float)_random.NextDouble() * Width,
            Y = Height + size,
            Vy = (10 + (float)_random.NextDouble() * 14) * Scale,
            Size = size,
            Life = 6,
            Wobble = (float)_random.NextDouble() * 6.28f,
        };
    }

    private void SpawnSymbol()
    {
        if (_count >= MaxParticles) return;
        var kind = (Kind)(1 + _random.Next(3));
        _particles[_count++] = new Particle
        {
            Kind = kind,
            X = (0.1f + 0.8f * (float)_random.NextDouble()) * Width,
            Y = Height * (0.45f + 0.4f * (float)_random.NextDouble()),
            Vx = ((float)_random.NextDouble() - 0.5f) * 10 * Scale,
            Vy = (8 + (float)_random.NextDouble() * 10) * Scale,
            Size = (kind == Kind.Sparkle ? 4.2f : 3.6f) * Scale,
            Life = 0.9f + (float)_random.NextDouble() * 0.5f,
            Spin = (float)_random.NextDouble() * 40,
        };
    }

    /// <summary>Couleurs de l'ambiance : haut et bas de la bulle, halo de la trace, teinte des symboles.</summary>
    private (SKColor Top, SKColor Bottom, SKColor Glow, SKColor Accent) Colors(Theme theme) => OptI("ambiance") switch
    {
        1 => (new SKColor(0xE9, 0xFF, 0xC2), new SKColor(0x2E, 0x9E, 0x3A), new SKColor(0xC8, 0xFF, 0x6A), new SKColor(0xFF, 0xF3, 0x8A)),
        2 => (Theme.Mix(theme.Spectral(0.95f), SKColors.White, 0.45f), theme.Spectral(0.3f), theme.Spectral(0.85f), theme.Spectral(1f)),
        _ => (new SKColor(0xC9, 0xF4, 0xFF), new SKColor(0x15, 0x86, 0xD4), new SKColor(0x7F, 0xE6, 0xFF), new SKColor(0xFF, 0xF6, 0xA8)),
    };

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        if (_capsule is null || _gloss is null) return;
        var (top, bottom, glow, accent) = Colors(ctx.Theme);
        bool glass = Flag("glass");

        canvas.Save();
        canvas.Translate(rect.Left, rect.Top);
        if (glass)
        {
            UpdateShaders(top, bottom, glow);
            _fill.Shader = _bodyShader;
            canvas.DrawRoundRect(_capsule, _fill);
            _fill.Shader = _glowShader;
            canvas.DrawRoundRect(_capsule, _fill);
            _fill.Shader = null;
            canvas.ClipRoundRect(_capsule, SKClipOperation.Intersect, true);
        }

        DrawParticles(canvas, Kind.Bubble, accent, glass);
        DrawTrace(canvas, glass ? SKColors.White : glow, glow);
        DrawParticles(canvas, Kind.Sparkle, accent, glass);

        if (glass)
        {
            if (_sweep >= 0) DrawSweep(canvas);
            _fill.Shader = _glossShader;
            canvas.DrawRoundRect(_gloss, _fill);
            _fill.Shader = null;
            _stroke.Color = SKColors.White.WithAlpha(0x8C);
            canvas.DrawRoundRect(_capsule, _stroke);
        }
        canvas.Restore();
    }

    private void UpdateShaders(SKColor top, SKColor bottom, SKColor glow)
    {
        var key = (top, bottom, glow, Width, Height);
        if (key == _shaderKey && _bodyShader is not null) return;
        _shaderKey = key;
        _bodyShader?.Dispose();
        _glowShader?.Dispose();
        _glossShader?.Dispose();
        _bodyShader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Height),
            new[] { top.WithAlpha(0xE0), Theme.Mix(top, bottom, 0.55f).WithAlpha(0xE0), bottom.WithAlpha(0xEC) },
            new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
        // Lueur « eau » qui monte du bas, typique des boutons Aero.
        _glowShader = SKShader.CreateRadialGradient(new SKPoint(Width / 2f, Height * 1.15f), Math.Max(Width * 0.55f, Height),
            new[] { glow.WithAlpha(0x90), glow.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        _glossShader = SKShader.CreateLinearGradient(new SKPoint(0, 1), new SKPoint(0, Height * 0.5f),
            new[] { SKColors.White.WithAlpha(0xB4), SKColors.White.WithAlpha(0x14) }, null, SKShaderTileMode.Clamp);
    }

    /// <summary>Trace de l'oscilloscope, calée sur un passage à zéro montant : halo en traits fins puis cœur blanc.</summary>
    private void DrawTrace(SKCanvas canvas, SKColor core, SKColor glow)
    {
        int width = Width;
        if (width < 2 || _scopeSilent >= ScopeSize) return;
        int span = Math.Clamp((int)(OptF("window") * 48), 128, 4096);
        int searchFrom = (_scopePos - span - 2048 + ScopeSize) & (ScopeSize - 1);
        int start = (_scopePos - span + ScopeSize) & (ScopeSize - 1);
        for (int k = 1; k < 2048; k++)
        {
            int i = (searchFrom + k) & (ScopeSize - 1);
            int prev = (i - 1) & (ScopeSize - 1);
            if (_scope[prev] < 0 && _scope[i] >= 0) start = i;
        }

        if (_points.Length != width) _points = new SKPoint[width];
        float half = Height * 0.5f - 3 * Scale;
        float mid = Height * 0.5f;
        float gain = 0.85f / _peak;
        for (int x = 0; x < width; x++)
        {
            float v = _scope[(start + x * span / width) & (ScopeSize - 1)] * gain;
            _points[x] = new SKPoint(x + 0.5f, mid - Math.Clamp(v, -1, 1) * half);
        }

        // Halo : la même ligne fine décalée de quelques pixels, de plus en plus transparente (bien moins cher qu'un flou).
        int reach = Math.Max(1, (int)MathF.Round(2 * Scale));
        for (int d = reach; d >= 1; d--)
        {
            _trace.Color = glow.WithAlpha((byte)(0x70 / d));
            canvas.Save();
            canvas.Translate(0, d);
            canvas.DrawPoints(SKPointMode.Polygon, _points, _trace);
            canvas.Translate(0, -2 * d);
            canvas.DrawPoints(SKPointMode.Polygon, _points, _trace);
            canvas.Restore();
        }
        _trace.Color = core;
        canvas.DrawPoints(SKPointMode.Polygon, _points, _trace);
    }

    private void DrawParticles(SKCanvas canvas, Kind layer, SKColor accent, bool glass)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _particles[i];
            bool bubble = p.Kind == Kind.Bubble;
            if (bubble != (layer == Kind.Bubble)) continue;
            if (bubble)
            {
                DrawBubble(canvas, p, glass);
                continue;
            }
            // Apparition « pop » puis fondu sur la fin de vie.
            float t = p.Age / p.Life;
            float grow = Math.Min(1, p.Age / 0.12f);
            float scale = p.Size * (0.6f + 0.55f * grow - 0.15f * t);
            byte alpha = (byte)(255 * Math.Clamp((1 - t) / 0.45f, 0, 1));
            var path = p.Kind switch { Kind.Sparkle => SparklePath, Kind.Note => NotePath, _ => DropPath };
            var color = p.Kind switch { Kind.Sparkle => accent, Kind.Drop => new SKColor(0xA8, 0xEC, 0xFF), _ => SKColors.White };

            canvas.Save();
            canvas.Translate(p.X, p.Y);
            canvas.Scale(scale);
            if (p.Kind == Kind.Sparkle) canvas.RotateDegrees(p.Spin);
            // Halo doux sous le symbole, puis le symbole.
            _fill.Color = color.WithAlpha((byte)(alpha / 4));
            canvas.Save();
            canvas.Scale(1.5f);
            canvas.DrawPath(path, _fill);
            canvas.Restore();
            _fill.Color = color.WithAlpha(alpha);
            canvas.DrawPath(path, _fill);
            canvas.Restore();
        }
    }

    private void DrawBubble(SKCanvas canvas, in Particle p, bool glass)
    {
        float r = p.Size;
        byte a = (byte)(255 * Math.Clamp(p.Y / (Height * 0.25f), 0, 1)); // s'efface en arrivant en haut
        _fill.Color = SKColors.White.WithAlpha((byte)(a * (glass ? 0x26 : 0x18) / 255));
        canvas.DrawCircle(p.X, p.Y, r, _fill);
        _stroke.Color = SKColors.White.WithAlpha((byte)(a * 0xB0 / 255));
        canvas.DrawCircle(p.X, p.Y, r, _stroke);
        _fill.Color = SKColors.White.WithAlpha((byte)(a * 0xE6 / 255));
        canvas.DrawCircle(p.X - r * 0.35f, p.Y - r * 0.35f, Math.Max(0.6f, r * 0.28f), _fill);
    }

    /// <summary>Reflet en biais qui traverse la bulle de gauche à droite.</summary>
    private void DrawSweep(SKCanvas canvas)
    {
        float band = Width * 0.22f;
        float x = -band + _sweep * (Width + 2 * band);
        using var shader = SKShader.CreateLinearGradient(new SKPoint(x - band, 0), new SKPoint(x + band, 0),
            new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(0x6E), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        _fill.Shader = shader;
        canvas.Save();
        canvas.Skew(-0.6f, 0);
        canvas.DrawRect(x - band + Height * 0.3f, 0, 2 * band + Height * 0.6f, Height, _fill);
        canvas.Restore();
        _fill.Shader = null;
    }

    private static SKPath BuildSparkle()
    {
        var path = new SKPath();
        path.MoveTo(0, -1);
        path.QuadTo(0.12f, -0.12f, 1, 0);
        path.QuadTo(0.12f, 0.12f, 0, 1);
        path.QuadTo(-0.12f, 0.12f, -1, 0);
        path.QuadTo(-0.12f, -0.12f, 0, -1);
        path.Close();
        return path;
    }

    private static SKPath BuildNote()
    {
        var path = new SKPath();
        path.AddOval(new SKRect(-0.75f, 0.35f, 0.15f, 1f));
        path.AddRect(new SKRect(0f, -1f, 0.18f, 0.7f));
        path.MoveTo(0.18f, -1f);
        path.CubicTo(0.55f, -0.75f, 0.8f, -0.55f, 0.55f, -0.1f);
        path.CubicTo(0.6f, -0.45f, 0.4f, -0.6f, 0.18f, -0.6f);
        path.Close();
        return path;
    }

    private static SKPath BuildDrop()
    {
        var path = new SKPath();
        path.MoveTo(0, -1);
        path.CubicTo(0.65f, -0.15f, 0.7f, 0.75f, 0, 0.8f);
        path.CubicTo(-0.7f, 0.75f, -0.65f, -0.15f, 0, -1);
        path.Close();
        return path;
    }

    public override void Dispose()
    {
        _capsule?.Dispose();
        _gloss?.Dispose();
        _bodyShader?.Dispose();
        _glowShader?.Dispose();
        _glossShader?.Dispose();
        _fill.Dispose();
        _stroke.Dispose();
        _trace.Dispose();
    }
}
