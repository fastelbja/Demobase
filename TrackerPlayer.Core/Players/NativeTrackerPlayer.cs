using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using TrackerPlayer.Core.Decoders;
using TrackerPlayer.Core.Interfaces;
using TrackerPlayer.Core.Models;

// Alias pour lever l'ambiguïté entre NAudio.Wave.PlaybackState et Models.PlaybackState
using ModelsPlaybackState = TrackerPlayer.Core.Models.PlaybackState;

namespace TrackerPlayer.Core.Players
{
    /// <summary>
    /// Lecteur audio de base utilisant NAudio.
    /// Pour les formats natifs (MOD, S3M, XM, IT) : délègue à libopenmpt via P/Invoke.
    /// Pour UADE / ZXTune : délègue aux wrappers externes (voir UadePlayer / ZxTunePlayer).
    ///
    /// Ce fichier contient aussi <see cref="TrackerService"/>, le point d'entrée principal.
    /// </summary>
    public sealed class NativeTrackerPlayer : ITrackerPlayer
    {
        /// <summary>Version de libopenmpt.dll effectivement chargée (ex. "0.8.1"), ou "?" si
        /// indisponible/non chargée. Wrapper public — <see cref="OpenMptInterop"/> est
        /// internal, inaccessible depuis DemoBase.App (autre assembly). Cf. le commentaire de
        /// <see cref="OpenMptInterop.LibraryVersionString"/> pour le contexte (retour
        /// utilisateur sur le support ChipTracker/Future Composer selon la version).</summary>
        public static string LibopenmptVersion => OpenMptInterop.LibraryVersionString;

        /// <summary>Vrai si la libopenmpt.dll chargée est antérieure à 0.8.0 — dans ce cas,
        /// ChipTracker, Future Composer et plusieurs autres formats exotiques ne peuvent PAS
        /// être lus par libopenmpt, quoi que fasse le code C# côté DemoBase (cf.
        /// OpenMptInterop.IsBefore_0_8_0).</summary>
        public static bool LibopenmptIsBefore_0_8_0 => OpenMptInterop.IsBefore_0_8_0;

        // ── État ─────────────────────────────────────────────────────────
        private TrackerModule? _module;
        private WaveOutEvent? _waveOut;
        private OpenMptStream? _openmptStream;
        private ModelsPlaybackState _state = new();
        private readonly ILogger _log;
        private CancellationTokenSource? _pollCts;

        /// <summary>
        /// False quand ce player utilise un WaveOutEvent "partagé" fourni par
        /// <see cref="UseSharedOutput"/> (mode playlist gapless, cf.
        /// SoundtrackPlayerViewModel) plutôt qu'un device qu'il a lui-même créé.
        /// Empêche Stop()/Dispose() d'arrêter/libérer un device qui appartient en
        /// réalité à la session de lecture entière, pas à cette seule piste.
        /// </summary>
        private bool _ownsWaveOut = true;

        public event EventHandler<ModelsPlaybackState>? StateChanged;
        public event EventHandler? PlaybackFinished;

        public TrackerFormat[] SupportedFormats =>
        [
            TrackerFormat.MOD, TrackerFormat.S3M,
            TrackerFormat.XM,  TrackerFormat.IT
        ];

        /// <summary>Buffer circulaire des samples audio pour l'oscilloscope.</summary>
        public SampleRingBuffer? SampleBuffer => _openmptStream?.SampleBuffer;

        // 2026-08-07 : transfert (comme SampleBuffer juste au-dessus) de la vue
        // d'ensemble de la forme d'onde exposée par OpenMptStream — oubliée lors du
        // premier correctif (échec de build utilisateur CS1061, WaveformOverview
        // absent de NativeTrackerPlayer alors que SoundtrackPlayerViewModel caste
        // sur CETTE classe, pas sur OpenMptStream qui est interne/non accessible
        // hors de ce fichier).
        public WaveformOverviewBuffer? WaveformOverview => _openmptStream?.WaveformOverview;

        // 2026-08-01, retour utilisateur ("format non jouable" affiché au lieu de
        // l'oscilloscope vide) : true si aucune info fiable n'est disponible (ex.
        // libopenmpt.dll absente — un tout autre problème, pas propre à CE fichier, pas
        // signalé ici) OU si libopenmpt a réellement réussi à charger ce fichier. False
        // uniquement quand libopenmpt a été appelé ET a explicitement rejeté le contenu
        // (cf. OpenMptStream.IsAvailable) — le seul cas où on est sûr que ce format
        // précis n'est pas joué par ce backend.
        public bool IsPlayable => _openmptStream == null || _openmptStream.IsAvailable;

        public ModelsPlaybackState CurrentState => _state;

        public float MasterVolume
        {
            get => _waveOut?.Volume ?? 1f;
            set { if (_waveOut != null) _waveOut.Volume = Math.Clamp(value, 0f, 1f); }
        }

        // 2026-07-30 : libopenmpt ne gère pas la notion de subsong UADE — un seul
        // "morceau" par fichier ici. Stub sans effet pour satisfaire ITrackerPlayer.
        public int  SubsongCount        => 1;
        public int  CurrentSubsongIndex => 0;
        public void SelectSubsong(int index) { /* non applicable */ }

        public NativeTrackerPlayer(ILogger? logger = null)
        {
            _log = logger ?? NullLogger.Instance;
        }

        public Task LoadAsync(TrackerModule module, CancellationToken ct = default)
        {
            _module = module;
            Stop();

            System.Diagnostics.Debug.WriteLine($"[libopenmpt] IsAvailable={OpenMptInterop.IsAvailable} File={module.FilePath}");

            // Charge le fichier dans libopenmpt si disponible
            if (OpenMptInterop.IsAvailable && File.Exists(module.FilePath))
            {
                _openmptStream = new OpenMptStream(module.FilePath, module.Channels, _log);
                _log.LogInformation("Chargé via libopenmpt : {Path}", module.FilePath);

                // ── Enrichit le module avec les vraies métadonnées libopenmpt ──
                // Ceci compense un éventuel décodeur C# incomplet ou un format
                // non décodé nativement (fallback métadonnées minimales).
                _openmptStream.EnrichModule(module);
            }
            else
            {
                _log.LogWarning("libopenmpt non disponible — lecture silencieuse (simulation).");
            }

            _state = new ModelsPlaybackState
            {
                DurationSeconds = module.DurationSeconds,
                CurrentBpm      = module.InitialBpm,
                CurrentSpeed    = module.InitialSpeed,
                ChannelVolumes  = new int[Math.Max(module.Channels, 1)]
            };

            return Task.CompletedTask;
        }

        /// <summary>
        /// Expose le stream libopenmpt comme IWaveProvider pour le mode Long Play
        /// (SwappableWaveProvider peut l'utiliser directement sans WaveOutEvent intermédiaire).
        /// </summary>
        public IWaveProvider? AsWaveProvider() =>
            _openmptStream is not null ? (IWaveProvider)_openmptStream : null;

        /// <summary>
        /// Bascule ce player en mode "sortie partagée" : au lieu de créer son propre
        /// WaveOutEvent (comme Play()/Preload() le font normalement), il devient une
        /// simple source de samples branchée sur un device déjà initialisé et en cours
        /// d'exécution, géré par l'appelant (SwappableWaveProvider.Swap côté
        /// SoundtrackPlayerViewModel). Play() ne fait alors plus que démarrer le
        /// polling d'état (position/pattern), sans toucher au device — c'est le swap
        /// de source sur le WaveOutEvent déjà lancé qui assure la continuité audio,
        /// sans le "trou" qu'un nouveau WaveOutEvent.Init()/Play() introduit à chaque
        /// changement de piste (latence de démarrage du device, ~150ms de
        /// DesiredLatency à chaque fois).
        /// </summary>
        public void UseSharedOutput(WaveOutEvent sharedWaveOut)
        {
            _waveOut     = sharedWaveOut;
            _ownsWaveOut = false;
        }

        /// <summary>
        /// Pré-initialise le device audio (WaveOutEvent.Init) sans démarrer la lecture.
        /// Appelé en arrière-plan pendant que le morceau précédent joue.
        /// Après Preload(), Play() démarre immédiatement sans délai d'init.
        /// </summary>
        public void Preload()
        {
            if (_module is null) return;
            if (_waveOut != null) return; // déjà initialisé

            IWaveProvider provider = _openmptStream is not null
                ? (IWaveProvider)_openmptStream
                : new SilenceWaveProvider(44100, 2);

            _waveOut = new WaveOutEvent { DesiredLatency = 150 };
            _waveOut.Init(provider);
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            // NE PAS appeler _waveOut.Play() — juste Init
        }

        public void Play()
        {
            if (_module is null) return;

            // Mode sortie partagée (playlist gapless) : le WaveOutEvent partagé tourne
            // déjà en continu (et a déjà reçu ce player comme source via
            // SwappableWaveProvider.Swap, cf. SoundtrackPlayerViewModel) — on ne fait
            // (re)démarrer que le device s'il était en pause (Play() après Pause() est
            // idempotent côté NAudio quand déjà en lecture), jamais son initialisation.
            if (!_ownsWaveOut && _waveOut != null)
            {
                _waveOut.Play();
                _state.IsPlaying = true;
                _state.IsPaused  = false;
                _pollCts = new CancellationTokenSource();
                _ = PollStateAsync(_pollCts.Token);
                return;
            }
            // Le device partagé a été détruit entre-temps (Stop global de la session,
            // cf. SoundtrackPlayerViewModel.TeardownSharedOutput) : ce player redevient
            // propriétaire de son propre device pour pouvoir reprendre normalement — la
            // continuité gapless ne concerne que les transitions automatiques entre
            // pistes, pas une reprise manuelle après un Stop explicite.
            _ownsWaveOut = true;

            // Reprendre après Pause
            if (_state.IsPaused && _waveOut != null)
            {
                _waveOut.Play();
                _state.IsPaused  = false;
                _state.IsPlaying = true;
                return;
            }

            // Si Preload() a déjà fait Init() → utiliser le WaveOut existant
            if (_waveOut != null)
            {
                _waveOut.Play();
                _state.IsPlaying = true;
                _state.IsPaused  = false;
                _pollCts = new CancellationTokenSource();
                _ = PollStateAsync(_pollCts.Token);
                return;
            }

            // Initialisation complète (cas normal sans pré-chargement)
            Stop();

            IWaveProvider provider = _openmptStream is not null
                ? (IWaveProvider)_openmptStream
                : new SilenceWaveProvider(44100, 2);

            _waveOut = new WaveOutEvent { DesiredLatency = 150 };
            _waveOut.Init(provider);
            _waveOut.PlaybackStopped += OnPlaybackStopped;
            _waveOut.Play();

            _state.IsPlaying = true;
            _state.IsPaused  = false;

            _pollCts = new CancellationTokenSource();
            _ = PollStateAsync(_pollCts.Token);
        }

