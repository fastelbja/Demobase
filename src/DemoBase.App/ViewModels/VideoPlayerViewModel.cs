using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using System.Windows.Threading;

namespace DemoBase.App.ViewModels;

/// <summary>
/// DTO léger pour un lien vidéo lisible (YouTube, Vimeo, fichier local).
/// </summary>
public class VideoLinkDto
{
    public string Title         { get; init; } = string.Empty;
    public string Url           { get; init; } = string.Empty;
    public string LinkClass     { get; init; } = string.Empty;
    public bool   OpenInBrowser { get; init; } = false;

    public bool   IsYouTube   => LinkClass == "YoutubeVideo";
    public bool   IsVimeo     => LinkClass == "VimeoVideo";
    public bool   IsLocalFile => !Url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    public string Icon        => IsYouTube ? "YouTube" : IsVimeo ? "Vimeo" : "▶";
    public string ButtonLabel => OpenInBrowser ? "🌐 " + DemoBase.App.Services.LocalizationService.Get("App_Open") : "▶ " + DemoBase.App.Services.LocalizationService.Get("RD_Play");
}

/// <summary>
/// ViewModel du lecteur vidéo LibVLC intégré dans la fiche release.
/// Gère : play/pause/stop, seek, volume, transition entre vidéos de la liste.
/// </summary>
public partial class VideoPlayerViewModel : ObservableObject, IDisposable
{
    // ── LibVLC ────────────────────────────────────────────────────────────────
    private LibVLC?         _libVlc;
    private MediaPlayer?    _mediaPlayer;
    private DispatcherTimer _positionTimer;
    private bool            _isSeeking;
    private bool            _disposed;

    // ── État lecture ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isPlaying;
    [ObservableProperty] private bool   _isPaused;
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private bool   _hasMedia;
    [ObservableProperty] private string _errorMessage = string.Empty;

    // ── Métadonnées ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _currentTitle = string.Empty;

    // ── Transport ─────────────────────────────────────────────────────────────
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1.0;
    [ObservableProperty] private int    _volume = 80;
    [ObservableProperty] private bool   _isMuted;

    // ── Playlist ──────────────────────────────────────────────────────────────
    [ObservableProperty] private List<VideoLinkDto> _playlist = [];
    [ObservableProperty] private VideoLinkDto?               _selectedVideo;
    [ObservableProperty] private int                         _playlistIndex = -1;

    public bool HasPrevious => PlaylistIndex > 0;
    public bool HasNext     => PlaylistIndex < Playlist.Count - 1;

    // ── Propriété connectée au VideoView WPF (fixée depuis le code-behind) ────
    public MediaPlayer? MediaPlayer => _mediaPlayer;

    /// <summary>
    /// 2026-07-27, retour utilisateur : en naviguant vers l'onglet Vidéo pendant qu'un
    /// soundtrack (TrackerPlayer) de la même release est en cours de lecture, la vidéo se
    /// lançait automatiquement PAR-DESSUS la musique déjà en cours (aucune des deux ne
    /// s'arrête, son mélangé). Callback optionnel fourni par ReleaseDetailViewModel
    /// (RefreshVideoPlayer/LoadLocalVideosAsync, aux 3 endroits qui créent un
    /// VideoPlayerViewModel) — évalué à l'instant T (pas figé à la création du VM, sinon une
    /// musique démarrée APRÈS la construction du player vidéo ne serait jamais détectée) via
    /// SoundtrackPlayer?.Vm.IsPlaying. Si vrai, <see cref="PlayCurrent"/> n'auto-démarre pas
    /// la vidéo — elle reste chargée/prête, l'utilisateur peut toujours cliquer ▶ à la main.
    /// </summary>
    public Func<bool>? IsOtherAudioPlaying { get; set; }

    public VideoPlayerViewModel()
    {
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _positionTimer.Tick += OnPositionTick;
    }

    // ─── Initialisation LibVLC (appelée une fois depuis la vue) ───────────────

