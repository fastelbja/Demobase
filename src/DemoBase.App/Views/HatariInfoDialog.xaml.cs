using System.Windows;

namespace DemoBase.App.Views;

public partial class HatariInfoDialog : Window
{
    public bool DontShowAgain { get; private set; }

    public HatariInfoDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DontShowAgain = DontShowAgainCheck.IsChecked == true;
        DialogResult = true;
    }
}
