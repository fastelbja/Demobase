using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;

namespace DemoBase.App.ViewModels;

// ─── Item d'émulateur dans la liste ──────────────────────────────────────────

public partial class EmulatorInstallItemViewModel : ObservableObject
{
    public DemoBase.App.Services.EmulatorDownloadEntry Entry { get; }

    [ObservableProperty] private bool   _isInstalled;
    [ObservableProperty] private string _installedVersion = "";
    [ObservableProperty] private bool   _updateAvailable;
    [ObservableProperty] private string _latestVersion    = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotInstalling))]
    private bool   _isInstalling;
    [ObservableProperty] private bool   _isChecking;
    [ObservableProperty] private string _statusMessage    = "";
    [ObservableProperty] private int    _progressPercent;
    [ObservableProperty] private bool   _hasNote;
    [ObservableProperty] private bool   _hasFailed;
    [ObservableProperty] private string _errorDetails     = "";

    /// <summary>Coché par défaut — l'utilisateur peut décocher en mode wizard
    /// avant de lancer l'installation.</summary>
    [ObservableProperty] private bool _isSelectedForInstall = true;

    public bool IsNotInstalling => !IsInstalling;

    public string  FolderName   => Entry.FolderName;
    public string  DisplayName  => Entry.DisplayName;
    public string? Systems      => Entry.Systems;
    public string? Note         => Entry.Note;
    public bool    IsBusy       => IsInstalling || IsChecking;

    public EmulatorInstallItemViewModel(DemoBase.App.Services.EmulatorDownloadEntry entry)
    {
        Entry    = entry;
        HasNote  = !string.IsNullOrEmpty(entry.Note);
    }
}

// ─── ViewModel principal ──────────────────────────────────────────────────────

public partial class EmulatorInstallerViewModel : ObservableObject
{
    private readonly DemoBase.App.Services.EmulatorInstallerService    _installer;
    private DemoBase.App.Services.EmulatorConfigExportService?         _exportService;
    private DemoBase.Data.ReleaseProfileOverrideExportService?         _profileOverrideExportService;

    public void SetExportService(DemoBase.App.Services.EmulatorConfigExportService svc)
        => _exportService = svc;

    [ObservableProperty] private bool   _isRefreshing;
    [ObservableProperty] private string _globalStatus = "";
    [ObservableProperty] private bool   _isInstallingAll;

    /// <summary>True une fois la passe d'installation automatique terminée au
    /// moins une fois (succès ou échec par item — les échecs ont un bouton
    /// Réessayer). Utilisé par le wizard pour bloquer "Suivant" tant que
    /// l'utilisateur n'a pas au moins laissé la tentative se dérouler.</summary>
    [ObservableProperty] private bool   _hasCompletedInitialPass;

    // ── Progression globale (affichée dans la barre de navigation du wizard,
    //    entre les boutons Annuler et Précédent — la liste en lignes compactes
    //    n'a pas la place pour une barre par ligne) ──────────────────────────
    [ObservableProperty] private bool   _isDownloadingAny;
    [ObservableProperty] private string _currentDownloadLabel = "";
    [ObservableProperty] private int    _currentDownloadPercent;
    [ObservableProperty] private bool   _hasErrors;

    /// <summary>Quand true (page wizard), masque les boutons "Check for updates"
    /// et "Error log" qui n'ont pas de sens lors du setup initial.</summary>
    [ObservableProperty] private bool   _isWizardMode;

    /// <summary>Quand true, l'installation démarre automatiquement sans phase de
    /// sélection (ex. External Tools — obligatoires). Ignoré hors wizard mode.</summary>
    [ObservableProperty] private bool   _isAutoInstall;

    /// <summary>True pendant la phase de sélection en wizard (avant le lancement
    /// de l'installation). False une fois que l'utilisateur a confirmé sa sélection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallPhase))]
    private bool _isSelectionPhase;

    public bool IsInstallPhase => !IsSelectionPhase;

