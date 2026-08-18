using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TrackerPlayer.Core.Players
{
    // ════════════════════════════════════════════════════════════════════════
    // Pont natif libuade.dll — P/Invoke direct, en remplacement du process
    // externe uade123.exe (streaming stdout, copies de fichiers compagnons
    // TFMX/Thomas Hermann/Dirk Bialluch) utilisé jusqu'ici par UadePlayer (cf.
    // commentaire de classe sur UadePlayer dans ExternalPlayers.cs).
    //
    // 2026-08-06, retour utilisateur : "j'ai fait de même avec uade. j'ai crée
    // une dll. voici le projet [UadeWpfPlayer.zip]. tu verras qu'on stocke
    // maintenant les durées dans une base duration.db et qu'il y a un système
    // pour calculer les durées des songs et subsongs". Ce fichier reprend
    // l'essentiel de NativeMethods/UadeNative.cs du projet de démo fourni.
    //
    // Contrairement à zxtune.dll (ZxTuneNative.cs), libuade.dll ne remplace
    // PAS entièrement le besoin d'un exécutable externe : l'architecture
    // interne d'UADE isole l'émulation 68k dans un process séparé
    // ("uadecore.exe", spawné par la DLL elle-même via UC_UADECORE_FILE) —
    // libuade.dll est le CÔTÉ HÔTE de cette architecture, pas un remplacement
    // qui l'élimine. Le gain reste réel : plus de génération de fichier/pipe
    // stdout géré à la main côté C# (uade_read() remplit directement un
    // buffer géré), plus de copies de fichiers compagnons avec renommage GUID
    // (remplacées par un simple changement de répertoire courant, cf.
    // UadePlayer.SetCwdToFileDir), et une vraie mesure de durée par
    // sous-chanson (cf. UadeDurationDatabase.cs) — chose impossible à faire
    // proprement avec le process externe (aucune API structurée pour la fin
    // de lecture, seulement du texte à parser sur stdout/stderr).
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Déclarations DllImport vers libuade.dll. uade_config*/uade_state* sont
    /// des handles opaques (IntPtr) — leur disposition C n'est jamais reflétée
    /// ici, uniquement manipulés via les fonctions natives.
    /// </summary>
    internal static class UadeNativeInterop
    {
        private const string Dll = "libuade.dll";

        public enum Option
        {
            UC_NO_OPTION = 0x1000,
            UC_BASE_DIR,
            UC_CONTENT_DETECTION,
            UC_DISABLE_TIMEOUTS,
            UC_ENABLE_TIMEOUTS,
            UC_EAGLEPLAYER_OPTION,
            UC_FILTER_TYPE,
            UC_FORCE_LED_OFF,
            UC_FORCE_LED_ON,
            UC_FORCE_LED,
            UC_FREQUENCY,
            UC_GAIN,
            UC_HEADPHONES,
            UC_HEADPHONES2,
            UC_IGNORE_PLAYER_CHECK,
            UC_NO_CONTENT_DB,
            UC_NO_FILTER,
            UC_NO_HEADPHONES,
            UC_NO_PANNING,
            UC_NO_POSTPROCESSING,
            UC_NO_EP_END,
            UC_NTSC,
            UC_ONE_SUBSONG,
            UC_PAL,
            UC_PANNING_VALUE,
            UC_PLAYER_FILE,
            UC_RESAMPLER,
            UC_SCORE_FILE,
            UC_SILENCE_TIMEOUT_VALUE,
            UC_SPEED_HACK,
            UC_SUBSONG_TIMEOUT_VALUE,
            UC_TIMEOUT_VALUE,
            UC_UADECORE_FILE,
            UC_UAE_CONFIG_FILE,
            UC_USE_TEXT_SCOPE,
            UC_VERBOSE,
            UC_AO_OPTION,
            UC_WRITE_AUDIO_FILE,
            UC_WRITE_AUDIO_FD,
        }

        public enum SeekMode
        {
            UADE_SEEK_NOT_SEEKING = 0,
            UADE_SEEK_SONG_RELATIVE,
            UADE_SEEK_SUBSONG_RELATIVE,
            UADE_SEEK_POSITION_RELATIVE,
        }

        // uade_play() / uade_play_from_buffer() return codes.
        public const int PLAY_FATAL_ERROR = -1;
        public const int PLAY_CANNOT_PLAY = 0;
        public const int PLAY_OK = 1;

        /// <summary>
        /// Vrai si libuade.dll est chargeable (présente, bonne architecture,
        /// exports attendus). Ne garantit pas qu'uadecore.exe est lui aussi
        /// présent/fonctionnel (nécessaire uniquement à la création d'un
        /// state, pas au chargement de la DLL elle-même) — cf.
        /// UadePlayer.IsAvailable qui vérifie aussi ce second fichier.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                try
                {
                    return NativeLibrary.TryLoad(Dll, typeof(UadeNativeInterop).Assembly, null, out _);
                }
                catch
                {
                    return false;
                }
            }
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr uade_new_config();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void uade_config_set_option(IntPtr config, Option opt,
            [MarshalAs(UnmanagedType.LPStr)] string value);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr uade_new_state(IntPtr config);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void uade_cleanup_state(IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_play([MarshalAs(UnmanagedType.LPStr)] string fname,
            int subsong, IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_stop(IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern long uade_read(byte[] buffer, UIntPtr bytes, IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_get_sampling_rate(IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_is_our_file([MarshalAs(UnmanagedType.LPStr)] string fname,
            IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_seek(SeekMode whence, double seconds, int subsong, IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern double uade_get_time_position(SeekMode whence, IntPtr state);

        // --- Accesseurs .NET-friendly (uade_dotnet_helpers.c côté natif) ---

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern double uade_net_get_duration(IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_net_get_subsong_cur(IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_net_get_subsong_min(IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_net_get_subsong_max(IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_net_get_subsong_def(IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void uade_net_get_formatname(IntPtr state, StringBuilder buf, int buflen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void uade_net_get_modulename(IntPtr state, StringBuilder buf, int buflen);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void uade_net_get_playername(IntPtr state, StringBuilder buf, int buflen);

        /// <summary>
        /// Vide les notifications en attente côté libuade et retourne la
        /// raison de fin de la dernière sous-chanson/morceau : "silence"
        /// (silence numérique réel détecté), "subsong timeout"/"song timeout"
        /// (cap configuré atteint, PAS une durée réelle mesurée), "player" (le
        /// player Amiga lui-même a signalé la fin — durée réelle), "no more
        /// subsongs left", ou une raison d'erreur ("score crashed", "module
        /// check failed"...). Retourne 1 si une raison a été trouvée, 0 sinon.
        /// À appeler juste après qu'uade_read() ait retourné &lt;= 0, avant le
        /// prochain uade_stop()/uade_play().
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_net_get_last_end_reason(IntPtr state, StringBuilder buf, int buflen);

        /// <summary>
        /// Diagnostic seul : exécute la détection de format propre à libuade
        /// (sniffing du contenu, repli sur le préfixe/suffixe du nom de
        /// fichier — exactement ce que fait uade_play() en interne) SANS
        /// tenter de charger/jouer quoi que ce soit. Remplit le tag de format
        /// détecté (ex. "MDAT", "TFMX7V") et le nom de l'eagleplayer trouvé ;
        /// byContent vaut 1 si la correspondance vient du contenu, 0 si repli
        /// sur le nom de fichier. Retourne 1 si un eagleplayer a été trouvé
        /// (c'est ce qui détermine si uade_play() tentera même le fichier),
        /// 0 sinon.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_net_detect_format(
            [MarshalAs(UnmanagedType.LPStr)] string fname, IntPtr state,
            StringBuilder extBuf, int extBufLen,
            StringBuilder playerBuf, int playerBufLen,
            out int byContent);

        /// <summary>Reproduit la construction de chemin "%s/players/%s" de
        /// libuade et vérifie que le résultat s'ouvre réellement avec une
        /// taille non nulle.</summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_net_check_player_file(
            [MarshalAs(UnmanagedType.LPStr)] string basedir,
            [MarshalAs(UnmanagedType.LPStr)] string playername,
            StringBuilder pathOut, int pathOutLen, out long sizeOut);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_net_check_score_file(
            [MarshalAs(UnmanagedType.LPStr)] string basedir,
            StringBuilder pathOut, int pathOutLen, out long sizeOut);

        /// <summary>
        /// Redirige le stderr de CE process vers un fichier temporaire —
        /// nécessaire car les diagnostics propres de libuade (uade_warning/
        /// uade_debug, simples fprintf(stderr, ...)) sont sinon totalement
        /// invisibles dans une appli graphique sans console attachée. À
        /// utiliser avec <see cref="uade_net_capture_stderr_stop"/>, puis
        /// relire le fichier temporaire côté C#.
        /// </summary>
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_net_capture_stderr_start(
            [MarshalAs(UnmanagedType.LPStr)] string tmpPath);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void uade_net_capture_stderr_stop();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr uade_get_effective_config(IntPtr state);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uade_config_toggle_boolean(IntPtr config, Option opt);
    }
}
