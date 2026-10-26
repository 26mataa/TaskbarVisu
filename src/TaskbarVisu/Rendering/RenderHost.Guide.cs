using SkiaSharp;
using TaskbarVisu.Core;
using TaskbarVisu.Native;
using TaskbarVisu.Taskbar;

namespace TaskbarVisu.Rendering;

/// <summary>Repère du guide de démarrage, dessiné sur la vraie barre des tâches.</summary>
internal enum GuideMark { None, Zones, Tray }

/// <summary>
/// Demande du guide. Les textes arrivent déjà traduits (le thread de rendu ne touche pas aux traductions) ;
/// <paramref name="Icon"/> : emplacement de l'icône de TaskbarVisu dans la zone de notification, s'il est connu.
/// </summary>
internal sealed record GuideRequest(GuideMark Mark, string Left, string Right, string Centre,
    string Tray, string TrayHidden, RECT? Icon = null);

/// <summary>
/// Guide de démarrage : les zones libres encadrées de pointillés qui défilent (les modules s'effacent le temps
/// de la présentation, puis se rallument avec l'animation d'auto-test), ou une flèche qui pointe l'icône
/// de la zone de notification, dans une petite fenêtre juste à côté de la barre.
/// </summary>
internal sealed partial class RenderHost
{
    private volatile GuideRequest? _guideRequest;
    private GuideRequest? _guide;
    private OverlayWindow? _pointer;
    private int _pointerTip;
    private bool _pointerUp, _pointerHidden;
    private readonly SKPaint _guidePaint = new() { IsAntialias = false };

    /// <summary>Repère à afficher (null : aucun). Appelable depuis n'importe quel thread.</summary>
    public void SetGuide(GuideRequest? request) => _guideRequest = request;

    private bool GuideActive => _guide is { Mark: not GuideMark.None };

    /// <summary>Thread de rendu : prend en compte une nouvelle demande du guide.</summary>
    private void SyncGuide()
    {
        var request = _guideRequest;
        if (ReferenceEquals(request, _guide)) return;
        // Fin de la présentation des zones : les modules se rallument comme un appareil qu'on allume.
        if (_guide?.Mark == GuideMark.Zones && request?.Mark != GuideMark.Zones) _bootPending = true;
        _guide = request;
        Relayout();
    }

    /// <summary>Présentation des zones : chaque fenêtre couvre toute sa zone libre. Faux si ce n'est pas le moment.</summary>
    private bool LayoutGuideZones(TaskbarState bar, int y, int height)
    {
        if (_guide is not { Mark: GuideMark.Zones } guide) return false;
        if (_settings.SingleGap == SingleGapMode.Center && !bar.Spanning.IsEmpty)
        {
            GuideZone(_left, bar.Spanning, guide.Centre, y, height);
        }
        else
        {
            GuideZone(_left, bar.Left, guide.Left, y, height);
            GuideZone(_right, bar.Right, guide.Right, y, height);
        }
        return true;
    }

    private static void GuideZone(ZoneView view, Zone zone, string label, int y, int height)
    {
        if (zone.Width < 8) return;
        view.Window.SetBounds(zone.X0, y, zone.Width, height);
        view.Guide = label;
    }

