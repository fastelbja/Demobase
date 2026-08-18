using System.ComponentModel.DataAnnotations.Schema;
using DemoBase.Core.Enums;

namespace DemoBase.Core.Models;

// ─── Base ─────────────────────────────────────────────────────────────────────

public abstract class BaseEntity
{
    public int Id { get; set; }
    // Nullable pour les entités importées depuis Demozoo qui n'ont pas ces champs
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

// ─── ReleaseType ──────────────────────────────────────────────────────────────
// Calqué sur productions_productiontype de Demozoo.
// Demozoo distingue "supertype" (production / graphics / music) et le type précis.

public class ReleaseType : BaseEntity
{
    public string Name { get; set; } = string.Empty;       // "Demo", "64K Intro", "Tracked Music"…
    public string Supertype { get; set; } = "production";  // "production" | "graphics" | "music"
    public string? Description { get; set; }
    public int SortOrder { get; set; } = 0;
    public int? DemozooId { get; set; }                    // id dans la DB Demozoo (import)

    public ICollection<Release> Releases { get; set; } = [];
}

// ─── Platform ─────────────────────────────────────────────────────────────────
// Calqué sur platforms_platform.

public class Platform : BaseEntity
{
    public string Name { get; set; } = string.Empty;       // "Amiga OCS/ECS", "MS-Dos"…
    public string? ShortName { get; set; }                 // utilisé dans les badges UI
    public int? DemozooId { get; set; }

    // Navigation M:N via table de jointure — cohérent avec ReleasePlatform
    public ICollection<ReleasePlatform> ReleasePlatforms { get; set; } = [];
    public ICollection<EmulatorConfig> Emulators { get; set; } = [];

    // Calculé par PlatformListViewModel.LoadAsync (via IEmulatorRepository.
    // GetConfiguredPlatformIdsAsync — une seule requête groupée, pas de N+1) — PAS chargé par
    // défaut, contrairement à ce que le nom pourrait suggérer : Platforms.GetAllAsync() n'a
    // pas d'.Include(Emulators), donc ne PAS s'y fier ailleurs sans l'avoir explicitement
    // renseigné. Vrai par défaut pour ne jamais colorer en rouge à tort un Platform chargé
    // sans être passé par ce calcul. Remplace le 2026-07-24 l'ancienne liste figée
    // PlatformNotEmulatedConverter (basée sur le nom, pas sur les configs réelles).
    [NotMapped] public bool HasEmulatorConfig { get; set; } = true;
}

// ─── Releaser (groupe ou scener) ──────────────────────────────────────────────
// Demozoo unifie groupes et sceners dans la table "demoscene_releaser".
// is_group discrimine les deux. On reproduit ce choix : un Releaser peut être
// un groupe (is_group=true) ou une personne (is_group=false).
// Les relations de membership sont dans ReleaserMembership.

public class Releaser : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsGroup { get; set; }
    public string? Abbreviation    { get; set; }    // "FC", "TBL"…
    public string? Differentiator  { get; set; }    // "pc", "amiga"… pour distinguer les homonymes
    public string? FirstName       { get; set; }    // Prénom réel
    public string? SurName         { get; set; }    // Nom de famille réel
    public string? Location        { get; set; }    // Pays/ville en clair

    // Nom complet avec différenciateur si présent : "Future Crew (pc)"
    [NotMapped]
    public string DisplayName => string.IsNullOrWhiteSpace(Differentiator)
        ? Name
        : $"{Name} ({Differentiator})";

    // Nombre de releases (calculé côté repository, non stocké)
    [NotMapped]
    public int ReleaseCount { get; set; }

    // Nom réel si disponible : "Jussi Pietilä"
    [NotMapped]
    public string? RealName
    {
        get
        {
            var parts = new[] { FirstName, SurName }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var full = string.Join(" ", parts);
            return string.IsNullOrWhiteSpace(full) ? null : full;
        }
    }
    public string? Country { get; set; }             // code ISO-2 : "FI", "DE"…
    public string? Website { get; set; }
    public string? Notes { get; set; }
    public string? LogoPath { get; set; }
    public int? DemozooId { get; set; }

    // Nicks (pseudos alternatifs, calqué sur demoscene_nick)
    public ICollection<Nick> Nicks { get; set; } = [];

    // Membership : groupes dont ce scener est membre / membres de ce groupe
    public ICollection<ReleaserMembership> MembershipsAsScener { get; set; } = [];
    public ICollection<ReleaserMembership> MembershipsAsGroup { get; set; } = [];

