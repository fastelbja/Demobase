using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.Core.DTOs;
using DemoBase.Core.Interfaces;
using DemoBase.Core.Models;
using DemoBase.Data;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;

namespace DemoBase.App.ViewModels;

// ─── MediaBrowserViewModel ────────────────────────────────────────────────────
// Vue plein écran style Netflix/Spotify pour parcourir toutes les releases
// Graphics (grille de vignettes) et Music (liste avec artwork).

public partial class MediaBrowserViewModel : ObservableObject
{
    private readonly IReleaseService       _releaseService;
    private readonly INavigationService    _navigation;
    private readonly PreferencesService    _prefs;

    [ObservableProperty] private bool   _isGraphicsActive = false;
    // 2026-07-30, demande utilisateur : 3e onglet "Musique (modland)" — un simple bool
    // binaire (comme IsGraphicsActive ci-dessus, hérité du schéma Music/Graphics à 2
    // onglets) ne suffit plus. Plutôt que de migrer IsGraphicsActive vers une enum (risque
    // de casser tous les bindings XAML existants sur ce bool), un second bool indépendant,
    // les trois commandes ShowXxx ci-dessous garantissant l'exclusion mutuelle.
    [ObservableProperty] private bool   _isModlandActive = false;
    [ObservableProperty] private bool   _isLoading;

    // 2026-07-30, demande utilisateur : la fenêtre principale doit s'agrandir pour
    // recouvrir la colonne de détail de release (normalement toujours visible à
    // droite, cf. MainWindow.xaml.cs/isFullWidth) dès l'ouverture de l'onglet
    // Modland — pas seulement une fois la lecture démarrée (retour initial :
    // "l'écran s'agrandit quand je joue le premier morceau [...] possible de le
    // faire dés l'ouverture de l'onglet ?", après une 1ère version qui ne
    // s'agrandissait qu'avec `IsModlandActive && Modland.IsPlaying`). MainWindow
    // observe cette seule propriété plutôt que de connaître les détails internes
    // de ModlandBrowserViewModel.
    [ObservableProperty] private bool   _wantsFullWidth;

    public GraphicsBrowserViewModel Graphics { get; }
    public MusicBrowserViewModel    Music    { get; }
    public ModlandBrowserViewModel  Modland  { get; }

    public MediaBrowserViewModel(
        IReleaseService    releaseService,
        INavigationService navigation,
        PreferencesService prefs,
        DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService releaseBuilder,
        DemoBase.Data.ModlandCatalogService modlandCatalog,
        DemoBase.App.Services.ModlandService modlandService,
        TrackerPlayer.Core.Interfaces.ITrackerService? tracker = null,
        DemoBase.Data.FavoriteSoundtrackService? favService = null)
    {
        _releaseService = releaseService;
        _navigation     = navigation;
        _prefs          = prefs;

        Graphics = new GraphicsBrowserViewModel(releaseService, navigation, prefs, releaseBuilder);
        Music    = new MusicBrowserViewModel(releaseService, navigation, prefs);
        Modland  = new ModlandBrowserViewModel(modlandCatalog, modlandService, tracker, favService);
    }

    partial void OnIsModlandActiveChanged(bool value) => WantsFullWidth = value;

    public async Task LoadAsync()
    {
        System.Diagnostics.Debug.WriteLine(
            $"[MediaBrowser] LoadAsync called, IsGraphicsActive={IsGraphicsActive} IsModlandActive={IsModlandActive}");
        if (IsModlandActive)
            await Modland.LoadAsync();
        else if (IsGraphicsActive)
            await Graphics.LoadAsync();
        else
            await Music.LoadAsync();
    }

    [RelayCommand]
    private async Task ShowGraphics()
    {
        IsGraphicsActive = true;
        IsModlandActive  = false;
        if (!Graphics.IsLoaded) await Graphics.LoadAsync();
    }

    [RelayCommand]
    private async Task ShowMusic()
    {
        IsGraphicsActive = false;
        IsModlandActive  = false;
        if (!Music.IsLoaded) await Music.LoadAsync();
    }

