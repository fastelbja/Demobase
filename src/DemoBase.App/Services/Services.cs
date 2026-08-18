using DemoBase.Core.Diagnostics;
using DemoBase.Core.DTOs;
using DemoBase.Core.Interfaces;
using DemoBase.Core.Models;
using DemoBase.Data;
using System.IO;
using System.IO.Compression;

namespace DemoBase.App.Services;

// ─── Release Service ──────────────────────────────────────────────────────────

public class ReleaseService : IReleaseService
{
    private readonly IUnitOfWork              _uow;
    private readonly IEmulatorService         _emulatorService;
    private readonly PreferencesService       _prefsService;
    private readonly FavoriteSoundtrackService? _favService;
    private readonly ReleaseProfileOverrideService? _overrideService;
    private readonly DatEntryProfileOverrideService? _datEntryOverrideService;
    private readonly ReleasePreferredFileService?    _preferredFileService;

    public ReleaseService(IUnitOfWork uow, IEmulatorService emulatorService,
                          PreferencesService prefsService,
                          FavoriteSoundtrackService? favService = null,
                          ReleaseProfileOverrideService? overrideService = null,
                          DatEntryProfileOverrideService? datEntryOverrideService = null,
                          ReleasePreferredFileService? preferredFileService = null)
    {
        _uow             = uow;
        _emulatorService = emulatorService;
        _prefsService    = prefsService;
        _favService      = favService;
        _overrideService = overrideService;
        _datEntryOverrideService = datEntryOverrideService;
        _preferredFileService    = preferredFileService;
    }

    public async Task<ReleaseDetailDto> GetDetailAsync(int id)
    {
        System.Diagnostics.Debug.WriteLine($"[GetDetailAsync] START id={id}");
        using (PerfLogger.Begin($"GetDetail[{id}].GetWithFullDetails"))
        {
        var release = await _uow.Releases.GetWithFullDetailsAsync(id)
            ?? throw new KeyNotFoundException($"Release {id} introuvable.");

        // Plateforme principale pour l'émulateur
        var mainPlatformId = release.ReleasePlatforms.FirstOrDefault()?.PlatformId;
        EmulatorConfig? defaultConfig = null;
        bool isOverridden = false;

        // Override par release (config.db, prioritaire sur le défaut de plateforme) —
        // uniquement possible pour les releases issues de Demozoo (clé = DemozooId).
        if (release.DemozooId.HasValue && _overrideService != null)
        {
            PerfLogger.Mark($"GetDetail.OverrideCheck dz={release.DemozooId}");
            var _sw_ov = System.Diagnostics.Stopwatch.StartNew();
            var overrideConfigId = await _overrideService.GetOverrideConfigIdAsync(release.DemozooId.Value);
            PerfLogger.Log("GetDetail.OverrideCheck", _sw_ov.ElapsedMilliseconds);
            if (overrideConfigId.HasValue)
            {
                defaultConfig = await _uow.Emulators.GetConfigByIdAsync(overrideConfigId.Value);
                isOverridden  = defaultConfig != null;
                // Si le profil référencé a été supprimé depuis, defaultConfig reste null
                // ici et on retombe normalement sur le défaut de plateforme ci-dessous.
            }
        }

        if (defaultConfig == null && mainPlatformId.HasValue)
        {
            using var _dc = PerfLogger.Begin("GetDetail.GetDefaultConfig");
            defaultConfig = await _uow.Emulators.GetDefaultConfigAsync(mainPlatformId.Value);
        }

        // Construire l'URL Demozoo depuis l'Id si non renseignée
        if (string.IsNullOrWhiteSpace(release.DemozooUrl) && release.DemozooId.HasValue)
            release.DemozooUrl = $"https://demozoo.org/productions/{release.DemozooId.Value}/";

        IEnumerable<DemoBase.Core.Models.DatEntry> datFiles;
        if (release.DemozooId.HasValue)
        {
            using var _d = PerfLogger.Begin($"GetDetail.DatEntries(dz={release.DemozooId})");
            datFiles = await _uow.Releases.GetDatEntriesAsync(release.DemozooId.Value);
        }
        else datFiles = [];

        IEnumerable<DemoBase.Core.DTOs.SoundtrackDto> soundtracks;
        using (PerfLogger.Begin("GetDetail.BuildSoundtrackDtos"))
            soundtracks = await BuildSoundtrackDtosAsync(release);

        return new ReleaseDetailDto
        {
            Release = release,
            Authors = release.Authors.Select(a => new ReleaseAuthorDto
            {
                ReleaserId      = a.Nick.ReleaserId,
                ReleaserName    = a.Nick.Releaser.DisplayName,
                IsGroup         = a.Nick.Releaser.IsGroup,
                NickUsed        = a.Nick.Name,
                AffiliationName = a.AffiliationNick?.Releaser.DisplayName,
            }).ToList(),
            Credits = release.Credits
                .GroupBy(c => c.ReleaserId)
                .OrderBy(g => g.First().Releaser.DisplayName)
                .Select(g =>
                {
                    var roles  = string.Join(", ", g
                        .Where(c => !string.IsNullOrWhiteSpace(c.Role))
                        .Select(c => c.Role).Distinct());
                    var detail = string.Join(", ", g
                        .Where(c => !string.IsNullOrWhiteSpace(c.Detail))
                        .Select(c => c.Detail).Distinct());
                    return new CreditDto
                    {
                        ReleaserId = g.Key,
                        Handle     = g.First().Releaser.DisplayName,
                        Role       = roles,
                        Detail     = string.IsNullOrEmpty(detail) ? null : detail,
                    };
                }).ToList(),
            CompetitionPlacings = release.CompetitionPlacings.Select(cp => new PlacingDto
            {
                CompetitionId   = cp.CompetitionId,
                CompetitionName = cp.Competition.Name,
                PartyId         = cp.Competition.PartyId,
                PartyName       = cp.Competition.Party.Name,
                StartDate       = cp.Competition.Party.StartDate,
                Ranking         = cp.Ranking,
                Score           = cp.Score,
            }).ToList(),
            Screenshots  = release.MediaFiles.Where(m => m.Type == Core.Enums.MediaType.Screenshot).OrderBy(m => m.SortOrder).ToList(),
            Videos       = release.MediaFiles.Where(m => m.Type == Core.Enums.MediaType.Video).ToList(),
            MusicFiles   = release.MediaFiles.Where(m => m.Type is Core.Enums.MediaType.ModMusic or Core.Enums.MediaType.AudioMusic).ToList(),
            Soundtracks    = soundtracks,
            UsedInReleases = release.UsedInReleases,
            Links          = release.Links,
            DatFiles       = datFiles,
            DefaultEmulatorConfig = defaultConfig,
            IsProfileOverridden   = isOverridden,
        };
        } // end using GetWithFullDetails
    }

