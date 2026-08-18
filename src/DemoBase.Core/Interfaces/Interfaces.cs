using DemoBase.Core.Models;
using DemoBase.Core.DTOs;

namespace DemoBase.Core.Interfaces;

// ─── Generic repository ───────────────────────────────────────────────────────

public interface IRepository<T> where T : BaseEntity
{
    Task<T?> GetByIdAsync(int id);
    Task<IEnumerable<T>> GetAllAsync();
    Task<T> AddAsync(T entity);
    Task UpdateAsync(T entity);
    Task DeleteAsync(int id);
}

// ─── Specialized repositories ────────────────────────────────────────────────

public interface IReleaseRepository : IRepository<Release>
{
    Task<ReleaseSummaryDto?> GetRandomMusicAsync(IReadOnlySet<int> excludedIds);

    /// <summary>
    /// Pioche une release sortie ce jour (mois+jour) une année passée quelconque ;
    /// si aucune ne correspond, pioche une release aléatoire dans tout le catalogue.
    /// IsExactMatch indique si la pioche correspond bien à "ce jour-là".
    /// </summary>
    Task<(ReleaseSummaryDto? Release, bool IsExactMatch)> GetOnThisDayOrRandomAsync(int month, int day);
    Task<IEnumerable<DatEntry>> GetDatEntriesAsync(int demozooId);
    Task<Dictionary<int, DatEntry>> GetDatEntriesForDemozooIdsAsync(IEnumerable<int> demozooIds);
    Task<int?> GetIdByDemozooIdAsync(int demozooId);
    // 2026-07-31, retour utilisateur (log de perf fourni) : recherche qui ralentit
    // de plus en plus à chaque frappe (4s, 14s, 15s, 17s...) — cf. commentaire sur
    // ReleaseService.SearchAsync (Services.cs) pour le diagnostic complet. CancellationToken
    // ajouté ici pour que l'appelant (ReleaseListViewModel/MediaBrowserViewModel) puisse
    // RÉELLEMENT interrompre une recherche obsolète côté SQLite (sqlite3_interrupt via
    // Microsoft.Data.Sqlite) au lieu de juste ignorer son résultat après coup — sans ça, les
    // requêtes abandonnées continuent de tourner jusqu'au bout et s'empilent sur l'unique
    // connexion partagée (EF Core + SQLite n'autorisent qu'une requête à la fois dessus).
    Task<IEnumerable<Release>> SearchAsync(ReleaseSearchFilter filter, CancellationToken ct = default);
    Task<int>                  CountAsync(ReleaseSearchFilter filter, CancellationToken ct = default);
    Task<IEnumerable<Release>> GetByReleaserAsync(int releaserId);

    /// <summary>
    /// Rôles tenus par ce releaser (via ReleaseCredits, résolu par Nicks)
    /// pour chaque release où il est crédité. Clé = ReleaseId, valeur =
    /// libellé du rôle (ex. "Music", "Graphics"). Ne contient une entrée
    /// que pour les releases où la personne a un crédit détaillé — les
    /// releases où elle n'apparaît qu'en ReleaseAuthors (auteur principal,
    /// sans rôle précis) n'y figurent pas.
    /// </summary>
    Task<Dictionary<int, string>> GetCreditedRolesByReleaserAsync(int releaserId);

    Task<IEnumerable<Release>> GetByPartyAsync(int partyId);
    Task<IEnumerable<Release>> GetByPlatformAsync(int platformId);
    Task<List<int>> GetAvailableYearsAsync();
    Task<IEnumerable<Release>> GetByReleaseTypeAsync(int releaseTypeId);
    Task<Release?> GetWithFullDetailsAsync(int id);
    Task<Dictionary<int, string>> GetAuthorNamesByReleaseIdsAsync(List<int> releaseIds);

    /// <summary>Incrémente le compteur de vues et retourne sa nouvelle valeur.</summary>
    Task<int> IncrementViewCountAsync(int releaseId);

