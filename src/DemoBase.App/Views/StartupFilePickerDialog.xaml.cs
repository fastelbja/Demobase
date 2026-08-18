using System.Windows;
using System.Windows.Controls;

namespace DemoBase.App.Views;

/// <summary>
/// Dialog modal permettant à l'utilisateur de choisir le fichier principal à lancer
/// pour une démo Amiga extraite sur disque dur virtuel, quand la détection automatique
/// ne peut pas trancher entre plusieurs candidats.
/// </summary>
public partial class StartupFilePickerDialog : Window
{
    /// <summary>Nom du fichier sélectionné (sans chemin, ex. "SLC3inv.exe").</summary>
    public string? SelectedFile { get; private set; }

    public StartupFilePickerDialog(IEnumerable<string> candidates)
    {
        InitializeComponent();

        foreach (var c in candidates)
            FileList.Items.Add(c);

        if (FileList.Items.Count > 0)
            FileList.SelectedIndex = 0;

        FileList.SelectionChanged += (_, _) =>
            OkButton.IsEnabled = FileList.SelectedItem != null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is string selected)
        {
            SelectedFile = selected;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void FileList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem != null)
            Ok_Click(sender, e);
    }
}
