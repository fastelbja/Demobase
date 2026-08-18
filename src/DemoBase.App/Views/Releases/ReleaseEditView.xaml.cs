using DemoBase.App.ViewModels.Releases;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace DemoBase.App.Views.Releases;

public partial class ReleaseEditView : UserControl
{
    public ReleaseEditView()
    {
        InitializeComponent();
    }

    private void BtnOpenDemozoo_Click(object sender, RoutedEventArgs e) =>
        OpenUrl((DataContext as ReleaseEditViewModel)?.DemozooUrl);

    private void BtnOpenPouet_Click(object sender, RoutedEventArgs e) =>
        OpenUrl((DataContext as ReleaseEditViewModel)?.PouetUrl);

    private void BtnOpenCsdb_Click(object sender, RoutedEventArgs e) =>
        OpenUrl((DataContext as ReleaseEditViewModel)?.CsdbUrl);

    private static void OpenUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
