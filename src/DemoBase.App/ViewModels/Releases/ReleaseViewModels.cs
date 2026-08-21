using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.App.ViewModels.Library;
using DemoBase.Core.DTOs;
using DemoBase.Core.Interfaces;
using DemoBase.Core.Models;
using System.Collections.ObjectModel;
using System.IO.Compression;
using DemoBase.App.Services;
using DemoBase.Core.Diagnostics;

namespace DemoBase.App.ViewModels.Releases;

// ─── Release List ─────────────────────────────────────────────────────────────

public partial class ReleaseListViewModel : ObservableObject
{
    private readonly IReleaseService     _releaseService;
    private readonly IReleaseTypeService _releaseTypeService;
    private readonly INavigationService  _navigation;

    private const int PageSize = 120; // items chargés par page

    [ObservableProperty] private ObservableCollection<ReleaseSummaryDto> _releases = [];
    [ObservableProperty] private ReleaseSummaryDto? _selectedRelease;
    [ObservableProperty] private string  _searchQuery      = string.Empty;
    [ObservableProperty] private int?    _selectedReleaseTypeId;
    [ObservableProperty] private string? _selectedSupertype;
    [ObservableProperty] private int?    _selectedPlatformId;
    [ObservableProperty] private string? _selectedPlatformName;
    [ObservableProperty] private string? _selectedTypeName;  // badge affiché pour le filtre type
    [ObservableProperty] private bool    _isLoading;
    [ObservableProperty] private bool    _isFavoriteOnly;
    [ObservableProperty] private bool    _isUnseenOnly;
    [ObservableProperty] private ObservableCollection<YearChip> _yearChips = [];
    [ObservableProperty] private int?    _selectedYear;
    [ObservableProperty] private string  _sortBy         = "Title";
    [ObservableProperty] private bool    _sortDescending = false;
    [ObservableProperty] private bool    _isLoadingMore;
    [ObservableProperty] private int     _totalCount;
    [ObservableProperty] private int     _currentPage     = 1;
    [ObservableProperty] private bool    _hasMorePages;

    [ObservableProperty] private ReleaseDetailViewModel? _detailViewModel;

    // Debounce pour la recherche
    private CancellationTokenSource? _searchDebounce;

    // Index vidéos locales
    private readonly DemoBase.App.Services.LocalVideoCaptureService? _captureService;
    private HashSet<string>? _videoTitleIndex;
    private Task? _videoIndexTask;

    // Déclenché après chaque rechargement lié à la recherche — la vue doit scroller en haut
    public event Action? ScrollResetRequested;

    // Déclenché par MainViewModel quand cette vue redevient active → restaurer le scroll
    public event Action? ScrollRestoreRequested;
    public void TriggerScrollRestore() => ScrollRestoreRequested?.Invoke();


    // Position de scroll sauvegardée par filtre actif
    private readonly Dictionary<string, double> _scrollOffsets = new();
    private string ScrollKey => $"{SelectedPlatformId}|{SelectedReleaseTypeId}|{IsFavoriteOnly}|{IsUnseenOnly}|{SelectedYear}|{SearchQuery}";
    public double SavedScrollOffset
    {
        get => _scrollOffsets.TryGetValue(ScrollKey, out var v) ? v : 0;
        set => _scrollOffsets[ScrollKey] = value;
    }

    [ObservableProperty] private ObservableCollection<ReleaseTypeDto> _availableTypes = [];

    public ReleaseListViewModel(IReleaseService releaseService,
                                IReleaseTypeService releaseTypeService,
                                INavigationService navigation,
                                DemoBase.App.Services.LocalVideoCaptureService? captureService = null)
    {
        _releaseService     = releaseService;
        _releaseTypeService = releaseTypeService;
        _navigation         = navigation;
        _captureService     = captureService;
        _videoIndexTask = BuildVideoIndexAsync();
    }

    private async Task BuildVideoIndexAsync()
    {
        if (_captureService == null) return;
        try { _videoTitleIndex = await _captureService.BuildTitleIndexAsync(); }
        catch { _videoTitleIndex = []; }
    }

    public async Task LoadTypesAsync()
    {
        var types = (await _releaseTypeService.GetAllAsync())
            .OrderBy(t => t.Supertype).ThenBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToList();
        // Item "Tous" en tête (Id=0)
        types.Insert(0, new ReleaseTypeDto { Id = 0, Name = "— Tous les types —", ReleaseCount = 0 });
        AvailableTypes = new ObservableCollection<ReleaseTypeDto>(types);
    }

    public async Task LoadYearsAsync()
    {
        var years = await _releaseService.GetAvailableYearsAsync();
        YearChips = new ObservableCollection<YearChip>(
            years.Select(y => new YearChip { Year = y, Label = y.ToString() }));
    }

    // ── Chargement initial (page 1, remplace la liste) ───────────────────────

    private CancellationTokenSource? _loadCts;

    [RelayCommand]
    private async Task LoadAsync()
    {
        // Annuler tout chargement en cours pour éviter les accès EF Core concurrents
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        CurrentPage = 1;
        IsLoading    = true;
        HasMorePages = false;

        try
        {
            var result = await FetchPageAsync(1, ct: ct);
            if (ct.IsCancellationRequested) return;
            // Les items ont déjà HasLocalVideo mis à jour dans FetchPageAsync
            // On crée la collection après pour que le binding lise la bonne valeur
            Releases     = new ObservableCollection<ReleaseSummaryDto>(result.Items);
            TotalCount   = result.TotalCount;
            HasMorePages = result.TotalPages > 1;
        }
        catch (OperationCanceledException) { }
        finally { if (!ct.IsCancellationRequested) IsLoading = false; }
    }

    // ── Chargement page suivante (scroll infini, ajoute à la liste) ──────────

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (IsLoadingMore || !HasMorePages) return;

        IsLoadingMore = true;
        CurrentPage++;

        try
        {
            // SkipCount=true : on connaît déjà le total, pas besoin de recompter
            var result = await FetchPageAsync(CurrentPage, skipCount: true);
            foreach (var item in result.Items)
                Releases.Add(item);
            HasMorePages = CurrentPage < result.TotalPages;
        }
        finally { IsLoadingMore = false; }
    }

    private bool CanLoadMore() => HasMorePages && !IsLoadingMore && !IsLoading;

    private async Task<PagedResult<ReleaseSummaryDto>> FetchPageAsync(int page, bool skipCount = false,
        CancellationToken ct = default)
    {
        var filter = new ReleaseSearchFilter
        {
            Query         = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
            ReleaseTypeId = SelectedReleaseTypeId == 0 ? null : SelectedReleaseTypeId,
            Supertype     = SelectedSupertype,
            PlatformId    = SelectedPlatformId,
            YearFrom      = SelectedYear?.ToString(),
            YearTo        = SelectedYear?.ToString(),
            IsFavorite     = IsFavoriteOnly ? true : null,
            IsUnseen       = IsUnseenOnly ? true : null,
            SortBy         = SortBy,
            SortDescending = SortDescending,
            Page           = page,
            PageSize       = PageSize,
            SkipCount      = skipCount,
            KnownTotal     = skipCount ? TotalCount : 0,
        };

        PagedResult<ReleaseSummaryDto> result;
        using (var op = PerfLogger.Begin($"ReleaseList.SearchAsync (page={page})"))
        {
            // 2026-07-31, retour utilisateur (log de perf) : ct transmis jusqu'à SQLite —
            // cf. commentaire détaillé sur ReleaseService.SearchAsync (Services.cs). Sans
            // ça, une recherche annulée ici (LoadAsync ci-dessus, à chaque nouvelle frappe)
            // continuait de tourner jusqu'au bout côté SQLite et s'empilait derrière les
            // suivantes sur l'unique connexion partagée.
            result = await _releaseService.SearchAsync(filter, ct);
            op.WithDetail($"{result.Items.Count()} items / {result.TotalCount} total");
        }

        // Matérialiser en List pour pouvoir modifier les items
        var items = result.Items.ToList();

        // Attendre que l'index soit prêt (si pas encore terminé)
        if (_videoIndexTask != null && !_videoIndexTask.IsCompleted)
            await _videoIndexTask;
        // Marquer les releases qui ont des vidéos locales
        if (_videoTitleIndex != null && _videoTitleIndex.Count > 0)
            foreach (var item in items)
                item.HasLocalVideo = _captureService!.HasLocalVideos(item.Title);

        result.Items = items;
        return result;
    }

    [RelayCommand]
    private async Task ClearFavoritesFilter()
    {
        IsFavoriteOnly = false;
        await LoadAsync();
    }

    // Appelé depuis MainViewModel pour afficher les favoris
    public async Task ApplyFavoritesFilterAsync()
    {
        SearchQuery            = string.Empty;
        SelectedPlatformId     = null;
        SelectedPlatformName   = null;
        SelectedReleaseTypeId  = null;
        SelectedTypeName       = null;
        SelectedSupertype      = null;
        ClearYearSelection();
        IsFavoriteOnly         = true;
        IsUnseenOnly           = false;
        await LoadAsync();
        ScrollResetRequested?.Invoke();
    }

    // Appelé depuis MainViewModel quand on vient de PlatformListView
    public async Task ApplyPlatformFilterAsync(int platformId, string platformName)
    {
        SearchQuery           = string.Empty;
        SelectedReleaseTypeId = null;
        SelectedTypeName      = null;
        SelectedSupertype     = null;
        ClearYearSelection();
        IsFavoriteOnly        = false;
        IsUnseenOnly          = false;
        SelectedPlatformId    = platformId;
        SelectedPlatformName  = platformName;
        await LoadAsync();
        ScrollResetRequested?.Invoke();
    }

    public async Task ApplyTypeFilterAsync(int releaseTypeId, string typeName)
    {
        SearchQuery           = string.Empty;
        SelectedPlatformId    = null;
        SelectedPlatformName  = null;
        SelectedSupertype     = null;
        ClearYearSelection();
        IsFavoriteOnly        = false;
        IsUnseenOnly          = false;
        SelectedReleaseTypeId = releaseTypeId;
        SelectedTypeName      = typeName;
        await LoadAsync();
        ScrollResetRequested?.Invoke();
    }

    // Réinitialise la sélection d'année (et les chips visuelles) sans relancer de
    // requête — utilisé par les Apply*FilterAsync qui font leur propre LoadAsync().
    private void ClearYearSelection()
    {
        foreach (var c in YearChips) c.IsSelected = false;
        SelectedYear = null;
    }

    [RelayCommand]
    private void ClearPlatformFilter()
    {
        SelectedPlatformId   = null;
        SelectedPlatformName = null;
        _ = LoadAsync();
    }

    [RelayCommand]
    private void ClearTypeFilter()
    {
        SelectedReleaseTypeId = null;
        SelectedTypeName      = null;
        _ = LoadAsync();
    }

    // ── Sélection ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectRelease(ReleaseSummaryDto dto)
    {
        SelectedRelease = dto;
        _navigation.NavigateTo<ReleaseDetailViewModel>(dto.Id);
    }

    /// <summary>
    /// Sélectionne la release à l'index courant + offset (±1).
    /// Retourne le nouvel index, ou -1 si impossible.
    /// </summary>
    public int SelectByOffset(int offset)
    {
        if (Releases.Count == 0) return -1;

        int current = SelectedRelease != null ? Releases.IndexOf(SelectedRelease) : -1;
        int next    = current + offset;

        if (next < 0 || next >= Releases.Count) return -1;

        // Ne pas appeler SelectRelease (qui navigue) — le GlobalKeyboardService
        // gère la navigation après SelectByOffset pour harmoniser toutes les vues
        SelectedRelease = Releases[next];
        return next;
    }

    public void SelectAt(int index)
    {
        if (index >= 0 && index < Releases.Count)
            SelectRelease(Releases[index]);
    }

    // Propriétés calculées pour l'état des chips (OneWay depuis le XAML)
    public bool IsAllSelected        => SelectedSupertype == null;
    public bool IsProductionSelected => SelectedSupertype == "production";
    public bool IsGraphicsSelected   => SelectedSupertype == "graphics";
    public bool IsMusicSelected      => SelectedSupertype == "music";

    // ── Filtres ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private void FilterBySupertype(string? supertype)
    {
        // null = "Tous", sinon toggle : reclique sur le même = désactive
        SelectedSupertype = (supertype == SelectedSupertype) ? null : supertype;
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(IsProductionSelected));
        OnPropertyChanged(nameof(IsGraphicsSelected));
        OnPropertyChanged(nameof(IsMusicSelected));
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task SelectYearAsync(YearChip? chip)
    {
        foreach (var c in YearChips) c.IsSelected = false;
        if (chip != null && SelectedYear != chip.Year)
        {
            chip.IsSelected = true;
            SelectedYear    = chip.Year;
        }
        else
        {
            SelectedYear = null;
        }
        await LoadAsync();
        ScrollResetRequested?.Invoke();
    }

    // Debounce 350 ms sur la saisie pour éviter une requête à chaque touche
    partial void OnSearchQueryChanged(string value) => _ = DebounceSearchAsync();

    private async Task DebounceSearchAsync()
    {
        _searchDebounce?.Cancel();
        _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, _searchDebounce.Token);
            await LoadAsync();
            ScrollResetRequested?.Invoke();
        }
        catch (OperationCanceledException) { }
    }

    partial void OnSelectedReleaseTypeIdChanged(int? value) => _ = LoadAsync();
    partial void OnSortByChanged(string value)            => _ = LoadAsync();
    partial void OnIsUnseenOnlyChanged(bool value)        => _ = LoadAsync();

    [RelayCommand]
    private void ToggleSort(string field)
    {
        if (SortBy == field) SortDescending = !SortDescending;
        else { SortBy = field; SortDescending = field == "Date"; }
    }
    partial void OnSortDescendingChanged(bool value)      => _ = LoadAsync();

    [RelayCommand]
    private void CreateRelease() => _navigation.NavigateTo<ReleaseEditViewModel>(null);
}

// ─── Release Detail ───────────────────────────────────────────────────────────

/// <summary>DatEntry enrichi avec l'éventuel mismatch de téléchargement
/// associé à ses ROMs — affiché dans l'onglet Fichiers sous chaque set.</summary>
public class DatEntryWithMismatch(DemoBase.Core.Models.DatEntry entry)
{
    public DemoBase.Core.Models.DatEntry Entry    { get; } = entry;
    public DemoBase.Data.DownloadAttempt? Mismatch { get; set; }
    public bool HasMismatch => Mismatch != null;
}

public partial class ReleaseDetailViewModel : ObservableObject, IDisposable
{
    private readonly IReleaseService    _releaseService;
    private readonly IEmulatorService   _emulatorService;
    private readonly IMediaService      _mediaService;
    private readonly INavigationService _navigation;
    private readonly TrackerPlayer.Core.Interfaces.ITrackerService? _trackerService;
    private readonly DemoBase.Data.PreferencesService? _prefsService;
    private readonly DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService? _releaseBuilderService;
    private readonly DemoBase.Data.DownloadAttemptService? _downloadAttempts;

    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<DemoBase.Data.DownloadAttempt>
        _downloadMismatches = [];

    public bool HasDownloadMismatches => DownloadMismatches.Count > 0;

    partial void OnDownloadMismatchesChanged(System.Collections.ObjectModel.ObservableCollection<DemoBase.Data.DownloadAttempt> value)
        => OnPropertyChanged(nameof(HasDownloadMismatches));

    /// <summary>DatFiles enrichis avec l'éventuel mismatch associé à leurs ROMs — n'inclut PAS
    /// les entrées "Code Sources" (déplacées dans CodeSourcesWithMismatch, voir ci-dessous).</summary>
    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<DatEntryWithMismatch>
        _datFilesWithMismatch = [];

    /// <summary>DatEntry dont le champ SourceFile contient "Sources Code" (ex.
    /// "Ressources\Sources Codes\...") — affichées dans l'onglet "Code Sources" plutôt que
    /// "Fichiers". Même wrapper/template que DatFilesWithMismatch.</summary>
    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<DatEntryWithMismatch>
        _codeSourcesWithMismatch = [];

    public bool HasCodeSourcesFiles => CodeSourcesWithMismatch.Count > 0;

    partial void OnCodeSourcesWithMismatchChanged(System.Collections.ObjectModel.ObservableCollection<DatEntryWithMismatch> value)
        => OnPropertyChanged(nameof(HasCodeSourcesFiles));

    // Déclenché quand la lecture d'une release Music échoue (format non reconnu,
    // aucun fichier audio trouvé) — permet au MediaBrowser de passer au suivant.
    public event Action? PlaybackStartFailed;

    // ── Écran "Téléchargement en cours…" (reconstruction automatique de release) ──
    [ObservableProperty] private bool   _isBuildingRelease;
    [ObservableProperty] private string _buildStatusMessage = "";
    [ObservableProperty] private int    _buildStatusPercent;
    [ObservableProperty] private string? _buildErrorMessage;

    // 2026-07-29, retour utilisateur : "possibile de mettre dans la liste des fichiers celui
    // est correspond au dat ?" — Id (DatRom.Id) des fichiers trouvés lors de la dernière
    // tentative de reconstruction (ReleaseBuilderService.TryBuildAsync), pour afficher une
    // coche ✓ sur la bonne ligne dans la liste Entry.Roms (onglet Fichiers). Toujours réassigné
    // en entier (jamais muté) pour déclencher le PropertyChanged utilisé par le MultiBinding.
    [ObservableProperty] private HashSet<int> _lastBuildFoundRomIds = new();

    // 2026-07-27, retour utilisateur : bouton OK sur l'overlay "Téléchargement en cours…"
    // pour fermer manuellement l'état d'erreur (BuildErrorMessage) — sans lui, l'overlay
    // resterait bloqué à l'écran indéfiniment une fois qu'un échec l'empêche de se refermer
    // tout seul (cf. LaunchAsync/ResolveAdHocMediaFileAsync : le "finally" ne remet plus
    // IsBuildingRelease à false tant que BuildErrorMessage n'est pas vidé).
    [RelayCommand]
    private void DismissBuildError()
    {
        BuildErrorMessage = null;
        IsBuildingRelease = false;
    }

    /// <summary>
    /// Index de l'onglet sélectionné dans le TabControl principal (Infos=0, Crédits=1,
    /// Used In=2, Médias=3, Fichiers=4, Code Sources=5). Basculé automatiquement sur
    /// "Fichiers" quand un téléchargement échoue (demande utilisateur : sans ça, l'échec
    /// passe inaperçu si on est resté sur un autre onglet — il fallait aller vérifier
    /// "Fichiers" à la main pour comprendre pourquoi rien ne se lance).
    /// </summary>
    [ObservableProperty] private int _selectedTabIndex;
    private const int FilesTabIndex = 4;
    private const int CodeSourcesTabIndex = 5;

    // Dernier Release.Id pour lequel ShowCodeSourceAsync a déjà été déclenché — évite de
    // relancer le chargement/téléchargement à chaque clic sur l'onglet Code Sources tant
    // qu'on reste sur la même release.
    private int? _codeSourceLoadedForReleaseId;

    /// <summary>
    /// Contrairement à Graphics (qui s'affiche automatiquement dès l'ouverture de la
    /// release), Code Sources ne se charge (et ne tente un téléchargement si absent du
    /// disque) que lorsque l'utilisateur clique explicitement sur l'onglet — demande
    /// utilisateur explicite : le viewer ne doit pas apparaître tant que l'onglet n'a pas
    /// été ouvert.
    /// </summary>
    partial void OnSelectedTabIndexChanged(int value)
    {
        // ShowCodeSourceArea dépend de l'onglet actif : le viewer ne doit être visible QUE
        // pendant qu'on est sur l'onglet Code Sources, pas juste "au moins une fois visité"
        // (sinon il resterait affiché en revenant sur Infos après l'avoir consulté une fois).
        OnPropertyChanged(nameof(ShowCodeSourceArea));

        if (value != CodeSourcesTabIndex) return;
        if (!HasCodeSourcesFiles) return;
        if (_codeSourceLoadedForReleaseId == _lastLoadedReleaseId) return;

        _codeSourceLoadedForReleaseId = _lastLoadedReleaseId;
        _ = ShowCodeSourceAsync(_lastLoadedReleaseId);
    }
    private readonly DemoBase.Data.FavoriteSoundtrackService? _favService;
    private readonly DemoBase.Data.FavoriteGraphicService? _favGraphicService;
    private readonly DemoBase.App.Services.LocalVideoCaptureService? _videoCaptureService;

    [ObservableProperty] private ReleaseDetailDto? _detail;
    [ObservableProperty] private bool _isLoading;
    private int      _lastLoadedReleaseId = -1;      // guard anti-rechargement inutile
    private bool     _isLoadingNow        = false;    // guard anti-appels concurrents
    private DateTime _lastLoadTime        = DateTime.MinValue; // debounce

    // Cache statique : évite de rouvrir les mêmes ZIPs réseau à chaque navigation
    // (clé = chemin absolu du ZIP, valeur = contient-il un exécutable lançable ?)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        _launchableCache = new(StringComparer.OrdinalIgnoreCase);
    // Même principe pour la détection de vidéo compagnon (cf. HasVideoCompanion)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        _videoCompanionCache = new(StringComparer.OrdinalIgnoreCase);
    [ObservableProperty] private DemoBase.App.Views.Releases.SoundtrackPlayerView? _soundtrackPlayer;
    [ObservableProperty] private DemoBase.App.ViewModels.VideoPlayerViewModel? _videoPlayer;
    [ObservableProperty] private IReadOnlyList<DemoBase.App.Services.LocalCaptureVideoDto> _localVideos = [];
    [ObservableProperty] private bool _isLoadingVideos;
    [ObservableProperty] private bool _hasVideos;
    [ObservableProperty] private bool _hasLocalVideos;

    /// <summary>Contrôle la visibilité de la section "Liens" de l'onglet Infos
    /// (réactivée le 2026-07-24, cf. OnDetailChanged).</summary>
    [ObservableProperty] private bool _hasLinks;

    // ── Lecteur vidéo inline (releases de type "Video") ──────────────────────
    // Distinct du VideoPlayer du Media tab (YouTube/captures locales) : celui-ci
    // n'est peuplé que quand l'utilisateur clique explicitement sur "Play Video",
    // et n'affecte pas HasVideos/l'onglet Media.
    [ObservableProperty] private DemoBase.App.ViewModels.VideoPlayerViewModel? _inlineVideoPlayer;

    // Généré par CommunityToolkit.Mvvm : appelé automatiquement à chaque
    // assignation de SoundtrackPlayer (peu importe l'endroit du code), donc
    // pas besoin de dupliquer OnPropertyChanged(ShowSoundtrackArea) à chaque
    // site d'assignation existant ou futur.
    partial void OnSoundtrackPlayerChanged(DemoBase.App.Views.Releases.SoundtrackPlayerView? value)
    {
        OnPropertyChanged(nameof(ShowSoundtrackArea));
        OnPropertyChanged(nameof(ShowCodeSourceArea));
    }

    // Appelé automatiquement par CommunityToolkit quand VideoPlayer change
    partial void OnVideoPlayerChanged(DemoBase.App.ViewModels.VideoPlayerViewModel? value)
    {
        // HasVideos est géré manuellement pour inclure les liens YouTube sans player
    }

    public bool IsMusic    => Detail?.Release?.Supertype == "music"    && !IsExecutableMusicOrGraphics;
    public bool IsGraphics => Detail?.Release?.Supertype == "graphics" && !IsExecutableMusicOrGraphics;