    /// <summary>Flèche vers l'icône : au-dessus de la barre, ou en dessous si la barre est en haut de l'écran.</summary>
    private void LayoutPointer(TaskbarState bar)
    {
        if (_guide is not { Mark: GuideMark.Tray } guide)
        {
            _pointer?.Hide();
            return;
        }
        if (_pointer is null)
        {
            _pointer = new OverlayWindow();
            _tracker.SetOwnWindows(new[] { _left.Window.Hwnd, _right.Window.Hwnd, _pointer.Hwnd });
        }

        var t = bar.Taskbar;
        int tip;
        if (guide.Icon is { } icon && !icon.IsEmpty && icon.Left >= t.Left && icon.Right <= t.Right
            && icon.Top >= t.Top - 4 && icon.Bottom <= t.Bottom + 4)
        {
            tip = (icon.Left + icon.Right) / 2;
            _pointerHidden = false;
        }
        else
        {
            // Icône rangée derrière la flèche ^ (réglage par défaut de Windows 11) : celle-ci ouvre la zone de
            // notification, juste après la zone libre de droite.
            int trayStart = !bar.Spanning.IsEmpty ? bar.Spanning.X1 : !bar.Right.IsEmpty ? bar.Right.X1 : t.Right - (int)(220 * bar.Scale);
            tip = trayStart + (int)(14 * bar.Scale);
            _pointerHidden = true;
        }

        int p = 2 * _ctx.P; // flèche et texte en double taille : bien visibles hors de la barre
        var (w, h) = PointerSize(guide, p);
        _pointerUp = bar.AtTop;
        int x = Math.Clamp(tip - w + 10 * p, bar.Monitor.Left, Math.Max(bar.Monitor.Left, bar.Monitor.Right - w));
        int y = _pointerUp ? t.Bottom + p : t.Top - h - p;
        _pointer.SetBounds(x, y, w, h);
        _pointerTip = Math.Clamp(tip - x, 6 * p, w - 6 * p);
    }

    private (int Width, int Height) PointerSize(GuideRequest guide, int p)
    {
        float text = _ctx.Labels.Measure(guide.Tray, 2);
        if (_pointerHidden) text = Math.Max(text, _ctx.Labels.Measure(guide.TrayHidden, 2));
        int lines = _pointerHidden ? 2 : 1;
        int box = (int)Math.Ceiling(_ctx.Labels.CapHeightFor(2) * lines + (lines - 1) * 2 * p + 6 * p);
        return ((int)Math.Ceiling(text + 10 * p), box + ArrowRows * p + BobRows * p);
    }

    private const int ArrowRows = 11, BobRows = 3;

    /// <summary>Zone libre : voile de la palette qui respire, pointillés qui défilent, coins marqués et nom au centre.</summary>
    private void DrawZoneGuide(SKCanvas canvas, int width, int height, string label)
    {
        var theme = _globalTheme;
        int p = _ctx.P;
        float now = (float)_now;
        float breath = 0.5f + 0.5f * MathF.Sin(now * MathF.PI * 1.6f);

        canvas.Clear(SKColors.Transparent);
        // Voile : la palette de gauche à droite, en pas de 4 px comme un afficheur.
        int step = 4 * p;
        byte veil = (byte)(0x1C + 0x30 * breath);
        for (int x = 0; x < width; x += step)
        {
            _guidePaint.Color = theme.Spectral(x / (float)Math.Max(1, width)).WithAlpha(veil);
            canvas.DrawRect(x, 0, Math.Min(step - p, width - x), height, _guidePaint);
        }

        // Pointillés qui font le tour de la zone (« fourmis »), d'un pixel de police d'épaisseur.
        int dash = 3 * p, period = 5 * p;
        int offset = (int)(now * 14 * p) % period;
        _guidePaint.Color = theme.Ink.WithAlpha((byte)(0x90 + 0x6F * breath));
        for (int x = -offset; x < width; x += period)
        {
            float x0 = Math.Max(0, x), x1 = Math.Min(width, x + dash);
            if (x1 <= x0) continue;
            canvas.DrawRect(x0, 0, x1 - x0, p, _guidePaint);
            canvas.DrawRect(width - x1, height - p, x1 - x0, p, _guidePaint);
        }
        for (int y = -offset; y < height; y += period)
        {
            float y0 = Math.Max(0, y), y1 = Math.Min(height, y + dash);
            if (y1 <= y0) continue;
            canvas.DrawRect(width - p, y0, p, y1 - y0, _guidePaint);
            canvas.DrawRect(0, height - y1, p, y1 - y0, _guidePaint);
        }

        // Coins en équerre, plus épais, aux couleurs des extrémités de la palette.
        int arm = Math.Min(8 * p, height / 2), thick = 2 * p;
        foreach (var (cx, cy, dx, dy) in new[] { (0, 0, 1, 1), (width, 0, -1, 1), (0, height, 1, -1), (width, height, -1, -1) })
        {
            _guidePaint.Color = theme.Spectral(cx == 0 ? 0.15f : 0.9f);
            float left = dx > 0 ? cx : cx - arm, top = dy > 0 ? cy : cy - thick;
            canvas.DrawRect(left, top, arm, thick, _guidePaint);
            left = dx > 0 ? cx : cx - thick;
            top = dy > 0 ? cy : cy - arm;
            canvas.DrawRect(left, top, thick, arm, _guidePaint);
        }

        // Nom et largeur au centre, sur un cartouche noir.
        string text = $"{label} · {width} PX";
        if (_ctx.Labels.Measure(text) > width - 8 * p) text = label;
        float tw = _ctx.Labels.Measure(text), cap = _ctx.Labels.CapHeight;
        float tx = MathF.Round((width - tw) / 2), ty = MathF.Round((height - cap) / 2);
        _guidePaint.Color = new SKColor(0, 0, 0, 0xD0);
        canvas.DrawRect(tx - 3 * p, ty - 2 * p, tw + 6 * p, cap + 4 * p, _guidePaint);
        _ctx.Labels.Draw(canvas, text, tx, ty, theme.Ink);
    }

