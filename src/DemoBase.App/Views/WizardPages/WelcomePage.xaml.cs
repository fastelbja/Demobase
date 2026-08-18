using System.Windows.Controls;
using DemoBase.App.Services;
using DemoBase.Data;

namespace DemoBase.App.Views.WizardPages;

public partial class WelcomePage : UserControl
{
    private readonly LocalizationService _locService;
    private readonly PreferencesService  _prefs;

    public WelcomePage(LocalizationService locService, PreferencesService prefs)
    {
        _locService = locService;
        _prefs      = prefs;
        InitializeComponent();
    }

    private void OnSelectFr(object sender, System.Windows.RoutedEventArgs e)
    {
        _locService.Apply("fr");
        _ = _prefs.SetAsync(PrefKeys.Language, "fr");
    }

    private void OnSelectEn(object sender, System.Windows.RoutedEventArgs e)
    {
        _locService.Apply("en");
        _ = _prefs.SetAsync(PrefKeys.Language, "en");
    }
}