    /// <summary>
    /// Vrai pour les releases de type "Executable Music"/"Executable Graphics" (ou toute
    /// variante dont le nom de type contient "executable") : bien que cataloguées comme
    /// music/graphics, ce sont en réalité des programmes qui doivent être lancés via
    /// l'émulateur de leur plateforme, pas joués/affichés comme de la musique/image
    /// streamable classique (demande utilisateur). IsMusic/IsGraphics deviennent donc
    /// faux pour ce cas, ce qui fait retomber LaunchLabel/LaunchAsync sur le chemin de
    /// lancement générique (même chemin qu'une production normale) sans rien dupliquer.
    /// </summary>
    /// <summary>
    /// Vrai si le type de release est de catégorie "Vidéo" (ReleaseType.Name contient
    /// "video"). Dans ce cas, le bouton principal devient "▶ Play Video" et au clic
    /// la vidéo est extraite du zip et jouée dans le player intégré inline.
    /// On détecte par le nom du TYPE (pas le Supertype) car Demozoo utilise par exemple
    /// "Video" comme nom de type explicite, et l'utilisateur pourra en ajouter d'autres.
    /// Comparaison sur le nom SANS accents (cf. <see cref="RemoveDiacritics"/>) : certains
    /// types sont enregistrés localement avec un nom déjà en français ("Vidéo", accentué),
    /// et "vidéo".Contains("video") est FAUX en OrdinalIgnoreCase (le "é" ne correspond
    /// jamais au "e" — bug constaté : bouton "Lancer"/lancement émulateur au lieu du
    /// player vidéo intégré sur une release Amstrad CPC de type "Vidéo").
    /// </summary>
    public bool IsVideoRelease
    {
        get
        {
            var name = RemoveDiacritics(Detail?.Release?.ReleaseType?.Name ?? "");
            return name.Contains("video",       StringComparison.OrdinalIgnoreCase)
                || name.Contains("performance", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var normalized = text.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    public bool IsExecutableMusicOrGraphics =>
        (Detail?.Release?.Supertype == "music" || Detail?.Release?.Supertype == "graphics")
        && ((Detail?.Release?.ReleaseType?.Name?.Contains("executable", StringComparison.OrdinalIgnoreCase) ?? false)
            || GraphicsHasNoDisplayableFile);

    /// <summary>
    /// Filet de sécurité complémentaire au test ReleaseType.Name.Contains("executable")
    /// ci-dessus (2026-07-28, retour utilisateur : release "Bat-Moule" #141160, Amstrad CPC,
    /// catégorie "Graphics" sur Demozoo mais livrée en .dsk bootable — "on a déjà vu les
    /// executables graphics pour windows. mais ça existe aussi pour amstrad cpc [...] ne se
    /// lance pas, malgré la présence de fichier .dsk dans le zip"). Demozoo ne tague pas
    /// systématiquement ces productions "Executable Graphics" — certaines restent
    /// simplement "Graphics" côté ReleaseType, même quand un crédit "Code" existe et que le
    /// seul fichier livré est un .dsk/.d64/.adf/etc. Le test ci-dessous ne dépend d'AUCUNE
    /// liste de plateformes ni d'extensions à maintenir : il réutilise directement
    /// ImageExtensions.IsDisplayable (GraphicsViewerViewModel) — si aucun des fichiers DAT
    /// connus pour cette release n'est un format d'image affichable, GraphicsViewerViewModel
    /// ne pourrait de toute façon rien montrer ; c'est donc structurellement un programme à
    /// lancer via émulateur, pas une image à afficher.
    /// </summary>
    private bool GraphicsHasNoDisplayableFile =>
        Detail != null
        && Detail.Release?.Supertype == "graphics"
        && Detail.DatFiles.Any()
        && !Detail.DatFiles.SelectMany(e => e.Roms).Any(r => ImageExtensions.IsDisplayable(r.Name));

    /// <summary>
    /// Vrai si la zone lecteur/oscilloscope doit être affichée : soit la
    /// release elle-même est de type musique, soit un soundtrack a été lancé
    /// depuis l'onglet Media (cas d'une Demo/Intro avec des .sndh/.sid
    /// associés — Supertype reste "demo", pas "music", mais le lecteur doit
    /// quand même apparaître une fois qu'on a cliqué "Play" sur un morceau).
    /// </summary>
    public bool ShowSoundtrackArea => IsMusic
        || (IsExecutableMusicOrGraphics && Detail?.Release?.Supertype == "music")
        || SoundtrackPlayer != null;

    /// <summary>
    /// Vrai si le CodeSourceViewer doit occuper la "Zone visu" partagée sous les onglets.
    /// Deux conditions cumulatives, demande utilisateur explicite :
    /// - SelectedTabIndex == CodeSourcesTabIndex : contrairement à Graphics (toujours visible
    ///   dès l'ouverture de la release), Code Sources ne doit apparaître QUE pendant que
    ///   l'onglet "Code Sources" est effectivement affiché — pas avant, pas après être
    ///   retourné sur un autre onglet.
    /// - !ShowSoundtrackArea && GraphicsViewer == null : une release Music/Graphics peut EN
    ///   PLUS avoir un DatEntry "Sources Code" ; le contenu principal de la release
    ///   (Tracker/Graphics) reste prioritaire dans cette zone partagée si jamais les deux se
    ///   demandaient l'affichage en même temps.
    /// </summary>
    public bool ShowCodeSourceArea =>
        CodeSourceViewer != null
        && SelectedTabIndex == CodeSourcesTabIndex
        && !ShowSoundtrackArea
        && GraphicsViewer == null;

    partial void OnGraphicsViewerChanged(DemoBase.App.ViewModels.GraphicsViewerViewModel? value)
        => OnPropertyChanged(nameof(ShowCodeSourceArea));

    partial void OnCodeSourceViewerChanged(DemoBase.App.ViewModels.CodeSourceViewerViewModel? value)
        => OnPropertyChanged(nameof(ShowCodeSourceArea));

    public string LaunchLabel => IsMusic
        ? DemoBase.App.Services.LocalizationService.Get("RD_Play")
        : IsGraphics
            ? DemoBase.App.Services.LocalizationService.Get("RD_View")
            : IsVideoRelease
                ? DemoBase.App.Services.LocalizationService.Get("RD_PlayVideo")
                : NeedsAdHocDownload
                    ? DemoBase.App.Services.LocalizationService.Get("RD_DownloadAndLaunch")
                    : DemoBase.App.Services.LocalizationService.Get("RD_Launch");

    [ObservableProperty] private DemoBase.App.ViewModels.GraphicsViewerViewModel? _graphicsViewer;
    [ObservableProperty] private DemoBase.App.ViewModels.CodeSourceViewerViewModel? _codeSourceViewer;
    [ObservableProperty] private bool _isMusicFavorite;
    [ObservableProperty] private bool _isGraphicFavorite;

    /// <summary>
    /// Vrai s'il existe au moins une référence de fichier pour cette release (lien de
    /// téléchargement ou entrée DAT) — sert à désactiver le bouton Play/Launch/View
    /// quand il n'y a littéralement rien à jouer/lancer/afficher (demande utilisateur).
    /// </summary>
    public bool HasAnyFile =>
        Detail != null && (Detail.Links.Any() || Detail.DatFiles.Any(d => !d.IsCodeSourceEntry));

    /// <summary>
    /// Vrai si cette release n'a encore AUCUN fichier DAT (pas encore couverte par le
    /// DAT mensuel) mais possède un lien de téléchargement direct marqué IsMainFile par
    /// Demozoo — jouable uniquement via téléchargement ad-hoc au lancement, pas encore
    /// via le DAT/Mega curaté et vérifié par CRC. Pilote le badge "Fichier externe (pas
    /// encore de DAT)" et la confirmation dans LaunchAsync (2026-07-25, cf.
    /// RESUME_PROJET.md).
    /// </summary>
    public bool IsExternalOnlyRelease =>
        Detail != null
        && !Detail.DatFiles.Any(d => !d.IsCodeSourceEntry)
        && Detail.Links.Where(l => !l.IsVideo)
            .Any(l => l.IsMainFile && !string.IsNullOrEmpty(l.EffectiveDownloadUrl));

    /// <summary>
    /// Vrai si le clic sur le bouton de lancement va d'abord devoir télécharger le
    /// fichier (release externe pas encore couverte par un DAT, ET pas encore mise en
    /// cache localement) — pilote le libellé du bouton ("Télécharger et lancer" au lieu
    /// de "Lancer", 2026-07-25, demande utilisateur : éviter la surprise d'un clic sur
    /// "Lancer" qui déclenche en réalité un téléchargement). Reflète exactement la même
    /// logique que le bloc "téléchargement ad-hoc" de LaunchAsync plus bas (même lien
    /// candidat, même vérification LocalFilePath/File.Exists) — DOIT rester synchronisé
    /// avec ce bloc si sa logique de sélection change.
    /// Limite connue : ne se remet pas à jour tout seul juste après un téléchargement
    /// réussi dans la même session (Detail n'est pas rechargé après LaunchAsync) — se
    /// corrige de lui-même à la prochaine ouverture de la fiche. Sans impact sur le
    /// lancement lui-même : un second clic relance directement le fichier déjà en cache.
    /// </summary>
    public bool NeedsAdHocDownload
    {
        get
        {
            if (!IsExternalOnlyRelease) return false;
            var candidate = Detail?.Links.Where(l => !l.IsVideo)
                .FirstOrDefault(l => l.IsMainFile && !string.IsNullOrEmpty(l.EffectiveDownloadUrl));
            if (candidate == null) return false;
            var alreadyLocal = !string.IsNullOrEmpty(candidate.LocalFilePath)
                && System.IO.File.Exists(candidate.LocalFilePath);
            return !alreadyLocal;
        }
    }

    // 2026-07-27, demande utilisateur : lien cliquable en mode debug, à côté du bouton
    // Lancer/Play, affichant l'ID Demozoo de la release et ouvrant sa fiche demozoo.org
    // dans le navigateur — pratique pour vérifier rapidement les données source (liens,
    // link_class/link_parameter…) pendant les sessions de diagnostic. Gardé DERRIÈRE
    // DebugHelper.IsDebugMode côté XAML (même pattern que le bouton "Télécharger" debug
    // existant dans l'onglet Captures) : invisible en Release.
    public string? DemozooIdDebugLabel =>
        Detail?.Release?.DemozooId is int id ? $"🔧 Demozoo #{id}" : null;

    public string? DemozooDebugUrl =>
        Detail?.Release?.DemozooId is int id ? $"https://demozoo.org/productions/{id}/" : null;

    [RelayCommand]
    private void OpenDemozooDebugPage()
    {
        var url = DemozooDebugUrl;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// Vrai si une archive déjà locale associée à cette release (lien de téléchargement
    /// ou ROM référencée par le catalogue DAT) contient un fichier "lançable" via
    /// émulateur (disquette, exécutable...) en plus de son contenu music/graphics
    /// habituel — fréquent sur C64 (demande utilisateur). Détection sur fichiers déjà
    /// présents localement uniquement, pas de téléchargement déclenché juste pour ça.
    /// N'a de sens que pour les releases qui restent Play/View (IsMusic/IsGraphics) —
    /// pour les types "executable", IsMusic/IsGraphics est déjà faux et le bouton
    /// principal devient "Lancer" directement, ce second bouton serait redondant.
    /// </summary>
    [ObservableProperty] private bool _hasLaunchableCompanion;

    /// <summary>
    /// Vrai si une archive déjà locale associée à cette release contient un fichier vidéo
    /// (extension dans <see cref="VideoFileExtensions"/>) en plus de son contenu habituel —
    /// fréquent pour les captures de démos qui ne tournent pas bien en émulation (demande
    /// utilisateur). N'a de sens que si la release n'est PAS déjà de type Vidéo
    /// (IsVideoRelease) — dans ce cas le bouton principal EST déjà "Lire la vidéo", ce
    /// second bouton serait redondant. Détection sur fichiers déjà locaux uniquement,
    /// aucun téléchargement déclenché juste pour ça (même philosophie que
    /// HasLaunchableCompanion ci-dessus).
    /// </summary>
    [ObservableProperty] private bool _hasVideoCompanion;

    // ── Override du profil de lancement ─────────────────────────────────────
    // 2026-07-25 : n'est plus limité au mode debug (retour utilisateur : le widget
    // était invisible en build Release, seul moyen pourtant de choisir la plateforme
    // sur une release multi-plateforme). Opère maintenant sur le FICHIER sélectionné
    // (SelectedDatEntry) quand il y en a un, et sur la release entière sinon — cf.
    // RefreshFileProfileOverrideDisplayAsync/ApplyProfileOverrideAsync ci-dessous.
    [ObservableProperty] private ObservableCollection<EmulatorConfig> _availableProfilesForOverride = [];
    [ObservableProperty] private EmulatorConfig? _selectedOverrideProfile;

    /// <summary>Libellé du widget "Profil" — précise s'il porte sur le fichier
    /// sélectionné ou sur la release entière (aucun DatEntry sélectionné).</summary>
    public string ProfileOverrideLabel =>
        SelectedDatEntry != null ? "Profil (fichier sélectionné) :" : "Profil (release) :";

    /// <summary>Vrai si un override (fichier ou release, selon le contexte courant) est
    /// actif — pilote la visibilité du bouton Réinitialiser et du libellé "override actif".</summary>
    public bool HasActiveProfileOverride => SelectedOverrideProfile != null;

    partial void OnSelectedOverrideProfileChanged(EmulatorConfig? value)
        => OnPropertyChanged(nameof(HasActiveProfileOverride));

    public ReleaseDetailViewModel(
        IReleaseService releaseService,
        IEmulatorService emulatorService,
        IMediaService mediaService,
        INavigationService navigation,
        TrackerPlayer.Core.Interfaces.ITrackerService? trackerService = null,
        DemoBase.Data.PreferencesService? prefsService = null,
        DemoBase.Data.FavoriteSoundtrackService? favService = null,
        DemoBase.Data.FavoriteGraphicService? favGraphicService = null,
        DemoBase.App.Services.LocalVideoCaptureService? videoCaptureService = null,
        DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService? releaseBuilderService = null,
        DemoBase.Data.DownloadAttemptService? downloadAttempts = null)
    {
        _releaseService       = releaseService;
        _emulatorService      = emulatorService;
        _mediaService         = mediaService;
        _navigation           = navigation;
        _trackerService       = trackerService;
        _prefsService         = prefsService;
        _favService           = favService;
        _favGraphicService    = favGraphicService;
        _videoCaptureService  = videoCaptureService;
        _releaseBuilderService = releaseBuilderService;
        _downloadAttempts     = downloadAttempts;

        // Recharger les profils d'émulateur quand on revient depuis la page Émulateurs
        // (un profil a pu être ajouté/modifié pendant qu'on y était)
        _navigation.Navigated += OnNavigated;
    }

    private void OnNavigated(object? sender, NavigationEventArgs e)
    {
        if (_lastLoadedReleaseId < 0) return;

        // Recharger les profils quand on quitte les émulateurs pour revenir ailleurs
        // _previousViewModelType mémorise la vue précédente
        bool wasOnEmulators = _previousNavType == typeof(DemoBase.App.ViewModels.Emulators.EmulatorSettingsViewModel);
        _previousNavType = e.ViewModelType;

        if (!wasOnEmulators) return;

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            if (_lastLoadedReleaseId > 0)
                await LoadAvailableProfilesAsync(_lastLoadedReleaseId);
        });
    }

    private Type? _previousNavType;

    public async Task LoadAsync(int releaseId)
    {
        // Guard 1 : appel concurrent — ignorer si un chargement est déjà en cours
        if (_isLoadingNow) return;

        // Guard 2 : debounce — ignorer si le même releaseId a été chargé il y a moins de 300ms
        // (évite les rechargements en cascade dus aux PropertyChanged en chaîne)
        var now = DateTime.UtcNow;
        if (releaseId == _lastLoadedReleaseId && (now - _lastLoadTime).TotalMilliseconds < 300)
            return;

        _isLoadingNow = true;
        _lastLoadedReleaseId = releaseId;
        _lastLoadTime = now;
        try
        {

        // Dispose explicite plutôt que de compter sur l'événement Unloaded de la vue
        // (SoundtrackPlayerView.Unloaded → _vm.Dispose()) : ce dernier dépend du
        // détachement effectif de l'arbre visuel WPF, ce qui n'est pas garanti de se
        // produire de façon synchrone/fiable, en particulier pendant la fermeture de
        // l'application — d'où des process externes (zxtune123.exe/uade123.exe) ou
        // des périphériques audio (WaveOutEvent) jamais relâchés, qui peuvent
        // maintenir le process DemoBase.App.exe en vie et verrouiller ses DLL.
        SoundtrackPlayer?.Vm.Dispose();
        SoundtrackPlayer = null;
        GraphicsViewer?.Dispose();
        GraphicsViewer   = null;
        CodeSourceViewer?.Dispose();
        CodeSourceViewer = null;
        HasVideos        = false;
        VideoPlayer?.Dispose();
        VideoPlayer = null;
        LocalVideos = [];
        InlineVideoPlayer?.Dispose();
        InlineVideoPlayer = null;
        HasLaunchableCompanion = false;
        _companionRomPath      = null;
        _companionLink         = null;
        HasVideoCompanion      = false;

        // Forcer un passage GC complet en arrière-plan pour libérer les BitmapSource,
        // frames GIF, buffers audio et géométries WPF de la release précédente.
        // Gen2 + finalizers = libération complète (Gen0 seul était insuffisant).
        // blocking:false + Task.Run = ne gèle pas le thread UI.
        // CORRECTIF : le code passait blocking:true (deux fois), à l'exact opposé
        // du commentaire ci-dessus — un GC.Collect bloquant suspend TOUS les
        // threads managés du process, y compris le thread UI, même quand il est
        // déclenché depuis un Task.Run (Task.Run ne l'isole pas). À chaque
        // navigation vers une release, ce faux "non-bloquant" gelait donc
        // l'interface le temps du passage GC complet. Repassé à blocking:false
        // pour correspondre à l'intention documentée.
        _ = Task.Run(() =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        });

        IsLoading = true;
        try
        {
            using (PerfLogger.Begin($"ReleaseDetail.GetDetailAsync (id={releaseId})"))
                Detail = await _releaseService.GetDetailAsync(releaseId);
            await LoadAvailableProfilesAsync(releaseId);
            await RefreshWinUAEStartupChoiceAsync();
            if (IsMusic || IsGraphics)
            {
                using (PerfLogger.Begin("ReleaseDetail.DetectLaunchableCompanion"))
                    await DetectLaunchableCompanionAsync();
            }
            if (!IsVideoRelease)
            {
                using (PerfLogger.Begin("ReleaseDetail.DetectVideoCompanion"))
                    await DetectVideoCompanionAsync();
            }

            await AutoSelectDatEntryAsync();
            if (IsGraphics)
                _ = ShowGraphicsAsync(releaseId);

            // Charger les tentatives de téléchargement échouées pour les liens de cette release
            await LoadDownloadMismatchesAsync();

            // Code Sources : PAS de chargement auto ici (contrairement à Graphics) — demande
            // utilisateur explicite, le viewer ne doit apparaître que si l'onglet est cliqué
            // (voir OnSelectedTabIndexChanged). On réinitialise juste le garde anti-relance
            // pour la nouvelle release.
            _codeSourceLoadedForReleaseId = null;
        }
        finally { IsLoading = false; }

        } // fermeture du try guard
        finally { _isLoadingNow = false; }
    }

    /// <summary>Sélectionne automatiquement le DAT le plus pertinent pour le lancement/affichage.</summary>
    private async Task LoadDownloadMismatchesAsync()
    {
        DownloadMismatches.Clear();
        DatFilesWithMismatch.Clear();
        CodeSourcesWithMismatch.Clear();

        if (Detail?.DatFiles == null) return;

        // Construire les wrappers DatEntry
        var wrappers = Detail.DatFiles
            .Select(d => new DatEntryWithMismatch(d))
            .ToList();

        // 2026-07-30, retour utilisateur : "j'ai pas de bouton réessayer" — l'ancienne version
        // cherchait chaque tentative connue via "link.Url" (le champ brut, non résolu), qui ne
        // correspond JAMAIS à l'URL réellement résolue et utilisée comme clé de cache par
        // ReleaseBuilderService pour les link_class dont l'URL réelle vient de LinkParameter
        // (ex. scene.org, Modland) — le panneau restait donc vide même avec un mismatch bel et
        // bien enregistré. DownloadAttempt stocke déjà le DemozooId : on interroge directement
        // par release au lieu de deviner l'URL.
        if (_downloadAttempts != null && Detail.Release.DemozooId.HasValue)
        {
            var attempts = await _downloadAttempts.GetForDemozooIdAsync(Detail.Release.DemozooId.Value);
            foreach (var attempt in attempts)
            {
                DownloadMismatches.Add(attempt);

                // 2026-07-30, retour utilisateur ("Starstruck", plusieurs versions/sets pour la
                // même release — Original/Final version/Party version/Alt1) : "je pense que
                // demobase s'emmêle un peu" — quand plusieurs DatEntry (sets) de la même release
                // contiennent chacun un rom du MÊME nom (ex. "Starstruck.dat" présent à la fois
                // dans "Party version" ET dans un autre set, avec des tailles DAT différentes),
                // l'association ne se faisait QUE par nom de fichier et prenait le premier
                // wrapper trouvé — potentiellement le MAUVAIS set. Résultat : un mismatch dont
                // la taille DAT affichée ne correspondait même pas à la taille DAT listée pour ce
                // rom dans CE set (confirmé : le panneau affichait une taille DAT différente de
                // celle indiquée dans la liste des fichiers du même groupe). On associe
                // maintenant le rom dont la taille DAT ET le CRC32 DAT correspondent EXACTEMENT
                // à attempt.SizeInDat/Crc32InDat — PAS le nom (2026-07-30, retour utilisateur :
                // "il faut vérifier la taille et le crc32 pour le match, le nom peut varier car
                // j'ai pu les renommer à l'intérieur du dat" — le nom d'un rom dans le DAT n'est
                // pas un identifiant fiable, seul le couple taille+CRC32 identifie right le
                // contenu réellement attendu, quel que soit le nom choisi pour l'entrée). Repli
                // sur taille+nom seulement si le CRC est absent d'un des deux côtés (certains
                // DAT n'enregistrent pas toujours de CRC32). Si rien ne correspond exactement
                // (DAT mis à jour depuis, ou mismatch qui ne concerne en réalité aucun set
                // affiché), on n'attache PLUS le mismatch à un set au hasard — mieux vaut ne rien
                // afficher en ligne qu'afficher une taille DAT sans rapport avec ce groupe. Il
                // reste visible dans le panneau global "Fichiers incompatibles" (DownloadMismatches)
                // pour nettoyage via "Réessayer" de toute façon.
                var target = wrappers.FirstOrDefault(w =>
                    w.Entry.Roms.Any(r => r.Size == attempt.SizeInDat
                                           && !string.IsNullOrEmpty(r.Crc32)
                                           && !string.IsNullOrEmpty(attempt.Crc32InDat)
                                           && string.Equals(r.Crc32, attempt.Crc32InDat, StringComparison.OrdinalIgnoreCase)))
                    ?? wrappers.FirstOrDefault(w =>
                    w.Entry.Roms.Any(r => (string.IsNullOrEmpty(r.Crc32) || string.IsNullOrEmpty(attempt.Crc32InDat))
                                           && r.Size == attempt.SizeInDat
                                           && string.Equals(r.Name, attempt.FileName, StringComparison.OrdinalIgnoreCase)));
                if (target != null)
                    target.Mismatch = attempt;
            }
        }

        // Répartition Fichiers / Code Sources : une entrée dont le SourceFile (chemin du DAT
        // source, ex. "Ressources\Sources Codes\....dat") contient "Sources Code" est déplacée
        // dans l'onglet "Code Sources" au lieu de "Fichiers" — demande utilisateur, pour séparer
        // visuellement les archives de code source des fichiers de jeu/démo habituels.
        foreach (var w in wrappers)
        {
            if (w.Entry.IsCodeSourceEntry)
                CodeSourcesWithMismatch.Add(w);
            else
                DatFilesWithMismatch.Add(w);
        }

        OnPropertyChanged(nameof(HasDownloadMismatches));
        OnPropertyChanged(nameof(HasCodeSourcesFiles));
    }

    [RelayCommand]
    private async Task ClearDownloadMismatches()
    {
        if (_downloadAttempts == null) return;
        // 2026-07-30, retour utilisateur : "si je clique sur Réessayer il ne se passe rien".
        // Deux bugs cumulés :
        //  1) DownloadMismatches.Clear() ne notifie jamais HasDownloadMismatches (propriété
        //     calculée, pas la collection elle-même) → le panneau ne se refermait/rafraîchissait
        //     pas visuellement tant qu'aucun autre binding ne forçait une réévaluation.
        //  2) Le bouton ne faisait QUE nettoyer le cache (marquer Success en base) sans jamais
        //     relancer de téléchargement — l'utilisateur devait deviner qu'il fallait ensuite
        //     recliquer sur "Lancer" séparément. Le libellé/tooltip ("forcer un nouveau
        //     téléchargement") promettait une action immédiate qui n'avait jamais lieu.
        // Correctif : on nettoie le cache ET on notifie ET on relance immédiatement une
        // tentative de téléchargement/reconstruction (même logique que "Lancer").
        foreach (var m in DownloadMismatches.ToList())
            await _downloadAttempts.SaveAsync(m with { Status = DemoBase.Data.DownloadAttemptStatus.Success });
        DownloadMismatches.Clear();
        OnPropertyChanged(nameof(HasDownloadMismatches));

        DemoBase.App.Controls.StatusScrollerControl.Post("Nouvelle tentative de téléchargement…");
        var ok = await EnsureReleaseFilesAvailableAsync(force: true);
        if (ok && string.IsNullOrEmpty(BuildErrorMessage))
            DemoBase.App.Controls.StatusScrollerControl.Post("Téléchargement terminé.");
    }

    private async Task AutoSelectDatEntryAsync()
    {
        if (Detail?.DatFiles == null) return;
        // Exclure les DatEntry "Code Sources" (SourceFile contient "Sources Code") : ce ne
        // sont pas des fichiers jouables/lançables, ils ne doivent jamais être sélectionnés
        // automatiquement pour le bouton "Lancer/Lire" — voir DatEntry.IsCodeSourceEntry.
        var dats = Detail.DatFiles.Where(d => !d.IsCodeSourceEntry).ToList();
        if (dats.Count == 0) return;

        // Fichier mémorisé comme préféré pour cette release (choix explicite déjà
        // fait antérieurement, via "Utiliser" ou la fenêtre de choix de fichier) —
        // prioritaire sur toute heuristique, et considéré comme confirmé : pas
        // besoin de reposer la question au clic sur "Lancer" (2026-07-25).
        if (Detail.Release.DemozooId != null)
        {
            var preferredPath = await _releaseService.GetPreferredFileAsync(Detail.Release.DemozooId.Value);
            if (preferredPath != null)
            {
                var preferred = dats.FirstOrDefault(d =>
                    string.Equals(d.RomPath, preferredPath, StringComparison.OrdinalIgnoreCase));
                if (preferred != null)
                {
                    SelectedDatEntry = preferred;
                    _fileSelectionConfirmed = true;
                    return;
                }
            }
        }
        _fileSelectionConfirmed = false;

        // Pour les releases Graphics : préférer le DAT avec des images viewables
        if (IsGraphics)
        {
            var imageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".iff", ".lbm",
                  ".pcx", ".tga", ".prg", ".koa", ".hires" };
            var withImages = dats.FirstOrDefault(d =>
                d.Roms.Any(r => imageExts.Contains(System.IO.Path.GetExtension(r.Name).ToLowerInvariant())
                             && d.Roms.Count > 1)); // préférer les ZIPs avec plusieurs fichiers
            if (withImages != null) { SelectedDatEntry = withImages; return; }
        }

        // Règle générale : premier DAT non-vidéo-only
        foreach (var dat in dats)
        {
            if (!IsVideoOnlyDat(dat)) { SelectedDatEntry = dat; return; }
        }
        SelectedDatEntry = dats[0];
    }

