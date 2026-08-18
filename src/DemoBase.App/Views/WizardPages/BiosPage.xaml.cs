using System.Windows.Controls;
using DemoBase.App.ViewModels;

namespace DemoBase.App.Views.WizardPages;

public partial class BiosPage : UserControl
{
    public BiosPage()
    {
        InitializeComponent();
        DataContext = new BiosPageViewModel();
    }
}