    /// <summary>Remet à zéro le compteur de vues de toutes les releases.</summary>
    Task ResetAllViewCountsAsync();
}

public interface IReleaserRepository : IRepository<Releaser>
{
    Task<Releaser?> GetWithNicksAndMembersAsync(int id);
    Task<(IEnumerable<Releaser> Items, int Total)> SearchPagedAsync(string? query, bool? isGroup, int page, int pageSize, string? letterFilter = null);
    Task<IEnumerable<Releaser>> SearchByNameAsync(string name);
    Task<IEnumerable<Releaser>> GetGroupsAsync();
    Task<IEnumerable<Releaser>> GetScenersAsync();
}

public interface IPartyRepository : IRepository<Party>
{
    Task<Party?> GetWithCompetitionsAsync(int id);
    Task<IEnumerable<Party>> GetBySeriesAsync(int partySeriesId);
    Task<(IEnumerable<Party> Items, int Total)> SearchPagedAsync(string? query, int page, int pageSize, int? year = null, string sortMode = "alpha");
    Task<List<int>> GetAvailableYearsAsync();
    Task<Dictionary<int, int>> GetReleaseCountsByPartyIdsAsync(IEnumerable<int> partyIds);
}

public interface IEmulatorRepository : IRepository<Emulator>
{
    Task<IEnumerable<EmulatorConfig>> GetConfigsForPlatformAsync(int platformId);
    Task<EmulatorConfig?> GetDefaultConfigAsync(int platformId);
    Task<EmulatorConfig?> GetConfigByIdAsync(int configId);
    Task<IEnumerable<Emulator>> GetAllWithConfigsAsync();
    Task<EmulatorConfig> AddConfigAsync(EmulatorConfig config);
    Task UpdateConfigAsync(EmulatorConfig config);
    Task DeleteConfigAsync(int configId);
    // Settings clé/valeur spécifiques au type d'émulateur, scopés par PROFIL (EmulatorConfig)
    Task<Dictionary<string, string?>> GetSettingsAsync(int emulatorConfigId);
    Task SaveSettingAsync(int emulatorConfigId, string key, string? value);
    Task SaveSettingsAsync(int emulatorConfigId, Dictionary<string, string?> settings);
    // Un seul profil "par défaut" par plateforme (tous émulateurs confondus)
    Task ClearDefaultForPlatformAsync(int platformId, int exceptConfigId);
    // Prochain Id disponible >= 100 pour un émulateur créé manuellement
    Task<int> NextManualIdAsync();
    // Toutes les configs d'un émulateur donné (pour "Appliquer à toutes")
    Task<IEnumerable<EmulatorConfig>> GetConfigsForEmulatorAsync(int emulatorId);
    // Id de toutes les Platform ayant au moins une EmulatorConfig — une seule requête
    // groupée (pas de N+1), utilisé par PlatformListViewModel pour le fond rouge "pas de
    // config" (2026-07-24, remplace l'ancienne liste figée PlatformNotEmulatedConverter).
    Task<HashSet<int>> GetConfiguredPlatformIdsAsync();
}

public interface IReleaseTypeRepository : IRepository<ReleaseType>
{
    Task<ReleaseType?> GetByNameAsync(string name);
    Task<IEnumerable<ReleaseTypeDto>> GetAllWithCountAsync();
    Task<bool> IsInUseAsync(int id);
}

// ─── Unit of Work ─────────────────────────────────────────────────────────────

public interface IUnitOfWork : IDisposable
{
    IReleaseRepository     Releases      { get; }
    IReleaserRepository    Releasers     { get; }
    IPartyRepository       Parties       { get; }
    IEmulatorRepository    Emulators     { get; }
    IReleaseTypeRepository ReleaseTypes  { get; }
    IRepository<Platform>      Platforms     { get; }
    IRepository<PartySeries>   PartySeries   { get; }
    IRepository<Competition>   Competitions  { get; }
    IRepository<MediaFile>     MediaFiles    { get; }
    IRepository<ReleaseLink>   ReleaseLinks  { get; }
    IRepository<Nick>          Nicks         { get; }