        public void Pause()
        {
            _waveOut?.Pause();
            _state.IsPlaying = false;
            _state.IsPaused = true;
            NotifyState();
        }

        public void Stop()
        {
            _pollCts?.Cancel();
            // En mode sortie partagée, le WaveOutEvent appartient à la session de
            // lecture entière (cf. UseSharedOutput) — l'arrêter/le libérer ici
            // couperait le son pour toute la playlist, pas seulement cette piste.
            if (_ownsWaveOut)
            {
                _waveOut?.Stop();
                _waveOut?.Dispose();
            }
            _waveOut = null;
            _state.IsPlaying = false;
            _state.IsPaused = false;
            _state.PositionSeconds = 0;
            _state.CurrentOrder = 0;
            _state.CurrentRow = 0;
            _openmptStream?.SampleBuffer.Clear(); // ligne plate dans l'oscilloscope
            NotifyState();
        }

        public void SeekToOrder(int orderIndex)
        {
            if (_openmptStream is not null)
                _openmptStream.SeekToOrder(orderIndex);
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            _state.IsPlaying = false;
            _openmptStream?.SampleBuffer.Clear(); // oscilloscope → ligne plate
            PlaybackFinished?.Invoke(this, EventArgs.Empty);
        }

        private async Task PollStateAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (_openmptStream is not null)
                {
                    _state.PositionSeconds = _openmptStream.PositionSeconds;
                    _state.CurrentOrder    = _openmptStream.CurrentOrder;
                    _state.CurrentPattern  = _openmptStream.CurrentPattern;
                    _state.CurrentRow      = _openmptStream.CurrentRow;
                    _state.CurrentBpm      = _openmptStream.CurrentBpm;
                    _state.CurrentSpeed    = _openmptStream.CurrentSpeed;
                    _state.ChannelVolumes  = _openmptStream.ChannelVolumes;
                    if (_openmptStream.Duration > 0)
                        _state.DurationSeconds = _openmptStream.Duration;
                }
                else
                {
                    // Simulation pour les tests sans libopenmpt
                    _state.PositionSeconds += 0.05;
                    _state.CurrentRow = (int)(_state.PositionSeconds * 4) % 64;
                }
                NotifyState();
                // Poll à 25fps (40ms) — suffisant pour la barre de progression et le pattern viewer.
                // 60fps (16ms) était excessif et consommait inutilement du CPU en lecture continue.
                // 25fps : précision ~40ms sur la position, imperceptible pour l'utilisateur.
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
        }

        private void NotifyState() =>
            StateChanged?.Invoke(this, new ModelsPlaybackState
            {
                IsPlaying = _state.IsPlaying,
                IsPaused = _state.IsPaused,
                PositionSeconds = _state.PositionSeconds,
                DurationSeconds = _state.DurationSeconds,
                CurrentOrder = _state.CurrentOrder,
                CurrentPattern = _state.CurrentPattern,
                CurrentRow = _state.CurrentRow,
                CurrentBpm = _state.CurrentBpm,
                CurrentSpeed = _state.CurrentSpeed,
                ChannelVolumes = _state.ChannelVolumes
            });

