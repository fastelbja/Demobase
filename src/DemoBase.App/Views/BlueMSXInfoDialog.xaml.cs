using System.Windows;

namespace DemoBase.App.Views;

public partial class BlueMSXInfoDialog : Window
{
    public bool DontShowAgain { get; private set; }

    public BlueMSXInfoDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DontShowAgain = DontShowAgainCheck.IsChecked == true;
        DialogResult = true;
    }
}
