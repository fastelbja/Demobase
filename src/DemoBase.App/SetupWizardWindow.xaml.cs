using System.Windows;
using System.Windows.Input;

namespace DemoBase.App;

public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow(
        DemoBase.Data.PreferencesService prefs,
        DemoBase.Import.DemozooImportService importService,
        DemoBase.App.ViewModels.EmulatorInstallerViewModel emulatorVm,
        DemoBase.App.Services.DbSetupDownloadService megaService,
        DemoBase.Data.DatImportService datImportService,
        DemoBase.App.Services.EmulatorSeedService seedService,
        DemoBase.App.Services.EmulatorInstallerService installerService,
        DemoBase.App.Services.LocalizationService locService,
        DemoBase.App.Services.EmulatorConfigExportService exportService,
        DemoBase.Data.ReleaseProfileOverrideExportService profileOverrideExportService)
    {
        InitializeComponent();
        DataContext = new SetupWizardViewModel(
            this, prefs, importService, emulatorVm, megaService, datImportService,
            seedService, installerService, locService, exportService, profileOverrideExportService);

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
    }

    public void CloseWizard() => Close();
}