    [RelayCommand]
    private async Task ShowModland()
    {
        IsGraphicsActive = false;
        IsModlandActive  = true;
        if (!Modland.IsLoaded) await Modland.LoadAsync();
    }
}


// ─── GraphicCardViewModel ─────────────────────────────────────────────────────
// Wrapper autour de ReleaseSummaryDto qui expose ThumbnailImage (BitmapImage)
// comme propriété WPF observable — impossible de mettre BitmapImage dans
// DemoBase.Core (pas de référence WPF).

public class GraphicCardViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public DemoBase.Core.DTOs.ReleaseSummaryDto Release { get; }

    private string? _thumbPath;
    public string? ThumbPath
    {
        get => _thumbPath;
        set { _thumbPath = value; Notify(nameof(ThumbPath)); }
    }

    // 2026-07-30, demande utilisateur : "gérer le download progressif des fichiers
    // quand on affiche le mediabrowser partie graphics, quand les fichiers ne sont
    // pas présents sur disque" — indique qu'un téléchargement en arrière-plan est
    // en cours pour cette vignette (cf. GraphicsBrowserViewModel.QueueBackgroundDownload),
    // affiché dans le placeholder à la place du simple icône "🖼".
    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set { _isDownloading = value; Notify(nameof(IsDownloading)); }
    }

    public GraphicCardViewModel(DemoBase.Core.DTOs.ReleaseSummaryDto release)
        => Release = release;
}

// ─── GraphicsBrowserViewModel ─────────────────────────────────────────────────

public partial class GraphicsBrowserViewModel : ObservableObject
{
    private readonly IReleaseService    _releaseService;
    private readonly INavigationService _navigation;
    private readonly DemoBase.Data.PreferencesService _prefs;
    private readonly DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService _releaseBuilder;

    // 2026-07-30, demande utilisateur : throttle des téléchargements en arrière-plan
    // déclenchés par le scroll dans la grille Graphics — 2 téléchargements simultanés
    // max, comme un navigateur limite ses requêtes par domaine. _downloadingIds évite
    // de relancer un téléchargement déjà en cours pour le même DemozooId (l'utilisateur
    // peut remonter/redescendre dans la grille pendant qu'un téléchargement précédent
    // tourne encore).
    private readonly SemaphoreSlim _downloadGate = new(2);
    private readonly HashSet<int>  _downloadingIds = new();

    // 2026-07-30, retour utilisateur : "le téléchargement ne s'arrête plus [...] si je
    // tape un nom d'artiste, je souhaiterais qu'il télécharge les images de ce qu'il a
    // filtré" — chaque nouvelle recherche/filtre annule les téléchargements de la
    // "génération" précédente (résultats qui ne sont plus affichés) au lieu de les
    // laisser continuer à occuper les 2 emplacements du throttle pendant que les
    // vignettes réellement filtrées attendent leur tour.
    private CancellationTokenSource _generation = new();

    [ObservableProperty] private ObservableCollection<GraphicCardViewModel> _items = [];
    [ObservableProperty] private bool    _isLoading;
    [ObservableProperty] private bool    _hasMore;
    [ObservableProperty] private int     _totalCount;
    [ObservableProperty] private string  _searchQuery = string.Empty;
    [ObservableProperty] private string? _selectedPlatform;
    [ObservableProperty] private string  _sortBy = "Date";

    public bool IsLoaded { get; private set; }
    public static string[] SortOptions => ["Title", "Date", "Author"];
    public ObservableCollection<string> Platforms { get; } = [];

    private int _currentPage = 1;
    private const int PageSize = 80;