    // ─── Override de profil par release (debug uniquement) ────────────────────

    public async Task<IEnumerable<EmulatorConfig>> GetAvailableProfilesForReleaseAsync(int releaseId)
    {
        var release = await _uow.Releases.GetWithFullDetailsAsync(releaseId)
            ?? throw new KeyNotFoundException($"Release {releaseId} introuvable.");

        // Une release peut être taguée sur plusieurs plateformes (ex. Atari ST + STE) :
        // on propose les profils de TOUTES ses plateformes, dédupliqués.
        var platformIds = release.ReleasePlatforms.Select(rp => rp.PlatformId).Distinct().ToList();
        var all = new List<EmulatorConfig>();
        foreach (var pid in platformIds)
            all.AddRange(await _uow.Emulators.GetConfigsForPlatformAsync(pid));

        return all.DistinctBy(c => c.Id).OrderBy(c => c.Emulator?.Name).ThenBy(c => c.ProfileName);
    }

    public async Task SetProfileOverrideAsync(int releaseId, int? emulatorConfigId)
    {
        if (_overrideService == null) return;
        var release = await _uow.Releases.GetByIdAsync(releaseId);
        if (release?.DemozooId == null) return;  // pas de release Demozoo → pas d'override possible
        await _overrideService.SetOverrideAsync(release.DemozooId.Value, emulatorConfigId);
    }

    // ─── Override de profil par FICHIER (2026-07-25) ───────────────────────────
    // Cf. DatEntryProfileOverrideService : releases multi-plateforme ET multi-fichier
    // (ex. Amiga AGA + Atari Falcon, plusieurs DatEntry/variantes) — un override par
    // release seul ne suffit pas, chaque fichier peut viser une plateforme différente.

    public async Task<int?> GetDatEntryProfileOverrideAsync(int demozooId, string romPath)
        => _datEntryOverrideService == null
            ? null
            : await _datEntryOverrideService.GetOverrideConfigIdAsync(demozooId, romPath);

    public async Task SetDatEntryProfileOverrideAsync(int demozooId, string romPath, int? emulatorConfigId)
    {
        if (_datEntryOverrideService == null) return;
        await _datEntryOverrideService.SetOverrideAsync(demozooId, romPath, emulatorConfigId);
    }

    public async Task<Dictionary<string, int>> GetDatEntryProfileOverridesForReleaseAsync(int demozooId)
        => _datEntryOverrideService == null
            ? new Dictionary<string, int>()
            : await _datEntryOverrideService.GetOverridesForReleaseAsync(demozooId);

    // ─── Fichier préféré par release (2026-07-25) ──────────────────────────────
    // Cf. ReleasePreferredFileService : mémorise quel DatEntry (via RomPath) lancer
    // pour une release multi-fichier, une fois choisi (bouton "Utiliser" ou fenêtre
    // de choix de fichier au clic sur "Lancer") — ne redemande plus jamais ensuite.

    public async Task<string?> GetPreferredFileAsync(int demozooId)
        => _preferredFileService == null
            ? null
            : await _preferredFileService.GetPreferredFileAsync(demozooId);

    public async Task SetPreferredFileAsync(int demozooId, string romPath)
    {
        if (_preferredFileService == null) return;
        await _preferredFileService.SetPreferredFileAsync(demozooId, romPath);
    }

    // ─── Compteur de vues ─────────────────────────────────────────────────────

    // Cache des préférences — invalide uniquement si SaveAllAsync est appelé.
    // Évite de recharger toutes les prefs depuis SQLite à chaque IncrementViewCount.
    private AppPreferences? _cachedPrefs;

    public async Task<(int ViewCount, bool IsFavorite)> IncrementViewCountAsync(
        int releaseId, bool currentIsFavorite = false)
    {
        var newCount = await _uow.Releases.IncrementViewCountAsync(releaseId);

        // Charger les prefs depuis le cache si possible
        _cachedPrefs ??= await _prefsService.LoadAllAsync();
        var prefs = _cachedPrefs;

        bool isFavorite = currentIsFavorite;

        // AutoFavorite : seulement si le seuil est atteint exactement et pas déjà favori
        if (prefs.AutoFavoriteViewThreshold > 0
            && newCount == prefs.AutoFavoriteViewThreshold
            && !currentIsFavorite)
        {
            // Charger uniquement si on doit modifier (cas rare)
            var release = await _uow.Releases.GetByIdAsync(releaseId);
            if (release != null && !release.IsFavorite)
            {
                release.IsFavorite = true;
                await _uow.Releases.UpdateAsync(release);
                isFavorite = true;
            }
        }

        return (newCount, isFavorite);
    }