    private static bool IsVideoOnlyDat(DemoBase.Core.Models.DatEntry dat)
    {
        var ignoreExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".nfo", ".diz", ".md", ".rtf", ".doc", ".pdf", ".jpg", ".jpeg", ".png", ".gif" };
        var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".avi", ".mp4", ".mkv", ".mov", ".mpg", ".mpeg", ".wmv", ".m4v", ".divx", ".xvid" };
        var meaningful = dat.Roms
            .Where(r => !ignoreExts.Contains(System.IO.Path.GetExtension(r.Name).ToLowerInvariant()))
            .ToList();
        return meaningful.Any() && meaningful.All(r =>
            videoExts.Contains(System.IO.Path.GetExtension(r.Name).ToLowerInvariant()));
    }

    /// <summary>
    /// Cherche un fichier vidéo (cf. <see cref="VideoFileExtensions"/>) dans les archives
    /// déjà locales associées à cette release — même logique/mêmes sources que
    /// <see cref="DetectLaunchableCompanionAsync"/> (DatFiles + Links), mais ne mémorise
    /// pas QUEL fichier a matché : PlayVideoCompanion() réutilise directement
    /// PlayVideoInlineAsync(), qui rescane déjà toutes les DatEntries de la release.
    /// </summary>
    private async Task DetectVideoCompanionAsync()
    {
        if (Detail == null) return;

        bool found = false;

        await Task.Run(async () =>
        {
            if (_prefsService != null && Detail!.DatFiles.Any())
            {
                var prefs = await _prefsService.LoadAllAsync();
                foreach (var dat in Detail!.DatFiles.Where(d => !d.IsCodeSourceEntry))
                {
                    var p = System.IO.Path.Combine(prefs.ResolvedPathReleases, dat.RomPath);
                    if (ZipContainsVideoCached(p)) { found = true; return; }
                }
            }

            foreach (var link in Detail!.Links)
            {
                if (string.IsNullOrEmpty(link.LocalFilePath) || !System.IO.File.Exists(link.LocalFilePath))
                    continue;
                if (ZipContainsVideoCached(link.LocalFilePath)) { found = true; return; }
            }
        });

        HasVideoCompanion = found;
    }

    private static bool ZipContainsVideoCached(string path)
    {
        if (_videoCompanionCache.TryGetValue(path, out var cached))
            return cached;
        var result = ZipContainsVideo(path);
        _videoCompanionCache[path] = result;
        return result;
    }

    private static bool ZipContainsVideo(string path)
    {
        if (!System.IO.File.Exists(path)) return false;

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (VideoFileExtensions.Contains(ext)) return true;
        if (ext != ".zip") return false;

        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            return zip.Entries.Any(e =>
                VideoFileExtensions.Contains(System.IO.Path.GetExtension(e.Name).ToLowerInvariant()));
        }
        catch { return false; }
    }

    [RelayCommand]
    private async Task PlayVideoCompanion() => await PlayVideoInlineAsync();

    // DAT sélectionné pour le lancement (null = premier disponible)
    private DemoBase.Core.Models.DatEntry? _selectedDatEntry;
    public DemoBase.Core.Models.DatEntry? SelectedDatEntry
    {
        get => _selectedDatEntry;
        set
        {
            if (SetProperty(ref _selectedDatEntry, value))
            {
                OnPropertyChanged(nameof(SelectedDatRomPath));
                OnPropertyChanged(nameof(ProfileOverrideLabel));
                // 2026-07-25 : le widget "Profil" reflète maintenant l'override du
                // FICHIER sélectionné (et non plus uniquement celui de la release) —
                // recharger l'affichage à chaque changement de sélection.
                _ = RefreshFileProfileOverrideDisplayAsync();
            }
        }
    }

    /// <summary>
    /// Vrai si SelectedDatEntry reflète un choix EXPLICITE de l'utilisateur (bouton
    /// "Utiliser", fichier mémorisé comme préféré pour cette release, ou choix fait
    /// via la fenêtre de sélection de fichier au clic sur "Lancer") plutôt qu'une
    /// simple estimation par défaut (AutoSelectDatEntryAsync, premier fichier
    /// non-vidéo) — cf. LaunchAsync : ne propose la fenêtre de choix de fichier que
    /// lorsque ce drapeau est faux (2026-07-25, retour utilisateur : releases
    /// multi-fichier où le "bon" fichier n'est pas forcément celui deviné).
    /// </summary>
    private bool _fileSelectionConfirmed;

    /// <summary>RomPath du DAT sélectionné — utilisé dans les DataTriggers XAML pour
    /// éviter un MultiBinding (EqualityConverter n'est qu'IValueConverter).</summary>
    public string? SelectedDatRomPath => _selectedDatEntry?.RomPath;

    [RelayCommand]
    private async Task SelectDatEntryAsync(DemoBase.Core.Models.DatEntry dat)
    {
        // Filet de sécurité : le bouton "Utiliser" n'est de toute façon plus exposé dans
        // l'onglet Code Sources, mais on refuse quand même ici toute sélection d'une entrée
        // "Sources Code" pour le lancement — voir DatEntry.IsCodeSourceEntry.
        if (dat.IsCodeSourceEntry) return;
        SelectedDatEntry = dat;
        _fileSelectionConfirmed = true;
        // Choix explicite de l'utilisateur → mémorisé durablement, comme pour le
        // profil : ce fichier redeviendra la sélection par défaut à chaque
        // réouverture de cette release (cf. AutoSelectDatEntryAsync).
        if (Detail?.Release?.DemozooId != null)
            await _releaseService.SetPreferredFileAsync(Detail.Release.DemozooId.Value, dat.RomPath);
    }

    // Extensions qui trahissent un fichier "à lancer via émulateur" (disquette,
    // exécutable...) plutôt qu'un asset music/graphics streamable classique. Volontairement
    // sans recoupement avec les formats tracker (.sid/.mod/...) ou image (.png/.iff/...).
    private static readonly string[] LaunchableCompanionExtensions =
        [".exe", ".com", ".prg", ".d64", ".d71", ".d81", ".t64", ".tap", ".crt",
         ".adf", ".dsk", ".st", ".msa",
         ".xex"];  // Atari 8-bit executable

    // Source exacte ayant déclenché HasLaunchableCompanion — mémorisée pour que
    // LaunchCompanion() lance précisément CE fichier/zip plutôt que de re-résoudre via
    // ReleaseService.LaunchAsync, qui privilégie le chemin DAT même quand celui-ci pointe
    // directement sur l'asset musique/graphics joué (le .sid, pas le .prg compagnon) —
    // ce qui lançait le mauvais fichier (bug constaté : .sid lancé au lieu du .prg).
    private string?      _companionRomPath;
    private ReleaseLink? _companionLink;

    /// <summary>
    /// Cherche un fichier "lançable" (cf. <see cref="LaunchableCompanionExtensions"/>) dans les
    /// archives déjà locales associées à cette release — celles référencées par le catalogue
    /// DAT (la source utilisée par Play/View) ET les liens de téléchargement classiques. Ne
    /// déclenche aucun téléchargement (demande utilisateur : fichiers déjà locaux uniquement).
    /// </summary>
    private async Task DetectLaunchableCompanionAsync()
    {
        if (Detail == null) return;

        // Pour les releases Music (y compris Executable Music), les .exe sont joués
        // via ExeMusicPlayer (PlayMusicReleaseAsync) — pas besoin du bouton "Lancer".
        if (IsMusic || (IsExecutableMusicOrGraphics && Detail?.Release?.Supertype == "music")) return;

        // Exécuté sur un thread pool pour ne pas bloquer l'UI
        // (ouvre des ZIPs réseau — peut prendre 300ms+)
        string? foundRomPath = null;
        DemoBase.Core.Models.ReleaseLink? foundLink = null;

        await Task.Run(async () =>
        {
            if (_prefsService != null && Detail!.DatFiles.Any())
            {
                var prefs = await _prefsService.LoadAllAsync();
                foreach (var dat in Detail!.DatFiles.Where(d => !d.IsCodeSourceEntry))
                {
                    var p = System.IO.Path.Combine(prefs.ResolvedPathReleases, dat.RomPath);
                    if (ZipContainsLaunchableCached(p))
                    {
                        foundRomPath = p;
                        return;
                    }
                }
            }

            foreach (var link in Detail!.Links)
            {
                if (string.IsNullOrEmpty(link.LocalFilePath) || !System.IO.File.Exists(link.LocalFilePath))
                    continue;
                if (ZipContainsLaunchableCached(link.LocalFilePath))
                {
                    foundLink = link;
                    return;
                }
            }
        });

        if (foundRomPath != null)
        {
            _companionRomPath      = foundRomPath;
            HasLaunchableCompanion = true;
        }
        else if (foundLink != null)
        {
            _companionLink         = foundLink;
            HasLaunchableCompanion = true;
        }
    }

    private static bool ZipContainsLaunchableCached(string path)
    {
        if (_launchableCache.TryGetValue(path, out var cached))
            return cached;
        var result = ZipContainsLaunchable(path);
        _launchableCache[path] = result;
        return result;
    }

    private static bool ZipContainsLaunchable(string path)
    {
        if (!System.IO.File.Exists(path)) return false;

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (LaunchableCompanionExtensions.Contains(ext)) return true;
        if (ext != ".zip") return false;

        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            return zip.Entries.Any(e =>
                LaunchableCompanionExtensions.Contains(System.IO.Path.GetExtension(e.Name).ToLowerInvariant()));
        }
        catch { return false; }
    }

    [RelayCommand]
    private async Task LaunchCompanion()
    {
        if (Detail?.Release == null) return;
        var config = Detail.DefaultEmulatorConfig;
        if (config == null)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Aucun émulateur configuré pour cette plateforme.", isWarning: true);
            return;
        }

        // Lance directement la source détectée — le launcher de l'émulateur cible fait
        // sa propre extraction/sélection si c'est un zip (priorité .prg sur .sid pour
        // VICE, etc.), donc pas besoin de re-choisir un fichier ici.
        if (_companionRomPath != null)
            await _emulatorService.LaunchReleaseAsync(_companionRomPath, Detail.Release, config);
        else if (_companionLink != null)
        {
            _companionLink.Release = Detail.Release;
            await _emulatorService.LaunchReleaseAsync(_companionLink, config);
        }
        else
        {
            // Sécurité : ne devrait pas arriver puisque le bouton n'est visible que si une
            // source a été détectée — au cas où, on retombe sur le chemin générique.
            await _releaseService.LaunchAsync(Detail.Release.Id, config.Id);
        }
    }

    /// <summary>
    /// Charge les profils disponibles pour CETTE release (toutes plateformes confondues —
    /// permet de remplacer le profil par défaut, par fichier ou pour toute la release, via
    /// DatEntryProfileOverrideService/ReleaseProfileOverrideService). Présélectionne
    /// l'override actif pour le fichier/la release courant(e) s'il existe.
    /// </summary>
    private async Task LoadAvailableProfilesAsync(int releaseId)
    {
        var profiles = await _releaseService.GetAvailableProfilesForReleaseAsync(releaseId);
        AvailableProfilesForOverride = new ObservableCollection<EmulatorConfig>(profiles);
        await RefreshFileProfileOverrideDisplayAsync();
    }

    /// <summary>
    /// Recharge SelectedOverrideProfile depuis la source qui s'applique au contexte
    /// courant : override du FICHIER sélectionné (SelectedDatEntry) s'il y en a un,
    /// sinon override de la RELEASE entière. Appelée à chaque changement de
    /// SelectedDatEntry, après chargement des profils disponibles, et après
    /// Appliquer/Réinitialiser.
    /// </summary>
    private async Task RefreshFileProfileOverrideDisplayAsync()
    {
        if (Detail?.Release == null) { SelectedOverrideProfile = null; return; }

        if (SelectedDatEntry != null && Detail.Release.DemozooId != null)
        {
            var cfgId = await _releaseService.GetDatEntryProfileOverrideAsync(
                Detail.Release.DemozooId.Value, SelectedDatEntry.RomPath);
            SelectedOverrideProfile = cfgId.HasValue
                ? AvailableProfilesForOverride.FirstOrDefault(c => c.Id == cfgId.Value)
                : null;
        }
        else
        {
            SelectedOverrideProfile = Detail.IsProfileOverridden
                ? AvailableProfilesForOverride.FirstOrDefault(c => c.Id == Detail.DefaultEmulatorConfig?.Id)
                : null;
        }
    }

    [RelayCommand]
    private async Task ApplyProfileOverrideAsync()
    {
        if (Detail?.Release == null) return;
        if (SelectedDatEntry != null && Detail.Release.DemozooId != null)
            await _releaseService.SetDatEntryProfileOverrideAsync(
                Detail.Release.DemozooId.Value, SelectedDatEntry.RomPath, SelectedOverrideProfile?.Id);
        else
            await _releaseService.SetProfileOverrideAsync(Detail.Release.Id, SelectedOverrideProfile?.Id);
        await RefreshProfileOverrideStateAsync(Detail.Release.Id);
    }

    [RelayCommand]
    private async Task ResetProfileOverrideAsync()
    {
        if (Detail?.Release == null) return;
        if (SelectedDatEntry != null && Detail.Release.DemozooId != null)
            await _releaseService.SetDatEntryProfileOverrideAsync(
                Detail.Release.DemozooId.Value, SelectedDatEntry.RomPath, null);
        else
            await _releaseService.SetProfileOverrideAsync(Detail.Release.Id, null);
        await RefreshProfileOverrideStateAsync(Detail.Release.Id);
    }

    /// <summary>
    /// Recharge uniquement Detail (pour resynchroniser DefaultEmulatorConfig/
    /// IsProfileOverridden, utilisés par le bouton Lancer) sans toucher au lecteur
    /// audio/vidéo en cours — contrairement à LoadAsync, qui les arrête.
    /// </summary>
    private async Task RefreshProfileOverrideStateAsync(int releaseId)
    {
        Detail = await _releaseService.GetDetailAsync(releaseId);
        await RefreshFileProfileOverrideDisplayAsync();
    }

    /// <summary>
    /// Arrête et masque tous les médias actifs (TrackerPlayer, VideoPlayer, GraphicsViewer).
    /// Appelé par MainViewModel dès qu'on navigue vers une autre vue.
    /// </summary>
    public void StopAllMedia()
    {
        SoundtrackPlayer?.Vm.Dispose();
        SoundtrackPlayer   = null;
        VideoPlayer?.Dispose();
        VideoPlayer    = null;
        InlineVideoPlayer?.Dispose();
        InlineVideoPlayer  = null;
        GraphicsViewer?.Dispose();
        GraphicsViewer = null;
        CodeSourceViewer?.Dispose();
        CodeSourceViewer = null;
    }

    // Dernier Release.Id pour lequel les resets ci-dessous ont déjà été appliqués — évite
    // de les rejouer quand Detail est réassigné pour LA MÊME release (ex.
    // RefreshProfileOverrideStateAsync, appelée par le bouton "Appliquer" du profil debug,
    // qui recharge Detail sans qu'il s'agisse d'une navigation vers une autre release).
    private int? _lastDetailResetReleaseId;

    partial void OnDetailChanged(ReleaseDetailDto? value)
    {
        var newReleaseId = value?.Release?.Id;
        if (newReleaseId != _lastDetailResetReleaseId)
        {
            _lastDetailResetReleaseId = newReleaseId;

            // ReleaseDetailViewModel est un Singleton (réutilisé pour toutes les releases,
            // jamais recréé à la navigation) — sans ce reset, le bandeau d'erreur d'un
            // téléchargement raté sur UNE release restait affiché sur toutes les releases
            // suivantes, même celles jamais téléchargées (BuildErrorMessage n'était remis à
            // null qu'au début de LaunchAsync, pas à chaque changement de release affichée).
            BuildErrorMessage   = null;
            BuildStatusMessage  = "";
            BuildStatusPercent  = 0;
            IsBuildingRelease   = false;

            // Même problème pour SelectedDatEntry : AutoSelectDatEntryAsync() (appelée juste
            // après Detail=... dans LoadAsync) ne fait RIEN si la nouvelle release n'a aucun
            // DatFile (retour anticipé) — sans ce reset, le DAT de la release précédente restait
            // sélectionné et LaunchAsync utilisait alors son romPath comme override, lançant
            // l'émulateur avec le fichier de l'AUTRE release (bug constaté : release BBC Micro
            // sans aucun fichier qui relançait quand même la release précédente).
            SelectedDatEntry = null;
            _fileSelectionConfirmed = false;

            // Revenir sur l'onglet Infos à chaque nouvelle release — SelectedTabIndex n'est
            // sinon basculé sur "Fichiers" (index 4) qu'en cas d'échec de téléchargement,
            // cf. EnsureReleaseFilesAvailableAsync.
            SelectedTabIndex = 0;
        }

        OnPropertyChanged(nameof(IsMusic));
        OnPropertyChanged(nameof(IsGraphics));
        OnPropertyChanged(nameof(IsExecutableMusicOrGraphics));
        OnPropertyChanged(nameof(IsVideoRelease));
        OnPropertyChanged(nameof(LaunchLabel));
        OnPropertyChanged(nameof(ShowSoundtrackArea));
        OnPropertyChanged(nameof(HasAnyFile));
        OnPropertyChanged(nameof(IsExternalOnlyRelease));
        OnPropertyChanged(nameof(NeedsAdHocDownload));
        OnPropertyChanged(nameof(DemozooIdDebugLabel));
        OnPropertyChanged(nameof(DemozooDebugUrl));
        if (IsMusic && _favService != null && value?.Release?.DemozooId != null)
            _ = CheckMusicFavoriteAsync(value.Release.DemozooId.Value);
        if (IsGraphics && _favGraphicService != null && value?.Release?.DemozooId != null)
            _ = CheckGraphicFavoriteAsync(value.Release.DemozooId.Value);

        // ── Graphics : afficher automatiquement le viewer ──
        // Note : LoadAsync() appelle aussi ShowGraphicsAsync() explicitement un peu plus loin
        // (une fois AutoSelectDatEntryAsync() passé) ; les deux appels sont fire-and-forget et
        // partagent donc le même garde forReleaseId==_lastLoadedReleaseId pour éviter que
        // l'un écrase le résultat de l'autre en cas d'exécution concurrente.
        if (IsGraphics && value?.Release != null)
            _ = ShowGraphicsAsync(value.Release.Id);

        // ── Vidéo : construire le VideoPlayer si des liens web existent ──
        RefreshVideoPlayer(value);

        // ── Liens (onglet Infos) — réactivé le 2026-07-24, diagnostic HasNoFile ──
        HasLinks = value?.Links?.Any() == true;

        // ── Vidéos locales : chercher les captures sur disque ──
        if (value?.Release != null)
            _ = LoadLocalVideosAsync(value.Release);
    }

    /// <summary>
    /// Appelé automatiquement par le conteneur DI à la fermeture de l'application
    /// (ReleaseDetailViewModel est enregistré en Singleton dans App.xaml.cs — son
    /// Dispose() est invoqué par _host.Dispose() dans App.OnExit). Doit donc libérer
    /// TOUTES les ressources actives, sans dépendre du détachement de l'arbre visuel
    /// WPF (SoundtrackPlayerView.Unloaded), qui n'est pas garanti pendant l'arrêt du
    /// process — c'est ce qui pouvait laisser zxtune123.exe/uade123.exe ou un
    /// périphérique audio (WaveOutEvent) ouvert, empêchant le process de se terminer
    /// proprement et verrouillant les DLL au prochain build.
    /// </summary>
    public void Dispose()
    {
        _navigation.Navigated -= OnNavigated;
        SoundtrackPlayer?.Vm.Dispose();
        SoundtrackPlayer = null;
        VideoPlayer?.Dispose();
        VideoPlayer = null;
        InlineVideoPlayer?.Dispose();
        InlineVideoPlayer = null;
        GraphicsViewer?.Dispose();
        GraphicsViewer = null;
        CodeSourceViewer?.Dispose();
        CodeSourceViewer = null;
    }

    // ─── VideoPlayer (liens web YouTube/Vimeo + captures locales) ────────────

    private void RefreshVideoPlayer(ReleaseDetailDto? detail)
    {
        if (detail == null)
        {
            VideoPlayer?.Dispose();
            VideoPlayer = null;
            return;
        }

        _webVideoLinks = DemoBase.App.ViewModels.VideoPlayerViewModel
            .ExtractVideoLinks(detail.Links, detail.Release?.Title ?? string.Empty)
            .ToList();
        OnPropertyChanged(nameof(WebVideoLinks));

        // Ne créer le VideoPlayer que pour les captures locales ou les vidéos
        // non-YouTube (Vimeo, fichiers locaux). Les liens YouTube seuls sont
        // affichés via un lien cliquable, pas via le player intégré.
        var nonYoutubeLinks = _webVideoLinks
            .Where(l => !l.IsYouTube)
            .ToList();

        if (nonYoutubeLinks.Count > 0)
        {
            var vm = new DemoBase.App.ViewModels.VideoPlayerViewModel();
            vm.LoadPlaylist(nonYoutubeLinks);
            // 2026-07-27 : ne pas auto-lancer la vidéo si un soundtrack de la même release
            // est déjà en cours de lecture — cf. VideoPlayerViewModel.IsOtherAudioPlaying.
            vm.IsOtherAudioPlaying = () => SoundtrackPlayer?.Vm.IsPlaying == true;
            VideoPlayer?.Dispose();
            VideoPlayer = vm;
        }
        else
        {
            VideoPlayer?.Dispose();
            VideoPlayer = null;
        }

        // L'onglet est visible dès qu'il y a un lien vidéo (YouTube inclus)
        // même si le player intégré n'est pas créé pour YouTube seul
        HasVideos = _webVideoLinks.Count > 0;
    }

    private List<DemoBase.App.ViewModels.VideoLinkDto> _webVideoLinks = [];

    /// <summary>Liens vidéo web (YouTube, Vimeo) — utilisés dans l'onglet Médias.</summary>
    public IReadOnlyList<DemoBase.App.ViewModels.VideoLinkDto> WebVideoLinks => _webVideoLinks;

    private async Task LoadLocalVideosAsync(DemoBase.Core.Models.Release release)
    {
        if (_videoCaptureService == null) return;
        IsLoadingVideos = true;
        try
        {
            var releasers = release.Authors
                .Where(a => a.Nick?.Releaser != null)
                .Select(a => (
                    Name:         a.Nick!.Releaser.Name,
                    Abbreviation: a.Nick.Releaser.Abbreviation))
                .Where(r => r.Name.Length > 0)
                .Distinct()
                .ToList();

            var locals = await _videoCaptureService.FindVideosAsync(
                release.Title, release.ReleaseDate, releasers);

            LocalVideos = locals;

            if (locals.Count > 0)
            {
                if (VideoPlayer == null)
                    VideoPlayer = new DemoBase.App.ViewModels.VideoPlayerViewModel();
                // 2026-07-27 : cf. commentaire dans RefreshVideoPlayer — même garde anti-superposition
                // avec un soundtrack en cours de lecture (réappliqué à chaque fois, sans effet de bord
                // si déjà posé).
                VideoPlayer.IsOtherAudioPlaying = () => SoundtrackPlayer?.Vm.IsPlaying == true;
                VideoPlayer.LoadLocalFiles(locals);
                HasLocalVideos = true;
                // Bug corrigé le 2026-07-24 (retour utilisateur : badge 🎬 affiché dans la liste
                // — cf. HasLocalVideo, calculé via l'index de titres — mais sous-onglet "Vidéo"
                // absent dans Médias). RefreshVideoPlayer() (appelé AVANT cette méthode, cf.
                // OnDetailChanged) ne met HasVideos à true que s'il y a des liens web (YouTube/
                // Vimeo) ; cette méthode-ci renseignait HasLocalVideos mais oubliait de mettre
                // aussi à jour HasVideos, qui pilote la Visibility du TabItem "Vidéo" — donc le
                // sous-onglet restait caché même quand des captures locales existaient vraiment.
                HasVideos = true;
            }
            else
            {
                HasLocalVideos = false;
                if (VideoPlayer == null && _webVideoLinks.Count > 0)
                {
                    var vm = new DemoBase.App.ViewModels.VideoPlayerViewModel();
                    vm.LoadPlaylist(_webVideoLinks);
                    // 2026-07-27 : cf. commentaire dans RefreshVideoPlayer.
                    vm.IsOtherAudioPlaying = () => SoundtrackPlayer?.Vm.IsPlaying == true;
                    VideoPlayer = vm;
                }
                else if (VideoPlayer != null && _webVideoLinks.Count > 0)
                {
                    VideoPlayer.LoadPlaylist(_webVideoLinks);
                }
            }
        }
        catch { LocalVideos = []; HasLocalVideos = false; }
        finally { IsLoadingVideos = false; }
    }

    private async Task CheckMusicFavoriteAsync(int demozooId)
    {
        IsMusicFavorite = await _favService!.IsFavoriteAsync(demozooId);
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        if (Detail?.Release == null) return;
        BuildErrorMessage = null; // Reset avant chaque tentative de lancement

        // Compteur de vues : incrémenté à chaque clic sur le bouton principal
        // (Play/Afficher/Regarder/Lancer), quel que soit le chemin emprunté ensuite.
        // Peut déclencher l'ajout automatique aux favoris si le seuil configuré est
        // atteint — on reflète alors l'étoile immédiatement sans recharger toute la
        // fiche (un rechargement complet couperait un lecteur média en cours d'ouverture).
        var (newViewCount, isFavoriteNow) = await _releaseService.IncrementViewCountAsync(
            Detail.Release.Id, Detail.Release.IsFavorite);
        Detail.Release.ViewCount  = newViewCount;
        Detail.Release.IsFavorite = isFavoriteNow;
        OnPropertyChanged(nameof(Detail));

        // Reconstruction automatique des fichiers manquants — couvre tous les
        // chemins de lancement ci-dessous (Music, Graphics, Vidéo, Demo générique
        // via _releaseService.LaunchAsync) sans dupliquer la logique dans chacun.
        if (!await EnsureReleaseFilesAvailableAsync())
        {
            // Signaler l'échec pour que le MediaBrowser passe à la release suivante
            System.Diagnostics.Debug.WriteLine(
                $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] PlaybackStartFailed.Invoke() [site=EnsureReleaseFilesAvailable] " +
                $"DemozooId={Detail.Release.DemozooId} thread={Environment.CurrentManagedThreadId}");
            PlaybackStartFailed?.Invoke();
            return;
        }

        // Pour les releases Music : ouvrir dans le TrackerPlayer
        if (IsMusic)
        {
            await PlayMusicReleaseAsync();
            return;
        }

        // Pour les "Executable Music" (supertype=music mais ReleaseType contient "executable") :
        // lancer via ExeMusicPlayer pour que PlaylistEnded déclenche le passage au suivant.
        if (IsExecutableMusicOrGraphics && Detail?.Release?.Supertype == "music")
        {
            await PlayMusicReleaseAsync();
            return;
        }

        // Pour les releases Graphics : afficher le viewer d'images
        if (IsGraphics)
        {
            await ShowGraphicsAsync(Detail!.Release.Id, allowAdHocDownload: true);
            return;
        }

        // Pour les releases de type Vidéo : extraire et jouer dans le player inline
        if (IsVideoRelease)
        {
            await PlayVideoInlineAsync();
            return;
        }

        // Choix explicite du FICHIER à lancer — quand la release a plusieurs fichiers
        // lançables et qu'aucun n'a encore été explicitement choisi pour cette release
        // (bouton "Utiliser" dans l'onglet Fichiers, ou choix déjà mémorisé lors d'un
        // lancement précédent), propose une fenêtre de choix (nom du zip + plateforme
        // concernée) plutôt que de se fier silencieusement à l'estimation par défaut
        // (AutoSelectDatEntryAsync, premier fichier non-vidéo, sans notion de
        // plateforme) — cf. RESUME_PROJET.md (2026-07-25, retour utilisateur :
        // releases multi-fichier, ex. "Starstruck" Amiga AGA + Atari Falcon, où le
        // fichier deviné automatiquement n'est pas forcément celui voulu). Le choix
        // est mémorisé durablement (ReleasePreferredFiles) — plus jamais redemandé
        // ensuite pour cette release.
        if (NeedsEmulatorProfile && !_fileSelectionConfirmed)
        {
            var launchableForPicker = Detail!.DatFiles.Where(d => !d.IsCodeSourceEntry).ToList();
            if (launchableForPicker.Count > 1)
            {
                var chosen = await PromptFileChoiceAsync(launchableForPicker);
                if (chosen == null)
                    return; // choix de fichier annulé par l'utilisateur — on n'insiste pas

                SelectedDatEntry = chosen;
                _fileSelectionConfirmed = true;
                if (Detail.Release.DemozooId != null)
                    await _releaseService.SetPreferredFileAsync(Detail.Release.DemozooId.Value, chosen.RomPath);
            }
        }

        // Résolution du profil à utiliser — override par fichier (SelectedDatEntry)
        // prioritaire sur l'override par release, prioritaire sur le défaut plateforme
        // (Detail.DefaultEmulatorConfig). Si la release est multi-plateforme et
        // qu'aucun des deux overrides n'existe encore pour ce fichier, propose un choix
        // via PlatformPickerWindow et mémorise la réponse — cf. RESUME_PROJET.md
        // (2026-07-25, retour utilisateur : releases multi-plateforme ET multi-fichier,
        // ex. Amiga AGA + Atari Falcon, un seul override par release ne suffisait pas).
        int? emulatorConfigId = Detail!.DefaultEmulatorConfig?.Id;
        if (NeedsEmulatorProfile)
        {
            emulatorConfigId = await ResolveOrPromptEmulatorConfigIdAsync();
            if (emulatorConfigId == null && IsReleaseMultiPlatform)
                return; // choix de plateforme annulé par l'utilisateur — on n'insiste pas
        }

        // Lancement direct depuis un lien Demozoo, sans attendre le DAT (2026-07-25) —
        // le DAT n'est reconstruit qu'environ 1x/mois alors que la base Demozoo (import
        // MySQL) est généralement plus à jour ; une release toute fraîche peut donc
        // n'avoir AUCUN DatEntry pendant plusieurs semaines. Si Demozoo fournit lui-même
        // un lien de téléchargement direct (IsMainFile, mappé sur son champ
        // "is_download_link" — donc un signal fiable, pas une supposition de notre
        // côté), on propose de le récupérer à la volée plutôt que de bloquer
        // l'utilisateur. Confirmation demandée uniquement la toute première fois (avant
        // que le fichier ne soit mis en cache localement) — cf. RESUME_PROJET.md.
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? downloadProgress = null;
        if (NeedsEmulatorProfile && !Detail.DatFiles.Any(d => !d.IsCodeSourceEntry))
        {
            // 2026-07-25 : EffectiveDownloadUrl doit être non vide — Demozoo peut marquer
            // un lien IsMainFile sans URL renseignée (constaté sur "Fullast Vinner 2"),
            // auquel cas il n'y a en réalité rien à télécharger. Sans ce filtre, la
            // confirmation s'affichait quand même (avec un libellé fichier/hébergeur vide)
            // puis le lancement échouait silencieusement (StatusScrollerControl seul,
            // facile à manquer) une fois arrivé dans ResolveFileAsync côté service.
            // 2026-07-25 (suite, "Return to Promised Land", Demozoo #394835) : Url tout
            // court était TROP strict — la classe de lien "BaseUrl" ne remplit jamais
            // "Url", seulement "LinkParameter" (qui contient déjà l'URL complète). D'où
            // EffectiveDownloadUrl (cf. ReleaseLink dans DemoBase.Core/Models/Models.cs),
            // qui couvre les deux cas sans réintroduire le bug "Fullast Vinner 2" (un lien
            // sans Url NI LinkParameter exploitable reste toujours filtré).
            var candidateLink = Detail.Links.Where(l => !l.IsVideo)
                .FirstOrDefault(l => l.IsMainFile && !string.IsNullOrEmpty(l.EffectiveDownloadUrl));
            if (candidateLink != null)
            {
                bool alreadyLocal = !string.IsNullOrEmpty(candidateLink.LocalFilePath)
                    && System.IO.File.Exists(candidateLink.LocalFilePath);

                if (!alreadyLocal)
                {
                    var prefs       = _prefsService != null ? await _prefsService.LoadAllAsync() : null;
                    bool skipConfirm = prefs?.SkipExternalDownloadConfirm ?? false;
                    if (!skipConfirm && !await ConfirmExternalDownloadAsync(candidateLink))
                        return; // annulé — on n'insiste pas
                }

                IsBuildingRelease   = true;
                BuildStatusMessage  = "Préparation…";
                BuildStatusPercent  = 0;
                downloadProgress = new Progress<DemoBase.Core.DTOs.LaunchDownloadProgress>(p =>
                {
                    // 2026-07-27 : ReleaseService.LaunchAsync relaie désormais une erreur de
                    // téléchargement (ex. connexion refusée) par ce même canal (IsError=true)
                    // au lieu d'une MessageBox système — affichée ici DANS l'overlay
                    // "Téléchargement en cours…" (BuildErrorMessage, déjà câblé sur cet overlay
                    // côté XAML, avec un bouton OK — cf. DismissBuildErrorCommand) plutôt que
                    // dans une fenêtre séparée par-dessus.
                    if (p.IsError)
                    {
                        BuildErrorMessage = p.Message;
                        return;
                    }
                    BuildStatusMessage = p.Message;
                    BuildStatusPercent = p.Percent;
                });
            }
        }

        try
        {
            await _releaseService.LaunchAsync(Detail!.Release.Id, emulatorConfigId,
                romPathOverride: SelectedDatEntry != null && _prefsService != null
                    ? await ResolveSelectedDatPathAsync()
                    : null,
                progress: downloadProgress);
        }
        finally
        {
            // 2026-07-27 : ne pas refermer l'overlay automatiquement si une erreur y est
            // affichée — il reste ouvert jusqu'au clic sur OK (DismissBuildErrorCommand).
            if (downloadProgress != null && BuildErrorMessage == null)
                IsBuildingRelease = false;
        }
    }

    /// <summary>
    /// Demande confirmation avant de télécharger un fichier pas encore couvert par un
    /// DAT directement depuis le lien Demozoo — contrairement aux fichiers du DAT/Mega,
    /// celui-ci n'est vérifié par aucun contrôle CRC, d'où la confirmation explicite
    /// (2026-07-25). Non redemandée une fois le fichier mis en cache localement, ni si
    /// l'utilisateur a coché "Ne plus demander" (cf. appelant, PrefKeys.
    /// SkipExternalDownloadConfirm). Utilise <see cref="DemoBase.App.Views.Releases.ExternalDownloadConfirmWindow"/>
    /// (même style que PlatformPickerWindow/FilePickerWindow) plutôt qu'une MessageBox
    /// système — remplace l'ancienne confirmation native, hors charte graphique.
    /// </summary>
    private async Task<bool> ConfirmExternalDownloadAsync(DemoBase.Core.Models.ReleaseLink link)
    {
        var downloadUrl = link.EffectiveDownloadUrl;
        var host = Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : (downloadUrl ?? "(hébergeur inconnu)");
        var fileLabel = link.FileName
            ?? (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var fileUri)
                ? System.IO.Path.GetFileName(fileUri.LocalPath)
                : null);
        if (string.IsNullOrWhiteSpace(fileLabel)) fileLabel = "(fichier)";

        var dialog = new DemoBase.App.Views.Releases.ExternalDownloadConfirmWindow(fileLabel, host)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (dialog.ShowDialog() != true)
            return false; // annulé

        if (dialog.DontAskAgain && _prefsService != null)
            await _prefsService.SetAsync(DemoBase.Data.PrefKeys.SkipExternalDownloadConfirm, "true");

        return true;
    }

    private async Task<string?> ResolveSelectedDatPathAsync()
    {
        if (SelectedDatEntry == null || _prefsService == null) return null;
        var prefs = await _prefsService.LoadAllAsync();
        var path  = System.IO.Path.Combine(prefs.ResolvedPathReleases, SelectedDatEntry.RomPath);
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>Vrai si les profils disponibles pour cette release couvrent plus d'une
    /// plateforme distincte (ex. Amiga AGA + Atari Falcon) — condition nécessaire pour
    /// proposer/justifier un choix de plateforme au lancement.</summary>
    private bool IsReleaseMultiPlatform =>
        AvailableProfilesForOverride.Select(c => c.PlatformId).Distinct().Count() > 1;

    /// <summary>
    /// Résout le profil à utiliser pour le fichier actuellement sélectionné (ou pour la
    /// release entière si aucun fichier n'est sélectionné) : override par fichier, puis
    /// override par release (déjà reflété dans Detail.DefaultEmulatorConfig), sans
    /// prompt — utilisé pour l'affichage (widget "Profil") comme pour la résolution
    /// silencieuse au lancement quand un choix a déjà été mémorisé.
    /// </summary>
    private async Task<int?> ResolveEffectiveEmulatorConfigIdAsync()
    {
        if (SelectedDatEntry != null && Detail?.Release?.DemozooId != null)
        {
            var fileOverride = await _releaseService.GetDatEntryProfileOverrideAsync(
                Detail.Release.DemozooId.Value, SelectedDatEntry.RomPath);
            if (fileOverride.HasValue) return fileOverride;
        }
        return Detail?.DefaultEmulatorConfig?.Id;
    }

    /// <summary>
    /// Comme <see cref="ResolveEffectiveEmulatorConfigIdAsync"/>, mais si rien n'est
    /// résolu ET que la release est multi-plateforme, ouvre <see cref="DemoBase.App.Views.Releases.PlatformPickerWindow"/>
    /// pour demander à l'utilisateur, puis mémorise sa réponse comme override (par
    /// fichier si un DatEntry est sélectionné, par release sinon) afin de ne plus
    /// jamais redemander pour ce même fichier. Retourne null si rien n'a pu être résolu
    /// (release non multi-plateforme sans profil configuré — laisse LaunchAsync gérer
    /// l'erreur comme avant) ou si l'utilisateur a annulé le choix proposé.
    /// </summary>
    private async Task<int?> ResolveOrPromptEmulatorConfigIdAsync()
    {
        var resolved = await ResolveEffectiveEmulatorConfigIdAsync();
        if (resolved.HasValue) return resolved;
        if (!IsReleaseMultiPlatform) return null;

        var fileLabel = SelectedDatEntry != null
            ? System.IO.Path.GetFileName(SelectedDatEntry.RomPath)
            : Detail?.Release?.Title;

        var picker = new DemoBase.App.Views.Releases.PlatformPickerWindow(AvailableProfilesForOverride, fileLabel)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (picker.ShowDialog() != true || picker.SelectedProfile == null)
            return null; // annulé

        if (SelectedDatEntry != null && Detail?.Release?.DemozooId != null)
            await _releaseService.SetDatEntryProfileOverrideAsync(
                Detail.Release.DemozooId.Value, SelectedDatEntry.RomPath, picker.SelectedProfile.Id);
        else if (Detail?.Release != null)
            await _releaseService.SetProfileOverrideAsync(Detail.Release.Id, picker.SelectedProfile.Id);

        await RefreshFileProfileOverrideDisplayAsync();
        return picker.SelectedProfile.Id;
    }

    /// <summary>
    /// Ouvre <see cref="DemoBase.App.Views.Releases.FilePickerWindow"/> pour demander à
    /// l'utilisateur quel fichier (parmi ceux lançables) utiliser, avec pour chaque ligne
    /// le nom du .zip et une estimation de la plateforme concernée — cf.
    /// <see cref="BuildFilePickerEntriesAsync"/>. Retourne le DatEntry choisi, ou null si
    /// l'utilisateur a annulé.
    /// </summary>
    private async Task<DemoBase.Core.Models.DatEntry?> PromptFileChoiceAsync(
        List<DemoBase.Core.Models.DatEntry> launchableDats)
    {
        var entries = await BuildFilePickerEntriesAsync(launchableDats);

        var picker = new DemoBase.App.Views.Releases.FilePickerWindow(entries)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        if (picker.ShowDialog() != true || picker.SelectedFile == null)
            return null; // annulé

        return picker.SelectedFile;
    }

    /// <summary>
    /// Construit les lignes de la fenêtre de choix de fichier : nom du .zip + libellé de
    /// plateforme concernée. Le libellé est déduit, par ordre de préférence, d'un override
    /// de profil déjà enregistré pour CE fichier (fiable), ou à défaut d'un rapprochement
    /// entre le dossier du RomPath et le nom des plateformes taguées sur la release
    /// (estimation, marquée comme telle — aucune table ne relie directement un DatEntry à
    /// une Platform dans le schéma actuel). Si rien ne matche, affiche "Plateforme à
    /// choisir" — le profil sera de toute façon résolu/demandé juste après (cf.
    /// ResolveOrPromptEmulatorConfigIdAsync), une estimation imprécise ici n'est donc
    /// jamais bloquante.
    /// </summary>
    private async Task<List<DemoBase.App.Views.Releases.FilePickerEntry>> BuildFilePickerEntriesAsync(
        List<DemoBase.Core.Models.DatEntry> launchableDats)
    {
        var result = new List<DemoBase.App.Views.Releases.FilePickerEntry>();

        if (Detail?.Release?.DemozooId == null)
        {
            var tbdLabel = DemoBase.App.Services.LocalizationService.Get("FPick_PlatformTBD");
            foreach (var dat in launchableDats)
                result.Add(new(dat, System.IO.Path.GetFileName(dat.RomPath), tbdLabel));
            return result;
        }

        var demozooId = Detail.Release.DemozooId.Value;
        var overrides = await _releaseService.GetDatEntryProfileOverridesForReleaseAsync(demozooId);
        var taggedPlatformNames = Detail.Release.ReleasePlatforms
            .Select(rp => rp.Platform?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dat in launchableDats)
        {
            var fileName = System.IO.Path.GetFileName(dat.RomPath);
            string platformLabel;

            if (overrides.TryGetValue(dat.RomPath, out var cfgId))
            {
                var cfg = AvailableProfilesForOverride.FirstOrDefault(c => c.Id == cfgId);
                platformLabel = cfg?.Platform?.Name
                    ?? DemoBase.App.Services.LocalizationService.Get("FPick_PlatformTBD");
            }
            else
            {
                var folder = System.IO.Path.GetDirectoryName(dat.RomPath) ?? string.Empty;
                var guess = !string.IsNullOrEmpty(folder)
                    ? taggedPlatformNames.FirstOrDefault(p =>
                        folder.Contains(p, StringComparison.OrdinalIgnoreCase) ||
                        p.Contains(folder, StringComparison.OrdinalIgnoreCase))
                    : null;
                platformLabel = guess != null
                    ? string.Format(DemoBase.App.Services.LocalizationService.Get("FPick_PlatformGuessed"), guess)
                    : DemoBase.App.Services.LocalizationService.Get("FPick_PlatformTBD");
            }

            result.Add(new(dat, fileName, platformLabel));
        }

        return result;
    }

    /// <summary>
    /// Vrai si le chemin de lancement de cette release passe par le lanceur d'émulateur
    /// générique (donc a besoin de Detail.DefaultEmulatorConfig) plutôt que par un chemin
    /// spécial qui n'en a pas besoin (TrackerPlayer pour Music, GraphicsViewer pour
    /// Graphics, lecteur vidéo inline pour Vidéo) — reflète exactement le routage fait par
    /// <see cref="LaunchAsync"/> plus bas, pour ne réclamer un profil que quand il sera
    /// réellement utilisé.
    /// </summary>
    private bool NeedsEmulatorProfile =>
        !IsVideoRelease && !IsMusic && !IsGraphics
        && !(IsExecutableMusicOrGraphics && Detail?.Release?.Supertype == "music");

    /// <summary>
    /// 2026-07-30 : un ZIP présent au chemin attendu n'est plus une garantie de complétude —
    /// <see cref="DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService.TryBuildAsync"/>
    /// peut désormais écrire un ZIP "best effort" volontairement incomplet (fallback quand un
    /// fichier essentiel reste introuvable, ex. lien AtariAge bloqué). On vérifie donc le
    /// contenu réel du ZIP par rapport aux fichiers essentiels du DAT (mêmes extensions
    /// ignorées que <see cref="IgnoredTextExtensions"/> : .txt/.nfo/… — non requis) plutôt que
    /// sa simple présence sur disque, sans quoi tout DatEntry ayant déjà eu un build partiel ne
    /// retenterait plus jamais rien (ni "Lancer" ni "Réessayer") en silence.
    /// </summary>
    /// <summary>
    /// 2026-08-08, retour utilisateur ("le repertoire match les dats qui sont importés donc
    /// il doit trouver le zip sans avoir à télécharger [...] verifie qu'il n'y ait pas une
    /// condition qui l'empeche de la faire") — <paramref name="reason"/> explique le
    /// "false" (fichier absent au chemin exact vs zip présent mais incomplet, avec la liste
    /// des noms internes manquants) plutôt qu'un simple booléen opaque comme avant ce
    /// correctif. Appelée par <see cref="EnsureReleaseFilesAvailableAsync"/> qui journalise
    /// ce détail dans perf_log.txt à CHAQUE tentative de lancement — jusqu'ici rien
    /// n'expliquait pourquoi un zip pourtant présent sur le disque de l'utilisateur était
    /// jugé "manquant" (mauvais RomPath ? sous-dossier de plateforme absent ? nom de fichier
    /// interne différent de celui attendu par le DAT ?). Diagnostic à distance nécessaire
    /// ici : l'outil n'a pas accès à la collection réelle de l'utilisateur.
    /// </summary>
    private static bool IsBuiltZipCompleteEnough(string zipPath, DemoBase.Core.Models.DatEntry entry, out string? reason)
    {
        reason = null;
        if (!System.IO.File.Exists(zipPath))
        {
            reason = "fichier absent à ce chemin exact";
            return false;
        }

        var essentialRomNames = entry.Roms
            .Where(r => !IgnoredTextExtensions.Contains(System.IO.Path.GetExtension(r.Name).ToLowerInvariant()))
            .Select(r => r.Name)
            .ToList();
        // Aucun fichier "essentiel" dans ce set (rare) : la simple présence du ZIP suffit.
        if (essentialRomNames.Count == 0) return true;

        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
            // 2026-08-08, retour utilisateur (bug confirmé — capture d'écran + log [BUILD] à
            // l'appui, cas réel "Suicide Barbie" PSP, 783 ROMs) : ZipArchiveEntry.Name ne
            // retourne QUE le nom de fichier (dernier segment du chemin), alors que
            // DatRom.Name contient le chemin relatif COMPLET avec antislashs tel que stocké
            // dans le DAT (ex. "__SCE__SuicideBarbie\BarbieData\music\atrac3streamer.prx").
            // BuildZipForSet (plus bas dans ReleaseBuilderService) écrit ses entrées avec ce
            // chemin complet (converti en slashs). Résultat : namesInZip ne contenait que des
            // noms de fichier NUS ("atrac3streamer.prx"), jamais égaux aux chemins complets
            // attendus — sauf pour les rares roms directement à la racine (2 sur 783 dans le
            // cas rapporté : Democoding_by_Daywish.jpg, readme.txt). Tout set avec une
            // arborescence de sous-dossiers (quasi tous les formats console/CD/disque) était
            // donc condamné à être signalé "incomplet" et à redéclencher un téléchargement,
            // même avec un ZIP déjà 100% correct et complet sur le disque de l'utilisateur.
            // Correctif : comparer FullName (chemin complet dans le zip) normalisé en slashs,
            // contre le nom du DatRom également normalisé en slashs (antislash → slash).
            var namesInZip = new HashSet<string>(
                archive.Entries.Select(e => e.FullName.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            var missing = essentialRomNames
                .Where(n => !namesInZip.Contains(n.Replace('\\', '/')))
                .ToList();
            if (missing.Count > 0)
            {
                reason = $"zip présent mais {missing.Count} fichier(s) interne(s) manquant(s) : " +
                          string.Join(", ", missing) +
                          $" (contenu réel du zip : {string.Join(", ", namesInZip)})";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // ZIP illisible/corrompu → traité comme incomplet, on retentera un build.
            reason = $"zip illisible/corrompu : {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Si au moins un fichier DAT de la release est manquant localement et qu'un
    /// download link existe (DemozooId connu), tente de le reconstruire
    /// automatiquement via <see cref="DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService"/>
    /// — affiche l'overlay "Téléchargement en cours…" pendant l'opération.
    /// Retourne true si les fichiers sont disponibles (déjà présents ou build réussi),
    /// false si le build a échoué (bloque la lecture).
    /// </summary>
    /// <param name="force">
    /// 2026-07-30, retour utilisateur : le bouton "Réessayer" du panneau "Fichiers
    /// incompatibles" appelait cette méthode mais, quand un ZIP (même partiel, issu du
    /// fallback "best effort" de TryBuildAsync) existait déjà au bon chemin, le court-circuit
    /// ci-dessous ("déjà présent → return true") empêchait TOUT nouveau téléchargement — le
    /// clic semblait ne rien faire. `force=true` (utilisé uniquement par "Réessayer") ignore
    /// ce court-circuit pour forcer une reconstruction complète.
    /// </param>
    // 2026-07-30, retour utilisateur ("Quik And Silva Ingame", TFMX) : certains formats sont
    // distribués en DEUX fichiers qui doivent se retrouver côte à côte pour être jouables — et
    // ces deux fichiers peuvent provenir de DEUX DatEntry/liens de téléchargement DIFFÉRENTS
    // (un set "mdat.*" et un autre set "smpl.*" du même suffixe). TFMX (Amiga, joué par UADE)
    // en est le premier cas rencontré ; "il y en aura peut être d'autres [cas particuliers]"
    // (retour utilisateur) — d'où cette liste, volontairement conçue pour être étendue sans
    // toucher au reste de la logique. Utilisée à la fois ici (forcer un nouveau build tant que
    // le compagnon n'est nulle part sur le disque) et dans PlayMusicReleaseAsync (copier le
    // compagnon à côté du fichier principal avant lecture).
    private static readonly (string TriggerPrefix, string CompanionPrefix)[] CompanionFilePairs =
    [
        ("mdat.", "smpl."), // TFMX (UADE) : patterns + échantillons
        // 2026-07-31, retour utilisateur (côté Modland, même principe applicable ici) :
        // format Thomas Hermann (UADE), même contrainte deux-fichiers que TFMX.
        ("thm.",  "smp."),  // Thomas Hermann (UADE) : morceau + échantillons
        // 2026-07-31, retour utilisateur : format Dirk Bialluch (UADE), mêmes
        // deux-fichiers ("smp.*" + "tpu.*", on joue le "tpu.*") — cf. pendant
        // TrackerPlayer.Core.Players.UadeCompanionFormats (Modland).
        ("tpu.",  "smp."),  // Dirk Bialluch (UADE) : morceau + échantillons
        // 2026-08-07, retour utilisateur ("les fichiers sjs.* doivent etre accompagné
        // des fichiers smp.*, tous comme les tfmx") : même contrainte deux-fichiers,
        // même pendant côté Modland (UadeCompanionFormats).
        ("sjs.",  "smp."),  // (UADE) : morceau + échantillons
    ];

    /// <summary>
    /// Vrai si au moins un DatEntry de la release contient un rom "déclencheur" (ex.
    /// "mdat.xxx") dont le fichier compagnon attendu (ex. "smpl.xxx") n'est présent dans AUCUN
    /// zip déjà construit sur le disque — y compris quand le set déclencheur est par ailleurs
    /// déjà "complet" à lui seul (cf. SetProgress.IsComplete, qui ignore ce genre de dépendance
    /// inter-set). Dans ce cas, un simple File.Exists() sur le set sélectionné ne suffit pas à
    /// dire "tout est prêt" — il faut relancer TryBuildAsync pour aller chercher le compagnon.
    /// </summary>
    private static bool HasUnsatisfiedCompanion(List<DatEntry> datEntries, string romsRoot)
    {
        foreach (var entry in datEntries)
        foreach (var rom in entry.Roms)
        foreach (var (triggerPrefix, companionPrefix) in CompanionFilePairs)
        {
            if (!rom.Name.StartsWith(triggerPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var companionName = companionPrefix + rom.Name[triggerPrefix.Length..];

            bool satisfied = datEntries.Any(e =>
            {
                var zipPath = System.IO.Path.Combine(romsRoot, e.RomPath);
                if (!System.IO.File.Exists(zipPath)) return false;
                try
                {
                    using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
                    return zip.Entries.Any(z =>
                        string.Equals(z.Name, companionName, StringComparison.OrdinalIgnoreCase));
                }
                catch { return false; }
            });
            if (!satisfied)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PLAY] Compagnon '{companionName}' (requis par '{rom.Name}') absent de tous les zips construits — rebuild nécessaire.");
                return true;
            }
        }
        return false;
    }

    private async Task<bool> EnsureReleaseFilesAvailableAsync(bool force = false)
    {
        if (Detail?.Release?.DemozooId == null || _releaseBuilderService == null || _prefsService == null)
            return true;
        // Les DatEntry "Code Sources" ne sont pas des fichiers lançables — ignorés ici pour ne
        // pas déclencher (ou éviter à tort) un téléchargement basé sur leur seule présence.
        var launchableDatFiles = Detail.DatFiles.Where(d => !d.IsCodeSourceEntry).ToList();
        if (launchableDatFiles.Count == 0) return true;

        // Pas la peine de lancer un téléchargement (parfois long) pour une plateforme sans
        // aucun profil d'émulateur configuré : la release ne pourra de toute façon pas être
        // lancée une fois téléchargée (demande utilisateur). Ne s'applique qu'aux releases
        // qui passeraient par le lanceur générique — Music/Graphics/Vidéo n'ont pas besoin
        // d'émulateur et continuent de se télécharger normalement.
        if (NeedsEmulatorProfile && Detail.DefaultEmulatorConfig == null)
        {
            BuildErrorMessage = "Aucun profil d'émulateur configuré pour cette plateforme — téléchargement annulé.";
            SelectedTabIndex  = FilesTabIndex;
            return false;
        }

        var prefs = await _prefsService.LoadAllAsync();

        // 2026-08-08, retour utilisateur ("le repertoire match les dats qui sont importés
        // donc il doit trouver le zip sans avoir à télécharger [...] verifie qu'il n'y ait
        // pas une condition qui l'empeche de la faire") : journalisation détaillée dans
        // perf_log.txt à CHAQUE tentative — chemin Releases résolu, chemin exact testé pour
        // CHAQUE DatEntry candidat, et la raison précise en cas d'échec (fichier absent à ce
        // chemin, vs zip présent mais contenu interne différent de ce que le DAT attend).
        // Nécessaire pour diagnostiquer à distance : l'outil n'a pas accès à la collection
        // réelle de l'utilisateur, seulement à ce que perf_log.txt peut en dire une fois
        // reproduit chez lui.
        PerfLogger.Mark($"[PLAY] Lancement release DemozooId={Detail.Release.DemozooId} — " +
                         $"chemin Releases résolu : '{prefs.ResolvedPathReleases}'");

        bool selectedZipMissing = false;
        if (SelectedDatEntry != null)
        {
            var selPath = System.IO.Path.Combine(prefs.ResolvedPathReleases, SelectedDatEntry.RomPath);
            bool selOk = IsBuiltZipCompleteEnough(selPath, SelectedDatEntry, out var selReason);
            selectedZipMissing = !selOk;
            PerfLogger.Mark(selOk
                ? $"[PLAY] Set sélectionné (DatEntry #{SelectedDatEntry.Id}, source '{SelectedDatEntry.SourceFile}') OK : '{selPath}'"
                : $"[PLAY] Set sélectionné (DatEntry #{SelectedDatEntry.Id}, source '{SelectedDatEntry.SourceFile}') INCOMPLET : '{selPath}' — {selReason}");
        }

        // 2026-07-30, retour utilisateur : après un "Réessayer"/"Lancer" qui n'a réussi qu'une
        // reconstruction "best effort" (fallback de TryBuildAsync — ZIP partiel, fichier
        // essentiel manquant type Cartridge_512kb_4kb_bankswitch.zip toujours bloqué par le 403
        // AtariAge), le ZIP partiel existe désormais au même chemin qu'un ZIP complet. Un simple
        // File.Exists() ne suffit donc plus à décider qu'"il n'y a rien à faire" : TOUT nouveau
        // clic sur "Lancer" (pas seulement "Réessayer") court-circuitait silencieusement le
        // nouvel essai — plus de message, plus de coche, plus de panneau, comme si de rien
        // n'était. On vérifie maintenant le contenu réel du ZIP contre les fichiers essentiels
        // du DAT plutôt que sa simple présence.
        bool anyZipAvailable = false;
        foreach (var d in launchableDatFiles)
        {
            var path = System.IO.Path.Combine(prefs.ResolvedPathReleases, d.RomPath);
            bool ok = IsBuiltZipCompleteEnough(path, d, out var reason);
            PerfLogger.Mark(ok
                ? $"[PLAY] DatEntry #{d.Id} (source '{d.SourceFile}') OK : '{path}'"
                : $"[PLAY] DatEntry #{d.Id} (source '{d.SourceFile}') INCOMPLET : '{path}' — {reason}");
            if (ok) anyZipAvailable = true;
        }
        bool noZipAvailable = !anyZipAvailable;

        // 2026-07-30, retour utilisateur (TFMX mdat/smpl sur deux DatEntry séparés) : voir
        // HasUnsatisfiedCompanion — un set "mdat.*" déjà construit lors d'un essai précédent
        // (suffisant à lui seul pour IsComplete) ne doit pas faire croire que la release est
        // prête si son compagnon "smpl.*" n'a jamais été téléchargé.
        bool missingCompanion = HasUnsatisfiedCompanion(launchableDatFiles, prefs.ResolvedPathReleases);

        if (!force && !selectedZipMissing && !noZipAvailable && !missingCompanion) return true;

        IsBuildingRelease  = true;
        BuildStatusMessage = "Recherche des fichiers de la release…";
        BuildStatusPercent = 0;
        try
        {
            var progress = new Progress<DemoBase.App.Services.ReleaseBuilder.BuildProgress>(p =>
            {
                BuildStatusMessage = p.Message;
                BuildStatusPercent = p.Percent;
            });
            var result = await Task.Factory.StartNew(
                () => _releaseBuilderService.TryBuildAsync(
                    Detail.Release.DemozooId.Value, progress,
                    preferredDatEntryId: SelectedDatEntry?.Id),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            System.Diagnostics.Debug.WriteLine(
                $"[PLAY] ReleaseBuilder: success={result.Success} " +
                $"files={result.FilesFound}/{result.FilesNeeded} error={result.Error}");
            LastBuildFoundRomIds = result.FoundRomIds != null
                ? new HashSet<int>(result.FoundRomIds) : new HashSet<int>();

            if (!result.Success)
            {
                BuildErrorMessage = result.Error ?? "Téléchargement incomplet";
                SelectedTabIndex  = FilesTabIndex; // bascule auto sur "Fichiers" pour que l'échec soit visible
                return false;
            }
            // 2026-07-30, retour utilisateur : succès "best effort" (fichier essentiel encore
            // manquant, ex. lien AtariAge en 403) — le lancement se poursuit quand même (le
            // .BIN suffit pour Atari 7800 ProSystem), mais l'avertissement de result.Error
            // n'était affiché QUE via un toast (StatusScrollerControl) puis perdu, alors que
            // BuildErrorMessage était remis à null : "j'ai plus les infos précédentes". On
            // garde maintenant le message visible dans la bannière Fichiers (liens cliquables,
            // détail par fichier) même après un lancement réussi en mode partiel, en plus du
            // toast pour un retour immédiat.
            BuildErrorMessage = result.Error;
            if (!string.IsNullOrEmpty(result.Error))
                DemoBase.App.Controls.StatusScrollerControl.Post(result.Error, isWarning: true);

            // Vider le cache du converter de couleur et recharger les DatFiles
            // pour que les sets passent immédiatement au vert sans rechargement
            DemoBase.App.Converters.DatEntryStatusToColorConverter.ClearCache();
            await LoadDownloadMismatchesAsync();

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PLAY] ReleaseBuilder a échoué : {ex.Message}");
            BuildErrorMessage = ex.Message;
            SelectedTabIndex  = FilesTabIndex; // bascule auto sur "Fichiers" pour que l'échec soit visible
            return false;
        }
        finally { IsBuildingRelease = false; }
    }

    // ─── Lecteur vidéo inline ──────────────────────────────────────────────────

    private static readonly string[] VideoFileExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".mpg", ".mpeg", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".vob"];

    private static readonly string[] IgnoredTextExtensions =
        [".txt", ".nfo", ".diz", ".doc", ".docx", ".pdf", ".md", ".rtf", ".log", ".ini", ".cfg"];

    /// <summary>
    /// Extrait les fichiers vidéo du zip DAT de la release et les joue dans
    /// <see cref="InlineVideoPlayer"/>. Ignore les fichiers texte/doc annexes
    /// (.txt, .nfo, .diz…) — si le zip ne contient QUE des vidéos + textes, on joue.
    /// </summary>
    private async Task PlayVideoInlineAsync()
    {
        if (Detail == null || _prefsService == null) return;

        var datEntries = (await _releaseService.GetDatEntriesAsync(
            Detail.Release.DemozooId ?? 0)).Where(d => !d.IsCodeSourceEntry).ToList();

        if (!datEntries.Any())
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Aucune entrée DAT trouvée pour cette release.", isWarning: true);
            return;
        }

        var prefs  = await _prefsService.LoadAllAsync();
        var paths  = new List<string>();

        foreach (var dat in datEntries)
        {
            var zipPath = System.IO.Path.Combine(prefs.ResolvedPathReleases, dat.RomPath);
            if (!System.IO.File.Exists(zipPath)) continue;

            var ext = System.IO.Path.GetExtension(zipPath).ToLowerInvariant();
            if (VideoFileExtensions.Contains(ext))
            {
                paths.Add(zipPath);
            }
            else if (ext == ".zip")
            {
                var extracted = await Task.Run(() =>
                {
                    var result = new List<string>();
                    var outDir = System.IO.Path.Combine(
                        DemoBase.App.Services.WorkingPaths.GetSubdir("Videos"),
                        "vid_" + Detail.Release.Id);
                    System.IO.Directory.CreateDirectory(outDir);
                    try
                    {
                        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
                        foreach (var entry in zip.Entries)
                        {
                            var entryExt = System.IO.Path.GetExtension(entry.Name).ToLowerInvariant();
                            if (!VideoFileExtensions.Contains(entryExt)) continue;
                            var dest = System.IO.Path.Combine(outDir, entry.Name);
                            if (!System.IO.File.Exists(dest))
                                entry.ExtractToFile(dest, overwrite: true);
                            if (System.IO.File.Exists(dest))
                                result.Add(dest);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VIDEO] Erreur extraction : {ex.Message}");
                    }
                    return result;
                });
                paths.AddRange(extracted);
            }
        }

        if (!paths.Any())
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Aucun fichier vidéo trouvé dans les archives de cette release.", isWarning: true);
            return;
        }

        var vm = new DemoBase.App.ViewModels.VideoPlayerViewModel();
        vm.LoadPlaylist(paths.Select(p => new DemoBase.App.ViewModels.VideoLinkDto
        {
            Title     = System.IO.Path.GetFileNameWithoutExtension(p),
            Url       = p,
            LinkClass = "LocalVideo",
        }));

        InlineVideoPlayer?.Dispose();
        InlineVideoPlayer = vm;
    }

    /// <summary>
    /// Pour les releases Music/Graphics/ExeMusic sans AUCUN DatEntry mais avec un lien
    /// de téléchargement direct Demozoo (IsExternalOnlyRelease) — même confirmation et
    /// overlay de progression que le bloc "téléchargement ad-hoc" de LaunchAsync (chemin
    /// émulateur générique), mais SANS lancement d'émulateur ensuite : retourne juste le
    /// chemin local du fichier téléchargé (généralement un .zip), à traiter exactement
    /// comme un DatEntry résolu par l'appelant (PlayMusicReleaseAsync/ShowGraphicsAsync).
    /// Retourne null si annulé par l'utilisateur, pas de lien exploitable, ou échec du
    /// téléchargement (BuildErrorMessage déjà positionné dans ce dernier cas — cf.
    /// ResolveAdHocFileAsync/DownloadAndExtractAsync côté service, qui peut lever une
    /// exception réseau propagée ici).
    /// 2026-07-26, retour utilisateur : le badge "Fichier externe (pas encore de DAT)"
    /// s'affichait sur ces releases sans que le bouton Play ne fasse jamais rien — ce
    /// système de téléchargement ad-hoc n'était branché que sur le chemin émulateur.
    /// </summary>
    private async Task<string?> ResolveAdHocMediaFileAsync()
    {
        if (Detail?.Release?.DemozooId == null || !IsExternalOnlyRelease) return null;

        var candidateLink = Detail.Links.Where(l => !l.IsVideo)
            .FirstOrDefault(l => l.IsMainFile && !string.IsNullOrEmpty(l.EffectiveDownloadUrl));
        if (candidateLink == null) return null;

        bool alreadyLocal = !string.IsNullOrEmpty(candidateLink.LocalFilePath)
            && System.IO.File.Exists(candidateLink.LocalFilePath);

        if (!alreadyLocal)
        {
            var prefs        = _prefsService != null ? await _prefsService.LoadAllAsync() : null;
            bool skipConfirm = prefs?.SkipExternalDownloadConfirm ?? false;
            if (!skipConfirm && !await ConfirmExternalDownloadAsync(candidateLink))
                return null; // annulé — on n'insiste pas

            IsBuildingRelease  = true;
            BuildStatusMessage = "Préparation…";
            BuildStatusPercent = 0;
        }

        try
        {
            var progress = new Progress<DemoBase.Core.DTOs.LaunchDownloadProgress>(p =>
            {
                // 2026-07-27 : même relais d'erreur que LaunchAsync (chemin émulateur
                // générique) — cf. commentaire là-bas — pour rester cohérent si ce chemin
                // Music/Graphics venait un jour à signaler une erreur via IsError.
                if (p.IsError)
                {
                    BuildErrorMessage = p.Message;
                    return;
                }
                BuildStatusMessage = p.Message;
                BuildStatusPercent = p.Percent;
            });
            return await _releaseService.DownloadAdHocFileAsync(Detail.Release.Id, progress);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PLAY] Téléchargement ad-hoc échoué : {ex.Message}");
            BuildErrorMessage = ex.Message;
            return null;
        }
        finally
        {
            // 2026-07-27 : ne pas refermer l'overlay automatiquement si une erreur y est
            // affichée — il reste ouvert jusqu'au clic sur OK (DismissBuildErrorCommand).
            if (BuildErrorMessage == null)
                IsBuildingRelease = false;
        }
    }

    /// <param name="allowAdHocDownload">
    /// 2026-07-26 : par défaut false — cette méthode est aussi appelée en fire-and-forget
    /// dès qu'une release Graphics est simplement affichée (LoadAsync, sans clic explicite
    /// de l'utilisateur), et un téléchargement ad-hoc y déclencherait une confirmation
    /// surprise juste en naviguant vers la release. Passé à true UNIQUEMENT depuis le clic
    /// explicite sur "Afficher" (LaunchAsync) — seul cas où proposer le téléchargement
    /// ad-hoc a du sens (retour utilisateur : badge "Fichier externe" affiché mais bouton
    /// sans effet sur les releases Graphics sans DAT).
    /// </param>
    private async Task ShowGraphicsAsync(int forReleaseId, bool allowAdHocDownload = false)    {
        if (Detail?.Release?.DemozooId == null || _prefsService == null) return;

        var datEntries = (await _releaseService.GetDatEntriesAsync(Detail.Release.DemozooId.Value))
            .Where(d => !d.IsCodeSourceEntry).ToList();

        // Guard anti race-condition : cette méthode est lancée en fire-and-forget depuis
        // LoadAsync (elle n'est jamais "await"-ée), donc si une navigation plus récente a eu
        // lieu pendant l'await ci-dessus, _lastLoadedReleaseId a déjà changé — on abandonne.
        // Sans ce garde, une tâche "en retard" pour l'ancienne release pouvait recréer
        // GraphicsViewer avec les images de l'ancienne release après que la nouvelle release
        // avait déjà pris sa place à l'écran (cf. GraphicsViewer = null fait dans LoadAsync).
        if (forReleaseId != _lastLoadedReleaseId) return;

        if (GraphicsViewer == null)
            GraphicsViewer = new DemoBase.App.ViewModels.GraphicsViewerViewModel();

        if (datEntries.Count == 0)
        {
            if (allowAdHocDownload)
            {
                var adHocPath = await ResolveAdHocMediaFileAsync();
                if (forReleaseId != _lastLoadedReleaseId) return;
                if (adHocPath != null)
                {
                    var recoilPathAdHoc = _prefsService != null
                        ? (await _prefsService.LoadAllAsync()).PathRecoil2Png
                        : null;
                    if (string.IsNullOrEmpty(recoilPathAdHoc) || !System.IO.File.Exists(recoilPathAdHoc))
                    {
                        var externalsAdHoc = System.IO.Path.Combine(
                            AppContext.BaseDirectory, "Externals", "RECOIL", "recoil2png.exe");
                        if (System.IO.File.Exists(externalsAdHoc))
                            recoilPathAdHoc = externalsAdHoc;
                    }
                    GraphicsViewer.SetRecoilPath(recoilPathAdHoc);
                    if (forReleaseId != _lastLoadedReleaseId) return;
                    await GraphicsViewer.LoadAsync(adHocPath);
                    return;
                }
            }
            GraphicsViewer.StatusMessage = DemoBase.App.Services.LocalizationService.Get("Msg_NoAudioDat");
            return;
        }

        var prefs = await _prefsService.LoadAllAsync();
        if (forReleaseId != _lastLoadedReleaseId) return;

        // Utiliser le DAT sélectionné par l'utilisateur (SelectedDatEntry) si disponible,
        // sinon le premier DAT dont le fichier existe sur disque.
        string? zipPath = null;
        if (SelectedDatEntry != null)
        {
            var candidate = System.IO.Path.Combine(prefs.ResolvedPathReleases, SelectedDatEntry.RomPath);
            if (System.IO.File.Exists(candidate)) zipPath = candidate;
        }
        if (zipPath == null)
        {
            foreach (var dat in datEntries)
            {
                var candidate = System.IO.Path.Combine(prefs.ResolvedPathReleases, dat.RomPath);
                if (System.IO.File.Exists(candidate)) { zipPath = candidate; break; }
            }
        }

        if (zipPath == null)
        {
            GraphicsViewer.StatusMessage = $"Aucun fichier trouvé dans les DATs.";
            return;
        }

        // Chemin recoil2png.exe : préférence utilisateur, sinon Externals\RECOIL\recoil2png.exe
        var recoilPath = prefs.PathRecoil2Png;
        if (string.IsNullOrEmpty(recoilPath) || !System.IO.File.Exists(recoilPath))
        {
            var externals = System.IO.Path.Combine(AppContext.BaseDirectory, "Externals", "RECOIL", "recoil2png.exe");
            if (System.IO.File.Exists(externals))
                recoilPath = externals;
        }
        GraphicsViewer.SetRecoilPath(recoilPath);

        if (forReleaseId != _lastLoadedReleaseId) return;
        await GraphicsViewer.LoadAsync(zipPath);
    }

    /// <summary>
    /// Ouvre le viewer "Code Sources" (arborescence + aperçu texte, voir
    /// CodeSourceViewerViewModel) sur le premier ZIP "Sources Code" disponible sur disque — et
    /// tente un téléchargement si aucun n'est trouvé localement (voir bloc ci-dessous). Appelé
    /// uniquement au clic sur l'onglet "Code Sources" (OnSelectedTabIndexChanged), plus jamais
    /// automatiquement à l'ouverture de la release (demande utilisateur). Mêmes gardes anti
    /// race-condition que ShowGraphicsAsync (forReleaseId comparé à _lastLoadedReleaseId à
    /// chaque await, puisque c'est un appel fire-and-forget).
    /// </summary>
    private async Task ShowCodeSourceAsync(int forReleaseId)
    {
        if (Detail?.Release?.DemozooId == null || _prefsService == null) return;

        var codeEntries = (await _releaseService.GetDatEntriesAsync(Detail.Release.DemozooId.Value))
            .Where(d => d.IsCodeSourceEntry).ToList();

        if (forReleaseId != _lastLoadedReleaseId) return;
        if (codeEntries.Count == 0) return;

        if (CodeSourceViewer == null)
            CodeSourceViewer = new DemoBase.App.ViewModels.CodeSourceViewerViewModel();

        var prefs = await _prefsService.LoadAllAsync();
        if (forReleaseId != _lastLoadedReleaseId) return;

        string? zipPath = FindLocalCodeSourceZip(codeEntries, prefs);

        // Zip absent localement : EnsureReleaseFilesAvailableAsync (bouton Lancer) ignore
        // désormais les entrées "Sources Code" pour décider SI on télécharge (elles ne
        // doivent pas déclencher/bloquer un téléchargement pour le lancement) — mais ça ne
        // doit pas dire "on ne télécharge JAMAIS le code source". On tente donc ici, à la
        // demande (clic sur l'onglet), le même ReleaseBuilderService.TryBuildAsync : il
        // parcourt TOUS les DatEntry de la release (Code Sources compris, vérifié par
        // relecture — preferredDatEntryId ne sert qu'à départager plusieurs sets déjà
        // complets, jamais à en exclure), donc capable de compléter spécifiquement le set
        // Code Sources même si c'est le seul manquant.
        if (zipPath == null && _releaseBuilderService != null)
        {
            CodeSourceViewer.IsLoading     = true;
            CodeSourceViewer.StatusMessage = "Téléchargement du code source…";
            try
            {
                var result = await Task.Factory.StartNew(
                    () => _releaseBuilderService.TryBuildAsync(
                        Detail.Release.DemozooId.Value, null,
                        preferredDatEntryId: codeEntries[0].Id),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();

                if (forReleaseId != _lastLoadedReleaseId) return;

                LastBuildFoundRomIds = result.FoundRomIds != null
                    ? new HashSet<int>(result.FoundRomIds) : new HashSet<int>();

                if (result.Success)
                {
                    DemoBase.App.Converters.DatEntryStatusToColorConverter.ClearCache();
                    await LoadDownloadMismatchesAsync(); // recharge aussi CodeSourcesWithMismatch (fond vert)
                    prefs   = await _prefsService.LoadAllAsync();
                    zipPath = FindLocalCodeSourceZip(codeEntries, prefs);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CODESRC] Téléchargement échoué : {ex.Message}");
            }
            finally
            {
                CodeSourceViewer.IsLoading = false;
            }
        }

        if (forReleaseId != _lastLoadedReleaseId) return;

        if (zipPath == null)
        {
            CodeSourceViewer.StatusMessage = "Aucun fichier de code source trouvé (téléchargement infructueux).";
            return;
        }

        await CodeSourceViewer.LoadAsync(zipPath);
    }

    private static string? FindLocalCodeSourceZip(
        List<DemoBase.Core.Models.DatEntry> codeEntries, DemoBase.Data.AppPreferences prefs)
    {
        foreach (var dat in codeEntries)
        {
            var candidate = System.IO.Path.Combine(prefs.ResolvedPathReleases, dat.RomPath);
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private async Task PlayMusicReleaseAsync()
    {
        if (_trackerService == null || Detail?.Release?.DemozooId == null) return;

        System.Diagnostics.Debug.WriteLine(
            $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] PlayMusicReleaseAsync ENTER " +
            $"DemozooId={Detail.Release.DemozooId} thread={Environment.CurrentManagedThreadId}");
        System.Diagnostics.Debug.WriteLine($"[PLAY] PlayMusicReleaseAsync: DemozooId={Detail.Release.DemozooId}");

        var datEntries = (await _releaseService.GetDatEntriesAsync(Detail.Release.DemozooId.Value))
            .Where(d => !d.IsCodeSourceEntry).ToList();
        System.Diagnostics.Debug.WriteLine($"[PLAY] DAT entries trouvés: {datEntries.Count}");

        var prefs = _prefsService != null
            ? await _prefsService.LoadAllAsync()
            : new DemoBase.Data.AppPreferences();
        System.Diagnostics.Debug.WriteLine($"[PLAY] PathReleases={prefs.ResolvedPathReleases}");

        var releaseId = Detail.Release.Id;
        var paths = new List<string>();
        int datIndex = 0;

        // Essayer le set explicitement sélectionné (bouton "Use" dans l'onglet
        // Files) en premier — sans ce tri, la boucle ci-dessous prenait le
        // premier ZIP trouvé sur le disque dans l'ordre naturel de la base,
        // ignorant silencieusement le choix de l'utilisateur dès qu'un set
        // antérieur dans cet ordre existait déjà localement.
        var orderedDatEntries = SelectedDatEntry != null
            ? datEntries.OrderByDescending(d => d.Id == SelectedDatEntry.Id).ToList()
            : datEntries;

        // 2026-07-26 : liste des .zip à scanner — normalement dérivée des DatEntry
        // (comme avant), mais si la release n'a AUCUN DatEntry et possède un lien de
        // téléchargement direct Demozoo (IsExternalOnlyRelease), tente un téléchargement
        // ad-hoc (même mécanisme que le bouton "Lancer" générique — confirmation +
        // overlay de progression) et scanne le zip obtenu exactement comme s'il
        // s'agissait d'un fichier DAT résolu. Retour utilisateur : badge "Fichier externe
        // (pas encore de DAT)" affiché mais bouton Play sans aucun effet sur les
        // releases Music sans DAT — ce système n'était branché que sur le chemin
        // émulateur générique.
        var zipPathsToScan = orderedDatEntries
            .Select(d => System.IO.Path.Combine(prefs.ResolvedPathReleases, d.RomPath))
            .ToList();
        if (zipPathsToScan.Count == 0)
        {
            var adHocZipPath = await ResolveAdHocMediaFileAsync();
            if (adHocZipPath != null) zipPathsToScan.Add(adHocZipPath);
        }

        foreach (var zipPath in zipPathsToScan)
        {
            System.Diagnostics.Debug.WriteLine($"[PLAY] ZIP: {zipPath} exists={System.IO.File.Exists(zipPath)}");
            if (!System.IO.File.Exists(zipPath)) continue;

            // Dossier court (Id + index) plutôt que le titre complet — cf. bug MAX_PATH.
            var tempDir = System.IO.Path.Combine(
                DemoBase.App.Services.WorkingPaths.GetSubdir("Tracker"),
                $"mus_{releaseId}_{datIndex++}");
            System.IO.Directory.CreateDirectory(tempDir);

            // Valider le ZIP avant de l'ouvrir — un téléchargement interrompu
            // produit un fichier sans End of Central Directory → InvalidDataException
            System.IO.Compression.ZipArchive zipArchive;
            try { zipArchive = System.IO.Compression.ZipFile.OpenRead(zipPath); }
            catch (System.IO.InvalidDataException)
            {
                System.Diagnostics.Debug.WriteLine($"[PLAY] ZIP corrompu, supprimé : {zipPath}");
                try { System.IO.File.Delete(zipPath); } catch { }
                continue;
            }

            using var zip = zipArchive;
            var allEntries = zip.Entries.Select(e => e.Name).ToList();
            System.Diagnostics.Debug.WriteLine($"[PLAY] ZIP contient: {string.Join(", ", allEntries)}");

            // Si le ZIP contient à la fois un fichier tracker (mod/xm/s3m…) ET des
            // fichiers audio convertis (wav/mp3/ogg), ne jouer que le tracker.
            // Le wav/mp3/ogg est une conversion du tracker — jouer les deux serait redondant.
            var audioConverted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".wav", ".mp3", ".ogg", ".flac", ".aiff", ".m4a" };
            var playableEntries = zip.Entries.Where(e => IsPlayableFilename(e.Name)).ToList();
            var hasTracker = playableEntries.Any(e =>
                !audioConverted.Contains(System.IO.Path.GetExtension(e.Name)));

            // 2026-07-31, retour utilisateur (release "Whatever", type "Musique Trackers" —
            // pas "Executable Music") : "si je clique 'lire' il essaie de lancer le fichier
            // .exe au lieu de jouer le fichier xm contenu dans l'archive [...] il faut donc
            // prioriser le .xm". Root cause — ce test déclenchait le chemin ExeMusicPlayer
            // dès qu'UN SEUL .exe/.com/.bin traînait dans le zip, même à côté d'un vrai
            // tracker (ici RDENTIF.EXE/USEMETH.COM, des utilitaires sans rapport, à côté de
            // WHATEVER.XM) — AVANT même de regarder s'il y avait un tracker jouable.
            // Déplacé après le calcul de hasTracker et restreint au cas où aucun tracker
            // n'a été trouvé : l'exe ne reste prioritaire que sur les conversions mp3/wav
            // (cas "Executable Music" réel, zip ne contenant qu'un exe + sa conversion),
            // jamais sur un vrai fichier tracker.
            var exeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".exe", ".com", ".bin" };
            if (!hasTracker && zip.Entries.Any(e => exeExtensions.Contains(
                    System.IO.Path.GetExtension(e.Name).ToLowerInvariant())))
            {
                System.Diagnostics.Debug.WriteLine("[PLAY] ZIP contient un exe et aucun tracker → délégué à ExeMusicPlayer");
                paths.Clear(); // ignorer tout ce qui a pu être ajouté avant
                break;         // sortir de la boucle — l'exe sera trouvé dans la 2ème passe
            }

            var entriesToPlay = hasTracker
                ? playableEntries.Where(e =>
                    !audioConverted.Contains(System.IO.Path.GetExtension(e.Name)))
                : playableEntries;

            foreach (var entry in entriesToPlay)
            {
                var dest = NormalizeTrackerFilename(entry.Name, tempDir);
                System.Diagnostics.Debug.WriteLine($"[PLAY] Extraction: {entry.Name} → {dest}");
                if (!System.IO.File.Exists(dest))
                    entry.ExtractToFile(dest, overwrite: true);
                if (System.IO.File.Exists(dest))
                {
                    paths.Add(dest);
                    System.Diagnostics.Debug.WriteLine($"[PLAY] Ajouté à la playlist: {dest}");
                }
            }
        }

        // 2026-07-30, retour utilisateur (TFMX "Quik And Silva Ingame") : voir
        // CompanionFilePairs/ResolveCompanionFiles — quand mdat.* et smpl.* proviennent de DEUX
        // DatEntry différents, ils atterrissent dans DEUX tempDir séparés (un par zip scanné
        // ci-dessus), ce qui casse la recherche par répertoire déjà en place côté UADE
        // (TrackerPlayer.Core/ExternalPlayers.cs). On copie ici le compagnon à côté du fichier
        // principal, en le cherchant dans TOUS les zips DAT de la release.
        ResolveCompanionFiles(paths, zipPathsToScan);

        System.Diagnostics.Debug.WriteLine($"[PLAY] Playlist finale: {paths.Count} fichiers");
        foreach (var p in paths)
            System.Diagnostics.Debug.WriteLine($"[PLAY]   → {p}");

        // ── Cas spécial : musiques génératives sous forme d'exécutable (.exe/.com/.bin) ──
        // Si aucun fichier tracker n'a été trouvé, chercher un exécutable dans les ZIPs.
        // Ces musiques sont des programmes autonomes — on les lance et on attend leur fin.
        if (paths.Count == 0)
        {
            var exeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".exe", ".com", ".bin" };

            // "Executable Music" existe aussi sous forme de ROM console (constaté : SNES .sfc,
            // ex. Molive - A Rude Interruption). Contrairement à .exe/.com/.bin, ce n'est pas un
            // exécutable natif Windows lançable via Process.Start (ExeMusicPlayer) — il faut
            // passer par l'émulateur de la plateforme (Mesen pour SNES), cf. plus bas.
            var romExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".sfc", ".smc" };

            string? exePath = null;
            var isRomMusic = false;
            foreach (var zipPath in zipPathsToScan)
            {
                if (!System.IO.File.Exists(zipPath)) continue;

                var ext = System.IO.Path.GetExtension(zipPath).ToLowerInvariant();
                if (exeExtensions.Contains(ext) || romExtensions.Contains(ext))
                {
                    // Le DAT pointe directement vers un exe/ROM (pas un zip)
                    exePath   = zipPath;
                    isRomMusic = romExtensions.Contains(ext);
                    break;
                }

                if (ext != ".zip") continue;
                using var zip2 = System.IO.Compression.ZipFile.OpenRead(zipPath);
                var exeEntries = zip2.Entries
                    .Where(e => exeExtensions.Contains(
                        System.IO.Path.GetExtension(e.Name).ToLowerInvariant())
                        || romExtensions.Contains(
                        System.IO.Path.GetExtension(e.Name).ToLowerInvariant()))
                    .ToList();
                if (exeEntries.Count == 0) continue;

                // Si plusieurs exe : préférer la version finale (sans -compo/-intro/-compat/-wavwriter etc.)
                // Score : plus bas = moins préféré
                static int ExeScore(string name)
                {
                    var n = name.ToLowerInvariant();
                    if (n.Contains("-compo"))      return -3;
                    if (n.Contains("-intro"))      return -2;
                    if (n.Contains("-compat"))     return -2;
                    if (n.Contains("-wavwriter"))  return -5;
                    if (n.Contains("wavwriter"))   return -5;
                    if (n.Contains("-player"))     return -1;
                    return 0; // version finale par défaut
                }
                var exeEntry = exeEntries
                    .OrderByDescending(e => ExeScore(e.Name))
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .First();
                if (exeEntry == null) continue;

                var tempDir2 = System.IO.Path.Combine(
                    DemoBase.App.Services.WorkingPaths.GetSubdir("Tracker"),
                    $"mus_{releaseId}_exe");
                System.IO.Directory.CreateDirectory(tempDir2);
                exePath   = System.IO.Path.Combine(tempDir2, exeEntry.Name);
                isRomMusic = romExtensions.Contains(
                    System.IO.Path.GetExtension(exeEntry.Name).ToLowerInvariant());
                if (!System.IO.File.Exists(exePath))
                    exeEntry.ExtractToFile(exePath, overwrite: true);
                break;
            }

            if (exePath != null && System.IO.File.Exists(exePath))
            {
                if (isRomMusic)
                {
                    // ROM console (ex. .sfc SNES) : pas un exécutable natif Windows —
                    // Process.Start (ExeMusicPlayer) échouerait. Passe par le pipeline de
                    // lancement générique, qui route vers l'émulateur de la plateforme
                    // (Mesen pour SNES) exactement comme le bouton "Lancer" d'une demo.
                    System.Diagnostics.Debug.WriteLine($"[PLAY] ROM music détectée → lancement via émulateur : {exePath}");
                    await _releaseService.LaunchAsync(Detail!.Release.Id, Detail.DefaultEmulatorConfig?.Id,
                        romPathOverride: exePath);
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"[PLAY] Exe music détecté → {exePath}");
                if (SoundtrackPlayer == null)
                    SoundtrackPlayer = new DemoBase.App.Views.Releases.SoundtrackPlayerView(_trackerService);
                await SoundtrackPlayer.Vm.LoadExeMusicAsync(exePath);
                return;
            }

            DemoBase.App.Controls.StatusScrollerControl.Post(
                DemoBase.App.Services.LocalizationService.Get("Msg_NoAudioDat"), isError: true);
            // Signaler l'échec — permet au MediaBrowser de passer à la release suivante
            System.Diagnostics.Debug.WriteLine(
                $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] PlaybackStartFailed.Invoke() [site=NoAudioDat] " +
                $"DemozooId={Detail.Release.DemozooId} thread={Environment.CurrentManagedThreadId}");
            PlaybackStartFailed?.Invoke();
            return;
        }

        if (SoundtrackPlayer == null)
            SoundtrackPlayer = new DemoBase.App.Views.Releases.SoundtrackPlayerView(_trackerService);

        await SoundtrackPlayer.Vm.LoadFilesAsync(paths);
    }

    [RelayCommand]
    private async Task ToggleMusicFavorite()
    {
        if (_favService == null || Detail?.Release == null || !IsMusic) return;
        var release = Detail.Release;

        // Chercher le fichier DAT principal (jamais une entrée "Code Sources")
        var dat = Detail.DatFiles.FirstOrDefault(d => !d.IsCodeSourceEntry);
        var rom = dat?.Roms.FirstOrDefault(r =>
            DemoBase.Core.DTOs.TrackerExtensions.IsPlayable(r.Name));

        var authorNames = Detail?.Authors.Any() == true
            ? string.Join(", ", Detail.Authors.Select(a => a.ReleaserName))
            : release.AuthorNamesCache;

        await _favService.ToggleAsync(new DemoBase.Core.Models.FavoriteSoundtrack
        {
            SoundtrackDemozooId = release.DemozooId ?? 0,
            Title               = release.Title,
            AuthorNames         = authorNames,
            RomName             = rom?.Name,
            ZipPath             = dat?.RomPath,
            ReleaseTitle        = null,  // c'est la release elle-même, pas une parente
        });

        IsMusicFavorite = await _favService.IsFavoriteAsync(release.DemozooId ?? 0);
        DemoBase.App.Controls.StatusScrollerControl.Post(
            IsMusicFavorite ? $"★ {release.Title} {DemoBase.App.Services.LocalizationService.Get("Msg_Added")}" : $"☆ {release.Title} {DemoBase.App.Services.LocalizationService.Get("Msg_Removed")}");
    }

    private async Task CheckGraphicFavoriteAsync(int demozooId)
    {
        IsGraphicFavorite = await _favGraphicService!.IsFavoriteAsync(demozooId);
    }

    [RelayCommand]
    private async Task ToggleGraphicFavorite()
    {
        if (_favGraphicService == null || Detail?.Release == null || !IsGraphics) return;
        var release = Detail.Release;

        // Récupérer le fichier sélectionné dans le viewer si disponible
        var fileInZip = GraphicsViewer?.SelectedEntry?.Name;
        var dat       = Detail.DatFiles.FirstOrDefault(d => !d.IsCodeSourceEntry);

        var authorNames = Detail?.Authors.Any() == true
            ? string.Join(", ", Detail.Authors.Select(a => a.ReleaserName))
            : release.AuthorNamesCache;

        await _favGraphicService.ToggleAsync(new DemoBase.Core.Models.FavoriteGraphic
        {
            ReleaseDemozooId = release.DemozooId ?? 0,
            Title            = release.Title,
            AuthorNames      = authorNames,
            ZipPath          = dat?.RomPath,
            FileInZip        = fileInZip,
        });

        IsGraphicFavorite = await _favGraphicService.IsFavoriteAsync(release.DemozooId ?? 0);
        DemoBase.App.Controls.StatusScrollerControl.Post(
            IsGraphicFavorite
                ? $"🖼 ★ {release.Title} {DemoBase.App.Services.LocalizationService.Get("Msg_Added")}"
                : $"🖼 ☆ {release.Title} {DemoBase.App.Services.LocalizationService.Get("Msg_Removed")}");
    }

    [RelayCommand]
    private void EditRelease()
    {
        if (Detail?.Release == null) return;
        _navigation.NavigateTo<ReleaseEditViewModel>(Detail.Release.Id);
    }

    [RelayCommand]
    private void FilterByType(int? releaseTypeId)
    {
        if (releaseTypeId == null || Detail?.Release == null) return;
        _navigation.NavigateTo<ReleaseListViewModel>(
            parameter: releaseTypeId,
            tag:       $"type:{Detail.Release.ReleaseType?.Name ?? ""}");
    }

    [RelayCommand]
    private void FilterByPlatform(Platform? platform)
    {
        if (platform == null) return;
        _navigation.NavigateTo<ReleaseListViewModel>(
            parameter: platform.Id,
            tag:        platform.Name);
    }

    [RelayCommand]
    private void OpenVideoLink(DemoBase.App.ViewModels.VideoLinkDto? link)
    {
        if (link == null || string.IsNullOrEmpty(link.Url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = link.Url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    // Réactivé le 2026-07-24 à la demande de l'utilisateur (avait été masqué dans une
    // session antérieure non documentée) — sert notamment de diagnostic pour le bug du
    // badge HasNoFile : permet de voir dans l'onglet Infos quels liens (Url/LinkClass/
    // IsMainFile) sont réellement présents sur une release, afin de comparer avec la
    // logique de calcul de HasNoFile (Links.Any(l => l.IsMainFile)).
    [RelayCommand]
    private void OpenReleaseLink(DemoBase.Core.Models.ReleaseLink? link)
    {
        // 2026-07-25 : EffectiveDownloadUrl plutôt que Url — un lien "BaseUrl" (Url
        // NULL, LinkParameter = URL complète) restait sans effet au clic sinon, cf.
        // ReleaseLink.EffectiveDownloadUrl.
        var url = link?.EffectiveDownloadUrl;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    private void OpenRelease(int releaseId) =>
        _navigation.NavigateTo<ReleaseDetailViewModel>(releaseId);

    [RelayCommand]
    private async Task ToggleFavoriteSound(int soundtrackId)
    {
        System.Diagnostics.Debug.WriteLine($"[FAV] ToggleFavoriteSound called, soundtrackId={soundtrackId}");

        if (_favService == null)
        {
            System.Diagnostics.Debug.WriteLine("[FAV] _favService is NULL — FavoriteSoundtrackService non injecté !");
            DemoBase.App.Controls.StatusScrollerControl.Post("FavoriteSoundtrackService non disponible.", isError: true);
            return;
        }

        var st = Detail?.Soundtracks.FirstOrDefault(s => s.SoundtrackId == soundtrackId);
        if (st == null)
        {
            System.Diagnostics.Debug.WriteLine($"[FAV] SoundtrackDto non trouvé pour id={soundtrackId}");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[FAV] st.Title={st.Soundtrack?.Title} IsFavorite={st.IsFavorite}");

        try
        {
            await _favService.ToggleAsync(new DemoBase.Core.Models.FavoriteSoundtrack
            {
                SoundtrackDemozooId = st.SoundtrackId,
                Title               = st.Soundtrack?.Title ?? "",
                AuthorNames         = st.AuthorNames,
                RomName             = st.RomName,
                ZipPath             = st.ZipPath,
                ReleaseTitle        = st.ReleaseTitle,
            });
            System.Diagnostics.Debug.WriteLine($"[FAV] ToggleAsync done, new IsFavorite={!st.IsFavorite}");

            // Mettre à jour l'état dans le DTO
            st.IsFavorite = !st.IsFavorite;

            // Forcer le rafraîchissement du binding (Detail est un ObservableProperty)
            OnPropertyChanged(nameof(Detail));

            DemoBase.App.Controls.StatusScrollerControl.Post(
                st.IsFavorite ? $"★ {st.Soundtrack?.Title} {DemoBase.App.Services.LocalizationService.Get("Msg_Added")}" : $"☆ {st.Soundtrack?.Title} {DemoBase.App.Services.LocalizationService.Get("Msg_Removed")}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FAV] Exception: {ex.Message}");
            DemoBase.App.Controls.StatusScrollerControl.Post($"Erreur favori : {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private void OpenSoundtrackRelease(int soundtrackId)
    {
        var st = Detail?.Soundtracks.FirstOrDefault(s => s.SoundtrackId == soundtrackId);
        if (st?.Soundtrack == null) return;
        _navigation.NavigateTo<ReleaseDetailViewModel>(st.Soundtrack.Id);
    }

    [RelayCommand]
    private async Task PlaySoundtrack(int soundtrackId)
    {
        System.Diagnostics.Debug.WriteLine($"[PLAY] PlaySoundtrack called, soundtrackId={soundtrackId}");

        if (_trackerService == null)
        {
            System.Diagnostics.Debug.WriteLine("[PLAY] _trackerService is NULL — TrackerService non injecté !");
            DemoBase.App.Controls.StatusScrollerControl.Post("TrackerService non disponible.", isError: true);
            return;
        }
        var st = Detail?.Soundtracks.FirstOrDefault(s => s.SoundtrackId == soundtrackId);
        if (st == null)
        {
            System.Diagnostics.Debug.WriteLine($"[PLAY] SoundtrackDto non trouvé pour id={soundtrackId}");
            return;
        }
        System.Diagnostics.Debug.WriteLine($"[PLAY] st.HasPlayableRom={st.HasPlayableRom} ZipPath={st.ZipPath} RomName={st.RomName}");

        // Priorité : ROM dans les DATs
        if (st.HasPlayableRom && st.ZipPath != null && st.RomName != null)
        {
            await PlayFromDatAsync(st.SoundtrackId, st.ZipPath, st.RomName);
            return;
        }

        // Fallback : lien direct
        if (st.Soundtrack?.Links == null) return;
        var link = st.Soundtrack.Links.FirstOrDefault(l => l.IsLocalCopy)
                ?? st.Soundtrack.Links.FirstOrDefault();
        if (link?.LocalFilePath == null) return;

        if (SoundtrackPlayer == null)
            SoundtrackPlayer = new DemoBase.App.Views.Releases.SoundtrackPlayerView(_trackerService);
        await SoundtrackPlayer.OpenAsync(link.LocalFilePath);
    }

    /// <summary>
    /// Corrige les noms inversés type "MOD.mysong" → "mysong.mod"
    /// Formats concernés : MOD, XM, S3M, IT, STM, DBM, SNDH, 669, MTM
    /// </summary>
    private static string NormalizeTrackerFilename(string name, string destDir)
    {
        var normalized = DemoBase.Core.DTOs.TrackerExtensions.NormalizeFilename(name);
        if (normalized != name)
            System.Diagnostics.Debug.WriteLine($"[PLAY] Renommage: {name} → {normalized}");
        return System.IO.Path.Combine(destDir, normalized);
    }

    private static bool IsPlayableFilename(string name)
        => DemoBase.Core.DTOs.TrackerExtensions.IsPlayable(name);

    /// <summary>
    /// 2026-07-30, retour utilisateur (release "Quik And Silva Ingame", format TFMX) : certains
    /// formats nécessitent un fichier compagnon présent dans le MÊME répertoire que le fichier
    /// principal pour être jouables (cf. UadePlayer.SetCwdToFileDir, TrackerPlayer.Core/
    /// ExternalPlayers.cs, qui fait chercher à UADE "smpl.&lt;suffixe&gt;" à côté de
    /// "mdat.&lt;suffixe&gt;" — mais uniquement dans le même dossier). Problème : certaines releases distribuent ces
    /// deux fichiers sur DEUX DatEntry/liens de téléchargement différents, donc extraits par la
    /// boucle ci-dessus dans deux dossiers temporaires séparés. On cherche ici, pour chaque
    /// fichier "déclencheur" (ex. mdat.*) déjà extrait, son compagnon (ex. smpl.*) dans TOUS les
    /// zips DAT de la release (pas seulement celui d'où il vient) et on le copie dans le MÊME
    /// dossier temporaire que le fichier principal — la logique de recherche déjà en place côté
    /// UADE le trouve alors normalement, sans aucune modification de ce code plus délicat.
    /// Liste CompanionFilePairs volontairement générique pour couvrir d'autres cas similaires à
    /// l'avenir ("il y en aura peut être d'autres [cas particuliers]", retour utilisateur).
    /// </summary>
    private static void ResolveCompanionFiles(List<string> extractedPaths, List<string> allZipPaths)
    {
        foreach (var mainPath in extractedPaths.ToList())
        {
            var fileName = System.IO.Path.GetFileName(mainPath);

            foreach (var (triggerPrefix, companionPrefix) in CompanionFilePairs)
            {
                if (!fileName.StartsWith(triggerPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var suffix        = fileName[triggerPrefix.Length..];
                var companionName = companionPrefix + suffix;
                var destDir       = System.IO.Path.GetDirectoryName(mainPath)!;
                var companionDest = System.IO.Path.Combine(destDir, companionName);

                if (System.IO.File.Exists(companionDest))
                    break; // déjà présent (mdat + smpl venaient du même DatEntry) — rien à faire

                System.Diagnostics.Debug.WriteLine(
                    $"[PLAY] '{fileName}' nécessite le compagnon '{companionName}' — recherche dans les autres sets…");

                foreach (var zipPath in allZipPaths)
                {
                    if (!System.IO.File.Exists(zipPath)) continue;
                    try
                    {
                        using var zip  = System.IO.Compression.ZipFile.OpenRead(zipPath);
                        var       zEnt = zip.Entries.FirstOrDefault(e =>
                            string.Equals(e.Name, companionName, StringComparison.OrdinalIgnoreCase));
                        if (zEnt == null) continue;

                        zEnt.ExtractToFile(companionDest, overwrite: true);
                        System.Diagnostics.Debug.WriteLine(
                            $"[PLAY] Compagnon trouvé dans {zipPath} → copié vers {companionDest}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[PLAY] Erreur lecture {zipPath} : {ex.Message}");
                    }
                }

                if (!System.IO.File.Exists(companionDest))
                    System.Diagnostics.Debug.WriteLine(
                        $"[PLAY] Compagnon '{companionName}' introuvable dans aucun set de la release.");

                break; // une seule règle peut matcher un nom donné
            }
        }
    }

    private async Task PlayFromDatAsync(int soundtrackDemozooId, string zipPath, string romName)
    {
        System.Diagnostics.Debug.WriteLine($"[PLAY] PlayFromDatAsync: zipPath={zipPath} romName={romName}");
        if (_trackerService == null) return;
        try
        {
            var prefs   = _prefsService != null
                ? await _prefsService.LoadAllAsync()
                : new DemoBase.Data.AppPreferences();
            var fullZip = System.IO.Path.Combine(prefs.ResolvedPathReleases, zipPath);
            System.Diagnostics.Debug.WriteLine($"[PLAY] PathReleases={prefs.ResolvedPathReleases}");
            System.Diagnostics.Debug.WriteLine($"[PLAY] fullZip={fullZip}");
            System.Diagnostics.Debug.WriteLine($"[PLAY] ZIP exists={System.IO.File.Exists(fullZip)}");

            // Le ZIP attendu (ZipPath vient du DAT, cf. Services.cs.LoadSoundtracksAsync) n'est
            // pas forcément déjà présent sur le disque — HasPlayableRom ne teste que la présence
            // d'une entrée jouable *dans le DAT*, pas le téléchargement effectif du fichier.
            // Sans ce bloc, le bouton "Lire" d'un soundtrack jamais téléchargé échouait
            // silencieusement avec "ZIP introuvable" au lieu de le télécharger comme le fait
            // "Lancer" sur la fiche release (cf. EnsureReleaseFilesAvailableAsync).
            System.Diagnostics.Debug.WriteLine(
                $"[PLAY] _releaseBuilderService={(_releaseBuilderService != null ? "OK" : "NULL")}");
            if (!System.IO.File.Exists(fullZip) && _releaseBuilderService != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PLAY] ZIP manquant, tentative de téléchargement du soundtrack DemozooId={soundtrackDemozooId}…");
                IsBuildingRelease   = true;
                BuildStatusMessage  = "Téléchargement du soundtrack…";
                BuildStatusPercent  = 0;
                try
                {
                    var buildProgress = new Progress<DemoBase.App.Services.ReleaseBuilder.BuildProgress>(p =>
                    {
                        BuildStatusMessage = p.Message;
                        BuildStatusPercent = p.Percent;
                    });
                    var buildResult = await Task.Factory.StartNew(
                        () => _releaseBuilderService.TryBuildAsync(soundtrackDemozooId, buildProgress),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default).Unwrap();
                    System.Diagnostics.Debug.WriteLine(
                        $"[PLAY] ReleaseBuilder (soundtrack): success={buildResult.Success} " +
                        $"files={buildResult.FilesFound}/{buildResult.FilesNeeded} error={buildResult.Error}");
                    LastBuildFoundRomIds = buildResult.FoundRomIds != null
                        ? new HashSet<int>(buildResult.FoundRomIds) : new HashSet<int>();
                    if (!buildResult.Success)
                    {
                        DemoBase.App.Controls.StatusScrollerControl.Post(
                            buildResult.Error ?? "Téléchargement du soundtrack échoué.", isError: true);
                        return;
                    }
                }
                finally { IsBuildingRelease = false; }
            }

            if (!System.IO.File.Exists(fullZip))
            {
                DemoBase.App.Controls.StatusScrollerControl.Post(
                    $"ZIP introuvable : {fullZip}", isError: true);
                return;
            }

            // Dossier court (hash du zipPath) plutôt que le nom complet — cf. bug MAX_PATH.
            var zipHash = System.Convert.ToHexString(
                System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(zipPath)))[..8].ToLowerInvariant();
            var tempDir = System.IO.Path.Combine(
                DemoBase.App.Services.WorkingPaths.GetSubdir("Tracker"),
                "mus_" + zipHash);
            System.IO.Directory.CreateDirectory(tempDir);

            var extractedPath = System.IO.Path.Combine(tempDir, romName);
            System.Diagnostics.Debug.WriteLine($"[PLAY] extractedPath={extractedPath}");
            if (!System.IO.File.Exists(extractedPath))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(fullZip);
                System.Diagnostics.Debug.WriteLine($"[PLAY] ZIP entries: {string.Join(", ", zip.Entries.Select(e => e.FullName))}");
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, romName, StringComparison.OrdinalIgnoreCase)
                    || e.FullName.EndsWith(romName, StringComparison.OrdinalIgnoreCase));
                System.Diagnostics.Debug.WriteLine($"[PLAY] entry found={entry?.FullName ?? "NULL"}");
                if (entry != null)
                {
                    extractedPath = NormalizeTrackerFilename(entry.Name, tempDir);
                    if (!System.IO.File.Exists(extractedPath))
                        entry.ExtractToFile(extractedPath, overwrite: true);
                }
            }

            System.Diagnostics.Debug.WriteLine($"[PLAY] extracted file exists={System.IO.File.Exists(extractedPath)}");
            if (!System.IO.File.Exists(extractedPath))
            {
                DemoBase.App.Controls.StatusScrollerControl.Post(
                    $"Extraction échouée : {romName}", isError: true);
                return;
            }
            System.Diagnostics.Debug.WriteLine($"[PLAY] Calling SoundtrackPlayer.OpenAsync({extractedPath})");

            if (SoundtrackPlayer == null)
                SoundtrackPlayer = new DemoBase.App.Views.Releases.SoundtrackPlayerView(_trackerService);

            // Laisse le binding WPF (ContentControl) attacher réellement le nouveau
            // contrôle à l'arbre visuel avant de lancer la conversion (potentiellement
            // longue pour certains formats type SNDH/ICE!) — sans ça, l'indicateur
            // "Conversion en cours…" risque de ne jamais être rendu avant la fin.
            await Task.Yield();

            await SoundtrackPlayer.OpenAsync(extractedPath);
        }
        catch (Exception ex)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                $"Erreur lecture soundtrack : {ex.Message}", isError: true);
        }
    }



    [RelayCommand]
    private void OpenReleaser(int releaserId) =>
        _navigation.NavigateTo<ReleaserDetailViewModel>(releaserId);

    [RelayCommand]
    private void OpenParty(int partyId) =>
        _navigation.NavigateTo<PartyDetailViewModel>(partyId);

    [RelayCommand]
    private async Task OpenDatFile(DemoBase.Core.Models.DatEntry entry)
    {
        if (_prefsService == null) return;
        var prefs   = await _prefsService.LoadAllAsync();
        var zipPath = System.IO.Path.Combine(prefs.ResolvedPathReleases, entry.RomPath);
        if (!System.IO.File.Exists(zipPath))
        {
            DemoBase.App.Controls.StatusScrollerControl.Post($"Fichier introuvable : {zipPath}");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = zipPath,
                UseShellExecute = true   // ouvre avec l'application par défaut (ex: 7-zip, WinRAR)
            });
        }
        catch (Exception ex)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post($"Erreur : {ex.Message}");
        }
    }

    [RelayCommand]
    private void DownloadScreenshots() =>
        _navigation.NavigateTo<ScreenshotDownloadViewModel>();

    [RelayCommand]
    private async Task AddScreenshotAsync(string sourcePath)
    {
        if (Detail?.Release == null) return;
        await _mediaService.AddScreenshotAsync(Detail.Release.Id, sourcePath);
        await LoadAsync(Detail.Release.Id);
    }

    [RelayCommand]
    private async Task DeleteMediaAsync(int mediaId)
    {
        if (Detail?.Release == null) return;
        await _mediaService.DeleteMediaAsync(mediaId);
        await LoadAsync(Detail.Release.Id);
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Detail?.Release == null) return;
        Detail.Release.IsFavorite = !Detail.Release.IsFavorite;
        var dto = new UpdateReleaseDto
        {
            Id            = Detail.Release.Id,
            Title         = Detail.Release.Title,
            Supertype     = Detail.Release.Supertype,
            ReleaseTypeId = Detail.Release.ReleaseTypeId,
            ReleaseDate   = Detail.Release.ReleaseDate,
            IsFavorite    = Detail.Release.IsFavorite,
            DemozooUrl    = Detail.Release.DemozooUrl,
            PouetUrl      = Detail.Release.PouetUrl,
            CsdbUrl       = Detail.Release.CsdbUrl,
            Tags          = Detail.Release.Tags,
        };
        await _releaseService.UpdateAsync(Detail.Release.Id, dto);
        OnPropertyChanged(nameof(Detail));
    }

    // ── WinUAE / DosBoxX : réinitialiser le choix du fichier de démarrage ──────

    /// <summary>
    /// Indique si un fichier de démarrage a été mémorisé pour cette release
    /// (WinUAE HD ou DosBoxX multi-exe).
    /// </summary>
    [ObservableProperty] private bool _hasWinUAEStartupChoice;
    [ObservableProperty] private string? _winUAEStartupChoiceLabel;

    public async Task RefreshWinUAEStartupChoiceAsync()
    {
        if (_prefsService == null || Detail?.Release == null || Detail.DefaultEmulatorConfig == null)
        {
            HasWinUAEStartupChoice = false;
            WinUAEStartupChoiceLabel = null;
            return;
        }
        var configId  = Detail.DefaultEmulatorConfig.Id;
        var releaseId = Detail.Release.Id;

        // Chercher d'abord une clé WinUAE, puis DosBoxX
        var prefKeyWinUAE  = $"winuae_startup:{configId}:{releaseId}";
        var prefKeyDosBoxX = $"dosboxx_startup:{configId}:{releaseId}";
        var saved = await _prefsService.GetAsync(prefKeyWinUAE)
                 ?? await _prefsService.GetAsync(prefKeyDosBoxX);

        HasWinUAEStartupChoice   = !string.IsNullOrEmpty(saved);
        WinUAEStartupChoiceLabel = saved;
    }

    /// <summary>
    /// Bouton "Reset Files" (mode debug) : efface le fichier de démarrage mémorisé
    /// (WinUAE HD / DosBoxX) pour cette release, afin que le sélecteur de fichier
    /// réapparaisse au prochain lancement si l'utilisateur s'est trompé de choix.
    ///
    /// Efface la clé pour TOUS les profils candidats de la release (DefaultEmulatorConfig
    /// actuel + tous ceux listés dans AvailableProfilesForOverride), pas seulement le
    /// profil actuellement résolu : si un override de profil (cf. ApplyProfileOverrideAsync)
    /// a été appliqué puis retiré entretemps, le fichier a pu être mémorisé sous un
    /// EmulatorConfigId différent de celui actif maintenant — effacer large garantit que
    /// le reset fonctionne quel que soit le profil utilisé au moment du choix initial.
    /// </summary>
    [RelayCommand]
    private async Task ResetWinUAEStartupFileAsync()
    {
        if (_prefsService == null || Detail?.Release == null) return;
        var releaseId = Detail.Release.Id;

        var configIds = new HashSet<int>();
        if (Detail.DefaultEmulatorConfig != null) configIds.Add(Detail.DefaultEmulatorConfig.Id);
        foreach (var p in AvailableProfilesForOverride) configIds.Add(p.Id);

        foreach (var configId in configIds)
        {
            await _prefsService.SetAsync($"winuae_startup:{configId}:{releaseId}",  null);
            await _prefsService.SetAsync($"dosboxx_startup:{configId}:{releaseId}", null);
        }

        HasWinUAEStartupChoice   = false;
        WinUAEStartupChoiceLabel = null;
    }
}

