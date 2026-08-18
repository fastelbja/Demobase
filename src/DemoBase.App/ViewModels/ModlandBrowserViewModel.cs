using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DemoBase.App.ViewModels;

// ─── ModlandTrackItemViewModel ─────────────────────────────────────────────────
// Wrapper autour de ModlandTrackRow (record immuable côté DemoBase.Data) exposant
// IsFavorite/IsDownloading comme propriétés observables — même schéma que
// GraphicCardViewModel (MediaBrowserViewModel.cs) pour les vignettes Graphics.

public class ModlandTrackItemViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public DemoBase.Data.ModlandTrackRow Track { get; }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set { _isFavorite = value; Notify(nameof(IsFavorite)); }
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set { _isDownloading = value; Notify(nameof(IsDownloading)); }
    }

    // 2026-07-30, demande utilisateur : "peux tu highlighter le fichier en cours de
    // lecture ?" — mis à jour par ModlandBrowserViewModel.ApplyCurrentlyPlayingHighlight,
    // pas au clic (CurrentTrack) mais au vrai changement de piste rapporté par le lecteur
    // (SoundtrackPlayerViewModel.CurrentFileName), pour rester juste aussi pendant
    // l'avance automatique d'une playlist "Tout jouer".
    private bool _isCurrentlyPlaying;
    public bool IsCurrentlyPlaying
    {
        get => _isCurrentlyPlaying;
        set { _isCurrentlyPlaying = value; Notify(nameof(IsCurrentlyPlaying)); }
    }

    public ModlandTrackItemViewModel(DemoBase.Data.ModlandTrackRow track, bool isFavorite)
    {
        Track = track;
        _isFavorite = isFavorite;
    }
}

// ─── ModlandBrowserViewModel ────────────────────────────────────────────────────
// 2026-07-30, demande utilisateur : nouvel onglet "Musique (modland)" du MediaBrowser
// pour parcourir/écouter le catalogue http://ftp.modland.com/ — deux modes de
// navigation ("Par format" / "Par auteur", cf. discussion), lecture via le lecteur
// tracker existant (téléchargement à la demande, cache local persistant — cf.
// DemoBase.App.Services.ModlandService), favoris/playlists partagés avec les
// musiques DAT via un SoundtrackDemozooId négatif synthétique (cf.
// FavoriteSoundtracksViewModel.BuildPlaylistAsync pour le pendant lecture).

public partial class ModlandBrowserViewModel : ObservableObject
{
    private readonly DemoBase.Data.ModlandCatalogService              _catalog;
    private readonly DemoBase.App.Services.ModlandService             _modland;
    private readonly TrackerPlayer.Core.Interfaces.ITrackerService?   _tracker;
    private readonly DemoBase.Data.FavoriteSoundtrackService?         _favService;

    private readonly HashSet<int> _favoriteModlandTrackIds = new();
    private readonly Dictionary<string, int> _pathToIndex = new(StringComparer.OrdinalIgnoreCase);

    // 2026-08-06, retour utilisateur ("dans le repertoire HSVC il y a un repertoire
    // 'MUSICIANS' mais il ne fait pas apparaitre les repertoires sous ce sous
    // repertoire") : certains "auteurs racine" sont en réalité des dossiers virtuels à
    // plusieurs niveaux (HVSC : "MUSICIANS/<Lettre>/<Artiste>"). _authorPathStack
    // retient les segments déjà descendus (fil d'Ariane) — vide = liste d'auteurs
    // racine normale (comportement d'origine, inchangé). _selectedAuthorFullPath est
    // le chemin COMPLET (avec préfixe du fil d'Ariane) une fois arrivé à un niveau
    // "feuille" (plus aucun sous-dossier en dessous) — distinct de SelectedAuthor.Name,
    // qui, en cours de descente, ne contient que le DERNIER segment cliqué.
    private readonly List<string> _authorPathStack = new();
    private string? _selectedAuthorFullPath;
    private CancellationTokenSource _authorSearchGeneration = new();
    // 2026-08-01 : générateur d'annulation dédié à la recherche par nom de fichier
    // (frappe rapide, comme _authorSearchGeneration ci-dessus) — séparé de celui-ci
    // pour ne pas interférer avec la recherche auteur, les deux pouvant en théorie
    // être tapées l'une après l'autre sans lien de dépendance entre elles.
    private CancellationTokenSource _fileNameSearchGeneration = new();

