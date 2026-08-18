using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using TrackerPlayer.Core.Interfaces;
using TrackerPlayer.Core.Models;

using ModelsPlaybackState = TrackerPlayer.Core.Models.PlaybackState;

namespace TrackerPlayer.Core.Players
{
/// <summary>
/// Registry des process externes lancés par ExeMusicPlayer.
/// Permet de les tuer proprement à la fermeture de l'application.
///
/// 2026-08-02, diagnostic suite à un retour utilisateur ("bouton stop sans effet" +
/// rafale de Win32Exception visibles au changement de release dans une party avec
/// plusieurs musiques exe) : cette liste n'était JAMAIS purgée (Register ajoutait,
/// rien ne retirait), donc grossissait sur toute la durée de la session — une
/// entrée par musique exe jouée. Or KillAll() est appelée comme "filet de sécurité"
/// à chaque Stop()/Dispose() d'ExeMusicPlayer (cf. plus bas), et ré-itérait donc
/// systématiquement sur TOUT l'historique de process exe déjà terminés depuis le
/// début de la session — Kill()/HasExited sur des process morts depuis longtemps
/// (parfois déjà Dispose() par leur propriétaire), coûteux et source de bruit
/// (Win32Exception first-chance visibles sous débogueur) qui s'aggrave à mesure
/// que la session avance. Ajout de Unregister(), appelée par ExeMusicPlayer.Stop()
/// une fois SON process géré directement — la liste ne contient donc plus jamais
/// que les musiques exe en cours (ou dans la brève fenêtre de course avant leur
/// premier Stop()), et KillAll() reste réservé au cas où _process est encore null
/// (seul cas où le filet de sécurité global est réellement utile).
/// List + lock plutôt que ConcurrentBag : Register/Unregister ne sont appelés
/// qu'une poignée de fois par session (une musique exe à la fois), donc aucun
/// besoin de structure lock-free ; ConcurrentBag n'offre de toute façon pas de
/// retrait ciblé par élément.
/// </summary>
public static class ExternalProcessRegistry
{
    private static readonly List<System.Diagnostics.Process> _processes = new();
    private static readonly object _lock = new();

    public static void Register(System.Diagnostics.Process p)
    {
        lock (_lock) _processes.Add(p);
    }

    /// <summary>Retire un process de la surveillance globale une fois qu'il a été
    /// géré directement (arrêté/disposé) par son ExeMusicPlayer propriétaire —
    /// évite que KillAll() le retraite inutilement plus tard (cf. commentaire de
    /// classe ci-dessus).</summary>
    public static void Unregister(System.Diagnostics.Process p)
    {
        lock (_lock) _processes.Remove(p);
    }

    public static void KillAll()
    {
        System.Diagnostics.Process[] snapshot;
        lock (_lock) snapshot = _processes.ToArray();
        foreach (var p in snapshot)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
        // Vidée après traitement : évite de retraiter les mêmes entrées si KillAll()
        // est appelée plusieurs fois dans la session (ex. plusieurs races _process
        // null successives), cohérent avec le fait qu'elles viennent d'être tuées.
        lock (_lock) _processes.Clear();
    }
}

    /// <summary>
    /// Dossier pour les fichiers WAV générés par ZXTune et UADE.
    /// Par défaut : %TEMP%\TrackerPlayer\ — peut être surchargé via TempDir.Override
    /// (appelé depuis DemoBase.App au démarrage pour rediriger vers Working\Tracker).
    /// Les fichiers sont supprimés à la fin de chaque lecture (Dispose).
    /// </summary>
    public static class TempDir
    {
        private static string? _override;

        /// <summary>Surcharge le dossier temp (appelé depuis l'app hôte au démarrage).</summary>
        public static void Override(string path) { _override = path; }

        public static string Path =>
            _override ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TrackerPlayer");

        static TempDir()
        {
            Directory.CreateDirectory(Path);
            CleanOldFiles();
        }

        /// <summary>Crée un chemin de fichier WAV temporaire unique.</summary>
        public static string NewWavPath(string prefix = "")
            => System.IO.Path.Combine(Path, $"{prefix}{Guid.NewGuid():N}.wav");

        /// <summary>Supprime les fichiers WAV résiduels de sessions précédentes.</summary>
        private static void CleanOldFiles()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Path, "*.wav"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ZXTune Player — formats exotiques via zxtune.dll (pont natif P/Invoke)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lecteur de formats exotiques (Amiga, ZX Spectrum, C64, Atari, etc.)
    /// via zxtune.dll — pont natif compilé par l'utilisateur (P/Invoke direct
    /// vers le cœur zxtune), en remplacement du process externe zxtune123.exe.
    ///
    /// 2026-08-06, retour utilisateur : "j'ai réussi à compiler une DLL pour
    /// utiliser zxtune sans externals. ça pourra aussi eviter de passer par la
    /// génération d'un wav et la detection des subsongs est instantanée. peux
    /// tu regarder ce projet et t'en inspirer pour intégrer la DLL en lieu et
    /// place de zxtune123 et il faudra aussi penser à l'enlever des
    /// externals". Remplace entièrement l'ancienne implémentation (ci-dessous
    /// avant ce correctif) qui lançait zxtune123.exe --wav filename=... pour
    /// chaque lecture ET pour chaque subsong sondé (un aller-retour process +
    /// fichier WAV temporaire par sondage — cf. QuerySingleSubsongAsync,
    /// supprimée). Le pont natif (TrackerPlayer.Core/Players/ZxTuneNative.cs,
    /// inspiré du projet ZxTuneWpfDemo fourni par l'utilisateur) apporte :
    ///   - Pas de process externe ni de fichier WAV temporaire — rendu PCM
    ///     directement en mémoire (Zx_Render), consommé par un IWaveProvider
    ///     (ZxTuneWaveProvider) comme le fait déjà NativeTrackerPlayer/libopenmpt.
    ///   - Découverte des subsongs instantanée : Zx_OpenContainer déclenche
    ///     Service::DetectModules côté natif une seule fois à l'ouverture, au
    ///     lieu de sonder "?#0", "?#1", "?#2"... un par un via des process
    ///     zxtune123 successifs (jusqu'à 64 lancements dans le pire cas avant
    ///     ce correctif, cf. QuerySubsongsAsync supprimée).
    ///   - Position de lecture et bouclage lus directement depuis
    ///     Module::State côté natif (Zx_GetPositionSeconds/Zx_GetLoopCount).
    ///
    /// PRÉREQUIS :
    ///   Compiler zxtune.dll (pont natif "zxtune_bridge.cpp" fourni par
    ///   l'utilisateur, x64) et la placer dans le répertoire de l'application
    ///   (ou dans Externals/, ajouté au PATH au démarrage — cf. App.xaml.cs/
    ///   ConfigureExternalPaths, même schéma que libopenmpt.dll). Aucun
    ///   téléchargement automatique — cf. EmulatorDownloadCatalog.cs, l'entrée
    ///   "ZXTune" (zxtune123.exe) a été retirée du catalogue Externals à
    ///   l'occasion de ce correctif : plus nécessaire, la DLL native remplace
    ///   entièrement le player en ligne de commande.
    ///
    /// FORMATS (liste non exhaustive, inchangée — cf. SupportedExtensions) :
    ///   Amiga : AHX, HVL
    ///   ZX Spectrum : AY, VTX, PSG, PT1/PT2/PT3, STC, STP…
    ///   C64 : SID
    ///   Atari : SAP, RMT
    ///   SNES : SPC
    ///   + GameBoy, NES, MSX, PSX, N64, VGM, V2M (Farbrausch)…
    /// </summary>
    public sealed class ZXTunePlayer : ITrackerPlayer
    {
        /// <summary>
        /// Extensions de fichiers gérées par ZXTune (formats non couverts par libopenmpt).
        /// libopenmpt gère déjà MOD/XM/S3M/IT — ici on cible les formats exotiques.
        /// </summary>
        public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            // Amiga exotiques
            // 2026-07-31, retour utilisateur ("les fichiers .mmd0, .mmd1, .mmd2, .mmd3
            // et .okta doivent passer par libopenmpt avec une vue ft2") : ".med"/".okt"/
            // ".okta" retirés d'ici — conflit réel avec libopenmpt (les deux les
            // listaient), et ZXTuneDecoder.CanDecode() renvoie toujours true, donc
            // ZXTune gagnait systématiquement la 1ère boucle de sélection de
            // TrackerService.OpenAsync avant même que libopenmpt n'ait sa chance (même
            // schéma que .psm/.stp/.digi déjà résolus). Ajoutés à
            // NativeTrackerPlayer.LibopenmptExtensions (déjà présents pour .okt/.okta/
            // .med, qui restent listés mais ne sont plus en conflit).
            ".ahx", ".hvl", // .dmf géré par libopenmpt
            // ZX Spectrum / AY
            ".ay", ".vtx", ".psg", ".pt1", ".pt2", ".pt3",
            ".stc", ".st1", ".st3", ".asc", ".sqt",
            // 2026-07-30, retour utilisateur (".psm - à ouvrir avec libopenmpt et
            // les patterns FT2") : conflit réel, même schéma que .digi plus haut
            // dans le projet — .psm existait ici (ProSoundMaker, ZX Spectrum) ET
            // dans LibopenmptExtensions vise en réalité le format PC "PSM" (Epic
            // MegaGames MASI), ce qui faisait gagner ZXTune dès la toute première
            // boucle de correspondance exacte (ZXTuneDecoder.CanDecode() toujours
            // vrai), avant même le garde-fou LibopenmptExtensions des fallbacks.
            // Retiré d'ici, ajouté à LibopenmptExtensions (NativeTrackerPlayer.cs).
            // 2026-07-31, retour utilisateur ("il faut ouvrir les fichiers .stp avec
            // visu ft2") : MÊME conflit d'extension exact — ".stp" désignait ici le
            // format "SoundTracker compiled" ZX Spectrum, mais ZXTuneDecoder ne
            // produit JAMAIS de vrais patterns (DecodeAsync retourne un module
            // "coquille vide", cf. son commentaire) — impossible d'y afficher QUELQUE
            // vue que ce soit, FT2 ou autre. ".stp" est aussi le format PC/Atari
            // Falcon "Soundtracker Pro II", supporté par libopenmpt AVEC de vrais
            // patterns (cf. EnrichModule, NativeTrackerPlayer.cs) — c'est très
            // probablement CE format-là que l'utilisateur ouvre. Retiré d'ici, ajouté
            // à LibopenmptExtensions.
            ".ftc", ".gtr", ".psc",
            // C64
            ".sid", ".psid",
            // 2026-07-30, retour utilisateur ("il va falloir ajuster les fichiers
            // jouables par uade ou zxtune... le nombre de format est enorme sur
            // modland") : .rsid est le même moteur SID que .sid/.psid ci-dessus,
            // juste une variante de nommage HVSC (RealSID) — 3540 pistes Modland
            // ("RealSID") jusqu'ici non routées du tout.
            ".rsid",
            // 2026-07-30, retour utilisateur ("à ouvrir avec zxtune") : .emul,
            // format non couvert par libopenmpt/UADE jusqu'ici.
            ".emul",
            // Atari
            ".sap", ".rmt",
            // YM (Atari ST / AY chip) — toutes variantes
            ".ym", ".ym2", ".ym3", ".ym4", ".ym5", ".ym6",
            // SNES
            ".spc",
            // Autres
            ".nsf", ".nsfe", ".gbs", ".gsf", ".hes", ".kss",
            ".vgm", ".vgz", ".gym",
            ".psf", ".psf2",
            // 2026-07-30 : variantes "mini" (une seule piste, mêmes données que le
            // format de base ci-dessus) des trois familles GSF/PSF/PSF2 déjà
            // routées vers ZXTune — Modland les classe séparément (37 000+ pistes
            // à elles trois : minigsf 22865, minipsf 12216, minipsf2 2383) mais
            // c'est le même moteur. Les fichiers compagnons "*lib" (gsflib/psflib/
            // psf2lib, données partagées non jouables seules — même principe que
            // smpl.* pour TFMX) ne sont volontairement PAS ajoutés ici : les
            // ajouter les rendrait sélectionnables dans le navigateur Modland alors
            // qu'ils ne produisent aucun son seuls.
            ".minigsf", ".minipsf", ".minipsf2",
            // Atari ST — SNDH retiré : ZXTune liste l'extension mais ne joue pas
            // réellement ces fichiers (cf. SndhPlayer.cs). Géré par SndhPlayer
            // (SndhPlayer.dll, émulation 68000/YM2149 complète), pas ici.
            // Formats audio courants RETIRÉS : .mp3/.flac/.m4a/.ogg/.wav/.aiff ne
            // doivent PAS être routés vers ZXTune — le cœur zxtune (process comme
            // DLL) est un moteur de formats tracker/chiptune, pas un lecteur audio
            // standard (RegisterPlayerPlugins n'enregistre pas ces formats côté
            // natif). Quand le zip d'une release music contient un .wav ou un .mp3
            // déjà encodé, il faut le jouer directement via NAudio (cf.
            // NativeAudioPlayer ci-dessous), pas le passer à ZXTune.
            // 2026-08-04, retour utilisateur ("les fichiers .v2m doivent passer par
            // zxtune et non uade") : .v2m (Farbrausch V2M, format tracker-like des
            // 64k/synthés V2) n'était dans AUCUNE des deux listes d'extensions
            // (ni ici, ni UadePlayer.SupportedExtensions) — donc jamais capturé par
            // la 1ère boucle de sélection par extension de TrackerService.OpenAsync,
            // qui tombait dans le "Fallback 2" (détection par contenu, sans extension).
            // Là, UadeDecoder.CanDecode() renvoie TOUJOURS true (cf. commentaires plus
            // haut dans ce fichier), donc UADE récupérait .v2m par défaut, avant même
            // que ZXTune (qui supporte réellement le format V2M) n'ait sa chance.
            ".v2m",
        };

