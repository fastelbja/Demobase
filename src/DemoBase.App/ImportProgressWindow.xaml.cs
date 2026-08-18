using DemoBase.Import;
using System.Windows;
using System.Windows.Media;

namespace DemoBase.App;

public partial class ImportProgressWindow : Window
{
    // Largeur disponible pour la barre (520 - 2*28 margins - 2*1 border)
    private double _barWidth = 462;

    public string DbPath
    {
        set => Dispatcher.Invoke(() => TxtMessage.Text = string.Format(
            DemoBase.App.Services.LocalizationService.Get("ImportProg_DbPrefix"), value));
    }

    public ImportProgressWindow()
    {
        InitializeComponent();
        // Calcule la largeur réelle après rendu
        Loaded += (_, _) =>
        {
            _barWidth = DeterminateBorder.ActualWidth > 0
                ? DeterminateBorder.ActualWidth
                : 462;
        };
    }

    public void Report(DemozooImportProgress progress)
    {
        // Toujours dispatcher sur le thread UI (non-bloquant pour laisser la ProgressBar s'animer)
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Report(progress),
                System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        TxtMessage.Text = progress.Message;

        switch (progress.Phase)
        {
            case Phase.Download:
                PhaseIcon.Text = "⬇";
                TxtPhase.Text  = DemoBase.App.Services.LocalizationService.Get("ImportProg_PhaseDownload");
                // Barre déterminée visible, indéterminée cachée
                DeterminateBorder.Visibility = Visibility.Visible;
                IndeterminateBar.Visibility  = Visibility.Collapsed;
                if (progress.Total > 0)
                {
                    var pct = Math.Min(progress.Percent, 100);
                    ProgressFill.Width  = _barWidth * pct / 100.0;
                    TxtPercent.Text     = $"{pct:N1}%";
                    TxtStats.Text       = $"{FormatBytes(progress.Current)} / {FormatBytes(progress.Total)}";
                }
                break;

            case Phase.Parse:
                PhaseIcon.Text = "⚙";
                TxtPhase.Text  = DemoBase.App.Services.LocalizationService.Get("ImportProg_PhaseImporting");
                // Barre indéterminée visible, déterminée cachée
                DeterminateBorder.Visibility = Visibility.Collapsed;
                IndeterminateBar.Visibility  = Visibility.Visible;
                TxtPercent.Text = "";
                TxtStats.Text   = progress.Current > 0
                    ? string.Format(DemoBase.App.Services.LocalizationService.Get("ImportProg_RowsProcessed"),
                        progress.Current.ToString("N0"))
                    : "";
                break;

            case Phase.Finalize:
                PhaseIcon.Text = "⚙";
                TxtPhase.Text  = DemoBase.App.Services.LocalizationService.Get("ImportProg_PhaseFinalizing");
                IndeterminateBar.Visibility  = Visibility.Visible;
                DeterminateBorder.Visibility = Visibility.Collapsed;
                // Le message détaillé est dans TxtMessage (déjà rempli avant le switch)
                // TxtStats affiche la progression des étapes
                TxtStats.Text   = progress.Current > 0 && progress.Total > 0
                    ? string.Format(DemoBase.App.Services.LocalizationService.Get("ImportProg_StepOf"),
                        progress.Current, progress.Total)
                    : string.Empty;
                TxtPercent.Text = progress.Current > 0 && progress.Total > 0
                    ? $"{progress.Current * 100 / progress.Total}%"
                    : string.Empty;
                break;

            case Phase.Done:
                PhaseIcon.Text       = "✓";
                PhaseIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x80));
                TxtPhase.Text        = DemoBase.App.Services.LocalizationService.Get("ImportProg_PhaseDone");
                IndeterminateBar.Visibility  = Visibility.Collapsed;
                DeterminateBorder.Visibility = Visibility.Visible;
                ProgressFill.Width   = _barWidth;
                ProgressFill.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x80));
                TxtPercent.Text      = "100%";
                TxtStats.Text        = "";
                break;
        }
    }

    public void ReportError(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ReportError(message)); return; }

        PhaseIcon.Text       = "✕";
        PhaseIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));
        TxtPhase.Text        = DemoBase.App.Services.LocalizationService.Get("ImportProg_PhaseError");
        TxtMessage.Text      = message;
        IndeterminateBar.Visibility  = Visibility.Collapsed;
        DeterminateBorder.Visibility = Visibility.Visible;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) =>
        Application.Current.Shutdown();

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        DragMove();

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1024               => $"{bytes} B",
            < 1024 * 1024        => $"{bytes / 1024:N0} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024 * 1024):N1} MB",
            _                    => $"{bytes / (1024.0 * 1024 * 1024):N2} GB"
        };
}