        public void Dispose()
        {
            Stop();
            _openmptStream?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Wrapper minimal libopenmpt (P/Invoke)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Wrapping minimal de libopenmpt pour la lecture audio.
    /// libopenmpt supporte nativement MOD, S3M, XM, IT et des centaines d'autres formats.
    ///
    /// Installation : placer openmpt.dll (Windows) / libopenmpt.so (Linux) dans le répertoire
    /// de l'application, ou l'installer via le package NuGet LibOpenMpt.
    ///
    /// Référence API : https://lib.openmpt.org/doc/libopenmpt_c.html
    /// </summary>
    internal sealed class OpenMptStream : IWaveProvider, IDisposable
    {
        private IntPtr _mod = IntPtr.Zero;
        private readonly WaveFormat _waveFormat;
        private bool _available;
        private readonly ILogger? _log;
        private int[] _channelVolumes = Array.Empty<int>();

        /// <summary>Niveaux VU par canal (0-64), mis à jour à chaque Read().</summary>
        public int[] ChannelVolumes => _channelVolumes;

        /// <summary>Buffer circulaire pour l'oscilloscope — rempli à chaque Read().</summary>
        public SampleRingBuffer SampleBuffer { get; } = new SampleRingBuffer(8192);

        // 2026-08-07, demande utilisateur : vue d'ensemble complète de la forme
        // d'onde sous l'oscilloscope, remplie progressivement pendant la lecture
        // pour les formats à synthèse temps réel (dont libopenmpt fait partie).
        // La position utilisée pour ranger chaque bloc dans le bon "bucket" est
        // interrogée à CHAQUE Read() via openmpt_module_get_position_seconds
        // (donc à jour même après un SeekToOrder — pas de compteur manuel à
        // resynchroniser).
        public WaveformOverviewBuffer WaveformOverview { get; } = new WaveformOverviewBuffer();

        // 2026-08-01, retour utilisateur ("quand un format n'est pas jouable, peux tu ,
        // au lieu d'afficher l'oscilloscope 'vide' mettre un message en 'Format non
        // jouable ...'") : true seulement si libopenmpt a effectivement réussi à créer
        // le module depuis ce fichier (_mod != IntPtr.Zero) — false si libopenmpt a
        // REJETÉ le contenu (format non reconnu/non supporté par cette version de la
        // DLL). Dans ce cas, Read() ci-dessous renvoie du silence sans jamais lever
        // d'exception ni logger d'erreur visible — jusqu'ici invisible pour
        // l'utilisateur, qui ne voyait qu'un oscilloscope plat sans explication.
        public bool IsAvailable => _available;

        public double PositionSeconds => _available ? OpenMptInterop.openmpt_module_get_position_seconds(_mod) : 0;
        public int CurrentOrder    => _available ? OpenMptInterop.openmpt_module_get_current_order(_mod) : 0;
        public int CurrentPattern  => _available ? OpenMptInterop.openmpt_module_get_current_pattern(_mod) : 0;
        public int CurrentRow      => _available ? OpenMptInterop.openmpt_module_get_current_row(_mod) : 0;
        public int CurrentBpm      => _available ? OpenMptInterop.openmpt_module_get_current_tempo(_mod) : 125;
        public int CurrentSpeed    => _available ? OpenMptInterop.openmpt_module_get_current_speed(_mod) : 6;
        public double Duration     => _available ? OpenMptInterop.openmpt_module_get_duration_seconds(_mod) : 0;

        /// <summary>
        /// Enrichit un TrackerModule avec les vraies métadonnées lues depuis libopenmpt :
        /// titre, auteur, channels, order list, durée. Appelé juste après le chargement.
        /// </summary>
        public void EnrichModule(TrackerModule module)
        {
            if (!_available || _mod == IntPtr.Zero) return;

            // Titre
            string title = GetMetadata("title");
            if (!string.IsNullOrWhiteSpace(title)) module.Title = title;

            // Auteur
            string artist = GetMetadata("artist");
            if (!string.IsNullOrWhiteSpace(artist)) module.Author = artist;

            // Commentaire
            string msg = GetMetadata("message_raw");
            if (!string.IsNullOrWhiteSpace(msg)) module.Comment = msg;

            // Canaux — ne pas écraser si le décodeur C# (XmDecoder, S3mDecoder…)
            // a déjà positionné la valeur correcte depuis l'en-tête du fichier.
            // openmpt_module_get_num_channels peut retourner le nombre de canaux
            // *effectivement utilisés* plutôt que le nombre total déclaré dans le
            // fichier (ex. 20 au lieu de 64 pour le 303demo2 XM).
            if (module.Channels == 0)
            {
                int ch = OpenMptInterop.openmpt_module_get_num_channels(_mod);
                if (ch > 0) module.Channels = ch;
            }

            // Durée
            double dur = OpenMptInterop.openmpt_module_get_duration_seconds(_mod);
            if (dur > 0) module.DurationSeconds = dur;

            // BPM initial
            int bpm = OpenMptInterop.openmpt_module_get_current_tempo(_mod);
            if (bpm > 0) module.InitialBpm = bpm;

            // 2026-07-31, retour utilisateur ("le nom du format que tu as mis aprés le nom
            // me parait être l'extension, par le format d'origine (ex : Chiptracker, Fast
            // Tracker) etc...") : nom complet du format, TOUJOURS interrogé — pas seulement
            // quand Format==Unknown ci-dessous. Raison : pour un fichier .mod par exemple,
            // FormatDetector.DetectFormat (S3mDecoder.cs) et/ou ModDecoder positionnent déjà
            // Format=MOD sur la seule base de l'extension, AVANT même d'arriver ici — donc
            // le bloc if ci-dessous (qui interroge aussi FormatName) ne s'exécutait jamais
            // pour ces fichiers, et FormatDisplay (SoundtrackPlayerViewModel.cs) retombait
            // sur Format.ToString() = "MOD", qui n'est qu'un code d'extension générique, pas
            // le vrai format d'origine (ChipTracker, TCB Tracker, UNIC Tracker...).
            // Bug additionnel trouvé au passage : la clé demandée était "type_name", qui
            // N'EST PAS une clé valide de openmpt_module_get_metadata (clés documentées :
            // "type"/"type_long"/"originaltype"/"originaltype_long"/"container"/
            // "container_long"/"tracker"/"artist"/"title"/"date"/"message"/"message_raw"/
            // "warnings", cf. libopenmpt.h) — "type_long" est la bonne clé pour un nom
            // complet type "Impulse Tracker"/"ChipTracker" (par opposition à "type", qui
            // renvoie le code court type extension, ex. "it"/"mod"). Ce champ était donc
            // TOUJOURS vide avant ce correctif, même dans les cas où le bloc s'exécutait.
            string typeLongStr = GetMetadata("type_long"); // "ChipTracker", "Impulse Tracker"...
            if (!string.IsNullOrWhiteSpace(typeLongStr) && string.IsNullOrWhiteSpace(module.FormatName))
                module.FormatName = typeLongStr;

            // Détection du format depuis libopenmpt si pas encore identifié
            // (utile pour STM, DBM et autres formats sans décodeur C# natif)
            if (module.Format == TrackerFormat.Unknown)
            {
                string typeStr = GetMetadata("type");          // "stm", "dbm", "it", ...
                module.Format = typeStr.ToLowerInvariant() switch
                {
                    "stm"  => TrackerFormat.STM,
                    "dbm"  => TrackerFormat.DBM,
                    "mod"  => TrackerFormat.MOD,
                    "s3m"  => TrackerFormat.S3M,
                    "xm"   => TrackerFormat.XM,
                    "it"   => TrackerFormat.IT,
                    // 2026-07-30, retour utilisateur : ".ult (ultracker) affichent
                    // les pattern mais il faut la vue FT2" — module.Format restait
                    // Unknown faute d'entrée ici (libopenmpt seul gère .ult, pas de
                    // décodeur C# dédié), donc TrackerStyle retombait sur ProTracker
                    // par défaut (cf. SoundtrackPlayerViewModel).
                    "ult"  => TrackerFormat.ULT,
                    // 2026-07-30, retour utilisateur (Astroidea XMF, .xmf) : valeur
                    // dédiée plutôt que Unknown, cf. TrackerModels.cs.
                    "xmf"  => TrackerFormat.XMF,
                    // 2026-07-30, retour utilisateur : ".amf/.667/.669/.digi - à
                    // ouvrir avec libopenmpt et les patterns FT2".
                    "amf"  => TrackerFormat.AMF,
                    "669"  => TrackerFormat.Composer669, // couvre .669 et .667
                    "digi" => TrackerFormat.DIGI,
                    // 2026-07-30, retour utilisateur : ".dsm/.dtm/.mdl - à ouvrir
                    // avec libopenmpt et les patterns FT2".
                    "dsm"  => TrackerFormat.DSM,
                    "dtm"  => TrackerFormat.DTM,
                    "mdl"  => TrackerFormat.MDL,
                    // 2026-07-30, retour utilisateur : ".dmf/.ams - FT2".
                    "dmf"  => TrackerFormat.DMF,
                    "ams"  => TrackerFormat.AMS,
                    // 2026-07-30, retour utilisateur : ".psm - à ouvrir avec
                    // libopenmpt et les patterns FT2". libopenmpt renvoie "psm"
                    // (Epic MegaGames MASI) ou "psm16" (ancienne variante) selon
                    // le fichier — les deux couverts.
                    "psm"  => TrackerFormat.PSM,
                    "psm16" => TrackerFormat.PSM,
                    // 2026-07-30, retour utilisateur : ".gtk/.gt2 - FT2" (Graoumf
                    // Tracker, Atari ST). libopenmpt distingue les deux versions
                    // par un type string différent ("gtk"/"gt2") mais même style
                    // d'affichage — un seul TrackerFormat pour les deux.
                    "gtk"  => TrackerFormat.GraoumfTracker,
                    "gt2"  => TrackerFormat.GraoumfTracker,
                    // 2026-07-30, retour utilisateur : ".mt2 - FT2" (MadTracker 2).
                    "mt2"  => TrackerFormat.MT2,
                    // 2026-07-31, retour utilisateur : ".stp - FT2" (Soundtracker
                    // Pro II, Atari Falcon — pas le format ZX Spectrum du même nom).
                    "stp"  => TrackerFormat.STP,
                    _      => TrackerFormat.Unknown
                };
                _log?.LogDebug("EnrichModule: type='{T}' → Format={F}", typeStr, module.Format);
            }

            // Order list (si le décodeur C# n'a rien produit)
            int orders = OpenMptInterop.openmpt_module_get_num_orders(_mod);
            if (orders > 0 && module.OrderList.Count == 0)
            {
                for (int i = 0; i < orders; i++)
                    module.OrderList.Add(OpenMptInterop.openmpt_module_get_order_pattern(_mod, i));
            }

            // Patterns : utilise les données du décodeur C# (MOD/S3M/XM)
            // On ne touche aux patterns QUE si le décodeur n'en a produit aucun.
            int numPat = OpenMptInterop.openmpt_module_get_num_patterns(_mod);
            _log?.LogDebug("EnrichModule: module.Patterns.Count={Count}, libopenmpt numPat={NumPat}",
                module.Patterns.Count, numPat);

            if (numPat > 0 && module.Patterns.Count == 0)
            {
                // Pas de décodeur C# pour ce format → libopenmpt remplit les cellules
                _log?.LogDebug("EnrichModule: remplissage via libopenmpt ({N} patterns)", numPat);
                int numCh = module.Channels > 0 ? module.Channels : 4;
                for (int p = 0; p < numPat; p++)
                {
                    int rows = OpenMptInterop.openmpt_module_get_pattern_num_rows(_mod, p);
                    if (rows <= 0) rows = 64;
                    var pattern = new TrackerPattern(p, rows, numCh);

                    for (int row = 0; row < rows; row++)
                        for (int col = 0; col < numCh; col++)
                        {
                            int note   = OpenMptInterop.openmpt_module_get_pattern_row_channel_command(_mod, p, row, col, 0);
                            int instr  = OpenMptInterop.openmpt_module_get_pattern_row_channel_command(_mod, p, row, col, 1);
                            int volFx  = OpenMptInterop.openmpt_module_get_pattern_row_channel_command(_mod, p, row, col, 2);
                            int vol    = OpenMptInterop.openmpt_module_get_pattern_row_channel_command(_mod, p, row, col, 3);
                            int effect = OpenMptInterop.openmpt_module_get_pattern_row_channel_command(_mod, p, row, col, 4);
                            int effPrm = OpenMptInterop.openmpt_module_get_pattern_row_channel_command(_mod, p, row, col, 5);

                            pattern.Cells[row, col] = new PatternCell
                            {
                                Note        = note is >= 1 and <= 120 ? note : 0,
                                Instrument  = instr,
                                Volume      = volFx > 0 ? vol : -1,
                                Effect      = effect,
                                EffectParam = effPrm
                            };
                        }

                    module.Patterns.Add(pattern);
                }
            }
            else if (module.Patterns.Count > 0)
            {
                _log?.LogDebug("EnrichModule: {Count} patterns du décodeur C# préservés", module.Patterns.Count);
            }

            // Samples — 2026-07-31, retour utilisateur ("infos sur les instruments (nom ?
            // taille ?)") : l'ancien code appelait GetMetadata($"sample_name{s}"), qui n'est
            // PAS une clé valide pour openmpt_module_get_metadata (jeu fixe de clés
            // génériques uniquement : title/artist/type/message_raw/...) — ce nom était donc
            // TOUJOURS vide en pratique. Le nom d'un sample se récupère via la fonction
            // dédiée openmpt_module_get_sample_name(mod, index), pas via get_metadata.
            // Taille (octets/frames) non exposée par l'API C de libopenmpt — Length reste à
            // 0 comme avant ce correctif, seul le nom change.
            int numSmp = OpenMptInterop.openmpt_module_get_num_samples(_mod);
            if (numSmp > 0 && module.Samples.Count == 0)
            {
                for (int s = 0; s < numSmp; s++)
                {
                    string sName = GetOpenMptString(
                        OpenMptInterop.openmpt_module_get_sample_name(_mod, s));
                    module.Samples.Add(new TrackerSample { Index = s, Name = sName });
                }
            }

            // Instruments — couche XM/IT au-dessus des samples (pas tous les formats en
            // ont ; num_instruments vaut 0 pour MOD/S3M par exemple, auquel cas la liste
            // reste vide et l'UI doit se rabattre sur Samples).
            int numInstr = OpenMptInterop.openmpt_module_get_num_instruments(_mod);
            if (numInstr > 0 && module.Instruments.Count == 0)
            {
                for (int i = 0; i < numInstr; i++)
                {
                    string iName = GetOpenMptString(
                        OpenMptInterop.openmpt_module_get_instrument_name(_mod, i));
                    module.Instruments.Add(new TrackerInstrument { Index = i, Name = iName });
                }
            }
        }

        /// <summary>Libère et convertit une chaîne UTF-8 renvoyée par libopenmpt
        /// (get_instrument_name, get_sample_name…) — distinct de <see cref="GetMetadata"/>
        /// qui utilise PtrToStringAnsi de longue date pour title/artist/message (comportement
        /// existant non modifié ici, hors périmètre de ce correctif). Toutes les chaînes
        /// libopenmpt sont documentées UTF-8 (libopenmpt.h, section "Strings") —
        /// PtrToStringUTF8 est le convertisseur correct pour préserver les caractères
        /// accentués dans les noms d'instruments/samples.</summary>
        private string GetOpenMptString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            string? result = Marshal.PtrToStringUTF8(ptr);
            OpenMptInterop.openmpt_free_string(ptr);
            return result ?? string.Empty;
        }

        /// <summary>
        /// Parse la string brute libopenmpt en PatternCell structurée.
        /// La string a un format variable selon le format source :
        ///   MOD : "G#3 02 964"     → note instr effet
        ///   XM  : "G#3 02 v40 F64" → note instr vol effet
        ///   IT  : "G#3 02 40 F64"  → note instr vol effet
        private string GetMetadata(string key)
        {
            IntPtr ptr = OpenMptInterop.openmpt_module_get_metadata(_mod, key);
            if (ptr == IntPtr.Zero) return string.Empty;
            string? result = Marshal.PtrToStringAnsi(ptr);
            OpenMptInterop.openmpt_free_string(ptr);
            return result ?? string.Empty;
        }

        public WaveFormat WaveFormat => _waveFormat;

        public OpenMptStream(string filePath, int channels, ILogger? log = null)
        {
            _log = log;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            if (!OpenMptInterop.IsAvailable) return;

            try
            {
                byte[] data = File.ReadAllBytes(filePath);
                _mod = OpenMptInterop.openmpt_module_create_from_memory2(
                    data,
                    (UIntPtr)data.Length,
                    IntPtr.Zero,   // logfunc  — NULL
                    IntPtr.Zero,   // loguser  — NULL
                    IntPtr.Zero,   // errfunc  — NULL
                    IntPtr.Zero,   // erruser  — NULL
                    IntPtr.Zero,   // error*   — NULL
                    IntPtr.Zero,   // error_message** — NULL
                    IntPtr.Zero);  // ctls     — NULL
                _available = _mod != IntPtr.Zero;
                if (_available)
                {
                    int numCh = OpenMptInterop.openmpt_module_get_num_channels(_mod);
                    _channelVolumes = new int[numCh];

                    // 2026-08-07 : dimensionne la vue d'ensemble dès que la durée
                    // est connue (disponible immédiatement, avant toute lecture).
                    double dur = OpenMptInterop.openmpt_module_get_duration_seconds(_mod);
                    WaveformOverview.SetDuration(dur > 0 ? dur : 300, 48000);
                }
            }
            catch (Exception ex)
            {
                _available = false;
                // Log silencieux — la lecture continue sans audio
                _ = ex;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!_available) return count; // silence

            int framesToRead = count / (2 * 4); // stéréo float = 8 octets/frame

            // ArrayPool : réutiliser les buffers — NAudio appelle Read() ~13-20×/sec,
            // élimine ~1 MB/sec d'allocations float[] + les 14 400 BitConverter.GetBytes/sec.
            var left  = System.Buffers.ArrayPool<float>.Shared.Rent(framesToRead);
            var right = System.Buffers.ArrayPool<float>.Shared.Rent(framesToRead);
            try
            {
                // Position AVANT ce bloc — sert à ranger les samples dans le bon
                // bucket de la vue d'ensemble ; interrogée ici (pas après) car
                // openmpt_module_read_float_stereo() ci-dessous avance la position.
                double posStartSec = OpenMptInterop.openmpt_module_get_position_seconds(_mod);

                int framesRead = (int)OpenMptInterop.openmpt_module_read_float_stereo(
                    _mod, 48000, (UIntPtr)framesToRead, left, right);

                if (framesRead > 0)
                {
                    SampleBuffer.Write(left, right, framesRead);
                    WaveformOverview.WriteAt((long)(posStartSec * 48000), left, right, framesRead);

                    for (int ch = 0; ch < _channelVolumes.Length; ch++)
                    {
                        float vuL = OpenMptInterop.openmpt_module_get_current_channel_vu_left(_mod, ch);
                        float vuR = OpenMptInterop.openmpt_module_get_current_channel_vu_right(_mod, ch);
                        _channelVolumes[ch] = (int)(Math.Max(vuL, vuR) * 64f);
                    }
                }

                // Interleave L/R → buffer via MemoryMarshal (zéro allocation)
                // MemoryMarshal.Cast réinterprète byte[] en float[] en place
                var outFloats = System.Runtime.InteropServices.MemoryMarshal
                    .Cast<byte, float>(buffer.AsSpan(offset, framesRead * 8));
                for (int i = 0; i < framesRead; i++)
                {
                    outFloats[i * 2]     = left[i];
                    outFloats[i * 2 + 1] = right[i];
                }

                return framesRead * 8;
            }
            finally
            {
                System.Buffers.ArrayPool<float>.Shared.Return(left);
                System.Buffers.ArrayPool<float>.Shared.Return(right);
            }
        }

        public void SeekToOrder(int order)
        {
            if (_available)
                OpenMptInterop.openmpt_module_set_position_order_row(_mod, (UIntPtr)order, UIntPtr.Zero);
        }

        public void Dispose()
        {
            if (_available && _mod != IntPtr.Zero)
                OpenMptInterop.openmpt_module_destroy(_mod);
            _mod = IntPtr.Zero;
        }
    }