    /// <summary>Nombre d'émulateurs cochés pour l'installation.</summary>
    public int SelectedCount => Items.Count(i => i.IsSelectedForInstall);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items) item.IsSelectedForInstall = true;
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items) item.IsSelectedForInstall = false;
        OnPropertyChanged(nameof(SelectedCount));
    }

    /// <summary>Confirme la sélection et passe à la phase d'installation.</summary>
    [RelayCommand]
    private async Task ConfirmSelection()
    {
        IsSelectionPhase = false;
        GlobalStatus     = $"Installing {SelectedCount} emulator(s)…";

        // N'installer que les items cochés (et non déjà installés)
        var targets = Items.Where(i => i.IsSelectedForInstall && !i.IsInstalled && !i.IsBusy).ToList();
        foreach (var item in targets)
            await Install(item);

        IsInstallingAll = false;
        HasCompletedInitialPass = true;
        GlobalStatus = $"{Items.Count(i => i.IsInstalled)}/{Items.Count} emulators installed";
    }

    public ObservableCollection<EmulatorInstallItemViewModel> Items { get; } = [];

    /// <summary>Levé juste avant le démarrage du téléchargement d'un item — la vue
    /// s'y abonne pour faire défiler automatiquement jusqu'à l'élément concerné.</summary>
    public event Action<EmulatorInstallItemViewModel>? ItemStartedInstalling;

    /// <summary>Dossier racine de cette instance (ex. "Emus" pour les émulateurs,
    /// "Externals" pour les outils tiers) — déduit du premier item du catalogue
    /// fourni, puisque toutes les entrées d'une même page partagent le même
    /// RootFolder par construction.</summary>
    private readonly string _rootFolder;

    public string EmusRoot =>
        DemoBase.App.Services.EmulatorInstallerService.GetRoot(_rootFolder);

    /// <summary>Chemin relatif au dossier de l'application, pour l'affichage UI
    /// (ex. ".\Emus" plutôt que "C:\...\bin\Release\net8.0-windows\Emus").</summary>
    public string EmusRootDisplay
    {
        get
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            return EmusRoot.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)
                ? $".\\{EmusRoot[baseDir.Length..].TrimStart('\\', '/')}"
                : EmusRoot;
        }
    }

    private string LogFile => System.IO.Path.Combine(EmusRoot, "install_errors.log");

    /// <summary>
    /// </summary>
    /// <param name="installer">Service de téléchargement partagé.</param>
    /// <param name="catalog">Sous-ensemble du catalogue à afficher — toutes les
    /// entrées doivent partager le même RootFolder. Par défaut : tous les
    /// émulateurs (EmulatorDownloadCatalog.AllEmulators).</param>
    public EmulatorInstallerViewModel(
        DemoBase.App.Services.EmulatorInstallerService installer,
        System.Collections.Generic.IReadOnlyList<DemoBase.App.Services.EmulatorDownloadEntry>? catalog = null,
        DemoBase.App.Services.EmulatorConfigExportService? exportService = null,
        DemoBase.Data.ReleaseProfileOverrideExportService? profileOverrideExportService = null)
    {
        _installer                     = installer;
        _exportService                 = exportService;
        _profileOverrideExportService  = profileOverrideExportService;
        catalog ??= DemoBase.App.Services.EmulatorDownloadCatalog.AllEmulators;
        _rootFolder = catalog.Count > 0 ? catalog[0].RootFolder : "Emus";

        foreach (var entry in catalog)
            Items.Add(new EmulatorInstallItemViewModel(entry));
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    public async Task InitAsync()
    {
        IsRefreshing = true;
        GlobalStatus = "Checking installed emulators…";
        try
        {
            await Parallel.ForEachAsync(Items,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                async (item, ct) =>
            {
                var isInstalled = _installer.IsInstalled(item.Entry);
                var version     = await _installer.GetInstalledVersionAsync(item.FolderName, item.Entry.RootFolder);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    item.IsInstalled      = isInstalled;
                    item.InstalledVersion = version?.Version ?? (isInstalled ? "?" : "");
                    item.StatusMessage    = isInstalled
                        ? "Installed"
                        : DemoBase.App.Services.LocalizationService.Get("Emu_StatusNotInstalled");
                });
            });
            GlobalStatus = $"{Items.Count(i => i.IsInstalled)}/{Items.Count} emulators installed";

            // En mode wizard : passer en phase de sélection après le scan initial.
            // L'utilisateur choisit ce qu'il veut installer avant de lancer.
            // Exception : IsAutoInstall (External Tools) — installation directe sans sélection.
            if (IsWizardMode && !IsAutoInstall)
            {
                IsSelectionPhase = true;
                // Décocher les émulateurs déjà installés (pas besoin de les réinstaller)
                foreach (var item in Items.Where(i => i.IsInstalled))
                    item.IsSelectedForInstall = false;
                OnPropertyChanged(nameof(SelectedCount));
                GlobalStatus = $"Select the emulators to install ({SelectedCount} selected)";
            }
        }
        finally { IsRefreshing = false; }
    }

    // ── Installer un seul émulateur ───────────────────────────────────────────

    [RelayCommand]
    private async Task Install(EmulatorInstallItemViewModel item)
    {
        if (item.IsBusy) return;

        item.IsInstalling    = true;
        item.HasFailed       = false;
        item.ProgressPercent = 0;
        item.StatusMessage   = DemoBase.App.Services.LocalizationService.Get("Emu_StatusStarting");
        ItemStartedInstalling?.Invoke(item);

        IsDownloadingAny       = true;
        CurrentDownloadLabel   = item.DisplayName;
        CurrentDownloadPercent = 0;

        var progress = new Progress<(string Message, int Percent)>(p =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                item.StatusMessage     = p.Message;
                item.ProgressPercent   = p.Percent;
                CurrentDownloadLabel   = item.DisplayName;
                CurrentDownloadPercent = p.Percent;
            });
        });

        var result = await _installer.InstallAsync(item.Entry, progress);

        item.IsInstalling    = false;
        item.ProgressPercent = result.Success ? 100 : 0;
        item.IsInstalled     = result.Success;
        item.HasFailed       = !result.Success;
        IsDownloadingAny     = false;

        if (result.Success)
        {
            item.InstalledVersion = result.Version ?? "?";
            item.StatusMessage    = DemoBase.App.Services.LocalizationService.Get("Emu_StatusInstalled");
            item.UpdateAvailable  = false;
            HasErrors = Items.Any(i => i.HasFailed);
        }
        else
        {
            item.ErrorDetails  = result.Error ?? "Unknown error.";
            item.StatusMessage = $"✗ {result.Error}";
            await LogErrorAsync(item.DisplayName, result.Error ?? "Unknown error.");
            HasErrors = true;
        }

        GlobalStatus = $"{Items.Count(i => i.IsInstalled)}/{Items.Count} emulators installed";
    }

    /// <summary>Journalise une erreur d'installation dans Emus/install_errors.log
    /// pour permettre un diagnostic après coup (ex. plusieurs émulateurs en échec
    /// suite à une limite de débit ou un changement côté serveur tiers).</summary>
    private async Task LogErrorAsync(string emulatorName, string error)
    {
        try
        {
            System.IO.Directory.CreateDirectory(EmusRoot);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {emulatorName} — {error}{Environment.NewLine}";
            await System.IO.File.AppendAllTextAsync(LogFile, line);
        }
        catch { /* le logging ne doit jamais faire planter l'installation */ }
    }

    // ── Installer tous les manquants ──────────────────────────────────────────

    [RelayCommand]
    private async Task InstallAll()
    {
        if (IsInstallingAll) return;
        IsInstallingAll = true;
        GlobalStatus    = "Installing…";

        // Installer les non-installés ET mettre à jour ceux qui ont une mise à jour disponible
        var targets = Items.Where(i => (!i.IsInstalled || i.UpdateAvailable) && !i.IsBusy).ToList();
        foreach (var item in targets)
            await Install(item);

        IsInstallingAll = false;
        GlobalStatus    = $"{Items.Count(i => i.IsInstalled)}/{Items.Count} emulators installed";
        HasCompletedInitialPass = true;
    }

    // ── Vérifier les mises à jour ─────────────────────────────────────────────

    [RelayCommand]
    private async Task CheckUpdates()
    {
        IsRefreshing = true;
        GlobalStatus = "Checking for updates…";

        var installed = Items.Where(i => i.IsInstalled).ToList();
        int updates   = 0;

        await Parallel.ForEachAsync(installed,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (item, ct) =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                item.IsChecking    = true;
                item.StatusMessage = DemoBase.App.Services.LocalizationService.Get("Emu_StatusChecking");
            });

            var info = await _installer.CheckUpdateAsync(item.Entry, ct);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                item.IsChecking       = false;
                item.UpdateAvailable  = info.UpdateAvailable;
                item.LatestVersion    = info.LatestVersion ?? "";
                item.StatusMessage    = info.UpdateAvailable
                    ? $"{DemoBase.App.Services.LocalizationService.Get("Emu_StatusUpdateAvail")} {info.LatestVersion}"
                    : $"{DemoBase.App.Services.LocalizationService.Get("Emu_StatusUpToDate")} ({item.InstalledVersion})";
                if (info.UpdateAvailable) updates++;
            });
        });

        IsRefreshing = false;
        GlobalStatus = updates > 0
            ? $"{updates} update(s) available"
            : "All emulators are up to date";
    }

    // ── Ouvrir le dossier Emus ────────────────────────────────────────────────

    [RelayCommand]
    private void OpenEmusFolder()
    {
        System.IO.Directory.CreateDirectory(EmusRoot);
        System.Diagnostics.Process.Start("explorer.exe", EmusRoot);
    }

    // ── Détails d'erreur (bouton ⓘ sur les lignes en échec) ───────────────────

    [RelayCommand]
    private void ShowErrorDetails(EmulatorInstallItemViewModel item)
    {
        System.Windows.MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            string.IsNullOrEmpty(item.ErrorDetails) ? "No details available." : item.ErrorDetails,
            $"Installation failed — {item.DisplayName}",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        if (!System.IO.File.Exists(LogFile))
        {
            System.Windows.MessageBox.Show(
                System.Windows.Application.Current.MainWindow,
                "No errors recorded yet.",
                "Error log",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogFile) { UseShellExecute = true });
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ExportConfigsAsync()
    {
        if (_exportService == null) return;
        try
        {
            var path = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                $"emulator_configs_{System.DateTime.Now:yyyyMMdd_HHmmss}.json");
            await _exportService.ExportToJsonAsync(path);
            System.Windows.MessageBox.Show(
                System.Windows.Application.Current.MainWindow,
                $"Export réussi :\n{path}",
                "Export configs",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(System.IO.Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                System.Windows.Application.Current.MainWindow,
                $"Erreur : {ex.Message}", "Export configs",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Exporte les sélections de profil par release (table ReleaseProfileOverrides,
    /// cf. ReleaseProfileOverrideExportService) en JSON — portable entre installations
    /// car indexé par ReleaseDemozooId et EmulatorConfigId, deux identifiants stables
    /// (identiques sur toute installation DemoBase, contrairement à un Id local
    /// auto-incrémenté).
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ExportProfileOverridesAsync()
    {
        if (_profileOverrideExportService == null) return;
        try
        {
            var path = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
                $"release_profile_overrides_{System.DateTime.Now:yyyyMMdd_HHmmss}.json");
            await _profileOverrideExportService.ExportToJsonAsync(path);
            System.Windows.MessageBox.Show(
                System.Windows.Application.Current.MainWindow,
                $"Export réussi :\n{path}",
                "Export profils par release",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(System.IO.Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                System.Windows.Application.Current.MainWindow,
                $"Erreur : {ex.Message}", "Export profils par release",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }
}
