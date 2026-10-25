using TaskbarVisu.Core;

namespace TaskbarVisu.Rendering;

/// <summary>Répartit les modules d'une zone sur sa largeur (positions relatives à la zone, en pixels).</summary>
internal static class ModuleLayout
{
    /// <param name="sizes">Largeur préférée et largeur minimale de chaque module, dans l'ordre.</param>
    /// <param name="fill">Étire les modules pour remplir toute la zone ; sinon taille préférée, alignés selon <paramref name="align"/>.</param>
    public static List<(float X, float Width)> Compute(List<(float Preferred, float Min)> sizes, float zoneWidth,
        float spacing, bool fill, ZoneAlign align)
    {
        var items = sizes.ToList();
        while (items.Count > 0)
        {
            float gaps = spacing * (items.Count - 1);
            float available = zoneWidth - gaps;
            float sumPref = items.Sum(i => i.Preferred);
            float sumMin = items.Sum(i => i.Min);

            // Pas assez de place même au minimum : on retire le dernier module de la zone.
            if (sumMin > available)
            {
                items.RemoveAt(items.Count - 1);
                continue;
            }

            var widths = new float[items.Count];
            if (sumPref <= available)
            {
                float k = fill ? available / sumPref : 1f;
                for (int i = 0; i < items.Count; i++) widths[i] = items[i].Preferred * k;
            }
            else
            {
                // On rétrécit chaque module proportionnellement à sa marge (préférée - minimale).
                float deficit = sumPref - available;
                float flex = Math.Max(1e-3f, sumPref - sumMin);
                for (int i = 0; i < items.Count; i++)
                    widths[i] = items[i].Preferred - (items[i].Preferred - items[i].Min) * deficit / flex;
            }

            float total = widths.Sum() + gaps;
            float free = Math.Max(0, zoneWidth - total);
            float x = align switch
            {
                ZoneAlign.Start => 0,
                ZoneAlign.End => free,
                _ => free / 2,
            };
            var result = new List<(float, float)>(items.Count);
            foreach (var w in widths)
            {
                float x0 = MathF.Round(x), x1 = MathF.Round(x + w);
                result.Add((x0, x1 - x0));
                x += w + spacing;
            }
            return result;
        }
        return new List<(float, float)>();
    }
}