    [ObservableProperty] private bool   _isAuthorMode; // false = "Par format" (défaut), true = "Par auteur"
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private bool   _isLoadingAuthors;
    [ObservableProperty] private bool   _isLoadingTracks;
    [ObservableProperty] private string _formatFilter = "";
    [ObservableProperty] private string _authorSearch = "";
    // 2026-08-01, demande utilisateur : "je ne peux pas faire de recherche sur le nom
    // du fichier dans le browser modland [...] rajoute une zone de saisie de
    // recherche en dessous de 'Musiques'" — recherche sur TOUT le catalogue,
    // prioritaire sur la sélection Format/Auteur courante tant qu'elle n'est pas
    // vide (cf. LoadTracksAsync).
    [ObservableProperty] private string _fileNameSearch = "";
    [ObservableProperty] private DemoBase.Data.ModlandNameCount? _selectedFormat;
    [ObservableProperty] private DemoBase.Data.ModlandNameCount? _selectedAuthor;
    [ObservableProperty] private DemoBase.App.Views.Releases.SoundtrackPlayerView? _player;
    [ObservableProperty] private ModlandTrackItemViewModel? _currentTrack;
    [ObservableProperty] private bool   _isPlaying;
    [ObservableProperty] private string _syncStatusMessage = "";
    [ObservableProperty] private bool   _isSyncing;
    [ObservableProperty] private int    _syncPercent;
    [ObservableProperty] private string _syncMessage = "";

    // 2026-08-01, retour utilisateur ("à droite de cette zone de saisie, peux tu
    // mettre le chemin complet du fichier en cours de lecture ? (sans le
    // /pub/modules/...)") : ModlandTrackRow.RelativePath = "Format/Auteur/FileName" —
    // déjà sans préfixe hôte/pub, reconstruit directement depuis les colonnes du
    // catalogue (cf. ModlandCatalogService.cs).
    public string? CurrentTrackPath => CurrentTrack?.Track.RelativePath;

    partial void OnCurrentTrackChanged(ModlandTrackItemViewModel? value)
        => OnPropertyChanged(nameof(CurrentTrackPath));

    public ObservableCollection<DemoBase.Data.ModlandNameCount> VisibleFormats { get; } = [];
    public ObservableCollection<DemoBase.Data.ModlandNameCount> Authors        { get; } = [];
    public ObservableCollection<ModlandTrackItemViewModel>      Tracks         { get; } = [];

    private List<DemoBase.Data.ModlandNameCount> _allFormats = [];

    public bool IsLoaded { get; private set; }
    public bool HasTracks => Tracks.Count > 0;

    // 2026-08-01, demande utilisateur : "quand je clique sur un format, peux tu faire
    // revenir le scrollbar tout en haut pour les auteurs ? idem si je clique sur un
    // auteur qu'il revienne tout en haut pour la liste des musiques" — même schéma que
    // ScrollResetRequested (ReleaseListViewModel/PartyDetailViewModel), mais deux
    // événements distincts ici : cette vue a deux listes indépendantes à réinitialiser
    // séparément (Auteurs et Pistes), contrairement aux vues d'origine qui n'en ont
    // qu'une. Levés depuis LoadAuthorsAsync/LoadTracksAsync (pas directement dans les
    // handlers OnSelectedXxxChanged) pour couvrir uniformément TOUS les déclencheurs
    // d'un rechargement de liste (clic format/auteur, recherche auteur, bascule de
    // mode, synchronisation) — pas seulement le clic explicitement demandé.
    public event Action? AuthorsScrollResetRequested;
    public event Action? TracksScrollResetRequested;

    // 2026-08-06 : fil d'Ariane pour la descente dans un "auteur" composé de plusieurs
    // niveaux (cf. _authorPathStack ci-dessus) — vide (chaîne "") quand on est à la
    // racine, ex. "MUSICIANS › H" une fois descendu dans la lettre H de HVSC.
    public string AuthorBreadcrumb    => string.Join(" › ", _authorPathStack);
    public bool   CanGoUpAuthorFolder => _authorPathStack.Count > 0;

