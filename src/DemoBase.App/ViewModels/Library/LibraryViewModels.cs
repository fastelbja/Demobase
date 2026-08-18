using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.App.ViewModels.Releases;
using DemoBase.Core.Interfaces;
using DemoBase.Core.Models;
using System.Collections.ObjectModel;

namespace DemoBase.App.ViewModels.Library;

// ─── Shared base for list VMs with search + infinite scroll ──────────────────

public abstract partial class LibraryListViewModelBase<T> : ObservableObject
{
    protected readonly INavigationService _navigation;

    [ObservableProperty] private ObservableCollection<T> _items = [];
    [ObservableProperty] private string  _searchQuery   = string.Empty;
    [ObservableProperty] private bool    _isLoading;
    [ObservableProperty] private bool    _isLoadingMore;
    [ObservableProperty] private bool    _hasMorePages;
    [ObservableProperty] private int     _totalCount;
    [ObservableProperty] private int     _currentPage   = 1;
    [ObservableProperty] private T?      _selectedItem;

    /// <summary>
    /// Sélectionne l'item à l'offset donné (+1 = suivant, -1 = précédent).
    /// Retourne le nouvel index, ou -1 si impossible.
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

    protected const int PageSize = 100;
    private CancellationTokenSource? _debounce;

    // Mémorise la position de scroll par clé de filtre actif
    // clé = filtre actif (lettre, année, texte…) ou "" pour "pas de filtre"
    private readonly Dictionary<string, double> _scrollOffsets = new();

    // Déclenché quand un filtre change — la vue doit scroller en haut
    public event Action? ScrollResetRequested;

    // Déclenché par MainViewModel quand cette vue redevient active → restaurer le scroll
    public event Action? ScrollRestoreRequested;
    public void TriggerScrollRestore() => ScrollRestoreRequested?.Invoke();

    public double SavedScrollOffset
    {
        get => _scrollOffsets.TryGetValue(ScrollKey, out var v) ? v : 0;
        set => _scrollOffsets[ScrollKey] = value;
    }

    // Clé représentant le filtre courant — surchargeable dans les sous-classes
    protected virtual string ScrollKey => SearchQuery ?? "";

    protected LibraryListViewModelBase(INavigationService navigation)
        => _navigation = navigation;

    [RelayCommand]
    public async Task LoadAsync()
    {
        // Si déjà chargé (VM singleton revenu), ne pas recharger
        if (Items.Count > 0 && !IsLoading) return;
        await ForceReloadAsync();
    }

    // Rechargement forcé — utilisé par les filtres (lettre, année, recherche)
    protected async Task ForceReloadAsync()
    {
        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {GetType().Name}.ForceReloadAsync ScrollKey={ScrollKey} savedOffset={SavedScrollOffset}");
        // Remettre le scroll à 0 pour ce nouveau filtre et notifier la vue
        _scrollOffsets[ScrollKey] = 0;
        CurrentPage  = 1;
        IsLoading    = true;
        HasMorePages = false;
        try
        {
            var (items, total) = await FetchAsync(SearchQuery, 1);
            Items        = new ObservableCollection<T>(items);
            TotalCount   = total;
            HasMorePages = Items.Count < total;
            // Demander le reset APRÈS que les items sont chargés
            ScrollResetRequested?.Invoke();
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (IsLoadingMore || !HasMorePages) return;
        IsLoadingMore = true;
        CurrentPage++;
        try
        {
            var (items, _) = await FetchAsync(SearchQuery, CurrentPage);
            foreach (var item in items) Items.Add(item);
            HasMorePages = Items.Count < TotalCount;
        }
        finally { IsLoadingMore = false; }
    }

    partial void OnSearchQueryChanged(string value) => _ = DebounceAsync();

    private async Task DebounceAsync()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        try { await Task.Delay(300, _debounce.Token); await ForceReloadAsync(); }
        catch (OperationCanceledException) { }
    }

    protected abstract Task<(IEnumerable<T> Items, int Total)> FetchAsync(string? query, int page);
}

// ─── Letter filter mixin ──────────────────────────────────────────────────────

