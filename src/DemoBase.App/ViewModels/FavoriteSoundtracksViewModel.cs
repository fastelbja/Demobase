using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.Core.Models;
using DemoBase.Data;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;

namespace DemoBase.App.ViewModels;

public partial class FavoriteSoundtracksViewModel : ObservableObject, IDisposable
{
    private readonly FavoriteSoundtrackService                      _favService;
    private readonly PreferencesService                             _prefs;
    private readonly TrackerPlayer.Core.Interfaces.ITrackerService? _tracker;
    private readonly DemoBase.Core.Interfaces.IReleaseService?      _releaseService;
    private readonly DemoBase.Core.Interfaces.INavigationService?   _navigation;
    private readonly PlaylistService?                               _playlistService;
    private readonly DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService? _releaseBuilderService;
    // 2026-07-30, demande utilisateur : favoris Modland partagés avec ce même système —
    // un SoundtrackDemozooId négatif synthétique (-ModlandTrackRow.Id) identifie une piste
    // Modland (jamais de collision, les vrais DemozooId Demozoo étant toujours positifs).
    // Cf. BuildPlaylistAsync ci-dessous pour le branchement lecture.
    private readonly DemoBase.App.Services.ModlandService?          _modlandService;

    // Correspondance chemin extrait → index favori (pour CurrentIndex en PlayAll)
    private readonly Dictionary<string, int> _pathToIndex = new(StringComparer.OrdinalIgnoreCase);

    // Tous les favoris (non filtrés) — Soundtracks n'affiche que ceux qui ne sont
    // dans aucune playlist (cf. RefreshVisibleSoundtracks) : une fois une piste
    // rangée dans une playlist, elle disparaît de la colonne principale, comme
    // dans Spotify (Titres likés vs Playlists).
    private List<FavoriteSoundtrack> _allFavorites = [];
    private readonly HashSet<int> _playlistTrackIds = new();

    [ObservableProperty] private ObservableCollection<FavoriteSoundtrack> _soundtracks = [];
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private bool   _isPlaying;
    [ObservableProperty] private DemoBase.App.Views.Releases.SoundtrackPlayerView? _player;
    [ObservableProperty] private FavoriteSoundtrack? _currentTrack;
    [ObservableProperty] private int _currentIndex = -1;
    [ObservableProperty] private ObservableCollection<PlaylistItemViewModel> _playlists = [];
    [ObservableProperty] private bool   _isBuildingRelease;
    [ObservableProperty] private string _buildStatusMessage = "";

    public bool HasTracks    => Soundtracks.Count > 0;
    public bool HasPlaylists => Playlists.Count > 0;

    public FavoriteSoundtracksViewModel(
        FavoriteSoundtrackService favService,
        PreferencesService prefs,
        TrackerPlayer.Core.Interfaces.ITrackerService? tracker = null,
        DemoBase.Core.Interfaces.IReleaseService? releaseService = null,
        DemoBase.Core.Interfaces.INavigationService? navigation = null,
        PlaylistService? playlistService = null,
        DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService? releaseBuilderService = null,
        DemoBase.App.Services.ModlandService? modlandService = null)
    {
        _favService             = favService;
        _prefs                  = prefs;
        _tracker                = tracker;
        _releaseService         = releaseService;
        _navigation             = navigation;
        _playlistService        = playlistService;
        _releaseBuilderService  = releaseBuilderService;
        _modlandService         = modlandService;
        _ = LoadAsync();
        _ = LoadPlaylistsAsync();
    }

    // ── Propriété Player — abonnement à CurrentFileName pour tracker PlayAll ──

    partial void OnPlayerChanged(
        DemoBase.App.Views.Releases.SoundtrackPlayerView? oldValue,
        DemoBase.App.Views.Releases.SoundtrackPlayerView? newValue)
    {
        if (oldValue != null)
            oldValue.Vm.PropertyChanged -= OnPlayerVmPropertyChanged;
        if (newValue != null)
            newValue.Vm.PropertyChanged += OnPlayerVmPropertyChanged;
    }