    // Extensions image reconnues dans les ZIPs graphics
    private static readonly HashSet<string> _imgExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".iff", ".ilbm",
          ".tga", ".webp", ".lbm", ".xim", ".raw", ".pic", ".ppm",
          ".tiff", ".tif", ".ham", ".ham8",
          ".scr", ".atr", ".g9b", ".neo", ".pi1", ".pi2", ".pi3" };

    public GraphicsBrowserViewModel(IReleaseService releaseService, INavigationService navigation,
        DemoBase.Data.PreferencesService prefs,
        DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService releaseBuilder)
    {
        _releaseService = releaseService;
        _navigation     = navigation;
        _prefs          = prefs;
        _releaseBuilder = releaseBuilder;
    }

    partial void OnSearchQueryChanged(string value) => _ = ReloadAsync();
    partial void OnSelectedPlatformChanged(string? value) => _ = ReloadAsync();
    partial void OnSortByChanged(string value) => _ = ReloadAsync();

    public async Task LoadAsync()
    {
        System.Diagnostics.Debug.WriteLine("[GraphicsBrowser] LoadAsync called");
        IsLoaded = true;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        // Nouvelle recherche/filtre → les téléchargements en cours pour les résultats
        // précédents (plus affichés) n'ont plus de raison d'occuper les 2 emplacements
        // du throttle pendant que les vignettes du nouveau filtre attendent leur tour.
        _generation.Cancel();
        _generation = new CancellationTokenSource();

        IsLoading = true;
        _currentPage = 1;
        Items.Clear();
        try
        {
            await FetchPageAsync();
        }
        finally { IsLoading = false; }
    }

    private async Task FetchPageAsync()
    {
        System.Diagnostics.Debug.WriteLine($"[GraphicsBrowser] FetchPageAsync page={_currentPage}");
        var filter = new ReleaseSearchFilter
        {
            Supertype      = "graphics",
            HasDatEntry    = true,
            AuthorsOnly    = true,
            Query          = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
            Page           = _currentPage,
            PageSize       = PageSize,
            SkipCount      = _currentPage > 1,
            KnownTotal     = TotalCount,
            SortBy         = SortBy == "Date"   ? "ReleaseDate" :
                             SortBy == "Author" ? "Author"       : "Title",
            SortDescending = SortBy == "Date",
        };

        // 2026-07-31, retour utilisateur (log de perf, ReleaseListViewModel) : même bug de
        // requêtes de recherche non annulées qui s'empilent sur l'unique connexion SQLite
        // partagée — cf. commentaire détaillé sur ReleaseService.SearchAsync (Services.cs).
        // _generation est déjà annulé/recréé à chaque ReloadAsync (throttle vignettes) ; son
        // token sert aussi ici pour couper une recherche devenue obsolète.
        var result = await _releaseService.SearchAsync(filter, _generation.Token);
        TotalCount = result.TotalCount;
        HasMore = Items.Count + result.Items.Count() < TotalCount;

        var newItems = result.Items.Select(i => new GraphicCardViewModel(i)).ToList();

        // Résoudre les miniatures sur thread de fond — complètement terminé avant
        // de revenir sur le thread UI pour ajouter les items à la collection.
        await Task.Run(() => ResolveZipThumbnails(newItems));

        // Maintenant sur thread UI — ajouter les items avec ThumbPath déjà assigné
        var withImg = newItems.Count(i => i.ThumbPath != null);
        System.Diagnostics.Debug.WriteLine($"[GfxGrid] adding {newItems.Count} items, {withImg} with image");
        foreach (var item in newItems)
            Items.Add(item);

        // Construire la liste des plateformes disponibles (une seule fois)
        if (Platforms.Count == 0 && _currentPage == 1)
        {
            var platforms = Items
                .SelectMany(i => (i.Release.PlatformNames ?? string.Empty).Split(", ",
                    System.StringSplitOptions.RemoveEmptyEntries))
                .Distinct()
                .OrderBy(p => p)
                .ToList();
            foreach (var p in platforms)
                Platforms.Add(p);
        }
    }

    private static string? GetRecoilPath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Externals", "RECOIL", "recoil2png.exe");
        if (File.Exists(local)) return local;
        return null;
    }

    /// <summary>
    /// Résout les vignettes DÉJÀ présentes sur disque pour une page tout juste chargée
    /// (pas de réseau, pas de mise en file de téléchargement — cf.
    /// <see cref="RequestVisibleDownloads"/> pour ça, déclenché uniquement par les
    /// cartes réellement visibles à l'écran, cf. commentaire ci-dessous).
    /// </summary>
    private void ResolveZipThumbnails(List<GraphicCardViewModel> items)
    {
        var needsThumb = items
            .Where(i => i.ThumbPath == null && i.Release.DemozooId.HasValue)
            .ToList();
        var noId = items.Count(i => !i.Release.DemozooId.HasValue);
        System.Diagnostics.Debug.WriteLine($"[GfxThumb] {items.Count} cards, {needsThumb.Count} need thumb, {noId} without DemozooId");
        if (needsThumb.Count == 0) return;

        var romsRoot = DemoBase.Data.PreferencesService.LastResolvedPathReleases;
        System.Diagnostics.Debug.WriteLine($"[GfxThumb] romsRoot='{romsRoot}'");
        if (string.IsNullOrEmpty(romsRoot)) return;

        var demozooIds = needsThumb
            .Select(i => i.Release.DemozooId!.Value)
            .Distinct()
            .ToList();
        System.Diagnostics.Debug.WriteLine($"[GfxThumb] querying {demozooIds.Count} demozooIds");

        Dictionary<int, DemoBase.Core.Models.DatEntry> datEntries;
        try { datEntries = _releaseService.GetDatEntriesForDemozooIdsAsync(demozooIds).ConfigureAwait(false).GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[GfxThumb] GetDatEntries error: {ex.Message}"); return; }
        System.Diagnostics.Debug.WriteLine($"[GfxThumb] got {datEntries.Count} datEntries");

        int found = 0, notFound = 0, noImg = 0, extracted = 0;
        foreach (var item in needsThumb)
        {
            if (!item.Release.DemozooId.HasValue) continue;
            if (!datEntries.TryGetValue(item.Release.DemozooId.Value, out var dat)) { notFound++; continue; }
            if (string.IsNullOrEmpty(dat.RomPath)) { notFound++; continue; }

            var zipPath = Path.Combine(romsRoot, dat.RomPath);
            if (!File.Exists(zipPath)) { found++; continue; } // absent du disque — laissé à RequestVisibleDownloads

            if (TryExtractThumbFromZip(item, zipPath)) extracted++; else noImg++;
        }
        System.Diagnostics.Debug.WriteLine($"[GfxThumb] done: found={found} notFound={notFound} noImg={noImg} extracted={extracted}");
    }

    /// <summary>
    /// 2026-07-30, retour utilisateur : "le téléchargement ne s'arrête plus. il doit se
    /// contenter des elements visibles à l'écran" — avant, CHAQUE page de 80 vignettes
    /// chargée par le scroll infini mettait en file d'attente un téléchargement pour les
    /// 80, visibles à l'écran ou non (avec seulement 2 téléchargements simultanés, la
    /// file grossissait indéfiniment dès que l'utilisateur scrollait vite ou changeait
    /// de filtre). Appelée par MediaBrowserView (code-behind) avec uniquement les cartes
    /// dont le container est effectivement dans le viewport du ScrollViewer, recalculé
    /// au scroll (débouncé) et après chaque chargement de page — donc aussi après une
    /// recherche par nom d'artiste (SearchQuery), qui vide et recharge Items.
    /// </summary>
    public void RequestVisibleDownloads(List<GraphicCardViewModel> visibleItems)
    {
        var needsThumb = visibleItems
            .Where(i => i.ThumbPath == null && !i.IsDownloading && i.Release.DemozooId.HasValue)
            .ToList();
        if (needsThumb.Count == 0) return;

        var token = _generation.Token;
        _ = Task.Run(() => ResolveOrQueueDownloads(needsThumb, token));
    }

    private void ResolveOrQueueDownloads(List<GraphicCardViewModel> items, CancellationToken generationToken)
    {
        var romsRoot = DemoBase.Data.PreferencesService.LastResolvedPathReleases;
        if (string.IsNullOrEmpty(romsRoot)) return;

        var demozooIds = items.Select(i => i.Release.DemozooId!.Value).Distinct().ToList();
        Dictionary<int, DemoBase.Core.Models.DatEntry> datEntries;
        try { datEntries = _releaseService.GetDatEntriesForDemozooIdsAsync(demozooIds).ConfigureAwait(false).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GfxThumb] RequestVisibleDownloads GetDatEntries error: {ex.Message}");
            return;
        }

        foreach (var item in items)
        {
            if (generationToken.IsCancellationRequested) return;
            var demozooId = item.Release.DemozooId!.Value;
            if (!datEntries.TryGetValue(demozooId, out var dat) || string.IsNullOrEmpty(dat.RomPath)) continue;

            var zipPath = Path.Combine(romsRoot, dat.RomPath);
            if (File.Exists(zipPath)) { TryExtractThumbFromZip(item, zipPath); continue; }

            QueueBackgroundDownload(item, demozooId, zipPath, generationToken);
        }
    }

    /// <summary>Extrait la première image reconnue du ZIP d'une release et l'assigne à
    /// <paramref name="item"/>.ThumbPath (ou son équivalent .png converti via recoil2png
    /// pour les formats non supportés nativement par WPF). Factorisé pour être appelé à
    /// la fois par le passage initial de résolution des vignettes ET par
    /// <see cref="QueueBackgroundDownload"/> une fois un téléchargement terminé.</summary>
    private bool TryExtractThumbFromZip(GraphicCardViewModel item, string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            // Prioriser png/jpg (bien supportés par WPF) avant gif/bmp/autres
            static int ExtPriority(string name) => Path.GetExtension(name).ToLowerInvariant() switch
            {
                ".png" => 0, ".jpg" => 1, ".jpeg" => 1,
                ".gif" => 2, ".bmp" => 3, ".tga" => 4,
                _ => 10
            };
            var imgEntry = zip.Entries
                .Where(e => _imgExts.Contains(Path.GetExtension(e.Name)))
                .OrderBy(e => ExtPriority(e.Name))
                .ThenBy(e => e.Name)
                .FirstOrDefault();

            if (imgEntry == null) return false;

            var cacheDir = Path.Combine(
                DemoBase.App.Services.WorkingPaths.GetSubdir("GfxThumb"),
                item.Release.DemozooId!.Value.ToString());
            Directory.CreateDirectory(cacheDir);
            var destPath = Path.Combine(cacheDir, imgEntry.Name);

            if (!File.Exists(destPath))
                imgEntry.ExtractToFile(destPath);

            // Stocker le chemin — WPF charge directement depuis FilePath comme dans ReleaseDetailView
            var ext = Path.GetExtension(destPath).ToLowerInvariant();
            var wpfExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".tif" };

            if (!wpfExts.Contains(ext))
            {
                // Format non supporté nativement par WPF → convertir via recoil2png
                var pngPath = Path.ChangeExtension(destPath, ".png");
                if (!File.Exists(pngPath))
                {
                    var recoilExe = GetRecoilPath();
                    if (!string.IsNullOrEmpty(recoilExe) && File.Exists(recoilExe))
                    {
                        try
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName         = recoilExe,
                                Arguments        = $"\"{destPath}\"",
                                WorkingDirectory = Path.GetDirectoryName(destPath)!,
                                CreateNoWindow   = true,
                                UseShellExecute  = false,
                            };
                            using var proc = System.Diagnostics.Process.Start(psi);
                            proc?.WaitForExit(10_000);
                        }
                        catch { }
                    }
                }
                if (File.Exists(pngPath))
                    destPath = pngPath;
                else
                    return false; // pas de recoil ou conversion échouée
            }

            item.ThumbPath = destPath;
            System.Diagnostics.Debug.WriteLine($"[GfxThumb] assigned dz={item.Release.DemozooId} file={Path.GetFileName(destPath)}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GfxThumb] error on {zipPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Lance un téléchargement en arrière-plan pour la release manquante (même
    /// mécanisme que BuildPlaylistAsync/TryDownloadZipAsync dans
    /// FavoriteSoundtracksViewModel, et PlayFromDatAsync dans ReleaseDetailViewModel :
    /// ReleaseBuilderService.TryBuildAsync télécharge/extrait/reconstruit le ZIP à
    /// l'emplacement attendu). Throttlé à <see cref="_downloadGate"/> téléchargements
    /// simultanés et dédupliqué via <see cref="_downloadingIds"/> pour ne pas relancer
    /// un téléchargement déjà en cours pour la même release. <paramref
    /// name="generationToken"/> (cf. <see cref="_generation"/>) permet d'annuler ce
    /// téléchargement — qu'il attende encore son tour dans <see cref="_downloadGate"/>
    /// ou soit déjà en cours — dès qu'une nouvelle recherche/filtre rend son résultat
    /// obsolète. Retourne false sans rien faire si un téléchargement est déjà en cours
    /// pour ce DemozooId.
    /// </summary>
    private bool QueueBackgroundDownload(GraphicCardViewModel item, int demozooId, string zipPath,
        CancellationToken generationToken)
    {
        lock (_downloadingIds)
        {
            if (!_downloadingIds.Add(demozooId)) return false;
        }
        item.IsDownloading = true;

        _ = Task.Run(async () =>
        {
            bool gateAcquired = false;
            try
            {
                await _downloadGate.WaitAsync(generationToken);
                gateAcquired = true;
                var result = await _releaseBuilder.TryBuildAsync(demozooId, ct: generationToken);
                if (result.Success)
                    TryExtractThumbFromZip(item, zipPath);
                else
                    System.Diagnostics.Debug.WriteLine(
                        $"[GfxThumb] Téléchargement en arrière-plan sans succès (dz={demozooId}) : {result.Error}");
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GfxThumb] Téléchargement annulé (nouveau filtre/page, dz={demozooId})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GfxThumb] Téléchargement en arrière-plan échoué (dz={demozooId}) : {ex.Message}");
            }
            finally
            {
                if (gateAcquired) _downloadGate.Release();
                lock (_downloadingIds) _downloadingIds.Remove(demozooId);
                item.IsDownloading = false;
            }
        });

        return true;
    }

    [RelayCommand]
    private async Task LoadMore()
    {
        if (!HasMore || IsLoading) return;
        IsLoading = true;
        _currentPage++;
        try { await FetchPageAsync(); }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void OpenRelease(GraphicCardViewModel card)
        => _navigation.NavigateTo<DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel>(card.Release.Id);

    [RelayCommand]
    private void StartSlideshow(GraphicCardViewModel startCard)
    {
        var startIdx = Items.ToList().FindIndex(c => c.Release.Id == startCard.Release.Id);
        if (startIdx < 0) startIdx = 0;
        var prefs = _prefs.LoadAllAsync().GetAwaiter().GetResult();
        var window = new DemoBase.App.Views.Media.SlideshowWindow(
            Items.ToList(), startIdx, prefs);
        window.Show();
    }
}