public abstract partial class ReleaserListViewModelBase : LibraryListViewModelBase<Releaser>
{
    // Toutes les lettres A-Z + # pour les non-alphabétiques
    private static readonly IReadOnlyList<LetterChip> AllLetterChips =
        Enumerable.Range('A', 26)
            .Select(c => new LetterChip { Letter = ((char)c).ToString() })
            .Prepend(new LetterChip { Letter = "#" })
            .ToList();

    [ObservableProperty] private ObservableCollection<LetterChip> _letterChips =
        new(AllLetterChips.Select(l => new LetterChip { Letter = l.Letter }));

    [ObservableProperty] private string? _selectedLetter;

    protected ReleaserListViewModelBase(INavigationService navigation)
        : base(navigation) { }

    protected override string ScrollKey =>
        $"{SelectedLetter ?? ""}|{SearchQuery ?? ""}";

    [RelayCommand]
    public async Task SelectLetterAsync(LetterChip? chip)
    {
        foreach (var c in LetterChips) c.IsSelected = false;

        if (chip != null && SelectedLetter != chip.Letter)
        {
            chip.IsSelected = true;
            SelectedLetter  = chip.Letter;
        }
        else
        {
            SelectedLetter = null;
        }
        await ForceReloadAsync();
    }
}

// ─── GroupListViewModel ───────────────────────────────────────────────────────

public partial class GroupListViewModel : ReleaserListViewModelBase
{
    private readonly IUnitOfWork _uow;

    public GroupListViewModel(INavigationService navigation, IUnitOfWork uow)
        : base(navigation) => _uow = uow;

    protected override Task<(IEnumerable<Releaser> Items, int Total)> FetchAsync(
        string? query, int page) =>
        _uow.Releasers.SearchPagedAsync(query, isGroup: true, page, PageSize, SelectedLetter);

    [RelayCommand]
    private void OpenGroup(Releaser group) =>
        _navigation.NavigateTo<ReleaserDetailViewModel>(group.Id);
}

// ─── ScenerListViewModel (Artistes) ──────────────────────────────────────────

public partial class ScenerListViewModel : ReleaserListViewModelBase
{
    private readonly IUnitOfWork _uow;

    public ScenerListViewModel(INavigationService navigation, IUnitOfWork uow)
        : base(navigation) => _uow = uow;

    protected override Task<(IEnumerable<Releaser> Items, int Total)> FetchAsync(
        string? query, int page) =>
        _uow.Releasers.SearchPagedAsync(query, isGroup: false, page, PageSize, SelectedLetter);

    [RelayCommand]
    private void OpenScener(Releaser scener) =>
        _navigation.NavigateTo<ReleaserDetailViewModel>(scener.Id);
}

// ─── LetterChip ───────────────────────────────────────────────────────────────

public partial class LetterChip : ObservableObject
{
    public string Letter { get; set; } = string.Empty;
    [ObservableProperty] private bool _isSelected;
}

// ─── PlatformListViewModel ────────────────────────────────────────────────────

public partial class PlatformListViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IUnitOfWork        _uow;

    [ObservableProperty] private ObservableCollection<Platform> _platforms = [];
    [ObservableProperty] private bool    _isLoading;
    [ObservableProperty] private string  _searchQuery = string.Empty;

    private List<Platform> _allPlatforms = [];
    private CancellationTokenSource? _debounce;

    public PlatformListViewModel(INavigationService navigation, IUnitOfWork uow)
    {
        _navigation = navigation;
        _uow        = uow;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _allPlatforms = (await _uow.Platforms.GetAllAsync())
                .OrderBy(p => p.Name).ToList();

            // Fond rouge "pas de config" (2026-07-24) : une seule requête groupée pour
            // savoir quelles plateformes ont au moins une EmulatorConfig, plutôt qu'une
            // requête par plateforme — cf. HasEmulatorConfig sur Platform (Models.cs).
            var configuredIds = await _uow.Emulators.GetConfiguredPlatformIdsAsync();
            foreach (var platform in _allPlatforms)
                platform.HasEmulatorConfig = configuredIds.Contains(platform.Id);

            ApplyFilter();
        }
        finally { IsLoading = false; }
    }

    partial void OnSearchQueryChanged(string value) => _ = DebounceAsync();

    private async Task DebounceAsync()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        try { await Task.Delay(250, _debounce.Token); ApplyFilter(); }
        catch (OperationCanceledException) { }
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(SearchQuery)
            ? _allPlatforms
            : _allPlatforms.Where(p => p.Name.Contains(SearchQuery,
                  StringComparison.OrdinalIgnoreCase)).ToList();
        Platforms = new ObservableCollection<Platform>(filtered);
    }

    [RelayCommand]
    private void FilterByPlatform(Platform platform) =>
        _navigation.NavigateTo<ReleaseListViewModel>(platform.Id, tag: platform.Name);
}