    public void InitializeVlc()
    {
        if (_libVlc != null) { System.Diagnostics.Debug.WriteLine("[VLC-VM] InitializeVlc — already init, skip"); return; }
        try
        {
            System.Diagnostics.Debug.WriteLine("[VLC-VM] InitializeVlc — calling Core.Initialize()");
            LibVLCSharp.Shared.Core.Initialize();
            System.Diagnostics.Debug.WriteLine("[VLC-VM] Core.Initialize() done — creating LibVLC instance");

            // Vérifier si les modules Lua YouTube/Vimeo sont présents (informatif)
            var luaPlaylistDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(
                    typeof(VideoPlayerViewModel).Assembly.Location)!,
                "libvlc", "win-x64", "lua", "playlist");
            var hasYoutubeLua = System.IO.File.Exists(
                System.IO.Path.Combine(luaPlaylistDir, "youtube.luac"));
            System.Diagnostics.Debug.WriteLine(
                $"[VLC-VM] lua/playlist/youtube.luac present={hasYoutubeLua}  dir={luaPlaylistDir}");

            // Pas d'options supplémentaires — LibVLC trouve ses plugins tout seul
            // dans libvlc/win-x64/ (déployé par VideoLAN.LibVLC.Windows NuGet)
            _libVlc = new LibVLC(enableDebugLogs: false);
            _mediaPlayer = new MediaPlayer(_libVlc);

            _mediaPlayer.Playing  += (_, _) => OnVlcPlaying();
            _mediaPlayer.Paused   += (_, _) => OnVlcPaused();
            _mediaPlayer.Stopped  += (_, _) => OnVlcStopped();
            _mediaPlayer.EndReached += (_, _) => OnVlcEndReached();
            _mediaPlayer.EncounteredError += (_, _) => OnVlcError();

            _mediaPlayer.Volume = Volume;
            System.Diagnostics.Debug.WriteLine("[VLC-VM] MediaPlayer created OK");
            OnPropertyChanged(nameof(MediaPlayer));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VLC-VM] InitializeVlc EXCEPTION: {ex}");
            ErrorMessage = $"Impossible d'initialiser LibVLC : {ex.Message}";
        }
    }

    // ─── Chargement d'une playlist ────────────────────────────────────────────

    /// <summary>
    /// Convertit des captures locales en VideoLinkDto et charge la playlist VLC.
    /// </summary>
    public void LoadLocalFiles(IEnumerable<DemoBase.App.Services.LocalCaptureVideoDto> locals)
    {
        var dtos = locals.Select(l => new VideoLinkDto
        {
            Title         = l.Label,   // "FHD · 25 fps · 16 min 28 s · making of"
            Url           = l.FilePath,
            LinkClass     = string.Empty,
            OpenInBrowser = false,
        }).ToList();

        LoadPlaylist(dtos);
    }

    public void LoadPlaylist(IEnumerable<VideoLinkDto> videos, int startIndex = 0)
    {
        // Stop uniquement si VLC est déjà initialisé
        if (_mediaPlayer != null) StopCommand.Execute(null);

        Playlist      = videos.ToList();
        PlaylistIndex = -1;
        ErrorMessage  = string.Empty;
        HasMedia      = Playlist.Count > 0;

        // Pré-sélectionner sans jouer (VLC peut ne pas être encore init)
        if (HasMedia && startIndex >= 0 && startIndex < Playlist.Count)
        {
            PlaylistIndex = startIndex;
            SelectedVideo = Playlist[startIndex];
            CurrentTitle  = SelectedVideo.Title;
        }

        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
    }

    /// <summary>
    /// Appelé depuis le code-behind après InitializeVlc(), pour démarrer la première vidéo.
    /// </summary>
    public void PlayCurrent()
    {
        if (IsOtherAudioPlaying?.Invoke() == true)
        {
            System.Diagnostics.Debug.WriteLine(
                "[VLC-VM] PlayCurrent — autoplay suspendu (autre lecture audio déjà en cours)");
            return;
        }
        if (_mediaPlayer != null && SelectedVideo != null && !SelectedVideo.OpenInBrowser)
            PlayAt(PlaylistIndex);
    }

    private void PlayAt(int index)
    {
        System.Diagnostics.Debug.WriteLine($"[VLC-VM] PlayAt({index}) — _mediaPlayer={(_mediaPlayer == null ? "NULL" : "OK")}  count={Playlist.Count}");
        if (_mediaPlayer == null || index < 0 || index >= Playlist.Count) return;

        PlaylistIndex  = index;
        SelectedVideo  = Playlist[index];
        CurrentTitle   = SelectedVideo.Title;
        ErrorMessage   = string.Empty;
        IsLoading      = true;

        System.Diagnostics.Debug.WriteLine($"[VLC-VM] Playing URL: {SelectedVideo.Url}  LinkClass={SelectedVideo.LinkClass}");
        try
        {
            var uri = new Uri(SelectedVideo.Url);
            System.Diagnostics.Debug.WriteLine($"[VLC-VM] URI ok: {uri.AbsoluteUri}");
            var media = new LibVLCSharp.Shared.Media(_libVlc!, uri);
            // Options pour YouTube/Vimeo via le module lua de VLC
            if (SelectedVideo.IsYouTube || SelectedVideo.IsVimeo)
            {
                media.AddOption(":no-video-title-show");
                System.Diagnostics.Debug.WriteLine("[VLC-VM] Added YouTube/Vimeo options");
            }

            var result = _mediaPlayer.Play(media);
            System.Diagnostics.Debug.WriteLine($"[VLC-VM] _mediaPlayer.Play() returned: {result}");
            media.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VLC-VM] PlayAt EXCEPTION: {ex}");
            ErrorMessage = $"Erreur : {ex.Message}";
            IsLoading    = false;
        }

        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
    }

    // ─── Commandes transport ──────────────────────────────────────────────────

    [RelayCommand]
    private void Play()
    {
        if (_mediaPlayer == null) return;
        if (IsPaused)       _mediaPlayer.Play();
        else if (!IsPlaying && SelectedVideo != null) PlayAt(PlaylistIndex);
    }

    [RelayCommand]
    private void Pause()
    {
        if (_mediaPlayer?.CanPause == true) _mediaPlayer.Pause();
    }

    [RelayCommand]
    private void Stop()
    {
        _positionTimer.Stop();
        _mediaPlayer?.Stop();
        IsPlaying      = false;
        IsPaused       = false;
        PositionSeconds = 0;
    }

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void Previous() => PlayAt(PlaylistIndex - 1);

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void Next() => PlayAt(PlaylistIndex + 1);

    [RelayCommand]
    private void PlayVideo(VideoLinkDto? video)
    {
        if (video == null) return;

        if (video.OpenInBrowser)
        {
            // YouTube/Vimeo : ouvrir dans le navigateur par défaut
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = video.Url,
                UseShellExecute = true,
            });
            return;
        }

        var idx = Playlist.IndexOf(video);
        if (idx >= 0) PlayAt(idx);
    }

    [RelayCommand]
    private void OpenInBrowserCmd(VideoLinkDto? video)
    {
        if (video?.Url == null) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = video.Url,
            UseShellExecute = true,
        });
    }

    // ─── Volume & Mute ────────────────────────────────────────────────────────

    partial void OnVolumeChanged(int value)
    {
        if (_mediaPlayer != null && !IsMuted)
            _mediaPlayer.Volume = value;
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    partial void OnIsMutedChanged(bool value)
    {
        if (_mediaPlayer != null)
            _mediaPlayer.Volume = value ? 0 : Volume;
    }

    // ─── Seek ─────────────────────────────────────────────────────────────────

    public void BeginSeek() => _isSeeking = true;

    public void EndSeek(double seconds)
    {
        if (_mediaPlayer == null || DurationSeconds <= 0)
        {
            _isSeeking = false;
            return;
        }
        var pos = (float)(seconds / DurationSeconds);
        _mediaPlayer.Position = Math.Clamp(pos, 0f, 1f);
        // Mettre à jour immédiatement l'affichage avant la reprise du timer
        PositionSeconds = Math.Clamp(seconds, 0, DurationSeconds);
        _isSeeking = false;
    }

    // ─── Events VLC (background threads → Dispatcher) ────────────────────────

    private void OnVlcPlaying()
    {
        System.Diagnostics.Debug.WriteLine($"[VLC-VM] EVENT: Playing — Length={_mediaPlayer?.Length}ms");
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsPlaying       = true;
            IsPaused        = false;
            IsLoading       = false;
            DurationSeconds = _mediaPlayer?.Length > 0
                ? _mediaPlayer.Length / 1000.0
                : 1.0;
            _positionTimer.Start();
        });
    }

    private void OnVlcPaused()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsPlaying = false;
            IsPaused  = true;
            _positionTimer.Stop();
        });
    }

    private void OnVlcStopped()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsPlaying       = false;
            IsPaused        = false;
            IsLoading       = false;
            _positionTimer.Stop();
        });
    }

    private void OnVlcEndReached()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _positionTimer.Stop();
            IsPlaying = false;
            // Auto-avance si playlist
            if (HasNext) PlayAt(PlaylistIndex + 1);
        });
    }

    private void OnVlcError()
    {
        System.Diagnostics.Debug.WriteLine("[VLC-VM] EVENT: EncounteredError !");
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsLoading    = false;
            IsPlaying    = false;
            ErrorMessage = "Impossible de lire cette vidéo. Les modules Lua YouTube de VLC sont peut-être absents (zinstaller VLC sur le système).";
        });
    }

    private void OnPositionTick(object? sender, EventArgs e)
    {
        if (_isSeeking || _mediaPlayer == null) return;
        PositionSeconds = _mediaPlayer.Position * DurationSeconds;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Filtre les ReleaseLinks pour ne garder que les vidéos lisibles.
    /// Utilise LinkClass (YoutubeVideo / VimeoVideo) ou, en fallback, l'extension de l'URL.
    /// </summary>
    public static IEnumerable<VideoLinkDto> ExtractVideoLinks(
        IEnumerable<DemoBase.Core.Models.ReleaseLink> links,
        string releaseTitle)
    {
        int i = 0;
        foreach (var l in links)
        {
            if (l.Url == null) continue;

            // YouTube / Vimeo : ouverture navigateur (VLC 3.x ne supporte plus l'API YouTube)
            if (l.IsYouTube || l.IsVimeo)
            {
                var platform = l.IsYouTube ? "YouTube" : "Vimeo";
                yield return new VideoLinkDto
                {
                    Title      = $"{releaseTitle} — {platform}",
                    Url        = l.Url,
                    LinkClass  = l.LinkClass ?? string.Empty,
                    OpenInBrowser = true,
                };
                i++;
                continue;
            }

            // Fichier vidéo local : lecture VLC inline
            if (HasVideoExtension(l.Url))
            {
                yield return new VideoLinkDto
                {
                    Title     = $"{releaseTitle} — vidéo {++i}",
                    Url       = l.Url,
                    LinkClass = string.Empty,
                    OpenInBrowser = false,
                };
            }
        }
    }

    private static bool HasVideoExtension(string url)
    {
        var ext = System.IO.Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".flv";
    }

    // ─── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _positionTimer.Stop();
        _mediaPlayer?.Stop();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }
}