    // Crédits sur des releases
    public ICollection<ReleaseCredit> Credits { get; set; } = [];

    // Releases dont ce releaser est auteur
    public ICollection<ReleaseAuthor> AuthoredReleases { get; set; } = [];
}

// ─── Nick (pseudo / abréviation d'un releaser) ───────────────────────────────
// Calqué sur demoscene_nick. Un releaser peut avoir plusieurs nicks.

public class Nick : BaseEntity
{
    public int ReleaserId { get; set; }
    public Releaser Releaser { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Abbreviation { get; set; }
    public bool IsPrimary { get; set; } = false;
    public int? DemozooId { get; set; }
}

// ─── ReleaserMembership ───────────────────────────────────────────────────────
// Calqué sur demoscene_membership.
// Scener (is_group=false) → membre de → Group (is_group=true).

public class ReleaserMembership
{
    public int ScenerId { get; set; }
    public Releaser Scener { get; set; } = null!;
    public int GroupId { get; set; }
    public Releaser Group { get; set; } = null!;
    public bool IsCurrentMember { get; set; } = true;
    public int? JoinYear { get; set; }
    public int? LeaveYear { get; set; }
}

// ─── PartySeries ─────────────────────────────────────────────────────────────
// Calqué sur parties_partyseries.
// Une série regroupe les éditions annuelles d'une party (ex : "Assembly").

public class PartySeries : BaseEntity
{
    public string Name { get; set; } = string.Empty;   // "Assembly", "Revision"…
    public string? Website { get; set; }
    public string? Notes { get; set; }
    public string? Country { get; set; }
    public int? DemozooId { get; set; }

    public ICollection<Party> Parties { get; set; } = [];
}

// ─── Party ────────────────────────────────────────────────────────────────────
// Calqué sur parties_party.

public class Party : BaseEntity
{
    public string Name { get; set; } = string.Empty;       // "Assembly 1993"
    public string? Tagline { get; set; }
    public int? PartySeriesId { get; set; }
    public PartySeries? PartySeries { get; set; }
    public string? StartDate { get; set; }                  // format Demozoo : "1993-08-05" ou "1993" ou "1993-08"
    public string? EndDate { get; set; }
    public string? Location { get; set; }                   // "Helsinki, Finland"
    public bool IsOnline { get; set; } = false;
    public string? CountryCode { get; set; }               // "FI"
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Website { get; set; }
    public string? Notes { get; set; }
    public int? DemozooId { get; set; }

    public ICollection<Competition> Competitions { get; set; } = [];
}

// ─── Competition ──────────────────────────────────────────────────────────────
// Calqué sur parties_competition.
// Une party a plusieurs compétitions (compos) : "Demo", "64K Intro", "Wild"…

public class Competition : BaseEntity
{
    public int PartyId { get; set; }
    public Party Party { get; set; } = null!;
    public string Name { get; set; } = string.Empty;   // "Demo compo", "64K Intro"…
    public int? DemozooId { get; set; }

    public ICollection<CompetitionPlacing> Placings { get; set; } = [];
}

// ─── CompetitionPlacing ───────────────────────────────────────────────────────
// Calqué sur parties_competitionplacing.
// Associe une release à une compétition avec son rang.

public class CompetitionPlacing : BaseEntity
{
    public int CompetitionId { get; set; }
    public Competition Competition { get; set; } = null!;
    public int ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    public int? Ranking { get; set; }                  // 1st, 2nd…
    public string? Score { get; set; }                 // points ou pourcentage (string libre)
    public int? DemozooId { get; set; }
}

// ─── Release (production dans Demozoo) ───────────────────────────────────────
// Calqué sur productions_production.
// Demozoo appelle ceci "production" ; on garde "Release" pour la cohérence DemoBase.

public class Release : BaseEntity
{
    public string Title { get; set; } = string.Empty;

    // Date de sortie : Demozoo stocke date + précision (year / month / day)
    public string? ReleaseDate { get; set; }            // "1993-08-05", "1993-08", "1993"
    public string? ReleaseDatePrecision { get; set; }   // "d" | "m" | "y"

    // Supertype : "production" | "graphics" | "music"
    public string Supertype { get; set; } = "production";

    // FK type (entité en base)
    public int? ReleaseTypeId { get; set; }
    public ReleaseType? ReleaseType { get; set; }

    // Peut avoir plusieurs plateformes (M:N)
    public ICollection<ReleasePlatform> ReleasePlatforms { get; set; } = [];