    /// <summary>P/Invoke vers libopenmpt (libopenmpt.dll sur Windows).</summary>
    internal static class OpenMptInterop
    {
        // Le binaire officiel Windows s'appelle "libopenmpt.dll"
        private const string DllName = "libopenmpt";

        public static bool IsAvailable
        {
            get
            {
                try { openmpt_get_library_version(); return true; }
                catch { return false; }
            }
        }

        /// <summary>
        /// 2026-07-31, retour utilisateur : "openmpt convertit les fichiers selon certains
        /// critères (ex: chipytracker, futur composer etc...). visiblement la librairie
        /// openmpt ne gère pas cette conversion [...] peux tu vérifier que c'est bien le
        /// cas ?" — Vérifié (changelog officiel libopenmpt, lib.openmpt.org) : le support de
        /// lecture de ChipTracker (variante .mod) ET de Future Composer (.fc/.fc13/.fc14/
        /// .smod) — ainsi que PumaTracker, Face The Music, Game Music Creator, TCB Tracker,
        /// Real Tracker 2, Images Music System, Chuck Biscuits/Black Artist — n'a été ajouté
        /// QUE dans libopenmpt 0.8.0 (31 mai 2025). Avant cette version, la librairie ne
        /// pouvait effectivement PAS lire ces formats, quelle que soit la version de l'appli
        /// OpenMPT elle-même (qui embarque en général la libopenmpt la plus récente au moment
        /// de sa sortie — d'où l'écart observé "OpenMPT l'ouvre, DemoBase non").
        /// Impossible de vérifier ICI quelle version de libopenmpt.dll est réellement présente
        /// dans le dossier Externals/ de l'utilisateur (le binaire n'est pas dans les sources) —
        /// exposé ici pour que l'appli elle-même le révèle (loggué au démarrage, cf.
        /// App.xaml.cs/ConfigureExternalPaths) plutôt que de deviner.
        /// </summary>
        public static string LibraryVersionString
        {
            get
            {
                try
                {
                    uint v = openmpt_get_library_version();
                    return $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{v & 0xFFFF}";
                }
                catch { return "?"; }
            }
        }

        /// <summary>Vrai si la version chargée est antérieure à 0.8.0 — cf.
        /// <see cref="LibraryVersionString"/> pour le détail des formats concernés
        /// (ChipTracker, Future Composer, PumaTracker, Face The Music, Game Music Creator,
        /// TCB Tracker, Real Tracker 2, Images Music System, Chuck Biscuits/Black Artist).</summary>
        public static bool IsBefore_0_8_0
        {
            get
            {
                try
                {
                    uint v = openmpt_get_library_version();
                    return v < ((0u << 24) | (8u << 16) | 0u);
                }
                catch { return true; } // version indéterminée → prudence
            }
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint openmpt_get_library_version();

        /// <summary>
        /// Crée un module depuis un buffer mémoire.
        /// Signature C : openmpt_module* openmpt_module_create_from_memory2(
        ///     const void* filedata, size_t filesize,
        ///     openmpt_log_func logfunc, void* loguser,
        ///     openmpt_error_func errfunc, void* erruser,
        ///     int* error, const char** error_message,
        ///     const openmpt_module_initial_ctl* ctls)
        /// On passe IntPtr.Zero pour tous les callbacks/ctls optionnels.
        /// error et error_message sont aussi optionnels (on passe IntPtr.Zero).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr openmpt_module_create_from_memory2(
            byte[]   filedata,
            UIntPtr  filesize,
            IntPtr   logfunc,       // openmpt_log_func  — NULL = log par défaut
            IntPtr   loguser,       // void*             — NULL
            IntPtr   errfunc,       // openmpt_error_func — NULL = comportement par défaut
            IntPtr   erruser,       // void*             — NULL
            IntPtr   error,         // int*              — NULL (on ignore le code d'erreur)
            IntPtr   error_message, // const char**      — NULL (on ignore le message)
            IntPtr   ctls           // openmpt_module_initial_ctl* — NULL
        );

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void openmpt_module_destroy(IntPtr mod);