        /// <summary>Vérifie si zxtune.dll est chargeable (présente, bonne architecture, exports attendus).</summary>
        public static bool IsAvailable => ZxTuneNativeInterop.IsAvailable;

        // ── ITrackerPlayer ────────────────────────────────────────────
        public TrackerFormat[] SupportedFormats => [];   // détection par extension
        public event EventHandler<ModelsPlaybackState>? StateChanged;
        public event EventHandler?                      PlaybackFinished;
        public ModelsPlaybackState CurrentState => _state;

        public int SubsongCount        => _subsongs.Count;
        public int CurrentSubsongIndex => _currentSubsong;

        // 2026-08-06, retour utilisateur ("j'ai l'impression que zxtune n'est jamais
        // testé pour les formats inconnus mais uniquement uade") : confirmé — avant ce
        // correctif, ZXTunePlayer n'avait AUCUN équivalent de
        // NativeTrackerPlayer.IsPlayable / UadePlayer.IsPlayable, et
        // SoundtrackPlayerViewModel.IsFormatUnsupported ne le testait donc jamais (cf.
        // son commentaire, qui documentait explicitement l'absence de signal fiable —
        // vrai avant le passage au pont natif zxtune.dll du 2026-08-06, plus
        // aujourd'hui). Deux cas couverts :
        //  1. Le conteneur natif ne reconnaît pas du tout le fichier (Zx_OpenContainer
        //     échoue) — LoadAsync mettait IsPlayable à false en levant une exception,
        //     affichée comme un toast d'erreur rouge (traitement différent, plus
        //     intrusif, que le message "Format non jouable" utilisé pour
        //     libopenmpt/UADE dans le même cas). LoadAsync ne lève plus d'exception
        //     dans ce cas précis : IsPlayable=false suffit, IsFormatUnsupported prend
        //     le relais comme pour les deux autres backends.
        //  2. Le conteneur natif reconnaît le fichier et annonce au moins un subsong,
        //     mais son rendu ne produit RÉELLEMENT aucune trame audio (faux positif
        //     d'ouverture) — jamais détecté avant ce correctif. Basé sur
        //     ZxTuneWaveProvider.FramesRendered (cf. son commentaire), même principe
        //     que UadePlayer._bytesDecoded : vérifié à la fin de CHAQUE subsong dans
        //     OnWaveOutStopped ci-dessous.
        public bool IsPlayable { get; private set; } = true;

        public void SelectSubsong(int index)
        {
            if (index < 0 || index >= _subsongs.Count) return;
            bool wasPlaying = _state.IsPlaying;
            Stop();
            _currentSubsong = index;
            _state.DurationSeconds = ResolvedDuration(index);
            if (wasPlaying) Play();
        }

        private double ResolvedDuration(int index) =>
            index >= 0 && index < _subsongs.Count && _subsongs[index].Duration.TotalSeconds > 0
                ? _subsongs[index].Duration.TotalSeconds
                : (_module?.DurationSeconds ?? 0);

        public float MasterVolume
        {
            get => _masterVolume;
            set { _masterVolume = value; if (_waveOut != null) _waveOut.Volume = value; }
        }

        // ── State ─────────────────────────────────────────────────────
        private ModelsPlaybackState _state = new();
        private float               _masterVolume = 1.0f;
        private ILogger             _log;
        private WaveOutEvent?       _waveOut;
        private ZxNativeContainer?  _container;
        private ZxNativeSubsongPlayer? _player;
        private CancellationTokenSource? _pollCts;
        private TrackerModule?      _module;
        private List<ZxNativeSubsongInfo> _subsongs = new();
        private int                 _currentSubsong;
        /// <summary>Provider du subsong EN COURS DE LECTURE — permet à
        /// OnWaveOutStopped de lire FramesRendered une fois la lecture terminée (cf.
        /// IsPlayable ci-dessus).</summary>
        private ZxTuneWaveProvider? _currentWaveProvider;

        /// <summary>Buffer circulaire pour l'oscilloscope.</summary>
        public SampleRingBuffer SampleBuffer { get; } = new SampleRingBuffer(8192);

        // 2026-08-07, demande utilisateur : vue d'ensemble complète de la forme
        // d'onde sous l'oscilloscope, remplie progressivement pendant la lecture
        // (Zx_Render synthétise en temps réel, pas de fichier WAV intermédiaire).
        // Redimensionnée (SetDuration) à chaque (re)démarrage de subsong, cf. Play()
        // et OnWaveOutStopped ci-dessous.
        public WaveformOverviewBuffer WaveformOverview { get; } = new WaveformOverviewBuffer();

        public ZXTunePlayer(ILogger? logger = null)
        {
            _log = logger ?? NullLogger.Instance;
        }

        public async Task LoadAsync(TrackerModule module, CancellationToken ct = default)
        {
            _module = module;
            Stop(); // arrête et libère tout (waveOut + player + ancien container)
            IsPlayable = true; // réinitialisé à chaque nouveau fichier, cf. commentaire sur IsPlayable

            var bytes = await File.ReadAllBytesAsync(module.FilePath, ct).ConfigureAwait(false);

            _container?.Dispose();
            _container = ZxNativeContainer.Open(bytes);
            if (_container == null)
            {
                // 2026-08-06 : ne lève plus d'exception ici (cf. commentaire sur
                // IsPlayable) — IsPlayable=false suffit, SoundtrackPlayerViewModel
                // affiche alors "Format non jouable" au lieu de l'oscilloscope vide,
                // au lieu du toast d'erreur rouge d'avant ce correctif. _subsongs reste
                // vide et Play() (appelé sans condition après LoadAsync par
                // SoundtrackPlayerViewModel.OpenAsync) ne fait rien tant que _container
                // est null (cf. son garde-fou existant).
                IsPlayable = false;
                _subsongs = new List<ZxNativeSubsongInfo>();
                _currentSubsong = 0;
                _state = new ModelsPlaybackState { DurationSeconds = 0, CurrentBpm = 0 };
                _log.LogInformation("ZXTune (natif): format non reconnu pour '{File}'", module.FilePath);
                return;
            }

            _subsongs = new List<ZxNativeSubsongInfo>(_container.Count);
            for (int i = 0; i < _container.Count; i++)
                _subsongs.Add(_container.GetInfo(i));
            _currentSubsong = 0;
            _log.LogInformation("ZXTune (natif): {N} subsong(s) pour '{File}'", _subsongs.Count, module.FilePath);

            var first = _subsongs.Count > 0 ? _subsongs[0] : null;
            double duration = first != null && first.Duration.TotalSeconds > 0
                ? first.Duration.TotalSeconds
                : (module.DurationSeconds > 0 ? module.DurationSeconds : 300);

            _state = new ModelsPlaybackState
            {
                DurationSeconds = duration,
                CurrentBpm      = 125
            };

            module.DurationSeconds = duration;

            // Métadonnées lues directement depuis le conteneur natif (Zx_GetSubsongProperty) —
            // plus besoin de les extraire du nom d'un fichier WAV généré par zxtune123.
            if (first != null)
            {
                if (!string.IsNullOrWhiteSpace(first.Type))   module.FormatName = first.Type;
                if (!string.IsNullOrWhiteSpace(first.Title))  module.Title      = first.Title;
                if (!string.IsNullOrWhiteSpace(first.Author)) module.Author     = first.Author;
            }
        }

        public void Play()
        {
            if (_container == null) return;

            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _player?.Dispose();
            _player = null;

            try
            {
                _player = _container.OpenSubsong(_currentSubsong);
            }
            catch (InvalidOperationException ex)
            {
                // 2026-08-06 : défense supplémentaire — le conteneur global a été
                // reconnu (Open a réussi), mais CE subsong précis peut malgré tout
                // échouer à s'ouvrir (Zx_OpenSubsong), cas rare mais possible pour un
                // conteneur multi-pistes partiellement corrompu. Sans ce garde-fou,
                // l'exception levée par ZxNativeSubsongPlayer.FromNativeHandle se
                // propagerait telle quelle — ici, appelée aussi bien depuis
                // OpenAsync (ViewModel, capturée) que depuis OnWaveOutStopped lors
                // d'un changement automatique de subsong (PAS dans un try/catch côté
                // appelant, événement NAudio) : mieux vaut dégrader proprement.
                _log.LogWarning("ZXTune (natif): échec d'ouverture du subsong {Index} : {Message}",
                    _currentSubsong, ex.Message);
                IsPlayable = false;
                return;
            }

            // Le bouclage infini par défaut de zxtune doit être désactivé :
            // DemoBase gère lui-même l'avance de playlist / fin de lecture via
            // PlaybackFinished (cf. OnWaveOutStopped ci-dessous), comme avant
            // avec zxtune123.exe (qui lui ne bouclait jamais).
            _player.SetLooped(false);

            _waveOut = new WaveOutEvent { DesiredLatency = 200 };
            _waveOut.Volume = _masterVolume;
            // 2026-08-07 : SampleRate vient du player OUVERT (_player,
            // ZxNativeSubsongPlayer), pas de ZxNativeSubsongInfo (_subsongs) qui ne
            // porte que des métadonnées légères (titre/auteur/type/durée) — corrigé
            // après échec de build utilisateur (CS1061, SampleRate inexistant sur
            // ZxNativeSubsongInfo).
            WaveformOverview.SetDuration(ResolvedDuration(_currentSubsong), _player.SampleRate);
            _currentWaveProvider = new ZxTuneWaveProvider(_player, SampleBuffer, WaveformOverview);
            _waveOut.Init(_currentWaveProvider);
            _waveOut.PlaybackStopped += OnWaveOutStopped;
            _waveOut.Play();
            _state.IsPlaying = true;
            _state.IsPaused  = false;

            _pollCts = new CancellationTokenSource();
            _ = PollAsync(_pollCts.Token);
        }