    // Auteurs (M:N via nick Demozoo)
    public ICollection<ReleaseAuthor> Authors { get; set; } = [];

    // Crédits individuels (graphiste, codeur, musicien…)
    public ICollection<ReleaseCredit> Credits { get; set; } = [];

    // Résultats de compos
    public ICollection<CompetitionPlacing> CompetitionPlacings { get; set; } = [];

    // Fichiers téléchargeables
    public ICollection<ReleaseLink> Links { get; set; } = [];

    // Médias (screenshots, vidéos, musiques)
    public ICollection<MediaFile>         MediaFiles     { get; set; } = [];
    public ICollection<ReleaseSoundtrack> Soundtracks    { get; set; } = [];
    [NotMapped] public IList<ReleaseSoundtrack> UsedInReleases { get; set; } = [];

    // Métadonnées
    public string? Notes { get; set; }
    public bool IsFavorite { get; set; } = false;
    public int? Rating { get; set; }
    public string? DemozooUrl { get; set; }
    public int? DemozooId { get; set; }
    public string? PouetUrl { get; set; }
    public string? CsdbUrl { get; set; }
    public string? Tags { get; set; }

    /// <summary>
    /// Nombre de fois où l'utilisateur a cliqué Play/Afficher/Regarder/Lancer sur cette
    /// release — incrémenté par ReleaseService.IncrementViewCountAsync, appelé depuis le
    /// bouton principal de la fiche release. Sert à filtrer "Non vu" (ViewCount == 0) et
    /// à l'ajout automatique aux favoris au-delà d'un seuil configurable.
    /// </summary>
    public int ViewCount { get; set; } = 0;

    /// <summary>Cache des noms d'auteurs — rempli par le repository, non persisté en base.</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string  AuthorNamesCache   { get; set; } = string.Empty;
    [NotMapped] public string? ThumbnailPathCache { get; set; }
}

// ─── ReleasePlatform (M:N Release ↔ Platform) ────────────────────────────────
// Calqué sur productions_production_platforms.

public class ReleasePlatform
{
    public int ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    public int PlatformId { get; set; }
    public Platform Platform { get; set; } = null!;
}

// ─── ReleaseAuthor (M:N Release ↔ Nick) ──────────────────────────────────────
// Calqué sur productions_production_author_nicks.
// Un auteur est référencé par son nick (pas directement par le Releaser),
// ce qui permet de garder trace du pseudo utilisé au moment de la release.

public class ReleaseAuthor
{
    public int ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    public int NickId { get; set; }
    public Nick Nick { get; set; } = null!;

    // Affiliation au moment de la release (nick du groupe)
    public int? AffiliationNickId { get; set; }
    public Nick? AffiliationNick { get; set; }
}

// ─── ReleaseCredit ────────────────────────────────────────────────────────────
// Calqué sur productions_credit.
// Rôle individuel d'un scener sur une release : code, gfx, music…

public class ReleaseCredit
{
    public int ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    public int ReleaserId { get; set; }
    public Releaser Releaser { get; set; } = null!;
    // String libre : valeurs Demozoo = "code", "music", "graphics", "font"…
    public string Role { get; set; } = string.Empty;
    public string? Detail { get; set; }        // précision libre : "main code", "music #3"
}

// ─── ReleaseLink (fichiers / URLs téléchargeables) ───────────────────────────
// Calqué sur productions_productionlink.
// Contient les URLs de téléchargement ET les fichiers locaux.

public class ReleaseLink : BaseEntity
{
    public int ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    public string? Url { get; set; }            // URL externe (scene.org, pouet, etc.)
    public string? LocalFilePath { get; set; }  // chemin local relatif au MediaRoot
    public string? FileName { get; set; }
    public string? Format { get; set; }         // "ADF", "EXE", "ZIP"…
    public long? FileSizeBytes { get; set; }
    public bool IsMainFile { get; set; } = false;
    public bool IsLocalCopy { get; set; } = false;
    public int? EmulatorConfigId { get; set; }
    public EmulatorConfig? EmulatorConfig { get; set; }

    // ── Champs Demozoo (productions_productionlink) ───────────────────────────
    public string? LinkClass      { get; set; }   // ex: "YoutubeVideo", "VimeoVideo", "SceneOrgFile"…
    public string? LinkParameter { get; set; }   // ex: ID YouTube ou Vimeo (champ "parameter" Demozoo)

    // Helpers
    public bool IsYouTube => LinkClass == "YoutubeVideo";
    public bool IsVimeo   => LinkClass == "VimeoVideo";
    public bool IsVideo   => IsYouTube || IsVimeo;

