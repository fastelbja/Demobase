using DemoBase.App.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace DemoBase.App;

/// <summary>
/// Fenêtre "Scan ROMs" (2026-07-27, demande utilisateur) — même style visuel sombre
/// et sans bordure que DatImportWindow, mais NE se ferme PAS automatiquement en fin
/// de scan : contrairement à un import DAT (qui est un simple rafraîchissement
/// silencieux), le résultat d'un scan ROMs (fichiers trouvés, releases mises à jour —
/// complètes ✓ ou encore partielles ◐, cf. RomScanService) est une information que
/// l'utilisateur doit pouvoir lire avant de fermer.
/// </summary>
public partial class RomScanWindow : Window
{
    private readonly RomScanService _service;
    private readonly string _folder;
    private readonly CancellationTokenSource _cts = new();
    private bool _scanFinished;

    public RomScanWindow(RomScanService service, string folder)
    {
        InitializeComponent();
        _service = service;
        _folder  = folder;
    }

    public async Task RunScanAsync()
    {
        var progress = new Progress<RomScanProgress>(p =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                GlobalProgress.Value = p.Percent;
                CurrentFileText.Text = p.Message;

                if (p.ArchiveMessage == null)
                {
                    ArchivePanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ArchivePanel.Visibility     = Visibility.Visible;
                    ArchiveLabelText.Text       = p.ArchiveMessage;
                    ArchiveProgress.IsIndeterminate = p.ArchiveIndeterminate;
                    if (!p.ArchiveIndeterminate) ArchiveProgress.Value = p.ArchivePercent;
                }
            }, System.Windows.Threading.DispatcherPriority.Render);
        });

        try
        {
            var result = await Task.Run(() => _service.ScanFolderAsync(_folder, progress, _cts.Token));
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            // Fermé/annulé par l'utilisateur pendant le scan — rien à afficher.
        }
        catch (Exception ex)
        {
            CurrentFileText.Text = "";
            ArchivePanel.Visibility = Visibility.Collapsed;
            StatsText.Text = string.Format(
                (string)FindResource("ScanRoms_Error"), ex.Message);
            SwitchToClosableState();
        }
    }

    private void ShowResult(RomScanResult result)
    {
        _scanFinished = true;
        GlobalProgress.Value = 100;
        CurrentFileText.Text = "";
        ArchivePanel.Visibility = Visibility.Collapsed;

        // 2026-07-28 (demande utilisateur) : une release mise à jour n'est plus forcément
        // complète — le décompte distingue les deux pour rester honnête sur ce qui a
        // vraiment été terminé.
        int completeCount = result.UpdatedReleases.Count(r => r.IsComplete);
        StatsText.Text = string.Format(
            (string)FindResource("ScanRoms_Summary"),
            result.FilesScanned, result.ArchivesScanned, result.FilesMatched,
            result.UpdatedReleases.Count, completeCount);

        if (result.UpdatedReleases.Count > 0)
        {
            ResultsPanel.Visibility = Visibility.Visible;
            foreach (var r in result.UpdatedReleases)
            {
                var marker = r.IsComplete ? "✓" : "◐";
                CompletedList.Items.Add(
                    $"{marker} #{r.DemozooId}  {r.RomPath}  ({r.SatisfiedCount}/{r.TotalCount})");
            }
        }

        SwitchToClosableState();
    }

    private void SwitchToClosableState()
    {
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility  = Visibility.Visible;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (!_scanFinished) _cts.Cancel();
        base.OnClosed(e);
    }
}