    public async Task ResetAllViewCountsAsync()
        => await _uow.Releases.ResetAllViewCountsAsync();

    // 2026-07-31, retour utilisateur (log de perf fourni) : après une recherche tapée
    // rapidement dans la liste des releases, les requêtes s'enchaînaient en s'aggravant à
    // chaque frappe (4,4s → 14,5s → 14,7s → 15,2s → 16,8s). Diagnostic : ApplyFilters
    // (Repositories.cs) construit, pour une recherche "standard" (ni TitleOnly ni
    // AuthorsOnly), un WHERE avec plusieurs LIKE '%...%' à joker de tête (donc pas
    // d'index utilisable) PLUS une sous-requête corrélée EXISTS sur ReleaseCredits — un
    // vrai scan complet des ~380k+ Releases à chaque frappe, coûteux par nature (plusieurs
    // secondes, cohérent avec les 4,4s observés sur la toute première recherche). Le
    // problème AGGRAVANT (4,4s → 16,8s) vient d'ailleurs : ReleaseListViewModel.LoadAsync
    // annule bien son CancellationTokenSource à chaque nouvelle frappe, MAIS ne
    // transmettait ce token nulle part jusqu'ici — la requête SQLite précédente, déjà
    // lancée, continuait donc de tourner JUSQU'AU BOUT au lieu d'être interrompue, et
    // s'empilait derrière les suivantes sur l'unique connexion SQLite partagée (cf.
    // commentaire ci-dessous : EF Core + SQLite n'autorisent qu'une requête à la fois
    // dessus) — chaque nouvelle frappe attendait alors la fin de TOUTES les recherches
    // devenues obsolètes avant même de démarrer la sienne. CancellationToken propagé de
    // bout en bout (ViewModel → ce service → repository → EF Core → sqlite3_interrupt via
    // Microsoft.Data.Sqlite) pour qu'une recherche dépassée soit réellement coupée dès la
    // frappe suivante, libérant la connexion immédiatement.
    public async Task<PagedResult<ReleaseSummaryDto>> SearchAsync(ReleaseSearchFilter filter, CancellationToken ct = default)
    {
        // COUNT évité sur LoadMore (page > 1 avec SkipCount) — total déjà connu.
        // IMPORTANT : SQLite + EF Core n'autorisent pas deux queries simultanées
        // sur le même DbContext → exécution séquentielle obligatoire.
        var sw = System.Diagnostics.Stopwatch.StartNew();

        System.Diagnostics.Debug.WriteLine($"[SearchAsync] START SearchAsync filter={filter.Query} supertype={filter.Supertype}");
        List<DemoBase.Core.Models.Release> releases;
        try
        {
            releases = (await _uow.Releases.SearchAsync(filter, ct)).ToList();
            System.Diagnostics.Debug.WriteLine($"[SearchAsync] SearchAsync done: {releases.Count} items");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SearchAsync] SearchAsync FAILED: {ex.GetType().Name} {ex.Message}");
            throw;
        }

        int total;
        if (filter.SkipCount)
        {
            total = filter.KnownTotal;
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[SearchAsync] START CountAsync");
            try
            {
                total = await _uow.Releases.CountAsync(filter, ct);
                System.Diagnostics.Debug.WriteLine($"[SearchAsync] CountAsync done: {total}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SearchAsync] CountAsync FAILED: {ex.GetType().Name} {ex.Message}");
                throw;
            }
        }

        PerfLogger.Log("ReleaseService.DB items+count query", sw.ElapsedMilliseconds,
            filter.SkipCount ? "skip-count" : "with-count");
        sw.Restart();

        var result = new PagedResult<ReleaseSummaryDto>
        {
            Items = releases.Select(r => new ReleaseSummaryDto
            {
                Id              = r.Id,
                DemozooId       = r.DemozooId,
                Title           = r.Title,
                Supertype       = r.Supertype,
                ReleaseTypeId   = r.ReleaseTypeId,
                ReleaseTypeName = r.ReleaseType?.Name ?? "",
                ReleaseDate     = r.ReleaseDate ?? "",
#pragma warning disable CS8601
                AuthorNames     = !string.IsNullOrEmpty(r.AuthorNamesCache)
                    ? r.AuthorNamesCache
                    : r.Authors.Any()
                        ? string.Join(", ", r.Authors
                            .Select(a => a.Nick?.Releaser?.Name ?? a.Nick?.Name ?? "")
                            .Where(s => s != "").Distinct())
                        : "",
#pragma warning restore CS8601
                PlatformNames   = string.Join(", ", r.ReleasePlatforms
                    .Select(rp => rp.Platform?.ShortName ?? rp.Platform?.Name ?? "")
                    .Where(s => s != "")),
                ReleaseYear     = r.ReleaseDate != null && r.ReleaseDate.Length >= 4
                                    ? r.ReleaseDate[..4] : string.Empty,
                IsFavorite      = r.IsFavorite,
                ViewCount       = r.ViewCount,
                BestRank        = r.CompetitionPlacings
                    .Where(cp => cp.Ranking.HasValue).MinBy(cp => cp.Ranking)?.Ranking,
                BestCompetition = r.CompetitionPlacings
                    .Where(cp => cp.Ranking.HasValue).MinBy(cp => cp.Ranking)?.Competition?.Party?.Name,
                ThumbnailPath   = r.ThumbnailPathCache,
            }),
            TotalCount = total,
            Page       = filter.Page,
            PageSize   = filter.PageSize,
        };
        PerfLogger.Log("ReleaseService.DTO mapping", sw.ElapsedMilliseconds,
            $"{releases.Count} releases");

        // Matérialiser en liste pour pouvoir itérer plusieurs fois (MainFileExt + HasNoFile
        // ci-dessous ont chacun besoin d'un passage complet sur les items de la page).
        var items = result.Items.ToList();
        result.Items = items;

