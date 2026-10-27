using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TaskbarVisu.Core;

namespace TaskbarVisu.UI;

/// <summary>Page Général, section Aide et diagnostic : consommation en direct et rapport de bug en un clic.</summary>
public partial class SettingsWindow
{
    private readonly UsageMeter _usage = new();
    private TextBlock? _usageTitle;
    private string _usageText = string.Empty;

    private void BuildDiagnostics(Panel p)
    {
        p.Children.Add(Section("Aide et diagnostic"));

        var card = Card(string.Empty, null, "", "Mesurée en direct. Moins de modules, moins d'images par seconde ou l'économie d'énergie la réduisent.");
        _usageTitle = (TextBlock)((CardParts)card.Tag).Texts.Children[0];
        p.Children.Add(card);
        UpdateUsage();

        // Le bouton confirme la copie en changeant de texte.
        var button = new Button { Content = Loc.T("Copier le rapport et ouvrir GitHub"), MinWidth = 120 };
        button.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(BugReport.Build(_usageText));
                button.Content = Loc.T("Rapport copié : collez-le dans le ticket (Ctrl+V)");
                // Le lien ne contient que du texte fixe : le rapport, lui, passe par le presse-papiers.
                string body = Uri.EscapeDataString(Loc.T("Décrivez le problème ici, puis collez le rapport (Ctrl+V) en dessous."));
                Process.Start(new ProcessStartInfo($"https://github.com/{UpdateChecker.Repository}/issues/new?body={body}") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Error("Rapport de bug", ex); }
        };
        p.Children.Add(Card("Signaler un problème", button, "",
            "Copie la version, Windows, vos réglages et la fin du journal (sans votre nom d'utilisateur), puis ouvre un nouveau ticket : collez le rapport avec Ctrl+V."));
    }

    /// <summary>Chaque seconde : la mesure tourne toujours (pour le rapport), l'affichage seulement sur la page Général.</summary>
    private void UpdateUsage()
    {
        var (cpu, memory) = _usage.Sample();
        _usageText = Loc.F("Processeur {0:0.0} % · mémoire {1:0} Mo", cpu, memory);
        if (_usageTitle is not null && Tabs.SelectedItem == TabGeneral)
            _usageTitle.Text = Loc.F("Consommation de TaskbarVisu : {0}", _usageText);
    }
}