        // Même logique d'auto-avance interne que ZXTunePlayer avant ce correctif
        // (et que UadePlayer.OnSubsongFinished) : ne lever PlaybackFinished qu'après
        // le DERNIER subsong, avancer silencieusement sinon — sans quoi le
        // ViewModel traiterait la fin d'un subsong intermédiaire comme la fin de
        // la PISTE entière et avancerait la playlist en pleine navigation subsong.
        private void OnWaveOutStopped(object? sender, StoppedEventArgs e)
        {
            // 2026-08-06, retour utilisateur ("zxtune n'est jamais testé pour les
            // formats inconnus") : le subsong qui vient de s'arrêter naturellement
            // (fin réelle, PAS un Stop() manuel — désabonné avant, cf. Stop()
            // ci-dessous) n'a-t-il RÉELLEMENT produit aucune trame audio ? Un
            // conteneur peut annoncer un subsong "valide" (Count >= 1) sans que son
            // rendu produise quoi que ce soit de réel — même principe que
            // UadePlayer._bytesDecoded (ExternalPlayers.cs, Read()).
            if (_currentWaveProvider is { FramesRendered: 0 })
                IsPlayable = false;

            if (_container != null && _currentSubsong + 1 < _subsongs.Count)
            {
                _currentSubsong++;
                _player?.Dispose();
                try
                {
                    _player = _container.OpenSubsong(_currentSubsong);
                }
                catch (InvalidOperationException ex)
                {
                    // cf. le même garde-fou dans Play() ci-dessus.
                    _log.LogWarning("ZXTune (natif): échec d'ouverture du subsong {Index} : {Message}",
                        _currentSubsong, ex.Message);
                    IsPlayable = false;
                    _state.IsPlaying = false;
                    SampleBuffer.Clear();
                    // cf. commentaire détaillé plus bas sur ce même appel : garantit que
                    // SoundtrackPlayerViewModel réévalue IsFormatUnsupported même si
                    // aucun tick de PollAsync n'a eu lieu avant cet arrêt quasi immédiat.
                    NotifyState();
                    PlaybackFinished?.Invoke(this, EventArgs.Empty);
                    return;
                }
                _player.SetLooped(false);
                _state.DurationSeconds = ResolvedDuration(_currentSubsong);

                // Nouveau WaveOutEvent — pas de ré-Init pendant la lecture (même
                // contrainte NAudio que UadePlayer/l'ancien ZXTunePlayer).
                _waveOut?.Dispose();
                _waveOut = new WaveOutEvent { DesiredLatency = 200 };
                _waveOut.Volume = _masterVolume;
                _waveOut.PlaybackStopped += OnWaveOutStopped;
                WaveformOverview.SetDuration(ResolvedDuration(_currentSubsong), _player.SampleRate);
                _currentWaveProvider = new ZxTuneWaveProvider(_player, SampleBuffer, WaveformOverview);
                _waveOut.Init(_currentWaveProvider);
                _waveOut.Play();
                return;
            }

            _state.IsPlaying = false;
            SampleBuffer.Clear();
            // 2026-08-06 : sans cet appel, un fichier "reconnu mais silencieux" (cf.
            // IsPlayable ci-dessus) peut atteindre la fin naturelle de son SEUL
            // subsong si vite (0 trame rendue dès le premier Read()) que PollAsync
            // n'a pas eu le temps de tourner ne serait-ce qu'une fois pendant que
            // _state.IsPlaying valait true — dans ce cas, aucun tick OnStateChanged
            // n'aurait jamais atteint SoundtrackPlayerViewModel, qui ne réévaluerait
            // donc jamais IsFormatUnsupported (resté à sa valeur "true = jouable"
            // posée juste après LoadAsync, avant que Play() ne tente réellement le
            // rendu) : l'oscilloscope resterait vide SANS le message "Format non
            // jouable", exactement le symptôme d'origine. NotifyState() publie l'état
            // final indépendamment de PollAsync — OnStateChanged (ViewModel) réévalue
            // IsFormatUnsupported/IsUadeFormat à CHAQUE appel, cf. son commentaire.
            NotifyState();
            PlaybackFinished?.Invoke(this, EventArgs.Empty);
        }

        public void Pause()
        {
            if (_waveOut?.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            {
                _waveOut.Pause();
                _state.IsPaused  = true;
                _state.IsPlaying = false;
                NotifyState();
            }
        }

        public void Stop()
        {
            _pollCts?.Cancel();
            // Désabonner AVANT Stop() — sinon Stop() → PlaybackStopped →
            // OnWaveOutStopped → avance au subsong suivant au lieu de s'arrêter
            // (même piège que UadePlayer.SelectSubsong).
            if (_waveOut != null)
                _waveOut.PlaybackStopped -= OnWaveOutStopped;
            _waveOut?.Stop();
            _waveOut?.Dispose(); _waveOut = null;
            _player?.Dispose();  _player  = null;
            _state.IsPlaying = _state.IsPaused = false;
            SampleBuffer.Clear();
        }

        // Le pont natif expose bien Zx_Seek (Seek(TimeSpan) sur ZxNativeSubsongPlayer),
        // contrairement à l'ancien process zxtune123.exe — mais ZXTunePlayer ne produit
        // jamais de vrais patterns (module "coquille vide", HasPatterns toujours faux
        // côté UI, cf. ZXTuneDecoder.DecodeAsync), donc aucun appelant ne fournit
        // d'orderIndex pertinent ici. Laissé en no-op comme avant ce correctif ;
        // câbler un vrai seek nécessiterait une barre de progression temporelle
        // dédiée (hors périmètre de cette demande).
        public void SeekToOrder(int orderIndex) { /* pas de seek par ordre pour ZXTune (pas de patterns) */ }

        private async Task PollAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _state.IsPlaying)
            {
                _state.PositionSeconds = _player?.Position.TotalSeconds ?? 0;
                NotifyState();
                await Task.Delay(40, ct).ConfigureAwait(false); // 25fps — suffisant pour UI
            }
        }

        private void NotifyState()
        {
            var copy = new ModelsPlaybackState
            {
                IsPlaying       = _state.IsPlaying,
                IsPaused        = _state.IsPaused,
                PositionSeconds = _state.PositionSeconds,
                DurationSeconds = _state.DurationSeconds,
                CurrentBpm      = _state.CurrentBpm,
                CurrentRow      = _state.CurrentRow,
            };
            StateChanged?.Invoke(this, copy);
        }

