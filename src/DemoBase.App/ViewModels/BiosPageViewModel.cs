using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.App.Services;

namespace DemoBase.App.ViewModels;

/// <summary>ViewModel léger pour la page BIOS du wizard et le panneau émulateurs.</summary>
public partial class BiosPageViewModel : ObservableObject
{
    [ObservableProperty] private bool    _isBiosDownloading;
    [ObservableProperty] private string  _biosDownloadLabel   = string.Empty;
    [ObservableProperty] private int     _biosDownloadPercent;
    [ObservableProperty] private string? _biosStatusMessage;

    /// <summary>True si des fichiers BIOS sont déjà présents dans AppPaths.Bios.</summary>
    public bool IsBiosAlreadyInstalled =>
        System.IO.Directory.Exists(AppPaths.Bios) &&
        System.IO.Directory.EnumerateFiles(AppPaths.Bios, "*", System.IO.SearchOption.AllDirectories).Any();

    public BiosPageViewModel()
    {
        // Pré-remplir le statut si le pack est déjà là
        if (IsBiosAlreadyInstalled)
            BiosStatusMessage = $"✓ Pack BIOS déjà installé ({AppPaths.Bios})";
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadBiosPack(System.Threading.CancellationToken ct = default)
    {
        IsBiosDownloading   = true;
        BiosStatusMessage   = null;
        BiosDownloadPercent = 0;
        BiosDownloadLabel   = string.Empty;

        try
        {
            var progress = new Progress<(string Label, int Percent)>(p =>
            {
                BiosDownloadLabel   = p.Label;
                BiosDownloadPercent = p.Percent;
            });
            var svc = new BiosPackService();
            var (success, message) = await svc.DownloadAndInstallAsync(progress, ct);
            BiosStatusMessage = success ? $"✓ {message}" : $"✗ {message}";
        }
        catch (Exception ex)
        {
            BiosStatusMessage = $"✗ Erreur : {ex.Message}";
        }
        finally
        {
            IsBiosDownloading   = false;
            BiosDownloadPercent = 0;
            BiosDownloadLabel   = string.Empty;
        }
    }

    private bool CanDownload() => !IsBiosDownloading;

    partial void OnIsBiosDownloadingChanged(bool value)
        => DownloadBiosPackCommand.NotifyCanExecuteChanged();
}
