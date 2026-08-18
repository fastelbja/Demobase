using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DemoBase.App;

// ─── Étape de la sidebar ─────────────────────────────────────────────────────

public partial class WizardStep : ObservableObject
{
    public string Number      { get; init; } = "";
    public string TitleKey    { get; init; } = "";
    public string SubtitleKey { get; init; } = "";

    [ObservableProperty] private string _title    = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool   _isCurrent;
    [ObservableProperty] private bool   _isDone;

    public bool HasSubtitle => !string.IsNullOrEmpty(SubtitleKey);

    /// <summary>Recharge Title et Subtitle depuis les ressources WPF courantes.</summary>
    public void RefreshLabels()
    {
        Title    = System.Windows.Application.Current.TryFindResource(TitleKey)    as string ?? TitleKey;
        Subtitle = System.Windows.Application.Current.TryFindResource(SubtitleKey) as string ?? SubtitleKey;
    }
}

// ─── ViewModel principal du wizard ───────────────────────────────────────────

public partial class SetupWizardViewModel : ObservableObject
{
    private readonly SetupWizardWindow _window;
    private readonly DemoBase.Data.PreferencesService _prefs;
    private readonly DemoBase.Import.DemozooImportService _importService;
    private readonly DemoBase.App.ViewModels.EmulatorInstallerViewModel _emulatorVm;
    private readonly DemoBase.App.Services.DbSetupDownloadService _megaService;
    private readonly DemoBase.Data.DatImportService _datImportService;
    private readonly DemoBase.App.Services.EmulatorSeedService _seedService;
    private readonly DemoBase.App.Services.EmulatorInstallerService _installerService;
    private readonly DemoBase.App.Services.LocalizationService _locService;
    private readonly DemoBase.App.Services.EmulatorConfigExportService _exportService;
    private readonly DemoBase.Data.ReleaseProfileOverrideExportService _profileOverrideExportService;

    // Pages du wizard (UserControl instancié à la demande)
    private readonly Lazy<FrameworkElement>[] _pages;

    [ObservableProperty] private int              _currentIndex;
    [ObservableProperty] private FrameworkElement? _currentPageView;
    [ObservableProperty] private bool              _canGoBack;
    [ObservableProperty] private bool              _isLastStep;
    [ObservableProperty] private bool              _isStepBusy;
    [ObservableProperty] private bool              _canGoBackEnabled;

    private readonly System.Windows.Threading.DispatcherTimer _busyWatcher;

    public List<WizardStep> Steps { get; }

    /// <summary>Exposé pour que la fenêtre puisse afficher la progression de
    /// téléchargement en cours dans la barre de navigation (entre Annuler et
    /// Précédent) — la liste compacte de la page Émulateurs n'a pas la place
    /// pour une barre de progression par ligne.</summary>
    public DemoBase.App.ViewModels.EmulatorInstallerViewModel EmulatorVm => _emulatorVm;

