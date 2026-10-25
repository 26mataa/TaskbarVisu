using SkiaSharp;
using TaskbarVisu.Dsp;
using TaskbarVisu.Rendering;

namespace TaskbarVisu.Modules;

/// <summary>
/// Piano roll : les notes réellement entendues (harmoniques écartées, voir <see cref="KeyAnalyzer.Notes"/>)
/// allument un clavier de plusieurs octaves, et leur historique défile au-dessus, chaque note dans sa colonne.
/// Rien n'est « deviné » : une note faible s'allume faiblement. L'historique est une image circulaire :
/// à chaque nouvelle ligne on n'écrit qu'une ligne de pixels.
/// </summary>
internal sealed class PianoRollModule : VisualizerModule
{
    private static readonly int[] WhiteKeys = { 0, 2, 4, 5, 7, 9, 11 };
    /// <summary>Étendues proposées : première note (MIDI) et nombre d'octaves.</summary>
    private static readonly (int Low, int Octaves)[] Ranges = { (48, 3), (36, 4), (36, 5), (24, 6) };
    private const int MiddleC = 60;

    private readonly float[] _target = new float[128], _level = new float[128];
    private float _gain;
    private bool _anyLit;

    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private SKBitmap? _bitmap;
    private uint[] _row = Array.Empty<uint>();
    private int _writeRow;
    private double _rowCarry;
    private int _emptyRows = int.MaxValue / 2;

    // Couleurs (BGRA prémultipliées), calculées au rendu et utilisées pour écrire l'historique.
    private uint[] _lut = Array.Empty<uint>();
    private readonly uint[] _noteColor = new uint[128];
    private (int Version, int Phase) _colorKey = (-1, -1);

    // Géométrie en pixels physiques, recalculée quand la taille ou l'étendue change.
    private (int W, int H, int Range, int P) _layoutKey = (-1, -1, -1, -1);
    private int _low, _count, _keysX, _whiteW, _blackW, _keyboardH, _rollH;
    private int[] _keyX = Array.Empty<int>(), _keyW = Array.Empty<int>();
    private bool[] _isBlack = Array.Empty<bool>();

    public PianoRollModule() : base("pianoroll") { }

    private bool KeyboardOnTop => OptI("keyboard") == 1;
    private bool ByIntensity => OptI("colors") == 1;

    public override bool IsIdle => !_anyLit && _emptyRows >= _rollH;

    public override string? Diagnostics
    {
        get
        {
            var lit = Enumerable.Range(0, 128).Where(m => _level[m] >= 0.5f).Select(m => $"{Harmony.NoteName(m % 12, false)}{m / 12 - 1}");
            return $"pianoroll lit={string.Join(' ', lit)}";
        }
    }

    protected override void OnResize() => _layoutKey = (-1, -1, -1, -1);

    private void EnsureLayout()
    {
        int range = Math.Clamp(OptI("range"), 0, Ranges.Length - 1);
        int p = Px;
        var key = (Width, Height, range, p);
        if (key == _layoutKey) return;
        _layoutKey = key;

        (_low, int octaves) = Ranges[range];
        _count = octaves * 12;
        int whites = octaves * 7;
        _whiteW = Math.Max(2, Width / whites);
        _keysX = Math.Max(0, (Width - _whiteW * whites) / 2);
        _blackW = Math.Clamp((int)MathF.Round(_whiteW * 0.6f), 1, _whiteW - 1);
        int gap = _whiteW >= 4 ? 1 : 0;
        _keyboardH = Math.Clamp((int)MathF.Round(Height * 0.3f), 3 * p, Math.Max(3 * p, Height / 2));
        _rollH = Math.Max(1, Height - _keyboardH - p);

        _keyX = new int[_count];
        _keyW = new int[_count];
        _isBlack = new bool[_count];
        for (int i = 0; i < _count; i++)
        {
            int pc = (_low + i) % 12, octave = i / 12;
            int white = Array.IndexOf(WhiteKeys, pc);
            if (white >= 0)
            {
                _keyX[i] = _keysX + (octave * 7 + white) * _whiteW;
                _keyW[i] = _whiteW - gap;
            }
            else
            {
                // Touche noire : à cheval sur la séparation avec la blanche de gauche.
                int left = octave * 7 + Array.IndexOf(WhiteKeys, pc - 1);
                _keyX[i] = _keysX + (left + 1) * _whiteW - _blackW / 2 - gap;
                _keyW[i] = _blackW;
                _isBlack[i] = true;
            }
        }

        _bitmap?.Dispose();
        _bitmap = new SKBitmap(new SKImageInfo(Width, _rollH, SKColorType.Bgra8888, SKAlphaType.Premul));
        _bitmap.Erase(SKColors.Transparent);
        _row = new uint[Width];
        _writeRow = 0;
        _emptyRows = _rollH;
    }