        public void Dispose()
        {
            Stop();
            _container?.Dispose();
            _container = null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ZXTuneDecoder — ITrackerDecoder pour les formats ZXTune
    // (crée un module minimal ; les métadonnées réelles sont lues par
    // ZXTunePlayer.LoadAsync via le conteneur natif zxtune.dll)
    // ════════════════════════════════════════════════════════════════════════

    public sealed class ZXTuneDecoder : ITrackerDecoder
    {
        public string   FormatName          => "ZXTune (Amiga/ZX/C64/Atari/…)";
        public string[] SupportedExtensions => [.. ZXTunePlayer.SupportedExtensions];

    public bool CanDecode(Stream stream) => true;  // on fait confiance à l'extension

    /// <summary>
    /// Retourne false si l'extension appartient exclusivement à UADE —
    /// pour éviter que ZXTune intercepte les fichiers UADE dans la détection par contenu.
    /// </summary>
    public bool CanDecodeFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (UadePlayer.SupportedExtensions.Contains(ext)) return false;
        return true;
    }

        public async Task<TrackerModule> DecodeAsync(Stream stream, string filePath,
            CancellationToken ct = default)
        {
            // Crée un module minimal — les métadonnées réelles (Type, Title, Author)
            // seront remplies par ZXTunePlayer.LoadAsync via le conteneur natif.
            var module = new TrackerModule
            {
                FilePath = filePath,
                FileSize = stream.Length,
                Title    = Path.GetFileNameWithoutExtension(filePath),
                Format   = TrackerFormat.Unknown,
                Channels = 2,
            };
            return module;
        }
    }



// ════════════════════════════════════════════════════════════════════════════
// UADE Player — formats Amiga exotiques via libuade.dll (pont natif P/Invoke)
// (ArtOfNoise, SoundMon, JamCracker, TFMX, Hippel, Custom, ~150 formats)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Lecteur UADE — Unix Amiga Delitracker Emulator.
/// Utilise libuade.dll (pont natif compilé par l'utilisateur) + uadecore.exe
/// (process enfant spawné par la DLL elle-même — cf. commentaire en tête de
/// UadeNative.cs pour le détail de cette architecture en deux parties).
///
/// 2026-08-06, retour utilisateur : "j'ai fait de même avec uade. j'ai crée
/// une dll. voici le projet [UadeWpfPlayer.zip]. tu verras qu'on stocke
/// maintenant les durées dans une base duration.db et qu'il y a un système
/// pour calculer les durées des songs et subsongs". Remplace entièrement
/// l'ancienne implémentation (ci-dessous avant ce correctif) qui lançait un
/// process uade123.exe PAR SUBSONG dès LoadAsync (`-e raw -f - --stderr -1
/// -s N --disable-timeouts`, streaming stdout) et copiait les fichiers
/// compagnons (TFMX mdat/smpl, Thomas Hermann thm/smp, Dirk Bialluch tpu/smp)
/// avec un renommage GUID pour éviter les collisions entre subsongs.
///
/// Le pont natif (TrackerPlayer.Core/Players/UadeNative.cs, inspiré du
/// projet UadeWpfPlayer fourni par l'utilisateur) apporte :
///   - Un seul "uade_state" natif par instance de UadePlayer, réutilisé pour
///     tous les subsongs (uade_stop+uade_play pour changer de subsong) au
///     lieu d'un process + pipe stdout PAR subsong ouverts dès LoadAsync
///     (l'ancien code lançait N process au chargement même si un seul
///     subsong à la fois est réellement écouté).
///   - Résolution des fichiers compagnons par simple changement de
///     répertoire courant du process (SetCwdToFileDir + nom de fichier nu),
///     comme le fait la référence "uade123" en ligne de commande — plus
///     aucune copie de fichier avec renommage GUID, donc plus besoin du
///     nettoyage au démarrage des copies orphelines (cf. RESUME_PROJET.md).
///   - Une vraie mesure de durée par sous-chanson, mise en cache dans un
///     fichier SQLite séparé (cf. UadeDurationDatabase.cs) et scannée
///     automatiquement en arrière-plan à l'ouverture d'un fichier — chose
///     impossible à faire proprement avec le process externe (aucune API
///     structurée de fin de lecture, seulement du texte à parser).
///
/// PRÉREQUIS :
///   Compiler libuade.dll + uadecore.exe (fourni par l'utilisateur, x64) et
///   les placer dans Externals/UADE/ (ou le répertoire de l'application),
///   à côté des ressources UADE existantes (eagleplayer.conf, uaerc, score,
///   players/). Aucun téléchargement automatique — cf.
///   EmulatorDownloadCatalog.cs, l'entrée "UADE" (uade123.exe Cygwin) a été
///   retirée du catalogue Externals à l'occasion de ce correctif.
///
/// FORMATS :
///   ~150 formats Amiga exotiques non couverts par libopenmpt/zxtune :
///   ArtOfNoise, SoundMon, JamCracker, TFMX, JochenHippel, Hippel-COSO,
///   SonicArranger, SidMon, MusicMaker, FutureComposer, etc.
/// </summary>

/// <summary>
/// Formats UADE dont le module est scindé en deux fichiers physiques distincts, qui
/// doivent être présents CÔTE À CÔTE dans le même dossier pour qu'UADE les trouve — le
/// "compagnon" n'est jamais passé en argument, UADE le cherche lui-même à côté du
/// fichier principal par convention de nommage. "mdat."+"smpl." pour TFMX (2026-07-30) ;
/// 2026-07-31, retour utilisateur : "pour modland, tout comme le tfmx, le format thomas
/// hermann a besoin de 2 fichiers : smp.x et thm.x [...] les deposer dans le repertoire
/// d'uade et lancer le fichier thm.x" — "thm."+"smp." ajouté sur le même principe. Liste
/// volontairement générique (même esprit que CompanionFilePairs dans ReleaseViewModels.cs,
/// pendant côté releases DAT) pour couvrir d'éventuels autres cas futurs sans toucher au
/// reste de la logique de copie/nettoyage ci-dessous. Classe PUBLIQUE (pas internal) : lue
/// aussi par DemoBase.App.Services.ModlandService (téléchargement du compagnon depuis le
/// catalogue Modland avant même que UADE ne s'en mêle) — une seule source de vérité pour
/// la liste des formats à deux fichiers, partagée entre téléchargement et lecture.
///
/// 2026-08-06 : cette classe reste utilisée par ModlandService (téléchargement) même si
/// UadePlayer/le pont natif ne s'en sert plus lui-même pour de la copie de fichiers — la
/// résolution des compagnons à la lecture se fait maintenant nativement par UADE lui-même
/// via le répertoire courant du process (cf. UadePlayer.SetCwdToFileDir), plus par une
/// copie explicite côté C#. IsMainFile/Match restent donc la seule source de vérité pour
/// savoir QUELS formats ont besoin d'un compagnon téléchargé.
/// </summary>
public static class UadeCompanionFormats
{
    public static readonly (string MainPrefix, string CompanionPrefix)[] Pairs =
    {
        ("mdat.", "smpl."), // TFMX
        ("thm.",  "smp."),  // Thomas Hermann
        // 2026-07-31, retour utilisateur : "autre format qui necessite 2 fichiers
        // 'Dirk Bialluch' les fichiers smp.* et tpu.* . il faut jouer le tpu.*" —
        // même mécanisme que Thomas Hermann ci-dessus (compagnon "smp." aussi, mais
        // préfixe principal différent "tpu." → aucun conflit, Match()/IsMainFile()
        // ne comparent que le préfixe PRINCIPAL).
        ("tpu.",  "smp."),  // Dirk Bialluch
        // 2026-08-07, retour utilisateur ("les fichiers sjs.* doivent etre accompagné
        // des fichiers smp.*, tous comme les tfmx") : même mécanisme, préfixe
        // principal "sjs." (encore différent, toujours aucun conflit).
        ("sjs.",  "smp."),
    };

