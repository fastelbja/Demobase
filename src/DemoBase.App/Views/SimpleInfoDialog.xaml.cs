using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;

namespace DemoBase.App.Views;

/// <summary>
/// Fenêtre d'information générique — remplace les MessageBox.Show natives (fenêtre
/// système, hors charte graphique, jamais traduites) par une fenêtre au même style que
/// HatariInfoDialog/WinUAEInfoDialog/DosBoxXInfoDialog. Les 3 clés de ressource passées au
/// constructeur sont résolues via <see cref="DemoBase.App.Services.LocalizationService.Get"/>
/// selon la langue courante au moment de la construction (2026-07-25, retour utilisateur :
/// message "Fichier introuvable" resté en français alors que l'interface était en anglais).
/// </summary>
public partial class SimpleInfoDialog : Window
{
    public SimpleInfoDialogViewModel Vm { get; }

    public SimpleInfoDialog(string titleKey, string headingKey, string bodyKey)
    {
        InitializeComponent();
        Vm = new SimpleInfoDialogViewModel(
            DemoBase.App.Services.LocalizationService.Get(titleKey),
            DemoBase.App.Services.LocalizationService.Get(headingKey),
            DemoBase.App.Services.LocalizationService.Get(bodyKey));
        DataContext = Vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

/// <summary>DataContext minimal de SimpleInfoDialog — juste les 3 textes déjà résolus,
/// pas de service injecté.</summary>
public partial class SimpleInfoDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _titleText;
    [ObservableProperty] private string _heading;
    [ObservableProperty] private string _body;

    public SimpleInfoDialogViewModel(string titleText, string heading, string body)
    {
        _titleText = titleText;
        _heading   = heading;
        _body      = body;
    }
}
