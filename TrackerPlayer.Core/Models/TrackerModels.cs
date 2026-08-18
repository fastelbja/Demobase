using System;
using System.Collections.Generic;

namespace TrackerPlayer.Core.Models
{
    /// <summary>
    /// Format de fichier tracker supporté.
    /// </summary>
    public enum TrackerFormat
    {
        Unknown,
        // ── Formats ProTracker / FastTracker ──────────────────────────────
        MOD,    // ProTracker / Noisetracker (Amiga)
        S3M,    // ScreamTracker 3 (PC)
        STM,    // ScreamTracker 2 (PC) — prédécesseur de S3M
        XM,     // FastTracker 2 (PC)
        IT,     // ImpulseTracker (PC)
        DBM,    // DigiBooster Pro (Amiga) — jusqu'à 32 canaux
        // 2026-07-30, retour utilisateur (piste Modland .ult, pattern affiché mais
        // vue ProTracker par défaut faute d'entrée dédiée) : "les fichiers .ult
        // (ultracker) affichent les pattern mais il faut la vue FT2".
        ULT,    // UltraTracker (PC) — géré par libopenmpt, style d'affichage FT2
        // 2026-07-30, retour utilisateur (piste Modland Astroidea XMF, .xmf,
        // jouable par libopenmpt avec pattern view) : "met la vue protracker déjà
        // pour voir si ça convient. sinon je te dirais de changer" — valeur d'enum
        // dédiée plutôt que de laisser reposer sur le fallback ProTracker implicite
        // (TrackerFormat.Unknown), pour que le choix soit explicite et facile à
        // changer plus tard si besoin (cf. TrackerStyle switch, SoundtrackPlayerViewModel).
        // 2026-07-30, retour utilisateur : "l'astroidea (.xmf) ... à ouvrir avec
        // libopenmpt et les patterns FT2" — révise le choix ProTracker initial
        // (fait "pour voir si ça convenait") vers FT2, comme XM/DBM/ULT.
        XMF,    // Astroidea XMF (PC) — géré par libopenmpt, style d'affichage FT2
        // 2026-07-30, retour utilisateur : ".amf - à ouvrir avec libopenmpt et
        // les patterns FT2".
        AMF,    // DSMI/ASYLUM AMF (PC) — géré par libopenmpt, style d'affichage FT2
        // 2026-07-30, retour utilisateur : ".667"/".669 - à ouvrir avec libopenmpt
        // et les patterns FT2" — .667 est une variante de nommage de .669.
        Composer669, // Composer 669 / UNIS 669 (PC) — libopenmpt, style FT2
        // 2026-07-30, retour utilisateur : ".digi - à ouvrir avec libopenmpt et
        // les patterns FT2" (DigiBooster non-Pro, différent de DBM/DigiBooster Pro).
        DIGI,   // DigiBooster (Amiga) — géré par libopenmpt, style d'affichage FT2
        // 2026-07-30, retour utilisateur : ".dsm/.dtm/.mdl - à ouvrir avec
        // libopenmpt et les patterns FT2".
        DSM,    // Digital Sound Interface Kit (PC) — libopenmpt, style FT2
        DTM,    // Digital Tracker (Atari Falcon) — libopenmpt, style FT2
        MDL,    // DigiTrakker (PC) — libopenmpt, style FT2
        // 2026-07-30, retour utilisateur : ".dmf/.ams - à ouvrir avec libopenmpt
        // et les patterns FT2".
        DMF,    // X-Tracker / Digital Media Format (PC) — libopenmpt, style FT2
        AMS,    // Extreme's Tracker / Velvet Studio (PC) — libopenmpt, style FT2
        // 2026-07-30, retour utilisateur : ".psm - à ouvrir avec libopenmpt et
        // les patterns FT2". Conflit d'extension avec un format ZX Spectrum du
        // même nom (ZXTunePlayer.SupportedExtensions) — retiré de ZXTune (cf.
        // ExternalPlayers.cs) au profit de libopenmpt.
        PSM,    // Epic MegaGames MASI (PC) — libopenmpt, style FT2
        // 2026-07-30, retour utilisateur : ".gtk/.gt2 - à ouvrir avec libopenmpt
        // et les patterns FT2" — couvre les deux versions du format.
        GraoumfTracker, // Graoumf Tracker (Atari ST), .gtk/.gt2 — libopenmpt, style FT2
        // 2026-07-30, retour utilisateur : ".mt2 - à ouvrir avec libopenmpt et
        // les patterns FT2".
        MT2,    // MadTracker 2 (PC) — libopenmpt, style FT2
        // 2026-07-31, retour utilisateur : "il faut ouvrir les fichiers .stp avec
        // visu ft2" — conflit d'extension avec un format ZX Spectrum du même nom
        // (ZXTunePlayer, "SoundTracker compiled") — retiré de ZXTune (cf.
        // ExternalPlayers.cs) au profit de libopenmpt, même schéma que PSM/GTK.
        STP,    // Soundtracker Pro II (Atari Falcon) — libopenmpt, style FT2
        // ── Formats exotiques Amiga (via UADE) ───────────────────────────
        SID,    // Commodore 64 SID
        AHX,    // Amiga HivelyTracker
        TFMX,   // Amiga TFMX
        HVSC,   // High Voltage SID Collection
        // ── Formats ZX / CPC (via ZXTune) ────────────────────────────────
        AY,     // ZX Spectrum AY chip
        YM,     // Amstrad CPC YM chip
        PT3,    // ProTracker 3 (ZX Spectrum)
        VTX,    // VortexTracker (ZX Spectrum)
        STC,    // SoundTracker compiled (ZX Spectrum)
        PSG,    // PSG dump (Amstrad/ZX)
        // ── Autres ────────────────────────────────────────────────────────
        GBS,    // Game Boy Sound
        NSF,    // NES Sound Format
        SNSF,   // Super Nintendo Sound Format
        VGM,    // Video Game Music (Sega)
    }