// ─── Release Edit ─────────────────────────────────────────────────────────────

public partial class ReleaseEditViewModel : ObservableObject
{
    private readonly IReleaseService     _releaseService;
    private readonly IReleaseTypeService _releaseTypeService;
    private readonly INavigationService  _navigation;

    // ── Champs du formulaire ─────────────────────────────────────────────────

    [ObservableProperty] private int     _releaseId;
    [ObservableProperty] private string  _title           = string.Empty;
    [ObservableProperty] private string  _supertype       = "production";
    [ObservableProperty] private int?    _releaseTypeId;
    [ObservableProperty] private string  _releaseDate     = string.Empty;
    [ObservableProperty] private string  _notes           = string.Empty;
    [ObservableProperty] private string  _demozooUrl      = string.Empty;
    [ObservableProperty] private string  _pouetUrl        = string.Empty;
    [ObservableProperty] private string  _csdbUrl         = string.Empty;
    [ObservableProperty] private string  _tags            = string.Empty;
    [ObservableProperty] private bool    _isFavorite;
    [ObservableProperty] private int?    _rating;

    // ── État UI ───────────────────────────────────────────────────────────────

    [ObservableProperty] private bool    _isLoading;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool    _isNewRelease;

    // ── Données de référence ─────────────────────────────────────────────────

