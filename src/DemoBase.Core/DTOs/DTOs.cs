using System.IO;
using DemoBase.Core.Models;

namespace DemoBase.Core.DTOs;

// ─── Filters & Pagination ─────────────────────────────────────────────────────

public class ReleaseSearchFilter
{
    /// <summary>
    /// Si true, le COUNT n'est pas refait (utilisé pour LoadMore où le total est déjà connu).
    /// </summary>
    public bool   SkipCount    { get; set; } = false;
    public int    KnownTotal   { get; set; } = 0;

    public string? Query        { get; set; }
    public int?    ReleaseTypeId { get; set; }
    public string? Supertype    { get; set; }   // "production" | "graphics" | "music"
    public int?    ReleaserId   { get; set; }   // auteur ou groupe
    public int?    PlatformId   { get; set; }
    public int?    PartyId      { get; set; }
    public string? YearFrom     { get; set; }   // "1993"
    public string? YearTo       { get; set; }
    public bool?   IsFavorite   { get; set; }
    /// <summary>Si true, ne retourne que les releases jamais lancées/jouées (ViewCount == 0).</summary>
    public bool?   IsUnseen     { get; set; }
    /// <summary>Si true, ne retourne que les releases ayant au moins un fichier (DatEntry).</summary>
    public bool?   HasDatEntry  { get; set; }
    /// <summary>Si true, la recherche textuelle ne porte que sur les auteurs (ReleaseAuthors),
    /// pas sur les crédits techniques (ReleaseCredits). Utilisé par le MediaBrowser.</summary>
    public bool    AuthorsOnly  { get; set; }
    /// <summary>Si true, la recherche textuelle ne porte QUE sur le titre de la release
    /// (ni auteurs, ni crédits). Prioritaire sur <see cref="AuthorsOnly"/> si les deux sont
    /// positionnés. Utilisé par le MediaBrowser (bascule Auteur/Titre).</summary>
    public bool    TitleOnly    { get; set; }
    public int     Page         { get; set; } = 1;
    public int     PageSize     { get; set; } = 50;
    public string  SortBy       { get; set; } = "Title";
    public bool    SortDescending { get; set; } = false;
}

public class PagedResult<T>
{
    public IEnumerable<T> Items      { get; set; } = [];
    public int            TotalCount { get; set; }
    public int            Page       { get; set; }
    public int            PageSize   { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}

// ─── Release DTOs ─────────────────────────────────────────────────────────────

public class ReleaseSummaryDto : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public int     Id              { get; set; }
    public string  Title           { get; set; } = string.Empty;
    public string  Supertype       { get; set; } = string.Empty;
    public int?    DemozooId       { get; set; }
    public int?    ReleaseTypeId   { get; set; }
    public string  ReleaseTypeName { get; set; } = string.Empty;
    public string  ReleaseDate     { get; set; } = string.Empty;
    public string  AuthorNames     { get; set; } = string.Empty;  // "Future Crew"
    public string  PlatformNames   { get; set; } = string.Empty;  // "Amiga OCS/ECS"
    public string  ReleaseYear     { get; set; } = string.Empty;  // "1993"
    public string? MainFileExt     { get; set; }  // ".mod", ".xm", ".s3m"… (premier fichier DAT)
    public int?    BestRank        { get; set; }
    public string? BestCompetition { get; set; }
    /// <summary>Vrai si Demozoo ne référence aucun fichier exploitable pour cette release
    /// (ni DatEntry connu, ni ReleaseLink hors référence vidéo YouTube/Vimeo) — cliquer sur
    /// "Lancer" échouera systématiquement avec "Fichier introuvable". Calculé en masse par
    /// ReleaseService.SearchAsync/GetByReleaserAsync pour être affiché dans les listes sans
    /// devoir cliquer sur chaque release.</summary>
    public bool    HasNoFile       { get; set; }
    public bool    IsFavorite      { get; set; }
    public int     ViewCount       { get; set; }
    private string? _thumbnailPath;
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set { if (_thumbnailPath == value) return; _thumbnailPath = value; OnPropertyChanged(nameof(ThumbnailPath)); }
    }

    public bool    HasLocalVideo   { get; set; }

    /// <summary>
    /// Rôle tenu par le releaser courant (celui de la fiche affichée) sur
    /// cette release précise, ex. "Music", "Graphics", "Code" — vient de
    /// ReleaseCredits.Role. Null si la personne est auteur principal
    /// (ReleaseAuthors) sans rôle de crédit détaillé spécifique enregistré.
    /// Utilisé pour afficher "(Music)" à côté du titre dans la fiche
    /// releaser, afin de distinguer en quoi la personne a contribué.
    /// </summary>
    public string? CreditedRole    { get; set; }
}