    public override void Update(AudioAnalyzer audio, double dt)
    {
        var harmony = audio.Harmony;
        bool fresh = harmony.Update(audio, dt);
        EnsureLayout();
        if (_lut.Length == 0) return; // couleurs pas encore prêtes (premier rendu)

        // Gain automatique : la note la plus forte des dernières secondes sert de référence,
        // pour que les passages calmes restent lisibles.
        _gain = Math.Max(_gain * (float)Math.Exp(-dt / 3.0), 3e-4f);
        if (fresh)
        {
            var notes = harmony.Notes;
            float max = 0;
            for (int m = 0; m < 128; m++) max = Math.Max(max, notes[m]);
            _gain = Math.Max(_gain, max);
            float range = Math.Max(6, OptF("sensitivity"));
            for (int m = 0; m < 128; m++)
            {
                float level = notes[m] <= 0 ? 0 : 1 + ToDb(notes[m] / _gain) / range;
                _target[m] = level < 0.05f ? 0 : Math.Min(level, 1);
            }
        }

        // Attaque immédiate, retombée rapide (~120 ms) : les notes courtes restent lisibles.
        float release = 1 - (float)Math.Exp(-dt / 0.12);
        _anyLit = false;
        for (int m = 0; m < 128; m++)
        {
            _level[m] = _target[m] >= _level[m] ? _target[m] : _level[m] + (_target[m] - _level[m]) * release;
            if (_level[m] < 0.01f) _level[m] = 0;
            else _anyLit = true;
        }

        _rowCarry += dt * Opt("speed");
        int newRows = (int)_rowCarry;
        if (newRows <= 0 || _bitmap is null) return;
        _rowCarry -= newRows;
        newRows = Math.Min(newRows, _rollH);

        bool empty = BuildRow();
        _emptyRows = empty ? _emptyRows + newRows : 0;
        unsafe
        {
            uint* pixels = (uint*)_bitmap.GetPixels();
            for (int r = 0; r < newRows; r++)
            {
                new ReadOnlySpan<uint>(_row).CopyTo(new Span<uint>(pixels + _writeRow * Width, Width));
                _writeRow = (_writeRow + 1) % _rollH;
            }
        }
        _bitmap.NotifyPixelsChanged();
    }

