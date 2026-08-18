using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;

namespace DemoBase.App.Views.WizardPages;

// ─── Un dossier configurable ──────────────────────────────────────────────────

public partial class FolderEntryViewModel : ObservableObject
{
    public string Label       { get; init; } = "";
    public string Description { get; init; } = "";
    public string PreferenceKey { get; init; } = "";

    [ObservableProperty] private string _path = "";

    [RelayCommand]
    private void Browse()
    {
        var dlg = new OpenFolderDialog
        {
            Title            = $"Choose folder — {Label}",
            InitialDirectory = System.IO.Directory.Exists(
                System.IO.Path.IsPathRooted(Path) ? Path
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, Path)))
                ? (System.IO.Path.IsPathRooted(Path) ? Path
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, Path)))
                : AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog() != true) return;

        // Afficher en relatif si sous le dossier appli
        var base_ = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var sel   = dlg.FolderName.TrimEnd('\\', '/');
        Path = sel.StartsWith(base_, StringComparison.OrdinalIgnoreCase)
            ? $".\\{sel[base_.Length..].TrimStart('\\', '/')}"
            : dlg.FolderName;
    }
}

// ─── ViewModel de la page ─────────────────────────────────────────────────────

public class FoldersPageViewModel
{
    public ObservableCollection<FolderEntryViewModel> Folders { get; } = [];

    public FoldersPageViewModel()
    {
        var base_ = AppContext.BaseDirectory;

        Folders.Add(new FolderEntryViewModel
        {
            Label         = Loc("Wiz_FolderBiosLabel"),
            Description   = Loc("Wiz_FolderBiosDesc"),
            PreferenceKey = DemoBase.App.Services.PathPreferenceKeys.Bios,
            Path          = ToRelative(System.IO.Path.Combine(base_, "BIOS")),
        });
        Folders.Add(new FolderEntryViewModel
        {
            Label         = Loc("Wiz_FolderConfigsLabel"),
            Description   = Loc("Wiz_FolderConfigsDesc"),
            PreferenceKey = DemoBase.App.Services.PathPreferenceKeys.Configs,
            Path          = ToRelative(System.IO.Path.Combine(base_, "Configs")),
        });
        Folders.Add(new FolderEntryViewModel
        {
            Label         = Loc("Wiz_FolderDatabaseLabel"),
            Description   = Loc("Wiz_FolderDatabaseDesc"),
            PreferenceKey = DemoBase.App.Services.PathPreferenceKeys.Database,
            Path          = ToRelative(System.IO.Path.Combine(base_, "Database")),
        });
        Folders.Add(new FolderEntryViewModel
        {
            Label         = Loc("Wiz_FolderReleasesLabel"),
            Description   = Loc("Wiz_FolderReleasesDesc"),
            PreferenceKey = DemoBase.App.Services.PathPreferenceKeys.Releases,
            Path          = ToRelative(System.IO.Path.Combine(base_, "Releases")),
        });
        Folders.Add(new FolderEntryViewModel
        {
            Label         = Loc("Wiz_FolderWorkingLabel"),
            Description   = Loc("Wiz_FolderWorkingDesc"),
            PreferenceKey = DemoBase.App.Services.PathPreferenceKeys.Working,
            Path          = ToRelative(System.IO.Path.Combine(base_, "Working")),
        });
    }

    /// <summary>
    /// Convertit un chemin absolu sous le dossier de l'application en chemin
    /// relatif (ex. ".\BIOS"). Les chemins extérieurs restent absolus.
    /// </summary>
    private static string ToRelative(string absolute)
    {
        var base_ = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var norm  = absolute.TrimEnd('\\', '/');
        if (norm.StartsWith(base_, StringComparison.OrdinalIgnoreCase))
        {
            var rel = norm[base_.Length..].TrimStart('\\', '/');
            return string.IsNullOrEmpty(rel) ? ".\\" : $".\\{rel}";
        }
        return absolute;
    }

    /// <summary>
    /// Reconvertit un chemin relatif (.\xxx) en absolu avant la sauvegarde.
    /// Les chemins déjà absolus sont retournés tels quels.
    /// </summary>
    private static string ToAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, path));
    }

    /// <summary>Appelé par le wizard quand l'utilisateur clique Suivant.</summary>
    /// <summary>True si tous les chemins sont renseignés (non vides). Le wizard
    /// bloque "Suivant" tant que ce n'est pas le cas — un chemin vide ferait
    /// échouer silencieusement la création des dossiers à l'étape finale.</summary>
    public bool AllPathsValid => Folders.All(f => !string.IsNullOrWhiteSpace(f.Path));


    private static string Loc(string key)
        => System.Windows.Application.Current.TryFindResource(key) as string ?? key;

    public async Task CommitAsync(DemoBase.Data.PreferencesService prefs)
    {
        var get = (string key) => ToAbsolute(Folders.First(f => f.PreferenceKey == key).Path);

        // Sauvegarde uniquement — les dossiers ne sont créés qu'à la fin du wizard
        await DemoBase.App.Services.AppPaths.SaveAsync(
            prefs,
            bios:     get(DemoBase.App.Services.PathPreferenceKeys.Bios),
            configs:  get(DemoBase.App.Services.PathPreferenceKeys.Configs),
            database: get(DemoBase.App.Services.PathPreferenceKeys.Database),
            releases: get(DemoBase.App.Services.PathPreferenceKeys.Releases),
            working:  get(DemoBase.App.Services.PathPreferenceKeys.Working));
    }
}

// ─── Code-behind ──────────────────────────────────────────────────────────────

public partial class FoldersPage : UserControl
{
    public FoldersPageViewModel Vm { get; } = new();

    public FoldersPage()
    {
        InitializeComponent();
        DataContext        = Vm;
        DirsList.ItemsSource = Vm.Folders;
    }
}
