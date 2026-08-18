using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.App.ViewModels.Emulators;
using DemoBase.App.ViewModels.Library;
using DemoBase.App.ViewModels.Releases;
using DemoBase.Core.DTOs;
using DemoBase.Core.Interfaces;
using DemoBase.Import;
using DemoBase.Media;
using Microsoft.Extensions.DependencyInjection;
using System.IO;

namespace DemoBase.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IServiceProvider   _services;

    // ── Singletons exposés pour MainWindow ───────────────────────────────────
    public DemoBase.App.ViewModels.Library.GroupListViewModel       GroupVm          => _services.GetRequiredService<DemoBase.App.ViewModels.Library.GroupListViewModel>();
    public DemoBase.App.ViewModels.Library.ScenerListViewModel      ScenerVm         => _services.GetRequiredService<DemoBase.App.ViewModels.Library.ScenerListViewModel>();
    public DemoBase.App.ViewModels.Library.PartyListViewModel       PartyVm          => _services.GetRequiredService<DemoBase.App.ViewModels.Library.PartyListViewModel>();
    public DemoBase.App.ViewModels.Releases.ReleaserDetailViewModel ReleaserDetailVm => _services.GetRequiredService<DemoBase.App.ViewModels.Releases.ReleaserDetailViewModel>();
    public DemoBase.App.ViewModels.Releases.PartyDetailViewModel    PartyDetailVm    => _services.GetRequiredService<DemoBase.App.ViewModels.Releases.PartyDetailViewModel>();
    // Colonne droite fixe — fiche Release toujours visible
    public DemoBase.App.ViewModels.MediaBrowserViewModel             MediaBrowserVm   => _services.GetRequiredService<DemoBase.App.ViewModels.MediaBrowserViewModel>();
    public DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel  ReleaseDetailVm  => _services.GetRequiredService<DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel>();
    public FavoriteSoundtracksViewModel                             FavSoundtracksVm => _services.GetRequiredService<FavoriteSoundtracksViewModel>();

    private readonly DemoBase.Data.DatImportService _datImport;
    private readonly DemozooVersionService _versionService;
    private readonly DemoBase.App.Services.RomScanService _romScan;
    private readonly string _dbDir;
    private ReleaseListViewModel? _activeListVm;

    [ObservableProperty] private ObservableObject? _currentViewModel;
    [ObservableProperty] private string _statusMessage    = "";
    [ObservableProperty] private string _dbVersionLabel   = string.Empty;

    // ── Notification mise à jour Demozoo ─────────────────────────────────────
    [ObservableProperty] private bool   _hasDemozooUpdate   = false;
    [ObservableProperty] private string _demozooUpdateLabel = string.Empty;

    private DemozooVersionInfo? _pendingVersionInfo;
    private string?             _pendingDbPath;

    public record SidebarItem(string Label, string Icon, IRelayCommand Command);
    [ObservableProperty] private IEnumerable<SidebarItem> _libraryItems    = [];
    [ObservableProperty] private IEnumerable<SidebarItem> _mediaItems      = [];
    [ObservableProperty] private IEnumerable<SidebarItem> _favoritesItems  = [];
    [ObservableProperty] private IEnumerable<SidebarItem> _managementItems = [];

    public MainViewModel(INavigationService navigation, IServiceProvider services,
                         DemoBase.Data.DatImportService datImport,
                         DemozooVersionService versionService,
                         DemoBase.App.Services.RomScanService romScan)
    {
        _navigation     = navigation;
        _services       = services;
        _datImport      = datImport;
        _versionService = versionService;
        _romScan        = romScan;
        _dbDir          = System.IO.Path.Combine(AppContext.BaseDirectory, "Database");
        _navigation.Navigated += OnNavigated;
        _ = LoadDbVersionAsync();
        RefreshNavLabels();
    }

    public void RefreshNavLabels()
    {
        LibraryItems = [
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Releases"),   "", NavigateToReleasesCommand),
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Groups"),      "", NavigateToGroupsCommand),
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Artists"),     "", NavigateToArtstsCommand),
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Platforms"),   "", NavigateToPlatformsCommand),
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Parties"),     "", NavigateToPartiesCommand),
        ];
        FavoritesItems = [
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Favorites"),   "♥", NavigateToFavoritesCommand),
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Soundtracks"), "", NavigateToFavSoundtracksCommand),
            new(DemoBase.App.Services.LocalizationService.Get("Nav_FavGraphics"), "🖼", NavigateToFavGraphicsCommand),
        ];
        MediaItems = [
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Media"), "🎬", NavigateToMediaBrowserCommand),
        ];
        ManagementItems = [
            new(DemoBase.App.Services.LocalizationService.Get("Nav_ScanROMs"),    "🔍", ScanRomsCommand),
            new(DemoBase.App.Services.LocalizationService.Get("Nav_OnThisDay"),   "🎲", PickReleaseOfTheDayCommand),
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Emulators"),   "", NavigateToEmulatorsCommand),
            new(DemoBase.App.Services.LocalizationService.Get("Nav_Preferences"), "", NavigateToPreferencesCommand),
        ];
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    private async void OnNavigated(object? sender, NavigationEventArgs args)
    {
        StatusMessage = args.ViewModelType.Name;

        // Arrêter les médias dès qu'on navigue ailleurs qu'une autre release
        if (args.ViewModelType != typeof(ReleaseDetailViewModel))
            ReleaseDetailVm.StopAllMedia();

        // Arrêter la lecture Favoris Soundtracks quand on navigue ailleurs
        if (args.ViewModelType != typeof(FavoriteSoundtracksViewModel))
            FavSoundtracksVm.StopPlayback();

        // ── Screenshots (fenêtre modale) ──────────────────────────────────────
        if (args.ViewModelType == typeof(ScreenshotDownloadViewModel))
        {
            var svc       = _services.GetRequiredService<ScreenshotDownloadService>();
            var imagesDir = Path.Combine(AppContext.BaseDirectory, "images");
            var vm        = new ScreenshotDownloadViewModel(svc, imagesDir);
            var win       = new ScreenshotDownloadWindow(vm)
            {
                Owner = System.Windows.Application.Current.MainWindow
            };
            win.Show();
            return;
        }

        // ── Filtre plateforme / type ──────────────────────────────────────────
        if (args.ViewModelType == typeof(ReleaseListViewModel)
            && args.Parameter is int filterId)
        {
            _activeListVm ??= _services.GetRequiredService<ReleaseListViewModel>();
            CurrentViewModel = _activeListVm;

            var savedOffset = _activeListVm.SavedScrollOffset;
            if (savedOffset > 0)
            {
                _activeListVm.SavedScrollOffset = savedOffset;
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                    _activeListVm.TriggerScrollRestore, System.Windows.Threading.DispatcherPriority.Loaded);
            }

            if (_activeListVm.AvailableTypes.Count == 0) await _activeListVm.LoadTypesAsync();
            if (_activeListVm.YearChips.Count == 0) await _activeListVm.LoadYearsAsync();
            if (args.Tag is string tag && tag == "favorites")
                await _activeListVm.ApplyFavoritesFilterAsync();
            else if (args.Tag is string tag2 && tag2.StartsWith("type:"))
                await _activeListVm.ApplyTypeFilterAsync(filterId, tag2["type:".Length..]);
            else
                await _activeListVm.ApplyPlatformFilterAsync(filterId, args.Tag as string ?? $"Plateforme {filterId}");
            return;
        }

        // ── Détail release → colonne droite fixe (ne change pas CurrentViewModel) ──
        if (args.ViewModelType == typeof(ReleaseDetailViewModel)
            && args.Parameter is int releaseId)
        {
            if (_activeListVm == null)
            {
                _activeListVm    = _services.GetRequiredService<ReleaseListViewModel>();
                CurrentViewModel = _activeListVm;
                await _activeListVm.LoadCommand.ExecuteAsync(null);
            }
            await ReleaseDetailVm.LoadAsync(releaseId);

            if (args.Tag is string tag && tag == "autoplay")
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] OnNavigated autoplay branch releaseId={releaseId} " +
                    $"thread={Environment.CurrentManagedThreadId}");
                _ = ReleaseDetailVm.LaunchCommand.ExecuteAsync(null);
                SubscribeToPlaylistEndedForMediaBrowser();
            }
            else
            {
                // Navigation normale — remettre les boutons ⏮/⏭ du player
                if (ReleaseDetailVm.SoundtrackPlayer?.Vm != null)
                    ReleaseDetailVm.SoundtrackPlayer.Vm.HideNavButtons = false;
            }

            return;
        }

        // ── Fiche Party ───────────────────────────────────────────────────────
        if (args.ViewModelType == typeof(PartyDetailViewModel))
        {
            var vm = _services.GetRequiredService<PartyDetailViewModel>();
            CurrentViewModel = vm;
            if (args.Parameter is int partyId)
                await vm.LoadAsync(partyId);
            return;
        }

        // ── Fiche Releaser ────────────────────────────────────────────────────
        if (args.ViewModelType == typeof(ReleaserDetailViewModel)
            && args.Parameter is int releaserId)
        {
            var vm = _services.GetRequiredService<ReleaserDetailViewModel>();
            CurrentViewModel = vm;
            await vm.LoadAsync(releaserId);
            return;
        }

        // ── Édition release ───────────────────────────────────────────────────
        if (args.ViewModelType == typeof(ReleaseEditViewModel))
        {
            var vm     = _services.GetRequiredService<ReleaseEditViewModel>();
            var editId = args.Parameter is int id ? id : (int?)null;
            CurrentViewModel = vm;
            await vm.LoadAsync(editId);
            return;
        }

        // ── Navigation générique (listes) ─────────────────────────────────────
        ObservableObject? listVm;
        try { listVm = _services.GetRequiredService(args.ViewModelType) as ObservableObject; }
        catch (InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"[Nav] Non enregistré : {args.ViewModelType.Name}");
            return;
        }
        if (listVm == null) return;

        CurrentViewModel = listVm;

        switch (listVm)
        {
            case ReleaseListViewModel rl:
                _activeListVm            = rl;
                rl.SearchQuery           = string.Empty;
                rl.SelectedPlatformId    = null;
                rl.SelectedPlatformName  = null;
                rl.SelectedReleaseTypeId = null;
                rl.SelectedTypeName      = null;
                rl.SelectedSupertype     = null;
                rl.SelectedYear          = null;
                foreach (var c in rl.YearChips) c.IsSelected = false;
                rl.IsFavoriteOnly        = false;
                if (rl.AvailableTypes.Count == 0) await rl.LoadTypesAsync();
                if (rl.YearChips.Count == 0) await rl.LoadYearsAsync();
                await rl.LoadCommand.ExecuteAsync(null);
                break;
            case GroupListViewModel gl:
            {
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Nav→GroupList savedOffset={gl.SavedScrollOffset} items={gl.Items?.Count}");
                var savedOffset = gl.SavedScrollOffset;
                await gl.LoadCommand.ExecuteAsync(null);
                if (savedOffset > 0)
                {
                    gl.SavedScrollOffset = savedOffset;
                    System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                        gl.TriggerScrollRestore, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                break;
            }
            case ScenerListViewModel sl:
            {
                var savedOffset = sl.SavedScrollOffset;
                await sl.LoadCommand.ExecuteAsync(null);
                if (savedOffset > 0)
                {
                    sl.SavedScrollOffset = savedOffset;
                    System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                        sl.TriggerScrollRestore, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                break;
            }
            case PlatformListViewModel pl: await pl.LoadCommand.ExecuteAsync(null); break;
            case PartyListViewModel pa:
            {
                var savedOffset = pa.SavedScrollOffset;
                await pa.InitAsync();
                if (savedOffset > 0)
                {
                    pa.SavedScrollOffset = savedOffset;
                    System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                        pa.TriggerScrollRestore, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                break;
            }
            case EmulatorSettingsViewModel em: await em.LoadCommand.ExecuteAsync(null); break;
            // 2026-07-30, retour utilisateur : "quand je rajoute une musique dans les
            // favoris, et que je vais voir dans les favoris sans quitter, je ne vois
            // pas la musique. il faut que je redemarre l'application pour la voir" —
            // FavoriteSoundtracksViewModel est enregistré en Singleton (App.xaml.cs,
            // pour garder la lecture en cours au fil des navigations), donc chaque
            // navigation ici récupère la MÊME instance déjà chargée une fois au
            // démarrage (constructeur → _ = LoadAsync()), jamais rechargée depuis —
            // contrairement aux autres VM de liste ci-dessus, qui ont toutes un
            // rechargement explicite dans ce switch. Un favori ajouté depuis une
            // autre vue (ReleaseDetail, MediaBrowser, Modland…) n'apparaissait donc
            // qu'après redémarrage complet de l'appli.
            case FavoriteSoundtracksViewModel fs:
                await fs.LoadAsync();
                await fs.LoadPlaylistsAsync();
                break;
        }
    }

    // ── Mise à jour Demozoo silencieuse ──────────────────────────────────────

    /// <summary>Appelé par App.xaml.cs au démarrage si une mise à jour est disponible.</summary>
    public void NotifyDemozooUpdate(DemozooVersionInfo info, string dbPath)
    {
        _pendingVersionInfo = info;
        _pendingDbPath      = dbPath;
        DemozooUpdateLabel  = info.LastModified.HasValue
            ? $"dump {info.LastModified.Value:yyyy-MM-dd}"
            : "nouvelle version";
        HasDemozooUpdate    = true;
    }

    [RelayCommand]
    private async Task DownloadDemozooUpdate()
    {
        if (_pendingVersionInfo == null || _pendingDbPath == null) return;
        await RunDemozooImportAsync(_pendingDbPath);
        _pendingVersionInfo = null;
        _pendingDbPath      = null;
    }

    /// <summary>
    /// Lance l'import Demozoo (fenêtre de progression visible, ImportProgressWindow) pour
    /// le dbPath donné — extrait de <see cref="DownloadDemozooUpdate"/> (2026-07-27, demande
    /// utilisateur) pour être réutilisable aussi bien depuis le clic sur le bouton sidebar
    /// (flux d'origine, ci-dessus) que depuis un déclenchement AUTOMATIQUE juste après une
    /// mise à jour du catalogue DATs (App.xaml.cs, DatsUpdateService) : de nouveaux DatEntry
    /// peuvent référencer des DemozooId absents d'une base Demozoo locale restée sur un
    /// ancien dump. Choix utilisateur explicite : "automatique mais visible" — même fenêtre,
    /// mêmes comportements (y compris l'arrêt de l'app en cas d'annulation, cf.
    /// App.RunInitialImportAsync), juste sans clic préalable.
    /// </summary>
    public async Task RunDemozooImportAsync(string dbPath)
    {
        HasDemozooUpdate = false;

        var mainWin = System.Windows.Application.Current.MainWindow;
        using var scope    = ((System.Windows.Application.Current as App)!._host!).Services.CreateScope();
        var progressWindow = scope.ServiceProvider.GetRequiredService<ImportProgressWindow>();
        progressWindow.DbPath = dbPath;
        if (mainWin != null) { progressWindow.Owner = mainWin; progressWindow.Topmost = true; }

        System.Windows.Application.Current.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        await App.RunInitialImportAsync(scope.ServiceProvider, progressWindow);
        System.Windows.Application.Current.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;

        await LoadDbVersionAsync();
    }

    // ── Version DB ───────────────────────────────────────────────────────────

    private async Task LoadDbVersionAsync()
    {
        try
        {
            var info = await _versionService.GetLocalVersionAsync();
            if (info?.LastModified != null)
                DbVersionLabel = $"dump {info.LastModified.Value:yyyy-MM-dd}";
            else if (info?.ImportedAt != null)
                DbVersionLabel = $"importé {info.ImportedAt.Value:yyyy-MM-dd}";
        }
        catch { /* pas critique */ }
    }

    // ── Commandes sidebar ─────────────────────────────────────────────────────

    [RelayCommand] private void NavigateToReleases()      => _navigation.NavigateTo<ReleaseListViewModel>();
    [RelayCommand] private void NavigateToFavSoundtracks()=> _navigation.NavigateTo<FavoriteSoundtracksViewModel>();
    [RelayCommand] private void NavigateToFavGraphics()   => _navigation.NavigateTo<FavoriteGraphicsViewModel>();
    [RelayCommand] private void NavigateToFavorites()     => _navigation.NavigateTo<ReleaseListViewModel>(parameter: 0, tag: "favorites");
    [RelayCommand] private void NavigateToGroups()        => _navigation.NavigateTo<GroupListViewModel>();
    [RelayCommand] private void NavigateToArtsts()        => _navigation.NavigateTo<ScenerListViewModel>();
    [RelayCommand] private void NavigateToPlatforms()     => _navigation.NavigateTo<PlatformListViewModel>();
    [RelayCommand] private void NavigateToParties()       => _navigation.NavigateTo<PartyListViewModel>();

    [RelayCommand]
    private async Task ExportDemozooRaw()
    {
        var mainWin = System.Windows.Application.Current.MainWindow;
        var result  = System.Windows.MessageBox.Show(
            "Ceci va telecharger et importer la base Demozoo (~500 Mo)." +
            " L operation peut prendre 30 a 60 minutes. Continuer ?",
            "Export Demozoo Raw DB",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return;
        var win = new DemoBase.App.DemozooRawExportWindow(_dbDir);
        if (mainWin != null) win.Owner = mainWin;
        win.Show();
        await win.RunAsync();
    }

    /// <summary>
    /// "Scan ROMs" (2026-07-27, demande utilisateur) — anciennement "Scan DATs" (le bouton
    /// est renommé, cf. demande explicite : "on pourrait utiliser le bouton 'Scan DATs' qu'on
    /// renommerait 'Scan ROMs'"). La mise à jour du catalogue DATs lui-même est désormais
    /// automatique au démarrage (DatsUpdateService) ; ce bouton sert maintenant à scanner un
    /// dossier choisi par l'utilisateur (fichiers isolés récupérés au fil du temps — ex.
    /// plusieurs .dsk Amstrad CPC) pour les faire correspondre au catalogue DATs et compléter
    /// automatiquement les releases correspondantes.
    /// </summary>
    private const string ScanRomsInfoDialogPrefKey = "scanroms.info_dialog.hidden";

    [RelayCommand]
    private async Task ScanRoms()
    {
        var mainWin = System.Windows.Application.Current.MainWindow;

        // Écran explicatif (2026-07-28, demande utilisateur : "peux tu mettre un écran
        // explicatif au design 'demobase' lorsque je clique sur 'Recherche de releases' ?"),
        // sauf si l'utilisateur a déjà coché "Ne plus afficher ce message" — même principe
        // que WinUAELauncher.MaybeShowQuitInfoAsync.
        var prefs = _services.GetRequiredService<DemoBase.Data.PreferencesService>();
        var hidden = await prefs.GetAsync(ScanRomsInfoDialogPrefKey);
        if (hidden != "true")
        {
            var info = new DemoBase.App.Views.RomScanInfoDialog();
            if (mainWin != null) info.Owner = mainWin;
            var proceed = info.ShowDialog();

            if (info.DontShowAgain)
                await prefs.SetAsync(ScanRomsInfoDialogPrefKey, "true");

            if (proceed != true) return; // "Annuler" ou fenêtre fermée sans "Compris"
        }

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = DemoBase.App.Services.LocalizationService.Get("ScanRoms_PickFolderTitle"),
            InitialDirectory = AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog() != true) return;

        var win = new DemoBase.App.RomScanWindow(_romScan, dlg.FolderName);
        if (mainWin != null) win.Owner = mainWin;
        win.Show();
        await win.RunScanAsync();
    }

    // ── "On this day" / pioche aléatoire (2026-07-29, demande utilisateur) ─────
    // Cherche dans tout le catalogue une release sortie ce jour (mois+jour), une
    // année passée quelconque ; sinon pioche une release aléatoire dans tout le
    // catalogue (pas seulement les releases complètes). Résultat annoncé via
    // StatusScrollerControl puis navigation vers la fiche détail (même mécanisme
    // que le clic sur une release dans une liste : NavigateTo<ReleaseDetailViewModel>).
    [RelayCommand]
    private async Task PickReleaseOfTheDay()
    {
        var releaseService = _services.GetRequiredService<IReleaseService>();
        var today = DateTime.Now;
        var (release, isExactMatch) = await releaseService.GetOnThisDayOrRandomReleaseAsync(today.Month, today.Day);

        if (release == null)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                DemoBase.App.Services.LocalizationService.Get("OnThisDay_Empty"), isWarning: true);
            return;
        }

        var year = release.ReleaseDate.Length >= 4 ? release.ReleaseDate[..4] : "?";
        var messageKey = isExactMatch ? "OnThisDay_Found" : "OnThisDay_Random";
        DemoBase.App.Controls.StatusScrollerControl.Post(
            string.Format(DemoBase.App.Services.LocalizationService.Get(messageKey), release.Title, year));

        _navigation.NavigateTo<ReleaseDetailViewModel>(release.Id);
    }

    [RelayCommand] private void NavigateToEmulators()   => _navigation.NavigateTo<EmulatorSettingsViewModel>();
    [RelayCommand] private void NavigateToPreferences() => _navigation.NavigateTo<PreferencesViewModel>();
    // ── MediaBrowser autoplay next ───────────────────────────────────────────
    private EventHandler? _playlistEndedHandler;
    private EventHandler? _playlistPrevHandler;

    // Garde anti-cascade pour l'auto-avance séquentielle (non-shuffle) — même
    // principe que _isShuffleTransitioning/_lastShufflePlay dans
    // MusicBrowserViewModel.PlayShuffleNextAsync (ajoutés pour ce même symptôme
    // en mode shuffle : "PlaybackStartFailed en boucle sur pistes sans DAT"),
    // mais qui n'avait pas d'équivalent ici pour le chemin séquentiel.
    //
    // Un premier correctif (compteur avec fenêtre de 3s, plafond 15) s'est
    // révélé INSUFFISANT en usage réel : le crash s'est reproduit après
    // seulement ~4-5 pistes sautées, bien avant que le plafond ne soit atteint.
    // Le vrai problème n'est donc pas "trop d'itérations" mais la PROFONDEUR de
    // pile PAR itération : PlaybackStartFailed est levé de façon synchrone
    // (simple appel de delegate C#, pas via le Dispatcher) depuis
    // PlayMusicReleaseAsync ; le gestionnaire ci-dessous appelait PlayNext()
    // directement, qui ré-déclenche synchroniquement toute la chaîne
    // (PlayReleaseCommand → NavigateTo<ReleaseDetailViewModel> → réutilise le
    // SINGLETON ReleaseDetailViewModel → OnDetailChanged → autoplay →
    // PlayMusicReleaseAsync → nouvel échec → PlaybackStartFailed → ...) SANS
    // jamais rendre la main à la pompe de messages WPF entre deux pistes — les
    // frames C# de chaque cycle (navigation, setters de propriétés générées par
    // CommunityToolkit, résolution DI, etc.) s'empilent donc les unes sur les
    // autres au lieu de se dérouler indépendamment, d'où un StackOverflowException
    // après une poignée de cycles seulement (la pile ne repart jamais de zéro).
    //
    // CORRECTIF STRUCTUREL : on ne rappelle plus PlayNext() en synchrone ici —
    // on le POSTE sur le Dispatcher (BeginInvoke, priorité Background). Ça
    // force chaque cycle à repartir d'une pile neuve à la prochaine itération
    // de la boucle de messages WPF, quel que soit le nombre de pistes
    // consécutives sans fichier jouable : la profondeur de pile ne peut plus
    // jamais s'accumuler d'un cycle à l'autre. Le compteur ci-dessous n'est
    // plus une protection anti-crash (elle est maintenant garantie
    // structurellement par le BeginInvoke) mais un simple filet UX pour éviter
    // de scanner indéfiniment une longue série de pistes "?" sans fichier.
    private int _consecutivePlaybackFailures;
    private DateTime _lastPlaybackFailureAt = DateTime.MinValue;
    private const int MaxConsecutivePlaybackFailures = 10;

    // [DIAG] Compteur d'abonnements actifs à PlaybackStartFailed — sert
    // uniquement à vérifier dans les logs que le correctif ci-dessous
    // (désabonnement avant réabonnement dans SubscribeToPlaylistEndedForMediaBrowser)
    // fonctionne bien et que ce nombre reste à 1, jamais plus.
    private int _playbackStartFailedSubscriberCount;

    private void OnPlaybackStartFailed()
    {
        // En MediaBrowser : toujours passer au suivant, même si le téléchargement
        // a échoué — l'erreur est visible dans l'onglet Fichiers de la release.
        var now = DateTime.UtcNow;
        if ((now - _lastPlaybackFailureAt).TotalSeconds > 10)
            _consecutivePlaybackFailures = 0;
        _lastPlaybackFailureAt = now;
        _consecutivePlaybackFailures++;

        System.Diagnostics.Debug.WriteLine(
            $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] OnPlaybackStartFailed ENTER " +
            $"thread={Environment.CurrentManagedThreadId} consecutiveFailures={_consecutivePlaybackFailures} " +
            $"activeSubscribers={_playbackStartFailedSubscriberCount}");

        if (_consecutivePlaybackFailures > MaxConsecutivePlaybackFailures)
        {
            _consecutivePlaybackFailures = 0;
            System.Diagnostics.Debug.WriteLine(
                $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] OnPlaybackStartFailed STOP — plafond {MaxConsecutivePlaybackFailures} atteint");
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Trop d'échecs de lecture consécutifs — lecture automatique arrêtée.", isError: true);
            return;
        }

        // Voir commentaire ci-dessus : BeginInvoke (au lieu d'un appel direct)
        // rompt la chaîne d'appels synchrone — chaque tentative repart d'une
        // pile fraîche au lieu de s'empiler sur la précédente.
        System.Diagnostics.Debug.WriteLine(
            $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] OnPlaybackStartFailed → BeginInvoke(PlayNext) QUEUED");
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] BeginInvoke callback RUNNING thread={Environment.CurrentManagedThreadId}");
            if (CurrentViewModel is MediaBrowserViewModel mbVm)
                _ = mbVm.Music.PlayNext();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SubscribeToPlaylistEndedForMediaBrowser()
    {
        System.Diagnostics.Debug.WriteLine(
            $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] SubscribeToPlaylistEndedForMediaBrowser ENTER " +
            $"thread={Environment.CurrentManagedThreadId} activeSubscribersAvant={_playbackStartFailedSubscriberCount}");

        // Désabonner les anciens handlers
        if (_playlistEndedHandler != null && ReleaseDetailVm.SoundtrackPlayer?.Vm != null)
        {
            ReleaseDetailVm.SoundtrackPlayer.Vm.PlaylistEnded -= _playlistEndedHandler;
            ReleaseDetailVm.SoundtrackPlayer.Vm.NextRequestedBeyondPlaylist -= _playlistEndedHandler;
        }

        _playlistEndedHandler = (s, e) =>
        {
            if (CurrentViewModel is MediaBrowserViewModel mbVm)
                _ = mbVm.Music.PlayNext();
        };

        // Réutilise le même pattern pour Previous
        if (_playlistPrevHandler != null && ReleaseDetailVm.SoundtrackPlayer?.Vm != null)
            ReleaseDetailVm.SoundtrackPlayer.Vm.PreviousRequestedBeyondPlaylist -= _playlistPrevHandler;
        _playlistPrevHandler = (s, e) =>
        {
            if (CurrentViewModel is MediaBrowserViewModel mbVm)
                mbVm.Music.PlayPrevious();
        };

        // Écouter PropertyChanged sur ReleaseDetailVm pour détecter quand
        // SoundtrackPlayer est assigné (après LaunchCommand) et s'abonner alors.
        void OnReleaseDetailPropertyChanged(object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "SoundtrackPlayer") return;
            var vm = ReleaseDetailVm.SoundtrackPlayer?.Vm;
            if (vm == null) return;
            // Désabonner d'abord pour éviter les doublons
            vm.PlaylistEnded -= _playlistEndedHandler;
            vm.NextRequestedBeyondPlaylist -= _playlistEndedHandler;
            vm.PreviousRequestedBeyondPlaylist -= _playlistPrevHandler;
            vm.PlaylistEnded += _playlistEndedHandler;
            vm.NextRequestedBeyondPlaylist += _playlistEndedHandler;
            vm.PreviousRequestedBeyondPlaylist += _playlistPrevHandler;
            vm.HideNavButtons = true; // masquer ⏮/⏭ en mode MediaBrowser
            // Se désabonner de PropertyChanged une fois le player trouvé
            ReleaseDetailVm.PropertyChanged -= OnReleaseDetailPropertyChanged;
        }

        // Si SoundtrackPlayer est déjà là (re-lecture), s'abonner directement
        if (ReleaseDetailVm.SoundtrackPlayer?.Vm != null)
        {
            ReleaseDetailVm.SoundtrackPlayer.Vm.PlaylistEnded += _playlistEndedHandler;
            ReleaseDetailVm.SoundtrackPlayer.Vm.NextRequestedBeyondPlaylist += _playlistEndedHandler;
            ReleaseDetailVm.SoundtrackPlayer.Vm.PreviousRequestedBeyondPlaylist += _playlistPrevHandler;
            ReleaseDetailVm.SoundtrackPlayer.Vm.HideNavButtons = true;
        }
        else
        {
            ReleaseDetailVm.PropertyChanged += OnReleaseDetailPropertyChanged;
        }

        // [BUG TROUVÉ] Ce point était atteint à CHAQUE appel de cette méthode,
        // c.-à-d. à CHAQUE piste enchaînée en autoplay (voir PlayRelease() dans
        // MediaBrowserViewModel.cs : chaque avance navigue avec tag "autoplay",
        // qui rappelle SubscribeToPlaylistEndedForMediaBrowser() depuis
        // OnNavigated). L'ancien code faisait "+=" ici SANS jamais faire "-="
        // au préalable — contrairement aux 3 autres event handlers ci-dessus
        // qui sont bien désabonnés avant réabonnement. Résultat : à la piste N,
        // OnPlaybackStartFailed était abonné N fois à PlaybackStartFailed. Un
        // seul échec de lecture déclenchait donc N exécutions synchrones de
        // OnPlaybackStartFailed, chacune postant son propre BeginInvoke(PlayNext).
        // C'est ce qui explique que le comportement ait "changé" après le
        // correctif Dispatcher.BeginInvoke (le crash par pile trop profonde a
        // bien disparu) tout en restant cassé (des dizaines de PlayNext()
        // concurrents finissaient par saturer/planter autrement). Correctif :
        // désabonner avant de réabonner, comme pour les autres handlers.
        ReleaseDetailVm.PlaybackStartFailed -= OnPlaybackStartFailed;
        ReleaseDetailVm.PlaybackStartFailed += OnPlaybackStartFailed;
        _playbackStartFailedSubscriberCount = 1;
        System.Diagnostics.Debug.WriteLine(
            $"[CASCADE {DateTime.Now:HH:mm:ss.fff}] SubscribeToPlaylistEndedForMediaBrowser EXIT " +
            $"activeSubscribersApres={_playbackStartFailedSubscriberCount}");
    }

    [RelayCommand] private void NavigateToMediaBrowser()
    {
        System.Diagnostics.Debug.WriteLine("[MainVM] NavigateToMediaBrowser called");
        var vm = _services.GetRequiredService<MediaBrowserViewModel>();
        vm.Music.SetStopAction(() => ReleaseDetailVm.SoundtrackPlayer?.Vm.Stop());
        CurrentViewModel = vm;
        _ = vm.LoadAsync();
    }
}

// ── Placeholder VMs ───────────────────────────────────────────────────────────
public partial class ImportViewModel       : ObservableObject { }
public partial class MediaLibraryViewModel : ObservableObject { }