// Extensions jouables par le TrackerPlayer
public static class TrackerExtensions
{
    public static readonly HashSet<string> Playable =
        [".mod", ".s3m", ".xm", ".it", ".sndh", ".dbm", ".stm", ".ft2", ".669", ".mtm",
         ".stk",   // ProTracker startup format (même format que .mod)
         // Formats ZXTune Amiga exotiques
         ".ahx",   // AHX / Abyss' Highest eXperience (ZXTune)
         ".hvl",   // HivelyTracker (ZXTune)
         ".med",   // OctaMED
         ".okt",   // Oktalyzer
         ".okta",  // Oktalyzer variante
         // 2026-07-31, retour utilisateur : "les fichiers .mmd0, .mmd1, .mmd2, .mmd3
         // et .okta doivent passer par libopenmpt avec une vue ft2" — OctaMED
         // MMD0/MMD1/MMD2/MMD3 (variantes de conteneur du même format .med),
         // absentes jusqu'ici de toute liste (ni ZXTune, ni UADE, ni libopenmpt) —
         // routées maintenant vers libopenmpt (cf. NativeTrackerPlayer.LibopenmptExtensions).
         // ".med"/".okt"/".okta" retirés de ZXTune au profit de libopenmpt à cette
         // occasion (cf. ExternalPlayers.cs) — le commentaire "libopenmpt prioritaire"
         // qui existait ici était erroné : ZXTuneDecoder.CanDecode() renvoie toujours
         // true, donc ZXTune gagnait en réalité systématiquement la 1ère boucle de
         // sélection tant que ces extensions restaient dans ZXTunePlayer.SupportedExtensions.
         ".mmd0", ".mmd1", ".mmd2", ".mmd3", // OctaMED (MMD0-3, mêmes variantes que .med)
         ".dmf",   // DigiBoosterPro / Digital Music Federation (libopenmpt)
         // Formats ZXTune (Atari ST, Amstrad, consoles)
         ".ym", ".ym2", ".ym3", ".ym4", ".ym5", ".ym6",
         ".ay", ".vtx", ".psg", ".spc", ".nsf", ".nsfe", ".gbs", ".vgm", ".vgz",
         ".sap", ".rmt", ".sid", ".psid", ".sqt",
         // Formats ZXTune ZX Spectrum / ProTracker ZX
         ".pt1", ".pt2", ".pt3",   // Pro Tracker 1/2/3 (ZX Spectrum)
         // 2026-07-31 : ".stp" retiré du commentaire "Sound Tracker ZX" — routé vers
         // libopenmpt depuis ce jour (Soundtracker Pro II, Atari Falcon), pas ZXTune,
         // cf. NativeTrackerPlayer.LibopenmptExtensions. Reste dans Playable : peu
         // importe le backend, le fichier est bien jouable.
         ".stc", ".stp", ".st1", ".st3",  // Sound Tracker ZX (".stp" = Falcon, cf. ci-dessus)
         ".asc",   // ASC Sound Master
         ".ftc",   // Fast Tracker (ZX)
         ".gtr",   // Global Tracker
         ".psc",   // Pro Sound Creator
         // Formats UADE (Amiga exotiques)
         ".bp", ".bp3",
         // David Whittaker format — joué par UADE (.dw)
         ".dw",
         // Ben Daglish format — joué par UADE (*.bd)
         ".bd",
         // SUNtronic — format Amiga des Sunriders, joué par UADE
         ".sun",
         // 2026-08-04, retour utilisateur ("les fichiers .v2m doivent passer par
         // zxtune et non uade") : Farbrausch V2M, joué par ZXTune (cf.
         // ZXTunePlayer.SupportedExtensions, ExternalPlayers.cs) — absent d'ici
         // jusqu'ici, comme de toute autre liste d'extensions.
         ".v2m",
         // Audio courant
         ".mp3", ".m4a", ".flac", ".ogg", ".wav", ".aiff"];