    private void OnPlayerVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoundtrackPlayerViewModel.CurrentFileName))
        {
            var fileName = Player?.Vm.CurrentFileName ?? string.Empty;
            if (_pathToIndex.TryGetValue(fileName, out int idx) && idx < Soundtracks.Count)
            {
                CurrentIndex = idx;
                CurrentTrack = Soundtracks[idx];
            }
        }
        else if (e.PropertyName == nameof(SoundtrackPlayerViewModel.IsPlaying))
        {
            IsPlaying = Player?.Vm.IsPlaying ?? false;
        }
    }

    // ── Chargement ────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _allFavorites = await _favService.GetAllAsync();
            RefreshVisibleSoundtracks();
        }
        finally { IsLoading = false; }
    }

    /// <summary>Recalcule Soundtracks = tous les favoris SAUF ceux déjà présents
    /// dans au moins une playlist. Appelé après tout changement de
    /// _allFavorites ou de composition des playlists.</summary>
    private void RefreshVisibleSoundtracks()
    {
        Soundtracks = new ObservableCollection<FavoriteSoundtrack>(
            _allFavorites.Where(f => !_playlistTrackIds.Contains(f.SoundtrackDemozooId)));
        OnPropertyChanged(nameof(HasTracks));
    }

    /// <summary>Reconstruit l'ensemble des DemozooId présents dans au moins une
    /// playlist, à partir de l'état actuel de Playlists.</summary>
    private void RecomputePlaylistTrackIds()
    {
        _playlistTrackIds.Clear();
        foreach (var pl in Playlists)
            foreach (var t in pl.Tracks)
                _playlistTrackIds.Add(t.SoundtrackDemozooId);
    }

    // ── Playlists ────────────────────────────────────────────────────────────

    public async Task LoadPlaylistsAsync()
    {
        if (_playlistService == null) return;
        var list = await _playlistService.GetAllAsync();
        var items = new List<PlaylistItemViewModel>();
        foreach (var pl in list)
        {
            var item = new PlaylistItemViewModel(pl);
            var tracks = await _playlistService.GetTracksAsync(pl.Id);
            item.Tracks = new ObservableCollection<FavoriteSoundtrack>(tracks);
            items.Add(item);
        }
        Playlists = new ObservableCollection<PlaylistItemViewModel>(items);
        OnPropertyChanged(nameof(HasPlaylists));

        RecomputePlaylistTrackIds();
        RefreshVisibleSoundtracks();
    }

    /// <summary>Playlist actuellement sélectionnée (une seule à la fois) — c'est
    /// la cible du bouton "➕" sur un favori non classé quand une playlist est
    /// sélectionnée (sinon, "➕" ouvre un menu de choix, cf. AddToPlaylist_Click
    /// dans le code-behind de la vue).</summary>
    public PlaylistItemViewModel? ActivePlaylist => Playlists.FirstOrDefault(p => p.IsSelected);

    /// <summary>Sélectionne/désélectionne une playlist (clic sur son en-tête) —
    /// une seule sélectionnée à la fois (les autres sont repliées/désactivées).
    /// La sélection sert à la fois à déplier ses pistes ET à la désigner comme
    /// cible du "➕" sur la colonne des favoris non classés.</summary>
    [RelayCommand]
    private void SelectPlaylist(PlaylistItemViewModel item)
    {
        var wasSelected = item.IsSelected;
        foreach (var p in Playlists) p.IsSelected = false;
        item.IsSelected = !wasSelected;
        OnPropertyChanged(nameof(ActivePlaylist));
    }

    /// <summary>Crée une nouvelle playlist via la boîte de dialogue de saisie du
    /// nom — factorisé entre CreatePlaylistCommand et l'option "Nouvelle
    /// playlist…" du menu "➕" (créer directement en ajoutant une piste).</summary>
    private async Task<PlaylistItemViewModel?> CreateNewPlaylistAsync(System.Windows.Window? owner = null)
    {
        if (_playlistService == null) return null;
        var mainWin = owner ?? System.Windows.Application.Current.MainWindow;
        var dialog  = new DemoBase.App.Views.PlaylistNameDialog { Owner = mainWin };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ResultName)) return null;

        var playlist = await _playlistService.CreateAsync(dialog.ResultName);
        var item = new PlaylistItemViewModel(playlist);
        Playlists.Add(item);
        OnPropertyChanged(nameof(HasPlaylists));
        return item;
    }

    [RelayCommand]
    private async Task CreatePlaylist() => await CreateNewPlaylistAsync();

    /// <summary>Crée une nouvelle playlist ET y ajoute directement la piste —
    /// option "➕ Nouvelle playlist…" du menu du bouton "➕" (code-behind de la
    /// vue), pour ne pas obliger l'utilisateur à créer la playlist à part avant
    /// de pouvoir y glisser un premier morceau.</summary>
    public async Task CreatePlaylistAndAddTrackAsync(FavoriteSoundtrack track)
    {
        var item = await CreateNewPlaylistAsync();
        if (item == null) return;
        await AddTrackToPlaylistAsync(track, item);
    }

    [RelayCommand]
    private async Task RenamePlaylist(PlaylistItemViewModel item)
    {
        if (_playlistService == null) return;
        var mainWin = System.Windows.Application.Current.MainWindow;
        var dialog  = new DemoBase.App.Views.PlaylistNameDialog(item.Name) { Owner = mainWin };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ResultName)) return;

        await _playlistService.RenameAsync(item.Id, dialog.ResultName);
        item.Name = dialog.ResultName;
    }

    [RelayCommand]
    private async Task DeletePlaylist(PlaylistItemViewModel item)
    {
        if (_playlistService == null) return;
        var msg = string.Format(
            DemoBase.App.Services.LocalizationService.Get("PL_DeleteConfirm"), item.Name);
        var result = System.Windows.MessageBox.Show(
            msg, "", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        await _playlistService.DeleteAsync(item.Id);
        Playlists.Remove(item);
        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(ActivePlaylist));

        // Les pistes qui n'étaient que dans cette playlist réapparaissent
        // dans la colonne principale.
        RecomputePlaylistTrackIds();
        RefreshVisibleSoundtracks();
    }

    [RelayCommand]
    private async Task PlayPlaylist(PlaylistItemViewModel item)
    {
        if (_tracker == null || item.Tracks.Count == 0) return;
        await PlayTrackListAsync(item.Tracks);
    }

    [RelayCommand]
    private async Task PlayAllPlaylists()
    {
        if (_tracker == null || Playlists.Count == 0) return;
        var all = Playlists.SelectMany(p => p.Tracks).ToList();
        if (all.Count == 0) return;
        await PlayTrackListAsync(all);
    }

    /// <summary>Ajoute une piste favorite à une playlist — appelé depuis le menu
    /// contextuel "➕ Ajouter à une playlist" (code-behind, cf. FavoriteSoundtracksView).</summary>
    public async Task AddTrackToPlaylistAsync(FavoriteSoundtrack track, PlaylistItemViewModel playlist)
    {
        if (_playlistService == null) return;
        await _playlistService.AddTrackAsync(playlist.Id, track.SoundtrackDemozooId);
        if (!playlist.Tracks.Any(t => t.SoundtrackDemozooId == track.SoundtrackDemozooId))
            playlist.Tracks.Add(track);

        // La piste est maintenant rangée dans une playlist — elle disparaît de
        // la colonne principale (comme "Titres likés" vs playlists sur Spotify).
        _playlistTrackIds.Add(track.SoundtrackDemozooId);
        RefreshVisibleSoundtracks();
    }

    /// <summary>Retire une piste d'une playlist — appelé depuis le code-behind
    /// (bouton "✕" sur une piste, dans la playlist dépliée).</summary>
    public async Task RemoveTrackFromPlaylistAsync(PlaylistItemViewModel playlist, FavoriteSoundtrack track)
    {
        if (_playlistService == null) return;
        await _playlistService.RemoveTrackAsync(playlist.Id, track.SoundtrackDemozooId);
        playlist.Tracks.Remove(track);

        // Si la piste n'est plus dans AUCUNE playlist, elle réapparaît dans la
        // colonne principale.
        var stillElsewhere = Playlists.Any(p =>
            p.Tracks.Any(t => t.SoundtrackDemozooId == track.SoundtrackDemozooId));
        if (!stillElsewhere)
        {
            _playlistTrackIds.Remove(track.SoundtrackDemozooId);
            RefreshVisibleSoundtracks();
        }
    }

    /// <summary>Déplace une piste dans une playlist (-1 monter, +1 descendre) —
    /// appelé depuis le code-behind (boutons ▲/▼).</summary>
    public async Task MoveTrackAsync(PlaylistItemViewModel playlist, FavoriteSoundtrack track, int direction)
    {
        if (_playlistService == null) return;
        var idx = playlist.Tracks.IndexOf(track);
        var swapIdx = idx + direction;
        if (idx < 0 || swapIdx < 0 || swapIdx >= playlist.Tracks.Count) return;

        await _playlistService.MoveTrackAsync(playlist.Id, track.SoundtrackDemozooId, direction);
        playlist.Tracks.Move(idx, swapIdx);
    }

    /// <summary>Lit une liste de pistes en enchaîné (playlist unique ou toutes
    /// les playlists concaténées) — même mécanisme que PlayAll.</summary>
    private async Task PlayTrackListAsync(IEnumerable<FavoriteSoundtrack> tracks)
    {
        var trackList = tracks.ToList();
        var paths = await BuildPlaylistAsync(trackList);
        if (paths.Count == 0) return;

        EnsurePlayer();

        _pathToIndex.Clear();
        for (int i = 0; i < paths.Count && i < trackList.Count; i++)
        {
            // Index dans la liste principale des favoris, pour rester cohérent
            // avec CurrentTrack/CurrentIndex même quand on joue une playlist.
            var mainIdx = Soundtracks.ToList().FindIndex(
                s => s.SoundtrackDemozooId == trackList[i].SoundtrackDemozooId);
            if (mainIdx >= 0) _pathToIndex[paths[i]] = mainIdx;
        }

        await Player!.Vm.LoadFilesAsync(paths);

        CurrentIndex = _pathToIndex.TryGetValue(paths[0], out var firstIdx) ? firstIdx : -1;
        CurrentTrack = trackList.Count > 0 ? trackList[0] : null;
        IsPlaying    = true;
    }

    // ── Commandes ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PlayTrack(FavoriteSoundtrack track)
    {
        if (_tracker == null) return;

        var paths = await BuildPlaylistAsync([track]);
        if (paths.Count == 0) return;

        EnsurePlayer();

        // Un seul track → Player.OpenAsync suffit, pas besoin de LoadFilesAsync
        await Player!.OpenAsync(paths[0]);

        CurrentIndex = Soundtracks.IndexOf(track);
        CurrentTrack = track;
        IsPlaying    = true;
    }

    [RelayCommand]
    private async Task PlayAll()
    {
        if (_tracker == null || !Soundtracks.Any()) return;

        var paths = await BuildPlaylistAsync(Soundtracks);
        if (paths.Count == 0) return;

        EnsurePlayer();

        // Alimenter la map chemin → index pour que OnPlayerVmPropertyChanged
        // mette à jour CurrentIndex quand PlayAll avance automatiquement.
        _pathToIndex.Clear();
        var soundtrackList = Soundtracks.ToList();
        for (int i = 0; i < paths.Count && i < soundtrackList.Count; i++)
            _pathToIndex[paths[i]] = i;

        // SoundtrackPlayerView gère toute la playlist en interne (auto-avancement,
        // preload, cross-fade) — pas besoin d'un deuxième player en parallèle.
        await Player!.Vm.LoadFilesAsync(paths);

        CurrentIndex = 0;
        CurrentTrack = soundtrackList.Count > 0 ? soundtrackList[0] : null;
        IsPlaying    = true;
    }

    [RelayCommand]
    private async Task OpenRelease(FavoriteSoundtrack track)
    {
        if (_releaseService == null || _navigation == null) return;

        // 2026-07-30 : favori Modland (SoundtrackDemozooId négatif synthétique) — pas de
        // release Demozoo associée, rien à ouvrir (cf. BuildPlaylistAsync pour le contexte).
        if (track.SoundtrackDemozooId < 0)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Piste Modland — pas de fiche release associée.", isWarning: true);
            return;
        }

        var id = await _releaseService.GetIdByDemozooIdAsync(track.SoundtrackDemozooId);
        if (id == null)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Release introuvable (peut-être supprimée).", isError: true);
            return;
        }
        _navigation.NavigateTo<DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel>(id.Value);
    }

    [RelayCommand]
    private async Task RemoveTrack(FavoriteSoundtrack track)
    {
        await _favService.RemoveAsync(track.SoundtrackDemozooId);
        _allFavorites.RemoveAll(f => f.SoundtrackDemozooId == track.SoundtrackDemozooId);
        RefreshVisibleSoundtracks();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsurePlayer()
    {
        if (Player == null)
            Player = new DemoBase.App.Views.Releases.SoundtrackPlayerView(_tracker!);
    }

    /// <summary>Extrait tous les fichiers et retourne leurs chemins locaux. Si le
    /// ZIP d'une piste n'est pas encore présent sur le disque (favori ajouté
    /// avant tout téléchargement — cas fréquent pour une playlist qui vient
    /// d'être constituée), tente de le télécharger à la demande via
    /// ReleaseBuilderService (même filet que le bouton "Lire" sur la fiche
    /// release). Sans ce filet, une playlist dont les ZIP ne sont pas encore
    /// en cache échouait silencieusement : PlayPlaylist ne faisait "rien".</summary>
    private async Task<List<string>> BuildPlaylistAsync(IEnumerable<FavoriteSoundtrack> tracks)
    {
        var prefs   = await _prefs.LoadAllAsync();
        var result  = new List<string>();
        var missing = new List<string>();
        foreach (var track in tracks)
        {
            // 2026-07-30, demande utilisateur : favori Modland (SoundtrackDemozooId négatif
            // synthétique, cf. ModlandBrowserViewModel.ToggleFavorite) — ZipPath stocke ici le
            // chemin relatif Modland ("Format/Auteur/fichier"), pas un chemin de ZIP DAT.
            // Téléchargement direct (cache local persistant), aucune extraction ZIP.
            if (track.SoundtrackDemozooId < 0)
            {
                if (track.ZipPath == null || _modlandService == null)
                {
                    missing.Add(track.Title);
                    continue;
                }
                try
                {
                    result.Add(await _modlandService.DownloadByRelativePathAsync(track.ZipPath));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[PLAYLIST] Modland — échec téléchargement {track.Title} : {ex.Message}");
                    missing.Add(track.Title);
                }
                continue;
            }

            if (track.ZipPath == null || track.RomName == null)
            {
                missing.Add(track.Title);
                continue;
            }

            var fullZip = Path.Combine(prefs.ResolvedPathReleases, track.ZipPath);
            if (!File.Exists(fullZip))
                await TryDownloadZipAsync(track);

            var path = await ExtractAsync(prefs.ResolvedPathReleases, track.ZipPath, track.RomName);
            if (path != null) result.Add(path);
            else missing.Add(track.Title);
        }

        if (missing.Count > 0)
        {
            var names = string.Join(", ", missing.Take(3)) + (missing.Count > 3 ? "…" : "");
            DemoBase.App.Controls.StatusScrollerControl.Post(
                $"{missing.Count} musique(s) introuvable(s) ou non téléchargeable(s) : {names}",
                isError: true);
        }

        return result;
    }

    /// <summary>Télécharge le ZIP d'une piste manquante — même mécanisme que
    /// PlayFromDatAsync (ReleaseDetailViewModel), appliqué ici par piste.</summary>
    private async Task TryDownloadZipAsync(FavoriteSoundtrack track)
    {
        if (_releaseBuilderService == null) return;
        IsBuildingRelease   = true;
        BuildStatusMessage  = $"Téléchargement de « {track.Title} »…";
        try
        {
            var progress = new Progress<DemoBase.App.Services.ReleaseBuilder.BuildProgress>(
                p => BuildStatusMessage = p.Message);
            await Task.Factory.StartNew(
                () => _releaseBuilderService.TryBuildAsync(track.SoundtrackDemozooId, progress),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PLAYLIST] Téléchargement échoué pour {track.Title} : {ex.Message}");
        }
        finally { IsBuildingRelease = false; }
    }

    // ── Extraction ZIP ────────────────────────────────────────────────────────

    private static Task<string?> ExtractAsync(string romsRoot, string zipPath, string romName)
        => Task.Run(() => ExtractSync(romsRoot, zipPath, romName));

    private static string? ExtractSync(string romsRoot, string zipPath, string romName)
    {
        try
        {
            var fullZip = Path.Combine(romsRoot, zipPath);
            if (!File.Exists(fullZip)) return null;

            var zipHash = Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(zipPath)))[..8].ToLowerInvariant();
            var tempDir = Path.Combine(
                DemoBase.App.Services.WorkingPaths.GetSubdir("Tracker"),
                "mus_" + zipHash);
            Directory.CreateDirectory(tempDir);

            var normalizedName = DemoBase.Core.DTOs.TrackerExtensions.NormalizeFilename(romName);
            var extractedPath  = Path.Combine(tempDir, normalizedName);
            if (!File.Exists(extractedPath))
            {
                using var zip = ZipFile.OpenRead(fullZip);
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, romName, StringComparison.OrdinalIgnoreCase)
                    || e.FullName.EndsWith(romName, StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                    entry.ExtractToFile(extractedPath, overwrite: true);
            }
            return File.Exists(extractedPath) ? extractedPath : null;
        }
        catch { return null; }
    }

    // ── Nettoyage ─────────────────────────────────────────────────────────────

    /// <summary>Arrête la lecture — appelé par MainViewModel à la navigation.</summary>
    public void StopPlayback()
    {
        if (Player != null && (Player.Vm.IsPlaying || Player.Vm.IsPaused))
        {
            Player.Stop();
            IsPlaying = false;
        }
    }

    public void Dispose()
    {
        if (Player != null)
        {
            Player.Vm.PropertyChanged -= OnPlayerVmPropertyChanged;
            Player.Stop();
        }
    }
}
