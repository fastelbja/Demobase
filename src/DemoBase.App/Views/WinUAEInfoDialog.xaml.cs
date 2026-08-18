using System.Windows;

namespace DemoBase.App.Views;

public partial class WinUAEInfoDialog : Window
{
    public bool DontShowAgain { get; private set; }

    public WinUAEInfoDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DontShowAgain = DontShowAgainCheck.IsChecked == true;
        DialogResult = true;
    }
}