        // Remplir MainFileExt via les DatEntries (music uniquement — une seule query bulk)
        if (filter.Supertype == "music")
        {
            var dzIds = items
                .Where(i => i.DemozooId.HasValue)
                .Select(i => i.DemozooId!.Value)
                .Distinct().ToList();
            if (dzIds.Count > 0)
            {
                var datMap = await _uow.Releases.GetDatEntriesForDemozooIdsAsync(dzIds);
                foreach (var item in items.Where(i => i.DemozooId.HasValue))
                    if (datMap.TryGetValue(item.DemozooId!.Value, out var dat))
                    {
                        // Extensions à ignorer — fichiers texte/doc non audio
                        var ignoredExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md",
                              ".jpg", ".jpeg", ".png", ".gif", ".bmp" };
                        // Prioriser tracker sur conversions audio
                        var audioConverted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { ".wav", ".mp3", ".ogg", ".flac", ".aiff", ".m4a" };
                        var bestRom = dat.Roms
                            .Where(r => !ignoredExts.Contains(
                                System.IO.Path.GetExtension(r.Name)))
                            .OrderBy(r => audioConverted.Contains(
                                System.IO.Path.GetExtension(r.Name)) ? 1 : 0)
                            .FirstOrDefault();
                        var romName = bestRom?.Name;
                        if (romName != null)
                        {
                            // Normaliser le nom (ex: "mod.dragonsfunk" → "dragonsfunk.mod")
                            // avant d'extraire l'extension
                            var normalized = DemoBase.Core.DTOs.TrackerExtensions.NormalizeFilename(romName);
                            item.MainFileExt = System.IO.Path.GetExtension(normalized).ToLowerInvariant();
                        }
                    }
            }
        }

        // ── HasNoFile : releases sans aucun fichier exploitable ────────────────────
        // Même logique que le message d'erreur "Fichier introuvable" de ReleaseService.
        // LaunchAsync (Services.cs) : aucun DatEntry connu pour ce DemozooId ET aucun
        // ReleaseLink "fichier de la production" (IsMainFile, mappé depuis is_download_link
        // côté import Demozoo — cf. EmulatorService.cs qui utilise le même champ comme
        // fallback pour trouver le fichier à lancer) — Demozoo ne référence alors strictement
        // rien de lançable pour cette release. Calculé en masse pour la page affichée
        // (≤ ~80 releases) afin que la liste puisse l'indiquer visuellement, plutôt que de
        // laisser l'utilisateur le découvrir en cliquant sur "Lancer" à chaque fois.
        //
        // Bug corrigé le 2026-07-24 (retour utilisateur : le badge 🚫 ne s'affichait pas dès
        // qu'il y avait "au moins un soundtrack ou au moins une vidéo") : l'ancien calcul
        // considérait la release comme lançable dès qu'un SEUL de ses liens n'était pas
        // vidéo (YouTube/Vimeo) — mais un lien Soundcloud (soundtrack), Pouet, ou toute autre
        // page annexe n'est PAS non plus quelque chose que "Lancer/Lire" peut lancer. Le badge
        // sert à signaler l'absence de fichier LANÇABLE pour la release elle-même, pas
        // l'absence de médias annexes (soundtrack/vidéo) qui s'affichent à côté.
        {
            var allDzIds = items
                .Where(i => i.DemozooId.HasValue)
                .Select(i => i.DemozooId!.Value)
                .Distinct().ToList();
            var datPresence = allDzIds.Count > 0
                ? await _uow.Releases.GetDatEntriesForDemozooIdsAsync(allDzIds)
                : new Dictionary<int, DatEntry>();

            var hasLaunchableLinkById = releases.ToDictionary(
                r => r.Id,
                r => r.Links.Any(l => l.IsMainFile));

            foreach (var item in items)
            {
                var hasDat = item.DemozooId.HasValue && datPresence.ContainsKey(item.DemozooId.Value);
                var hasLaunchableLink = hasLaunchableLinkById.TryGetValue(item.Id, out var v) && v;
                item.HasNoFile = !hasDat && !hasLaunchableLink;
            }
        }

        return result;
    }

    public Task<List<int>> GetAvailableYearsAsync() => _uow.Releases.GetAvailableYearsAsync();

    public async Task<Release> CreateAsync(CreateReleaseDto dto)
    {
        var release = new Release
        {
            Title                  = dto.Title,
            Supertype              = dto.Supertype,
            ReleaseTypeId          = dto.ReleaseTypeId,
            ReleaseDate            = dto.ReleaseDate,
            ReleaseDatePrecision   = dto.ReleaseDatePrecision,
            Notes                  = dto.Notes,
        };
        await _uow.Releases.AddAsync(release);
        await _uow.SaveChangesAsync();
        return release;
    }

    public async Task UpdateAsync(int id, UpdateReleaseDto dto)
    {
        var release = await _uow.Releases.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Release {id} introuvable.");

        release.Title                = dto.Title;
        release.Supertype            = dto.Supertype;
        release.ReleaseTypeId        = dto.ReleaseTypeId;
        release.ReleaseDate          = dto.ReleaseDate;
        release.ReleaseDatePrecision = dto.ReleaseDatePrecision;
        release.Notes                = dto.Notes;
        release.IsFavorite           = dto.IsFavorite;
        release.Rating               = dto.Rating;
        release.DemozooUrl           = dto.DemozooUrl;
        release.PouetUrl             = dto.PouetUrl;
        release.CsdbUrl              = dto.CsdbUrl;
        release.Tags                 = dto.Tags;

        await _uow.Releases.UpdateAsync(release);
        await _uow.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await _uow.Releases.DeleteAsync(id);
        await _uow.SaveChangesAsync();
    }

    public async Task<IEnumerable<DemoBase.Core.Models.DatEntry>> GetDatEntriesAsync(int demozooId)
        => await _uow.Releases.GetDatEntriesAsync(demozooId);

    public async Task<Dictionary<int, DemoBase.Core.Models.DatEntry>> GetDatEntriesForDemozooIdsAsync(
        IEnumerable<int> demozooIds)
        => await _uow.Releases.GetDatEntriesForDemozooIdsAsync(demozooIds);

    public async Task<int?> GetIdByDemozooIdAsync(int demozooId)
        => await _uow.Releases.GetIdByDemozooIdAsync(demozooId);

    private async Task<IEnumerable<SoundtrackDto>> BuildSoundtrackDtosAsync(Release release)
    {
        if (!release.Soundtracks.Any()) return [];

        // ReleaseSoundtrack.SoundtrackId = DemozooId de la release soundtrack
        var soundtrackDemozooIds = release.Soundtracks
            .Select(s => s.SoundtrackId)
            .ToList();

        System.Diagnostics.Debug.WriteLine(
            $"[SOUNDTRACK] Release {release.Id} — {release.Soundtracks.Count} soundtracks, " +
            $"SoundtrackIds: [{string.Join(", ", soundtrackDemozooIds)}]");

        // Chercher dans les DATs
        var datMap = soundtrackDemozooIds.Any()
            ? await _uow.Releases.GetDatEntriesForDemozooIdsAsync(soundtrackDemozooIds)
            : new Dictionary<int, DatEntry>();

        System.Diagnostics.Debug.WriteLine(
            $"[SOUNDTRACK] DatMap keys: [{string.Join(", ", datMap.Keys)}]");

        var result = new List<SoundtrackDto>();
        foreach (var st in release.Soundtracks)
        {
            var dto = new SoundtrackDto
            {
                SoundtrackId = st.SoundtrackId,
                Soundtrack   = st.Soundtrack!,
            };

            if (datMap.TryGetValue(st.SoundtrackId, out var dat))
            {
                var playable = dat.Roms.FirstOrDefault(r =>
                    DemoBase.Core.DTOs.TrackerExtensions.IsPlayable(r.Name));
                if (playable != null)
                {
                    dto.HasPlayableRom = true;
                    dto.ZipPath        = dat.RomPath;
                    dto.RomName        = playable.Name;
                }
            }
#pragma warning disable CS8601
            dto.AuthorNames  = st.Soundtrack?.AuthorNamesCache ?? string.Empty;
#pragma warning restore CS8601
            dto.ReleaseTitle = release.Title;

            if (_favService != null)
                dto.IsFavorite = await _favService.IsFavoriteAsync(st.SoundtrackId);

            result.Add(dto);
        }
        return result;
    }

    public async Task LaunchAsync(int releaseId, int? emulatorConfigId = null, string? romPathOverride = null,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null)
    {
        try
        {
            var detail = await GetDetailAsync(releaseId);
            var release = detail.Release;
            System.Diagnostics.Debug.WriteLine(
                $"[LAUNCH] Release={release.Title} (Id={release.Id}) " +
                $"MainPlatform={release.ReleasePlatforms.FirstOrDefault()?.Platform?.Name ?? "(aucune)"} " +
                $"emulatorConfigId={emulatorConfigId} DefaultEmulatorConfig={detail.DefaultEmulatorConfig?.Id}");

            // ── 1. Config émulateur ───────────────────────────────────────────
            EmulatorConfig? config = emulatorConfigId.HasValue
                ? await _uow.Emulators.GetConfigByIdAsync(emulatorConfigId.Value)
                : detail.DefaultEmulatorConfig;

            if (config == null)
            {
                System.Diagnostics.Debug.WriteLine("[LAUNCH] Aucune EmulatorConfig résolue → abandon.");
                // 2026-07-25 : texte résolu via LocalizationService (fr/en) plutôt que codé
                // en dur en français — cf. RESUME_PROJET.md, la popup ET le bandeau défilant
                // suivent maintenant la langue courante de l'interface.
                var noConfigHeading = DemoBase.App.Services.LocalizationService.Get("Dlg_NoConfig_Heading");
                var noConfigBody    = DemoBase.App.Services.LocalizationService.Get("Dlg_NoConfig_Body");
                DemoBase.App.Controls.StatusScrollerControl.Post(
                    $"{noConfigHeading}\n\n{noConfigBody}", isWarning: true);
                // Erreur bloquante (le lancement s'arrête complètement) : une popup
                // s'impose en plus du bandeau défilant, facile à manquer si on ne
                // regarde pas l'écran au bon moment — sans ça, l'utilisateur ne voit
                // que "rien ne se passe" au clic sur Play. Fenêtre custom (SimpleInfoDialog)
                // plutôt qu'une MessageBox native — même style que le reste de l'app,
                // remplace l'ancienne popup système hors charte.
                new DemoBase.App.Views.SimpleInfoDialog(
                    "Dlg_NoConfig_Title", "Dlg_NoConfig_Heading", "Dlg_NoConfig_Body")
                {
                    Owner = System.Windows.Application.Current?.MainWindow,
                }.ShowDialog();
                return;
            }
            System.Diagnostics.Debug.WriteLine(
                $"[LAUNCH] Config résolue : Id={config.Id} Profil={config.ProfileName} " +
                $"Platform={config.Platform?.Name} Emulator={config.Emulator?.Name} " +
                $"EmulatorType={config.Emulator?.EmulatorType} ExePath={config.Emulator?.ExecutablePath}");

            // ── 2. Résoudre le fichier ROM ────────────────────────────────────
            // Priorité : romPathOverride (DAT sélectionné par l'utilisateur) > premier DAT existant
            string? romPath = romPathOverride;
            if (romPath == null && release.DemozooId.HasValue && detail.DatFiles.Any())
            {
                var prefs    = await _prefsService.LoadAllAsync();
                var romsRoot = prefs.ResolvedPathReleases;
                // Ignore les DatEntry "Code Sources" (archives de code source, pas des
                // fichiers jouables) — voir DatEntry.IsCodeSourceEntry.
                foreach (var dat in detail.DatFiles.Where(d => !d.IsCodeSourceEntry))
                {
                    var candidate = Path.Combine(romsRoot, dat.RomPath);
                    if (File.Exists(candidate)) { romPath = candidate; break; }
                }
            }
            System.Diagnostics.Debug.WriteLine($"[LAUNCH] romPath (DAT) = {romPath ?? "(non résolu)"}");

            // ── 3. Fallback sur ReleaseLinks ─────────────────────────────────
            if (romPath == null)
            {
                // Exclure les liens purement vidéo (YouTube/Vimeo, cf. ReleaseLink.IsVideo) —
                // ce sont des références externes, pas des fichiers de jeu/démo lançables. Sans
                // ce filtre, un ancien repli inconditionnel pouvait récupérer un lien YouTube et
                // lancer l'émulateur avec un LocalFilePath vide, d'où un émulateur qui démarre
                // sans rien charger (constaté : ColEm qui affiche juste son BIOS, sans cartouche
                // insérée).
                //
                // 2026-07-25 : retrait du tout dernier repli "?? fileLinks.FirstOrDefault()"
                // (sans condition) — il pouvait attraper N'IMPORTE QUEL lien non-vidéo (page
                // Pouet, site officiel du groupe…) et tenter de le télécharger/lancer comme si
                // c'était le fichier du jeu. Un lien n'est maintenant retenu que s'il est
                // explicitement marqué IsMainFile par Demozoo (mappé sur son propre champ
                // "is_download_link" à l'import — un signal fiable, pas une supposition de notre
                // côté). Cf. RESUME_PROJET.md : releases pas encore couvertes par un DAT.
                // 2026-07-25 : IsMainFile seul ne suffit pas — Demozoo peut marquer un lien
                // comme fichier de téléchargement (is_download_link) sans que son URL soit
                // renseignée (constaté sur "Fullast Vinner 2"). Sans le filtre Url non vide
                // ici, ce lien "fantôme" était quand même sélectionné, puis le lancement
                // échouait plus loin (ResolveFileAsync) avec juste un message discret sur
                // le bandeau défilant — d'où l'impression que "rien ne se passe".
                // 2026-07-25 (suite, "Return to Promised Land", Demozoo #394835) : le champ
                // "Url" tout court était en fait TROP strict dans l'autre sens — certaines
                // classes de lien Demozoo (ex. "BaseUrl", vu sur les releases hébergées sur
                // plus4world.powweb.com) ne remplissent JAMAIS "url" côté Postgres, l'URL
                // réelle n'existant que dans "parameter"/LinkParameter. EffectiveDownloadUrl
                // (cf. ReleaseLink, DemoBase.Core/Models/Models.cs) couvre les deux cas :
                // Url s'il est renseigné, sinon LinkParameter pour la classe "BaseUrl" —
                // sans réintroduire le lien "fantôme" de "Fullast Vinner 2" (ni Url ni
                // LinkParameter exploitable → toujours filtré).
                var fileLinks = detail.Links.Where(l => !l.IsVideo).ToList();
                var link = fileLinks.FirstOrDefault(l => l.IsMainFile && l.IsLocalCopy)
                        ?? fileLinks.FirstOrDefault(l => l.IsLocalCopy)
                        ?? fileLinks.FirstOrDefault(l => l.IsMainFile && !string.IsNullOrEmpty(l.EffectiveDownloadUrl));

                if (link == null)
                {
                    System.Diagnostics.Debug.WriteLine("[LAUNCH] Aucun ReleaseLink jouable disponible (hors liens vidéo, IsMainFile requis) → abandon.");
                    // 2026-07-25 : texte résolu via LocalizationService (fr/en) — retour
                    // utilisateur, ce message restait en français avec une interface en
                    // anglais. Fenêtre custom (SimpleInfoDialog) plutôt qu'une MessageBox
                    // native — même style que le reste de l'app.
                    var noFileHeading = DemoBase.App.Services.LocalizationService.Get("Dlg_NoFile_Heading");
                    var noFileBody    = DemoBase.App.Services.LocalizationService.Get("Dlg_NoFile_Body");
                    DemoBase.App.Controls.StatusScrollerControl.Post(
                        $"{noFileHeading} — {noFileBody}", isError: true);
                    new DemoBase.App.Views.SimpleInfoDialog(
                        "Dlg_NoFile_Title", "Dlg_NoFile_Heading", "Dlg_NoFile_Body")
                    {
                        Owner = System.Windows.Application.Current?.MainWindow,
                    }.ShowDialog();
                    return;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[LAUNCH] Via ReleaseLink Id={link.Id} Url={link.Url} EffectiveDownloadUrl={link.EffectiveDownloadUrl} LocalFilePath={link.LocalFilePath}");
                link.Release = release;
                await _emulatorService.LaunchReleaseAsync(link, config, progress);
                DemoBase.App.Controls.StatusScrollerControl.Post($"Lancement de {release.Title}...");
                return;
            }

            // ── 4. Lancement via ROM DAT ──────────────────────────────────────
            await _emulatorService.LaunchReleaseAsync(romPath, release, config);
            DemoBase.App.Controls.StatusScrollerControl.Post($"Lancement de {release.Title}...");
        }
        catch (Exception ex)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                $"Erreur au lancement : {ex.Message}", isError: true);

            // 2026-07-27, retour utilisateur (capture d'écran : échec réseau pendant un
            // téléchargement ad-hoc — "Aucune connexion n'a pu être établie... dahlia.zone:443")
            // — la MessageBox système s'affichait PAR-DESSUS l'overlay "Téléchargement en
            // cours…" déjà visible à ce moment-là. Demande explicite : afficher l'erreur DANS
            // cet overlay (avec un bouton OK pour le fermer) plutôt qu'une fenêtre en plus.
            // Si un "progress" a été fourni, un overlay est forcément affiché côté appelant
            // (ReleaseDetailViewModel) — on relaie l'erreur par ce même canal (IsError=true,
            // cf. LaunchDownloadProgress) plutôt que d'ouvrir une MessageBox. Fallback
            // MessageBox conservé uniquement pour les appelants SANS overlay de progression
            // (ex. lancement direct d'un DAT déjà local, romPath résolu synchronement à
            // l'étape 4 ci-dessus) — pas de canal pour relayer l'erreur autrement dans ce cas.
            if (progress != null)
                progress.Report(new DemoBase.Core.DTOs.LaunchDownloadProgress(ex.Message, 0, IsError: true));
            else
                System.Windows.MessageBox.Show(
                    System.Windows.Application.Current?.MainWindow,
                    $"Une erreur est survenue au lancement de la release :\n\n{ex.Message}",
                    "Erreur de lancement",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
        }
    }

    public Task<DemoBase.Core.DTOs.ReleaseSummaryDto?> GetRandomMusicReleaseAsync(
        IReadOnlySet<int> excludedIds)
        => _uow.Releases.GetRandomMusicAsync(excludedIds);

    public Task<(DemoBase.Core.DTOs.ReleaseSummaryDto? Release, bool IsExactMatch)> GetOnThisDayOrRandomReleaseAsync(
        int month, int day)
        => _uow.Releases.GetOnThisDayOrRandomAsync(month, day);

    // 2026-07-26, retour utilisateur : le téléchargement ad-hoc (release pas encore
    // couverte par un DAT, cf. bloc ci-dessus dans LaunchAsync) n'était accessible que
    // via le chemin émulateur générique — les releases Music/Graphics (qui n'appellent
    // jamais LaunchAsync, cf. ReleaseDetailViewModel.LaunchAsync qui route directement
    // vers PlayMusicReleaseAsync/ShowGraphicsAsync) affichaient donc le badge "Fichier
    // externe" sans que le bouton Play puisse jamais rien télécharger. Même sélection de
    // lien que le bloc "3. Fallback sur ReleaseLinks" ci-dessus, factorisée ici pour être
    // appelée directement par les chemins Music/Graphics du ViewModel.
    public async Task<string?> DownloadAdHocFileAsync(int releaseId,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null)
    {
        var detail  = await GetDetailAsync(releaseId);
        var release = detail.Release;

        var fileLinks = detail.Links.Where(l => !l.IsVideo).ToList();
        var link = fileLinks.FirstOrDefault(l => l.IsMainFile && l.IsLocalCopy)
                ?? fileLinks.FirstOrDefault(l => l.IsLocalCopy)
                ?? fileLinks.FirstOrDefault(l => l.IsMainFile && !string.IsNullOrEmpty(l.EffectiveDownloadUrl));
        if (link == null) return null;

        var resolved = await _emulatorService.ResolveAdHocFileAsync(release, link, progress);
        if (resolved == null) return null;

        // 2026-07-27, retour utilisateur : certains sites de musique tracker (ex. mirroirs type
        // modland) distribuent leurs fichiers en .gz simple (compression seule, PAS une archive
        // — un seul fichier interne, ex. "song.mod.gz" → "song.mod"), pas en .zip. Décompresser
        // AVANT le test d'extension ci-dessous, sinon un .gz aurait fini enveloppé tel quel
        // (compressé) dans le mini-zip, et PlayMusicReleaseAsync n'aurait jamais reconnu
        // l'extension ".gz" comme jouable (silencieux : pas de crash, juste "aucune musique
        // trouvée"). Si jamais un .gz contient lui-même un .zip (rare), le nouveau test
        // d'extension ci-dessous s'applique normalement sur le résultat décompressé.
        if (string.Equals(Path.GetExtension(resolved), ".gz", StringComparison.OrdinalIgnoreCase))
        {
            try { resolved = DecompressGzip(resolved); }
            catch (Exception ex)
            {
                // Best-effort : si la décompression échoue (fichier corrompu, etc.), on
                // continue avec le .gz brut — sera enveloppé tel quel ci-dessous, pas pire
                // qu'avant ce correctif (juste "aucune musique trouvée", pas de crash).
                System.Diagnostics.Debug.WriteLine($"[ADHOC] Décompression .gz échouée : {ex.Message}");
            }
        }

        // 2026-07-27, retour utilisateur : "je viens de télécharger une musique au format
        // .wav ... ce ne sera jamais [un zip]" — Demozoo peut effectivement pointer un lien
        // direct vers un fichier isolé (.wav, .mod, .png...) plutôt qu'une archive.
        // ResolveAdHocFileAsync renvoie alors ce fichier tel quel (rien à extraire). Or
        // PlayMusicReleaseAsync (ReleaseViewModels.cs) et GraphicsViewerViewModel.LoadAsync
        // s'attendent TOUJOURS à un ZIP à scanner — exactement comme un fichier DAT résolu,
        // système existant et non retouché ici — un fichier brut y provoquait
        // System.IO.InvalidDataException (ZipFile.OpenRead direct sur un .wav). Plutôt que
        // modifier les deux consommateurs (plusieurs points d'ouverture de zip chacun),
        // on enveloppe ici le fichier brut dans un mini-zip à une seule entrée : transparent
        // pour tout le reste du pipeline, qui continue de ne voir "que des zips".
        if (!string.Equals(Path.GetExtension(resolved), ".zip", StringComparison.OrdinalIgnoreCase))
            return WrapAsSingleEntryZip(resolved, release.Id, link.Id);

        return resolved;
    }

    // Décompresse un .gz simple (un seul fichier interne, PAS une archive tar/zip) — le nom de
    // sortie retire juste le suffixe ".gz" ("song.mod.gz" → "song.mod"). Si le nom ne se termine
    // pas par ".gz" (appelant garanti que si, gardé en défense), fallback ".decompressed".
    private static string DecompressGzip(string gzPath)
    {
        var outPath = gzPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? gzPath[..^3]
            : gzPath + ".decompressed";

        // Déjà décompressé lors d'un précédent Play — pas de retravail (cache).
        if (!File.Exists(outPath))
        {
            var tmpPath = outPath + ".part";
            using (var inStream = File.OpenRead(gzPath))
            using (var gzip = new GZipStream(inStream, CompressionMode.Decompress))
            using (var outStream = File.Create(tmpPath))
                gzip.CopyTo(outStream);
            File.Move(tmpPath, outPath, overwrite: true);
        }
        return outPath;
    }

    private static string WrapAsSingleEntryZip(string filePath, int releaseId, int linkId)
    {
        var dir = Path.Combine(
            DemoBase.App.Services.WorkingPaths.NotCuratedRoot,
            releaseId.ToString(), linkId.ToString());
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(filePath) + "_wrapped.zip");

        // Déjà enveloppé lors d'un précédent Play — pas de réécriture (cache).
        if (!File.Exists(zipPath))
        {
            var tmpZipPath = zipPath + ".part";
            using (var zip = ZipFile.Open(tmpZipPath, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(filePath, Path.GetFileName(filePath));
            File.Move(tmpZipPath, zipPath, overwrite: true);
        }
        return zipPath;
    }
}

