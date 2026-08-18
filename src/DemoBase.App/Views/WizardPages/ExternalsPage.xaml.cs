using System.Windows.Controls;
using EmulatorInstallerViewModel = DemoBase.App.ViewModels.EmulatorInstallerViewModel;

namespace DemoBase.App.Views.WizardPages;

public partial class ExternalsPage : UserControl
{
    public EmulatorInstallerViewModel Vm { get; }

    public ExternalsPage(DemoBase.App.Services.EmulatorInstallerService installerService)
    {
        InitializeComponent();
        Vm = new EmulatorInstallerViewModel(installerService, DemoBase.App.Services.EmulatorDownloadCatalog.AllExternals);
        InstallerView.DataContext = Vm;
        Vm.IsWizardMode   = true;
        Vm.IsAutoInstall  = true;

        // Auto-scroll, same pattern as EmulatorsPage — see its comment for why
        // this must be wired here and not in EmulatorInstallerView's own ctor.
        Vm.ItemStartedInstalling += item => InstallerView.ScrollToItem(item);

        Loaded += async (_, _) =>
        {
            await Vm.InitAsync();
            // Automatic installation, no user action required — same rationale
            // as the Database/Emulators/DATS steps.
            if (Vm.InstallAllCommand.CanExecute(null))
                await Vm.InstallAllCommand.ExecuteAsync(null);
        };
    }
}