// ─── PartyListViewModel ───────────────────────────────────────────────────────

public partial class PartyListViewModel : LibraryListViewModelBase<PartyListItem>
{
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private ObservableCollection<YearChip> _yearChips = [];
    [ObservableProperty] private int? _selectedYear;

    // Tri : "alpha" (défaut) ou "date" — toujours visible, indépendant du
    // filtre par année (comme demandé : pas conditionné à SelectedYear).
    [ObservableProperty] private string _sortMode = "alpha";
    public bool SortByAlpha => SortMode == "alpha";
    public bool SortByDate  => SortMode == "date";

    public PartyListViewModel(INavigationService navigation, IUnitOfWork uow)
        : base(navigation) => _uow = uow;

    protected override string ScrollKey =>
        $"{SelectedYear?.ToString() ?? ""}|{SearchQuery ?? ""}|{SortMode}";

    protected override async Task<(IEnumerable<PartyListItem> Items, int Total)> FetchAsync(
        string? query, int page)
    {
        var (parties, total) = await _uow.Parties.SearchPagedAsync(query, page, PageSize, SelectedYear, SortMode);
        var ids = parties.Select(p => p.Id).ToList();

        // Compter les releases par party via CompetitionPlacings
        var counts = await _uow.Parties.GetReleaseCountsByPartyIdsAsync(ids);

        var items = parties.Select(p => new PartyListItem
        {
            Party        = p,
            ReleaseCount = counts.TryGetValue(p.Id, out var c) ? c : 0,
        });
        return (items, total);
    }

    // Chargement initial : récupère les années puis lance la liste
    public async Task InitAsync()
    {
        if (YearChips.Count == 0)
        {
            var years = await _uow.Parties.GetAvailableYearsAsync();
            YearChips = new ObservableCollection<YearChip>(
                years.Select(y => new YearChip { Year = y, Label = y.ToString() }));
        }
        await LoadCommand.ExecuteAsync(null);
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
        await ForceReloadAsync();
    }

    // Tri en mémoire impossible ici (la liste est paginée côté serveur),
    // donc on relance une requête avec le nouveau sortMode plutôt que de
    // re-trier _allItemsCache comme dans ReleaserDetailViewModel.
    [RelayCommand]
    private async Task SetSort(string mode)
    {
        if (SortMode == mode) return;
        SortMode = mode;
        OnPropertyChanged(nameof(SortByAlpha));
        OnPropertyChanged(nameof(SortByDate));
        await ForceReloadAsync();
    }

    [RelayCommand]
    private void OpenParty(PartyListItem item) =>
        _navigation.NavigateTo<PartyDetailViewModel>(item.Id);
}

// ─── YearChip ─────────────────────────────────────────────────────────────────

public partial class YearChip : ObservableObject
{
    public int    Year  { get; set; }
    public string Label { get; set; } = string.Empty;
    [ObservableProperty] private bool _isSelected;
}

// ─── PartyListItem ────────────────────────────────────────────────────────────

public class PartyListItem
{
    public Party  Party        { get; init; } = null!;
    public int    ReleaseCount { get; init; }

    // Délégation pour les bindings XAML
    public int     Id          => Party.Id;
    public string  Name        => Party.Name;
    public string? StartDate   => Party.StartDate;
    public string? Location    => Party.Location;
    public bool    IsOnline    => Party.IsOnline;
    public PartySeries? PartySeries => Party.PartySeries;
}
