using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.App.Services;
using DemoBase.Data;
using System.IO;
using System.IO.Compression;
using System.Windows.Controls;

namespace DemoBase.App.Views.WizardPages;

// ─── ViewModel ────────────────────────────────────────────────────────────────

public partial class DatsPageViewModel : ObservableObject
{
    private readonly DbSetupDownloadService _megaService;
    private readonly DatImportService    _datImportService;
    private CancellationTokenSource?     _cts;

    // 2026-08-17 : migration Mega.nz → HTTP direct (site de l'utilisateur) — sous-dossier
    // "DATS" inchangé, mais le zip a désormais un nom EXACT et fixe (plus de
    // recherche par sous-chaîne "Demobase DATs (...)", aucun listing de répertoire
    // disponible sur http://demobase.free.fr).
    private const string MegaFolderUrl     = "http://demobase.free.fr/DBSetup";
    private const string MegaSubFolder     = "DATS";
    private const string MegaFileNameMatch = "Demobase_DATs.zip";

    [ObservableProperty] private bool   _notStarted = true;
    [ObservableProperty] private bool   _isImporting;
    [ObservableProperty] private bool   _isDone;
    [ObservableProperty] private bool   _hasError;

    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _doneSummary  = "";

    // ── Phase 1 : Téléchargement (Mega.nz) ──────────────────────────────────
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadPercentText = "";
    [ObservableProperty] private string _downloadDetail      = DemoBase.App.Services.LocalizationService.Get("Wiz_DatsWaiting");

    // ── Phase 2 : Extraction + Import des DATs ──────────────────────────────
    [ObservableProperty] private bool   _importStarted;
    [ObservableProperty] private double _importPercent;
    [ObservableProperty] private string _importPercentText   = "";
    [ObservableProperty] private string _importDetail        = DemoBase.App.Services.LocalizationService.Get("Wiz_DatsWaitingDl");

    public bool ImportSucceeded { get; private set; }

    public DatsPageViewModel(DbSetupDownloadService megaService, DatImportService datImportService)
    {
        _megaService      = megaService;
        _datImportService = datImportService;
    }

    /// <summary>Appelée au chargement de la page — si des DatEntries existent
    /// déjà en base (import réussi lors d'une précédente ouverture du wizard,
    /// interrompue avant la fin), marque directement cette étape comme
    /// terminée plutôt que de forcer un nouveau téléchargement. Note :
    /// NeedsImportAsync() ne convient pas ici car elle se base sur la présence
    /// de fichiers dans DATS/, qui est vidé après un import réussi — on utilise
    /// IsFirstRunAsync() qui interroge directement le contenu de la table.</summary>
    public async Task CheckExistingDataAsync()
    {
        if (NotStarted && !await _datImportService.IsFirstRunAsync())
        {
            NotStarted      = false;
            IsDone          = true;
            ImportSucceeded = true;
            DoneSummary     = DemoBase.App.Services.LocalizationService.Get("Wiz_DatsAlreadyImported");
        }
    }

    [RelayCommand]
    private async Task StartImport()
    {
        NotStarted   = false;
        IsImporting  = true;
        IsDone       = false;
        HasError     = false;

        DownloadPercent     = 0;
        DownloadPercentText = "";
        DownloadDetail      = DemoBase.App.Services.LocalizationService.Get("Wiz_DatsConnecting");

        ImportStarted     = false;
        ImportPercent     = 0;
        ImportPercentText = "";
        ImportDetail      = DemoBase.App.Services.LocalizationService.Get("Wiz_DatsWaitingDl");

        _cts = new CancellationTokenSource();

        try
        {
            // ── Téléchargement depuis le site DemoBase ───────────────────────
            var tmpZip = Path.Combine(Path.GetTempPath(), $"DemoBaseDats_{Guid.NewGuid():N}.zip");

            var dlProgress = new Progress<double>(p =>
            {
                DownloadPercent     = p;
                DownloadPercentText = $"{p:0}%";
                DownloadDetail      = string.Format(DemoBase.App.Services.LocalizationService.Get("Wiz_DatsDownloading"), p);
            });

            var result = await _megaService.DownloadFirstMatchingFileAsync(
                MegaFolderUrl, MegaFileNameMatch, tmpZip,
                subFolder: MegaSubFolder, progress: dlProgress, ct: _cts.Token);

            if (!result.Success)
            {
                IsImporting  = false;
                HasError     = true;
                ErrorMessage = result.Error ?? "Download failed.";
                return;
            }

            DownloadPercent     = 100;
            DownloadPercentText = "100%";
            DownloadDetail      = string.Format(DemoBase.App.Services.LocalizationService.Get("Wiz_DatsDoneFile"), result.FileName);

            // ── Extraction dans le dossier DATS de l'application ─────────────
            ImportStarted = true;
            ImportDetail  = DemoBase.App.Services.LocalizationService.Get("Wiz_DatsExtracting");
            ImportPercent = 10;

            var datsDir = Path.Combine(AppContext.BaseDirectory, "DATS");
            Directory.CreateDirectory(datsDir);

            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(tmpZip);
                var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry    = entries[i];
                    var destPath = Path.GetFullPath(Path.Combine(datsDir, entry.FullName));
                    if (!destPath.StartsWith(Path.GetFullPath(datsDir))) continue; // sécurité

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }, _cts.Token);

            try { File.Delete(tmpZip); } catch { }

            ImportPercent = 30;
            ImportDetail  = DemoBase.App.Services.LocalizationService.Get("Wiz_DatsImporting");

            // ── Import en base — même service que l'import DAT au démarrage ──
            var importProgress = new Progress<DatImportProgress>(p =>
            {
                if (p.IsComplete)
                {
                    ImportPercent     = 100;
                    ImportPercentText = "100%";
                    ImportDetail      = $"{p.EntriesImported:N0} entries imported";
                }
                else if (p.FilesTotal > 0)
                {
                    var pct = 30 + (double)p.FilesProcessed / p.FilesTotal * 70;
                    ImportPercent     = Math.Min(99, pct);
                    ImportPercentText = $"{ImportPercent:0}%";
                    ImportDetail      = $"{p.CurrentFile} ({p.FilesProcessed}/{p.FilesTotal})";
                }
            });

            await Task.Run(() => _datImportService.ImportAsync(importProgress, _cts.Token));

            ImportPercent     = 100;
            ImportPercentText = "100%";
            ImportDetail      = DemoBase.App.Services.LocalizationService.Get("Wiz_DatsCleaningUp");

            // Les fichiers XML/zip ne servent qu'à l'import — une fois les données
            // intégrées dans demobase.db, ils sont redondants et inutiles à
            // conserver (peuvent peser plusieurs dizaines de Mo). En cas d'échec
            // de suppression (fichier verrouillé, permissions), on n'interrompt
            // pas le wizard pour autant : l'import en base a déjà réussi.
            try { Directory.Delete(datsDir, recursive: true); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DatsPage] Failed to delete DATS/ (non-blocking): {ex.Message}");
            }

            IsImporting     = false;
            IsDone          = true;
            ImportSucceeded = true;
            DoneSummary     = DemoBase.App.Services.LocalizationService.Get("Wiz_DatsImportSuccess");
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

public partial class DatsPage : UserControl
{
    public DatsPageViewModel Vm { get; }

    public DatsPage(DbSetupDownloadService megaService, DatImportService datImportService)
    {
        InitializeComponent();
        Vm          = new DatsPageViewModel(megaService, datImportService);
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
