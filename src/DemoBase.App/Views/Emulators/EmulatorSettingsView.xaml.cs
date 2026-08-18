using DemoBase.App.ViewModels.Emulators;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace DemoBase.App.Views.Emulators;

public partial class EmulatorSettingsView : UserControl
{
    public EmulatorSettingsView() => InitializeComponent();

    private void BtnWebsite_Click(object sender, RoutedEventArgs e)
    {
        var url = (DataContext as EmulatorSettingsViewModel)?.Selected?.Website;
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