    /// <summary>Vrai si <paramref name="fileName"/> est le fichier "principal" d'un format
    /// à deux fichiers connu (ex. "mdat.xxx", "thm.xxx").</summary>
    public static bool IsMainFile(string fileName)
    {
        foreach (var (main, _) in Pairs)
            if (fileName.StartsWith(main, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Retourne (préfixe principal, préfixe compagnon) si <paramref name="fileName"/>
    /// est un fichier principal connu, sinon null.</summary>
    public static (string MainPrefix, string CompanionPrefix)? Match(string fileName)
    {
        foreach (var pair in Pairs)
            if (fileName.StartsWith(pair.MainPrefix, StringComparison.OrdinalIgnoreCase))
                return pair;
        return null;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// UadePlayer — formats Amiga exotiques via libuade.dll (pont natif)
// ════════════════════════════════════════════════════════════════════════════

public sealed class UadePlayer : ITrackerPlayer, IWaveProvider
{
    // ── Résolution des chemins natifs (analogue à ZXTunePlayer/SndhPlayer) ──

    /// <summary>Chemin vers uadecore.exe, passé à libuade.dll via l'option
    /// UC_UADECORE_FILE (process enfant spawné PAR la DLL, pas par nous).
    /// Renseigné par App.xaml.cs/ConfigureExternalPaths.</summary>
    public static string UadecoreExePath { get; set; } = "uadecore.exe";

    /// <summary>Dossier UC_BASE_DIR (eagleplayer.conf/uaerc/score/players/).
    /// Si non renseigné explicitement, déduit du dossier de
    /// <see cref="UadecoreExePath"/> une fois résolu.</summary>
    public static string? BaseDirOverride { get; set; }

    // 2026-08-06, retour utilisateur ("on avait mis une case à coché et un slider pour
    // la separation stereo pour uade. peux tu le rajouter dans les preferences et le
    // gérer au niveau du player ?") — repris du projet UadeWpfPlayer fourni
    // (UC_PANNING_VALUE) : l'Amiga (chip Paula) envoie chaque voie en dur à 100% gauche
    // ou droite, il n'y a jamais eu de vrai "joint stereo" côté matériel, d'où un rendu
    // très tranché sur des enceintes/casques modernes. Statiques (comme
    // UadecoreExePath/BaseDirOverride ci-dessus) car un nouveau UadePlayer est créé à
    // chaque ouverture de fichier (TrackerService.OpenAsync) — la préférence doit donc
    // survivre au-delà d'une seule instance. Renseignées depuis les préférences
    // utilisateur par DemoBase.App (SoundtrackPlayerViewModel), pas lues directement
    // depuis PreferencesService ici (TrackerPlayer.Core ne référence pas DemoBase.Data).
    // Désactivé par défaut (son Amiga brut, comportement historique).
    public static bool   PanningEnabled { get; set; } = false;
    /// <summary>0.0 = aucun effet (identique à désactivé), 2.0 = mixage complet en
    /// mono ; 0.7 = réglage historique par défaut d'UADE quand l'effet est actif.</summary>
    public static double PanningAmount  { get; set; } = 0.7;

    // 2026-08-06, retour utilisateur ("les musiques venant de uade sont generalement
    // moins forte en volume que les autres (zxtune ou openmpt). il me semble qu'il a
    // un replay gain dans la DLL uade. peux tu regarder si il est possible d'augmenter
    // par defaut le son de uade ?") — confirmé : libuade expose bien une option de gain
    // (UC_GAIN, cf. UadeNativeInterop.Option), équivalent à l'option --gain d'uade123
    // (multiplicateur linéaire appliqué en sortie ; 1.0 = neutre, valeur par défaut de
    // libuade elle-même quand l'option n'est pas positionnée — c'est ce défaut "neutre"
    // qui explique le volume plus faible perçu face à ZXTune/libopenmpt, dont les
    // formats natifs ne subissent pas cette même atténuation d'origine). Statique comme
    // PanningEnabled/PanningAmount ci-dessus (un nouveau UadePlayer est créé à chaque
    // ouverture de fichier). 1.8 choisi initialement comme compromis empirique, puis
    // ramené à 1.6 le 2026-08-07 (retour utilisateur : "fixe le gain à 1.6 [...] pour
    // les nouvelles installations") : perceptible sans clipper sur les morceaux déjà
    // proches du maximum (UC_GAIN amplifie après le rendu, un gain trop élevé peut
    // saturer sur les pistes qui utilisent déjà tout le range dynamique). Réglable
    // depuis les Préférences (section UADE) — cette valeur n'est donc que le défaut
    // des NOUVELLES installations, pour les utilisateurs qui n'ouvrent jamais cet
    // écran.
    public static double GainAmount { get; set; } = 1.6;

    private const int SampleRate = 44100;
    private const int DefaultSilenceTimeoutSeconds = 20;
    // 2026-08-06, repris du projet fourni : cap par défaut relevé de 180s à 600s
    // (certains TFMX, ex. Turrican II, dépassaient l'ancien défaut).
    private const int DefaultSubsongTimeoutSeconds = 600;

    internal static string ResolveUadecoreExePath()
    {
        var exeDir = AppContext.BaseDirectory;
        var name   = UadecoreExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? UadecoreExePath : UadecoreExePath + ".exe";
        var local  = Path.Combine(exeDir, name);
        if (File.Exists(local)) return local;
        if (File.Exists(UadecoreExePath)) return UadecoreExePath;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir.Trim(), "uadecore.exe");
            if (File.Exists(full)) return full;
        }
        return UadecoreExePath; // laisse échouer à la création du state, avec un message clair
    }

    internal static string ResolveBaseDir()
    {
        if (!string.IsNullOrWhiteSpace(BaseDirOverride) && Directory.Exists(BaseDirOverride))
            return BaseDirOverride!;
        var dir = Path.GetDirectoryName(ResolveUadecoreExePath());
        return string.IsNullOrEmpty(dir) ? AppContext.BaseDirectory : dir;
    }

    /// <summary>Vrai si libuade.dll est chargeable ET qu'uadecore.exe est trouvable
    /// (les deux sont nécessaires à la création d'un state — cf. UC_UADECORE_FILE).</summary>
    public static bool IsAvailable =>
        UadeNativeInterop.IsAvailable && File.Exists(ResolveUadecoreExePath());

    public static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ".cust", ".custom", ".intro",
        ".tfmx", ".tfx", ".mdat",
        ".jc",
        ".bp", ".bp3",
        ".aon", ".aon4", ".aon8",
        // 2026-07-31, retour utilisateur ("généralise-le au format que libopenmpt peut
        // lire") : Future Composer (.fc/.fc13/.fc14/.smod) est lisible par libopenmpt
        // depuis sa version 0.8.0, AVEC de vrais patterns — contrairement à UADE, qui
        // ne produit jamais qu'un module "coquille vide" (cf. UadeDecoder.DecodeAsync,
        // même limite que ZXTuneDecoder). Retiré d'ici, ajouté à LibopenmptExtensions
        // (NativeTrackerPlayer.cs), même schéma que .psm/.gtk/.stp.
        ".hpl", ".hip", ".hip7", ".coso",
        ".sa",
        ".sm", ".sm1", ".sm2",
        ".mmu", ".mmm",
        ".dl", ".dl2",
        ".fred",
        ".rk",
        ".tom",
        ".4q", ".4v",
        // 2026-07-31, retour utilisateur : ".sfx" (SoundFX/MultiMedia Sound) est lisible
        // par libopenmpt — même conflit qu'avec Future Composer ci-dessus, même remède.
        ".mk2",
        ".nt",
        // 2026-07-30, retour utilisateur ("à ouvrir avec libopenmpt et les patterns
        // FT2") : .digi (DigiBooster non-Pro) était listé ici ET dans
        // LibopenmptExtensions (NativeTrackerPlayer.cs) — conflit réel, pas
        // seulement un manque. Comme UadeDecoder.SupportedExtensions correspond
        // directement à cette liste, le tout premier passage de sélection de
        // décodeur (match d'extension exact, avant même les fallbacks) attrapait
        // .digi pour UADE avant que libopenmpt ait sa chance. Retiré ici.
        ".dm", ".dm2",
        ".ems", ".emsv6",
        ".pum",
        // 2026-07-31, retour utilisateur : ".ims" (Images Music System) est lisible par
        // libopenmpt — même conflit/remède que Future Composer/.sfx ci-dessus.
        ".mug", ".mug2",
        ".sc68",
        ".stk",
        ".uds",
        ".sog",
        // 2026-07-30, retour utilisateur ("il va falloir ajuster les fichiers
        // jouables par uade ou zxtune... le nombre de format est enorme sur
        // modland") : formats Amiga natifs identifiés dans le catalogue Modland
        // réel (338 formats distincts) qui n'étaient routés vers AUCUN backend —
        // ni libopenmpt (pas des formats trackers "standard"), ni ZXTune (pas dans
        // sa liste), ni UADE jusqu'ici. Avec le garde-fou ajouté ce même jour
        // (TrackerService.OpenAsync : DecodeAsync protégé par try/catch, repli sur
        // métadonnées minimales), une entrée ajoutée ici par erreur ne peut plus
        // faire planter la lecture — au pire le fichier ne joue simplement pas.
        // .dw/.bd en particulier corrigent une incohérence déjà présente dans le
        // commentaire de DemoBase.Core.DTOs.TrackerExtensions ("David Whittaker
        // format — joué par UADE (.dw)" / "Ben Daglish format — joué par UADE
        // (*.bd)") : ces extensions n'étaient en réalité jamais listées ici.
        ".dw",       // David Whittaker (121 pistes Modland)
        ".bd",       // Ben Daglish (44 pistes Modland)
        ".eup",      // Euphony (1498 pistes Modland)
        ".dln",      // Dave Lowe New — extension réelle Modland (.dl2 déjà listé
                     // ci-dessus ne correspond à aucune piste du catalogue réel)
        // 2026-07-31, retour utilisateur : ".symmod" (Symphonie/Symphonie Pro) est
        // lisible par libopenmpt — retiré d'ici, ajouté à LibopenmptExtensions
        // (même conflit/remède que Future Composer/.sfx/.ims ci-dessus).
        ".ml",       // Musicline Editor (817 pistes Modland)
        ".cus",      // Delitracker Custom, forme "extension" plutôt que préfixe
                     // "cust." déjà géré par UadeDecoder.KnownPrefixes (414 pistes)
        ".fuz",
        ".emod",
        ".kris",
        ".ymst",
        ".nos",
        ".sun",  // SUNtronic (Suntronic custom) — format Amiga des Sunriders (1989)
    };

    // ── ITrackerPlayer ────────────────────────────────────────────────
    public TrackerFormat[]                  SupportedFormats  => [];
    public event EventHandler<ModelsPlaybackState>? StateChanged;
    public event EventHandler?              PlaybackFinished;
    public ModelsPlaybackState              CurrentState      => _state;
    public SampleRingBuffer                 SampleBuffer      { get; } = new(8192);

    // 2026-08-07, demande utilisateur : vue d'ensemble complète de la forme d'onde
    // sous l'oscilloscope, remplie progressivement pendant la lecture (uade_read()
    // synthétise en temps réel, pas de fichier WAV intermédiaire). Redimensionnée
    // (SetDuration) à chaque fois que la durée du subsong courant est (ré)connue,
    // cf. ApplyKnownDurationIfAny.
    public WaveformOverviewBuffer WaveformOverview { get; } = new WaveformOverviewBuffer();
    public float MasterVolume
    {
        get => _masterVolume;
        set { _masterVolume = value; if (_waveOut != null) _waveOut.Volume = value; }
    }

    /// <summary>Événement déclenché au début de LoadAsync (compatibilité API — la
    /// génération n'est plus qu'un uade_play() natif quasi instantané, plus de
    /// process/fichier WAV à générer, mais l'événement reste utile si une future
    /// UI veut un indicateur de chargement).</summary>
    public event EventHandler? GenerationStarted;
    /// <summary>Événement déclenché à la fin de LoadAsync.</summary>
    public event EventHandler? GenerationCompleted;

    // 2026-08-01, retour utilisateur ("j'ai testé pour les fichiers non jouables mais
    // l'oscilloscope vide s'affiche encore à l'écran malgré le 'unknown format de
    // uade'") : optimiste par défaut (true), passe à false uniquement quand uade_play()
    // retourne explicitement PLAY_CANNOT_PLAY/PLAY_FATAL_ERROR (signal natif direct,
    // bien plus fiable que l'ancienne heuristique "aucun octet PCM produit" du process
    // externe).
    public bool IsPlayable { get; private set; } = true;

    public int SubsongCount        => Math.Max(1, SubsongMax - SubsongMin + 1);
    public int CurrentSubsongIndex => _currentSubsongIndex;
    public int SubsongMin { get; private set; }
    public int SubsongMax { get; private set; }

    public WaveFormat WaveFormat { get; } = new WaveFormat(SampleRate, 16, 2);

    // ── State ─────────────────────────────────────────────────────────
    private readonly object     _lock = new();
    private IntPtr               _engineState;
    private ModelsPlaybackState  _state        = new();
    private float                _masterVolume = 1.0f;
    private readonly ILogger     _log;
    private WaveOutEvent?        _waveOut;
    private CancellationTokenSource? _pollCts;
    private CancellationTokenSource? _durationScanCts;
    private TrackerModule?       _module;
    private long                 _bytesDecoded;
    private bool                 _reachedEnd;
    private int                  _currentSubsongIndex;
    /// <summary>Valeur de panoramique effectivement utilisée pour créer <see cref="_engineState"/>
    /// (copie figée de PanningEnabled/PanningAmount au moment de la création — cf.
    /// SetPanning pour changer cette valeur en cours de route).</summary>
    private double?               _panning;

    /// <summary>Durées connues (réelles ou plafonnées) par numéro de sous-chanson NATIF
    /// (pas l'index 0-based de <see cref="CurrentSubsongIndex"/>) — alimenté par le
    /// cache SQLite à l'ouverture, puis complété au fil des scans en arrière-plan.</summary>
    private readonly Dictionary<int, double> _knownDurations = new();
    private double? _knownDurationForCurrentSubsong;

    public UadePlayer(ILogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
    }

    private void EnsureState()
    {
        if (_engineState != IntPtr.Zero) return;
        _panning = PanningEnabled ? PanningAmount : (double?)null;
        _engineState = CreateState(ResolveBaseDir(), ResolveUadecoreExePath(),
            SampleRate, DefaultSilenceTimeoutSeconds, DefaultSubsongTimeoutSeconds, _panning, GainAmount);
    }

    /// <summary>
    /// Change le panoramique stéréo (null = désactivé, son Amiga brut ; 0.0-2.0 =
    /// activé, cf. <see cref="PanningAmount"/>) et recrée le uade_state natif — l'option
    /// UC_PANNING_VALUE n'est lue par libuade qu'à la création de l'état, comme
    /// UC_BASE_DIR/UC_UADECORE_FILE, donc aucun moyen de la changer "à chaud" sur un
    /// état déjà créé. Arrête la lecture en cours si nécessaire (recréer l'état sous
    /// NAudio pendant qu'il lit dessus serait une vraie course) ; ne relance PAS
    /// automatiquement la lecture — c'est à l'appelant (SoundtrackPlayerViewModel) de le
    /// faire s'il le souhaite, après avoir constaté que la lecture était en cours.
    /// </summary>
    public void SetPanning(double? amount)
    {
        if (amount is < 0.0 or > 2.0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Panning doit être entre 0.0 et 2.0.");

        Stop();
        lock (_lock)
        {
            if (amount == _panning) return;
            _panning = amount;
            if (_engineState != IntPtr.Zero)
            {
                UadeNativeInterop.uade_cleanup_state(_engineState);
                _engineState = IntPtr.Zero;
            }
            _engineState = CreateState(ResolveBaseDir(), ResolveUadecoreExePath(),
                SampleRate, DefaultSilenceTimeoutSeconds, DefaultSubsongTimeoutSeconds, _panning, GainAmount);

            // L'état vient d'être recréé "à froid" — recharger le module courant pour
            // que Play() (rappelé par l'appelant si besoin) reparte sur un état valide
            // avec le bon intervalle de subsongs, plutôt que sur un state jamais chargé.
            if (_module != null)
            {
                SetCwdToFileDir(_module.FilePath);
                UadeNativeInterop.uade_stop(_engineState);
                int nativeSubsong = SubsongMin + _currentSubsongIndex;
                UadeNativeInterop.uade_play(Path.GetFileName(_module.FilePath), nativeSubsong, _engineState);
                UadeNativeInterop.uade_stop(_engineState);
            }
        }
    }

    /// <summary>
    /// Crée un config + state libuade indépendant — utilisé pour l'instance
    /// elle-même (EnsureState) ET pour chaque worker du scan de durées en
    /// arrière-plan (chacun avec son propre uadecore.exe enfant, cf.
    /// ScanSubsongDurationsParallel), sur le même principe que le projet fourni.
    /// </summary>
    private static IntPtr CreateState(string basedir, string uadecoreExePath, int sampleRate,
        int silenceTimeoutSeconds, int subsongTimeoutSeconds, double? panning, double gain = 1.0)
    {
        IntPtr config = UadeNativeInterop.uade_new_config();
        if (config == IntPtr.Zero)
            throw new InvalidOperationException("uade_new_config a échoué");

        UadeNativeInterop.uade_config_set_option(config, UadeNativeInterop.Option.UC_BASE_DIR, basedir);
        UadeNativeInterop.uade_config_set_option(config, UadeNativeInterop.Option.UC_UADECORE_FILE, uadecoreExePath);
        UadeNativeInterop.uade_config_set_option(config, UadeNativeInterop.Option.UC_FREQUENCY, sampleRate.ToString());

        // Beaucoup de morceaux Amiga bouclent à l'infini et ne se terminent jamais
        // d'eux-mêmes — ces timeouts forcent une "fin de morceau" après N secondes de
        // silence ou un cap fixe par sous-chanson, aussi bien pour la lecture normale
        // que pour le scan de durées (sinon un scan pourrait bloquer indéfiniment sur
        // une sous-chanson qui boucle).
        UadeNativeInterop.uade_config_set_option(config, UadeNativeInterop.Option.UC_ENABLE_TIMEOUTS, "1");
        UadeNativeInterop.uade_config_set_option(config, UadeNativeInterop.Option.UC_SILENCE_TIMEOUT_VALUE,
            silenceTimeoutSeconds.ToString());
        UadeNativeInterop.uade_config_set_option(config, UadeNativeInterop.Option.UC_SUBSONG_TIMEOUT_VALUE,
            subsongTimeoutSeconds.ToString());

        // Sans cette option, libuade enchaîne automatiquement sur la sous-chanson
        // SUIVANTE dès que celle demandée atteint sa fin naturelle — jouer/scanner la
        // sous-chanson 0 continuerait donc silencieusement dans la 1, la 2, etc. On
        // veut toujours une sous-chanson isolée par Play()/scan, et gérer nous-mêmes
        // les changements de sous-chanson (cf. OnPlaybackStopped ci-dessous).
        UadeNativeInterop.uade_config_set_option(config, UadeNativeInterop.Option.UC_ONE_SUBSONG, "1");

        if (panning.HasValue)
        {
            UadeNativeInterop.uade_config_set_option(config, UadeNativeInterop.Option.UC_PANNING_VALUE,
                panning.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // 2026-08-06, retour utilisateur : cf. commentaire de GainAmount ci-dessus.
        // 1.0 = comportement natif de libuade (aucun changement) — c'est la valeur
        // utilisée par les workers de scan de durées ci-dessous (ScanSubsongDurationsParallel),
        // pour lesquels le rendu audio n'est jamais entendu et où toucher au gain n'aurait
        // aucun sens. N'écrit l'option que si elle diffère du défaut natif, par cohérence
        // avec le style "n'écrit que ce qui change" déjà utilisé pour UC_PANNING_VALUE.
        if (gain != 1.0)
        {
            UadeNativeInterop.uade_config_set_option(config, UadeNativeInterop.Option.UC_GAIN,
                gain.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        IntPtr state = UadeNativeInterop.uade_new_state(config);
        if (state == IntPtr.Zero)
            throw new InvalidOperationException(
                "uade_new_state a échoué — vérifier basedir (eagleplayer.conf/uaerc/score/players) et le chemin d'uadecore.exe.");
        return state;
    }

    /// <summary>
    /// Certains formats Amiga (TFMX, Thomas Hermann, Dirk Bialluch — cf.
    /// UadeCompanionFormats) chargent un second fichier compagnon au moment de
    /// la lecture. Le player Amiga demande ce fichier par nom RELATIF, résolu
    /// par libuade contre le répertoire courant du PROCESS — pas contre le
    /// dossier du fichier ouvert. On pointe donc le répertoire courant sur le
    /// dossier du fichier avant chaque uade_play(), et on ne passe que le nom
    /// de fichier nu (jamais le chemin complet — un ':' dedans, comme dans
    /// "C:\...", serait interprété comme un séparateur de volume Amiga par
    /// libuade et ferait échouer la résolution avec "unknown amiga volume").
    /// </summary>
    private static void SetCwdToFileDir(string fname)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(fname));
        if (!string.IsNullOrEmpty(dir))
            Directory.SetCurrentDirectory(dir);
    }

    public async Task LoadAsync(TrackerModule module, CancellationToken ct = default)
    {
        _module = module;
        Stop();
        _durationScanCts?.Cancel();
        _knownDurations.Clear();
        _knownDurationForCurrentSubsong = null;
        _currentSubsongIndex = 0;

        GenerationStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            EnsureState();

            string formatName = "", moduleName = "", playerName = "";
            lock (_lock)
            {
                SetCwdToFileDir(module.FilePath);
                UadeNativeInterop.uade_stop(_engineState);
                int result = UadeNativeInterop.uade_play(Path.GetFileName(module.FilePath), -1, _engineState);
                IsPlayable = result == UadeNativeInterop.PLAY_OK;
                if (!IsPlayable)
                {
                    UadeNativeInterop.uade_stop(_engineState);
                    throw new InvalidOperationException(
                        "UADE n'a pas reconnu ce fichier (format non supporté ou player manquant).");
                }

                SubsongMin = UadeNativeInterop.uade_net_get_subsong_min(_engineState);
                SubsongMax = UadeNativeInterop.uade_net_get_subsong_max(_engineState);
                int def    = UadeNativeInterop.uade_net_get_subsong_cur(_engineState);
                _currentSubsongIndex = Math.Max(0, def - SubsongMin);

                var fmt    = new StringBuilder(256);
                var mod    = new StringBuilder(256);
                var player = new StringBuilder(256);
                UadeNativeInterop.uade_net_get_formatname(_engineState, fmt, fmt.Capacity);
                UadeNativeInterop.uade_net_get_modulename(_engineState, mod, mod.Capacity);
                UadeNativeInterop.uade_net_get_playername(_engineState, player, player.Capacity);
                formatName = fmt.ToString();
                moduleName = mod.ToString();
                playerName = player.ToString();

                // Repli sur la détection de format de libuade elle-même quand le player
                // Amiga (code 68k) ne signale pas son format/module — mécanisme optionnel
                // ("AMIGAMSG_FORMATNAME" etc.) que beaucoup de players n'implémentent pas
                // (ex. TFMX-7V), même en cas de lecture réussie.
                if (string.IsNullOrEmpty(formatName) || string.IsNullOrEmpty(playerName))
                {
                    var extBuf    = new StringBuilder(64);
                    var playerBuf = new StringBuilder(256);
                    int matched = UadeNativeInterop.uade_net_detect_format(module.FilePath, _engineState,
                        extBuf, extBuf.Capacity, playerBuf, playerBuf.Capacity, out _);
                    if (matched != 0)
                    {
                        if (string.IsNullOrEmpty(formatName)) formatName = extBuf.ToString();
                        if (string.IsNullOrEmpty(playerName))  playerName = playerBuf.ToString();
                    }
                }

                // État laissé IDLE : Play() refera son propre uade_stop()+uade_play() frais
                // (comme GetSubsongRange dans le projet fourni — ce uade_play() initial ne
                // sert qu'à lire les métadonnées/l'intervalle de sous-chansons).
                UadeNativeInterop.uade_stop(_engineState);
            }

            if (!string.IsNullOrWhiteSpace(formatName)) module.FormatName = formatName;
            // Nommage Amiga par préfixe (mdat.xxx, smpl.xxx), pas par suffixe — le titre
            // par défaut posé par UadeDecoder.DecodeAsync (nom de fichier) reste donc
            // préférable à un Path.GetFileNameWithoutExtension ici ; on ne le remplace
            // que si UADE a un vrai nom de module à proposer.
            if (!string.IsNullOrWhiteSpace(moduleName)) module.Title = moduleName;

            _bytesDecoded = 0;
            _reachedEnd   = false;
            _state = new ModelsPlaybackState { DurationSeconds = 0, CurrentBpm = 125 };
            module.DurationSeconds = 0;

            _log.LogInformation("UADE (natif): '{File}' — {N} subsong(s) [{Min}..{Max}], format={Fmt} player={Player}",
                module.FilePath, SubsongCount, SubsongMin, SubsongMax, formatName, playerName);

            // Cache SQLite (rapide, synchrone) — applique immédiatement les durées déjà
            // connues d'un scan précédent, avant même de lancer un éventuel scan.
            TryApplyCachedDurations(module);

            // Scan de durées automatique en arrière-plan (2026-08-06, retour utilisateur :
            // choix explicite "automatique" plutôt qu'un bouton dédié) — ne bloque jamais
            // LoadAsync ni la lecture ; ne fait rien si le cache couvre déjà tout l'intervalle
            // de sous-chansons pour le cap configuré (cf. TryApplyCachedDurations ci-dessus).
            _durationScanCts = new CancellationTokenSource();
            var scanCt = _durationScanCts.Token;
            _ = Task.Run(() => ScanAndCacheDurationsAsync(module, scanCt), scanCt);
        }
        finally
        {
            GenerationCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Play()
    {
        if (_module is null) return;

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        lock (_lock)
        {
            SetCwdToFileDir(_module.FilePath);
            UadeNativeInterop.uade_stop(_engineState);
            int nativeSubsong = SubsongMin + _currentSubsongIndex;
            int result = UadeNativeInterop.uade_play(Path.GetFileName(_module.FilePath), nativeSubsong, _engineState);
            IsPlayable = result == UadeNativeInterop.PLAY_OK;
            if (!IsPlayable) return;
            _bytesDecoded = 0;
            _reachedEnd   = false;
        }

        _waveOut = new WaveOutEvent { DesiredLatency = 200 };
        _waveOut.Volume = _masterVolume;
        _waveOut.Init(this);
        _waveOut.PlaybackStopped += OnPlaybackStopped;
        _waveOut.Play();
        _state.IsPlaying = true;
        _state.IsPaused  = false;
        ApplyKnownDurationIfAny();

        _pollCts = new CancellationTokenSource();
        _ = PollAsync(_pollCts.Token);
    }

    // Même logique d'auto-avance interne qu'avant ce correctif (et que ZXTunePlayer) :
    // ne lever PlaybackFinished qu'après le DERNIER subsong, avancer silencieusement
    // sinon — sans quoi le ViewModel traiterait la fin d'un subsong intermédiaire comme
    // la fin de la PISTE entière et avancerait la playlist en pleine navigation subsong.
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_module != null && _currentSubsongIndex + 1 < SubsongCount)
        {
            _currentSubsongIndex++;
            bool ok;
            lock (_lock)
            {
                SetCwdToFileDir(_module.FilePath);
                UadeNativeInterop.uade_stop(_engineState);
                int nativeSubsong = SubsongMin + _currentSubsongIndex;
                int result = UadeNativeInterop.uade_play(Path.GetFileName(_module.FilePath), nativeSubsong, _engineState);
                ok = result == UadeNativeInterop.PLAY_OK;
                _bytesDecoded = 0;
                _reachedEnd   = false;
            }
            if (ok)
            {
                // Nouveau WaveOutEvent — on ne peut pas re-Init pendant la lecture
                // (même contrainte NAudio que ZXTunePlayer/l'ancien UadePlayer).
                _waveOut?.Dispose();
                _waveOut = new WaveOutEvent { DesiredLatency = 200 };
                _waveOut.Volume = _masterVolume;
                _waveOut.PlaybackStopped += OnPlaybackStopped;
                _waveOut.Init(this);
                _waveOut.Play();
                ApplyKnownDurationIfAny();
                return;
            }
        }

        _state.IsPlaying = false;
        SampleBuffer.Clear();
        PlaybackFinished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saute au subsong N (0-based, cf. contrat ITrackerPlayer).</summary>
    public void SelectSubsong(int index)
    {
        if (index < 0 || index >= SubsongCount) return;
        bool wasPlaying = _state.IsPlaying;

        // Désabonner AVANT Stop() — sinon PlaybackStopped → OnPlaybackStopped → avance
        // au subsong suivant au lieu de s'arrêter proprement ici.
        if (_waveOut != null)
            _waveOut.PlaybackStopped -= OnPlaybackStopped;

        _pollCts?.Cancel();
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _currentSubsongIndex = index;
        if (wasPlaying) Play();
        else ApplyKnownDurationIfAny();
    }

    public void Pause()
    {
        if (_waveOut?.PlaybackState == NAudio.Wave.PlaybackState.Playing)
        {
            _waveOut.Pause();
            _state.IsPaused = true; _state.IsPlaying = false;
            NotifyState();
        }
    }

    public void Stop()
    {
        _pollCts?.Cancel();
        // Même piège que SelectSubsong ci-dessus : désabonner AVANT Stop() pour que ce
        // Stop() soit un vrai arrêt (et non un passage au subsong suivant).
        if (_waveOut != null)
            _waveOut.PlaybackStopped -= OnPlaybackStopped;
        _waveOut?.Stop();
        _waveOut?.Dispose(); _waveOut = null;
        lock (_lock)
        {
            if (_engineState != IntPtr.Zero)
                UadeNativeInterop.uade_stop(_engineState);
        }
        _state.IsPlaying = _state.IsPaused = false;
        SampleBuffer.Clear();
    }

    // UADE ne produit jamais de vrais patterns (module "coquille vide", cf.
    // UadeDecoder.DecodeAsync) — HasPatterns reste toujours faux côté UI pour ce
    // format, donc aucun appelant ne fournit d'orderIndex pertinent ici. Le pont natif
    // expose bien uade_seek (position temporelle), mais câbler un vrai seek nécessiterait
    // une barre de progression dédiée — hors périmètre de cette demande, comme pour
    // ZXTunePlayer.SeekToOrder.
    public void SeekToOrder(int orderIndex) { /* pas de seek par ordre pour UADE (pas de patterns) */ }

    /// <summary>Position de lecture, calculée depuis le nombre d'octets PCM réellement
    /// décodés (uade_get_time_position ne fonctionne que pour le format conteneur "RMC"
    /// interne aux tests d'UADE, pas les vrais fichiers Amiga — cf. uade.h).</summary>
    private TimeSpan Position => TimeSpan.FromSeconds(_bytesDecoded / (double)WaveFormat.AverageBytesPerSecond);

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _state.IsPlaying)
        {
            double pos = Position.TotalSeconds;
            _state.PositionSeconds = pos;
            // Durée réelle si connue (cache ou scan terminé) ; sinon repli sur l'ancien
            // comportement "durée = position" (pas de barre de progression avec un vrai
            // total tant qu'on ne connaît pas la durée réelle du morceau).
            _state.DurationSeconds = _knownDurationForCurrentSubsong ?? pos;
            NotifyState();
            await Task.Delay(40, ct).ConfigureAwait(false); // 25fps — suffisant pour UI
        }
    }

    private void NotifyState()
        => StateChanged?.Invoke(this, new ModelsPlaybackState
        {
            IsPlaying       = _state.IsPlaying, IsPaused = _state.IsPaused,
            PositionSeconds = _state.PositionSeconds, DurationSeconds = _state.DurationSeconds,
            CurrentBpm      = _state.CurrentBpm, CurrentRow = _state.CurrentRow
        });

    // ── IWaveProvider ─────────────────────────────────────────────────
    // UadePlayer implémente directement IWaveProvider (comme UadePlayer dans le projet
    // fourni) plutôt que de déléguer à une classe séparée façon ZXTuneStream/l'ancien
    // UadeStream : contrairement à ZXTune (un nouvel objet natif par subsong), UADE
    // garde un unique uade_state réutilisé pour tous les subsongs — pas besoin d'une
    // couche d'indirection supplémentaire.
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_engineState == IntPtr.Zero || _reachedEnd) return 0;

            byte[] tmp = offset == 0 ? buffer : new byte[count];
            long n = UadeNativeInterop.uade_read(tmp, (UIntPtr)count, _engineState);
            if (n <= 0)
            {
                _reachedEnd = true;
                // Aucun octet PCM jamais produit pour ce subsong précis — format non
                // reconnu/joué en pratique malgré un uade_play() qui avait réussi
                // (rare, mais possible pour un subsong invalide d'un conteneur).
                if (_bytesDecoded == 0) IsPlayable = false;
                return 0;
            }
            if (offset != 0)
                Array.Copy(tmp, 0, buffer, offset, (int)n);

            FeedSampleBuffer(tmp, (int)n);
            _bytesDecoded += n;
            return (int)n;
        }
    }