    [RelayCommand]
    private async Task GoUpAuthorFolder()
    {
        if (_authorPathStack.Count == 0) return;

        // 2026-08-06, retour utilisateur ("coop-doc Holiday est un sous repertoire de
        // Twilight (DE), si je clique sur la fleche de retour, je souhaite 'remonter
        // d'un niveau' et afficher les fichiers racine de Twilight (DE)") : une piste
        // "feuille" cliquée dans la colonne Auteurs (ex. "coop-Doc Holiday", qui n'a
        // elle-même AUCUN sous-dossier) ne pousse RIEN sur le fil d'Ariane — ce n'est
        // pas un dossier dans lequel descendre, juste un auteur dont on affiche les
        // pistes (cf. HandleAuthorSelectionAsync, branche "feuille" : seul
        // `_selectedAuthorFullPath` change, `_authorPathStack` reste identique). Sans
        // ce cas particulier, cliquer "retour" depuis les pistes de "coop-Doc Holiday"
        // dépilait directement "Twilight (DE)" du fil d'Ariane (le seul niveau présent
        // dans la pile) et renvoyait d'un coup à la liste complète/racine, en sautant
        // le niveau intermédiaire attendu ("Twilight (DE)" avec ses pistes racine et
        // "coop-Doc Holiday" toujours listé à côté). Le premier "retour" doit donc
        // juste quitter la vue des pistes de la feuille et revenir à l'affichage du
        // dossier courant (fil d'Ariane INCHANGÉ) — seul un DEUXIÈME clic "retour"
        // remonte réellement d'un niveau de dossier.
        if (_selectedAuthorFullPath != null)
        {
            _selectedAuthorFullPath = null;
            SelectedAuthor = null;
            await LoadExactLevelTracksAsync(string.Join("/", _authorPathStack));
            return;
        }

        _authorPathStack.RemoveAt(_authorPathStack.Count - 1);
        OnPropertyChanged(nameof(AuthorBreadcrumb));
        OnPropertyChanged(nameof(CanGoUpAuthorFolder));
        SelectedAuthor = null;
        await LoadAuthorsAsync();
        // Le niveau vers lequel on remonte peut lui aussi avoir des pistes placées
        // directement dessus (ex. "unknown"), en plus de ses sous-dossiers. Si on
        // remonte jusqu'à la racine absolue (pile vide), rien à charger : Tracks vide,
        // comme au tout premier affichage de la colonne Auteurs.
        if (_authorPathStack.Count > 0)
            await LoadExactLevelTracksAsync(string.Join("/", _authorPathStack));
        else
        {
            Tracks.Clear();
            OnPropertyChanged(nameof(HasTracks));
        }
    }

    /// <summary>Repart de la racine du fil d'Ariane — appelé chaque fois que le
    /// contexte de navigation change indépendamment d'un clic sur un auteur (recherche,
    /// changement de format/mode, synchronisation) : une descente dans un sous-dossier
    /// n'a de sens que par rapport au format/à la recherche qui l'a affiché.</summary>
    private void ResetAuthorNavigation()
    {
        _authorPathStack.Clear();
        _selectedAuthorFullPath = null;
        OnPropertyChanged(nameof(AuthorBreadcrumb));
        OnPropertyChanged(nameof(CanGoUpAuthorFolder));
    }

    public ModlandBrowserViewModel(
        DemoBase.Data.ModlandCatalogService catalog,
        DemoBase.App.Services.ModlandService modland,
        TrackerPlayer.Core.Interfaces.ITrackerService? tracker = null,
        DemoBase.Data.FavoriteSoundtrackService? favService = null)
    {
        _catalog    = catalog;
        _modland    = modland;
        _tracker    = tracker;
        _favService = favService;
    }

