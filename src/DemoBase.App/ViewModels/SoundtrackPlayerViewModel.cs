using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
// Alias plutôt que "using NAudio.Wave;" : ce namespace expose aussi
// NAudio.Wave.PlaybackState, en conflit avec TrackerPlayer.Core.Models.PlaybackState
// (déjà utilisé partout dans ce fichier, ex. OnStateChanged) — un using générique
// rendrait "PlaybackState" ambigu (CS0104) dès qu'on en a besoin pour la sortie
// audio partagée (WaveOutEvent).
using WaveOutEvent = NAudio.Wave.WaveOutEvent;
using TrackerPlayer.Core.Interfaces;
using TrackerPlayer.Core.Models;
using TrackerPlayer.Core.Players;
using TrackerPlayer.UI.Controls;
using System.IO;

namespace DemoBase.App.ViewModels;

public partial class SoundtrackPlayerViewModel : ObservableObject, IDisposable
{
    private readonly ITrackerService _service;
    private ITrackerPlayer?          _player;

    // Incrémenté à chaque CleanupPlayer() — permet à PreloadNextAsync de
    // détecter, à son retour, qu'un nettoyage a eu lieu pendant son exécution
    // (changement de release / Dispose) et de disposer immédiatement le
    // player qu'elle vient de créer plutôt que de le laisser orphelin.
    private int _playerGeneration;
    public TrackerPlayer.Core.Players.SampleRingBuffer? SampleBuffer
        => (_player as TrackerPlayer.Core.Players.NativeTrackerPlayer)?.SampleBuffer
        ?? (_player as TrackerPlayer.Core.Players.NativeAudioPlayer)?.SampleBuffer
        ?? (_player as TrackerPlayer.Core.Players.ZXTunePlayer)?.SampleBuffer
        ?? (_player as TrackerPlayer.Core.Players.UadePlayer)?.SampleBuffer
        ?? (_player as TrackerPlayer.Core.Players.SndhPlayer)?.SampleBuffer;

    // 2026-08-07, demande utilisateur : vue d'ensemble complète de la forme d'onde
    // sous l'oscilloscope. Même schéma de coalescence que SampleBuffer ci-dessus —
    // volontairement PAS de cas pour ExeMusicPlayer, confirmé explicitement par
    // l'utilisateur ("pas necessaire pour les musiques executables, mais ok pour
    // le reste") : WaveformOverview reste donc null pour ce format, et
    // WaveformOverviewView (contrôle XAML) doit gérer ce null en n'affichant rien.
    public TrackerPlayer.Core.Players.WaveformOverviewBuffer? WaveformOverview
        => (_player as TrackerPlayer.Core.Players.NativeTrackerPlayer)?.WaveformOverview
        ?? (_player as TrackerPlayer.Core.Players.NativeAudioPlayer)?.WaveformOverview
        ?? (_player as TrackerPlayer.Core.Players.ZXTunePlayer)?.WaveformOverview
        ?? (_player as TrackerPlayer.Core.Players.UadePlayer)?.WaveformOverview
        ?? (_player as TrackerPlayer.Core.Players.SndhPlayer)?.WaveformOverview;

    // 2026-08-01, retour utilisateur ("quand un format n'est pas jouable, peux tu, au
    // lieu d'afficher l'oscilloscope 'vide' mettre un message 'Format non jouable'") :
    // true quand le backend actif a explicitement établi qu'aucun son n'a été produit
    // pour ce fichier (IsPlayable=false), même quand le "chargement" n'a levé aucune
    // exception (métadonnées minimales). Couvre libopenmpt (NativeTrackerPlayer,
    // rendu audio de la quasi-totalité des formats trackers de l'appli) ET UADE
    // (UadePlayer — retour utilisateur du 2026-08-01 : "j'ai testé pour les fichiers
    // non jouables mais l'oscilloscope vide s'affiche encore [...] malgré le 'unknown
    // format de uade'").
    // 2026-08-06, retour utilisateur ("j'ai l'impression que zxtune n'est jamais
    // testé pour les formats inconnus mais uniquement uade") : confirmé — ZXTune
    // (ZXTunePlayer) n'avait jusqu'ici AUCUN signal équivalent (le commentaire
    // ci-dessus l'excluait explicitement, "pas de signal équivalent fiable
    // disponible aujourd'hui" — vrai avant le passage au pont natif zxtune.dll du
    // 2026-08-06, plus aujourd'hui). ZXTunePlayer.IsPlayable ajouté (basé sur le
    // nombre de trames réellement rendues, cf. son commentaire) et inclus ici. Pour
    // SNDH/audio natif, toujours false ici — pas de signal équivalent fiable
    // disponible aujourd'hui, mieux vaut ne rien afficher qu'un faux positif.
    public bool IsFormatUnsupported
        => (_player is TrackerPlayer.Core.Players.NativeTrackerPlayer ntp && !ntp.IsPlayable)
        || (_player is TrackerPlayer.Core.Players.UadePlayer up && !up.IsPlayable)
        || (_player is TrackerPlayer.Core.Players.ZXTunePlayer zxp && !zxp.IsPlayable);

    // 2026-08-06, retour utilisateur ("on avait mis une case à coché et un slider pour la
    // separation stereo pour uade") : le réglage de panoramique (UC_PANNING_VALUE) n'a de
    // sens que pour le backend UADE (Amiga hard-pan) — sert à masquer la case+curseur dans
    // la vue quand le fichier chargé n'utilise pas UadePlayer.
    public bool IsUadeFormat => _player is TrackerPlayer.Core.Players.UadePlayer;

    // 2026-08-06, retour utilisateur ("peux tu rajouter une info pour que je sache
    // quel lecteur joue le morceaux ?") : plusieurs formats sont couverts par
    // plusieurs backends (ex. "Amiga exotiques" par UADE ET ZXTune, avec un repli de
    // l'un vers l'autre en cas d'échec réel — cf. TrackerService.OpenAsync), donc le
    // nom de format seul (FormatDisplay) ne suffit plus à savoir quel MOTEUR a
    // effectivement fini par jouer le fichier. Recalculé au même rythme
    // qu'IsFormatUnsupported/IsUadeFormat (mêmes points de OnPropertyChanged) — un
    // repli entre backends ne change PAS le type de `_player` en cours de lecture
    // (le choix est déjà figé avant que Play() ne soit appelé), donc pas besoin de
    // ré-évaluer plus souvent que ces deux propriétés existantes.
    public string EngineDisplay => _player switch
    {
        TrackerPlayer.Core.Players.NativeTrackerPlayer => "libopenmpt",
        TrackerPlayer.Core.Players.UadePlayer          => "UADE",
        TrackerPlayer.Core.Players.ZXTunePlayer        => "ZXTune",
        TrackerPlayer.Core.Players.SndhPlayer          => "SNDH",
        TrackerPlayer.Core.Players.NativeAudioPlayer   => "Audio",
        TrackerPlayer.Core.Players.ExeMusicPlayer      => "EXE",
        null                                            => "",
        _                                               => _player.GetType().Name,
    };

    // ── Module courant ────────────────────────────────────────────────────────

    [ObservableProperty] private TrackerModule? _module;
    [ObservableProperty] private string         _title        = DemoBase.App.Services.LocalizationService.Get("Msg_NoFileLoaded");
    [ObservableProperty] private string         _currentFileName = "";
    public bool HasCurrentFileName => !string.IsNullOrEmpty(CurrentFileName);

    partial void OnCurrentFileNameChanged(string value)
        => OnPropertyChanged(nameof(HasCurrentFileName));
    [ObservableProperty] private string         _formatDisplay = "—";

    // 2026-07-31, retour utilisateur ("rajoute à cote du nom de la musique, entre
    // parenthese, le type de module joué. possible aussi sur la vue oscilloscope ?") :
    // le header (avec Title) est commun à la vue pattern ET à la vue oscilloscope (Row 0,
    // toujours visible, indépendamment de ce qui est affiché en Row 1) — un seul binding
    // ici suffit donc pour couvrir les deux, pas besoin de dupliquer sur OscilloscopeView.
    public string TitleDisplay =>
        (string.IsNullOrWhiteSpace(FormatDisplay) || FormatDisplay == "—")
            ? Title
            : $"{Title} ({FormatDisplay})";

    partial void OnTitleChanged(string value)
        => OnPropertyChanged(nameof(TitleDisplay));