    public static bool IsPlayable(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (Playable.Contains(ext)) return true;

        // Formats inversés : "MOD.nom", "XM.nom", "S3M.nom", "BD.nom" (Ben Daglish), etc.
        // 2026-07-30 : "MDAT." ajouté — format TFMX (Amiga, joué par UADE), toujours nommé
        // "mdat.<suffixe>" (pas d'extension classique). Contrairement aux autres préfixes de
        // cette liste, ce n'est PAS un nom inversé à corriger — NormalizeFilename n'inclut donc
        // volontairement PAS "MDAT" (le renommer casserait la recherche par répertoire du
        // fichier compagnon "smpl.<suffixe>" côté TrackerPlayer.Core/ExternalPlayers.cs, qui
        // attend littéralement le préfixe "mdat."). Cf. aussi CompanionFilePairs
        // (ReleaseViewModels.cs) qui gère le cas où mdat.*/smpl.* proviennent de deux DatEntry
        // différents.
        // 2026-07-31 : "THM." ajouté — format Thomas Hermann (UADE), même principe que MDAT,
        // toujours nommé "thm.<suffixe>" avec compagnon "smp.<suffixe>" (cf.
        // TrackerPlayer.Core.Players.UadeCompanionFormats). Même raison de ne PAS l'ajouter à
        // NormalizeFilename ci-dessous.
        // 2026-08-07 : "TPU." (Dirk Bialluch, ajouté le 2026-07-31 à UadeCompanionFormats/
        // UadeDecoder.KnownPrefixes/ReleaseViewModels.CompanionFilePairs mais oublié ICI) et
        // "SJS." (retour utilisateur : "les fichiers sjs.* doivent etre accompagné des
        // fichiers smp.*, tous comme les tfmx") ajoutés ensemble — même principe que MDAT/THM,
        // compagnon "smp.<suffixe>" pour les deux.
        var prefixes = new[] { "MOD.", "XM.", "S3M.", "IT.", "STM.", "DBM.", "SNDH.", "669.", "MTM.", "FT2.", "BD.", "STK.", "MDAT.", "THM.", "TPU.", "SJS." };
        var upper = filename.ToUpperInvariant();
        return prefixes.Any(p => upper.StartsWith(p));
    }

    /// <summary>Corrige "MOD.mysong" → "mysong.mod", "BD.mysong" → "mysong.bd" pour le TrackerPlayer.</summary>
    public static string NormalizeFilename(string name)
    {
        var prefixes = new[] { "MOD", "XM", "S3M", "IT", "STM", "DBM", "SNDH", "669", "MTM", "FT2", "BD", "STK" };
        foreach (var prefix in prefixes)
            if (name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                return name.Substring(prefix.Length + 1) + "." + prefix.ToLower();
        return name;
    }
}

// DTO pour un soundtrack enrichi avec ses ROMs jouables
public class SoundtrackDto : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public int     SoundtrackId    { get; set; }
    public Release Soundtrack      { get; set; } = null!;
    public bool    HasPlayableRom  { get; set; }
    public string? PlayableRomPath { get; set; }
    public string? ZipPath         { get; set; }
    public string? RomName         { get; set; }
    public string? AuthorNames     { get; set; }
    public string? ReleaseTitle    { get; set; }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite == value) return; _isFavorite = value; OnPropertyChanged(nameof(IsFavorite)); }
    }
}

public class ReleaseDetailDto
{
    public Release                        Release             { get; set; } = null!;
    public IEnumerable<ReleaseAuthorDto>  Authors             { get; set; } = [];
    public IEnumerable<CreditDto>         Credits             { get; set; } = [];
    public IEnumerable<PlacingDto>        CompetitionPlacings { get; set; } = [];
    public IEnumerable<MediaFile>         Screenshots         { get; set; } = [];
    public IEnumerable<MediaFile>         Videos              { get; set; } = [];
    public IEnumerable<MediaFile>         MusicFiles          { get; set; } = [];
    public IEnumerable<SoundtrackDto>     Soundtracks         { get; set; } = [];
    public IEnumerable<ReleaseSoundtrack> UsedInReleases      { get; set; } = [];
    public bool                           HasUsedInReleases   => UsedInReleases.Any();
    public IEnumerable<ReleaseLink>       Links               { get; set; } = [];
    public IEnumerable<DatEntry>          DatFiles            { get; set; } = [];
    public EmulatorConfig?                DefaultEmulatorConfig { get; set; }
    // True si DefaultEmulatorConfig provient d'un override manuel pour CETTE release
    // (ReleaseProfileOverrideService) plutôt que du profil par défaut de la plateforme.
    public bool                           IsProfileOverridden   { get; set; }
}

public class ReleaseAuthorDto
{
    public int    ReleaserId       { get; set; }
    public string ReleaserName     { get; set; } = string.Empty;
    public bool   IsGroup          { get; set; }
    public string NickUsed         { get; set; } = string.Empty;
    public string? AffiliationName { get; set; }
}

