using System.Windows;
using System.Windows.Input;

namespace DemoBase.App.Views;

/// <summary>
/// Dialog modal permettant de saisir le nom d'une nouvelle playlist ou de
/// renommer une playlist existante — modélisé sur StartupFilePickerDialog.
/// </summary>
public partial class PlaylistNameDialog : Window
{
    /// <summary>Nom saisi, si validé.</summary>
    public string? ResultName { get; private set; }

    public PlaylistNameDialog(string? existingName = null)
    {
        InitializeComponent();

        var isRename = !string.IsNullOrEmpty(existingName);
        Title = DemoBase.App.Services.LocalizationService.Get(
            isRename ? "PL_DialogTitleRename" : "PL_DialogTitleNew");
        LabelText.Text = DemoBase.App.Services.LocalizationService.Get("PL_DialogLabel");
        OkButton.Content     = DemoBase.App.Services.LocalizationService.Get("PL_DialogOk");
        CancelButton.Content = DemoBase.App.Services.LocalizationService.Get("PL_DialogCancel");

        NameBox.Text = existingName ?? "";
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        ResultName  = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }
}