    /// <summary>Alimente le SampleRingBuffer (oscilloscope) depuis le PCM 16-bit stéréo
    /// entrelacé tout juste rendu par uade_read().</summary>
    private void FeedSampleBuffer(byte[] pcm, int byteCount)
    {
        int frames = byteCount / 4; // 16-bit stéréo = 4 octets/trame
        if (frames <= 0) return;
        // Position AVANT ce bloc (en frames) — _bytesDecoded n'est incrémenté par
        // l'appelant (Read()) qu'APRÈS ce FeedSampleBuffer(), donc sa valeur
        // courante ici correspond bien au début du bloc qu'on s'apprête à écrire.
        long framePosStart = _bytesDecoded / 4;
        var left  = System.Buffers.ArrayPool<float>.Shared.Rent(frames);
        var right = System.Buffers.ArrayPool<float>.Shared.Rent(frames);
        try
        {
            for (int i = 0; i < frames; i++)
            {
                short l = (short)(pcm[i * 4]     | (pcm[i * 4 + 1] << 8));
                short r = (short)(pcm[i * 4 + 2] | (pcm[i * 4 + 3] << 8));
                left[i]  = l / 32768f;
                right[i] = r / 32768f;
            }
            SampleBuffer.Write(left, right, frames);
            WaveformOverview.WriteAt(framePosStart, left, right, frames);
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(left);
            System.Buffers.ArrayPool<float>.Shared.Return(right);
        }
    }