        /// <summary>
        /// Lit count frames en float stéréo interleaved (L puis R).
        /// Retourne le nombre de frames effectivement lus (0 = fin du module).
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr openmpt_module_read_float_stereo(
            IntPtr  mod,
            int     samplerate,
            UIntPtr count,
            float[] left,
            float[] right);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double openmpt_module_get_position_seconds(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double openmpt_module_set_position_order_row(
            IntPtr  mod,
            UIntPtr order,
            UIntPtr row);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double openmpt_module_get_duration_seconds(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_current_order(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_current_pattern(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_current_row(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_current_tempo(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_current_speed(IntPtr mod);

        /// <summary>Retourne une chaîne de métadonnée (title, artist, message…). À libérer avec openmpt_free_string.</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr openmpt_module_get_metadata(IntPtr mod,
            [MarshalAs(UnmanagedType.LPStr)] string key);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void openmpt_free_string(IntPtr str);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_num_channels(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern float openmpt_module_get_current_channel_vu_left(IntPtr mod, int channel);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern float openmpt_module_get_current_channel_vu_right(IntPtr mod, int channel);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_num_orders(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_num_patterns(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_order_pattern(IntPtr mod, int order);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_pattern_num_rows(IntPtr mod, int pattern);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_num_samples(IntPtr mod);

        // 2026-07-31, retour utilisateur ("est-ce que tu peux recuperer d'autres infos via
        // libopenmpt ? [...] infos sur les instruments (nom ? taille ?)") : le nombre de
        // samples était déjà exposé (openmpt_module_get_num_samples ci-dessus) mais pas le
        // nombre d'instruments, ni le nom des uns ou des autres (le code existant appelait à
        // tort GetMetadata("sample_nameN"), qui n'est pas une clé valide — cf. EnrichModule).
        // Vérifié dans libopenmpt.h (buildbot.openmpt.org / github OpenMPT/openmpt) : la
        // taille d'un sample/instrument (en octets ou en frames) N'EST PAS exposée par l'API
        // C publique — seuls les noms le sont via ces deux fonctions dédiées (à ne pas
        // confondre avec openmpt_module_get_metadata, qui ne gère qu'un jeu fixe de clés
        // génériques comme "title"/"artist"/"message_raw").
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_num_instruments(IntPtr mod);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr openmpt_module_get_instrument_name(IntPtr mod, int index);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr openmpt_module_get_sample_name(IntPtr mod, int index);

        /// <summary>
        /// Lit une commande dans une cellule de pattern.
        /// command : 0=note 1=instrument 2=volumeeffect 3=volume 4=effect 5=effectparam
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int openmpt_module_get_pattern_row_channel_command(
            IntPtr mod, int pattern, int row, int channel, int command);
    }

    /// <summary>Provider WAV silencieux pour les tests sans libopenmpt.</summary>
    internal sealed class SilenceWaveProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; }
        public SilenceWaveProvider(int sampleRate = 44100, int channels = 2)
            => WaveFormat = new WaveFormat(sampleRate, 16, channels);
        public int Read(byte[] buffer, int offset, int count)
        { Array.Clear(buffer, offset, count); return count; }
    }

    // ════════════════════════════════════════════════════════════════════════
    // TrackerService — point d'entrée principal
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lecteur NAudio direct pour les formats audio courants (WAV, MP3, OGG, FLAC...) — sans
    /// passer par ZXTune ou libopenmpt. Utilisé quand le fichier à jouer est un fichier audio
    /// standard déjà encodé (ex. release music dont le zip contient un .wav ou .mp3 final),
    /// pas un format tracker exotique à décoder/convertir.
    /// </summary>
    public sealed class NativeAudioPlayer : ITrackerPlayer
    {
        public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aiff", ".aif",
        };

        private readonly ILogger _log;
        private WaveOutEvent?    _waveOut;
        private WaveStream?      _reader;
        private TrackerModule?   _module;
        private float            _masterVolume = 1.0f;
        private ModelsPlaybackState _state = new();

        // ── ITrackerPlayer ────────────────────────────────────────────────────
        public TrackerFormat[]                       SupportedFormats => [];
        public event EventHandler<ModelsPlaybackState>? StateChanged;
        public event EventHandler?                   PlaybackFinished;
        public ModelsPlaybackState                   CurrentState => _state;
        public SampleRingBuffer                      SampleBuffer { get; } = new(8192);

        // 2026-08-07, demande utilisateur : vue d'ensemble complète de la forme
        // d'onde sous l'oscilloscope. Contrairement aux formats à synthèse temps
        // réel (libopenmpt/UADE/ZXTune/SNDH), un fichier audio ici est déjà entier
        // sur disque — pas besoin d'attendre la lecture : un décodage COMPLET est
        // lancé une fois en arrière-plan (Task.Run, cf. LoadAsync) via un lecteur
        // NAudio indépendant du lecteur de lecture, pour ne jamais gêner celle-ci.
        public WaveformOverviewBuffer WaveformOverview { get; } = new WaveformOverviewBuffer();
        private CancellationTokenSource? _overviewCts;

        public int    SubsongCount        => 1;
        public int    CurrentSubsongIndex => 0;
        public void   SelectSubsong(int index) { /* non applicable — un seul flux audio */ }
        public float  MasterVolume
        {
            get => _masterVolume;
            set { _masterVolume = value; if (_waveOut != null) _waveOut.Volume = value; }
        }

        public NativeAudioPlayer(ILogger log) => _log = log;

        public Task LoadAsync(TrackerModule module, CancellationToken ct = default)
        {
            _module = module;
            var ext = Path.GetExtension(module.FilePath).ToLowerInvariant();
            try
            {
                // WaveFileReader et Mp3FileReader sont dans NAudio.Wave (déjà référencé).
                // VorbisWaveReader (NAudio.Vorbis) gère OGG : WMF ne supporte pas Vorbis nativement.
                // AudioFileReader gère les autres formats via les codecs MediaFoundation Windows.
                _reader = ext switch
                {
                    ".wav"  => new WaveFileReader(module.FilePath),
                    ".mp3"  => new Mp3FileReader(module.FilePath),
                    ".ogg"  => new NAudio.Vorbis.VorbisWaveReader(module.FilePath),
                    _       => new AudioFileReader(module.FilePath),
                };

                // Alimenter la durée du module depuis le fichier audio
                double totalSec = _reader.TotalTime.TotalSeconds;
                if (totalSec > 0)
                {
                    module.DurationSeconds = totalSec;
                    _state.DurationSeconds = totalSec;
                }

                // 2026-08-07 : lance le décodage complet en arrière-plan pour la vue
                // d'ensemble — annule un éventuel décodage précédent encore en cours
                // (changement rapide de piste) ; passe par un DEUXIÈME reader créé
                // dans la tâche elle-même (pas _reader, réservé à la lecture live).
                _overviewCts?.Cancel();
                _overviewCts = new CancellationTokenSource();
                var overviewCt = _overviewCts.Token;
                WaveformOverview.SetDuration(totalSec > 0 ? totalSec : 1, _reader.WaveFormat.SampleRate);
                string filePath = module.FilePath;
                _ = Task.Run(() => DecodeFullFileForOverview(filePath, ext, overviewCt), overviewCt);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "NativeAudioPlayer: impossible d'ouvrir '{File}'", module.FilePath);
                throw;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Décode le fichier ENTIER une seule fois, en arrière-plan, pour remplir la
        /// vue d'ensemble (WaveformOverview) — via un reader NAudio indépendant de
        /// celui utilisé pour la lecture (_reader), donc sans jamais la perturber.
        /// Best-effort : toute erreur ici reste invisible pour l'utilisateur (la
        /// vue d'ensemble reste simplement incomplète/vide, la lecture n'est pas
        /// affectée).
        /// </summary>
        private void DecodeFullFileForOverview(string filePath, string ext, CancellationToken ct)
        {
            try
            {
                using WaveStream reader = ext switch
                {
                    ".wav"  => new WaveFileReader(filePath),
                    ".mp3"  => new Mp3FileReader(filePath),
                    ".ogg"  => new NAudio.Vorbis.VorbisWaveReader(filePath),
                    _       => new AudioFileReader(filePath),
                };
                var sampleProvider = reader.ToSampleProvider();
                int channels = Math.Max(1, sampleProvider.WaveFormat.Channels);
                const int chunkFrames = 8192;
                var scratch = new float[chunkFrames * channels];
                long framePos = 0;
                int read;
                while (!ct.IsCancellationRequested &&
                       (read = sampleProvider.Read(scratch, 0, scratch.Length)) > 0)
                {
                    int frames = read / channels;
                    var left  = new float[frames];
                    var right = new float[frames];
                    for (int i = 0; i < frames; i++)
                    {
                        left[i]  = scratch[i * channels];
                        right[i] = channels > 1 ? scratch[i * channels + 1] : left[i];
                    }
                    WaveformOverview.WriteAt(framePos, left, right, frames);
                    framePos += frames;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex,
                    "NativeAudioPlayer: décodage arrière-plan de la vue d'ensemble échoué pour '{File}' (best-effort, sans impact sur la lecture).",
                    filePath);
            }
        }

        public void Play()
        {
            if (_reader == null) return;
            try { _reader.Position = 0; } catch { /* seek pas toujours supporté */ }
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = new WaveOutEvent { DesiredLatency = 150 };
            _waveOut.Volume = _masterVolume;

            var sampler = new SamplingSampleProvider(_reader.ToSampleProvider(), SampleBuffer);
            _waveOut.Init(sampler);
            _waveOut.PlaybackStopped += (_, _) =>
            {
                _state.IsPlaying = false;
                SampleBuffer.Clear();
                PlaybackFinished?.Invoke(this, EventArgs.Empty);
            };
            _waveOut.Play();
            _state.IsPlaying = true;
            _state.IsPaused  = false;

            // Démarrer la boucle de position
            _pollCts?.Cancel();
            _pollCts = new System.Threading.CancellationTokenSource();
            _ = PollPositionAsync(_pollCts.Token);
        }

        private System.Threading.CancellationTokenSource? _pollCts;

        private async Task PollPositionAsync(System.Threading.CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _state.IsPlaying)
            {
                if (_reader != null)
                {
                    try { _state.PositionSeconds = _reader.CurrentTime.TotalSeconds; }
                    catch { }
                }
                StateChanged?.Invoke(this, _state);
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
        }

        public void Pause()
        {
            _waveOut?.Pause();
            _state.IsPlaying = false;
            _state.IsPaused  = true;
        }

        public void Resume()
        {
            _waveOut?.Play();
            _state.IsPlaying = true;
            _state.IsPaused  = false;
        }

        public void Stop()
        {
            _pollCts?.Cancel();
            _waveOut?.Stop();
            _state.IsPlaying = false;
            _state.IsPaused  = false;
            SampleBuffer.Clear();
        }

        public void SeekTo(TimeSpan position) { /* non supporté uniformément */ }
        public void SeekToOrder(int orderIndex) { }

        public void Dispose()
        {
            _pollCts?.Cancel();
            _overviewCts?.Cancel();
            _waveOut?.Dispose();
            _reader?.Dispose();
        }
    }

    /// <summary>
    /// Intercepte chaque <c>Read()</c> d'un <see cref="ISampleProvider"/> pour alimenter
    /// un <see cref="SampleRingBuffer"/> — permet à l'oscilloscope de voir les samples de
    /// <see cref="NativeAudioPlayer"/>.
    ///
    /// Utilise <see cref="ISampleProvider"/> plutôt qu'<see cref="IWaveProvider"/> pour éviter
    /// toute manipulation byte-level fragile : NAudio expose toujours des <c>float[]</c>
    /// interleaved (L0, R0, L1, R1…) via <see cref="WaveExtensionMethods.ToSampleProvider"/>,
    /// quelle que soit la profondeur d'origine (16-bit, 24-bit, float32, mono, stéréo…).
    /// <see cref="WaveOutEvent"/> accepte <see cref="ISampleProvider"/> directement — pas
    /// besoin de repasser par <see cref="IWaveProvider"/>.
    /// </summary>
    internal sealed class SamplingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider  _inner;
        private readonly SampleRingBuffer _buffer;

        public WaveFormat WaveFormat => _inner.WaveFormat;

        public SamplingSampleProvider(ISampleProvider inner, SampleRingBuffer buffer)
        {
            _inner  = inner;
            _buffer = buffer;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (read <= 0) return read;

            int ch     = WaveFormat.Channels;
            int frames = read / ch;

            // ArrayPool : évite 2 allocations float[] par appel NAudio
            var left  = System.Buffers.ArrayPool<float>.Shared.Rent(frames);
            var right = System.Buffers.ArrayPool<float>.Shared.Rent(frames);
            try
            {
                for (int i = 0; i < frames; i++)
                {
                    left[i]  = buffer[offset + i * ch];
                    right[i] = ch > 1 ? buffer[offset + i * ch + 1] : left[i];
                }
                _buffer.Write(left, right, frames);
            }
            finally
            {
                System.Buffers.ArrayPool<float>.Shared.Return(left);
                System.Buffers.ArrayPool<float>.Shared.Return(right);
            }
            return read;
        }
    }

    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Service de haut niveau.
    /// Enregistre les décodeurs et players disponibles,
    /// détecte automatiquement le format et orchestre le chargement.
    /// </summary>
    public sealed class TrackerService : ITrackerService
    {
        /// <summary>
        /// Extensions gérées exclusivement par libopenmpt — jamais redirigées vers UADE/ZXTune.
        /// Inclut tous les formats tracker connus de libopenmpt.
        /// </summary>
        private static readonly HashSet<string> LibopenmptExtensions =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // Formats avec décodeur C# natif
            ".mod", ".nst", ".wow", ".stk",  // ProTracker (.stk = startup format, même structure)
            ".xm",
            ".s3m",
            // Formats sans décodeur C# mais gérés par libopenmpt
            ".it",
            ".mptm",
            ".stm", ".st2",  // ScreamTracker 2
            ".dbm", ".dmf", // DigiBooster Pro / DigiBooster Module
            ".ult",
            // 2026-07-30, retour utilisateur ("uade prend le dessus sur les .xmf") :
            // .xmf n'était dans AUCUNE liste explicite (ni UADE, ni ZXTune, ni ici),
            // donc il tombait dans le fallback 2 (détection par contenu) où
            // UadeDecoder.CanDecode() renvoie toujours true et gagnait la main avant
            // même que ZXTuneDecoder soit essayé. L'ajouter ici court-circuite les
            // fallbacks UADE/ZXTune (basés sur !LibopenmptExtensions.Contains(ext))
            // et garantit un routage déterministe vers libopenmpt, comme .dbm/.ult.
            ".xmf",
            // 2026-07-30, retour utilisateur ("à ouvrir avec libopenmpt et les
            // patterns FT2") : .667 est une variante de nommage de .669
            // (Composer 669 / UNIS 669) rencontrée sur Modland — même moteur
            // libopenmpt, même type de module ("669").
            ".667",
            ".669",
            // 2026-07-30, retour utilisateur (".psm - à ouvrir avec libopenmpt et
            // les patterns FT2") : format PC "PSM" (Epic MegaGames MASI), distinct
            // du ".psm" ZX Spectrum (ProSoundMaker) qui était listé dans
            // ZXTunePlayer.SupportedExtensions (ExternalPlayers.cs) — vrai conflit
            // d'extension, retiré de ZXTune au profit de libopenmpt ici (même
            // schéma que .digi).
            ".psm",
            // 2026-07-30, retour utilisateur ("idem" = à ouvrir avec libopenmpt et
            // les patterns FT2) : Graoumf Tracker (Atari ST) — .gtk (v1) et .gt2
            // (v2), aucun conflit détecté avec UADE/ZXTune (formats absents de
            // leurs listes SupportedExtensions/KnownPrefixes).
            ".gtk",
            ".gt2",
            // 2026-07-30, retour utilisateur ("un dernier rajout :P — .mt2 - à
            // ouvrir avec libopenmpt et les patterns FT2") : MadTracker 2 (PC),
            // aucun conflit détecté avec UADE/ZXTune.
            ".mt2",
            ".far",
            ".mtm",
            ".amf",
            ".dsm",
            ".gdm",
            ".imf",
            ".ptm",
            ".mdl",
            ".ams",
            ".digi",
            ".dbm",
            // 2026-07-31, retour utilisateur ("les fichiers .mmd0, .mmd1, .mmd2, .mmd3
            // et .okta doivent passer par libopenmpt avec une vue ft2") : le commentaire
            // précédent ("libopenmpt ET zxtune/uade, libopenmpt prioritaire") était erroné
            // — ZXTuneDecoder.CanDecode() renvoie toujours true (cf. ExternalPlayers.cs,
            // "on fait confiance à l'extension"), donc tant que .okt/.okta/.med restaient
            // listés dans ZXTunePlayer.SupportedExtensions, ZXTuneDecoder gagnait
            // systématiquement dès la 1ère boucle de TrackerService.OpenAsync (avant même
            // que la présence dans LibopenmptExtensions ici ne soit consultée — ce set ne
            // sert qu'à restreindre les fallbacks 1b/2, pas cette 1ère boucle). Retirés de
            // ZXTunePlayer.SupportedExtensions à cette occasion : libopenmpt est
            // maintenant le SEUL backend pour ces trois extensions.
            ".okt", ".okta",   // Oktalyzer
            ".med",             // OctaMED
            // MMD0-3 : mêmes variantes de conteneur OctaMED que ".med" ci-dessus,
            // absentes jusqu'ici de toute liste (ni ZXTune, ni UADE, ni libopenmpt).
            ".mmd0", ".mmd1", ".mmd2", ".mmd3",
            ".dtm",
            // 2026-07-31, retour utilisateur ("il faut ouvrir les fichiers .stp avec
            // visu ft2") : format PC/Atari Falcon "Soundtracker Pro II", distinct du
            // ".stp" ZX Spectrum ("SoundTracker compiled") retiré de
            // ZXTunePlayer.SupportedExtensions (ExternalPlayers.cs) — même conflit
            // d'extension déjà rencontré pour .psm, même remède.
            ".stp",
            ".mo3",
            ".xpk",
            ".ppm",
            ".mmcmp",
            // Atari ST — géré par SndhPlayer (DLL dédiée), jamais par UADE
            // ni par la détection de contenu générique ci-dessous
            ".sndh",

            // 2026-07-31, retour utilisateur : "généralise-le au format que libopenmpt
            // peut lire" — liste complète fournie par l'utilisateur (page des formats
            // supportés par libopenmpt/OpenMPT). Formats déjà présents ci-dessus non
            // reprogrammés ; uniquement les extensions manquantes ou EN CONFLIT avec
            // UADE/ZXTune (retirées de leurs listes respectives, cf.
            // ExternalPlayers.cs — même remède que .psm/.gtk/.stp).
            ".c67",              // Composer 670 / CDFM
            ".cba",              // Chuck Biscuits / Black Artist
            ".dsym",             // Digital Symphony
            ".etx",              // EasyTrax
            // Future Composer — retiré d'UadePlayer.SupportedExtensions (conflit réel,
            // UADE ne produit jamais de vrais patterns, cf. UadeDecoder.DecodeAsync).
            ".fc", ".fc13", ".fc14", ".smod",
            ".fmt",              // Davey W. Taylor's FM Tracker
            ".ftm",              // Face The Music
            ".gmc",              // Game Music Creator
            // Ice Tracker / SoundTracker 2.6 — extensions DÉDIÉES libopenmpt, distinctes
            // du cas ".mod" générique déjà géré par le décodeur maison (cf. ModDecoder.cs,
            // DetectVariant/Untagged31 — fichier réel "doitnow.mod" du 2026-07-31).
            ".ice", ".st26",
            ".ims",              // Images Music System — retiré d'UadePlayer (conflit réel)
            ".itp",              // Impulse Tracker Project
            ".j2b",              // Jazz Jackrabbit 2 Music
            ".m15",              // SoundTracker M15 (variante nommage, cf. ".stk" déjà listé)
            ".mus",              // Psycho Pinball / Micro Machines 2
            ".oxm",              // OggMod XM
            ".plm",              // Disorder Tracker 2
            ".pt36",             // ProTracker 3.6 IFF
            ".puma",             // PumaTracker (distinct de ".pum" — format UADE différent,
                                 // cf. ExternalPlayers.cs, aucun conflit littéral)
            ".rtm",              // RealTracker
            // SoundFX / MultiMedia Sound — ".sfx" retiré d'UadePlayer (conflit réel,
            // ".sfx2"/".mms" n'y étaient de toute façon pas listés).
            ".sfx", ".sfx2", ".mms",
            ".stx",              // Scream Tracker Music Interface Kit
            // Symphonie / Symphonie Pro — retiré d'UadePlayer (conflit réel).
            ".symmod",
            ".umx",              // Unreal Music (Unreal Tournament/Deus Ex/Jazz Jackrabbit 3D)
        };
        private readonly List<ITrackerDecoder> _decoders;
        private readonly ILogger<TrackerService> _log;

        /// <summary>
        /// Instancie le service avec les décodeurs par défaut.
        /// Pour injecter des décodeurs personnalisés, utilisez le constructeur avec paramètre.
        /// </summary>
        public TrackerService(ILogger<TrackerService>? logger = null)
            : this(null, logger) { }

        public TrackerService(IEnumerable<ITrackerDecoder>? decoders, ILogger<TrackerService>? logger = null)
        {
            _log = logger ?? NullLogger<TrackerService>.Instance;
            _decoders = decoders?.ToList() ?? [new ModDecoder(), new XmDecoder(), new S3mDecoder()];
        }

        public string[] AllSupportedExtensions =>
            _decoders.SelectMany(d => d.SupportedExtensions).Distinct().Order().ToArray();

        public async Task<(TrackerModule Module, ITrackerPlayer Player)> OpenAsync(
            string filePath, CancellationToken ct = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Fichier tracker introuvable.", filePath);

            // On lit tout le fichier en mémoire une seule fois.
            // Un MemoryStream est toujours repositionnable sans effet de bord,
            // ce qui évite les bugs de position entre CanDecode / DecodeAsync.
            byte[] fileBytes = await File.ReadAllBytesAsync(filePath, ct);

            // ── Décompression ICE! (Pack-Ice) ───────────────────────────────
            // Certains fichiers de la scène demo restent stockés compressés au
            // format ICE! au lieu d'être stockés décompressés. ZXTune (et les
            // autres décodeurs) ne savent pas lire ce format directement : on
            // le détecte et décompresse ici, en amont de tout le reste du
            // pipeline. EXCLUSION .sndh : SndhPlayer.dll gère elle-même la
            // décompression ICE! en interne et attend les octets bruts du
            // fichier d'origine — ne pas décompresser ici sous peine de lui
            // fournir des données déjà traitées qu'elle n'attend pas.
            bool isSndhRouted = SndhPlayer.SupportedExtensions.Contains(
                Path.GetExtension(filePath).ToLowerInvariant().Trim());
            if (!isSndhRouted && Decoders.IceDecruncher.IsIceData(fileBytes))
            {
                _log.LogInformation("OpenAsync: '{File}' est une archive ICE! compressée, décompression...", filePath);
                byte[]? decrunched = null;
                try
                {
                    decrunched = Decoders.IceDecruncher.Decrunch(fileBytes);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "OpenAsync: échec décompression ICE! pour '{File}'", filePath);
                }

                if (decrunched != null)
                {
                    // Écrit le résultat décompressé vers un fichier temporaire et
                    // redirige le reste du pipeline (décodeurs + players externes
                    // type ZXTune/UADE qui lisent le fichier depuis le disque) vers
                    // ce nouveau chemin.
                    var tempDir = Path.Combine(Path.GetTempPath(), "DemoBase_IceDecrunch");
                    Directory.CreateDirectory(tempDir);
                    var decrunchedPath = Path.Combine(tempDir,
                        Path.GetFileNameWithoutExtension(filePath) + "_decrunched" + Path.GetExtension(filePath));
                    await File.WriteAllBytesAsync(decrunchedPath, decrunched, ct);

                    _log.LogInformation("OpenAsync: décompression ICE! réussie ({Before} → {After} octets) : '{Out}'",
                        fileBytes.Length, decrunched.Length, decrunchedPath);

                    filePath  = decrunchedPath;
                    fileBytes = decrunched;
                }
                else
                {
                    _log.LogWarning("OpenAsync: décompression ICE! a échoué pour '{File}' — tentative de lecture du fichier original.", filePath);
                }
            }

            ITrackerDecoder? decoder = null;
            string ext = Path.GetExtension(filePath).ToLowerInvariant().Trim();
            _log.LogDebug("OpenAsync: filePath='{Path}' ext='{Ext}' decoders={Count}",
                filePath, ext, _decoders.Count);
            foreach (var d in _decoders)
                _log.LogDebug("  Decoder '{Name}' supports: {Exts}",
                    d.FormatName, string.Join(",", d.SupportedExtensions));

            foreach (var d in _decoders)
            {
                if (!d.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                using var probe = new MemoryStream(fileBytes, writable: false);
                try
                {
                    if (d.CanDecode(probe)) { decoder = d; break; }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "CanDecode a levé une exception pour {Decoder}", d.FormatName);
                }
            }

            // Fallback 1 : extension seule (formats exotiques sans décodeur C#)
            //
            // 2026-07-31, retour utilisateur (fichiers ChipTracker/TCB Tracker en .mod,
            // Future Composer... — "généralise-le au format que libopenmpt peut lire") :
            // ce fallback réassignait N'IMPORTE QUEL décodeur dont l'extension correspond,
            // MÊME quand la boucle juste au-dessus avait déjà essayé son CanDecode() et
            // obtenu FALSE — annulant purement et simplement la protection anti-faux-positif
            // de ModDecoder/XmDecoder/S3mDecoder/StmDecoder/DbmDecoder (tous ont un vrai
            // CanDecode basé sur le contenu, cf. leurs fichiers respectifs). Concrètement :
            // un vrai fichier ChipTracker en .mod, correctement rejeté par ModDecoder.
            // CanDecode() (ni tag connu, ni structure 15/31-samples plausible), se voyait
            // quand même réassigné à ModDecoder ICI — DecodeAsync tentait alors de le
            // décoder n'importe comment (garbage silencieux ou exception selon les octets),
            // et libopenmpt (qui sait pourtant lire ChipTracker nativement depuis sa version
            // 0.8.0) n'avait JAMAIS l'occasion d'essayer, puisque le choix du player
            // ci-dessous se base sur ce `decoder`.
            //
            // Fix : restreint aux SEULS décodeurs dont CanDecode() ne fait AUCUNE
            // vérification réelle de contenu (ZXTuneDecoder/UadeDecoder renvoient toujours
            // `true`, cf. leurs fichiers — "on fait confiance à l'extension") — pour ceux-là,
            // ce fallback est un pur no-op de toute façon (la boucle du dessus les aurait
            // déjà sélectionnés). Pour les 5 décodeurs C# à vérification réelle, un rejet
            // reste un rejet : `decoder` reste null, et la suite du pipeline (Fallback 1b/2
            // puis le choix de player final) laisse maintenant sa vraie chance à libopenmpt.
            decoder ??= _decoders.FirstOrDefault(d =>
                (d is ZXTuneDecoder or UadeDecoder) &&
                d.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase));

            // Fallback 1b : préfixe de nom de fichier UADE (ex: cust.intro → préfixe "cust")
            // Uniquement si l'extension n'est pas gérée par libopenmpt
            if (decoder is null && !LibopenmptExtensions.Contains(ext))
            {
                var uade = _decoders.OfType<UadeDecoder>().FirstOrDefault();
                if (uade != null && uade.CanDecodeFile(filePath))
                {
                    decoder = uade;
                    _log.LogInformation("OpenAsync: préfixe UADE détecté pour '{File}'", filePath);
                }
            }

            // Fallback 2 : détection par contenu sans tenir compte de l'extension
            // Couvre les fichiers avec extension non standard (mod.krunk_d, cust.intro, etc.)
            // EXCLUT les extensions libopenmpt pour éviter qu'UADE/ZXTune les intercepte.
            if (decoder is null && !LibopenmptExtensions.Contains(ext))
            {
                _log.LogDebug("OpenAsync: pas de décodeur par extension, détection par contenu...");

                var orderedDecoders = _decoders
                    .OrderBy(d => d is ZXTuneDecoder ? 1 : 0)
                    .ToList();

                foreach (var d in orderedDecoders)
                {
                    if (d is ZXTuneDecoder zxd && !zxd.CanDecodeFile(filePath)) continue;

                    using var probe = new MemoryStream(fileBytes, writable: false);
                    try
                    {
                        if (d.CanDecode(probe)) { decoder = d; break; }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "CanDecode (contenu) a levé une exception pour {Decoder}", d.FormatName);
                    }
                }
                if (decoder is not null)
                    _log.LogInformation("OpenAsync: format détecté par contenu → {Decoder}", decoder.FormatName);
            }

            TrackerModule module;
            if (decoder is not null)
            {
                _log.LogInformation("Décodage avec {Decoder} : {File}", decoder.FormatName, filePath);
                try
                {
                    using var decodeStream = new MemoryStream(fileBytes, writable: false);
                    module = await decoder.DecodeAsync(decodeStream, filePath, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 2026-07-30, retour utilisateur (piste Modland "Ace Tracker", .am) :
                    // un décodeur retenu par la détection par contenu (Fallback 2 ci-dessus,
                    // utilisée pour les extensions inconnues — cas courant avec Modland) peut
                    // se révéler être un FAUX POSITIF une fois le décodage réellement tenté :
                    // son CanDecode() a accepté le fichier sur une heuristique imparfaite (cf.
                    // ModDecoder.cs, tag/volumes 15-samples), mais DecodeAsync() découvre
                    // ensuite que la structure ne correspond pas vraiment — ex. lit des
                    // patterns bien au-delà de la fin réelle du fichier, EndOfStreamException
                    // ("Unable to read beyond the end of the stream."). Avant ce correctif,
                    // cette exception remontait telle quelle jusqu'à l'utilisateur et
                    // bloquait toute lecture. Repli : mêmes métadonnées minimales que "pas de
                    // décodeur trouvé" — le choix du player ci-dessous (UADE/ZXTune/
                    // libopenmpt selon l'extension) garde sa chance de lire le fichier
                    // correctement ; seules les métadonnées enrichies par CE décodeur C#
                    // particulier sont perdues. decoder=null pour ne pas router le CHOIX DU
                    // PLAYER sur la base d'un décodeur dont on vient de prouver qu'il ne
                    // convient pas à ce fichier (sans effet pour Mod/Xm/S3m/Stm/Dbm, qui
                    // n'influencent pas ce choix — seul cas réel : ZXTuneDecoder/UadeDecoder,
                    // dont le DecodeAsync ne fait en pratique jamais échouer ce bloc).
                    _log.LogWarning(ex,
                        "DecodeAsync a échoué pour {Decoder} sur {File} — repli métadonnées minimales.",
                        decoder.FormatName, filePath);
                    module = new TrackerModule
                    {
                        FilePath = filePath,
                        FileSize = fileBytes.Length,
                        Format   = Decoders.FormatDetector.DetectFormat(filePath,
                                       new MemoryStream(fileBytes, writable: false)),
                        Title    = Path.GetFileNameWithoutExtension(filePath)
                    };
                    decoder = null;
                }
            }
            else
            {
                _log.LogWarning("Pas de décodeur trouvé pour {File} — métadonnées minimales.", filePath);
                module = new TrackerModule
                {
                    FilePath = filePath,
                    FileSize = fileBytes.Length,
                    Format   = Decoders.FormatDetector.DetectFormat(filePath,
                                   new MemoryStream(fileBytes, writable: false)),
                    Title    = Path.GetFileNameWithoutExtension(filePath)
                };
            }

            // Choisit le bon player selon le décodeur :
            // audio courant (.wav/.mp3/.flac/.ogg...) → NativeAudioPlayer (NAudio direct)
            // .sndh         → SndhPlayer     (SndhPlayer.dll, émulation 68000/YM2149 complète)
            // ZXTuneDecoder → ZXTunePlayer   (process externe zxtune123.exe)
            // UadeDecoder   → UadePlayer     (process externe uade123.exe)
            // autres        → NativeTrackerPlayer (libopenmpt)
            ITrackerPlayer player;
            if (NativeAudioPlayer.SupportedExtensions.Contains(ext))
            {
                _log.LogInformation("Player NAudio (audio natif) pour : {File}", filePath);
                var nativeAudio = new NativeAudioPlayer(_log);
                await nativeAudio.LoadAsync(module, ct);
                player = nativeAudio;
            }
            else if (SndhPlayer.SupportedExtensions.Contains(ext))
            {
                if (SndhPlayer.IsAvailable)
                {
                    _log.LogInformation("Player SNDH (SndhPlayer.dll) pour : {File}", filePath);
                    var sndhPlayer = new SndhPlayer(_log);
                    await sndhPlayer.LoadAsync(module, ct);
                    player = sndhPlayer;
                }
                else
                {
                    _log.LogWarning(
                        "SndhPlayer.dll introuvable (répertoire de l'application ou Externals/). " +
                        "Compiler depuis https://github.com/arnaud-carre/sndh-player et placer " +
                        "SndhPlayer.dll dans le répertoire de l'application. " +
                        "Aucun fallback fiable pour ce format (ZXTune liste l'extension mais ne la " +
                        "lit pas réellement, libopenmpt ne la gère pas) — lecture silencieuse.");
                    var nativePlayer = new NativeTrackerPlayer(_log);
                    await nativePlayer.LoadAsync(module, ct);
                    player = nativePlayer;
                }
            }
            else if (decoder is ZXTuneDecoder)
            {
                // 2026-08-06 : ZXTunePlayer utilise désormais zxtune.dll (pont natif
                // P/Invoke) au lieu de zxtune123.exe (process externe) — cf. le
                // commentaire de classe sur ZXTunePlayer (ExternalPlayers.cs) pour le
                // détail du changement.
                if (ZXTunePlayer.IsAvailable)
                {
                    _log.LogInformation("Player ZXTune (natif, zxtune.dll) pour : {File}", filePath);
                    var zxPlayer = new ZXTunePlayer(_log);
                    bool zxFailed;
                    try
                    {
                        await zxPlayer.LoadAsync(module, ct);
                        zxFailed = !zxPlayer.IsPlayable;
                    }
                    catch (Exception ex)
                    {
                        // 2026-08-06 : ZXTunePlayer.LoadAsync ne lève normalement plus
                        // d'exception pour un format non reconnu (IsPlayable=false suffit
                        // désormais, cf. son commentaire) — ce catch couvre les cas plus
                        // rares restants (I/O, etc.). Même second recours symétrique que
                        // pour UadeDecoder ci-dessous : les deux décodeurs se disputent les
                        // mêmes formats "Amiga exotiques" par confiance en le contenu/
                        // l'extension (pas une vraie discrimination), donc un échec de l'un
                        // mérite un essai par l'autre avant d'abandonner. zxFailed=true ICI
                        // plutôt que de se fier seulement à IsPlayable après coup : si
                        // l'exception survient avant même que LoadAsync ait pu positionner
                        // IsPlayable (ex. erreur I/O sur File.ReadAllBytesAsync), IsPlayable
                        // resterait à sa valeur par défaut `true` — un faux "succès".
                        _log.LogError(ex, "ZXTune: LoadAsync a levé une exception");
                        zxFailed = true;
                    }

                    if (!zxFailed || !UadePlayer.IsAvailable)
                    {
                        player = zxPlayer;
                    }
                    else
                    {
                        _log.LogInformation(
                            "ZXTune n'a pas pu lire '{File}' — tentative UADE en second recours.", filePath);
                        zxPlayer.Dispose();
                        var uadeFallback = new UadePlayer(_log);
                        try { await uadeFallback.LoadAsync(module, ct); }
                        catch (Exception ex2)
                        {
                            _log.LogWarning(ex2, "UADE (second recours) a également échoué pour '{File}'.", filePath);
                        }
                        player = uadeFallback; // IsPlayable reflète honnêtement le résultat, quel qu'il soit
                    }
                }
                else
                {
                    _log.LogWarning(
                        "zxtune.dll introuvable ou non chargeable (répertoire de l'application " +
                        "ou Externals/, en x64). Compiler le pont natif (zxtune_bridge.cpp) et " +
                        "placer zxtune.dll à côté de l'exécutable. " +
                        "Tentative de lecture via libopenmpt en fallback.");
                    var nativePlayer = new NativeTrackerPlayer(_log);
                    await nativePlayer.LoadAsync(module, ct);
                    player = nativePlayer;
                }
            }
            else if (decoder is UadeDecoder)
            {
                // 2026-08-06 : UadePlayer utilise désormais libuade.dll + uadecore.exe
                // (pont natif P/Invoke) au lieu d'uade123.exe (process externe streaming
                // stdout) — cf. le commentaire de classe sur UadePlayer (ExternalPlayers.cs).
                if (UadePlayer.IsAvailable)
                {
                    _log.LogInformation("Player UADE (natif, libuade.dll) pour : {File}", filePath);
                    var uadePlayer = new UadePlayer(_log);
                    bool uadeFailed;
                    try
                    {
                        _log.LogInformation("UADE: appel LoadAsync...");
                        await uadePlayer.LoadAsync(module, ct);
                        _log.LogInformation("UADE: LoadAsync terminé, SubsongCount={N}",
                            uadePlayer.SubsongCount);
                        uadeFailed = !uadePlayer.IsPlayable;
                    }
                    catch (Exception ex)
                    {
                        // uadeFailed=true ICI plutôt que de se fier seulement à IsPlayable
                        // après coup : cf. le même raisonnement détaillé dans la branche
                        // ZXTuneDecoder ci-dessus (une exception avant que uade_play() ait pu
                        // s'exécuter laisserait IsPlayable à sa valeur par défaut `true`).
                        _log.LogError(ex, "UADE: LoadAsync a levé une exception");
                        uadeFailed = true;
                    }

                    // 2026-08-06, retour utilisateur ("je ne vois pas le test pour zxtune.
                    // j'ai l'impression que l'exception de uade fait planter tout les
                    // autres tests") : confirmé — decoder=UadeDecoder est choisi par la
                    // détection heuristique de Fallback 2 ci-dessus (CanDecode() "de
                    // confiance", pas une vraie analyse de contenu — UadeDecoder ET
                    // ZXTuneDecoder se disputent les mêmes formats "Amiga exotiques" de
                    // cette façon, UADE étant simplement ordonné en premier). Avant ce
                    // correctif, quand UadePlayer.LoadAsync échouait réellement (comme dans
                    // les logs fournis — InvalidOperationException, "UADE: LoadAsync a levé
                    // une exception"), l'exception était journalisée puis le player UADE
                    // CASSÉ était quand même gardé tel quel : ZXTune n'avait alors JAMAIS
                    // l'occasion d'essayer ce même fichier. IsPlayable (ajouté plus tôt
                    // aujourd'hui pour les deux backends) permet maintenant de détecter cet
                    // échec ici et de tenter ZXTune en second recours avant d'abandonner.
                    if (!uadeFailed || !ZXTunePlayer.IsAvailable)
                    {
                        player = uadePlayer;
                    }
                    else
                    {
                        _log.LogInformation(
                            "UADE n'a pas pu lire '{File}' — tentative ZXTune en second recours.", filePath);
                        uadePlayer.Dispose();
                        var zxFallback = new ZXTunePlayer(_log);
                        try { await zxFallback.LoadAsync(module, ct); }
                        catch (Exception ex2)
                        {
                            _log.LogWarning(ex2, "ZXTune (second recours) a également échoué pour '{File}'.", filePath);
                        }
                        player = zxFallback; // IsPlayable reflète honnêtement le résultat, quel qu'il soit
                    }
                }
                else
                {
                    _log.LogWarning(
                        "libuade.dll/uadecore.exe introuvables (uadecore={Path}). " +
                        "Placer libuade.dll et uadecore.exe (compilés en x64) dans le répertoire " +
                        "de l'application ou Externals/UADE/, à côté d'eagleplayer.conf/uaerc/score/players/.",
                        UadePlayer.UadecoreExePath);
                    var nativePlayer = new NativeTrackerPlayer(_log);
                    await nativePlayer.LoadAsync(module, ct);
                    player = nativePlayer;
                }
            }
            else
            {
                // 2026-08-06, retour utilisateur ("il y a aussi le cas openmpt qui peut
                // planter sur la lecture des fichiers car non reconnu malgre l'extension
                // [...] il faudrait tester derriere uade et zxtune") : libopenmpt est le
                // backend par défaut pour la quasi-totalité des extensions tracker
                // "standard" (branche atteinte quand aucun décodeur ZXTune/UADE/SNDH/audio
                // natif n'a été retenu), mais un fichier dont l'extension correspond peut
                // malgré tout être une variante/un fichier que libopenmpt ne sait pas
                // vraiment lire (mauvais sous-format, corruption, faux positif
                // d'extension...). Avant ce correctif, un tel échec n'avait AUCUN filet —
                // ni UADE ni ZXTune n'étaient jamais tentés pour un fichier arrivé jusqu'ici.
                // Même principe de repli honnête que les branches ZXTuneDecoder/UadeDecoder
                // ci-dessus, avec un maillon de plus (libopenmpt → UADE → ZXTune).
                _log.LogInformation("Player libopenmpt (natif) pour : {File}", filePath);
                var nativePlayer = new NativeTrackerPlayer(_log);
                bool nativeFailed;
                try
                {
                    await nativePlayer.LoadAsync(module, ct);
                    nativeFailed = !nativePlayer.IsPlayable;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "libopenmpt: LoadAsync a levé une exception pour '{File}'", filePath);
                    nativeFailed = true;
                }

                if (!nativeFailed)
                {
                    player = nativePlayer;
                }
                else
                {
                    _log.LogInformation(
                        "libopenmpt n'a pas pu lire '{File}' — tentative UADE puis ZXTune en second recours.",
                        filePath);

                    ITrackerPlayer fallback = nativePlayer; // gardé tel quel si aucun autre backend ne s'en sort mieux

                    if (UadePlayer.IsAvailable)
                    {
                        var uadeFallback = new UadePlayer(_log);
                        bool uadeFallbackFailed;
                        try
                        {
                            await uadeFallback.LoadAsync(module, ct);
                            uadeFallbackFailed = !uadeFallback.IsPlayable;
                        }
                        catch (Exception ex2)
                        {
                            _log.LogWarning(ex2, "UADE (recours après libopenmpt) a échoué pour '{File}'.", filePath);
                            uadeFallbackFailed = true;
                        }

                        if (!uadeFallbackFailed)
                        {
                            fallback.Dispose();
                            fallback = uadeFallback;
                        }
                        else if (ZXTunePlayer.IsAvailable)
                        {
                            var zxFallback = new ZXTunePlayer(_log);
                            try { await zxFallback.LoadAsync(module, ct); }
                            catch (Exception ex3)
                            {
                                _log.LogWarning(ex3,
                                    "ZXTune (recours après libopenmpt+UADE) a échoué pour '{File}'.", filePath);
                            }
                            fallback.Dispose();
                            uadeFallback.Dispose();
                            fallback = zxFallback; // dernier maillon : IsPlayable reflète honnêtement le résultat
                        }
                        else
                        {
                            fallback.Dispose();
                            fallback = uadeFallback;
                        }
                    }
                    else if (ZXTunePlayer.IsAvailable)
                    {
                        var zxFallback = new ZXTunePlayer(_log);
                        try { await zxFallback.LoadAsync(module, ct); }
                        catch (Exception ex3)
                        {
                            _log.LogWarning(ex3, "ZXTune (recours après libopenmpt) a échoué pour '{File}'.", filePath);
                        }
                        fallback.Dispose();
                        fallback = zxFallback;
                    }

                    player = fallback;
                }
            }

            return (module, player);
        }
    }
}
