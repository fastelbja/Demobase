using DemoBase.Data;
using System.Windows;

namespace DemoBase.App;

public partial class DatImportWindow : Window
{
    private readonly DatImportService     _service;
    private readonly CancellationTokenSource _cts = new();

    // Constructeur depuis App.xaml.cs (connection string)
    public DatImportWindow(string connectionString)
    {
        InitializeComponent();
        _service = new DatImportService(connectionString);
    }

    // Constructeur depuis MainViewModel (service injecté)
    public DatImportWindow(DatImportService service)
    {
        InitializeComponent();
        _service = service;
    }

    public async Task RunImportAsync()
    {
        var progress = new Progress<DatImportProgress>(p =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (p.IsComplete) { Close(); return; }

                double pct = p.FilesTotal > 0
                    ? p.FilesProcessed / (double)p.FilesTotal * 100 : 0;
                GlobalProgress.Value = pct;
                CurrentFileText.Text = p.CurrentFile;
                StatsText.Text       = $"{p.FilesProcessed} / {p.FilesTotal} fichiers  ·  {p.EntriesImported:N0} releases importées";
            }, System.Windows.Threading.DispatcherPriority.Render);
        });

        try   { await Task.Run(() => _service.ImportAsync(progress, _cts.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show($"Erreur import DAT :\n{ex.Message}", "DemoBase",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }
}
