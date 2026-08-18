using DemoBase.Import;
using System.Windows;

namespace DemoBase.App;

public partial class DemozooRawExportWindow : Window
{
    private readonly DemozooRawExportService  _service;
    private readonly CancellationTokenSource  _cts = new();

    public DemozooRawExportWindow(string databaseDir)
    {
        InitializeComponent();
        _service = new DemozooRawExportService(databaseDir);
    }

    public async Task RunAsync()
    {
        var uiContext = System.Threading.SynchronizationContext.Current;

        var progress = new Progress<RawExportProgress>(p =>
        {
            uiContext?.Post(_ => UpdateUI(p), null);
        });

        try   { await Task.Run(async () => await _service.ExportAsync(progress, _cts.Token), _cts.Token); }
        catch (OperationCanceledException) { Close(); }
        catch (Exception ex)
        {
            // 2026-08-07, retour utilisateur (lancement de DemoBase depuis un partage
            // SMB) : demozoo_raw.db vit dans le même dossier "Database" que demobase.db
            // — si l'appli tourne sur un partage réseau, ce message peut s'afficher pour
            // la même raison (cf. DemozooRawExportService.ExportAsync, garde
            // journal_mode ajoutée le même jour). Ce catch managé n'attrape toutefois
            // PAS un plantage natif (SEH Windows côté SQLite) — un APPCRASH silencieux
            // reste possible dans de rares cas, indépendamment de ce message.
            MessageBox.Show(
                $"Erreur export :\n{ex.Message}\n\n" +
                "Si DemoBase est lancé depuis un partage réseau (SMB), essayez de le " +
                "copier sur un disque local, ou désactivez le mode \"Fichiers hors " +
                "connexion\" de Windows pour ce partage (Centre de synchronisation).",
                "DemoBase",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void UpdateUI(RawExportProgress p)
    {
        if (p.IsComplete)
        {
            MessageBox.Show($"Export terminé !\n\nFichier : {_service.DbPath}",
                "DemoBase", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }
        if (p.Error != null)
        {
            MessageBox.Show($"Erreur : {p.Error}", "DemoBase",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }
        double pct = p.TotalBytes > 0 ? p.BytesRead / (double)p.TotalBytes * 100 : 0;
        ExportProgress.Value = pct;
        MessageText.Text     = p.Message;
        StatsText.Text       = p.RowsInserted > 0
            ? $"{p.TablesCreated} tables  ·  {p.RowsInserted:N0} lignes"
            : string.Empty;
        // Forcer le rendu WPF
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            System.Windows.Threading.DispatcherPriority.Render, () => { });
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }
}
