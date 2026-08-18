using System.Windows.Controls;
using EmulatorInstallerViewModel = DemoBase.App.ViewModels.EmulatorInstallerViewModel;

namespace DemoBase.App.Views.WizardPages;

public partial class EmulatorsPage : UserControl
{
    public EmulatorInstallerViewModel Vm { get; }

    public EmulatorsPage(EmulatorInstallerViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        InstallerView.DataContext = vm;
        vm.IsWizardMode = true;

        // L'auto-scroll doit être câblé ICI : EmulatorInstallerView est instanciée
        // par le compilateur XAML via son constructeur sans paramètre, donc tout
        // abonnement fait dans un éventuel constructeur paramétré de cette classe
        // ne serait jamais exécuté (bug vécu précédemment).
        vm.ItemStartedInstalling += item => InstallerView.ScrollToItem(item);

        Loaded += async (_, _) =>
        {
            await vm.InitAsync();
            // En mode wizard : la phase de sélection est activée par InitAsync —
            // l'installation ne démarre qu'une fois que l'utilisateur a confirmé.
        };
    }
}