    Task<int> SaveChangesAsync();
    Task BeginTransactionAsync();
    Task CommitTransactionAsync();
    Task RollbackTransactionAsync();
}

// ─── Application services ─────────────────────────────────────────────────────

public interface IReleaseService
{
    Task<ReleaseDetailDto> GetDetailAsync(int id);
    Task<IEnumerable<DemoBase.Core.Models.DatEntry>> GetDatEntriesAsync(int demozooId);
    Task<Dictionary<int, DemoBase.Core.Models.DatEntry>> GetDatEntriesForDemozooIdsAsync(IEnumerable<int> demozooIds);
    Task<PagedResult<ReleaseSummaryDto>> SearchAsync(ReleaseSearchFilter filter, CancellationToken ct = default);
    Task<List<int>> GetAvailableYearsAsync();
    Task<Release> CreateAsync(CreateReleaseDto dto);
    Task UpdateAsync(int id, UpdateReleaseDto dto);
    Task DeleteAsync(int id);
    // progress : rapporté uniquement pour une release sans DAT lancée directement depuis
    // son lien Demozoo (téléchargement ad-hoc) — cf. DemoBase.Core.DTOs.LaunchDownloadProgress.
    Task LaunchAsync(int releaseId, int? emulatorConfigId = null, string? romPathOverride = null,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null);
    Task<int?> GetIdByDemozooIdAsync(int demozooId);
    // Override de profil par release (debug uniquement, stocké dans config.db)
    Task<IEnumerable<EmulatorConfig>> GetAvailableProfilesForReleaseAsync(int releaseId);
    Task SetProfileOverrideAsync(int releaseId, int? emulatorConfigId);

    // Override de profil par FICHIER (2026-07-25) — releases multi-plateforme ET
    // multi-fichier (ex. Amiga AGA + Atari Falcon) : un override par release seul ne
    // suffit pas, chaque fichier (DatEntry, identifié par son RomPath — stable,
    // contrairement à DatEntry.Id qui change à chaque réimport DAT) peut viser une
    // plateforme différente. Cf. DatEntryProfileOverrideService.
    Task<int?> GetDatEntryProfileOverrideAsync(int demozooId, string romPath);
    Task SetDatEntryProfileOverrideAsync(int demozooId, string romPath, int? emulatorConfigId);
    Task<Dictionary<string, int>> GetDatEntryProfileOverridesForReleaseAsync(int demozooId);

    // Fichier préféré par release (2026-07-25) — quel DatEntry (RomPath) lancer parmi
    // plusieurs fichiers lançables. Mémorisé une fois choisi (bouton "Utiliser" ou
    // fenêtre de choix de fichier au clic sur "Lancer") — cf. ReleasePreferredFileService.
    Task<string?> GetPreferredFileAsync(int demozooId);
    Task SetPreferredFileAsync(int demozooId, string romPath);

    /// <summary>
    /// Incrémente le compteur de vues d'une release (appelé au clic sur Play/Afficher/
    /// Regarder/Lancer) et déclenche l'ajout automatique aux favoris si le seuil
    /// configuré dans les préférences est atteint. Retourne le nouveau compteur de vues
    /// et l'état IsFavorite à jour (pour rafraîchir l'UI sans recharger toute la fiche,
    /// ce qui couperait un lecteur média en cours d'initialisation).
    /// </summary>
    Task<(int ViewCount, bool IsFavorite)> IncrementViewCountAsync(int releaseId, bool currentIsFavorite = false);
    /// <summary>Retourne une release musicale aléatoire parmi les 130 000+,
    /// en excluant les IDs déjà joués dans la session shuffle courante.</summary>
    Task<ReleaseSummaryDto?> GetRandomMusicReleaseAsync(IReadOnlySet<int> excludedIds);