// ─── MusicBrowserViewModel ────────────────────────────────────────────────────

public partial class MusicBrowserViewModel : ObservableObject
{
    private readonly IReleaseService    _releaseService;
    private readonly INavigationService _navigation;
    private readonly PreferencesService _prefs;
    private Action? _stopAction;  // injectée depuis MainViewModel

    public void SetStopAction(Action stopAction) => _stopAction = stopAction;
    [ObservableProperty] private ObservableCollection<ReleaseSummaryDto> _items = [];
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private bool   _hasMore;
    [ObservableProperty] private int    _totalCount;
    [ObservableProperty] private string _searchQuery = string.Empty;
    // Bascule "Auteur" / "Titre" : détermine sur quel champ porte SearchQuery.
    // Remplace l'ancien ComboBox de tri (SortBy/SortOptions), qui ne triait pas
    // correctement sur "Author" (pas de colonne Author directe sur Release —
    // cf. ApplySort côté repository, qui retombait silencieusement sur Title) et
    // que l'utilisateur a signalé comme non fonctionnel. La liste reste triée par
    // titre (stable), la recherche devient explicite Auteur XOR Titre.
    [ObservableProperty] private string _searchField = "Author";
    [ObservableProperty] private ReleaseSummaryDto? _selectedItem;
    [ObservableProperty] private bool   _isShuffleMode;