    /// <summary>Flèche pixel qui sautille vers l'icône, et son cartouche.</summary>
    private void DrawPointer()
    {
        if (_pointer?.Canvas is not { } canvas || _guide is not { Mark: GuideMark.Tray } guide) return;
        var theme = _globalTheme;
        int p = 2 * _ctx.P, w = _pointer.Width, h = _pointer.Height;
        canvas.Clear(SKColors.Transparent);

        // Sautillement par pas entiers de pixel de police : le dessin reste net.
        int bob = (int)MathF.Round((0.5f + 0.5f * MathF.Sin((float)_now * 7f)) * BobRows) * p;
        int column = _pointerTip / p * p;
        // Distance à la barre de chaque rangée de la flèche (0 = la pointe).
        float Y(int row) => _pointerUp ? bob + row * p : h - bob - (row + 1) * p;
        static int Half(int row) => row < 0 ? 0 : row < 6 ? row : 1; // pointe triangulaire puis tige

        // Contour sombre d'un pixel de police : la flèche reste visible sur un fond d'écran clair.
        _guidePaint.Color = new SKColor(0x08, 0x08, 0x0C, 0xF0);
        for (int row = -1; row <= ArrowRows; row++)
        {
            int half = Half(Math.Min(row, ArrowRows - 1)) + (row is >= 0 and < ArrowRows ? 1 : 0);
            canvas.DrawRect(column - half * p, Y(row), (2 * half + 1) * p, p, _guidePaint);
        }
        for (int row = 0; row < ArrowRows; row++)
        {
            int half = Half(row);
            _guidePaint.Color = theme.Spectral(0.95f - 0.6f * row / ArrowRows);
            canvas.DrawRect(column - half * p, Y(row), (2 * half + 1) * p, p, _guidePaint);
        }

        // Cartouche : fond noir, liseré de la palette, texte à l'encre.
        int boxHeight = h - (ArrowRows + BobRows) * p;
        float boxTop = _pointerUp ? bob + ArrowRows * p : h - bob - ArrowRows * p - boxHeight;
        var box = new SKRect(0, boxTop, w, boxTop + boxHeight);
        _guidePaint.Color = theme.Spectral(0.85f);
        canvas.DrawRect(box, _guidePaint);
        _guidePaint.Color = new SKColor(0x08, 0x08, 0x0C, 0xF0);
        canvas.DrawRect(box.Left + p, box.Top + p, box.Width - 2 * p, box.Height - 2 * p, _guidePaint);

        float cap = _ctx.Labels.CapHeightFor(2), y = box.Top + 3 * p;
        _ctx.Labels.Draw(canvas, guide.Tray, 5 * p, y, theme.Ink, 2);
        if (_pointerHidden) _ctx.Labels.Draw(canvas, guide.TrayHidden, 5 * p, y + cap + 2 * p, theme.Spectral(0.7f), 2);

        _pointer.Present(255);
        if (_visible && !Headless && !_pointer.IsShown) _pointer.Show();
    }
}
