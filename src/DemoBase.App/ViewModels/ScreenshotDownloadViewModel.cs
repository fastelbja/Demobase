using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.Media;
using System.IO;

namespace DemoBase.App.ViewModels;

public partial class ScreenshotDownloadViewModel : ObservableObject
{
    private readonly ScreenshotDownloadService _service;
    private readonly string _imagesRoot;

    [ObservableProperty] private string _statusMessage    = "Cliquez sur Démarrer pour lancer le téléchargement.";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private int    _downloaded;
    [ObservableProperty] private int    _skipped;
    [ObservableProperty] private int    _errors;
    [ObservableProperty] private int    _total;
    [ObservableProperty] private bool   _isRunning;
    [ObservableProperty] private string _imagesRootDisplay = string.Empty;

    private CancellationTokenSource? _cts;

    public ScreenshotDownloadViewModel(ScreenshotDownloadService service, string imagesRoot)
    {
        _service          = service;
        _imagesRoot       = imagesRoot;
        ImagesRootDisplay = imagesRoot;
    }

    // Chargé après ouverture de la fenêtre — affiche les stats
    public async Task LoadStatsAsync()
    {
        try
        {
            var (total, local) = await _service.GetStatsAsync(_imagesRoot);
            Total         = total;
            Skipped       = local;
            StatusMessage = total == 0
                ? "Aucun screenshot en base. Lancez d'abord un import."
                : $"{total:N0} screenshots en base · {local:N0} déjà locaux · {total - local:N0} à télécharger";
        }
        catch (Exception ex) { StatusMessage = $"Erreur stats : {ex.Message}"; }
    }

    // ── Démarrer ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        _cts      = new CancellationTokenSource();

        var progress = new Progress<ScreenshotDownloadProgress>(p =>
        {
            StatusMessage   = p.Message;
            ProgressPercent = p.Percent;
            Downloaded      = p.Downloaded;
            Skipped         = p.Skipped;
            Errors          = p.Errors;
            Total           = (int)p.Total;
        });

        try   { await _service.DownloadAllAsync(_imagesRoot, progress, _cts.Token); }
        catch (OperationCanceledException) { StatusMessage = "Téléchargement annulé."; }
        catch (Exception ex)               { StatusMessage = $"Erreur : {ex.Message}"; }
        finally { IsRunning = false; }
    }

    // ── Arrêter ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    // ── Ouvrir le dossier ─────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(_imagesRoot);
        System.Diagnostics.Process.Start("explorer.exe", _imagesRoot);
    }
}