    /// <summary>URL de téléchargement effective. 2026-07-25 (retour utilisateur :
    /// "Return to Promised Land", Demozoo #394835, plateforme Commodore 16/Plus4) —
    /// certaines classes de lien Demozoo ne remplissent JAMAIS le champ "url" Postgres
    /// (donc "Url" reste NULL après import), l'URL réelle n'existant que dans
    /// "LinkParameter" ("parameter" côté Demozoo). C'est notamment le cas de la classe
    /// "BaseUrl" : pour cette classe, "parameter" contient DÉJÀ l'URL absolue complète
    /// (c'est comme ça que Demozoo construit lui-même ses liens de téléchargement côté
    /// site web — même principe que "YoutubeVideo"/"VimeoVideo", où DemozooImportService
    /// reconstruit déjà "Url" à partir de "LinkParameter" lors de l'import, cf. étape 4
    /// de finalisation). DemozooImportService backfille désormais "Url" pour "BaseUrl"
    /// de la même façon — cette propriété reste nécessaire pour les lignes importées
    /// AVANT ce correctif (tant qu'une réimportation complète n'a pas eu lieu) et sert
    /// de filet de sécurité générique pour tout lien "BaseUrl" mal peuplé.</summary>
    public string? EffectiveDownloadUrl =>
        !string.IsNullOrEmpty(Url) ? Url :
        (LinkClass == "BaseUrl" && !string.IsNullOrEmpty(LinkParameter)) ? LinkParameter :
        null;
}

// ─── Emulator & config ───────────────────────────────────────────────────────

public class Emulator : BaseEntity
{
    public string       Name            { get; set; } = string.Empty;
    public string       Version         { get; set; } = string.Empty;
    public string       ExecutablePath  { get; set; } = string.Empty;
    public string?      DefaultArgs     { get; set; }
    public string?      Website         { get; set; }
    public string?      Notes           { get; set; }
    public EmulatorStatus Status        { get; set; } = EmulatorStatus.Active;
    public EmulatorType   EmulatorType  { get; set; } = EmulatorType.Generic;

    public ICollection<EmulatorConfig>  Configurations { get; set; } = [];
}

// ─── EmulatorSetting : paires clé/valeur par PROFIL de lancement ─────────────
// Permet de stocker des paramètres spécifiques à chaque type d'émulateur sans
// modifier le schéma principal. Rattaché à EmulatorConfig (le "profil") et non
// à Emulator : deux profils du même émulateur (ex. "Atari ST 512K" et "Atari ST
// 1024K" sur Hatari) peuvent ainsi avoir des réglages différents.
// Ex : (WinUAE) KickstartPath, ChipRam, FastRam, CpuModel, AmigaModel...

public class EmulatorSetting
{
    public int     Id               { get; set; }
    public int     EmulatorConfigId { get; set; }
    public EmulatorConfig EmulatorConfig { get; set; } = null!;
    public string  Key              { get; set; } = string.Empty;
    public string? Value            { get; set; }
}

/// <summary>
/// Profil de lancement pour un émulateur sur une plateforme donnée.
/// Variables disponibles dans CommandLine et WorkingDirectory :
///   {file}     → chemin absolu du fichier à lancer
///   {dir}      → répertoire contenant le fichier
///   {filename} → nom du fichier sans extension
///   {ext}      → extension du fichier
/// </summary>
public class EmulatorConfig : BaseEntity
{
    public int     EmulatorId      { get; set; }
    public Emulator Emulator       { get; set; } = null!;
    public int     PlatformId      { get; set; }
    public Platform Platform       { get; set; } = null!;
    public string  ProfileName     { get; set; } = "Default";
    public string  CommandLine     { get; set; } = string.Empty;  // ex: -f {file} -s 3
    public string? WorkingDirectory { get; set; }                 // ex: {dir} ou chemin fixe
    public string? ConfigFilePath  { get; set; }                  // fichier de config à passer
    public bool    IsDefault       { get; set; } = false;
    public bool    FullScreen      { get; set; } = false;
    public string? PreLaunchScript { get; set; }                  // script batch avant lancement
    public string? Notes           { get; set; }

    public ICollection<EmulatorSetting> Settings { get; set; } = [];
}


// ─── DAT Entries ─────────────────────────────────────────────────────────────
// Une entrée par machine trouvée dans les fichiers DAT, identifiée par DemozooId.

public class DatEntry
{
    public int    Id          { get; set; }
    public int    DemozooId   { get; set; }  // lien vers Releases.DemozooId
    public string RomPath     { get; set; } = string.Empty; // chemin relatif du .zip
    public string SourceFile  { get; set; } = string.Empty; // fichier DAT source (relatif)