    /// <summary>
    /// "On this day" : pioche une release sortie ce jour (mois+jour) une année passée
    /// quelconque, dans tout le catalogue ; si aucune ne correspond, pioche une release
    /// aléatoire dans tout le catalogue. IsExactMatch indique laquelle des deux.
    /// </summary>
    Task<(ReleaseSummaryDto? Release, bool IsExactMatch)> GetOnThisDayOrRandomReleaseAsync(int month, int day);

    /// <summary>Remet à zéro le compteur de vues de toutes les releases.</summary>
    Task ResetAllViewCountsAsync();

    /// <summary>
    /// Télécharge (sans lancer d'émulateur) le fichier d'une release pas encore
    /// couverte par un DAT mais possédant un lien de téléchargement direct Demozoo —
    /// même sélection de lien que <see cref="LaunchAsync"/> (IsMainFile, copie locale
    /// prioritaire, EffectiveDownloadUrl). Pour les chemins Music/Graphics, qui
    /// n'appellent jamais LaunchAsync directement. Retourne le chemin local du fichier
    /// (souvent un .zip, à traiter comme un fichier DAT résolu) ou null si aucun lien
    /// exploitable / échec. 2026-07-26, retour utilisateur.
    /// </summary>
    Task<string?> DownloadAdHocFileAsync(int releaseId,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null);
}

public interface IReleaseTypeService
{
    Task<IEnumerable<ReleaseTypeDto>> GetAllAsync();
    Task<ReleaseType> CreateAsync(CreateReleaseTypeDto dto);
    Task UpdateAsync(int id, CreateReleaseTypeDto dto);
    Task DeleteAsync(int id);
}

public interface IEmulatorService
{
    Task<bool> TestExecutableAsync(int emulatorId);
    // progress : rapporté uniquement quand le fichier n'est pas encore local et doit être
    // téléchargé à la volée depuis le lien Demozoo (release pas encore couverte par un
    // DAT, cf. DemoBase.Core.DTOs.LaunchDownloadProgress, 2026-07-25).
    Task LaunchReleaseAsync(ReleaseLink file, EmulatorConfig config,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null);
    Task LaunchReleaseAsync(string romPath, Release release, EmulatorConfig config);
    Task<string> BuildCommandLineAsync(EmulatorConfig config, string filePath);

    // 2026-07-26 : résout (télécharge si besoin, SANS lancer d'émulateur ensuite) le
    // fichier local d'un lien de téléchargement direct Demozoo — pour les releases
    // Music/Graphics qui n'ont pas de profil émulateur mais peuvent quand même avoir
    // besoin du même mécanisme de téléchargement ad-hoc que le bouton "Lancer"
    // générique (release pas encore couverte par un DAT). Retour utilisateur :
    // badge "Fichier externe" affiché mais bouton "Play" sans effet sur les releases
    // Music/Graphics — ce système n'était branché que sur le chemin émulateur.
    Task<string?> ResolveAdHocFileAsync(Release release, ReleaseLink link,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null);
}

public interface IImportService
{
    Task<ImportResult> ImportFromMySqlAsync(MySqlImportOptions options, IProgress<ImportProgress>? progress = null);
    Task<ImportResult> ImportFromCsvAsync(string csvPath, IProgress<ImportProgress>? progress = null);
}

public interface IMediaService
{
    Task<string> AddScreenshotAsync(int releaseId, string sourcePath);
    Task<string> AddVideoAsync(int releaseId, string sourcePath);
    Task<string> AddMusicAsync(int releaseId, string sourcePath);
    Task DeleteMediaAsync(int mediaFileId);
    Task<byte[]?> GetThumbnailAsync(int mediaFileId);
}

public interface INavigationService
{
    void NavigateTo<TViewModel>(object? parameter = null, object? tag = null) where TViewModel : class;
    void NavigateTo(Type viewModelType, object? parameter = null, object? tag = null);
    void GoBack();
    bool CanGoBack { get; }
    event EventHandler<NavigationEventArgs>? Navigated;
}