    // IDs déjà joués en mode shuffle (pour éviter les répétitions dans la session)
    private readonly HashSet<int> _shuffleHistory = [];
    private bool _isShuffleTransitioning = false;        // guard anti-cascade
    private DateTime _lastShufflePlay = DateTime.MinValue; // anti-rebond temporel

    public bool IsLoaded { get; private set; }

    private int _currentPage = 1;
    private const int PageSize = 100;

    public MusicBrowserViewModel(
        IReleaseService    releaseService,
        INavigationService navigation,
        PreferencesService prefs)
    {
        _releaseService = releaseService;
        _navigation     = navigation;
        _prefs          = prefs;
    }

    private CancellationTokenSource? _searchDebounce;
    partial void OnSearchQueryChanged(string value) => _ = DebounceSearchAsync();
    partial void OnSearchFieldChanged(string value) => _ = ReloadAsync();

    private async Task DebounceSearchAsync()
    {
        _searchDebounce?.Cancel();
        _searchDebounce = new CancellationTokenSource();
        try { await Task.Delay(350, _searchDebounce.Token); await ReloadAsync(); }
        catch (OperationCanceledException) { }
    }

    public async Task LoadAsync()
    {
        IsLoaded = true;
        await ReloadAsync();
    }

    // 2026-07-31, retour utilisateur (log de perf, ReleaseListViewModel) : même bug de
    // requêtes de recherche non annulées qui s'empilent sur l'unique connexion SQLite
    // partagée (cf. commentaire détaillé sur ReleaseService.SearchAsync, Services.cs) —
    // contrairement à GraphicsBrowserViewModel (_generation) et ReleaseListViewModel
    // (_loadCts), cette classe n'avait AUCUN mécanisme d'annulation de la requête déjà
    // lancée : _searchDebounce ne coupe que le délai de 350ms avant l'appel, pas
    // FetchPageAsync lui-même une fois démarré.
    private CancellationTokenSource? _loadCts;

