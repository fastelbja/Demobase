using System.Windows;

namespace DemoBase.App.Views;

/// <summary>
/// Écran explicatif affiché avant le sélecteur de dossier de "Scan for Releases"
/// (2026-07-28, demande utilisateur). Même gabarit que WinUAEInfoDialog/HatariInfoDialog —
/// bouton "Compris" pour continuer vers le sélecteur de dossier, ou "Annuler" pour abandonner
/// sans rien faire. La case "Ne plus afficher ce message" n'est prise en compte que si
/// l'utilisateur a bien continué (Compris) — annuler ne doit pas masquer silencieusement
/// l'explication d'une prochaine tentative.
/// </summary>
public partial class RomScanInfoDialog : Window
{
    public bool DontShowAgain { get; private set; }

    public RomScanInfoDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DontShowAgain = DontShowAgainCheck.IsChecked == true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
