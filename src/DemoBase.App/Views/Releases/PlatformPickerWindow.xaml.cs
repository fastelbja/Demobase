using CommunityToolkit.Mvvm.ComponentModel;
using DemoBase.Core.Models;
using System.Collections.ObjectModel;
using System.Windows;

namespace DemoBase.App.Views.Releases;

/// <summary>
/// Fenêtre modale de choix de plateforme/profil — affichée par
/// ReleaseDetailViewModel.ResolveOrPromptEmulatorConfigIdAsync quand une release
/// multi-plateforme est lancée sans profil déjà assigné au fichier sélectionné
/// (2026-07-25, retour utilisateur : releases multi-plateforme ET multi-fichier,
/// ex. Amiga AGA + Atari Falcon, où un seul override par release ne suffit pas).
/// </summary>
public partial class PlatformPickerWindow : Window
{
    public PlatformPickerViewModel Vm { get; }

    /// <summary>Profil choisi par l'utilisateur, ou null si annulé (vérifier
    /// DialogResult == true avant de lire cette propriété).</summary>
    public EmulatorConfig? SelectedProfile => Vm.SelectedProfile;

    public PlatformPickerWindow(IEnumerable<EmulatorConfig> profiles, string? fileLabel)
    {
        InitializeComponent();
        Vm = new PlatformPickerViewModel(profiles, fileLabel);
        DataContext = Vm;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedProfile == null)
        {
            // 2026-07-25 : remplace la MessageBox.Show native (jamais traduite,
            // hors charte graphique) par SimpleInfoDialog — même correctif que
            // celui déjà appliqué à Services.cs/ExternalDownloadConfirmWindow.
            new DemoBase.App.Views.SimpleInfoDialog(
                "PPick_NoneSelected_Title", "PPick_NoneSelected_Heading", "PPick_NoneSelected_Body")
            { Owner = this }.ShowDialog();
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}

/// <summary>DataContext minimal de PlatformPickerWindow — pas de service injecté,
/// juste de quoi porter les bindings (liste de profils, sélection, libellé fichier).</summary>
public partial class PlatformPickerViewModel : ObservableObject
{
    public ObservableCollection<EmulatorConfig> Profiles { get; }

    [ObservableProperty] private EmulatorConfig? _selectedProfile;
    [ObservableProperty] private string? _fileLabel;

    public bool HasFileLabel => !string.IsNullOrWhiteSpace(FileLabel);

    public PlatformPickerViewModel(IEnumerable<EmulatorConfig> profiles, string? fileLabel)
    {
        Profiles  = new ObservableCollection<EmulatorConfig>(profiles);
        FileLabel = fileLabel;
        // Présélectionner le profil actuellement marqué IsDefault pour SA plateforme,
        // s'il y en a un dans la liste — évite une liste vide sélectionnée par défaut.
        SelectedProfile = Profiles.FirstOrDefault(p => p.IsDefault) ?? Profiles.FirstOrDefault();
    }

    partial void OnFileLabelChanged(string? value) => OnPropertyChanged(nameof(HasFileLabel));
}