    /// <summary>Ligne d'historique : une colonne par note allumée (blanches puis noires, par-dessus). Renvoie true si vide.</summary>
    private bool BuildRow()
    {
        Array.Clear(_row);
        bool empty = true;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_isBlack[i] != (pass == 1)) continue;
                float level = _level[_low + i];
                if (level <= 0.02f) continue;
                uint color = ByIntensity ? _lut[(int)(level * 255)] : Dim(_noteColor[_low + i], level);
                int x0 = Math.Max(0, _keyX[i]), x1 = Math.Min(Width, _keyX[i] + _keyW[i]);
                for (int x = x0; x < x1; x++) _row[x] = color;
                empty = false;
            }
        }
        return empty;
    }

    /// <summary>Couleur prémultipliée atténuée (alpha et canaux multipliés par k).</summary>
    private static uint Dim(uint premultiplied, float k)
    {
        uint a = (uint)((premultiplied >> 24) * k), r = (uint)(((premultiplied >> 16) & 0xFF) * k);
        uint g = (uint)(((premultiplied >> 8) & 0xFF) * k), b = (uint)((premultiplied & 0xFF) * k);
        return a << 24 | r << 16 | g << 8 | b;
    }

    private void EnsureColors(Theme theme)
    {
        // Palette animée : on recalcule au fil de la rotation (quelques dizaines de couleurs, coût négligeable).
        var key = (theme.Version, theme.Animated ? (int)(theme.Phase * 128) : 0);
        if (key == _colorKey) return;
        _colorKey = key;
        _lut = theme.BuildLut();
        for (int m = 0; m < 128; m++)
        {
            // Couleur selon la hauteur : de la plus grave à la plus aiguë de l'étendue la plus large.
            var c = theme.Spectral((m - 24) / 72f);
            _noteColor[m] = 0xFFu << 24 | (uint)c.Red << 16 | (uint)c.Green << 8 | c.Blue;
        }
    }

    private SKColor KeyColor(Theme theme, int midi, float level) =>
        ByIntensity ? theme.Spectral(level) : theme.Spectral((midi - 24) / 72f);

    public override void Render(SKCanvas canvas, SKRect rect, RenderContext ctx)
    {
        var t = ctx.Theme;
        EnsureColors(t);
        EnsureLayout();
        if (_bitmap is null) return;
        int p = ctx.P;

        canvas.Save();
        if (KeyboardOnTop)
        {
            // Clavier en haut, historique qui descend : même dessin, retourné verticalement.
            canvas.Translate(0, rect.Top + rect.Bottom);
            canvas.Scale(1, -1);
        }

        // Repères d'octave (Do) derrière l'historique.
        if (ctx.ShowScales)
        {
            _fill.Color = t.GridFaint;
            for (int i = 0; i < _count; i++)
                if ((_low + i) % 12 == 0) canvas.DrawRect(rect.Left + _keyX[i], rect.Top, 1, _rollH, _fill);
        }

        // Historique : lignes les plus anciennes en haut, les plus récentes contre le clavier.
        int older = _rollH - _writeRow;
        if (older > 0)
            canvas.DrawBitmap(_bitmap, new SKRect(0, _writeRow, Width, _rollH),
                new SKRect(rect.Left, rect.Top, rect.Left + Width, rect.Top + older));
        if (_writeRow > 0)
            canvas.DrawBitmap(_bitmap, new SKRect(0, 0, Width, _writeRow),
                new SKRect(rect.Left, rect.Top + older, rect.Left + Width, rect.Top + _rollH));

        DrawKeyboard(canvas, rect, ctx, rect.Top + _rollH + p);
        canvas.Restore();
        ctx.ApplyScanlines(canvas, rect);
    }

    private void DrawKeyboard(SKCanvas canvas, SKRect rect, RenderContext ctx, float top)
    {
        var t = ctx.Theme;
        int p = ctx.P;
        float blackH = MathF.Round(_keyboardH * 0.6f);

        for (int i = 0; i < _count; i++)
        {
            if (_isBlack[i]) continue;
            float x = rect.Left + _keyX[i];
            _fill.Color = t.Grid;
            canvas.DrawRect(x, top, _keyW[i], _keyboardH, _fill);
            DrawLit(canvas, t, i, x, top, _keyboardH);
        }
        // Do central : petit repère au pied de la touche.
        int middle = MiddleC - _low;
        if (ctx.ShowScales && middle >= 0 && middle < _count)
        {
            _fill.Color = t.InkDim;
            float w = Math.Max(1, _keyW[middle] - 2);
            canvas.DrawRect(rect.Left + _keyX[middle] + (_keyW[middle] - w) / 2, top + _keyboardH - p, w, p, _fill);
        }

        // Touches noires : en haut de la bande du clavier, contre l'historique (comme sur un vrai clavier vu de face).
        for (int i = 0; i < _count; i++)
        {
            if (!_isBlack[i]) continue;
            float x = rect.Left + _keyX[i];
            _fill.Color = new SKColor(0, 0, 0, 0xD8);
            canvas.DrawRect(x, top, _keyW[i], blackH, _fill);
            if (_level[_low + i] <= 0.02f)
            {
                // Liseré pour distinguer les touches noires éteintes sur fond sombre.
                _fill.Color = t.Grid;
                canvas.DrawRect(x, top + blackH - 1, _keyW[i], 1, _fill);
            }
            DrawLit(canvas, t, i, x, top, blackH);
        }
    }

    private void DrawLit(SKCanvas canvas, Theme theme, int i, float x, float top, float height)
    {
        float level = _level[_low + i];
        if (level <= 0.02f) return;
        _fill.Color = KeyColor(theme, _low + i, level).WithAlpha((byte)(Math.Clamp(0.25f + level * 0.75f, 0, 1) * 255));
        canvas.DrawRect(x, top, _keyW[i], height, _fill);
    }

    public override void Dispose()
    {
        _bitmap?.Dispose();
        _fill.Dispose();
    }
}