// ─── ReleaseType Service ──────────────────────────────────────────────────────

public class ReleaseTypeService : IReleaseTypeService
{
    private readonly IUnitOfWork _uow;
    public ReleaseTypeService(IUnitOfWork uow) => _uow = uow;

    public async Task<IEnumerable<ReleaseTypeDto>> GetAllAsync() =>
        await _uow.ReleaseTypes.GetAllWithCountAsync();

    public async Task<ReleaseType> CreateAsync(CreateReleaseTypeDto dto)
    {
        var existing = await _uow.ReleaseTypes.GetByNameAsync(dto.Name);
        if (existing != null) throw new InvalidOperationException($"Un type « {dto.Name} » existe déjà.");

        var rt = new ReleaseType { Name = dto.Name.Trim(), Supertype = dto.Supertype, Description = dto.Description, SortOrder = dto.SortOrder };
        await _uow.ReleaseTypes.AddAsync(rt);
        await _uow.SaveChangesAsync();
        return rt;
    }

    public async Task UpdateAsync(int id, CreateReleaseTypeDto dto)
    {
        var rt = await _uow.ReleaseTypes.GetByIdAsync(id)
            ?? throw new KeyNotFoundException($"Type {id} introuvable.");
        var conflict = await _uow.ReleaseTypes.GetByNameAsync(dto.Name);
        if (conflict != null && conflict.Id != id) throw new InvalidOperationException($"Un type « {dto.Name} » existe déjà.");

        rt.Name = dto.Name.Trim(); rt.Supertype = dto.Supertype; rt.Description = dto.Description; rt.SortOrder = dto.SortOrder;
        await _uow.ReleaseTypes.UpdateAsync(rt);
        await _uow.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        if (await _uow.ReleaseTypes.IsInUseAsync(id))
            throw new InvalidOperationException("Ce type est utilisé par une ou plusieurs releases et ne peut pas être supprimé.");
        await _uow.ReleaseTypes.DeleteAsync(id);
        await _uow.SaveChangesAsync();
    }
}