    // ── Chargement initial ───────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        if (IsLoaded) return;
        IsLoaded = true;
        IsLoading = true;
        try
        {
            await RefreshSnapshotInfoAsync();
            await RefreshFavoriteIdsAsync();

            _allFormats = await _catalog.GetFormatsAsync();
            ApplyFormatFilter();

            await LoadAuthorsAsync();
        }
        finally { IsLoading = false; }
    }

    private async Task RefreshSnapshotInfoAsync()
    {
        var info = await _modland.GetSnapshotInfoAsync();
        SyncStatusMessage = info != null
            ? $"{info.TrackCount:N0} pistes — synchronisé le {info.ImportedAt.ToLocalTime():dd/MM/yyyy HH:mm}"
            : "Catalogue non synchronisé — cliquez sur « Rafraîchir » pour télécharger le catalogue Modland (~6 Mo).";
    }

    private async Task RefreshFavoriteIdsAsync()
    {
        _favoriteModlandTrackIds.Clear();
        if (_favService == null) return;
        var favs = await _favService.GetAllAsync();
        foreach (var f in favs)
            if (f.SoundtrackDemozooId < 0)
                _favoriteModlandTrackIds.Add(-f.SoundtrackDemozooId);
    }

    // ── Formats (mode "Par format") ──────────────────────────────────────────

    partial void OnFormatFilterChanged(string value) => ApplyFormatFilter();

    private void ApplyFormatFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(FormatFilter)
            ? _allFormats
            : _allFormats.Where(f => f.Name.Contains(FormatFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        VisibleFormats.Clear();
        foreach (var f in filtered) VisibleFormats.Add(f);
    }

    partial void OnSelectedFormatChanged(DemoBase.Data.ModlandNameCount? value)
    {
        ResetAuthorNavigation();
        SelectedAuthor = null;
        Tracks.Clear();
        OnPropertyChanged(nameof(HasTracks));
        _ = LoadAuthorsAsync();
    }

    // ── Auteurs (mode "Par format" scoped, ou "Par auteur" global) ──────────

    partial void OnAuthorSearchChanged(string value)
    {
        // 2026-08-06 : une recherche n'a de sens qu'au niveau racine — repart du fil
        // d'Ariane à zéro si l'utilisateur tapait pendant qu'il était descendu dans un
        // auteur composé (ex. HVSC : MUSICIANS › H).
        ResetAuthorNavigation();
        _ = LoadAuthorsAsync();
    }

    [RelayCommand]
    private async Task SetFormatMode()
    {
        if (!IsAuthorMode) return;
        IsAuthorMode = false;
        ResetAuthorNavigation();
        SelectedAuthor = null;
        Tracks.Clear();
        OnPropertyChanged(nameof(HasTracks));
        await LoadAuthorsAsync();
    }

    [RelayCommand]
    private async Task SetAuthorMode()
    {
        if (IsAuthorMode) return;
        IsAuthorMode = true;
        ResetAuthorNavigation();
        SelectedFormat = null; // ne redéclenche pas LoadAuthorsAsync : déjà fait ci-dessous
        SelectedAuthor = null;
        Tracks.Clear();
        OnPropertyChanged(nameof(HasTracks));
        await LoadAuthorsAsync();
    }

    /// <summary>Recharge la liste d'auteurs — avec un léger anti-rebond (250 ms) pour
    /// éviter une requête SQL à chaque frappe pendant une recherche (des dizaines de
    /// milliers d'auteurs au total), et une annulation de la requête précédente si
    /// une nouvelle frappe/sélection survient entre-temps.</summary>
    private async Task LoadAuthorsAsync()
    {
        _authorSearchGeneration.Cancel();
        _authorSearchGeneration = new CancellationTokenSource();
        var token = _authorSearchGeneration.Token;

        try
        {
            await Task.Delay(250, token);
        }
        catch (OperationCanceledException) { return; }

        // En mode "Par format" sans format sélectionné, rien à afficher — évite de
        // charger les 200 premiers auteurs toutes catégories confondues sans contexte.
        // Ne s'applique pas quand on est descendu dans un auteur composé (fil d'Ariane
        // non vide) : dans ce cas un format a nécessairement déjà été sélectionné (ou
        // on est en mode "Par auteur") pour avoir pu descendre.
        if (!IsAuthorMode && SelectedFormat == null && _authorPathStack.Count == 0)
        {
            Authors.Clear();
            return;
        }

        IsLoadingAuthors = true;
        try
        {
            List<DemoBase.Data.ModlandNameCount> authors;
            if (_authorPathStack.Count > 0)
            {
                // 2026-08-06 : descente dans un auteur composé à plusieurs niveaux
                // (ex. HVSC : MUSICIANS › H) — sous-dossiers immédiatement sous le
                // chemin courant du fil d'Ariane, PAS une recherche/liste racine.
                var path = string.Join("/", _authorPathStack);
                authors = await _catalog.GetAuthorSubfoldersAsync(
                    path, format: IsAuthorMode ? null : SelectedFormat?.Name, ct: token);
            }
            else
            {
                authors = await _catalog.GetAuthorsAsync(
                    format: IsAuthorMode ? null : SelectedFormat?.Name,
                    search: AuthorSearch,
                    limit: 300, ct: token);
            }
            if (token.IsCancellationRequested) return;

            Authors.Clear();
            foreach (var a in authors) Authors.Add(a);
            AuthorsScrollResetRequested?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally { if (!token.IsCancellationRequested) IsLoadingAuthors = false; }
    }

    partial void OnSelectedAuthorChanged(DemoBase.Data.ModlandNameCount? value)
        => _ = HandleAuthorSelectionAsync(value);

    /// <summary>
    /// 2026-08-06, retour utilisateur ("dans le repertoire HSVC il y a un repertoire
    /// 'MUSICIANS' mais il ne fait pas apparaitre les repertoires sous ce sous
    /// repertoire") : avant de charger les pistes d'un auteur cliqué, vérifie s'il
    /// reste des sous-dossiers en dessous (auteur composé à plusieurs niveaux, cf.
    /// GetAuthorSubfoldersAsync). Si oui, on DESCEND dans l'arborescence (fil d'Ariane)
    /// au lieu d'aplatir toutes les pistes des niveaux inférieurs d'un coup — c'était à
    /// la fois une UX qui cache la vraie arborescence ET la cause probable du blocage
    /// signalé pour les listes de plus de 1000 pistes (HVSC range ~50 000 fichiers
    /// sous "MUSICIANS").
    /// </summary>
    private async Task HandleAuthorSelectionAsync(DemoBase.Data.ModlandNameCount? value)
    {
        if (value == null)
        {
            _selectedAuthorFullPath = null;
            await LoadTracksAsync();
            return;
        }

        var candidatePath = _authorPathStack.Count > 0
            ? string.Join("/", _authorPathStack) + "/" + value.Name
            : value.Name;

        List<DemoBase.Data.ModlandNameCount> subfolders;
        try
        {
            subfolders = await _catalog.GetAuthorSubfoldersAsync(
                candidatePath, format: IsAuthorMode ? null : SelectedFormat?.Name);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MODLAND] Vérification des sous-dossiers de '{candidatePath}' échouée : {ex.Message}");
            subfolders = new();
        }

        if (subfolders.Count > 0)
        {
            _authorPathStack.Add(value.Name);
            OnPropertyChanged(nameof(AuthorBreadcrumb));
            OnPropertyChanged(nameof(CanGoUpAuthorFolder));
            _selectedAuthorFullPath = null;
            // Remet la sélection à null : réutilise le même ListBox pour afficher le
            // niveau suivant (nouveaux items, ex. les lettres A-Z sous "MUSICIANS") —
            // sans ça, l'ancien item resterait visuellement "sélectionné" au mauvais
            // niveau. Redéclenche ce handler avec value=null, qui se contente de vider
            // Tracks (branche ci-dessus) : sans effet de bord sur la descente — cet
            // effet de bord (Clear synchrone avant le premier "await" de LoadTracksAsync,
            // cf. son code) s'exécute AVANT que LoadExactLevelTracksAsync ci-dessous ne
            // repeuple Tracks, donc pas de perte de données malgré l'ordre d'appel.
            SelectedAuthor = null;
            await LoadAuthorsAsync();
            // 2026-08-06, retour utilisateur ("si je choisi un repertoire qui a des
            // sous repertoires il n'affiche pas les fichiers qui se trouvent à la
            // racine") : un dossier composé peut contenir À LA FOIS des sous-dossiers
            // (ci-dessus, dans Authors) ET des pistes directement à CE niveau (ex.
            // l'auteur générique "unknown", qui a des pistes isolées EN PLUS de ses
            // sous-dossiers de compilation) — chargées séparément ici, sans jamais
            // inclure celles des sous-dossiers (cf. GetTracksAtExactAuthorAsync).
            await LoadExactLevelTracksAsync(candidatePath);
            return;
        }

        // Niveau "feuille" : plus aucun sous-dossier — c'est un vrai auteur, on charge
        // ses pistes avec le chemin COMPLET (fil d'Ariane + segment cliqué), pas juste
        // value.Name (qui ne contiendrait que le dernier segment, ex. "Hubbard_Rob"
        // sans "MUSICIANS/H/" devant).
        _selectedAuthorFullPath = candidatePath;
        await LoadTracksAsync();
    }

    /// <summary>Pistes placées EXACTEMENT à <paramref name="path"/> — complète la liste
    /// de sous-dossiers affichée dans Authors quand on descend dans un auteur composé
    /// (cf. HandleAuthorSelectionAsync/GoUpAuthorFolder), sans jamais y mélanger les
    /// pistes des sous-dossiers eux-mêmes (celles-ci ne sont visibles qu'en descendant
    /// jusqu'à leur propre niveau "feuille").</summary>
    private async Task LoadExactLevelTracksAsync(string path)
    {
        Tracks.Clear();
        OnPropertyChanged(nameof(HasTracks));
        try
        {
            var rows = await _catalog.GetTracksAtExactAuthorAsync(
                path, format: IsAuthorMode ? null : SelectedFormat?.Name);
            foreach (var row in rows)
                Tracks.Add(new ModlandTrackItemViewModel(row, _favoriteModlandTrackIds.Contains(row.Id)));
            OnPropertyChanged(nameof(HasTracks));
            ApplyCurrentlyPlayingHighlight();
            TracksScrollResetRequested?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MODLAND] Chargement des pistes directes de '{path}' échoué : {ex.Message}");
        }
    }

    // 2026-08-01, demande utilisateur : recherche par nom de fichier — mêmes items
    // (ModlandTrackItemViewModel) affichés dans la même colonne Pistes, mais
    // interrogeant tout le catalogue plutôt qu'un auteur précis. Cf. LoadTracksAsync
    // ci-dessous pour la priorité entre les deux sources.
    partial void OnFileNameSearchChanged(string value) => _ = LoadTracksAsync();

    /// <summary>
    /// Recharge la colonne Pistes. Deux sources mutuellement exclusives :
    /// - <see cref="FileNameSearch"/> non vide → recherche par nom de fichier sur tout
    ///   le catalogue (anti-rebond 250ms, comme LoadAuthorsAsync — frappe rapide) ;
    ///   PRIORITAIRE sur la sélection Format/Auteur courante tant qu'elle est non vide.
    /// - Sinon → comportement d'origine, basé sur <see cref="SelectedAuthor"/>/
    ///   <see cref="SelectedFormat"/> (pas de débounce ici — un clic sur un auteur est
    ///   un événement discret, pas une frappe rapide).
    /// </summary>
    private async Task LoadTracksAsync()
    {
        if (!string.IsNullOrWhiteSpace(FileNameSearch))
        {
            _fileNameSearchGeneration.Cancel();
            _fileNameSearchGeneration = new CancellationTokenSource();
            var searchToken = _fileNameSearchGeneration.Token;

            try
            {
                await Task.Delay(250, searchToken);
            }
            catch (OperationCanceledException) { return; }

            IsLoadingTracks = true;
            try
            {
                var rows = await _catalog.SearchTracksByFileNameAsync(FileNameSearch, limit: 300, ct: searchToken);
                if (searchToken.IsCancellationRequested) return;

                Tracks.Clear();
                foreach (var row in rows)
                    Tracks.Add(new ModlandTrackItemViewModel(row, _favoriteModlandTrackIds.Contains(row.Id)));
                OnPropertyChanged(nameof(HasTracks));
                ApplyCurrentlyPlayingHighlight();
                TracksScrollResetRequested?.Invoke();
            }
            catch (OperationCanceledException) { }
            finally { if (!searchToken.IsCancellationRequested) IsLoadingTracks = false; }
            return;
        }

        // Une recherche en cours (débounce pas encore écoulé, ou requête en vol) doit
        // être annulée si l'utilisateur vide la zone de recherche entre-temps — sinon
        // son résultat pourrait arriver APRÈS le rechargement ci-dessous et écraser la
        // liste par auteur qu'on vient de recharger.
        _fileNameSearchGeneration.Cancel();

        Tracks.Clear();
        OnPropertyChanged(nameof(HasTracks));
        // 2026-08-06 : chemin COMPLET (fil d'Ariane inclus si descendu dans un auteur
        // composé, cf. HandleAuthorSelectionAsync) — PAS SelectedAuthor?.Name, qui ne
        // contiendrait que le dernier segment cliqué pour un auteur à plusieurs niveaux.
        if (_selectedAuthorFullPath == null) return;

        IsLoadingTracks = true;
        try
        {
            var rows = IsAuthorMode || SelectedFormat == null
                ? await _catalog.GetTracksByAuthorAsync(_selectedAuthorFullPath)
                : await _catalog.GetTracksAsync(SelectedFormat.Name, _selectedAuthorFullPath);

            foreach (var row in rows)
                Tracks.Add(new ModlandTrackItemViewModel(row, _favoriteModlandTrackIds.Contains(row.Id)));
            OnPropertyChanged(nameof(HasTracks));
            // Ré-appliquer le highlight "en cours de lecture" — les nouveaux items
            // n'ont pas hérité de l'état de l'ancienne collection (ex. on rebrowse
            // vers l'auteur/piste en train de jouer après être passé ailleurs).
            ApplyCurrentlyPlayingHighlight();
            TracksScrollResetRequested?.Invoke();
        }
        finally { IsLoadingTracks = false; }
    }

    // ── Synchronisation (bouton "Rafraîchir") ────────────────────────────────

    [RelayCommand]
    private async Task Sync()
    {
        if (IsSyncing) return;
        IsSyncing = true;
        SyncPercent = 0;
        SyncMessage = "Démarrage…";
        try
        {
            var progress = new Progress<DemoBase.App.Services.ModlandSyncProgress>(p =>
            {
                SyncMessage = p.Message;
                SyncPercent = p.Percent;
            });
            var count = await _modland.SyncAsync(progress);

            _allFormats = await _catalog.GetFormatsAsync();
            ApplyFormatFilter();
            await RefreshSnapshotInfoAsync();

            // Recharger le niveau auteurs/pistes courant pour refléter le nouveau
            // catalogue immédiatement (au cas où l'utilisateur avait déjà navigué).
            // 2026-08-06 : repart aussi de la racine du fil d'Ariane — un chemin
            // composé descendu avant la synchronisation (ex. "MUSICIANS/H") pourrait ne
            // plus exister à l'identique dans le nouveau catalogue.
            ResetAuthorNavigation();
            SelectedAuthor = null;
            Tracks.Clear();
            OnPropertyChanged(nameof(HasTracks));
            await LoadAuthorsAsync();

            // 2026-07-30, retour utilisateur : une sync "réussie" (aucune exception) mais
            // qui ne trouve AUCUNE piste passait inaperçue (juste un toast de succès
            // trompeur) — le format réel du listing interne d'allmods.zip n'a pas pu être
            // vérifié depuis l'environnement de dev (accès réseau restreint). Signalé
            // maintenant explicitement plutôt que de laisser croire que tout va bien.
            if (count == 0)
                DemoBase.App.Controls.StatusScrollerControl.Post(
                    "Synchronisation Modland terminée mais aucune piste trouvée — le format du " +
                    "listing dans allmods.zip ne correspond peut-être pas à ce qui est attendu. " +
                    "Voir la fenêtre Sortie (mode Debug) pour le détail.", isError: true);
            else
                DemoBase.App.Controls.StatusScrollerControl.Post(
                    $"Catalogue Modland synchronisé — {count:N0} pistes.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MODLAND] Sync échouée : {ex.Message}");
            DemoBase.App.Controls.StatusScrollerControl.Post(
                $"Échec de la synchronisation Modland : {ex.Message}", isError: true);
        }
        finally { IsSyncing = false; }
    }

    // ── Lecture ───────────────────────────────────────────────────────────────

    // 2026-07-30, demande utilisateur : "peux tu highlighter le fichier en cours de
    // lecture ?" — nom de fichier (pas l'item cliqué) rapporté en dernier par le
    // lecteur, seule source fiable pendant l'avance automatique d'une playlist
    // "Tout jouer" (CurrentTrack, lui, ne bouge qu'au clic explicite — cf. PlayTrack/
    // PlayAllTracks ci-dessous, jamais mis à jour piste par piste dans une playlist).
    private string? _currentlyPlayingFileName;

    private void ApplyCurrentlyPlayingHighlight()
    {
        foreach (var t in Tracks)
            t.IsCurrentlyPlaying = _currentlyPlayingFileName != null
                && string.Equals(t.Track.FileName, _currentlyPlayingFileName, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsurePlayer()
    {
        if (Player == null && _tracker != null)
        {
            Player = new DemoBase.App.Views.Releases.SoundtrackPlayerView(_tracker);
            Player.Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(DemoBase.App.ViewModels.SoundtrackPlayerViewModel.CurrentFileName)) return;
                _currentlyPlayingFileName = Player.Vm.CurrentFileName;
                ApplyCurrentlyPlayingHighlight();
            };
        }
    }

    // 2026-07-30, retour utilisateur : "quand tu lance le telecharger peux tu
    // changer l'icone ou l'afficher quelque part ? car la souris reste en fleche
    // et on l'impression qu'il ne fait rien." — en plus du fond de ligne teinté et
    // de l'icône ⬇ agrandie côté XAML (IsDownloading), le curseur système lui-même
    // passe en "Attente" pendant tout téléchargement, quel que soit l'endroit de la
    // fenêtre où se trouve la souris. Compteur (pas juste un bool) : PlayAllTracks
    // télécharge plusieurs pistes à la suite — sans compteur, la fin du 1er
    // téléchargement remettrait le curseur normal alors que les suivants tournent
    // encore.
    private int _activeDownloadCount;

    private void BeginDownloadCursor()
    {
        if (++_activeDownloadCount == 1)
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
    }

    private void EndDownloadCursor()
    {
        if (--_activeDownloadCount <= 0)
        {
            _activeDownloadCount = 0;
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    [RelayCommand]
    private async Task PlayTrack(ModlandTrackItemViewModel item)
    {
        if (_tracker == null) return;
        item.IsDownloading = true;
        BeginDownloadCursor();
        try
        {
            var path = await _modland.DownloadTrackAsync(item.Track);
            EnsurePlayer();
            if (Player == null) return;
            await Player.OpenAsync(path);
            CurrentTrack = item;
            IsPlaying    = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MODLAND] Lecture échouée ({item.Track.FileName}) : {ex.Message}");
            DemoBase.App.Controls.StatusScrollerControl.Post(
                $"Impossible de lire « {item.Track.FileName} » : {ex.Message}", isError: true);
        }
        finally { item.IsDownloading = false; EndDownloadCursor(); }
    }

    [RelayCommand]
    private async Task PlayAllTracks()
    {
        if (_tracker == null || Tracks.Count == 0) return;

        var items = Tracks.ToList();
        var paths = new List<string>();
        IsLoadingTracks = true;
        BeginDownloadCursor();
        try
        {
            foreach (var item in items)
            {
                try
                {
                    item.IsDownloading = true;
                    paths.Add(await _modland.DownloadTrackAsync(item.Track));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MODLAND] Échec téléchargement {item.Track.FileName} : {ex.Message}");
                }
                finally { item.IsDownloading = false; }
            }
        }
        finally { IsLoadingTracks = false; EndDownloadCursor(); }

        if (paths.Count == 0) return;

        EnsurePlayer();
        if (Player == null) return;

        _pathToIndex.Clear();
        for (int i = 0; i < paths.Count && i < items.Count; i++)
            _pathToIndex[paths[i]] = i;

        await Player.Vm.LoadFilesAsync(paths);
        CurrentTrack = items[0];
        IsPlaying    = true;
    }

    [RelayCommand]
    private async Task ToggleFavorite(ModlandTrackItemViewModel item)
    {
        if (_favService == null) return;
        var syntheticId = -item.Track.Id;
        if (item.IsFavorite)
        {
            await _favService.RemoveAsync(syntheticId);
            _favoriteModlandTrackIds.Remove(item.Track.Id);
            item.IsFavorite = false;
        }
        else
        {
            await _favService.AddAsync(new DemoBase.Core.Models.FavoriteSoundtrack
            {
                SoundtrackDemozooId = syntheticId,
                Title               = item.Track.FileName,
                AuthorNames         = item.Track.Author,
                RomName             = item.Track.FileName,
                // ZipPath stocke le chemin relatif Modland ("Format/Auteur/fichier") — pas
                // un chemin de ZIP DAT. BuildPlaylistAsync (FavoriteSoundtracksViewModel)
                // reconnaît un SoundtrackDemozooId négatif et route vers ModlandService
                // plutôt que vers l'extraction ZIP habituelle. Cf. RESUME_PROJET.md.
                ZipPath             = item.Track.RelativePath,
                ReleaseTitle        = $"Modland — {item.Track.Format}",
            });
            _favoriteModlandTrackIds.Add(item.Track.Id);
            item.IsFavorite = true;
        }
    }
}