    // ── Cache de durées (UadeDurationDatabase) ─────────────────────────

    /// <summary>Charge en mémoire les durées déjà connues (cache SQLite) pour le
    /// fichier en cours, et met à jour module.DurationSeconds/_knownDurationForCurrentSubsong
    /// si le subsong courant est déjà connu. Best-effort — jamais bloquant/fatal.</summary>
    private void TryApplyCachedDurations(TrackerModule module)
    {
        try
        {
            string md5 = UadeDurationDatabase.ComputeFileMd5(module.FilePath);
            var cached = UadeDurationCache.Instance.GetCached(md5);
            foreach (var kv in cached)
                _knownDurations[kv.Key] = kv.Value.Duration.TotalSeconds;
            ApplyKnownDurationIfAny();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "UADE: cache de durées indisponible (best-effort)");
        }
    }

    /// <summary>Répercute la durée connue du subsong COURANT (s'il y en a une) sur
    /// module.DurationSeconds et _knownDurationForCurrentSubsong, sinon les remet à
    /// "inconnu" (repli sur le calcul par position, cf. PollAsync).</summary>
    private void ApplyKnownDurationIfAny()
    {
        int nativeSubsong = SubsongMin + _currentSubsongIndex;
        double durationForOverview;
        if (_knownDurations.TryGetValue(nativeSubsong, out double seconds))
        {
            _knownDurationForCurrentSubsong = seconds;
            if (_module != null) _module.DurationSeconds = seconds;
            durationForOverview = seconds;
        }
        else
        {
            _knownDurationForCurrentSubsong = null;
            // 2026-08-07 : durée réelle pas encore connue (cache/scan en arrière-plan
            // pas terminés) — repli généreux, même logique que SndhPlayer (300s) :
            // un morceau plus long verra juste ses derniers buckets se tasser sur le
            // dernier (WaveformOverviewBuffer.WriteAt plafonne l'index), artefact
            // mineur acceptable pour un simple visuel d'ensemble.
            durationForOverview = _module != null && _module.DurationSeconds > 0
                ? _module.DurationSeconds : 300;
        }
        WaveformOverview.SetDuration(durationForOverview, SampleRate);
    }

    /// <summary>
    /// Vérifie le cache SQLite puis, si nécessaire, mesure la durée de chaque
    /// sous-chanson en arrière-plan (chacune décodée intégralement — aucun
    /// moyen de connaître la longueur d'un morceau Amiga sans le jouer jusqu'au
    /// bout) et enregistre le résultat. Annulé si un nouveau LoadAsync est
    /// déclenché entre-temps (cf. _durationScanCts.Cancel() dans LoadAsync).
    /// </summary>
    private async Task ScanAndCacheDurationsAsync(TrackerModule module, CancellationToken ct)
    {
        try
        {
            string filePath = module.FilePath;
            string md5 = UadeDurationDatabase.ComputeFileMd5(filePath);
            var db = UadeDurationCache.Instance;
            var cached = db.GetCached(md5);
            int min = SubsongMin, max = SubsongMax;

            if (db.IsFullyCovered(cached, min, max, DefaultSubsongTimeoutSeconds))
                return; // déjà entièrement couvert pour ce cap — rien à scanner

            ct.ThrowIfCancellationRequested();
            _log.LogInformation("UADE: scan de durées en arrière-plan pour '{File}' ({N} subsong(s))",
                filePath, max - min + 1);

            var results = await Task.Run(() => ScanSubsongDurationsParallel(filePath, min, max, ct), ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            db.SaveResults(md5, Path.GetFileName(filePath), min, results, DefaultSubsongTimeoutSeconds);

            lock (_lock)
            {
                for (int i = 0; i < results.Length; i++)
                    _knownDurations[min + i] = results[i].Duration.TotalSeconds;
            }
            // Répercute immédiatement si l'utilisateur écoute encore ce même fichier.
            if (ReferenceEquals(_module, module))
                ApplyKnownDurationIfAny();

            _log.LogInformation("UADE: scan de durées terminé pour '{File}'", filePath);
        }
        catch (OperationCanceledException)
        {
            // Fichier changé (nouveau LoadAsync) pendant le scan — normal, pas une erreur.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "UADE: scan de durées en arrière-plan a échoué pour '{File}'", module.FilePath);
        }
    }

    /// <summary>
    /// Mesure la durée de chaque sous-chanson [min..max] en répartissant le
    /// travail sur des uade_states INDÉPENDANTS (chacun avec son propre
    /// uadecore.exe enfant) — libuade n'autorise qu'un seul thread à la fois
    /// PAR state, mais rien n'empêche plusieurs states indépendants de tourner
    /// en parallèle. Réduit le temps d'attente total à peu près par le nombre
    /// de cœurs utilisés, sans toucher au timing d'émulation (donc sans risque
    /// sur la justesse des durées mesurées) — même principe que
    /// ScanSubsongDurationsParallel dans le projet UadeWpfPlayer fourni.
    ///
    /// Limitation connue (héritée du projet fourni) : le répertoire courant du
    /// process est un état PROCESS-WIDE, partagé par tous les threads — fixé
    /// une fois ici avant le Parallel.For (tous les subsongs d'un même fichier
    /// vivent dans le même dossier). Si un NOUVEAU LoadAsync change le
    /// répertoire courant pendant qu'un scan d'un fichier précédent est encore
    /// en vol, ce scan peut échouer à résoudre un éventuel fichier compagnon
    /// pour les itérations restantes — sans gravité : au pire ce résultat n'est
    /// pas mis en cache cette fois-ci, un scan ultérieur (à la prochaine
    /// ouverture du même fichier) le retentera proprement.
    /// </summary>
    private (TimeSpan Duration, string EndReason)[] ScanSubsongDurationsParallel(
        string filePath, int min, int max, CancellationToken ct)
    {
        SetCwdToFileDir(filePath);
        string playName = Path.GetFileName(filePath);
        string basedir  = ResolveBaseDir();
        string uadecore = ResolveUadecoreExePath();

        int count = max - min + 1;
        var results = new (TimeSpan Duration, string EndReason)[count];
        int workers = Math.Max(1, Math.Min(Environment.ProcessorCount, count));

        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, i =>
        {
            int subsong = min + i;
            IntPtr workerState = IntPtr.Zero;
            try
            {
                workerState = CreateState(basedir, uadecore, SampleRate,
                    DefaultSilenceTimeoutSeconds, DefaultSubsongTimeoutSeconds, panning: null);

                if (UadeNativeInterop.uade_play(playName, subsong, workerState) != UadeNativeInterop.PLAY_OK)
                {
                    results[i] = (TimeSpan.Zero, "impossible à charger");
                    return;
                }

                byte[] scratch = new byte[128 * 1024];
                long bytes = 0;
                long n;
                while ((n = UadeNativeInterop.uade_read(scratch, (UIntPtr)scratch.Length, workerState)) > 0)
                {
                    bytes += n;
                    ct.ThrowIfCancellationRequested();
                }

                var reasonBuf = new StringBuilder(256);
                int gotReason = UadeNativeInterop.uade_net_get_last_end_reason(workerState, reasonBuf, reasonBuf.Capacity);
                string reason = gotReason != 0 ? reasonBuf.ToString() : "(inconnue)";
                results[i] = (TimeSpan.FromSeconds(bytes / (double)WaveFormat.AverageBytesPerSecond), reason);
                UadeNativeInterop.uade_stop(workerState);
            }
            finally
            {
                if (workerState != IntPtr.Zero)
                    UadeNativeInterop.uade_cleanup_state(workerState);
            }
        });

        return results;
    }

