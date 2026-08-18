using System.Windows;

namespace DemoBase.App;

/// <summary>
/// Fenêtre standalone pour télécharger/mettre à jour les émulateurs et outils
/// externes en dehors du wizard — accessible depuis Émulateurs (bouton
/// "⬇ Download / Update"). Le wizard ne se rouvre plus une fois terminé
/// (cf. PrefKeys.WizardCompleted), donc c'est le seul moyen de relancer un
/// téléchargement en échec ou de vérifier les mises à jour après la
/// configuration initiale.
///
/// Contrairement aux pages du wizard, l'installation n'est PAS déclenchée
/// automatiquement à l'ouverture — l'utilisateur clique explicitement sur
/// "Installer/mettre à jour tout" ou réessaie ligne par ligne, pour ne pas
/// surprendre par un téléchargement de masse non demandé en dehors du
/// contexte d'installation initiale.
/// </summary>
public partial class EmulatorDownloadManagerWindow : Window
{
    public EmulatorDownloadManagerWindow(
        DemoBase.App.Services.EmulatorInstallerService installerService,
        DemoBase.App.Services.EmulatorConfigExportService? exportService = null,
        DemoBase.Data.ReleaseProfileOverrideExportService? profileOverrideExportService = null)
    {
        InitializeComponent();

        var emulatorsVm = new DemoBase.App.ViewModels.EmulatorInstallerViewModel(
            installerService, DemoBase.App.Services.EmulatorDownloadCatalog.AllEmulators,
            exportService, profileOverrideExportService);
        EmulatorsView.DataContext = emulatorsVm;

        var externalsVm = new DemoBase.App.ViewModels.EmulatorInstallerViewModel(
            installerService, DemoBase.App.Services.EmulatorDownloadCatalog.AllExternals);
        ExternalsView.DataContext = externalsVm;

        // Auto-scroll — même câblage que EmulatorsPage/ExternalsPage du wizard.
        emulatorsVm.ItemStartedInstalling += item => EmulatorsView.ScrollToItem(item);
        externalsVm.ItemStartedInstalling += item => ExternalsView.ScrollToItem(item);

        // Uniquement l'état d'installation local (rapide, aucun téléchargement) —
        // pas d'auto-déclenchement de InstallAllCommand ici, contrairement au wizard.
        Loaded += async (_, _) =>
        {
            await emulatorsVm.InitAsync();
            await externalsVm.InitAsync();
        };
    }
}