    private async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        _currentPage = 1;
        Items.Clear();
        try { await FetchPageAsync(ct); }
        catch (OperationCanceledException) { }
        finally { if (!ct.IsCancellationRequested) IsLoading = false; }
    }

    private async Task FetchPageAsync(CancellationToken ct = default)
    {
        var filter = new ReleaseSearchFilter
        {
            Supertype      = "music",
            AuthorsOnly    = SearchField != "Title",
            TitleOnly      = SearchField == "Title",
            Query          = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
            Page           = _currentPage,
            PageSize       = PageSize,
            SkipCount      = _currentPage > 1,
            KnownTotal     = TotalCount,
            // Tri fixe par titre (stable) — l'ancien sélecteur de tri est retiré,
            // cf. commentaire sur SearchField ci-dessus.
            SortBy         = "Title",
            SortDescending = false,
        };

        var result = await _releaseService.SearchAsync(filter, ct);
        TotalCount = result.TotalCount;
        HasMore = Items.Count + result.Items.Count() < TotalCount;
        foreach (var item in result.Items)
            Items.Add(item);
    }

    [RelayCommand]
    private async Task LoadMore()
    {
        if (!HasMore || IsLoading) return;
        IsLoading = true;
        _currentPage++;
        try { await FetchPageAsync(); }
        finally { IsLoading = false; }
    }

    /// <summary>
    /// Sélectionne l'item à l'offset donné (+1 = suivant, -1 = précédent), sans lancer
    /// la lecture — même contrat que LibraryListViewModelBase.SelectByOffset. Le
    /// changement de SelectedItem déclenche automatiquement le scroll dans la vue
    /// (cf. MediaBrowserView.OnMusicVmPropertyChanged). Utilisé par
    /// GlobalKeyboardService (flèches haut/bas) pour naviguer dans la liste Music.
    /// </summary>
    public int SelectByOffset(int offset)
    {
        if (Items.Count == 0) return -1;
        int current = SelectedItem != null ? Items.IndexOf(SelectedItem) : -1;
        int next    = Math.Max(0, Math.Min(Items.Count - 1, current + offset));
        if (next == current && current >= 0) return -1;
        SelectedItem = Items[next];
        return next;
    }

    [RelayCommand]
    private void PlayRelease(ReleaseSummaryDto dto)
    {
        // Charger la fiche et lancer automatiquement la lecture
        System.Diagnostics.Debug.WriteLine(
            $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] PlayRelease id={dto.Id} title=\"{dto.Title}\" " +
            $"thread={Environment.CurrentManagedThreadId}");
        SelectedItem = dto;
        _navigation.NavigateTo<DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel>(
            parameter: dto.Id, tag: "autoplay");
    }

    [RelayCommand]
    private void Stop() => _stopAction?.Invoke();

    /// <summary>Passe à la release précédente dans la liste filtrée courante.</summary>
    [RelayCommand]
    public void PlayPrevious()
    {
        if (!Items.Any()) return;
        var list = Items.ToList();
        var currentIdx = SelectedItem != null
            ? list.FindIndex(i => i.Id == SelectedItem.Id)
            : 0;
        var prevIdx = currentIdx - 1;
        if (prevIdx < 0) prevIdx = list.Count - 1; // boucle
        PlayReleaseCommand.Execute(list[prevIdx]);
    }

    /// <summary>Passe à la release suivante dans la liste filtrée courante.
    /// En mode shuffle, tire une release aléatoire parmi les 130 000+.
    /// Appelé par MainViewModel quand PlaylistEnded est déclenché.</summary>
    [RelayCommand]
    public async Task PlayNext()
    {
        System.Diagnostics.Debug.WriteLine(
            $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] PlayNext ENTER thread={Environment.CurrentManagedThreadId} " +
            $"selectedId={SelectedItem?.Id.ToString() ?? "null"} shuffle={IsShuffleMode}");
        if (IsShuffleMode) { await PlayShuffleNextAsync(); return; }
        if (!Items.Any()) return;
        var list = Items.ToList();
        var currentIdx = SelectedItem != null
            ? list.FindIndex(i => i.Id == SelectedItem.Id)
            : -1;
        var nextIdx = currentIdx + 1;
        if (nextIdx >= list.Count) nextIdx = 0;
        System.Diagnostics.Debug.WriteLine(
            $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] PlayNext → currentIdx={currentIdx} nextIdx={nextIdx} " +
            $"nextId={list[nextIdx].Id} nextTitle=\"{list[nextIdx].Title}\"");
        PlayReleaseCommand.Execute(list[nextIdx]);
    }

    /// <summary>Active/désactive le mode shuffle. En mode shuffle, PlayNext tire
    /// une release aléatoire parmi les 130 000+ en base, pas seulement les 100 chargés.</summary>
    [RelayCommand]
    private async Task ToggleShuffle()
    {
        IsShuffleMode = !IsShuffleMode;
        if (IsShuffleMode)
        {
            // Démarrer immédiatement une musique aléatoire
            _shuffleHistory.Clear();
            await PlayShuffleNextAsync();
        }
    }

    private async Task PlayShuffleNextAsync()
    {
        // Guard anti-cascade : si un shuffle est déjà en cours de transition,
        // ou si le dernier shuffle date de moins de 500 ms (PlaybackStartFailed
        // en boucle sur pistes sans DAT), on ignore l'appel.
        if (_isShuffleTransitioning) return;
        var now = DateTime.UtcNow;
        if ((now - _lastShufflePlay).TotalMilliseconds < 500) return;

        _isShuffleTransitioning = true;
        _lastShufflePlay = now;
        try
        {
            var random = await _releaseService.GetRandomMusicReleaseAsync(_shuffleHistory);
            if (random == null)
            {
                // Toutes les musiques jouées — repartir de zéro
                _shuffleHistory.Clear();
                random = await _releaseService.GetRandomMusicReleaseAsync(_shuffleHistory);
            }
            if (random == null) return;
            _shuffleHistory.Add(random.Id);
            SelectedItem = random;
            _navigation.NavigateTo<DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel>(
                parameter: random.Id, tag: "autoplay");

            // Délai minimum de 2s entre deux shuffles pour éviter la cascade
            // sur les pistes qui échouent (0 DAT, format non reconnu)
            await Task.Delay(2000);
        }
        finally
        {
            _isShuffleTransitioning = false;
        }
    }
}
