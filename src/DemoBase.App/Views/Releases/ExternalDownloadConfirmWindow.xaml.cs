using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;

namespace DemoBase.App.Views.Releases;

/// <summary>
/// Fenêtre de confirmation avant de télécharger à la volée un fichier pas encore couvert
/// par un DAT, directement depuis un lien Demozoo — cf.
/// ReleaseDetailViewModel.LaunchAsync (2026-07-25, RESUME_PROJET.md). Remplace l'ancienne
/// MessageBox.Show native (fenêtre système, hors charte graphique) par une fenêtre au même
/// style que PlatformPickerWindow/FilePickerWindow, avec une case "Ne plus demander".
/// </summary>
public partial class ExternalDownloadConfirmWindow : Window
{
    public ExternalDownloadConfirmViewModel Vm { get; }

    /// <summary>Vrai si l'utilisateur a coché "Ne plus demander pour les prochaines
    /// releases" — à lire seulement si DialogResult == true (l'utilisateur a confirmé le
    /// téléchargement) ; ignoré si annulé.</summary>
    public bool DontAskAgain => Vm.DontAskAgain;

    public ExternalDownloadConfirmWindow(string fileLabel, string hostLabel)
    {
        InitializeComponent();
        Vm = new ExternalDownloadConfirmViewModel(fileLabel, hostLabel);
        DataContext = Vm;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e)  => DialogResult = false;
}

/// <summary>DataContext minimal de ExternalDownloadConfirmWindow — pas de service
/// injecté, juste de quoi porter les bindings (libellés fichier/hébergeur, case à
/// cocher).</summary>
public partial class ExternalDownloadConfirmViewModel : ObservableObject
{
    [ObservableProperty] private string _fileLabel;
    [ObservableProperty] private string _hostLabel;
    [ObservableProperty] private bool   _dontAskAgain;

    public ExternalDownloadConfirmViewModel(string fileLabel, string hostLabel)
    {
        _fileLabel = fileLabel;
        _hostLabel = hostLabel;
    }
}