    [ObservableProperty] private ObservableCollection<ReleaseTypeDto> _releaseTypes = [];

    // Catégories Demozoo (valeurs fixes)
    public IReadOnlyList<string> Supertypes { get; } = ["production", "graphics", "music"];

    public string WindowTitle => IsNewRelease ? "Nouvelle release" : $"Éditer — {Title}";

    public ReleaseEditViewModel(
        IReleaseService     releaseService,
        IReleaseTypeService releaseTypeService,
        INavigationService  navigation)
    {
        _releaseService     = releaseService;
        _releaseTypeService = releaseTypeService;
        _navigation         = navigation;
    }

    // ── Chargement ───────────────────────────────────────────────────────────

    public async Task LoadAsync(int? releaseId)
    {
        IsLoading   = true;
        IsNewRelease = releaseId == null;
        ErrorMessage = null;

        try
        {
            // Charger la liste des types de release
            var types = await _releaseTypeService.GetAllAsync();
            ReleaseTypes = new ObservableCollection<ReleaseTypeDto>(types);

            if (releaseId.HasValue)
            {
                var detail = await _releaseService.GetDetailAsync(releaseId.Value);
                var r      = detail.Release;

                ReleaseId     = r.Id;
                Title         = r.Title;
                Supertype     = r.Supertype;
                ReleaseTypeId = r.ReleaseTypeId;
                ReleaseDate   = r.ReleaseDate ?? string.Empty;
                Notes         = r.Notes       ?? string.Empty;
                DemozooUrl    = r.DemozooUrl  ?? string.Empty;
                PouetUrl      = r.PouetUrl    ?? string.Empty;
                CsdbUrl       = r.CsdbUrl     ?? string.Empty;
                Tags          = r.Tags        ?? string.Empty;
                IsFavorite    = r.IsFavorite;
                Rating        = r.Rating;
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    // ── Sauvegarde ───────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave()) return;
        IsSaving     = true;
        ErrorMessage = null;

        try
        {
            var dto = new UpdateReleaseDto
            {
                Id            = ReleaseId,
                Title         = Title.Trim(),
                Supertype     = Supertype,
                ReleaseTypeId = ReleaseTypeId,
                ReleaseDate   = string.IsNullOrWhiteSpace(ReleaseDate) ? null : ReleaseDate.Trim(),
                Notes         = string.IsNullOrWhiteSpace(Notes)       ? null : Notes.Trim(),
                DemozooUrl    = string.IsNullOrWhiteSpace(DemozooUrl)  ? null : DemozooUrl.Trim(),
                PouetUrl      = string.IsNullOrWhiteSpace(PouetUrl)    ? null : PouetUrl.Trim(),
                CsdbUrl       = string.IsNullOrWhiteSpace(CsdbUrl)     ? null : CsdbUrl.Trim(),
                Tags          = string.IsNullOrWhiteSpace(Tags)        ? null : Tags.Trim(),
                IsFavorite    = IsFavorite,
                Rating        = Rating,
            };

            await _releaseService.UpdateAsync(ReleaseId, dto);

            // Retour au détail
            _navigation.NavigateTo<ReleaseDetailViewModel>(ReleaseId);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsSaving = false; }
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Title) && !IsSaving;

    [RelayCommand]
    private void Cancel()
    {
        if (ReleaseId > 0)
            _navigation.NavigateTo<ReleaseDetailViewModel>(ReleaseId);
        else
            _navigation.NavigateTo<ReleaseListViewModel>();
    }

    partial void OnTitleChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

// ─── Releaser Detail ──────────────────────────────────────────────────────────

public partial class ReleaserDetailViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IUnitOfWork        _uow;
    private readonly IReleaseService    _releaseService;

    [ObservableProperty] private Releaser? _releaser;
    [ObservableProperty] private bool      _isLoading;
    [ObservableProperty] private string?   _errorMessage;
    [ObservableProperty] private string?   _notes;

    // Releases groupées par plateforme
    [ObservableProperty]
    private ObservableCollection<ReleasePlatformGroup> _platformGroups = [];
    [ObservableProperty] private bool   _isLoadingReleases;
    [ObservableProperty] private int    _totalReleaseCount;

    // Cache en mémoire — évite de relancer la requête au changement de tri
    private List<ReleaseSummaryDto> _allReleasesCache = [];

    // Tri : "alpha" (défaut) ou "date"
    [ObservableProperty] private string _sortMode = "alpha";
    public bool SortByAlpha => SortMode == "alpha";
    public bool SortByDate  => SortMode == "date";

    // Filtre par Supertype (Demo/Music/Graphics) — même mécanique que
    // ReleaseListViewModel.FilterBySupertype, mais appliqué en mémoire sur
    // _allReleasesCache (déjà chargé en entier pour ce releaser), pas via
    // une nouvelle requête.
    [ObservableProperty] private string? _selectedSupertype;
    public bool IsAllSelected      => SelectedSupertype == null;
    public bool IsProductionSelected => SelectedSupertype == "production";
    public bool IsGraphicsSelected => SelectedSupertype == "graphics";
    public bool IsMusicSelected    => SelectedSupertype == "music";

    [RelayCommand]
    private void FilterBySupertype(string? supertype)
    {
        SelectedSupertype = (supertype == SelectedSupertype) ? null : supertype;
        OnPropertyChanged(nameof(IsAllSelected));
        OnPropertyChanged(nameof(IsProductionSelected));
        OnPropertyChanged(nameof(IsGraphicsSelected));
        OnPropertyChanged(nameof(IsMusicSelected));
        ApplySortAndGroup();
    }

    // VM d'origine pour le bouton retour (GroupListViewModel ou ScenerListViewModel)
    private int   _currentReleaserId;
    // Scroll sauvegardé par releaserId
    private readonly Dictionary<int, (double releases, double right)> _scrollCache = new();
    public double SavedReleasesScrollOffset
    {
        get => _currentReleaserId > 0 && _scrollCache.TryGetValue(_currentReleaserId, out var v) ? v.releases : 0;
        set { if (_currentReleaserId > 0) _scrollCache[_currentReleaserId] = (value, SavedRightScrollOffset); }
    }
    public double SavedRightScrollOffset
    {
        get => _currentReleaserId > 0 && _scrollCache.TryGetValue(_currentReleaserId, out var v) ? v.right : 0;
        set { if (_currentReleaserId > 0) _scrollCache[_currentReleaserId] = (SavedReleasesScrollOffset, value); }
    }

    // Membres du groupe (si IsGroup)
    [ObservableProperty] private ObservableCollection<Releaser> _members = [];
    // Groupes d'appartenance (si scener)
    [ObservableProperty] private ObservableCollection<Releaser> _groups = [];

    public bool IsGroup  => Releaser?.IsGroup == true;
    public bool IsScener => Releaser?.IsGroup == false;



    public string CountryFlag => Releaser?.Country switch
    {
        null or "" => "",
        var c      => string.Concat(c.ToUpperInvariant().Select(ch => char.ConvertFromUtf32(ch - 'A' + 0x1F1E6)))
    };

    public ReleaserDetailViewModel(
        INavigationService navigation,
        IUnitOfWork        uow,
        IReleaseService    releaseService)
    {
        _navigation     = navigation;
        _uow            = uow;
        _releaseService = releaseService;
    }

    public async Task LoadAsync(int releaserId)
    {
        _currentReleaserId = releaserId;
        IsLoading          = true;
        ErrorMessage  = null;
        Notes         = null;
        _allReleasesCache.Clear();
        PlatformGroups.Clear();

        try
        {
            // Requête 1 : infos du releaser (sans Nicks des membres — cf. AsNoTracking)
            Releaser = await _uow.Releasers.GetWithNicksAndMembersAsync(releaserId);
            if (Releaser == null) { ErrorMessage = "Releaser introuvable."; return; }
            Notes = Releaser.Notes;

            OnPropertyChanged(nameof(IsGroup));
            OnPropertyChanged(nameof(IsScener));
            OnPropertyChanged(nameof(CountryFlag));

            if (Releaser.IsGroup)
                Members = new ObservableCollection<Releaser>(
                    Releaser.MembershipsAsGroup
                        .OrderBy(m => !m.IsCurrentMember)
                        .ThenBy(m => m.Scener.Name)
                        .Select(m => m.Scener));
            else
                Groups = new ObservableCollection<Releaser>(
                    Releaser.MembershipsAsScener
                        .OrderBy(m => !m.IsCurrentMember)
                        .ThenBy(m => m.Group.Name)
                        .Select(m => m.Group));

            // Requête 2 : releases (AsSplitQuery, sans N+1)
            var releases     = await _uow.Releases.GetByReleaserAsync(Releaser.Id);
            var creditedRoles = await _uow.Releases.GetCreditedRolesByReleaserAsync(Releaser.Id);
            await BuildReleasesFromResultAsync(releases, creditedRoles);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void SearchByNick(string nickName) =>
        _navigation.NavigateTo<ReleaseListViewModel>(parameter: null, tag: $"nick:{nickName}");

    // Tri en mémoire — instantané, pas de requête
    [RelayCommand]
    private void SetSort(string mode)
    {
        if (SortMode == mode) return;
        SortMode = mode;
        OnPropertyChanged(nameof(SortByAlpha));
        OnPropertyChanged(nameof(SortByDate));
        ApplySortAndGroup();
    }

    private async Task BuildReleasesFromResultAsync(
        IEnumerable<DemoBase.Core.Models.Release> releases,
        Dictionary<int, string>? creditedRoles = null)
    {
        IsLoadingReleases = true;
        try
        {
            var releaseList = releases.ToList();

            _allReleasesCache = releaseList.Select(r => new ReleaseSummaryDto
            {
                Id              = r.Id,
                DemozooId       = r.DemozooId,
                Title           = r.Title,
                ReleaseDate     = r.ReleaseDate ?? string.Empty,
                Supertype       = r.Supertype,
                ReleaseTypeId   = r.ReleaseTypeId,
                ReleaseTypeName = r.ReleaseType?.Name ?? string.Empty,
                PlatformNames   = string.Join(", ", r.ReleasePlatforms
                    .Select(rp => rp.Platform?.ShortName ?? rp.Platform?.Name ?? "")
                    .Where(s => s != "")),
                AuthorNames     = r.AuthorNamesCache ?? string.Empty,
                IsFavorite      = r.IsFavorite,
                ThumbnailPath   = r.ThumbnailPathCache,
                CreditedRole    = creditedRoles != null && creditedRoles.TryGetValue(r.Id, out var role)
                    ? FormatCreditRole(role)
                    : null,
                // Meilleur classement en compétition — même calcul que ReleaseService
                // (Services.cs) pour la liste principale des releases, cf.
                // GetByReleaserAsync qui Include désormais CompetitionPlacings.
                BestRank        = r.CompetitionPlacings
                    .Where(cp => cp.Ranking.HasValue).MinBy(cp => cp.Ranking)?.Ranking,
                BestCompetition = r.CompetitionPlacings
                    .Where(cp => cp.Ranking.HasValue).MinBy(cp => cp.Ranking)?.Competition?.Party?.Name,
            }).ToList();

            // HasNoFile : même calcul que ReleaseService.SearchAsync (cf. son commentaire pour
            // le détail du fix du 2026-07-24 — un lien soundtrack/annexe non-vidéo ne doit pas
            // suffire à masquer le badge 🚫, seul IsMainFile compte). GetByReleaserAsync
            // Include désormais Links pour permettre ce calcul.
            var dzIds = _allReleasesCache
                .Where(i => i.DemozooId.HasValue)
                .Select(i => i.DemozooId!.Value)
                .Distinct().ToList();
            var datPresence = dzIds.Count > 0
                ? await _uow.Releases.GetDatEntriesForDemozooIdsAsync(dzIds)
                : new Dictionary<int, DemoBase.Core.Models.DatEntry>();
            var hasLaunchableLinkById = releaseList.ToDictionary(
                r => r.Id,
                r => r.Links.Any(l => l.IsMainFile));
            foreach (var item in _allReleasesCache)
            {
                var hasDat = item.DemozooId.HasValue && datPresence.ContainsKey(item.DemozooId.Value);
                var hasLaunchableLink = hasLaunchableLinkById.TryGetValue(item.Id, out var v) && v;
                item.HasNoFile = !hasDat && !hasLaunchableLink;
            }

            TotalReleaseCount = _allReleasesCache.Count;
            ApplySortAndGroup();
        }
        finally { IsLoadingReleases = false; }
    }

    /// <summary>
    /// Les valeurs Demozoo stockées dans ReleaseCredits.Role sont en minuscules
    /// libres ("code", "music", "graphics", "font"…), et plusieurs rôles
    /// peuvent être concaténés par ", " (cf. GetCreditedRolesByReleaserAsync,
    /// même format que l'onglet Credits d'une release : "code, graphics, other").
    /// Capitalise chaque rôle individuellement pour l'affichage,
    /// ex. "Code, Graphics, Other" plutôt que "Code, graphics, other".
    /// </summary>
    private static string FormatCreditRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return role;
        return string.Join(", ", role.Split(", ").Select(r =>
            r.Length > 0 ? char.ToUpperInvariant(r[0]) + r[1..] : r));
    }

    private void ApplySortAndGroup()
    {
        IEnumerable<ReleaseSummaryDto> filtered = _allReleasesCache;
        if (SelectedSupertype != null)
            filtered = filtered.Where(r => r.Supertype == SelectedSupertype);

        var sorted = SortMode == "date"
            ? filtered.OrderByDescending(r => r.ReleaseDate).ToList()
            : filtered.OrderBy(r => r.Title).ToList();

        var grouped = sorted
            .GroupBy(r => string.IsNullOrWhiteSpace(r.PlatformNames)
                ? "Autres"
                : r.PlatformNames.Split(',')[0].Trim())
            .OrderBy(g => g.Key == "Autres")
            .ThenBy(g => g.Key);

        PlatformGroups = new ObservableCollection<ReleasePlatformGroup>(
            grouped.Select(g => new ReleasePlatformGroup
            {
                PlatformName = g.Key,
                Releases     = new ObservableCollection<ReleaseSummaryDto>(g.ToList()),
            }));
    }

    [ObservableProperty] private ReleaseSummaryDto? _selectedRelease;

    /// <summary>Navigue dans la liste aplatie de toutes les releases (+1/−1).</summary>
    public int SelectByOffset(int offset)
    {
        var flat = PlatformGroups.SelectMany(g => g.Releases).ToList();
        if (flat.Count == 0) return -1;
        int current = SelectedRelease != null ? flat.IndexOf(SelectedRelease) : -1;
        int next = Math.Max(0, Math.Min(flat.Count - 1, current + offset));
        if (next == current && current >= 0) return -1;
        SelectedRelease = flat[next];
        return next;
    }

    [RelayCommand]
    private void OpenRelease(ReleaseSummaryDto dto) =>
        _navigation.NavigateTo<ReleaseDetailViewModel>(dto.Id);

    [RelayCommand]
    private void OpenMember(Releaser member) =>
        _navigation.NavigateTo<ReleaserDetailViewModel>(member.Id);
}

// ─── ReleasePlatformGroup ─────────────────────────────────────────────────────

public partial class ReleasePlatformGroup : ObservableObject
{
    public string PlatformName { get; set; } = string.Empty;
    public ObservableCollection<ReleaseSummaryDto> Releases { get; set; } = [];
    public int Count => Releases.Count;

    [ObservableProperty] private bool _isExpanded = true;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
// ─── Party Detail ─────────────────────────────────────────────────────────────

public partial class PartyDetailViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IUnitOfWork        _uow;

    [ObservableProperty] private Party?  _party;
    [ObservableProperty] private bool    _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _notes;

    /// <summary>Déclenché après LoadAsync pour que la vue remette le scroll en haut.</summary>
    public event Action? ScrollResetRequested;

    // Compétitions enrichies pour l'affichage
    [ObservableProperty]
    private ObservableCollection<CompetitionViewModel> _competitions = [];

    // Résumé stats
    public int TotalEntries   => Competitions.Sum(c => c.Placings.Count);
    public int TotalCompetitions => Competitions.Count;

    public string DateRange
    {
        get
        {
            if (Party == null) return string.Empty;
            if (string.IsNullOrEmpty(Party.StartDate)) return string.Empty;
            if (string.IsNullOrEmpty(Party.EndDate)
                || Party.StartDate == Party.EndDate)
                return Party.StartDate;
            return $"{Party.StartDate} – {Party.EndDate}";
        }
    }

    public string CountryFlag => Party?.CountryCode switch
    {
        null or "" => "",
        var c      => string.Concat(c.ToUpperInvariant()
            .Select(ch => char.ConvertFromUtf32(ch - 'A' + 0x1F1E6)))
    };

    public PartyDetailViewModel(INavigationService navigation, IUnitOfWork uow)
    {
        _navigation = navigation;
        _uow        = uow;
    }

    [ObservableProperty] private PlacingViewModel? _selectedPlacing;

    /// <summary>Navigue dans la liste aplatie de toutes les placings (+1/−1).</summary>
    public int SelectByOffset(int offset)
    {
        var flat = Competitions.SelectMany(c => c.Placings).ToList();
        if (flat.Count == 0) return -1;
        int current = SelectedPlacing != null ? flat.IndexOf(SelectedPlacing) : -1;
        int next = Math.Max(0, Math.Min(flat.Count - 1, current + offset));
        if (next == current && current >= 0) return -1;
        SelectedPlacing = flat[next];
        return next;
    }

    [RelayCommand]
    private void GoBackToList() =>
        _navigation.NavigateTo<DemoBase.App.ViewModels.Library.PartyListViewModel>();

    public async Task LoadAsync(int partyId)
    {
        IsLoading    = true;
        ErrorMessage = null;
        Competitions.Clear();
        try
        {
            Party = await _uow.Parties.GetWithCompetitionsAsync(partyId);
            if (Party == null) { ErrorMessage = "Party introuvable."; return; }
            Notes = Party.Notes;

            OnPropertyChanged(nameof(DateRange));
            OnPropertyChanged(nameof(CountryFlag));

            // Construire les VM de compétitions triées par nom
            foreach (var comp in Party.Competitions.OrderBy(c => c.Name))
            {
                var placings = comp.Placings
                    .OrderBy(p => p.Ranking.HasValue && p.Ranking > 0 ? 0 : 1)  // classés en premier
                    .ThenBy(p => p.Ranking ?? 999)
                    .Select(p => new PlacingViewModel
                    {
                        ReleaseId    = p.ReleaseId,
                        Ranking      = p.Ranking,
                        Score        = p.Score,
                        Title        = p.Release?.Title ?? "—",
                        ReleaseType  = p.Release?.ReleaseType?.Name ?? "",
                        Supertype    = p.Release?.Supertype ?? "",
                        Platforms    = string.Join(", ", p.Release?.ReleasePlatforms
                                           .Select(rp => rp.Platform?.Name ?? "") ?? []),
                        AuthorNames  = p.Release?.AuthorNamesCache ?? "",
                        DemozooId    = p.Release?.DemozooId,
                        HasLaunchableLink = p.Release?.Links.Any(l => l.IsMainFile) ?? false,
                    })
                    .ToList();

                Competitions.Add(new CompetitionViewModel
                {
                    Id      = comp.Id,
                    Name    = comp.Name,
                    Placings = new ObservableCollection<PlacingViewModel>(placings),
                });
            }

            // Charger les noms d'auteurs via JOIN pour toutes les releases des compos
            await LoadAuthorNamesAsync();

            // 2026-07-31, retour utilisateur ("il faudrait afficher le petit icone
            // 'interdit' [...] quand il n'y a aucun fichier DATs ou de lien de
            // téléchargement pour la release [...] il n'apparait pas sur cette vue") :
            // même badge 🚫 que ReleaseListView/ReleaserDetailView, absent ici jusqu'ici
            // car HasNoFile n'était jamais calculé pour cette liste.
            await ComputeHasNoFileAsync();

            OnPropertyChanged(nameof(TotalEntries));
            OnPropertyChanged(nameof(TotalCompetitions));
            ScrollResetRequested?.Invoke();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void OpenRelease(int releaseId) =>
        _navigation.NavigateTo<ReleaseDetailViewModel>(releaseId);

    private async Task LoadAuthorNamesAsync()
    {
        var allReleaseIds = Competitions
            .SelectMany(c => c.Placings)
            .Select(p => p.ReleaseId)
            .Distinct()
            .ToList();
        if (!allReleaseIds.Any()) return;

        // JOIN ReleaseAuthors → Nicks → Releasers pour tous les IDs d'un coup
        var authorMap = await _uow.Releases
            .GetAuthorNamesByReleaseIdsAsync(allReleaseIds);

        foreach (var comp in Competitions)
            foreach (var placing in comp.Placings)
                if (authorMap.TryGetValue(placing.ReleaseId, out var names))
                    placing.AuthorNames = names;
    }

    /// <summary>
    /// Calcule HasNoFile pour toutes les placings — même logique que
    /// ReleaseService.SearchAsync ("HasNoFile : releases sans aucun fichier
    /// exploitable") : aucun DatEntry connu pour le DemozooId ET aucun ReleaseLink
    /// "fichier de la production" (HasLaunchableLink, déjà capturé à la construction
    /// des PlacingViewModel depuis Release.Links). Requête DatEntries groupée en une
    /// fois pour tous les DemozooId de la party (même schéma que LoadAuthorNamesAsync).
    /// </summary>
    private async Task ComputeHasNoFileAsync()
    {
        var allPlacings = Competitions.SelectMany(c => c.Placings).ToList();
        var dzIds = allPlacings
            .Where(p => p.DemozooId.HasValue)
            .Select(p => p.DemozooId!.Value)
            .Distinct().ToList();

        var datPresence = dzIds.Count > 0
            ? await _uow.Releases.GetDatEntriesForDemozooIdsAsync(dzIds)
            : new Dictionary<int, DatEntry>();

        foreach (var p in allPlacings)
        {
            var hasDat = p.DemozooId.HasValue && datPresence.ContainsKey(p.DemozooId.Value);
            p.HasNoFile = !hasDat && !p.HasLaunchableLink;
        }
    }
}

// ─── CompetitionViewModel ─────────────────────────────────────────────────────

public partial class CompetitionViewModel : ObservableObject
{
    public int    Id      { get; set; }
    public string Name    { get; set; } = string.Empty;

    [ObservableProperty] private bool _isExpanded = true;

    public ObservableCollection<PlacingViewModel> Placings { get; set; } = [];
    public int EntryCount => Placings.Count;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

// ─── PlacingViewModel ─────────────────────────────────────────────────────────

public class PlacingViewModel
{
    public int     ReleaseId   { get; set; }
    public int?    Ranking     { get; set; }
    public string? Score       { get; set; }
    public string  Title       { get; set; } = string.Empty;
    public string  ReleaseType { get; set; } = string.Empty;
    public string  Supertype   { get; set; } = string.Empty;
    public string  Platforms   { get; set; } = string.Empty;
    public string  AuthorNames { get; set; } = string.Empty;

    // 2026-07-31, retour utilisateur ("il faudrait afficher le petit icone 'interdit'
    // que tu affiches sur d'autres vues quand il n'y a aucun fichier DATs ou de lien
    // de téléchargement") : DemozooId/HasLaunchableLink alimentent le calcul de
    // HasNoFile (même logique que ReleaseService.SearchAsync — cf. PartyDetailViewModel.
    // ComputeHasNoFileAsync) — DemozooId et HasLaunchableLink capturés dès la
    // construction (Release déjà chargé avec .Links via GetWithCompetitionsAsync),
    // HasNoFile résolu dans un second passage (a besoin d'une requête groupée sur les
    // DatEntries, cf. LoadAuthorNamesAsync pour le même schéma en deux passes).
    public int?  DemozooId         { get; set; }
    public bool  HasLaunchableLink { get; set; }
    public bool  HasNoFile         { get; set; }

    public string RankLabel => Ranking switch
    {
        1 => "★ 1st",
        2 => "2nd",
        3 => "3rd",
        { } n => $"{n}th",
        null => "—"
    };

    public bool IsTopThree => Ranking is >= 1 and <= 3;
}

