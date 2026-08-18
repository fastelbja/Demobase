using DemoBase.App.ViewModels;
using System.Windows;

namespace DemoBase.App;

public partial class ScreenshotDownloadWindow : Window
{
    public ScreenshotDownloadWindow(ScreenshotDownloadViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadStatsAsync();
    }
}
