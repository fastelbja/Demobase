using System.Windows;

namespace DemoBase.App.Views;

/// <summary>
/// Popup d'information ponctuelle affichée au lancement d'un disque TR-DOS (.trd) via
/// ZEsarUX, expliquant les deux commandes TR-DOS à taper à la main (RANDOMIZE USR 15616
/// puis RUN "NOM") — cf. ZEsarUXLauncher.cs. Abandon des tentatives d'automatisation
/// (simulation de touche NMI, protocole distant ZRCP) : la première n'a pas de mapping
/// clavier fiable/documenté, la seconde demande une exception pare-feu Windows que les
/// utilisateurs n'apprécient pas.
/// </summary>
public partial class TrDosInfoDialog : Window
{
    /// <summary>Vrai si l'utilisateur a coché "Ne plus afficher ce message".</summary>
    public bool DontShowAgain { get; private set; }

    public TrDosInfoDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DontShowAgain = DontShowAgainCheck.IsChecked == true;
        DialogResult = true;
    }
}