    public ICollection<DatRom> Roms { get; set; } = [];

    /// <summary>
    /// Vrai si ce DatEntry provient d'un DAT "Sources Code" (SourceFile contient "Sources
    /// Code", ex. "Ressources\Sources Codes\....dat") — affiché dans l'onglet dédié "Code
    /// Sources" de ReleaseDetailView au lieu de "Fichiers". Ce ne sont pas des fichiers
    /// jouables/lançables : source de vérité unique pour exclure ces entrées de toute
    /// logique de sélection/lancement automatique (AutoSelectDatEntry, HasAnyFile,
    /// ShowGraphicsAsync, PlayMusicReleaseAsync, PlayVideoInlineAsync, companions,
    /// ReleaseService.LaunchAsync…) — voir RESUME_PROJET.md.
    /// </summary>
    public bool IsCodeSourceEntry =>
        SourceFile.Contains("Sources Code", StringComparison.OrdinalIgnoreCase);
}

public class DatRom
{
    public int      Id          { get; set; }
    public int      DatEntryId  { get; set; }
    public DatEntry DatEntry    { get; set; } = null!;
    public string   Name        { get; set; } = string.Empty;
    public long     Size        { get; set; }
    public string?  Crc32       { get; set; }
    public string?  Md5         { get; set; }
    public string?  Sha1        { get; set; }
}

// Version des fichiers DAT importés (1 ligne par fichier DAT)
public class DatFileVersion
{
    public int    Id        { get; set; }
    public string FileName  { get; set; } = string.Empty; // chemin relatif dans DATS/
    public string Version   { get; set; } = string.Empty; // "2026-06-05"
}

// ─── FavoriteSoundtrack (soundtracks favoris — stocké dans config.db) ──────────

public class FavoriteSoundtrack
{
    public int      Id              { get; set; }
    public int      SoundtrackDemozooId { get; set; }  // DemozooId de la release soundtrack
    public string   Title           { get; set; } = string.Empty;
    public string?  AuthorNames     { get; set; }
    public string?  RomName         { get; set; }   // fichier .mod/.s3m dans le ZIP
    public string?  ZipPath         { get; set; }   // DatEntry.RomPath
    public string?  ReleaseTitle    { get; set; }   // titre de la release parente
    public DateTime AddedAt         { get; set; } = DateTime.UtcNow;
}

// ─── Playlist (playlists de soundtracks favoris — stocké dans config.db) ──────

public class Playlist
{
    public int      Id        { get; set; }
    public string   Name      { get; set; } = string.Empty;
    public int      SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ─── PlaylistTrack (lien playlist → soundtrack favori, avec position) ─────────

public class PlaylistTrack
{
    public int      Id                  { get; set; }
    public int      PlaylistId          { get; set; }
    public int      SoundtrackDemozooId { get; set; }
    public int      Position            { get; set; }
    public DateTime AddedAt             { get; set; } = DateTime.UtcNow;
}

// ─── ReleaseSoundtrack (lien release → soundtrack) ────────────────────────────

public class ReleaseSoundtrack
{
    public int     Id           { get; set; }
    public int     ReleaseId    { get; set; }
    public Release Release      { get; set; } = null!;
    public int     SoundtrackId { get; set; }
    public Release Soundtrack   { get; set; } = null!;
}

// ─── MediaFile (screenshots, vidéos, musiques) ───────────────────────────────

public class MediaFile : BaseEntity
{
    public int ReleaseId { get; set; }
    public Release Release { get; set; } = null!;
    public MediaType Type { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? Title { get; set; }
    public int SortOrder { get; set; } = 0;
    public string? Format { get; set; }
    public int? DurationSeconds { get; set; }
}

// ─── FavoriteGraphic (graphiques favoris — stocké dans config.db) ─────────────

public class FavoriteGraphic
{
    public int      Id              { get; set; }
    public int      ReleaseDemozooId { get; set; }  // DemozooId de la release graphics
    public string   Title           { get; set; } = string.Empty;
    public string?  AuthorNames     { get; set; }
    public string?  ZipPath         { get; set; }   // chemin relatif du ZIP (DatEntry.RomPath)
    public string?  FileInZip       { get; set; }   // fichier .ANS/.PNG/... dans le ZIP
    public DateTime AddedAt         { get; set; } = DateTime.UtcNow;
}
