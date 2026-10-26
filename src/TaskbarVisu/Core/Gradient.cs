using System.Globalization;

namespace TaskbarVisu.Core;

/// <summary>Repère d'un dégradé personnalisé : position (0 = grave / faible, 1 = aigu / fort) et couleur #RRVVBB.</summary>
internal sealed class ColorStop
{
    public ColorStop() { }

    public ColorStop(double pos, string color)
    {
        Pos = pos;
        Color = color;
    }

    public double Pos { get; set; }
    public string Color { get; set; } = "#FFFFFF";
}

/// <summary>Dégradés personnalisés (palette « Personnalisée ») : de 2 à <see cref="MaxStops"/> couleurs.</summary>
internal static class Gradients
{
    public const int MaxStops = 8;

    /// <summary>Dégradé proposé la première fois : violet profond → magenta → orange → jaune.</summary>
    public static List<ColorStop> Default() => new()
    {
        new(0, "#2B0B5E"), new(0.45, "#B5179E"), new(0.8, "#FF5E3A"), new(1, "#FFD60A"),
    };

    public static List<ColorStop> Copy(IEnumerable<ColorStop> stops) =>
        stops.Select(s => new ColorStop(s.Pos, s.Color)).ToList();

    /// <summary>Repères valides, triés, 8 au plus ; null s'il en reste moins de deux.</summary>
    public static List<ColorStop>? Clean(List<ColorStop>? stops)
    {
        if (stops is null) return null;
        var result = stops
            .Where(s => s is not null && Parse(s.Color) is not null && double.IsFinite(s.Pos))
            .Select(s => new ColorStop(Math.Clamp(s.Pos, 0, 1), s.Color.ToUpperInvariant()))
            .OrderBy(s => s.Pos)
            .Take(MaxStops)
            .ToList();
        return result.Count >= 2 ? result : null;
    }

    /// <summary>
    /// Conversion des anciens réglages (v0.8 : deux couleurs grave / aigu) en dégradé équivalent :
    /// la couleur grave assombrie au départ, la couleur aiguë éclaircie à l'arrivée.
    /// </summary>
    public static List<ColorStop> FromLowHigh(string low, string high)
    {
        var l = Parse(low) ?? (0x3A, 0x0C, 0xA3);
        var h = Parse(high) ?? (0xFF, 0xD6, 0x0A);
        return new()
        {
            new(0, Hex(Mix(l, (0, 0, 0), 0.25))),
            new(0.17, Hex(l)),
            new(0.87, Hex(h)),
            new(1, Hex(Mix(h, (255, 255, 255), 0.6))),
        };
    }

    /// <summary>Couleur du dégradé à la position <paramref name="pos"/> (repères triés).</summary>
    public static string At(IReadOnlyList<ColorStop> stops, double pos)
    {
        if (stops.Count == 0) return "#FFFFFF";
        if (pos <= stops[0].Pos) return stops[0].Color;
        for (int i = 1; i < stops.Count; i++)
        {
            if (pos > stops[i].Pos) continue;
            var a = stops[i - 1];
            var b = stops[i];
            double t = b.Pos - a.Pos < 1e-9 ? 0 : (pos - a.Pos) / (b.Pos - a.Pos);
            return Hex(Mix(Parse(a.Color) ?? (255, 255, 255), Parse(b.Color) ?? (255, 255, 255), t));
        }
        return stops[^1].Color;
    }

    public static (int R, int G, int B)? Parse(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#'
            || !int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            return null;
        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    public static string Hex((int R, int G, int B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static (int, int, int) Mix((int R, int G, int B) a, (int R, int G, int B) b, double t) =>
        ((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
}
