using SkiaSharp;
using TaskbarVisu.Core;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Un module de visualisation. Update() est appelé à chaque image avec l'analyse audio partagée,
/// Render() dessine dans le rectangle attribué (déjà découpé, fond transparent).
/// Tout est fait sur le thread de rendu : pas de verrou nécessaire.
/// </summary>
internal abstract class VisualizerModule : IDisposable
{
    protected VisualizerModule(string id)
    {
        Info = ModuleCatalog.Find(id) ?? throw new ArgumentException(id);
        Config = ModuleCatalog.CreateConfig(id);
    }

    public ModuleInfo Info { get; }
    protected ModuleConfig Config { get; private set; }
    protected int Width { get; private set; }
    protected int Height { get; private set; }
    protected float Scale { get; private set; } = 1f;

    /// <summary>Taille d'un pixel « technique » (segments, traits), identique à celle de la police pixel.</summary>
    protected int Px => Math.Max(1, (int)MathF.Round(Scale * 0.85f));

    /// <summary>Vrai quand plus rien ne bouge à l'écran (permet d'arrêter de redessiner en silence).</summary>
    public virtual bool IsIdle => true;

    /// <summary>Ligne d'état pour le diagnostic (--dump), ou null.</summary>
    public virtual string? Diagnostics => null;

    public void Configure(ModuleConfig config)
    {
        Config = config;
        OnConfigure();
    }

    public void Resize(int width, int height, float scale)
    {
        if (width == Width && height == Height && scale == Scale) return;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Scale = scale;
        OnResize();
    }

    protected virtual void OnConfigure() { }
    protected virtual void OnResize() { }

    public abstract void Update(AudioAnalyzer audio, double dt);
    public abstract void Render(SKCanvas canvas, SKRect rect, RenderContext ctx);

    protected double Opt(string key) => Config.Get(key);
    protected float OptF(string key) => (float)Config.Get(key);
    protected int OptI(string key) => (int)Math.Round(Config.Get(key));
    protected bool Flag(string key) => Config.Get(key) >= 0.5;

    protected SKColor OptColor(string key)
    {
        uint v = (uint)Math.Clamp(Math.Round(Config.Get(key)), 0, 0xFFFFFF);
        return new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    protected static float ToDb(float linear) => 20f * MathF.Log10(Math.Max(linear, 1e-10f));

    public virtual void Dispose() { }
}
