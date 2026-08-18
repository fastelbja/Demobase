using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.Core.Models;
using DemoBase.Data;
using System.Collections.ObjectModel;

namespace DemoBase.App.ViewModels;

public partial class FavoriteGraphicsViewModel : ObservableObject
{
    private readonly FavoriteGraphicService _favService;
    private readonly PreferencesService     _prefs;

    [ObservableProperty] private ObservableCollection<FavoriteGraphic> _graphics = [];
    [ObservableProperty] private bool              _isLoading;
    [ObservableProperty] private FavoriteGraphic?  _selectedGraphic;
    [ObservableProperty] private GraphicsViewerViewModel? _viewer;

    public bool HasGraphics => Graphics.Count > 0;

    public FavoriteGraphicsViewModel(
        FavoriteGraphicService favService,
        PreferencesService prefs)
    {
        _favService = favService;
        _prefs      = prefs;
        _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var list = await _favService.GetAllAsync();
            Graphics = new ObservableCollection<FavoriteGraphic>(list);
            OnPropertyChanged(nameof(HasGraphics));
        }
        finally { IsLoading = false; }
    }

    partial void OnSelectedGraphicChanged(FavoriteGraphic? value)
    {
        if (value != null)
            _ = ShowGraphicAsync(value);
    }

    private async Task ShowGraphicAsync(FavoriteGraphic fav)
    {
        if (fav.ZipPath == null) return;

        var prefs   = await _prefs.LoadAllAsync();
        var zipPath = System.IO.Path.Combine(prefs.ResolvedPathReleases, fav.ZipPath);

        if (!System.IO.File.Exists(zipPath))
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                $"Fichier introuvable : {zipPath}", isError: true);
            return;
        }

        if (Viewer == null)
            Viewer = new GraphicsViewerViewModel();

        await Viewer.LoadAsync(zipPath);

        // Sélectionner automatiquement le fichier mémorisé
        if (fav.FileInZip != null && Viewer.Entries.Count > 0)
        {
            var entry = Viewer.Entries.FirstOrDefault(e =>
                string.Equals(e.Name, fav.FileInZip, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
                Viewer.SelectedEntry = entry;
        }
    }

    [RelayCommand]
    private async Task RemoveGraphic(FavoriteGraphic fav)
    {
        await _favService.RemoveAsync(fav.ReleaseDemozooId);
        Graphics.Remove(fav);
        OnPropertyChanged(nameof(HasGraphics));
        if (SelectedGraphic == fav)
        {
            SelectedGraphic = null;
            Viewer = null;
        }
    }
}
