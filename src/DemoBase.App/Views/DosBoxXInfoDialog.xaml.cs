using System.Windows;

namespace DemoBase.App.Views;

public partial class DosBoxXInfoDialog : Window
{
    public bool DontShowAgain { get; private set; }

    public DosBoxXInfoDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DontShowAgain = DontShowAgainCheck.IsChecked == true;
        DialogResult = true;
    }
}