    public void Dispose()
    {
        _durationScanCts?.Cancel();
        Stop();
        lock (_lock)
        {
            if (_engineState != IntPtr.Zero)
            {
                UadeNativeInterop.uade_cleanup_state(_engineState);
                _engineState = IntPtr.Zero;
            }
        }
    }
}

public sealed class UadeDecoder : ITrackerDecoder
{
    public string   FormatName          => "UADE (Amiga exotiques)";
    public string[] SupportedExtensions => [.. UadePlayer.SupportedExtensions];

    // Préfixes de noms de fichiers reconnus par UADE (avant le premier point)
    // Ex: "cust.intro" → préfixe "cust"
    public static readonly HashSet<string> KnownPrefixes =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "cust", "custom", "bss",
        "tfmx", "mdat",
        // 2026-07-31, retour utilisateur : format Thomas Hermann, toujours nommé
        // "thm.<suffixe>" — compagnon "smp.<suffixe>" cf. UadeCompanionFormats.
        "thm",
        // 2026-07-31, retour utilisateur : format Dirk Bialluch, toujours nommé
        // "tpu.<suffixe>" — compagnon "smp.<suffixe>" cf. UadeCompanionFormats.
        "tpu",
        // 2026-08-07, retour utilisateur ("les fichiers sjs.* doivent etre accompagné
        // des fichiers smp.*, tous comme les tfmx") : même mécanisme que Thomas
        // Hermann/Dirk Bialluch ci-dessus, toujours nommé "sjs.<suffixe>" — compagnon
        // "smp.<suffixe>" cf. UadeCompanionFormats.
        "sjs",
        "bp",
        "emsv6", "ems",
        "fc", "fc13", "fc14",
        "hip", "hip7",
        "jc",
        "sa",
        "sfx",
        "sm", "smf",
        "sog", "fred", "rk",
        "dl", "dm", "dm2",
        "pum",
    };

    /// <summary>Retourne true si le nom de fichier a un préfixe ou extension UADE connu.</summary>
    public bool CanDecodeFile(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var ext  = Path.GetExtension(filePath);
        if (UadePlayer.SupportedExtensions.Contains(ext)) return true;
        // Préfixe : partie avant le premier point
        var dot = name.IndexOf('.');
        if (dot > 0)
        {
            var prefix = name[..dot];
            if (KnownPrefixes.Contains(prefix)) return true;
        }
        return false;
    }

    public bool CanDecode(Stream stream) => true;  // UADE détecte par nom ET contenu

    public Task<TrackerModule> DecodeAsync(Stream stream, string filePath,
        CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(filePath);
        var dot      = fileName.IndexOf('.');

        string title, formatName;

        // Fichier à préfixe UADE : "mdat.intro_and_title", "cust.bubble_bobble"...
        //   titre   = suffixe après le premier point, underscores → espaces
        //   format  = préfixe en majuscules (MDAT → TFMX, CUST → Custom)
        if (dot > 0 && KnownPrefixes.Contains(fileName[..dot]))
        {
            var prefix = fileName[..dot].ToUpperInvariant();
            title      = fileName[(dot + 1)..].Replace('_', ' ');
            formatName = prefix switch
            {
                "MDAT" => "TFMX",
                "CUST" => "Custom",
                "TFMX" => "TFMX",
                "BSS"  => "BSS SoundMaster",
                "THM"  => "Thomas Hermann",
                "TPU"  => "Dirk Bialluch",
                _      => prefix
            };
        }
        else
        {
            // Fichier à extension : "song.jc", "music.aon"
            title      = Path.GetFileNameWithoutExtension(filePath)
                             .Replace('_', ' ').Replace('.', ' ').Trim();
            formatName = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        }

        var module = new TrackerModule
        {
            FilePath   = filePath,
            FileSize   = stream.Length,
            Title      = title,
            Format     = TrackerFormat.Unknown,
            FormatName = formatName,
            Channels   = 4,  // Paula Amiga = 4 canaux par défaut
        };
        return Task.FromResult(module);
    }
}

}


// ════════════════════════════════════════════════════════════════════════════
// ExeMusicPlayer — lecteur de musiques génératives Windows (.exe, .com)
// Lance le process et attend sa fin pour déclencher PlaybackFinished.
// Utilisé pour les démos-musiques MS-DOS/Windows qui sont des exécutables
// autonomes (ex: AceMan - 3D Monstarz in da PocketLand.exe).
// ════════════════════════════════════════════════════════════════════════════

namespace TrackerPlayer.Core.Players
{

public sealed class ExeMusicPlayer : TrackerPlayer.Core.Interfaces.ITrackerPlayer
{
    private System.Diagnostics.Process? _process;
    private System.Threading.CancellationTokenSource? _cts;
    private readonly ModelsPlaybackState _state = new();
    private bool _stopRequested = false;

    /// <summary>Déclenché pour chaque ligne de sortie stdout/stderr de l'exe console.</summary>
    public event EventHandler<string>? OutputReceived;


    public TrackerPlayer.Core.Models.TrackerFormat[] SupportedFormats => [];
    public event EventHandler<ModelsPlaybackState>? StateChanged;
    public event EventHandler? PlaybackFinished;
    public ModelsPlaybackState CurrentState => _state;
    public TrackerPlayer.Core.Players.SampleRingBuffer SampleBuffer { get; } = new(512);
    public int    SubsongCount        => 1;
    public int    CurrentSubsongIndex => 0;
    public void   SelectSubsong(int index) { /* non applicable — process externe unique */ }
    public float  MasterVolume      { get; set; } = 1f;

    public Task LoadAsync(TrackerPlayer.Core.Models.TrackerModule module,
        System.Threading.CancellationToken ct = default)
    {
        _stopRequested = false;
        _state.IsPlaying = true;
        StateChanged?.Invoke(this, _state);

        _cts = new System.Threading.CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = module.FilePath,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    WorkingDirectory       = System.IO.Path.GetDirectoryName(module.FilePath) ?? "",
                };
                _process = System.Diagnostics.Process.Start(psi);
                if (_process == null)
                {
                    _state.IsPlaying = false;
                    StateChanged?.Invoke(this, _state);
                    PlaybackFinished?.Invoke(this, EventArgs.Empty);
                    return;
                }
                // Enregistrer pour nettoyage à la fermeture de l'app
                ExternalProcessRegistry.Register(_process);
                // Capturer stdout/stderr et relayer vers l'UI
                _process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    var line = e.Data.Contains('\r') ? e.Data.Split('\r')[^1] : e.Data;
                    if (!string.IsNullOrEmpty(line)) OutputReceived?.Invoke(this, line);
                };
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) OutputReceived?.Invoke(this, e.Data);
                };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                // Surveiller l'arbre de process complet via Job Object
                await ExeMusicJobMonitor.WaitForProcessTreeAsync(_process, _cts!.Token);
            }
            catch (System.OperationCanceledException) { }
            catch { }
            finally
            {
                _state.IsPlaying = false;
                StateChanged?.Invoke(this, _state);
                // Ne déclencher PlaybackFinished que si ce n'est pas un stop volontaire
                if (!_stopRequested)
                    PlaybackFinished?.Invoke(this, EventArgs.Empty);
            }
        }); // pas de token — on contrôle via Kill()

        return Task.CompletedTask;
    }

    public void Play()  { }
    public void Pause() { }

    public void Stop()
    {
        if (_stopRequested) return; // guard anti-double appel
        _stopRequested = true;

        // 2026-08-02, retour utilisateur ("j'ai tenté l'arrêt, et ça boucle sur
        // cette exception [...] obligé de killer l'application") : cette méthode
        // appelait ICI p.Kill(entireProcessTree: true) EN PLUS de _cts.Cancel()
        // ci-dessous — or _cts.Cancel() fait DÉJÀ terminer tout l'arbre de process,
        // via TerminateJobObject dans le finally de
        // ExeMusicJobMonitor.WaitForProcessTreeAsync (déclenché par l'annulation,
        // sur le thread d'arrière-plan du Task.Run de LoadAsync). Les deux
        // mécanismes tuaient donc LE MÊME arbre de process EN MÊME TEMPS, depuis
        // deux threads différents : l'implémentation interne de
        // Process.Kill(entireProcessTree: true) énumère puis attend la sortie de
        // chaque descendant, et se met à boucler/re-tenter indéfiniment quand ces
        // process se font tuer "sous ses pieds" par TerminateJobObject entre-temps
        // — Win32Exception en rafale ininterrompue. Stop() s'exécutant sur le
        // thread UI (RelayCommand), cette boucle gelait toute l'application.
        // Un seul mécanisme d'arrêt effectif désormais : l'annulation ci-dessous,
        // qui laisse le Job Object (conçu justement pour tuer tout un arbre de
        // process de façon fiable et atomique) faire le travail, sans concurrence.
        _cts?.Cancel();

        var p = _process;
        if (p != null)
        {
            // 2026-08-02 : terminaison déléguée à l'annulation ci-dessus (via le Job
            // Object) → retiré du registre global plutôt que laissé dedans pour
            // toujours (cf. commentaire de classe ExternalProcessRegistry) ;
            // KillAll() n'a plus besoin de le retraiter.
            ExternalProcessRegistry.Unregister(p);
        }
        else
        {
            // Filet de sécurité, réservé au cas où _process est encore null au
            // moment du Stop (course avec le Task.Run de LoadAsync, avant même que
            // Process.Start() soit revenu) : ce process-ci n'a pas pu être
            // récupéré/annulé via _cts ci-dessus (LoadAsync n'a pas encore atteint
            // WaitForProcessTreeAsync), donc on retombe sur KillAll() — qui ne
            // contient plus, grâce à Unregister() ci-dessus, que des entrées
            // réellement non traitées (jamais tout l'historique de la session, cf.
            // bug initial : rafale de Win32Exception au changement de release après
            // plusieurs musiques exe jouées).
            ExternalProcessRegistry.KillAll();
        }
    }

    public void SeekToOrder(int orderIndex) { }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
        _cts?.Dispose();
    }
}
} // namespace TrackerPlayer.Core.Players
