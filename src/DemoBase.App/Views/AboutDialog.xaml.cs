using System.Windows;

namespace DemoBase.App.Views;

public partial class AboutDialog : Window
{
    public AboutDialog(string? dbVersionLabel = null)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        TxtDbVersion.Text    = dbVersionLabel ?? "—";
        TxtReleaseCount.Text = "385 000+ releases (Demozoo)";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