public class CreditDto
{
    public int    ReleaserId { get; set; }
    public string Handle     { get; set; } = string.Empty;
    public string Role       { get; set; } = string.Empty;  // "code", "music", "graphics"…
    public string? Detail    { get; set; }
}

public class PlacingDto
{
    public int     CompetitionId   { get; set; }
    public string  CompetitionName { get; set; } = string.Empty;
    public int     PartyId         { get; set; }
    public string  PartyName       { get; set; } = string.Empty;
    public string? StartDate       { get; set; }
    public int?    Ranking         { get; set; }
    public string? Score           { get; set; }
}

// ─── ReleaseType DTOs ─────────────────────────────────────────────────────────

public class ReleaseTypeDto
{
    public int    Id           { get; set; }
    public string Name         { get; set; } = string.Empty;
    public string Supertype    { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int    SortOrder    { get; set; }
    public int    ReleaseCount { get; set; }
}

public class CreateReleaseTypeDto
{
    public string  Name        { get; set; } = string.Empty;
    public string  Supertype   { get; set; } = "production";
    public string? Description { get; set; }
    public int     SortOrder   { get; set; } = 0;
}

// ─── Create / Update Release DTOs ─────────────────────────────────────────────

public class CreateReleaseDto
{
    public string  Title           { get; set; } = string.Empty;
    public string  Supertype       { get; set; } = "production";
    public int?    ReleaseTypeId   { get; set; }
    public string? ReleaseDate     { get; set; }
    public string? ReleaseDatePrecision { get; set; }
    public string? Notes           { get; set; }
}

public class UpdateReleaseDto : CreateReleaseDto
{
    public int     Id         { get; set; }
    public bool    IsFavorite { get; set; }
    public int?    Rating     { get; set; }
    public string? DemozooUrl { get; set; }
    public string? PouetUrl   { get; set; }
    public string? CsdbUrl    { get; set; }
    public string? Tags       { get; set; }
}

// ─── Import ───────────────────────────────────────────────────────────────────

public class MySqlImportOptions
{
    public string Host     { get; set; } = "localhost";
    public int    Port     { get; set; } = 3306;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool   OverwriteExisting { get; set; } = false;
    public bool   ImportMedia       { get; set; } = true;
    public string? MediaSourceRoot  { get; set; }
}

public class ImportResult
{
    public bool     Success          { get; set; }
    public int      ReleasesImported { get; set; }
    public int      ReleasersImported { get; set; }
    public int      PartiesImported  { get; set; }
    public int      ReleaseTypesImported { get; set; }
    public int      Errors           { get; set; }
    public List<string> ErrorMessages { get; set; } = [];
    public TimeSpan Duration         { get; set; }
}

public class ImportProgress
{
    public string Stage   { get; set; } = string.Empty;
    public int    Current { get; set; }
    public int    Total   { get; set; }
    public double Percent => Total > 0 ? (double)Current / Total * 100 : 0;
}

// ─── Misc ─────────────────────────────────────────────────────────────────────

public class NavigationEventArgs : EventArgs
{
    public Type    ViewModelType { get; set; } = null!;
    public object? Parameter    { get; set; }
    public object? Tag          { get; set; }  // donnée auxiliaire (ex: nom plateforme)
}

/// <summary>
/// Progression d'un téléchargement ad-hoc déclenché au lancement (release pas encore
/// couverte par un DAT, fichier récupéré directement depuis le lien Demozoo) — cf.
/// EmulatorLaunchService.DownloadAndExtractAsync / ReleaseDetailViewModel.LaunchAsync
/// (2026-07-25). Type volontairement déclaré dans Core (pas dans DemoBase.App) : à la
/// fois IEmulatorService et IReleaseService en ont besoin dans leur signature, et Core
/// ne doit pas dépendre de App.
///
/// <paramref name="IsError"/> (2026-07-27, retour utilisateur : popup système "Erreur de
/// lancement" affichée par-dessus l'overlay de progression déjà visible lors d'un échec
/// réseau — ex. connexion refusée — demande explicite de l'afficher DANS l'overlay à la
/// place) : permet à <c>ReleaseService.LaunchAsync</c> de relayer une exception survenue
/// pendant le téléchargement via ce même canal <c>IProgress&lt;LaunchDownloadProgress&gt;</c>
/// plutôt que par une MessageBox — l'appelant (ReleaseDetailViewModel) route alors le
/// message vers <c>BuildErrorMessage</c>, déjà câblé sur l'overlay avec un bouton OK.
/// Par défaut à <c>false</c> pour ne rien casser des appelants existants.
/// </summary>
public record LaunchDownloadProgress(string Message, int Percent, bool IsError = false);