// ─── Navigation Service ───────────────────────────────────────────────────────

public class NavigationService : INavigationService
{
    private readonly Stack<NavigationEventArgs> _history = new();
    private NavigationEventArgs? _current;
    private bool _isGoingBack;

    public bool CanGoBack => _history.Count > 0;
    public event EventHandler<NavigationEventArgs>? Navigated;

    public void NavigateTo<TViewModel>(object? parameter = null, object? tag = null)
        where TViewModel : class
        => NavigateTo(typeof(TViewModel), parameter, tag);

    public void NavigateTo(Type viewModelType, object? parameter = null, object? tag = null)
    {
        var args = new NavigationEventArgs
        {
            ViewModelType = viewModelType,
            Parameter     = parameter,
            Tag           = tag,
        };

        // Ne pas empiler dans l'historique si :
        // 1. On revient en arrière (GoBack) — l'historique est déjà géré par GoBack
        // 2. C'est la même destination que la vue courante (double-appui Alt+2 etc.)
        if (!_isGoingBack && _current != null && !IsSameDestination(_current, args))
            _history.Push(_current);

        _current = args;
        Navigated?.Invoke(this, args);
    }

    public void GoBack()
    {
        if (_history.Count == 0) return;
        _isGoingBack = true;
        try
        {
            var prev = _history.Pop();
            _current = prev;
            Navigated?.Invoke(this, prev);
        }
        finally { _isGoingBack = false; }
    }

    private static bool IsSameDestination(NavigationEventArgs a, NavigationEventArgs b)
        => a.ViewModelType == b.ViewModelType
        && Equals(a.Tag, b.Tag)
        && Equals(a.Parameter, b.Parameter);
}


// Extension du NavigationService pour résoudre les VMs via DI
// (à enregistrer dans App.xaml.cs comme Singleton)
