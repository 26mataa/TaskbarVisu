using TaskbarVisu.Modules;

namespace TaskbarVisu.Core;

/// <summary>Sélection de modules proposée par le guide de démarrage : modules affichés à gauche et à droite, dans l'ordre.</summary>
internal sealed record ModulePack(string Name, string Description, string[] Left, string[] Right);

internal static class ModulePacks
{
    public static readonly ModulePack[] All =
    {
        new("Studio", "Pour mixer : niveaux et sonie, spectrogramme, spectre · image stéréo, accordeur, forme d'onde.",
            new[] { "levels", "spectrogram", "spectrum" }, new[] { "imager", "tuner", "waveform" }),
        new("Musicien", "Pour jouer : spectre et piano roll · accordeur et forme d'onde.",
            new[] { "spectrum", "pianoroll" }, new[] { "tuner", "waveform" }),
        new("Minimal", "L'essentiel, discret : un spectre et un VU-mètre.",
            new[] { "spectrum" }, new[] { "vu" }),
    };

    /// <summary>Affiche les modules du pack dans son ordre et masque les autres (leurs réglages sont conservés).</summary>
    public static void Apply(AppSettings s, ModulePack pack)
    {
        var rest = s.AllModules.ToList();
        ModuleConfig Take(string id)
        {
            var cfg = rest.FirstOrDefault(m => m.Id == id) ?? ModuleCatalog.CreateConfig(id);
            rest.Remove(cfg);
            cfg.Enabled = true;
            return cfg;
        }

        var left = pack.Left.Select(Take).ToList();
        var right = pack.Right.Select(Take).ToList();
        foreach (var cfg in rest)
        {
            cfg.Enabled = false;
            (s.Left.Contains(cfg) ? left : right).Add(cfg);
        }
        s.Left = left;
        s.Right = right;
    }

    /// <summary>Le pack qui correspond exactement aux modules affichés, ou null.</summary>
    public static ModulePack? Current(AppSettings s) => All.FirstOrDefault(p =>
        s.Left.Where(m => m.Enabled).Select(m => m.Id).SequenceEqual(p.Left) &&
        s.Right.Where(m => m.Enabled).Select(m => m.Id).SequenceEqual(p.Right));
}