    partial void OnFormatDisplayChanged(string value)
        => OnPropertyChanged(nameof(TitleDisplay));
    [ObservableProperty] private bool           _isLoaded;
    [ObservableProperty] private bool           _isPlaying;
    [ObservableProperty] private bool           _isPaused;
    [ObservableProperty] private bool           _isLoading;
    [ObservableProperty] private string?        _errorMessage;

    // ── Subsongs (UADE) ──────────────────────────────────────────────────────
    // 2026-07-30, retour utilisateur : certains modules Amiga (TFMX, etc.) ont
    // plusieurs subsongs, jusqu'ici enchaînés automatiquement (UadePlayer) sans
    // moyen de naviguer ni d'afficher l'info. SubsongCount/CurrentSubsongIndex
    // reflètent ITrackerPlayer et sont rafraîchis à chaque tick de OnStateChanged
    // — ce qui couvre aussi bien la sélection manuelle que l'avance automatique.
    [ObservableProperty] private int _subsongCount        = 1;
    [ObservableProperty] private int _currentSubsongIndex;

    public bool   HasMultipleSubsongs => SubsongCount > 1;
    public string SubsongDisplay      => $"Subsong {CurrentSubsongIndex + 1}/{SubsongCount}";

    partial void OnSubsongCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasMultipleSubsongs));
        OnPropertyChanged(nameof(SubsongDisplay));
    }
    partial void OnCurrentSubsongIndexChanged(int value)
        => OnPropertyChanged(nameof(SubsongDisplay));

    // ── Pattern / Oscilloscope ────────────────────────────────────────────────

    [ObservableProperty] private PatternViewModel? _currentPatternVm;
    [ObservableProperty] private int               _highlightedRow;
    [ObservableProperty] private int               _currentPatternIndex;
    // 2026-07-31, retour utilisateur ("est-ce que tu peux recuperer d'autres infos via
    // libopenmpt ? [...] la pattern en cours de lecture, le nombre de patterns joués et la
    // liste des patterns") : CurrentPatternIndex existait déjà (déduit de
    // PlaybackState.CurrentPattern), mais rien ne suivait l'ordre courant (position dans
    // l'order list / séquence) — nécessaire pour distinguer "pattern N" de "position M dans
    // la séquence" quand un même pattern est rejoué à plusieurs endroits (cas fréquent).
    [ObservableProperty] private int               _currentOrderIndex;
    [ObservableProperty] private int               _currentBpm   = 125;
    [ObservableProperty] private int               _currentSpeed = 6;
    [ObservableProperty] private TrackerStyle      _trackerStyle = TrackerStyle.ProTracker;

    // VU-mètres par canal (PatternView, style ProTracker uniquement) — niveau
    // 0.0-1.0 par voie, dérivé de PlaybackState.ChannelVolumes (échelle 0-64
    // côté NativeTrackerPlayer/OpenmptStream). Propriété manquante depuis
    // l'introduction de ChannelVolumes : le binding XAML ChannelLevels existait
    // déjà mais ne trouvait aucune propriété correspondante ici, d'où les
    // vumètres invisibles sans erreur visible à part le warning de binding.
    [ObservableProperty] private float[]           _channelLevels = Array.Empty<float>();

    // ── Position ──────────────────────────────────────────────────────────────

    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private float  _masterVolume    = 0.8f;

    /// <summary>Si true, masque les boutons ⏮/⏭ du player (utilisé en mode MediaBrowser
    /// pour éviter la confusion avec les boutons de navigation de la liste).</summary>
    [ObservableProperty] private bool _hideNavButtons;

    /// <summary>Déclenché quand la playlist est épuisée — permet au MediaBrowser
    /// de passer à la release suivante automatiquement.</summary>
    public event EventHandler? PlaylistEnded;

    /// <summary>Déclenché quand Next est pressé alors qu'on est déjà à la dernière piste
    /// de la playlist — permet au MediaBrowser de passer à la release suivante.</summary>
    public event EventHandler? NextRequestedBeyondPlaylist;

    public string PositionDisplay => TimeSpan.FromSeconds(PositionSeconds).ToString(@"m\:ss");
    public string DurationDisplay => TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss");
    public bool   HasPatterns     => Module?.Patterns.Count > 0;

    // 2026-07-31, retour utilisateur (généralisation des infos libopenmpt exposées) :
    // affichage compact "pattern courant / total" et "position dans la séquence / total"
    // — pensé pour tenir sur une seule ligne dans le header du player sans agrandir la
    // zone réservée au pattern viewer (contrainte explicite de la demande utilisateur).
    public string PatternPositionDisplay =>
        (Module != null && Module.Patterns.Count > 0)
            ? $"Pat {CurrentPatternIndex + 1}/{Module.Patterns.Count}"
            : string.Empty;

    public string OrderPositionDisplay =>
        (Module != null && Module.OrderList.Count > 0)
            ? $"Ord {CurrentOrderIndex + 1}/{Module.OrderList.Count}"
            : string.Empty;

    partial void OnCurrentPatternIndexChanged(int value)
        => OnPropertyChanged(nameof(PatternPositionDisplay));

    partial void OnCurrentOrderIndexChanged(int value)
        => OnPropertyChanged(nameof(OrderPositionDisplay));

    partial void OnModuleChanged(TrackerModule? value)
    {
        OnPropertyChanged(nameof(PatternPositionDisplay));
        OnPropertyChanged(nameof(OrderPositionDisplay));
    }

    private          Dictionary<int, PatternViewModel> _patternCache = new();

    public SoundtrackPlayerViewModel(ITrackerService service)
    {
        _service = service;
        // 2026-08-07, retour utilisateur ("peux tu faire de meme pour le
        // 'Panoramique' [...] et enleve le du player. Crée du coup une section 'UADE'
        // dans les préférences pour mettre le panoramique et le replay gain.") :
        // panoramique (UC_PANNING_VALUE) ET gain (UC_GAIN) vivent maintenant tous les
        // deux exclusivement sur l'écran Préférences (PreferencesViewModel, section
        // UADE) — plus aucune case à cocher/slider ici, plus aucun [ObservableProperty]
        // dédié dans CE ViewModel. Il reste néanmoins nécessaire de pousser les valeurs
        // persistées (caches statiques PreferencesService, mis à jour par
        // PreferencesService.LoadAllAsync au démarrage de l'appli) vers les statiques
        // TrackerPlayer.Core.Players.UadePlayer correspondantes DÈS la construction
        // d'un écran de lecture : PreferencesViewModel.SaveAsync ne les pousse QUE
        // lorsque l'utilisateur clique "Sauvegarder" sur la page Préférences — sans
        // cette ligne, une valeur restaurée d'une session précédente resterait au
        // défaut du code tant que cette page n'a pas été rouverte et sauvegardée au
        // moins une fois pendant la session en cours. _player est encore null ici (rien
        // à réappliquer côté lecture en cours) — de toute façon, ni UC_PANNING_VALUE ni
        // UC_GAIN n'ont d'effet "à chaud" sur un state déjà créé, seulement à la
        // PROCHAINE ouverture d'un fichier UADE.
        TrackerPlayer.Core.Players.UadePlayer.PanningEnabled = DemoBase.Data.PreferencesService.LastUadePanningEnabled;
        TrackerPlayer.Core.Players.UadePlayer.PanningAmount  = DemoBase.Data.PreferencesService.LastUadePanningAmount;
        TrackerPlayer.Core.Players.UadePlayer.GainAmount     = DemoBase.Data.PreferencesService.LastUadeGainAmount;
    }

    // ── Ouverture d'un fichier ────────────────────────────────────────────────

    public async Task OpenAsync(string filePath, CancellationToken ct = default)
    {
        // Bug du 2026-07-24 : après un Stop() manuel (qui met _stopRequested=true et ne le
        // remet JAMAIS à false lui-même — seul OnPlaybackFinished le fait, en réaction à SA
        // PROPRE exécution), relancer une playlist plus tard laissait le flag bloqué à true.
        // Résultat : dès que la 1ère piste de la NOUVELLE playlist se terminait naturellement,
        // OnPlaybackFinished le traitait comme "Stop volontaire" (le tout premier check dans
        // son lambda dispatché) et s'arrêtait sans avancer — d'où "la playlist s'arrête après
        // la fin du 1er morceau" à chaque fois qu'un Stop précédait le lancement. OpenAsync est
        // le point d'entrée systématique de toute nouvelle session de lecture (direct ou via
        // LoadFilesAsync).
        _stopRequested     = false;
        _diagLoggedNearEnd = false;
        System.Diagnostics.Debug.WriteLine($"[PLAYER] OpenAsync: {filePath}");
        System.Diagnostics.Debug.WriteLine($"[PLAYER] Exists={File.Exists(filePath)} Ext={Path.GetExtension(filePath)}");
        if (!File.Exists(filePath))
        {
            ErrorMessage = $"Fichier introuvable : {filePath}";
            DemoBase.App.Controls.StatusScrollerControl.Post($"Fichier introuvable : {Path.GetFileName(filePath)}", isError: true);
            return;
        }
        CleanupPlayer();
        ErrorMessage = null;
        IsLoading    = true;

        // Laisse WPF rendre l'indicateur "Conversion en cours…" avant de lancer
        // l'ouverture, qui peut être longue pour certains formats (SNDH/ICE! via
        // ZXTune, conversion WAV complète avant lecture).
        await Task.Yield();

        try
        {
            // Task.Run : _service.OpenAsync() décode le module ET calcule sa durée
            // (openmpt_module_get_duration_seconds), qui simule en interne toute la
            // lecture du module la première fois — potentiellement plusieurs
            // centaines de ms pour un module long. Sans ce Task.Run, l'appel est
            // effectué directement dans la continuation du thread UI (capturé par
            // le SynchronizationContext WPF au moment de cet await), ce qui gèle
            // l'interface (PatternView, boutons...) pendant tout le calcul — c'est
            // exactement ce qu'on cherche à éviter avec l'indicateur "Conversion en
            // cours…" affiché juste avant (cf. Task.Yield() ci-dessus). Voir le
            // même correctif, plus critique encore, dans PreloadNextAsync ci-dessous.
            var (module, player) = await Task.Run(() => _service.OpenAsync(filePath, ct));

            _player = player;
            _player.StateChanged     += OnStateChanged;
            // En mode sortie partagée (playlist gapless, cf. EnsureSharedOutput), le
            // swap de piste ne passe plus par PlaybackFinished du player (le device
            // partagé ne "s'arrête" jamais entre deux pistes) — c'est
            // OnSharedSourceEnded qui prend le relais. S'abonner quand même ici ne
            // gêne pas (l'event ne sera simplement jamais levé dans ce mode).
            _player.PlaybackFinished += OnPlaybackFinished;
            _player.MasterVolume      = MasterVolume;
            // Valeurs initiales pour ce nouveau player (subsongs déjà connus dès
            // LoadAsync côté UadePlayer, pas besoin d'attendre le 1er tick de
            // OnStateChanged) — évite d'afficher brièvement les valeurs du fichier
            // précédent (ex. "Subsong 3/4") avant la 1re mise à jour de state.
            SubsongCount        = _player.SubsongCount;
            CurrentSubsongIndex = _player.CurrentSubsongIndex;
            if (_player is NativeTrackerPlayer ntp)
                EnsureSharedOutput(ntp);
            OnPropertyChanged(nameof(SampleBuffer));
        OnPropertyChanged(nameof(WaveformOverview));
            OnPropertyChanged(nameof(IsFormatUnsupported));
            OnPropertyChanged(nameof(IsUadeFormat));
            OnPropertyChanged(nameof(EngineDisplay));

            Module        = module;
            Title         = !string.IsNullOrWhiteSpace(module.Title)
                ? module.Title
                : Path.GetFileName(filePath);
            CurrentFileName = Path.GetFileName(filePath);
            FormatDisplay = !string.IsNullOrWhiteSpace(module.FormatName)
                ? module.FormatName
                : module.Format.ToString();
            DurationSeconds = module.DurationSeconds > 0 ? module.DurationSeconds : 1;
            OnPropertyChanged(nameof(DurationDisplay));
            TrackerStyle    = module.Format switch
            {
                TrackerFormat.XM  => TrackerStyle.FastTracker2,
                // 2026-07-30, retour utilisateur (piste Modland DigiBooster Pro/.dbm,
                // jouable + pattern view fonctionnel — "bonne surprise") : "la vue FT2
                // serait plus judicieuse que la vue protracker" pour ce format.
                TrackerFormat.DBM => TrackerStyle.FastTracker2,
                // 2026-07-30, retour utilisateur : "les fichiers .ult (ultracker)
                // affichent les pattern mais il faut la vue FT2".
                TrackerFormat.ULT => TrackerStyle.FastTracker2,
                // 2026-07-30, retour utilisateur : "l'astroidea (.xmf) ... à ouvrir
                // avec libopenmpt et les patterns FT2" — remplace le choix
                // ProTracker initial (posé "pour voir si ça convenait").
                TrackerFormat.XMF => TrackerStyle.FastTracker2,
                // 2026-07-30, retour utilisateur : ".amf/.667/.669/.digi - à ouvrir
                // avec libopenmpt et les patterns FT2".
                TrackerFormat.AMF         => TrackerStyle.FastTracker2,
                TrackerFormat.Composer669 => TrackerStyle.FastTracker2,
                TrackerFormat.DIGI        => TrackerStyle.FastTracker2,
                // 2026-07-30, retour utilisateur : ".dsm/.dtm/.mdl - FT2".
                TrackerFormat.DSM => TrackerStyle.FastTracker2,
                TrackerFormat.DTM => TrackerStyle.FastTracker2,
                TrackerFormat.MDL => TrackerStyle.FastTracker2,
                // 2026-07-30, retour utilisateur : ".dmf/.ams - FT2".
                TrackerFormat.DMF => TrackerStyle.FastTracker2,
                TrackerFormat.AMS => TrackerStyle.FastTracker2,
                // 2026-07-30, retour utilisateur : ".psm - FT2".
                TrackerFormat.PSM => TrackerStyle.FastTracker2,
                // 2026-07-30, retour utilisateur : ".gtk/.gt2 - FT2".
                TrackerFormat.GraoumfTracker => TrackerStyle.FastTracker2,
                TrackerFormat.MT2 => TrackerStyle.FastTracker2,
                // 2026-07-31, retour utilisateur : "il faut ouvrir les fichiers .stp
                // avec visu ft2" (Soundtracker Pro II, Atari Falcon).
                TrackerFormat.STP => TrackerStyle.FastTracker2,
                // 2026-07-31, retour utilisateur ("généralise-le au format que libopenmpt
                // peut lire" — longue liste de ~25 formats) : plutôt que d'ajouter une
                // valeur d'enum + un mapping EnrichModule + une ligne ici pour CHACUN (gros
                // risque d'erreur sur des chaînes "type" libopenmpt jamais vérifiées une par
                // une), TrackerFormat.Unknown avec de VRAIS patterns ne peut venir QUE de
                // libopenmpt (EnrichModule ne remplit Module.Patterns que si le décodeur C#
                // n'en a produit aucun, cf. NativeTrackerPlayer.cs) — jamais de ZXTune/UADE,
                // qui renvoient toujours un module "coquille vide" sans patterns (donc sans
                // impact visuel, quel que soit le style choisi ici). Vue FT2 par défaut pour
                // TOUT format encore non cartographié individuellement, cohérent avec le
                // choix systématique de l'utilisateur sur chaque format demandé jusqu'ici.
                TrackerFormat.Unknown => TrackerStyle.FastTracker2,
                TrackerFormat.S3M => TrackerStyle.ScreamTracker3,
                TrackerFormat.IT  => TrackerStyle.ImpulseTracker,
                _                 => TrackerStyle.ProTracker,
            };

            _patternCache.Clear();
            IsLoaded  = true;
            OnPropertyChanged(nameof(HasPatterns));

            Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PLAYER] Exception sur {Path.GetFileName(filePath)}: {ex.Message}");
            DemoBase.App.Controls.StatusScrollerControl.Post($"Erreur {Path.GetFileName(filePath)}: {ex.Message}", isError: true);
            ErrorMessage = $"Erreur : {ex.Message}";
            IsLoaded = false;
        }
        finally { IsLoading = false; }
    }

    // ── Playlist avec pré-chargement gapless ─────────────────────────────────

    private List<string>    _playlist      = [];
    private int             _playlistIndex = 0;
    private PreparedSlot?   _nextSlot      = null;
    private ITrackerPlayer? _nextPlayer    = null;
    private bool            _stopRequested = false;   // Stop volontaire — ignore PlaybackFinished
    private bool            _diagLoggedNearEnd = false; // évite de spammer le log "Fin proche" (cf. OnStateChanged)

    // ── Sortie audio partagée (playlist gapless, formats natifs MOD/XM/S3M/IT) ──
    // Un seul WaveOutEvent reste ouvert pendant toute la durée d'une playlist
    // multi-pistes : au changement de piste on swap juste la source interne
    // (SwappableWaveProvider.Swap) au lieu de recréer un device (Init/Play), ce
    // qui élimine le petit "trou" audible inhérent au ré-init NAudio (~150ms de
    // DesiredLatency à chaque piste). Ne s'active que si TOUTES les pistes de la
    // playlist passent par libopenmpt (NativeTrackerPlayer.AsWaveProvider() non
    // null) — les formats externes (ZXTune/UADE/SNDH) gardent l'ancien
    // comportement (Play() du nouveau avant Stop() de l'ancien).
    private WaveOutEvent?          _sharedWaveOut;
    private SwappableWaveProvider? _sharedProvider;

    /// <summary>HWND de la fenêtre exe à intégrer (PIP). 0 si pas d'exe music.</summary>
    public nint ExeMusicHwnd { get; private set; }
    public event EventHandler<nint>? ExeMusicWindowReady;
    private bool _lastLineWasProgress = false;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isExeMusic;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _exeOutput = string.Empty;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _exeMusicName = string.Empty;



    private sealed class PreparedSlot
    {
        public required ITrackerPlayer                     Player;
        public required TrackerModule                      Module;
        public required Dictionary<int, PatternViewModel>  PatternCache;
        public required TrackerPlayer.Core.Players.SampleRingBuffer? SampleBuffer;
    }

    public async Task LoadFilesAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        ExeMusicHwnd = nint.Zero;
        ExeMusicWindowReady?.Invoke(this, nint.Zero);
        IsExeMusic = false;
        ExeOutput  = string.Empty;
        var list = paths.Where(System.IO.File.Exists).ToList();
        if (list.Count == 0) return;
        _playlist      = list;
        _playlistIndex = 0;
        _nextSlot      = null;
        OnPropertyChanged(nameof(PlaylistDisplay));
        OnPropertyChanged(nameof(HasPlaylist));

        // Charger le 1er + pré-charger le 2ème EN PARALLÈLE
        var loadFirst   = OpenAsync(list[0], ct);
        var preloadNext = list.Count > 1 ? PreloadNextAsync() : Task.CompletedTask;
        await loadFirst;
        await preloadNext;
    }

    /// <summary>
    /// Lance une musique générative sous forme d'exécutable Windows/DOS.
    /// Utilise ExeMusicPlayer : lance le process et déclenche PlaylistEnded à sa fermeture.
    /// </summary>
    public async Task LoadExeMusicAsync(string exePath)
    {
        // Stopper le player courant si nécessaire
        if (_player != null)
        {
            _player.PlaybackFinished -= OnPlaybackFinished;
            _player.StateChanged     -= OnStateChanged;
            _player.Stop();
            _player.Dispose();
        }
        _nextPlayer?.Dispose(); _nextPlayer = null; _nextSlot = null;
        _stopRequested = false;  // reset pour que PlaybackFinished soit traité
        _playlist      = [exePath];
        _playlistIndex = 0;
        OnPropertyChanged(nameof(PlaylistDisplay));
        OnPropertyChanged(nameof(HasPlaylist));

        var exePlayer = new TrackerPlayer.Core.Players.ExeMusicPlayer();
        _player = exePlayer;
        _player.PlaybackFinished += OnPlaybackFinished;
        _player.StateChanged     += OnStateChanged;
        exePlayer.OutputReceived += (_, line) =>
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var lines = ExeOutput.Split('\n').ToList();
                if (lines.Count > 0 && _lastLineWasProgress)
                    lines[lines.Count - 1] = line;
                else
                    lines.Add(line);
                _lastLineWasProgress = true;
                ExeOutput = string.Join("\n", lines);
            });
        };
        IsExeMusic    = true;
        // 2026-08-02, retour utilisateur ("j'ai voulu l'arreter par le bouton stop
        // mais sans effet") : IsLoaded n'était mis à true que dans OpenAsync (chemin
        // tracker), jamais ici — or StopCommand (bouton ⏹) a
        // IsEnabled="{Binding IsLoaded}" dans SoundtrackPlayerView.xaml. Pour une
        // musique exe, IsLoaded restait donc à sa valeur par défaut (false) tant
        // qu'aucun fichier tracker n'avait été ouvert avant dans la session : le
        // bouton Stop était simplement DÉSACTIVÉ, d'où le clic sans effet — le
        // process exe (horizon.exe) ne s'arrêtait qu'à sa fin naturelle.
        IsLoaded      = true;
        ExeOutput     = string.Empty;
        ExeMusicName  = System.IO.Path.GetFileNameWithoutExtension(exePath);
        _lastLineWasProgress = false;
        var module = new TrackerPlayer.Core.Models.TrackerModule
        {
            FilePath = exePath,
            Title    = System.IO.Path.GetFileNameWithoutExtension(exePath),
            Format   = TrackerPlayer.Core.Models.TrackerFormat.Unknown,
        };

        await _player.LoadAsync(module);
    }

    private async Task PreloadNextAsync()
    {
        var nextIndex = _playlistIndex + 1;
        if (nextIndex >= _playlist.Count) return;
        var nextPath = _playlist[nextIndex];
        if (!System.IO.File.Exists(nextPath)) return;
        var generationAtStart = _playerGeneration;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _nextPlayer?.Dispose();
            _nextPlayer = null; _nextSlot = null;

            // Task.Run — CAUSE DU RALENTISSEMENT PÉRIODIQUE observé en lecture :
            // _service.OpenAsync() (donc NativeTrackerPlayer.LoadAsync ->
            // OpenMptStream.EnrichModule -> openmpt_module_get_duration_seconds)
            // fait un appel natif libopenmpt qui SIMULE la lecture complète du
            // module pour calculer sa durée exacte — ça peut prendre plusieurs
            // centaines de ms, parfois ~1s pour un module long. PreloadNextAsync
            // est lancée en fire-and-forget (_ = PreloadNextAsync()) depuis une
            // continuation Dispatcher (UI thread) : sans ce Task.Run, cet await
            // reprend sur le thread UI (SynchronizationContext capturé), qui reste
            // bloqué pendant tout le calcul de durée. Résultat observé : à CHAQUE
            // changement de piste (préchargement de la suivante pendant que la
            // piste courante joue), PatternView se fige ~1s (les mises à jour de
            // position/ligne, elles aussi en attente sur le thread UI via
            // OnStateChanged, ne peuvent plus s'exécuter) puis "rattrape" d'un
            // coup plusieurs lignes de pattern d'un coup dès que le thread UI se
            // libère — la saccade caractéristique remontée par l'utilisateur.
            // Le Task.Run qui suit (construction du cache PatternViewModel) était
            // déjà déporté ; il manquait celui-ci, qui couvre le vrai coût.
            var (module, player) = await Task.Run(() => _service.OpenAsync(nextPath));
            player.MasterVolume = MasterVolume;

            var slot = await Task.Run(() =>
            {
                var cache = new Dictionary<int, PatternViewModel>(module.Patterns.Count);
                foreach (var p in module.Patterns) cache[p.Index] = new PatternViewModel(p);
                var buf = (player is TrackerPlayer.Core.Players.NativeTrackerPlayer ntp) ? ntp.SampleBuffer
                        : (player is TrackerPlayer.Core.Players.ZXTunePlayer zxp)        ? zxp.SampleBuffer
                        : (player is TrackerPlayer.Core.Players.UadePlayer uadep)        ? uadep.SampleBuffer
                        : (player is TrackerPlayer.Core.Players.SndhPlayer sndhp)        ? sndhp.SampleBuffer
                        : null;
                return new PreparedSlot { Player = player, Module = module,
                    PatternCache = cache, SampleBuffer = buf };
            });

            // CleanupPlayer a pu être appelée pendant l'await ci-dessus (changement
            // de release, Dispose) — dans ce cas le player qu'on vient de créer
            // n'a plus personne pour le réclamer : on le dispose immédiatement
            // au lieu de l'assigner à _nextPlayer, où il fuirait indéfiniment.
            if (_playerGeneration != generationAtStart)
            {
                // Stop() et Dispose() dans des try/catch SÉPARÉS (fix 2026-07-24, cf.
                // grand commentaire sur DisposePlayerSafely ci-dessous) : si Stop()
                // lève une exception, Dispose() doit quand même s'exécuter, sinon des
                // ressources natives (libopenmpt _openmptStream, process externe
                // zxtune123/uade123...) fuient silencieusement.
                DisposePlayerSafely(slot.Player);
                return;
            }

            _nextSlot = slot; _nextPlayer = slot.Player;

            // En mode sortie partagée (playlist gapless active, cf. EnsureSharedOutput),
            // le swap à venir se fera en branchant directement AsWaveProvider() du
            // player préchargé sur le device déjà ouvert — inutile (et coûteux : ouvre
            // un device audio distinct pour rien) d'appeler Preload() ici, qui créerait
            // son propre WaveOutEvent séparé jamais utilisé.
            bool willUseSharedOutput = _sharedProvider != null
                && slot.Player is TrackerPlayer.Core.Players.NativeTrackerPlayer ntpShared
                && ntpShared.AsWaveProvider() != null;

            if (!willUseSharedOutput && slot.Player is TrackerPlayer.Core.Players.NativeTrackerPlayer ntpPre)
                await Task.Run(() => ntpPre.Preload());

            System.Diagnostics.Debug.WriteLine(
                $"[GAPLESS] Preload piste {nextIndex + 1}/{_playlist.Count} terminé en " +
                $"{sw.ElapsedMilliseconds}ms — willUseSharedOutput={willUseSharedOutput} " +
                $"duration={module.DurationSeconds:F2}s ({System.IO.Path.GetFileName(nextPath)})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[GAPLESS] PreloadNext piste {nextIndex + 1}/{_playlist.Count} FAILED " +
                $"après {sw.ElapsedMilliseconds}ms: {ex.Message}");
            _nextSlot = null; _nextPlayer = null;
        }
    }

    /// <summary>
    /// Active/alimente la sortie audio partagée pour <paramref name="ntp"/> — voir
    /// le commentaire sur les champs <see cref="_sharedWaveOut"/>/<see cref="_sharedProvider"/>.
    /// Ne fait rien hors contexte playlist (une seule piste ouverte via OpenAsync
    /// direct, ex. lecture d'un favori isolé) ni si libopenmpt n'est pas disponible
    /// pour ce module (AsWaveProvider() null) — dans ces cas, le player garde son
    /// comportement normal (WaveOutEvent propre, cf. NativeTrackerPlayer.Play()).
    /// </summary>
    private void EnsureSharedOutput(NativeTrackerPlayer ntp)
    {
        if (_playlist.Count <= 1)
        {
            System.Diagnostics.Debug.WriteLine("[GAPLESS] EnsureSharedOutput: hors playlist, ignoré");
            return;
        }
        var wp = ntp.AsWaveProvider();
        if (wp == null)
        {
            System.Diagnostics.Debug.WriteLine("[GAPLESS] EnsureSharedOutput: AsWaveProvider() NULL — libopenmpt indisponible pour cette piste");
            return;
        }

        if (_sharedProvider == null)
        {
            System.Diagnostics.Debug.WriteLine("[GAPLESS] EnsureSharedOutput: création du device partagé");
            _sharedProvider = new SwappableWaveProvider();
            _sharedProvider.SourceEnded += OnSharedSourceEnded;
            _sharedProvider.Swap(wp);
            _sharedWaveOut = new WaveOutEvent { DesiredLatency = 150 };
            _sharedWaveOut.Init(_sharedProvider);
            _sharedWaveOut.Volume = MasterVolume;
            _sharedWaveOut.Play();
        }
        else
        {
            // Piste suivante de la playlist : swap de source à chaud sur le device
            // déjà en cours d'exécution — aucun ré-init, donc aucun trou audio.
            System.Diagnostics.Debug.WriteLine("[GAPLESS] EnsureSharedOutput: swap de source à chaud");
            _sharedProvider.Swap(wp);
        }

        ntp.UseSharedOutput(_sharedWaveOut!);
    }

    /// <summary>
    /// Callback du device partagé quand la source active atteint sa fin (SwappableWaveProvider.
    /// Read() vient de retourner 0 octet pour la 1ère fois). Levé de façon SYNCHRONE depuis le
    /// thread audio NAudio, exactement au sample près — c'est maintenant le SEUL déclencheur du
    /// swap piste à piste en mode gapless (cf. refonte du 2026-07-24 ci-dessous).
    ///
    /// Ancienne approche (abandonnée) : un "swap anticipé" dans OnStateChanged, basé sur la
    /// position rapportée périodiquement par le player et une marge devinée avant la fin
    /// estimée. Deux retours utilisateur coup sur coup ont montré que cette marge n'a pas de
    /// bon réglage : trop petite (100ms, réglage initial) → le Dispatcher.BeginInvoke perd
    /// parfois la course contre l'épuisement réel de la source (observé : swap anticipé
    /// déclenché à seulement 70ms de la fin sous charge système, source épuisée avant que le
    /// swap ne s'exécute réellement → trou audible) ; trop grande (350ms, 1ère tentative de
    /// correctif) → le swap coupe alors systématiquement les 350 dernières ms de la piste
    /// courante, perceptible ("il swappe trop vite... avant que l'audio du 1er fichier soit
    /// terminé"). Aucune marge fixe ne peut être à la fois sûre et non tronquante, puisque le
    /// signal utilisé (position estimée) n'est jamais synchronisé au sample près avec la sortie
    /// audio réelle.
    ///
    /// Nouvelle approche : plus aucune anticipation basée sur une estimation. On swap au moment
    /// EXACT où la source réelle s'épuise (ce callback), sans deviner. SwappableWaveProvider.
    /// Read() retente IMMÉDIATEMENT sur la nouvelle source dans le MÊME appel après cet event
    /// (cf. son commentaire) — donc si <see cref="_nextSlot"/> est déjà prêt (cas normal, le
    /// préchargement a largement le temps de finir pendant la lecture de la piste courante),
    /// zéro sample tronqué ET zéro silence : ni trop tôt, ni trop tard.
    /// </summary>
    private void OnSharedSourceEnded(object? sender, EventArgs e)
    {
        var result = SwapAudioNow();
        if (result != null)
        {
            var (slot, oldPlayer) = result.Value;
            System.Diagnostics.Debug.WriteLine(
                $"[GAPLESS] OnSharedSourceEnded — swap au sample près, piste {_playlistIndex + 1}/{_playlist.Count}");
            var app = System.Windows.Application.Current;
            if (app != null)
                app.Dispatcher.BeginInvoke(
                    () => FinishAdvanceUi(slot, oldPlayer),
                    System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        // _nextSlot pas encore prêt (préchargement toujours en cours — rare, cf. diagnostic
        // dans OnStateChanged) ou Stop en cours : pas de swap possible ici, on retombe sur le
        // chemin de secours existant (réouverture complète via OpenAsync, gère aussi le Stop
        // volontaire). Un silence bref est possible dans ce cas précis, mais c'est un problème
        // de préchargement pas assez rapide, pas un problème de timing du swap lui-même.
        System.Diagnostics.Debug.WriteLine(
            "[GAPLESS] OnSharedSourceEnded — _nextSlot pas prêt (ou stop) → chemin de secours");
        OnPlaybackFinished(this, EventArgs.Empty);
    }

    /// <summary>
    /// Partie AUDIO-CRITIQUE du swap de piste : change la source du device partagé + le player
    /// actif. Ne touche à AUCUNE propriété liée à l'UI/WPF (bindings) — appelable depuis
    /// n'importe quel thread, y compris le thread audio NAudio (cf. OnSharedSourceEnded, qui
    /// l'appelle directement et de façon synchrone). Retourne (slot, oldPlayer) pour que
    /// l'appelant termine le reste (UI, préchargement suivant) via <see cref="FinishAdvanceUi"/>
    /// — sur le thread UI — ou null si rien n'a été swappé (stop en cours, ou pas de slot prêt).
    /// </summary>
    private (PreparedSlot slot, ITrackerPlayer? oldPlayer)? SwapAudioNow()
    {
        if (_nextSlot == null) return null;

        if (_stopRequested)
        {
            // Stop demandé entre le préchargement du slot et ce swap — ne pas avancer,
            // juste nettoyer le slot devenu inutile.
            var cancelledSlot = _nextSlot;
            _nextSlot = null; _nextPlayer = null;
            _ = Task.Run(() => DisposePlayerSafely(cancelledSlot.Player));
            return null;
        }

        _playerGeneration++;
        var slot      = _nextSlot;
        var oldPlayer = _player;
        _nextSlot      = null;
        _nextPlayer    = null;
        _playlistIndex++;
        _diagLoggedNearEnd = false;

        _player = slot.Player;
        _player.StateChanged     += OnStateChanged;
        _player.PlaybackFinished += OnPlaybackFinished;
        _player.MasterVolume      = MasterVolume;

        if (_player is NativeTrackerPlayer ntpNext)
            EnsureSharedOutput(ntpNext);

        _player.Play();

        if (oldPlayer != null)
        {
            oldPlayer.StateChanged     -= OnStateChanged;
            oldPlayer.PlaybackFinished -= OnPlaybackFinished;

            // Fix 2026-07-24 (gel après ~20 pistes, playlists 100% .MOD — donc hors
            // process externes ZXTune/UADE, cf. RESUME_PROJET.md) : Stop() de l'ancien
            // player était jusqu'ici DIFFÉRÉ à la Task.Run asynchrone de FinishAdvanceUi
            // (DisposePlayerSafely), potentiellement bien après ce swap. Pour
            // NativeTrackerPlayer, Stop() annule la boucle de poll interne
            // (PollStateAsync, ~25 fps, appels natifs libopenmpt pour position/pattern/
            // volumes) — tant que Stop() n'a pas été appelé, cette boucle continue de
            // tourner et de faire des appels natifs sur le module de l'ANCIENNE piste,
            // en parallèle de la nouvelle qui joue déjà. Si la Task.Run de disposition
            // est retardée (ThreadPool chargé par les Task.Run de PreloadNextAsync,
            // lancée juste après pour la piste suivante), la fenêtre pendant laquelle
            // CE poll loop tourne encore ET où Dispose() peut arriver (destruction du
            // handle natif openmpt pendant qu'un appel natif est en cours sur un AUTRE
            // thread) s'élargit — un module natif libopenmpt n'est pas garanti
            // thread-safe pour un tel accès concurrent. Plausible cause d'un gel/plantage
            // silencieux (pas d'exception .NET, juste un blocage ou une corruption côté
            // natif) qui s'aggrave avec le nombre de transitions accumulées.
            //
            // Fix : Stop() est maintenant appelé ICI, de façon SYNCHRONE, immédiatement
            // après le swap (annule _pollCts sans bloquer — en sortie partagée,
            // _ownsWaveOut est false donc Stop() ne touche à aucun WaveOutEvent, juste
            // quelques écritures de champs + Cancel() du CancellationTokenSource : rapide
            // et sûr à exécuter ici, y compris depuis le thread audio NAudio). Dispose()
            // (destruction du handle natif) reste différé à FinishAdvanceUi — il n'y a
            // plus de risque puisque la boucle de poll est déjà arrêtée.
            try { oldPlayer.Stop(); } catch { /* tenté à nouveau, sans risque, dans DisposePlayerSafely */ }
        }

        return (slot, oldPlayer);
    }

    /// <summary>
    /// Partie UI du swap de piste (propriétés liées aux bindings WPF + préchargement de la
    /// piste suivante) — DOIT s'exécuter sur le thread UI. Séparée de <see cref="SwapAudioNow"/>
    /// pour que celle-ci puisse s'exécuter immédiatement, sans attendre un aller-retour
    /// Dispatcher, quand elle est appelée depuis le thread audio.
    /// </summary>
    private void FinishAdvanceUi(PreparedSlot slot, ITrackerPlayer? oldPlayer)
    {
        IsPlaying = true;

        if (oldPlayer != null)
            _ = Task.Run(() => DisposePlayerSafely(oldPlayer));

        Module           = slot.Module;
        _patternCache    = slot.PatternCache;
        Title            = !string.IsNullOrWhiteSpace(slot.Module.Title)
            ? slot.Module.Title
            : System.IO.Path.GetFileName(_playlist[_playlistIndex]);
        CurrentFileName  = System.IO.Path.GetFileName(_playlist[_playlistIndex]);
        // 2026-07-31, retour utilisateur ("le nom du format (tracker) reste le même quand
        // je fais 'tout jouer' ! il met le même type que le 1er lu à tous les musiques
        // d'aprés") : FinishAdvanceUi (avance playlist/gapless, chemin séparé
        // d'OpenAsync ci-dessus) ne réassignait jamais FormatDisplay — donc la valeur du
        // tout premier morceau ouvert restait affichée pour toute la playlist. Même
        // logique que OpenAsync (ligne ~233).
        FormatDisplay    = !string.IsNullOrWhiteSpace(slot.Module.FormatName)
            ? slot.Module.FormatName
            : slot.Module.Format.ToString();
        DurationSeconds  = slot.Module.DurationSeconds;
        PositionSeconds  = 0;
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(PositionDisplay));
        CurrentPatternVm = null;
        TrackerStyle     = slot.Module.Format switch
        {
            TrackerFormat.XM  => TrackerStyle.FastTracker2,
            TrackerFormat.DBM => TrackerStyle.FastTracker2,
            TrackerFormat.ULT => TrackerStyle.FastTracker2,
            TrackerFormat.XMF => TrackerStyle.FastTracker2,
            TrackerFormat.AMF         => TrackerStyle.FastTracker2,
            TrackerFormat.Composer669 => TrackerStyle.FastTracker2,
            TrackerFormat.DIGI        => TrackerStyle.FastTracker2,
            TrackerFormat.DSM => TrackerStyle.FastTracker2,
            TrackerFormat.DTM => TrackerStyle.FastTracker2,
            TrackerFormat.MDL => TrackerStyle.FastTracker2,
            TrackerFormat.DMF => TrackerStyle.FastTracker2,
            TrackerFormat.AMS => TrackerStyle.FastTracker2,
            TrackerFormat.PSM => TrackerStyle.FastTracker2,
            TrackerFormat.GraoumfTracker => TrackerStyle.FastTracker2,
            TrackerFormat.MT2 => TrackerStyle.FastTracker2,
            TrackerFormat.STP => TrackerStyle.FastTracker2,
            // 2026-07-31, retour utilisateur (généralisation libopenmpt, ~45 formats) :
            // même raisonnement que dans OpenAsync ci-dessus — TrackerFormat.Unknown
            // avec de vrais patterns ne peut venir que de libopenmpt. Vue FT2 par
            // défaut pour tout format non cartographié individuellement.
            TrackerFormat.Unknown => TrackerStyle.FastTracker2,
            TrackerFormat.S3M => TrackerStyle.ScreamTracker3,
            TrackerFormat.IT  => TrackerStyle.ImpulseTracker,
            _                 => TrackerStyle.ProTracker,
        };
        OnPropertyChanged(nameof(SampleBuffer));
        OnPropertyChanged(nameof(WaveformOverview));
        OnPropertyChanged(nameof(IsFormatUnsupported));
        OnPropertyChanged(nameof(IsUadeFormat));
        OnPropertyChanged(nameof(EngineDisplay));
        OnPropertyChanged(nameof(HasPatterns));
        OnPropertyChanged(nameof(PlaylistDisplay));

        _ = PreloadNextAsync();
    }

    /// <summary>Arrête et libère le device audio partagé — appelé quand la session
    /// de lecture playlist se termine (nouvelle ouverture hors playlist, Stop,
    /// Dispose) pour ne pas laisser un WaveOutEvent tourner indéfiniment.</summary>
    private void TeardownSharedOutput()
    {
        if (_sharedProvider != null)
            _sharedProvider.SourceEnded -= OnSharedSourceEnded;
        try { _sharedWaveOut?.Stop(); _sharedWaveOut?.Dispose(); } catch { /* déjà arrêté/disposé */ }
        _sharedWaveOut  = null;
        _sharedProvider = null;
    }

    // ── Commandes ─────────────────────────────────────────────────────────────

    [RelayCommand] public void Play()  { _player?.Play();  IsPlaying = true;  IsPaused = false; }
    [RelayCommand] public void Pause() { _player?.Pause(); IsPaused  = true;  IsPlaying = false; }

    // Bascule Play/Pause (2026-07-29, raccourci clavier Espace) — le bouton Play/Pause
    // de SoundtrackPlayerView appelait jusqu'ici toujours PlayCommand, y compris en
    // cours de lecture (l'icône changeait bien via IsPlaying mais le clic relançait Play()
    // au lieu de mettre en pause). Utilisé aussi par GlobalKeyboardService pour Espace.
    [RelayCommand] public void PlayPause() { if (IsPlaying) Pause(); else Play(); }
    [RelayCommand] public void Stop()
    {
        _stopRequested  = true;   // signaler AVANT Stop() pour que PlaybackFinished l'ignore
        if (_nextPlayer != null)
            DisposePlayerSafely(_nextPlayer);
        _nextSlot       = null;
        _nextPlayer     = null;
        _player?.Stop();
        // _player.Stop() ne coupe pas le device partagé (cf. UseSharedOutput —
        // Stop() d'un player en sortie partagée ne touche jamais au WaveOutEvent,
        // qui appartient à la session entière) : sans ce teardown explicite, le
        // son continuerait après un clic sur "Stop" en mode playlist gapless.
        TeardownSharedOutput();
        IsPlaying       = false;
        IsPaused        = false;
        PositionSeconds = 0;

        // Les vumètres (ChannelLevels) ne sont mis à jour que par OnStateChanged,
        // qui ne se déclenche plus une fois le player arrêté. PatternView expose
        // ChannelLevels comme DependencyProperty dont le callback de redessin ne
        // se déclenche que si la référence du tableau change (Equals par défaut
        // sur float[] compare la référence, pas le contenu) — donc on assigne ICI
        // une nouvelle instance plutôt que de vider le tableau existant en place,
        // sinon le contrôle ne redessine jamais et les vumètres restent figés
        // visuellement sur leurs dernières valeurs.
        if (ChannelLevels.Length > 0)
            ChannelLevels = new float[ChannelLevels.Length];
        OnPropertyChanged(nameof(PositionDisplay));
    }

    /// <summary>Déclenché quand Previous est pressé alors qu'on est déjà à la première piste.</summary>
    public event EventHandler? PreviousRequestedBeyondPlaylist;

    [RelayCommand]
    private async Task Previous()
    {
        if (_playlist.Count == 0) return;
        // Si on est à la première piste et la position est au début → release précédente
        if (_playlistIndex == 0 && PositionSeconds < 3.0)
        {
            PreviousRequestedBeyondPlaylist?.Invoke(this, EventArgs.Empty);
            return;
        }
        _playlistIndex = Math.Max(0, _playlistIndex - 2); // -2 car OnPlaybackFinished fait +1
        _nextSlot = null; _nextPlayer = null;
        await OpenAsync(_playlist[_playlistIndex]);
        _ = PreloadNextAsync();
    }

    [RelayCommand]
    private async Task Next()
    {
        if (_playlist.Count == 0 || _playlistIndex + 1 >= _playlist.Count)
        {
            // Fin de playlist — signaler au MediaBrowser de passer à la release suivante
            NextRequestedBeyondPlaylist?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_nextSlot != null)
        {
            AdvanceToNextSlot();
        }
        else
        {
            _playlistIndex++;
            await OpenAsync(_playlist[_playlistIndex]);
            _ = PreloadNextAsync();
        }
    }

    /// <summary>
    /// Swap instantané vers le slot préchargé — utilisé par la commande Next() (skip manuel de
    /// l'utilisateur). Enchaîne simplement les deux moitiés du swap (cf. OnSharedSourceEnded
    /// pour le détail de la refonte du 2026-07-24) l'une après l'autre : Next() s'exécute déjà
    /// sur le thread UI (RelayCommand), donc pas besoin de Dispatcher ici.
    /// </summary>
    private void AdvanceToNextSlot()
    {
        var result = SwapAudioNow();
        if (result == null) return; // rien à swapper (stop en cours, ou pas de slot prêt)
        var (slot, oldPlayer) = result.Value;
        FinishAdvanceUi(slot, oldPlayer);
    }

    public string PlaylistDisplay => _playlist.Count > 1
        ? $"{_playlistIndex + 1} / {_playlist.Count}" : "";
    public bool HasPlaylist => _playlist.Count > 1;

    [RelayCommand]
    public void SeekToOrder(int orderIndex) => _player?.SeekToOrder(orderIndex);

    // 2026-07-30, retour utilisateur : "2 boutons supplémentaires pour passer
    // d'une subsong à l'autre" — SelectSubsong() gère déjà proprement le cas où
    // l'utilisateur bascule EN COURS de lecture (désabonne/réabonne
    // OnSubsongFinished côté UadePlayer pour éviter un double-avancement).
    [RelayCommand]
    public void NextSubsong()
    {
        if (_player == null || CurrentSubsongIndex + 1 >= SubsongCount) return;
        _player.SelectSubsong(CurrentSubsongIndex + 1);
        CurrentSubsongIndex = _player.CurrentSubsongIndex;
    }

    [RelayCommand]
    public void PreviousSubsong()
    {
        if (_player == null || CurrentSubsongIndex <= 0) return;
        _player.SelectSubsong(CurrentSubsongIndex - 1);
        CurrentSubsongIndex = _player.CurrentSubsongIndex;
    }

    partial void OnMasterVolumeChanged(float value)
    {
        if (_player != null) _player.MasterVolume = value;
    }

    // ── Callbacks player ──────────────────────────────────────────────────────

    private void OnStateChanged(object? sender, PlaybackState state)
    {
        // Plus de swap déclenché ici (ancien "swap anticipé" basé sur une marge devinée avant
        // la fin estimée — supprimé le 2026-07-24, cf. le grand commentaire sur
        // OnSharedSourceEnded pour le pourquoi). Le swap se fait maintenant exclusivement dans
        // OnSharedSourceEnded, au moment exact où la source réelle s'épuise. Diagnostic
        // seulement ici : si on approche de la fin sans que _nextSlot soit prêt, le swap
        // réactif tombera sur le chemin de secours (réouverture, silence bref possible) plutôt
        // que sur le swap au sample près — utile pour repérer un préchargement trop lent.
        if (!_diagLoggedNearEnd && _nextSlot == null && state.DurationSeconds > 0
            && state.PositionSeconds >= state.DurationSeconds - 1.0)
        {
            _diagLoggedNearEnd = true;
            System.Diagnostics.Debug.WriteLine(
                $"[GAPLESS] Fin proche piste {_playlistIndex + 1}/{_playlist.Count} " +
                $"pos={state.PositionSeconds:F2}/{state.DurationSeconds:F2} _nextSlot=PAS PRÊT " +
                "— risque de silence bref au swap réactif");
        }

        var app = System.Windows.Application.Current;
        if (app == null) return;
        app.Dispatcher.InvokeAsync(() =>
        {
            PositionSeconds      = state.PositionSeconds;
            CurrentBpm           = state.CurrentBpm;
            CurrentSpeed         = state.CurrentSpeed;
            CurrentPatternIndex  = state.CurrentPattern;
            CurrentOrderIndex    = state.CurrentOrder;
            HighlightedRow       = state.CurrentRow;
            OnPropertyChanged(nameof(PositionDisplay));
            // 2026-08-01, retour utilisateur ("l'oscilloscope vide s'affiche encore
            // [...] malgré le 'unknown format de uade'") : contrairement à libopenmpt
            // (IsAvailable connu dès le chargement, synchrone), UADE ne sait qu'un
            // fichier ne produit aucun son qu'APRÈS avoir tenté de le streamer (cf.
            // UadePlayer.Read, TrackerPlayer.Core — IWaveProvider depuis le 2026-08-06,
            // même logique qu'avant) — ce tick, déjà appelé périodiquement pendant la
            // lecture (25fps côté UADE) et déjà marshalé sur le thread UI ci-dessus,
            // est le point de ré-évaluation le plus simple et le plus sûr (pas besoin
            // d'un mécanisme de notification cross-thread dédié).
            OnPropertyChanged(nameof(IsFormatUnsupported));
            OnPropertyChanged(nameof(IsUadeFormat));
            OnPropertyChanged(nameof(EngineDisplay));

            // 2026-07-30, retour utilisateur ("la durée total du subsong ne se met
            // pas à jour. la durée reste celle du 1er joué") : DurationSeconds
            // n'était jamais resynchronisée après le chargement initial — seule
            // PositionSeconds l'était à chaque tick. SelectSubsong (ZXTunePlayer)
            // met pourtant bien à jour sa propre PlaybackState.DurationSeconds en
            // interne, mais rien ne la reprenait ici. Filtre ">0" pour ne pas
            // clignoter sur 0:00 pendant le bref silence de rendu au changement de
            // subsong ZXTune (cf. OnWaveOutStopped) ; couvre aussi bien le
            // changement manuel (◀/▶) que l'avance automatique naturelle.
            if (state.DurationSeconds > 0)
            {
                DurationSeconds = state.DurationSeconds;
                OnPropertyChanged(nameof(DurationDisplay));
            }

            // Rafraîchi à chaque tick (25fps côté UADE) : couvre aussi bien
            // l'avance automatique de subsong (OnSubsongFinished côté UadePlayer)
            // que le SelectSubsong manuel déclenché par les boutons ◀/▶.
            if (_player != null)
            {
                SubsongCount        = _player.SubsongCount;
                CurrentSubsongIndex = _player.CurrentSubsongIndex;
            }

            if (state.ChannelVolumes is { Length: > 0 } vols)
            {
                if (ChannelLevels.Length != vols.Length)
                    ChannelLevels = new float[vols.Length];
                for (int i = 0; i < vols.Length; i++)
                    ChannelLevels[i] = state.IsPlaying ? vols[i] / 64f : 0f;
                OnPropertyChanged(nameof(ChannelLevels));
            }
            else if (!state.IsPlaying && ChannelLevels.Length > 0)
            {
                ChannelLevels = new float[ChannelLevels.Length];
                OnPropertyChanged(nameof(ChannelLevels));
            }

            if (Module != null && state.CurrentPattern < Module.Patterns.Count)
            {
                if (!_patternCache.TryGetValue(state.CurrentPattern, out var vm))
                {
                    vm = new PatternViewModel(Module.Patterns[state.CurrentPattern]);
                    _patternCache[state.CurrentPattern] = vm;
                }
                CurrentPatternVm = vm;
            }
        });
    }

    private void OnPlaybackFinished(object? sender, EventArgs e)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;

        // Capturé AU MOMENT où ce signal est levé (thread audio pour le mode
        // partagé, synchrone), pas au moment où le Dispatcher l'exécute — permet
        // de détecter un signal PÉRIMÉ : si _playerGeneration a changé d'ici
        // l'exécution du lambda ci-dessous, c'est qu'un AUTRE chemin (SwapAudioNow,
        // appelé directement par OnSharedSourceEnded ou par un Next() manuel) a
        // déjà traité cette même transition entre-temps — typiquement le cas où
        // OnSharedSourceEnded a réussi son swap et appelle quand même ce
        // OnPlaybackFinished "de secours" par un autre chemin (ex. signal du
        // player externe pour les formats hors sortie partagée). Sans cette garde,
        // un signal "en retard" retombait sur le chemin de secours (_nextSlot déjà
        // consommé par l'autre swap → traité comme "rien de préchargé") et
        // ré-avançait l'index UNE SECONDE FOIS : une piste sur deux sautée/coupée,
        // avec un nouveau lecteur+device créés à chaque fois (d'où le
        // ralentissement progressif observé sur les longues playlists).
        var generationAtCall = _playerGeneration;

        // DispatcherPriority.Background : s'exécute APRÈS les événements Input/Normal
        // (clics utilisateur). Évite la race condition où l'avancement de playlist
        // se déclenchait avant que la navigation ait pu appeler Stop().
        app.Dispatcher.BeginInvoke(async () =>
        {
            // Stop volontaire — ne pas avancer dans la playlist
            if (_stopRequested)
            {
                System.Diagnostics.Debug.WriteLine($"[PLAYER] Finished ignoré — Stop volontaire");
                _stopRequested = false;
                return;
            }

            // Signal périmé : une transition a déjà eu lieu depuis que ce signal
            // a été levé (typiquement le swap anticipé, qui gagne la course de
            // quelques millisecondes contre le signal réactif de fin de piste).
            if (_playerGeneration != generationAtCall)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PLAYER] Finished ignoré — signal périmé (génération {generationAtCall} → {_playerGeneration})");
                return;
            }

            // Si _nextSlot est prêt, AdvanceToNextSlot fait lui-même _playlistIndex++
            if (_nextSlot != null)
            {
                System.Diagnostics.Debug.WriteLine($"[PLAYER] Finished → AdvanceToNextSlot");
                AdvanceToNextSlot();
                return;
            }

            _playlistIndex++;
            System.Diagnostics.Debug.WriteLine($"[PLAYER] Finished → {_playlistIndex}/{_playlist.Count}");
            if (_playlistIndex < _playlist.Count)
            {
                System.Diagnostics.Debug.WriteLine($"[PLAYER] → {_playlist[_playlistIndex]}");
                await OpenAsync(_playlist[_playlistIndex]);
                _ = PreloadNextAsync();
            }
            else
            {
                IsPlaying = false; IsPaused = false;
                _playlist = []; _playlistIndex = 0;
                // Playlist épuisée : libérer le device partagé, sinon il reste ouvert
                // (silence en boucle) jusqu'à la prochaine ouverture.
                TeardownSharedOutput();
                // Remettre les vumètres à zéro
                if (ChannelLevels.Length > 0)
                    ChannelLevels = new float[ChannelLevels.Length];
                // Signaler la fin de la playlist — utilisé par le MediaBrowser
                // pour passer automatiquement à la release suivante.
                PlaylistEnded?.Invoke(this, EventArgs.Empty);
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── Nettoyage ─────────────────────────────────────────────────────────────

    private void CleanupPlayer()
    {
        _playerGeneration++;
        TeardownSharedOutput();
        if (_player != null)
        {
            var p = _player;
            p.StateChanged     -= OnStateChanged;
            p.PlaybackFinished -= OnPlaybackFinished;
            _player = null;
            // Stop()+Dispose() désormais dans DisposePlayerSafely (try/catch séparés,
            // cf. commentaire ci-dessous) — un Stop() qui lève une exception ne doit
            // plus empêcher Dispose() de libérer les ressources natives.
            DisposePlayerSafely(p);
        }
        // Le préchargement playlist (PreloadNextAsync) crée un player en
        // arrière-plan après chaque ouverture — s'il n'a jamais été consommé
        // (changement de release ou arrêt avant la fin du préchargement),
        // il fuyait en mémoire indéfiniment, jamais disposé.
        if (_nextPlayer != null)
        {
            var np = _nextPlayer;
            _nextPlayer = null;
            DisposePlayerSafely(np);
        }
        _nextSlot      = null;
        IsPlaying = false; IsPaused = false; IsLoaded = false;
        _patternCache.Clear();
        CurrentPatternVm = null;

        // Même raison que dans Stop() : nouvelle instance requise pour que
        // PatternView (DependencyProperty, comparaison par référence) redessine.
        if (ChannelLevels.Length > 0)
            ChannelLevels = new float[ChannelLevels.Length];
    }

    /// <summary>
    /// Arrête puis libère un player, dans deux try/catch SÉPARÉS (fix 2026-07-24).
    ///
    /// Avant ce fix, tout le fichier enchaînait Stop() et Dispose() dans un seul
    /// try/catch (ex. "try { player.Stop(); player.Dispose(); } catch {}") — si
    /// Stop() levait une exception (NAudio/WaveOutEvent peut lever une MmException
    /// sous certaines conditions de driver audio, notamment lors de cycles Init/
    /// Dispose répétés à chaque piste pour les formats externes ZXTune/UADE, qui ne
    /// partagent pas le device de sortie gapless), Dispose() n'était alors JAMAIS
    /// appelé — et pour NativeTrackerPlayer, Dispose() libère EN PLUS de Stop() le
    /// handle natif libopenmpt (_openmptStream, via openmpt_module_destroy) ; pour
    /// ZXTunePlayer/UadePlayer, Dispose() est ce qui garantit la fermeture du
    /// process externe (zxtune123.exe/uade123.exe) et du fichier WAV temporaire.
    /// Un Stop() qui échoue laissait donc fuir silencieusement mémoire native,
    /// fichiers temp et/ou process externes à CHAQUE occurrence — plausible
    /// explication d'un gel progressif de l'interface après un nombre significatif
    /// de pistes lues en continu (symptôme rapporté : ~20 pistes sur 30, lecture de
    /// 3 playlists à la suite, sans log récupérable). Voir aussi les fix similaires
    /// apportés côté process externes dans TrackerPlayer.Core/Players/
    /// ExternalPlayers.cs (kill explicite sur timeout de zxtune123/uade123 -g, qui
    /// pouvaient rester orphelins ET bloquer indéfiniment le thread appelant).
    /// </summary>
    private static void DisposePlayerSafely(ITrackerPlayer player)
    {
        try { player.Stop(); } catch { /* on tente quand même Dispose() ci-dessous */ }
        try { player.Dispose(); } catch { /* déjà disposé, ou driver déjà en échec */ }
    }

    public void Dispose() => CleanupPlayer();
}
