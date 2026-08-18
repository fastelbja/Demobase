using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.Import;
using System.Windows.Controls;

namespace DemoBase.App.Views.WizardPages;

// ─── ViewModel ────────────────────────────────────────────────────────────────

public partial class DatabasePageViewModel : ObservableObject
{
    private readonly DemozooImportService _importService;
    private CancellationTokenSource?      _cts;

    [ObservableProperty] private bool   _notStarted = true;
    [ObservableProperty] private bool   _isImporting;
    [ObservableProperty] private bool   _isDone;
    [ObservableProperty] private bool   _hasError;

    [ObservableProperty] private string _errorMessage   = "";
    [ObservableProperty] private string _doneSummary    = "";

    // ── Phase 1 : Téléchargement ────────────────────────────────────────────
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadPercentText  = "";
    [ObservableProperty] private string _downloadDetail       = DemoBase.App.Services.LocalizationService.Get("Wiz_DatabaseWaiting");
    [ObservableProperty] private bool   _downloadIndeterminate;

    // ── Phase 2 : Import en base ────────────────────────────────────────────
    [ObservableProperty] private bool   _importStarted;
    [ObservableProperty] private double _importPercent;
    [ObservableProperty] private string _importPercentText    = "";
    [ObservableProperty] private string _importDetail         = DemoBase.App.Services.LocalizationService.Get("Wiz_DatabaseWaitingDl");

    /// <summary>True une fois l'import terminé avec succès — empêche un import en double.</summary>
    public bool ImportSucceeded { get; private set; }

    public DatabasePageViewModel(DemozooImportService importService)
    {
        _importService = importService;
    }

    /// <summary>Appelée au chargement de la page — si le catalogue Demozoo est
    /// déjà présent en base (import réussi lors d'une précédente ouverture du
    /// wizard, interrompue avant la fin), marque directement cette étape comme
    /// terminée plutôt que de forcer un nouveau téléchargement.</summary>
    public async Task CheckExistingDataAsync()
    {
        if (NotStarted && await _importService.HasExistingDataAsync())
        {
            NotStarted      = false;
            IsDone          = true;
            ImportSucceeded = true;
            DoneSummary     = DemoBase.App.Services.LocalizationService.Get("Wiz_DatabaseAlreadyImported");
        }
    }

    [RelayCommand]
    private async Task StartImport()
    {
        NotStarted   = false;
        IsImporting  = true;
        IsDone       = false;
        HasError     = false;

        DownloadPercent        = 0;
        DownloadPercentText    = "";
        DownloadDetail         = DemoBase.App.Services.LocalizationService.Get("Wiz_DatabaseConnecting");
        DownloadIndeterminate  = false;

        ImportStarted    = false;
        ImportPercent    = 0;
        ImportPercentText = "";
        ImportDetail      = DemoBase.App.Services.LocalizationService.Get("Wiz_DatabaseWaitingDl");

        _cts = new CancellationTokenSource();

        var progress = new Progress<DemozooImportProgress>(p =>
        {
            switch (p.Phase)
            {
                case Phase.Download:
                    // Le total peut être inconnu (serveur sans Content-Length) → barre indéterminée
                    DownloadIndeterminate = p.Total <= 0;
                    DownloadPercent       = p.Percent;
                    DownloadPercentText   = p.Total > 0 ? $"{p.Percent:0}%" : "";
                    DownloadDetail        = p.Message;
                    break;

                case Phase.Parse:
                    // ATTENTION : le téléchargement (gzip) et le parsing ne sont PAS
                    // séquentiels — c'est un seul flux streaming où le SQL est
                    // décompressé et parsé au fil de l'eau, pendant que la connexion
                    // réseau continue d'alimenter le flux. Les événements Phase.Download
                    // continuent donc d'arriver PENDANT toute la durée du Phase.Parse
                    // (le CountingStream sous-jacent rapporte sa progression à chaque
                    // Mo lu, peu importe la "phase logique" affichée). Forcer
                    // DownloadPercent à 100 ici faisait donc osciller la barre : 100%
                    // (ce bloc) → vraie valeur (prochain événement Download) → 100%
                    // (Parse suivant) → etc. On laisse simplement DownloadPercent
                    // suivre les vrais événements Phase.Download, qui atteindront
                    // naturellement 100% une fois le flux réseau entièrement consommé.
                    ImportStarted  = true;
                    ImportDetail   = p.Message;
                    // Pas de total fiable en nombre de lignes → progression visuelle continue
                    ImportPercent  = Math.Min(99, ImportPercent + 0.4);
                    break;

                case Phase.Finalize:
                    // À ce stade, le flux réseau est nécessairement épuisé (le parsing
                    // qui le consommait est terminé) — on peut donc affirmer le 100%.
                    DownloadIndeterminate = false;
                    DownloadPercent       = 100;
                    DownloadPercentText   = "100%";
                    DownloadDetail        = DemoBase.App.Services.LocalizationService.Get("Wiz_DatabaseDone2");

                    ImportStarted     = true;
                    ImportDetail      = p.Message;
                    ImportPercent     = Math.Max(ImportPercent, 99);
                    break;

                case Phase.Done:
                    ImportPercent     = 100;
                    ImportPercentText = "100%";
                    ImportDetail      = p.Message;
                    break;
            }
        });

        try
        {
            // Task.Run : l'import tourne sur un thread pool pour ne pas geler l'UI
            // du wizard pendant les milliers d'opérations SQLite.
            _importService.SetLanguage(DemoBase.App.Services.LocalizationService.CurrentLanguageStatic);
            await Task.Run(() => _importService.ImportAsync(progress, _cts.Token));

            ImportPercent     = 100;
            ImportPercentText = "100%";

            IsImporting     = false;
            IsDone          = true;
            ImportSucceeded = true;
            DoneSummary     = DemoBase.App.Services.LocalizationService.Get("Wiz_DatabaseImportSuccess");
        }
        catch (OperationCanceledException)
        {
            IsImporting = false;
            NotStarted  = true;
        }
        catch (Exception ex)
        {
            IsImporting  = false;
            HasError     = true;
            ErrorMessage = ex.Message;
        }
    }
}

// ─── Code-behind ──────────────────────────────────────────────────────────────

public partial class DatabasePage : UserControl
{
    public DatabasePageViewModel Vm { get; }

    public DatabasePage(DemozooImportService importService)
    {
        InitializeComponent();
        Vm          = new DatabasePageViewModel(importService);
        DataContext = Vm;
        Loaded += async (_, _) =>
        {
            await Vm.CheckExistingDataAsync();
            // Démarrer automatiquement si pas encore importé
            if (Vm.NotStarted && Vm.StartImportCommand.CanExecute(null))
                await Vm.StartImportCommand.ExecuteAsync(null);
        };
    }
}