    public SetupWizardViewModel(
        SetupWizardWindow window,
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
        _window           = window;
        _prefs            = prefs;
        _importService    = importService;
        _emulatorVm       = emulatorVm;
        _megaService      = megaService;
        _datImportService = datImportService;
        _seedService      = seedService;
        _installerService = installerService;
        _locService       = locService;
        _exportService    = exportService;
        _profileOverrideExportService = profileOverrideExportService;

        // Surveille en continu si la page courante a une opération bloquante en
        // cours (import DB, installation d'émulateurs) pour griser Suivant/
        // Précédent/Annuler. Plus simple et robuste qu'un abonnement/désabonnement
        // PropertyChanged par page — coût négligeable (vérification de quelques
        // booléens toutes les 250 ms).
        _busyWatcher = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _busyWatcher.Tick += (_, _) =>
        {
            IsStepBusy       = IsBusyOnCurrentPage();
            CanGoBackEnabled = CanGoBack && !IsStepBusy;
        };
        _busyWatcher.Start();

        // Définir les étapes de la sidebar
        Steps =
        [
            new() { Number = "1", TitleKey = "Wiz_StepWelcome" },
            new() { Number = "2", TitleKey = "Wiz_StepFolders",   SubtitleKey = "Wiz_StepFoldersSub" },
            new() { Number = "3", TitleKey = "Wiz_StepDatabase",  SubtitleKey = "Wiz_StepDatabaseSub" },
            new() { Number = "4", TitleKey = "Wiz_StepEmulators", SubtitleKey = "Wiz_StepEmulatorsSub" },
            new() { Number = "5", TitleKey = "Wiz_StepExternals", SubtitleKey = "Wiz_StepExternalsSub" },
            new() { Number = "6", TitleKey = "Wiz_StepBios",      SubtitleKey = "Wiz_StepBiosSub" },
            new() { Number = "7", TitleKey = "Wiz_StepDats",      SubtitleKey = "Wiz_StepDatsSub" },
            new() { Number = "8", TitleKey = "Wiz_StepReady" },
        ];

        // Charger les libellés initiaux puis les rafraîchir à chaque changement de langue
        foreach (var s in Steps) s.RefreshLabels();
        _locService.LanguageChanged += () => { foreach (var s in Steps) s.RefreshLabels(); };

        // Pages WPF associées (lazy — instanciées seulement quand on y accède)
        _pages =
        [
            new Lazy<FrameworkElement>(() => new DemoBase.App.Views.WizardPages.WelcomePage(_locService, _prefs)),
            new Lazy<FrameworkElement>(() => new DemoBase.App.Views.WizardPages.FoldersPage()),
            new Lazy<FrameworkElement>(() => new DemoBase.App.Views.WizardPages.DatabasePage(_importService)),
            new Lazy<FrameworkElement>(() => new DemoBase.App.Views.WizardPages.EmulatorsPage(_emulatorVm)),
            new Lazy<FrameworkElement>(() => new DemoBase.App.Views.WizardPages.ExternalsPage(_installerService)),
            new Lazy<FrameworkElement>(() => new DemoBase.App.Views.WizardPages.BiosPage()),
            new Lazy<FrameworkElement>(() => new DemoBase.App.Views.WizardPages.DatsPage(_megaService, _datImportService)),
            new Lazy<FrameworkElement>(() => new DemoBase.App.Views.WizardPages.ReadyPage(_seedService, _prefs, _megaService, _exportService, _profileOverrideExportService)),
        ];

        GoTo(0);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    /// <summary>
    /// True si la page courante a une opération bloquante en cours (import base de
    /// données ou installation d'émulateurs). Dans ce cas, Annuler/Suivant/Précédent
    /// sont désactivés pour éviter de couper un téléchargement ou une écriture SQLite.
    /// </summary>
    private bool IsBusyOnCurrentPage()
    {
        if (CurrentIndex == 2 && _pages[2].IsValueCreated &&
            _pages[2].Value is DemoBase.App.Views.WizardPages.DatabasePage dp &&
            dp.Vm.IsImporting)
            return true;

        if (CurrentIndex == 3 && _pages[3].IsValueCreated &&
            _pages[3].Value is DemoBase.App.Views.WizardPages.EmulatorsPage ep &&
            (ep.Vm.IsInstallingAll || ep.Vm.Items.Any(i => i.IsInstalling)))
            return true;

        if (CurrentIndex == 4 && _pages[4].IsValueCreated &&
            _pages[4].Value is DemoBase.App.Views.WizardPages.ExternalsPage xp &&
            (xp.Vm.IsInstallingAll || xp.Vm.Items.Any(i => i.IsInstalling)))
            return true;

        if (CurrentIndex == 6 && _pages[6].IsValueCreated &&
            _pages[6].Value is DemoBase.App.Views.WizardPages.DatsPage dap &&
            dap.Vm.IsImporting)
            return true;

        return false;
    }

    /// <summary>
    /// Retourne un message d'erreur si l'étape courante n'est pas complète, ou
    /// null si on peut avancer. Toutes les étapes du wizard sont obligatoires —
    /// DemoBase doit sortir d'une configuration initiale entièrement faite,
    /// <summary>
    /// Retourne un message de confirmation (Oui/Non) pour les étapes optionnelles
    /// où l'utilisateur doit confirmer qu'il veut vraiment continuer sans avoir rien fait.
    /// </summary>
    private string? GetConfirmationMessage()
    {
        // Étape Émulateurs : demander confirmation si aucun émulateur installé
        if (CurrentIndex == 3
            && _pages[3].IsValueCreated
            && _pages[3].Value is DemoBase.App.Views.WizardPages.EmulatorsPage ep)
        {
            var vm = ep.Vm;
            bool noneInstalled = vm.Items.Count > 0 && !vm.Items.Any(i => i.IsInstalled);
            if (noneInstalled)
                return DemoBase.App.Services.LocalizationService.Get("Wiz_EmuNoInstallConfirm")
                    ?? "Aucun émulateur n'a été installé.\n\nVous ne pourrez pas lancer de productions sans émulateurs.\n\nVoulez-vous continuer quand même ?";
        }
        return null;
    }

    /// pas à moitié (chemins valides, catalogue Demozoo importé, émulateurs au
    /// moins tentés, fichiers DAT importés).
    /// </summary>
    private string? GetIncompleteStepMessage()
    {
        switch (CurrentIndex)
        {
            case 1: // Dossiers
                if (_pages[1].IsValueCreated &&
                    _pages[1].Value is DemoBase.App.Views.WizardPages.FoldersPage fp &&
                    !fp.Vm.AllPathsValid)
                    return "All folders must have a path before continuing.";
                return null;

            case 2: // Base de données
                if (!_pages[2].IsValueCreated) // page jamais affichée → import jamais lancé
                    return "DemoBase cannot work without the Demozoo catalog.\n\n" +
                           "Please download and import the database before continuing.";
                if (_pages[2].Value is DemoBase.App.Views.WizardPages.DatabasePage dp && !dp.Vm.ImportSucceeded)
                    return "DemoBase cannot work without the Demozoo catalog.\n\n" +
                           "Please download and import the database before continuing.";
                return null;

            case 3: // Émulateurs — optionnel
                return null;

            case 4: // External tools
                if (!_pages[4].IsValueCreated)
                    return "Please wait while the external tools list is loading.";
                if (_pages[4].Value is DemoBase.App.Views.WizardPages.ExternalsPage xp && !xp.Vm.HasCompletedInitialPass)
                    return "Please let the external tools installation finish before continuing.\n\n" +
                           "Failed downloads can be retried later without blocking this step.";
                return null;

            case 5: // Pack BIOS — optionnel
                return null;

            case 6: // DAT files
                if (!_pages[6].IsValueCreated)
                    return "Please download and import the DAT files before continuing.";
                if (_pages[6].Value is DemoBase.App.Views.WizardPages.DatsPage dap && !dap.Vm.ImportSucceeded)
                    return "Please download and import the DAT files before continuing.";
                return null;

            default:
                return null;
        }
    }

    [RelayCommand]
    private async Task Next()
    {
        if (IsBusyOnCurrentPage()) return;

        var incompleteMessage = GetIncompleteStepMessage();
        if (incompleteMessage != null)
        {
            System.Windows.MessageBox.Show(
                _window, incompleteMessage, "Incomplete step",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        // Confirmation pour les étapes optionnelles mais importantes
        var confirmMessage = GetConfirmationMessage();
        if (confirmMessage != null)
        {
            var result = System.Windows.MessageBox.Show(
                _window, confirmMessage,
                _locService.CurrentLanguage == "fr" ? "Continuer ?" : "Continue?" ?? "Continuer sans emulateurs ?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes)
                return;
        }

        // Valider/sauvegarder la page courante avant de naviguer
        if (CurrentIndex == 1 && _pages[1].IsValueCreated &&
            _pages[1].Value is DemoBase.App.Views.WizardPages.FoldersPage fp)
            await fp.Vm.CommitAsync(_prefs);

        if (CurrentIndex < _pages.Length - 1)
            GoTo(CurrentIndex + 1);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (IsBusyOnCurrentPage()) return;
        if (CurrentIndex > 0)
            GoTo(CurrentIndex - 1);
    }

    [RelayCommand]
    private async Task Finish()
    {
        _busyWatcher.Stop();
        // Création physique des dossiers configurés — seul moment du wizard
        // où l'on touche le système de fichiers pour les chemins utilisateur.
        DemoBase.App.Services.AppPaths.CreateDirectories();

        // Seul point de tout le wizard qui empêche sa réouverture au prochain
        // lancement — tant que ce flag n'est pas écrit, App.xaml.cs rouvrira
        // systématiquement le wizard (cf. son commentaire détaillé).
        await _prefs.MarkWizardCompletedAsync();

        // Copier les profils WinUAE si WinUAE a été installé pendant le wizard

        _window.CloseWizard();
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusyOnCurrentPage())
        {
            MessageBox.Show(
                _window,
                "An operation is in progress (download or installation).\n" +
                "Please wait for it to finish before canceling.",
                "Operation in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            _window,
            "Do you really want to cancel the setup?\n\n" +
            "You can relaunch this wizard from the Help menu.",
            "Cancel setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _busyWatcher.Stop();
            _window.CloseWizard();
        }
    }


    private void GoTo(int index)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsCurrent = i == index;
            Steps[i].IsDone    = i < index;
        }

        CurrentIndex    = index;
        CurrentPageView = _pages[index].Value;
        CanGoBack       = CurrentIndex > 0;
        IsLastStep      = CurrentIndex == _pages.Length - 1;
        IsStepBusy       = IsBusyOnCurrentPage();
        CanGoBackEnabled = CanGoBack && !IsStepBusy;

        BackCommand.NotifyCanExecuteChanged();
    }

    // ── Placeholder pour les pages pas encore implémentées ───────────────────

    private static FrameworkElement BuildPlaceholder(string title, string icon, string desc)
    {
        var panel = new System.Windows.Controls.StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(new TextBlock
        {
            Text              = icon,
            FontSize          = 48,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin            = new Thickness(0, 0, 0, 20),
        });

        panel.Children.Add(new TextBlock
        {
            Text       = title,
            FontSize   = 28,
            FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
            Margin     = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock
        {
            Text        = desc,
            FontSize    = 14,
            Foreground  = (System.Windows.Media.Brush)Application.Current.Resources["TextSecondary"],
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text       = "This step will be available soon.",
            FontSize   = 12,
            Margin     = new Thickness(0, 24, 0, 0),
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
        });

        return new ContentControl { Content = panel };
    }
}
