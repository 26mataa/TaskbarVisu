using SkiaSharp;
using TaskbarVisu.Core;
using TaskbarVisu.Modules;

namespace TaskbarVisu.Rendering;

/// <summary>
/// Aperçus en direct pour la fenêtre de réglages : le thread de rendu fait tourner une copie de chaque module
/// demandé sur le son du moment, à la hauteur et à l'échelle de la vraie barre, et la dessine ~20 fois par seconde
/// dans une image que l'interface vient copier. Rien ne tourne tant qu'aucun aperçu n'est demandé.
/// </summary>
internal sealed partial class RenderHost
{
    /// <summary>Dernière image d'un aperçu (BGRA prémultiplié). Lire sous <see cref="Gate"/>.</summary>
    internal sealed class PreviewFrame
    {
        public readonly object Gate = new();
        public byte[] Pixels = Array.Empty<byte>();
        public int Width, Height, Version;
    }

    private sealed class PreviewSlot
    {
        public required VisualizerModule Module { get; init; }
        public required PreviewFrame Frame { get; init; }
        public required int WidthDip { get; init; }
        public SKBitmap? Bitmap;
        public SKCanvas? Canvas;
    }

    /// <summary>Demande de l'interface : modules (id, largeur en DIP) ; null = plus d'aperçus.</summary>
    private volatile (string Id, int WidthDip)[]? _previewRequest;
    private (string Id, int WidthDip)[]? _previewApplied;
    private readonly Dictionary<string, PreviewSlot> _previews = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PreviewFrame> _previewFrames = new();
    private double _nextPreview;
    private float _previewScale;
    private int _previewHeight;
    private bool _previewSettingsDirty;

    /// <summary>Aperçus voulus par l'interface (vide ou null : aucun).</summary>
    public void SetPreviews((string Id, int WidthDip)[]? modules) =>
        _previewRequest = modules is { Length: > 0 } ? modules : null;

    /// <summary>Image d'aperçu d'un module, si elle existe (thread de l'interface).</summary>
    public PreviewFrame? GetPreview(string id) => _previewFrames.TryGetValue(id, out var frame) ? frame : null;

    private bool Previewing => _previews.Count > 0;

    /// <summary>Crée ou retire les modules d'aperçu selon la demande, et suit la taille de la barre.</summary>
    private void SyncPreviews()
    {
        var request = _previewRequest;
        float scale = _bar is { Valid: true } bar ? bar.Scale : 1;
        int height = _bar is { Valid: true } b && b.Taskbar.Height > 0 ? b.Taskbar.Height : (int)Math.Round(48 * scale);
        bool resize = scale != _previewScale || height != _previewHeight;
        if (ReferenceEquals(request, _previewApplied) && !resize && !_previewSettingsDirty) return;
        _previewApplied = request;
        _previewScale = scale;
        _previewHeight = height;
        _previewSettingsDirty = false;

        var wanted = request ?? Array.Empty<(string Id, int WidthDip)>();
        foreach (var id in _previews.Keys.ToList())
        {
            if (wanted.Any(w => w.Id == id && w.WidthDip == _previews[id].WidthDip)) continue;
            DisposePreview(_previews[id]);
            _previews.Remove(id);
            _previewFrames.TryRemove(id, out _);
        }

        foreach (var (id, widthDip) in wanted)
        {
            var info = ModuleCatalog.Find(id);
            var cfg = _settings.AllModules.FirstOrDefault(c => c.Id == id);
            if (info is null || cfg is null) continue;
            if (!_previews.TryGetValue(id, out var slot))
            {
                var frame = _previewFrames.GetOrAdd(id, _ => new PreviewFrame());
                slot = new PreviewSlot { Module = info.Create(), Frame = frame, WidthDip = widthDip };
                _previews[id] = slot;
            }
            slot.Module.Configure(cfg);
            int w = Math.Max(8, (int)Math.Round(widthDip * scale));
            if (slot.Bitmap is null || slot.Bitmap.Width != w || slot.Bitmap.Height != height)
            {
                slot.Canvas?.Dispose();
                slot.Bitmap?.Dispose();
                slot.Bitmap = new SKBitmap(new SKImageInfo(w, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                slot.Canvas = new SKCanvas(slot.Bitmap);
                slot.Module.Resize(w, height, scale);
            }
        }
    }

    /// <summary>Fait avancer les aperçus à chaque image, et les dessine à ~20 images par seconde.</summary>
    private void UpdatePreviews(double dt, double now)
    {
        foreach (var slot in _previews.Values)
        {
            _ctx.Theme = ThemeFor(slot.Module);
            slot.Module.Update(_analyzer, dt);
        }
        if (now < _nextPreview) return;
        _nextPreview = now + 0.05;

        // Fond de la vraie barre (clair ou sombre), pour juger le module tel qu'il y apparaîtra.
        var back = _globalTheme.Light ? new SKColor(0xEE, 0xEE, 0xF0) : new SKColor(0x1C, 0x1C, 0x20);
        foreach (var slot in _previews.Values)
        {
            if (slot.Bitmap is null || slot.Canvas is null) continue;
            var canvas = slot.Canvas;
            var rect = new SKRect(0, 0, slot.Bitmap.Width, slot.Bitmap.Height);
            canvas.Clear(back);
            canvas.Save();
            canvas.ClipRect(rect);
            if (_settings.Background == BackgroundChoice.Lcd)
            {
                _panel.Color = new SKColor(0x9A, 0xA5, 0x7E, 0xF2);
                canvas.DrawRoundRect(rect, 3 * _ctx.Scale, 3 * _ctx.Scale, _panel);
            }
            try
            {
                _ctx.Theme = ThemeFor(slot.Module);
                slot.Module.Render(canvas, rect, _ctx);
            }
            catch (Exception ex)
            {
                Log.Error($"Aperçu du module {slot.Module.Info.Id}", ex);
            }
            canvas.Restore();
            canvas.Flush();

            var frame = slot.Frame;
            lock (frame.Gate)
            {
                int size = slot.Bitmap.ByteCount;
                if (frame.Pixels.Length != size) frame.Pixels = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(slot.Bitmap.GetPixels(), frame.Pixels, 0, size);
                frame.Width = slot.Bitmap.Width;
                frame.Height = slot.Bitmap.Height;
                frame.Version++;
            }
        }
    }

    private static void DisposePreview(PreviewSlot slot)
    {
        slot.Module.Dispose();
        slot.Canvas?.Dispose();
        slot.Bitmap?.Dispose();
    }

    private void DisposePreviews()
    {
        foreach (var slot in _previews.Values) DisposePreview(slot);
        _previews.Clear();
        _previewFrames.Clear();
    }
}