    /// <summary>
    /// Représente un sample (instrument) dans un fichier tracker.
    /// </summary>
    public class TrackerSample
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Length { get; set; }
        public int LoopStart { get; set; }
        public int LoopLength { get; set; }
        public int Volume { get; set; }
        public int FineTune { get; set; }
        public bool HasData => Length > 0;
    }

    /// <summary>
    /// Représente un instrument (au sens XM/IT : une couche au-dessus des samples,
    /// qui peut combiner plusieurs samples avec enveloppes/mapping clavier). Tous les
    /// formats n'ont pas cette couche (MOD/S3M par ex. n'ont que des samples) —
    /// 2026-07-31, retour utilisateur ("infos sur les instruments (nom ? taille ?)").
    /// libopenmpt n'expose PAS de taille pour un instrument via son API C publique
    /// (ni d'ailleurs pour un sample, cf. TrackerSample.Length toujours à 0 pour les
    /// modules enrichis par libopenmpt) — seul le nom est disponible.
    /// </summary>
    public class TrackerInstrument
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Une note dans une cellule de pattern.
    /// </summary>
    public record PatternCell
    {
        public int Note { get; init; }        // 0 = vide, 1-96 = notes
        public int Instrument { get; init; }  // 0 = vide
        public int Volume { get; init; }      // 0-64 ou -1 si non utilisé
        public int Effect { get; init; }      // code d'effet (0x0-0xF...)
        public int EffectParam { get; init; }

        /// <summary>
        /// Chaîne pré-formatée par libopenmpt (ex: "G#3 02 964").
        /// Null si non disponible (formats sans libopenmpt).
        /// Quand elle est définie, c'est elle qui fait foi pour l'affichage.
        /// </summary>
        public string? RawString { get; init; }

        public string NoteString => Note switch
        {
            0 => "---",
            _ => NoteNames[(Note - 1) % 12] + ((Note - 1) / 12).ToString()
        };

        private static readonly string[] NoteNames =
            ["C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-"];
    }

    /// <summary>
    /// Un pattern complet : tableau [row, channel] de PatternCell.
    /// </summary>
    public class TrackerPattern
    {
        public int Index { get; set; }
        public int Rows { get; set; }
        public int Channels { get; set; }
        public PatternCell[,] Cells { get; set; }

        public TrackerPattern(int index, int rows, int channels)
        {
            Index = index;
            Rows = rows;
            Channels = channels;
            Cells = new PatternCell[rows, channels];
            // Initialise avec des cellules vides
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < channels; c++)
                    Cells[r, c] = new PatternCell();
        }
    }

    /// <summary>
    /// Métadonnées complètes d'un fichier tracker chargé.
    /// </summary>
    public class TrackerModule
    {
        /// <summary>Format détecté du fichier.</summary>
        public TrackerFormat Format     { get; set; }
        /// <summary>Nom lisible du format (ex: "YM", "AY", "SID", "PT3"…). Priorité sur Format.</summary>
        public string?       FormatName { get; set; }

        /// <summary>Nom du morceau tel qu'il est stocké dans le fichier.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Auteur / tracker d'origine (si disponible).</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>Commentaire / message du fichier (IT, S3M…).</summary>
        public string Comment { get; set; } = string.Empty;

        /// <summary>Nombre de canaux (voix).</summary>
        public int Channels { get; set; }

        /// <summary>BPM initial.</summary>
        public int InitialBpm { get; set; } = 125;

        /// <summary>Speed initiale (ticks par ligne).</summary>
        public int InitialSpeed { get; set; } = 6;

        /// <summary>Volume global (0-64).</summary>
        public int GlobalVolume { get; set; } = 64;

        /// <summary>Durée estimée en secondes.</summary>
        public double DurationSeconds { get; set; }

        /// <summary>Ordre de lecture des patterns (song order list).</summary>
        public List<int> OrderList { get; set; } = new();

        /// <summary>Patterns du morceau.</summary>
        public List<TrackerPattern> Patterns { get; set; } = new();

        /// <summary>Samples / échantillons.</summary>
        public List<TrackerSample> Samples { get; set; } = new();

        /// <summary>Instruments (couche XM/IT au-dessus des samples) — 2026-07-31, retour
        /// utilisateur. Vide pour les formats sans cette notion (MOD/S3M...), dans ce cas
        /// se référer à <see cref="Samples"/> à la place.</summary>
        public List<TrackerInstrument> Instruments { get; set; } = new();

        /// <summary>Chemin complet du fichier source.</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Taille du fichier en octets.</summary>
        public long FileSize { get; set; }
    }

    /// <summary>
    /// État de lecture courant, diffusé en temps réel.
    /// </summary>
    public class PlaybackState
    {
        public int CurrentOrder { get; set; }
        public int CurrentPattern { get; set; }
        public int CurrentRow { get; set; }
        public int CurrentBpm { get; set; }
        public int CurrentSpeed { get; set; }
        public double PositionSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public int[] ChannelVolumes { get; set; } = Array.Empty<int>();
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
    }
}
