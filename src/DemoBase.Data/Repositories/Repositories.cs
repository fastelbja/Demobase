using DemoBase.Core.DTOs;
using DemoBase.Core.Interfaces;
using DemoBase.Core.Enums;
using DemoBase.Core.Models;
using DemoBase.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace DemoBase.Data.Repositories;

// ─── Generic Repository ───────────────────────────────────────────────────────

public class Repository<T> : IRepository<T> where T : BaseEntity
{
    protected readonly DemoBaseDbContext _ctx;
    protected readonly DbSet<T> _set;

    public Repository(DemoBaseDbContext ctx) { _ctx = ctx; _set = ctx.Set<T>(); }

    public virtual async Task<T?> GetByIdAsync(int id)            => await _set.FindAsync(id);
    public virtual async Task<IEnumerable<T>> GetAllAsync()        => await _set.AsNoTracking().ToListAsync();
    public virtual async Task<T> AddAsync(T entity)                { await _set.AddAsync(entity); return entity; }
    public virtual Task UpdateAsync(T entity)
    {
        // Une autre instance de T avec la même clé peut déjà être suivie par EF Core
        // (typiquement : une requête ailleurs a fait un .Include() sur cette entité sans
        // .AsNoTracking()). Dans ce cas, attacher une 2e instance avec la même clé lève
        // InvalidOperationException ("already being tracked"). On détecte ce cas et on
        // recopie les valeurs sur l'instance déjà suivie plutôt que de planter.
        var tracked = _ctx.ChangeTracker.Entries<T>()
            .FirstOrDefault(e => !ReferenceEquals(e.Entity, entity) && e.Entity.Id == entity.Id);
        if (tracked != null)
        {
            tracked.CurrentValues.SetValues(entity);
            tracked.State = EntityState.Modified;
        }
        else
        {
            _ctx.Entry(entity).State = EntityState.Modified;
        }
        return Task.CompletedTask;
    }
    public virtual async Task DeleteAsync(int id)                  { var e = await _set.FindAsync(id); if (e != null) _set.Remove(e); }
}

// ─── Release Repository ───────────────────────────────────────────────────────

public class ReleaseRepository : Repository<Release>, IReleaseRepository
{
    public async Task<int?> GetIdByDemozooIdAsync(int demozooId)
    {
        var release = await _set.AsNoTracking()
            .FirstOrDefaultAsync(r => r.DemozooId == demozooId);
        return release?.Id;
    }

    public async Task<IEnumerable<DatEntry>> GetDatEntriesAsync(int demozooId)
    {
        // Base unique (demobase.db) depuis la fusion config.db/dats.db — plus
        // besoin d'ATTACH ni de qualifier le schéma, tout est dans "main".
        var conn = (Microsoft.Data.Sqlite.SqliteConnection)_ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var entries = new List<DatEntry>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.Id, e.DemozooId, e.RomPath, e.SourceFile,
                   r.Id as RomId, r.DatEntryId, r.Name, r.Size,
                   r.Crc32, r.Md5, r.Sha1
            FROM "DatEntries" e
            LEFT JOIN "DatRoms" r ON r.DatEntryId = e.Id
            WHERE e.DemozooId = @id
            """;
        cmd.Parameters.AddWithValue("@id", demozooId);

        await using var reader = await cmd.ExecuteReaderAsync();
        DatEntry? current = null;
        while (await reader.ReadAsync())
        {
            var entryId = reader.GetInt32(0);
            if (current == null || current.Id != entryId)
            {
                current = new DatEntry
                {
                    Id          = entryId,
                    DemozooId  = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    RomPath    = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    SourceFile = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Roms        = [],
                };
                entries.Add(current);
            }
            if (!reader.IsDBNull(4))
            {
                current.Roms.Add(new DatRom
                {
                    Id         = reader.GetInt32(4),
                    DatEntryId = reader.GetInt32(5),
                    Name       = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    Size       = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                    Crc32      = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Md5        = reader.IsDBNull(9)  ? null : reader.GetString(9),
                    Sha1       = reader.IsDBNull(10) ? null : reader.GetString(10),
                });
            }
        }
        return entries;
    }
    public ReleaseRepository(DemoBaseDbContext ctx) : base(ctx) { }

    /// <summary>
    /// Retourne les DatEntries pour une liste de DemozooIds (soundtracks).
    /// Clé = DemozooId, Valeur = DatEntry avec ses Roms.
    /// </summary>
    public async Task<Dictionary<int, DatEntry>> GetDatEntriesForDemozooIdsAsync(IEnumerable<int> demozooIds)
    {
        var ids = demozooIds.ToList();
        if (ids.Count == 0) return [];

        var conn = (Microsoft.Data.Sqlite.SqliteConnection)_ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var result = new Dictionary<int, DatEntry>();
        await using var cmd = conn.CreateCommand();
        var paramList = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        cmd.CommandText = $"""
            SELECT e.Id, e.DemozooId, e.RomPath, e.SourceFile,
                   r.Id as RomId, r.DatEntryId, r.Name, r.Size, r.Crc32, r.Md5, r.Sha1
            FROM "DatEntries" e
            LEFT JOIN "DatRoms" r ON r.DatEntryId = e.Id
            WHERE e.DemozooId IN ({paramList})
            ORDER BY e.Id, r.Id
            """;
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var demozooId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            if (!result.TryGetValue(demozooId, out var entry))
            {
                entry = new DatEntry
                {
                    Id         = reader.GetInt32(0),
                    DemozooId  = demozooId,
                    RomPath    = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    SourceFile = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Roms       = [],
                };
                result[demozooId] = entry;
            }
            if (!reader.IsDBNull(4))
            {
                entry.Roms.Add(new DatRom
                {
                    Id         = reader.GetInt32(4),
                    DatEntryId = reader.GetInt32(5),
                    Name       = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    Size       = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                    Crc32      = reader.IsDBNull(8)  ? null : reader.GetString(8),
                    Md5        = reader.IsDBNull(9)  ? null : reader.GetString(9),
                    Sha1       = reader.IsDBNull(10) ? null : reader.GetString(10),
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Requête ultra-légère : SELECT sur Releases seulement, sans aucun JOIN.
    /// Les données complémentaires (type, plateforme) sont chargées en deux
    /// requêtes séparées après la pagination — évite le produit cartésien EF.
    /// </summary>
    public async Task<IEnumerable<Release>> SearchAsync(ReleaseSearchFilter filter, CancellationToken ct = default)
    {
        // 1. Requête de base sur Releases uniquement — rapide même sur 300k lignes
        var q = _ctx.Releases.AsNoTracking();

        q = ApplyFilters(q, filter);
        q = ApplySort(q, filter);

        var ids = await q
            .Select(r => r.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct);

        if (ids.Count == 0) return [];

        // 2. Charge la page avec ses jointures (seulement 80 lignes) — en parallèle
        var releasesTask = _ctx.Releases
            .Where(r => ids.Contains(r.Id))
            .Include(r => r.ReleaseType)
            .Include(r => r.ReleasePlatforms).ThenInclude(rp => rp.Platform)
            .Include(r => r.CompetitionPlacings)
            // Nécessaire pour ReleaseService.SearchAsync (calcul de HasNoFile — releases sans
            // aucun fichier exploitable, ni DatEntry ni ReleaseLink hors référence vidéo).
            .Include(r => r.Links)
            .AsNoTracking()
            .ToListAsync(ct);

        // 3. Charge le premier screenshot par release (une seule requête plate)
        var thumbsTask = _ctx.Set<MediaFile>()
            .Where(m => ids.Contains(m.ReleaseId) && m.Type == MediaType.Screenshot)
            .GroupBy(m => m.ReleaseId)
            .Select(g => new { ReleaseId = g.Key, FilePath = g.OrderBy(m => m.SortOrder).First().FilePath })
            .AsNoTracking()
            .ToDictionaryAsync(x => x.ReleaseId, x => x.FilePath, ct);

        // 3b. Charge les noms d'auteurs pour la page via une seule requête SQL plate
        //    (JOIN ReleaseAuthors → Nicks → Releasers) — retourne une liste de (ReleaseId, ReleaserName)
        var authorsTask = _ctx.ReleaseAuthors
            .Where(a => ids.Contains(a.ReleaseId))
            .Join(_ctx.Nicks,    a => a.NickId,      n => n.Id, (a, n) => new { a.ReleaseId, n.ReleaserId })
            .Join(_ctx.Releasers, x => x.ReleaserId, r => r.Id, (x, r) => new { x.ReleaseId, r.Name })
            .AsNoTracking()
            .ToListAsync(ct);

        await Task.WhenAll(releasesTask, authorsTask, thumbsTask);

        var releases    = await releasesTask;
        var authorNames = (await authorsTask)
            .GroupBy(x => x.ReleaseId)
            .ToDictionary(g => g.Key,
                          g => string.Join(", ", g.Select(x => x.Name).Distinct()));

        // Injecter les noms d'auteurs et thumbnail dans chaque release (champs synthétiques)
        var thumbs = await thumbsTask;
        foreach (var r in releases)
        {
            r.AuthorNamesCache  = authorNames.TryGetValue(r.Id, out var n) ? n : string.Empty;
            r.ThumbnailPathCache = thumbs.TryGetValue(r.Id, out var tp) ? tp : null;
        }

        // Réordonner selon l'ordre des IDs (SQL IN ne garantit pas l'ordre)
        var order = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return releases.OrderBy(r => order.TryGetValue(r.Id, out var i) ? i : 0).ToList();
    }

    public async Task<int> CountAsync(ReleaseSearchFilter filter, CancellationToken ct = default)
    {
        var q = _ctx.Releases.AsNoTracking();
        q = ApplyFilters(q, filter);
        return await q.CountAsync(ct);
    }

    public async Task<List<int>> GetAvailableYearsAsync()
    {
        var currentYear = DateTime.Now.Year;

        // Même approche que PartyRepository.GetAvailableYearsAsync : extraire les
        // préfixes d'année distincts côté SQLite, puis parser/filtrer en mémoire.
        var yearStrings = await _ctx.Releases
            .Where(r => r.ReleaseDate != null && r.ReleaseDate.Length >= 4)
            .Select(r => r.ReleaseDate!.Substring(0, 4))
            .Distinct()
            .AsNoTracking()
            .ToListAsync();

        return yearStrings
            .Select(s => int.TryParse(s, out var y) ? y : 0)
            .Where(y => y > 1980 && y <= currentYear)
            .OrderByDescending(y => y)
            .ToList();
    }

    // ─── Compteur de vues ─────────────────────────────────────────────────────

    public async Task<int> IncrementViewCountAsync(int releaseId)
    {
        // ExecuteUpdateAsync (EF Core 7+) : incrémentation atomique en une requête SQL,
        // sans charger l'entité — évite tout conflit avec un éventuel tracking en cours
        // ailleurs et reste rapide même appelé fréquemment.
        await _ctx.Releases
            .Where(r => r.Id == releaseId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ViewCount, r => r.ViewCount + 1));

        return await _ctx.Releases
            .Where(r => r.Id == releaseId)
            .AsNoTracking()
            .Select(r => r.ViewCount)
            .FirstOrDefaultAsync();
    }

    public async Task ResetAllViewCountsAsync()
    {
        await _ctx.Releases.ExecuteUpdateAsync(s => s.SetProperty(r => r.ViewCount, 0));
    }

    // ─── Filtres et tri partagés ──────────────────────────────────────────────

    private IQueryable<Release> ApplyFilters(IQueryable<Release> q, ReleaseSearchFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var pattern = "%" + filter.Query.ToLower() + "%";
            System.Diagnostics.Debug.WriteLine($"[Search] query='{filter.Query}' pattern='{pattern}' authorsOnly={filter.AuthorsOnly} titleOnly={filter.TitleOnly} supertype={filter.Supertype}");
            if (filter.TitleOnly)
            {
                // MediaBrowser (bascule "Titre") : ne cherche QUE dans le titre de la release,
                // ni auteurs ni crédits — pendant du bloc AuthorsOnly ci-dessous.
                q = q.Where(r => EF.Functions.Like(r.Title.ToLower(), pattern));
            }
            else if (filter.AuthorsOnly)
            {
                // MediaBrowser : cherche si le nom commence par la saisie OU si un mot
                // du nom commence par la saisie (ex: "mav" → "MaV" oui, "Maverick" non,
                // "Kral Sumavy" non). On utilise deux patterns :
                // - "query%" : nom commence par la saisie
                // - "% query%" : un mot commence par la saisie
                var startPattern  = filter.Query.ToLower() + "%";
                var wordPattern   = "% " + filter.Query.ToLower() + "%";
                q = q.Where(r =>
                    r.Authors.Any(a =>
                        EF.Functions.Like(a.Nick.Releaser.Name.ToLower(), startPattern) ||
                        EF.Functions.Like(a.Nick.Releaser.Name.ToLower(), wordPattern)));
            }
            else
            {
                // Vue standard : auteurs + crédits techniques
                q = q.Where(r =>
                    EF.Functions.Like(r.Title.ToLower(), pattern) ||
                    r.Authors.Any(a =>
                        EF.Functions.Like(a.Nick.Name.ToLower(), pattern) ||
                        EF.Functions.Like(a.Nick.Releaser.Name.ToLower(), pattern)) ||
                    _ctx.Set<DemoBase.Core.Models.ReleaseCredit>()
                        .Join(_ctx.Set<DemoBase.Core.Models.Releaser>(),
                              c => c.ReleaserId, rel => rel.Id,
                              (c, rel) => new { c.ReleaseId, ReleaserName = rel.Name })
                        .Any(x => x.ReleaseId == r.Id &&
                                  EF.Functions.Like(x.ReleaserName.ToLower(), pattern)));
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.Supertype))
            q = q.Where(r => r.Supertype == filter.Supertype);

        if (filter.ReleaseTypeId.HasValue)
            q = q.Where(r => r.ReleaseTypeId == filter.ReleaseTypeId.Value);

        if (filter.PlatformId.HasValue)
            q = q.Where(r => _ctx.ReleasePlatforms
                .Any(rp => rp.ReleaseId == r.Id && rp.PlatformId == filter.PlatformId.Value));

        if (filter.ReleaserId.HasValue)
        {
            var rid = filter.ReleaserId.Value;
            q = q.Where(r => _ctx.ReleaseAuthors
                .Join(_ctx.Nicks, a => a.NickId, n => n.Id,
                      (a, n) => new { a.ReleaseId, n.ReleaserId })
                .Any(x => x.ReleaseId == r.Id && x.ReleaserId == rid));
        }

        if (filter.PartyId.HasValue)
            q = q.Where(r => _ctx.CompetitionPlacings
                .Join(_ctx.Competitions, cp => cp.CompetitionId, c => c.Id,
                      (cp, c) => new { cp.ReleaseId, c.PartyId })
                .Any(x => x.ReleaseId == r.Id && x.PartyId == filter.PartyId.Value));

        if (!string.IsNullOrWhiteSpace(filter.YearFrom))
            q = q.Where(r => r.ReleaseDate != null
                           && string.Compare(r.ReleaseDate, filter.YearFrom) >= 0);

        if (!string.IsNullOrWhiteSpace(filter.YearTo))
            q = q.Where(r => r.ReleaseDate != null
                           && string.Compare(r.ReleaseDate, filter.YearTo + "-99") <= 0);

        if (filter.IsFavorite.HasValue)
            q = q.Where(r => r.IsFavorite == filter.IsFavorite.Value);

        if (filter.IsUnseen.HasValue && filter.IsUnseen.Value)
            q = q.Where(r => r.ViewCount == 0);

        if (filter.HasDatEntry.HasValue && filter.HasDatEntry.Value)
            q = q.Where(r => r.DemozooId.HasValue
                && _ctx.Set<DemoBase.Core.Models.DatEntry>()
                        .Any(d => d.DemozooId == r.DemozooId));

        return q;
    }

    private static IQueryable<Release> ApplySort(IQueryable<Release> q, ReleaseSearchFilter filter) =>
        filter.SortBy switch
        {
            "Date" => filter.SortDescending
                ? q.OrderByDescending(r => r.ReleaseDate)
                : q.OrderBy(r => r.ReleaseDate),
            _ => filter.SortDescending
                ? q.OrderByDescending(r => r.Title)
                : q.OrderBy(r => r.Title),
        };

    // ─── Autres méthodes ─────────────────────────────────────────────────────

    public async Task<IEnumerable<Release>> GetByReleaserAsync(int releaserId)
    {
        // SQL direct pour les IDs — plus rapide qu'EF LINQ sur cette jointure
        var conn = _ctx.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var releaseIds = new List<int>();
        await using (var cmd = conn.CreateCommand())
        {
            // Deux sources possibles de paternité d'une release pour ce releaser :
            //  - ReleaseAuthors (auteur "principal", via Nicks.ReleaserId)
            //  - ReleaseCredits (crédit détaillé par rôle : code/music/graphics/…).
            //    ATTENTION : ReleaseCredits.ReleaserId stocke en réalité un NickId
            //    (artefact de l'import Demozoo, cf. GetWithFullDetailsAsync) — il
            //    faut donc aussi résoudre via Nicks.ReleaserId, pas comparer
            //    directement à @rid.
            // Sans cette union, un releaser uniquement crédité (table ReleaseCredits)
            // sur une release n'apparaît pas dans sa propre liste de releases.
            cmd.CommandText = """
                SELECT DISTINCT ra.ReleaseId
                FROM ReleaseAuthors ra
                INNER JOIN Nicks n ON n.Id = ra.NickId
                WHERE n.ReleaserId = @rid
                UNION
                SELECT DISTINCT rc.ReleaseId
                FROM ReleaseCredits rc
                INNER JOIN Nicks n ON n.Id = rc.ReleaserId
                WHERE n.ReleaserId = @rid
                """;
            var p = cmd.CreateParameter();
            p.ParameterName = "@rid";
            p.Value = releaserId;
            cmd.Parameters.Add(p);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                releaseIds.Add(reader.GetInt32(0));
        }

        if (releaseIds.Count == 0) return [];

        return await _ctx.Releases
            .Where(r => releaseIds.Contains(r.Id))
            .Include(r => r.ReleaseType)
            .Include(r => r.ReleasePlatforms).ThenInclude(rp => rp.Platform)
            // Classement en compétition (cf. Services.cs, même Include que le calcul de
            // BestRank/BestCompetition sur la liste principale des releases) — nécessaire
            // pour que la fiche releaser affiche aussi le rang, comme sur la fiche release.
            .Include(r => r.CompetitionPlacings).ThenInclude(cp => cp.Competition).ThenInclude(c => c.Party)
            // Nécessaire pour ReleaserDetailViewModel.BuildReleasesFromResultAsync (calcul de
            // HasNoFile, même logique que ReleaseService.SearchAsync).
            .Include(r => r.Links)
            .AsSplitQuery()
            .OrderBy(r => r.Title)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<Dictionary<int, string>> GetCreditedRolesByReleaserAsync(int releaserId)
    {
        // ReleaseCredits.ReleaserId stocke en réalité un NickId (même artefact
        // d'import Demozoo documenté dans GetByReleaserAsync) — résolution via
        // Nicks.ReleaserId, pas de comparaison directe à releaserId.
        // Une personne peut avoir plusieurs rôles distincts sur une même
        // release (ex. "code" + "graphics" + "other") : on les concatène
        // tous, dédupliqués, exactement comme le fait l'onglet Credits d'une
        // release (cf. Services.cs, GetWithFullDetailsAsync) — sinon on ne
        // voit que le premier rôle alors que la fiche release en montre
        // plusieurs ("Code, Graphics, Other").
        var rows = await _ctx.ReleaseCredits
            .Join(_ctx.Nicks, rc => rc.ReleaserId, n => n.Id, (rc, n) => new { n.ReleaserId, rc.ReleaseId, rc.Role })
            .Where(x => x.ReleaserId == releaserId && x.Role != "")
            .AsNoTracking()
            .ToListAsync();

        return rows
            .GroupBy(x => x.ReleaseId)
            .ToDictionary(
                g => g.Key,
                g => string.Join(", ", g.Select(x => x.Role).Distinct()));
    }

    public async Task<IEnumerable<Release>> GetByPartyAsync(int partyId)
    {
        var ids = await _ctx.CompetitionPlacings
            .Join(_ctx.Competitions, cp => cp.CompetitionId, c => c.Id,
                  (cp, c) => new { cp.ReleaseId, c.PartyId })
            .Where(x => x.PartyId == partyId)
            .Select(x => x.ReleaseId)
            .Distinct()
            .ToListAsync();

        return await _ctx.Releases
            .Where(r => ids.Contains(r.Id))
            .Include(r => r.ReleaseType)
            .AsNoTracking().ToListAsync();
    }

    public async Task<IEnumerable<Release>> GetByPlatformAsync(int platformId)
    {
        var ids = await _ctx.ReleasePlatforms
            .Where(rp => rp.PlatformId == platformId)
            .Select(rp => rp.ReleaseId)
            .ToListAsync();

        return await _ctx.Releases
            .Where(r => ids.Contains(r.Id))
            .Include(r => r.ReleaseType)
            .OrderBy(r => r.ReleaseDate)
            .AsNoTracking().ToListAsync();
    }

    public async Task<IEnumerable<Release>> GetByReleaseTypeAsync(int releaseTypeId) =>
        await _ctx.Releases
            .Where(r => r.ReleaseTypeId == releaseTypeId)
            .Include(r => r.ReleasePlatforms).ThenInclude(rp => rp.Platform)
            .OrderBy(r => r.ReleaseDate)
            .AsNoTracking().ToListAsync();

    public async Task<Dictionary<int, string>> GetAuthorNamesByReleaseIdsAsync(List<int> releaseIds)
    {
        var rows = await _ctx.ReleaseAuthors
            .Where(a => releaseIds.Contains(a.ReleaseId))
            .Join(_ctx.Nicks,    a => a.NickId,      n => n.Id, (a, n) => new { a.ReleaseId, n.ReleaserId })
            .Join(_ctx.Releasers, x => x.ReleaserId, r => r.Id, (x, r) => new { x.ReleaseId, r.Name })
            .AsNoTracking()
            .ToListAsync();

        return rows
            .GroupBy(x => x.ReleaseId)
            .ToDictionary(
                g => g.Key,
                g => string.Join(", ", g.Select(x => x.Name).Distinct()));
    }

    public async Task<Release?> GetWithFullDetailsAsync(int id)
    {
        var _sw = System.Diagnostics.Stopwatch.StartNew();

        // Requête principale — crédits et auteurs chargés séparément via JOIN explicites
        var release = await _ctx.Releases
            .Where(r => r.Id == id)
            .Include(r => r.ReleaseType)
            .Include(r => r.ReleasePlatforms).ThenInclude(rp => rp.Platform)
            .Include(r => r.CompetitionPlacings)
                .ThenInclude(cp => cp.Competition)
                .ThenInclude(c => c.Party)
            .Include(r => r.Links)
            .Include(r => r.MediaFiles)
            .Include(r => r.Soundtracks).ThenInclude(s => s.Soundtrack)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync();
        DemoBase.Core.Diagnostics.PerfLogger.Log($"GetDetail[{id}].MainQuery", _sw.ElapsedMilliseconds);
        _sw.Restart();

        if (release == null) return null;

        // ── Releases qui utilisent cette release comme soundtrack ─────────────
        var usedIn = await _ctx.Set<ReleaseSoundtrack>()
            .Where(rs => rs.SoundtrackId == id)
            .Include(rs => rs.Release)
                .ThenInclude(r => r.ReleasePlatforms).ThenInclude(rp => rp.Platform)
            .Include(rs => rs.Release)
                .ThenInclude(r => r.ReleaseType)
            .AsNoTracking()
            .ToListAsync();
        release.UsedInReleases = usedIn;
        DemoBase.Core.Diagnostics.PerfLogger.Log($"GetDetail[{id}].UsedInReleases", _sw.ElapsedMilliseconds);
        _sw.Restart();

        // ── Auteurs ───────────────────────────────────────────────────────────
        var authors = await _ctx.ReleaseAuthors
            .Where(a => a.ReleaseId == id)
            .Join(_ctx.Nicks,    a => a.NickId,     n => n.Id, (a, n) => new { n.Id, n.Name, n.ReleaserId })
            .Join(_ctx.Releasers, x => x.ReleaserId, r => r.Id, (x, r) => new
            {
                NickId       = x.Id,
                NickName     = x.Name,
                ReleaserName = r.Name,
                ReleaserId   = r.Id,
            })
            .AsNoTracking()
            .ToListAsync();
        DemoBase.Core.Diagnostics.PerfLogger.Log($"GetDetail[{id}].Authors", _sw.ElapsedMilliseconds);
        _sw.Restart();

        release.Authors = authors.Select(a => new ReleaseAuthor
        {
            ReleaseId = id,
            NickId    = a.NickId,
            Nick      = new Nick
            {
                Id         = a.NickId,
                Name       = a.NickName,
                ReleaserId = a.ReleaserId,
                Releaser   = new Releaser { Id = a.ReleaserId, Name = a.ReleaserName },
            },
        }).ToList();

        // ── Crédits ───────────────────────────────────────────────────────────
        // ReleaserId stocke en réalité un NickId (données Demozoo)
        // → résolution : NickId → ReleaserId → Releaser.Name
        var credits = await _ctx.ReleaseCredits
            .Where(c => c.ReleaseId == id)
            .Join(_ctx.Nicks, c => c.ReleaserId, n => n.Id, (c, n) => new
            {
                c.ReleaseId,
                NickId      = c.ReleaserId,   // stocke un NickId
                c.Role,
                c.Detail,
                n.ReleaserId,                  // vrai ReleaserId via Nick
                NickName    = n.Name,
            })
            .Join(_ctx.Releasers, x => x.ReleaserId, r => r.Id, (x, r) => new
            {
                x.ReleaseId,
                x.NickId,
                x.Role,
                x.Detail,
                ReleaserId   = r.Id,
                ReleaserName = r.Name,
            })
            .AsNoTracking()
            .ToListAsync();
        DemoBase.Core.Diagnostics.PerfLogger.Log($"GetDetail[{id}].Credits", _sw.ElapsedMilliseconds);
        _sw.Restart();

        release.Credits = credits.Select(c => new ReleaseCredit
        {
            ReleaseId  = c.ReleaseId,
            ReleaserId = c.ReleaserId,
            Role       = c.Role,
            Detail     = c.Detail,
            Releaser   = new Releaser { Id = c.ReleaserId, Name = c.ReleaserName },
        }).ToList();

        // ── AuthorNamesCache pour les soundtracks ─────────────────────────────
        var stIds = release.Soundtracks.Select(s => s.SoundtrackId).ToList();
        if (stIds.Any())
        {
            var stAuthors = await _ctx.ReleaseAuthors
                .Where(a => stIds.Contains(a.ReleaseId))
                .Join(_ctx.Nicks, a => a.NickId, n => n.Id, (a, n) => new { a.ReleaseId, n.ReleaserId })
                .Join(_ctx.Releasers, x => x.ReleaserId, r => r.Id, (x, r) => new { x.ReleaseId, r.Name })
                .AsNoTracking()
                .ToListAsync();

            var stAuthorMap = stAuthors
                .GroupBy(x => x.ReleaseId)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Name)));

            foreach (var st in release.Soundtracks)
                if (st.Soundtrack != null)
                    st.Soundtrack.AuthorNamesCache = stAuthorMap.TryGetValue(st.SoundtrackId, out var n) ? n : string.Empty;
            DemoBase.Core.Diagnostics.PerfLogger.Log($"GetDetail[{id}].SoundtrackAuthors", _sw.ElapsedMilliseconds);
        }

        return release;
    }

    public async Task<ReleaseSummaryDto?> GetRandomMusicAsync(IReadOnlySet<int> excludedIds)
    {
        var excluded = excludedIds.Count > 900 ? [] : excludedIds.ToArray();
        var sql = excluded.Length == 0
            ? "SELECT Id,Title,AuthorNamesCache,ReleaseDate,Supertype,DemozooId,ViewCount,IsFavorite " +
              "FROM Releases WHERE Supertype='music' ORDER BY RANDOM() LIMIT 1"
            : "SELECT Id,Title,AuthorNamesCache,ReleaseDate,Supertype,DemozooId,ViewCount,IsFavorite " +
              $"FROM Releases WHERE Supertype='music' AND Id NOT IN ({string.Join(",", excluded)}) " +
              "ORDER BY RANDOM() LIMIT 1";

        var conn = _ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return new ReleaseSummaryDto
            {
                Id          = reader.GetInt32(0),
                Title       = reader.IsDBNull(1) ? "" : reader.GetString(1),
                AuthorNames = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ReleaseDate = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Supertype   = reader.IsDBNull(4) ? "" : reader.GetString(4),
                DemozooId   = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                ViewCount   = reader.IsDBNull(6) ? 0   : reader.GetInt32(6),
                IsFavorite  = !reader.IsDBNull(7) && reader.GetBoolean(7),
            };
        }
        finally { await conn.CloseAsync(); }
    }

    public async Task<(ReleaseSummaryDto? Release, bool IsExactMatch)> GetOnThisDayOrRandomAsync(int month, int day)
    {
        var conn = _ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            var onThisDay = await ReadOneAsync(conn,
                "SELECT Id,Title,AuthorNamesCache,ReleaseDate,Supertype,DemozooId,ViewCount,IsFavorite " +
                "FROM Releases WHERE length(ReleaseDate)>=10 " +
                "AND CAST(substr(ReleaseDate,6,2) AS INTEGER)=@month " +
                "AND CAST(substr(ReleaseDate,9,2) AS INTEGER)=@day " +
                "ORDER BY RANDOM() LIMIT 1",
                ("@month", month), ("@day", day));
            if (onThisDay != null) return (onThisDay, true);

            var random = await ReadOneAsync(conn,
                "SELECT Id,Title,AuthorNamesCache,ReleaseDate,Supertype,DemozooId,ViewCount,IsFavorite " +
                "FROM Releases ORDER BY RANDOM() LIMIT 1");
            return (random, false);
        }
        finally { await conn.CloseAsync(); }
    }

    private static async Task<ReleaseSummaryDto?> ReadOneAsync(
        System.Data.Common.DbConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new ReleaseSummaryDto
        {
            Id          = reader.GetInt32(0),
            Title       = reader.IsDBNull(1) ? "" : reader.GetString(1),
            AuthorNames = reader.IsDBNull(2) ? "" : reader.GetString(2),
            ReleaseDate = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Supertype   = reader.IsDBNull(4) ? "" : reader.GetString(4),
            DemozooId   = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            ViewCount   = reader.IsDBNull(6) ? 0   : reader.GetInt32(6),
            IsFavorite  = !reader.IsDBNull(7) && reader.GetBoolean(7),
        };
    }
}

// ─── Releaser Repository ──────────────────────────────────────────────────────

public class ReleaserRepository : Repository<Releaser>, IReleaserRepository
{
    public ReleaserRepository(DemoBaseDbContext ctx) : base(ctx) { }

    public async Task<Releaser?> GetWithNicksAndMembersAsync(int id) =>
        await _ctx.Releasers
            .Include(r => r.Nicks)
            .Include(r => r.MembershipsAsScener).ThenInclude(m => m.Group)
            .Include(r => r.MembershipsAsGroup).ThenInclude(m => m.Scener)
            // AsSplitQuery : 4 requêtes simples au lieu d'un seul JOIN cartésien
            // (1 releaser × N nicks × M membres = explosion sans split)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

    public async Task<(IEnumerable<Releaser> Items, int Total)> SearchPagedAsync(
        string? query, bool? isGroup, int page, int pageSize, string? letterFilter = null)
    {
        var q = _ctx.Releasers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q2 = query.ToLowerInvariant();
            q = q.Where(r => r.Name.ToLower().Contains(q2)
                           || r.Nicks.Any(n => n.Name.ToLower().Contains(q2)));
        }

        if (isGroup.HasValue)
            q = q.Where(r => r.IsGroup == isGroup.Value);

        if (!string.IsNullOrEmpty(letterFilter))
        {
            if (letterFilter == "#")
                // Commence par un chiffre ou un caractère non-alphabétique
                q = q.Where(r => r.Name.Length > 0
                    && !((r.Name[0] >= 'A' && r.Name[0] <= 'Z')
                      || (r.Name[0] >= 'a' && r.Name[0] <= 'z')));
            else
                q = q.Where(r => r.Name.ToUpper().StartsWith(letterFilter));
        }

        q = q.OrderBy(r => r.Name);
        var total = await q.CountAsync();
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        // Enrichir avec le nombre de releases par artiste (auteur principal OU
        // simplement crédité — cf. GetByReleaserAsync pour la même logique).
        // ATTENTION : ReleaseCredits.ReleaserId stocke en réalité un NickId
        // (artefact de l'import Demozoo) — il faut résoudre via Nicks.ReleaserId.
        var ids = items.Select(r => r.Id).ToList();
        var authorPairs = await _ctx.ReleaseAuthors
            .Join(_ctx.Nicks, ra => ra.NickId, n => n.Id, (ra, n) => new { n.ReleaserId, ra.ReleaseId })
            .Where(x => ids.Contains(x.ReleaserId))
            .Select(x => new { x.ReleaserId, x.ReleaseId })
            .ToListAsync();
        var creditPairs = await _ctx.ReleaseCredits
            .Join(_ctx.Nicks, rc => rc.ReleaserId, n => n.Id, (rc, n) => new { n.ReleaserId, rc.ReleaseId })
            .Where(x => ids.Contains(x.ReleaserId))
            .Select(x => new { x.ReleaserId, x.ReleaseId })
            .ToListAsync();
        var counts = authorPairs.Concat(creditPairs)
            .GroupBy(x => x.ReleaserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ReleaseId).Distinct().Count());

        foreach (var r in items)
            r.ReleaseCount = counts.GetValueOrDefault(r.Id, 0);

        return (items, total);
    }

    public async Task<IEnumerable<Releaser>> SearchByNameAsync(string name) =>
        await _ctx.Releasers
            .Where(r => r.Name.Contains(name) || r.Nicks.Any(n => n.Name.Contains(name)))
            .OrderBy(r => r.Name)
            .AsNoTracking().ToListAsync();

    public async Task<IEnumerable<Releaser>> GetGroupsAsync() =>
        await _ctx.Releasers.Where(r => r.IsGroup).OrderBy(r => r.Name).AsNoTracking().ToListAsync();

    public async Task<IEnumerable<Releaser>> GetScenersAsync() =>
        await _ctx.Releasers.Where(r => !r.IsGroup).OrderBy(r => r.Name).AsNoTracking().ToListAsync();

}

// ─── Party Repository ─────────────────────────────────────────────────────────

public class PartyRepository : Repository<Party>, IPartyRepository
{
    public PartyRepository(DemoBaseDbContext ctx) : base(ctx) { }

    public async Task<Party?> GetWithCompetitionsAsync(int id) =>
        await _ctx.Parties
            .Where(p => p.Id == id)
            .Include(p => p.PartySeries)
            .Include(p => p.Competitions)
                .ThenInclude(c => c.Placings)
                .ThenInclude(cp => cp.Release)
                .ThenInclude(r => r.ReleaseType)
            .Include(p => p.Competitions)
                .ThenInclude(c => c.Placings)
                .ThenInclude(cp => cp.Release)
                .ThenInclude(r => r.ReleasePlatforms)
                .ThenInclude(rp => rp.Platform)
            // 2026-07-31, retour utilisateur ("il faudrait afficher le petit icone
            // 'interdit' [...] quand il n'y a aucun fichier DATs ou de lien de
            // téléchargement pour la release [...] il n'apparait pas sur cette
            // release") : PartyDetailViewModel a besoin de Release.Links pour calculer
            // HasNoFile (cf. son commentaire, même logique que ReleaseService.SearchAsync)
            // — absent jusqu'ici de cette requête.
            .Include(p => p.Competitions)
                .ThenInclude(c => c.Placings)
                .ThenInclude(cp => cp.Release)
                .ThenInclude(r => r.Links)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync();

    public async Task<IEnumerable<Party>> GetBySeriesAsync(int seriesId) =>
        await _ctx.Parties
            .Where(p => p.PartySeriesId == seriesId)
            .OrderBy(p => p.StartDate)
            .AsNoTracking().ToListAsync();

    public async Task<(IEnumerable<Party> Items, int Total)> SearchPagedAsync(
        string? query, int page, int pageSize, int? year = null, string sortMode = "alpha")
    {
        var q = _ctx.Parties.Include(p => p.PartySeries).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q2 = query.ToLowerInvariant();
            q = q.Where(p => p.Name.ToLower().Contains(q2));
        }
        if (year.HasValue)
            q = q.Where(p => p.StartDate != null && p.StartDate.StartsWith(year.Value.ToString()));
        q = sortMode == "date"
            ? q.OrderByDescending(p => p.StartDate)
            : q.OrderBy(p => p.Name);
        var total = await q.CountAsync();
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return (items, total);
    }

    public async Task<Dictionary<int, int>> GetReleaseCountsByPartyIdsAsync(IEnumerable<int> partyIds)
    {
        var ids = partyIds.ToList();
        return await _ctx.Competitions
            .Where(c => ids.Contains(c.PartyId))
            .GroupBy(c => c.PartyId)
            .Select(g => new { PartyId = g.Key, Count = g.Sum(c => c.Placings.Count) })
            .ToDictionaryAsync(x => x.PartyId, x => x.Count);
    }

    public async Task<List<int>> GetAvailableYearsAsync()
    {
        var currentYear = DateTime.Now.Year;

        // Récupère les préfixes d'année distincts depuis SQLite
        var yearStrings = await _ctx.Parties
            .Where(p => p.StartDate != null && p.StartDate.Length >= 4)
            .Select(p => p.StartDate!.Substring(0, 4))
            .Distinct()
            .AsNoTracking()
            .ToListAsync();

        // Parse et filtre en mémoire
        return yearStrings
            .Select(s => int.TryParse(s, out var y) ? y : 0)
            .Where(y => y > 1980 && y <= currentYear)
            .OrderByDescending(y => y)
            .ToList();
    }
}

// ─── Emulator Repository ──────────────────────────────────────────────────────

public class EmulatorRepository : Repository<Emulator>, IEmulatorRepository
{
    public EmulatorRepository(DemoBaseDbContext ctx) : base(ctx) { }

    public async Task<IEnumerable<EmulatorConfig>> GetConfigsForPlatformAsync(int platformId)
    {
        return await _ctx.EmulatorConfigs
            .Include(ec => ec.Emulator)
            .Include(ec => ec.Platform)
            .Where(ec => ec.PlatformId == platformId)
            .AsNoTracking().ToListAsync();
    }

    // Une seule requête groupée (DISTINCT PlatformId) — évite un N+1 sur GetConfigsForPlatformAsync
    // si on l'appelait plateforme par plateforme depuis PlatformListViewModel.
    public async Task<HashSet<int>> GetConfiguredPlatformIdsAsync()
    {
        var ids = await _ctx.EmulatorConfigs
            .Select(ec => ec.PlatformId)
            .Distinct()
            .ToListAsync();
        return new HashSet<int>(ids);
    }

    public async Task<EmulatorConfig> AddConfigAsync(EmulatorConfig config)
    {
        _ctx.EmulatorConfigs.Add(config);
        await _ctx.SaveChangesAsync();
        // Recharger avec navigation — AsNoTracking : cette instance est ensuite gardée par
        // ProfileViewModel et réutilisée plus tard pour UpdateConfigAsync ; si elle restait
        // suivie ici, elle entrerait en conflit avec elle-même au moment de la sauvegarde
        // (cf. UpdateConfigAsync ci-dessous).
        return await _ctx.EmulatorConfigs
            .Include(c => c.Platform)
            .Include(c => c.Emulator)
            .AsNoTracking()
            .FirstAsync(c => c.Id == config.Id);
    }

    public async Task UpdateConfigAsync(EmulatorConfig config)
    {
        // Une autre instance d'EmulatorConfig avec la même clé peut déjà être suivie par EF
        // Core (typiquement : GetDefaultConfigAsync/GetConfigByIdAsync, appelées ailleurs dans
        // la même session — ex. en consultant la fiche d'une release de cette plateforme —
        // chargent SANS AsNoTracking). Attacher une 2e instance avec la même clé lève
        // DbUpdateException au moment de SaveChangesAsync. Même fix que Repository<T>.UpdateAsync,
        // dupliqué ici car EmulatorConfig passe par cette méthode dédiée, pas par la générique.
        var tracked = _ctx.ChangeTracker.Entries<EmulatorConfig>()
            .FirstOrDefault(e => !ReferenceEquals(e.Entity, config) && e.Entity.Id == config.Id);
        if (tracked != null)
        {
            tracked.CurrentValues.SetValues(config);
            tracked.State = EntityState.Modified;
        }
        else
        {
            _ctx.Entry(config).State = EntityState.Modified;
        }
        await _ctx.SaveChangesAsync();
    }

    public async Task DeleteConfigAsync(int configId)
    {
        var config = await _ctx.EmulatorConfigs.FindAsync(configId);
        if (config != null)
        {
            _ctx.EmulatorConfigs.Remove(config);
            await _ctx.SaveChangesAsync();
        }
    }

    public async Task<IEnumerable<Emulator>> GetAllWithConfigsAsync()
    {
        return await _ctx.Emulators
            .Include(e => e.Configurations).ThenInclude(c => c.Platform)
            .OrderBy(e => e.Name)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task<EmulatorConfig?> GetDefaultConfigAsync(int platformId)
    {
        return await _ctx.EmulatorConfigs
            .Include(ec => ec.Emulator)
            .AsNoTracking()
            .FirstOrDefaultAsync(ec => ec.PlatformId == platformId && ec.IsDefault);
    }

    public async Task<EmulatorConfig?> GetConfigByIdAsync(int configId)
    {
        return await _ctx.EmulatorConfigs
            .Include(ec => ec.Emulator)
            .Include(ec => ec.Platform)
            .AsNoTracking()
            .FirstOrDefaultAsync(ec => ec.Id == configId);
    }

    public async Task<Dictionary<string, string?>> GetSettingsAsync(int emulatorConfigId) =>
        await _ctx.Set<EmulatorSetting>()
            .Where(s => s.EmulatorConfigId == emulatorConfigId)
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value);

    public async Task SaveSettingAsync(int emulatorConfigId, string key, string? value)
    {
        var existing = await _ctx.Set<EmulatorSetting>()
            .FirstOrDefaultAsync(s => s.EmulatorConfigId == emulatorConfigId && s.Key == key);
        if (existing != null)
            existing.Value = value;
        else
            _ctx.Set<EmulatorSetting>().Add(new EmulatorSetting { EmulatorConfigId = emulatorConfigId, Key = key, Value = value });
        await _ctx.SaveChangesAsync();
    }

    public async Task SaveSettingsAsync(int emulatorConfigId, Dictionary<string, string?> settings)
    {
        var existing = await _ctx.Set<EmulatorSetting>()
            .Where(s => s.EmulatorConfigId == emulatorConfigId)
            .ToListAsync();
        foreach (var kv in settings)
        {
            var row = existing.FirstOrDefault(s => s.Key == kv.Key);
            if (row != null) row.Value = kv.Value;
            else _ctx.Set<EmulatorSetting>().Add(new EmulatorSetting { EmulatorConfigId = emulatorConfigId, Key = kv.Key, Value = kv.Value });
        }
        await _ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Retourne le prochain Id disponible pour un émulateur créé manuellement.
    /// Les IDs 0-99 sont réservés aux émulateurs gérés (= (int)EmulatorType).
    /// Le premier Id manuel est 100.
    /// </summary>
    public async Task<int> NextManualIdAsync()
    {
        const int manualStart = 100;
        var maxManual = await _ctx.Emulators
            .Where(e => e.Id >= manualStart)
            .Select(e => (int?)e.Id)
            .MaxAsync();
        return Math.Max(manualStart, (maxManual ?? manualStart - 1) + 1);
    }

    public async Task<IEnumerable<EmulatorConfig>> GetConfigsForEmulatorAsync(int emulatorId)
        => await _ctx.EmulatorConfigs
            .Where(c => c.EmulatorId == emulatorId)
            .AsNoTracking()
            .ToListAsync();

    // Un seul profil par défaut PAR PLATEFORME, tous émulateurs confondus — nécessaire
    // maintenant que plusieurs profils peuvent cibler la même plateforme (ex. "Atari ST
    // 512K" et "Atari ST 1024K" tous deux sur la plateforme "Atari ST" : un seul des
    // deux doit rester le défaut, sinon GetDefaultConfigAsync devient non déterministe).
    public async Task ClearDefaultForPlatformAsync(int platformId, int exceptConfigId)
    {
        var others = await _ctx.EmulatorConfigs
            .Where(ec => ec.PlatformId == platformId && ec.IsDefault && ec.Id != exceptConfigId)
            .ToListAsync();
        foreach (var ec in others) ec.IsDefault = false;
        if (others.Count > 0) await _ctx.SaveChangesAsync();
    }
}

// ─── ReleaseType Repository ───────────────────────────────────────────────────

public class ReleaseTypeRepository : Repository<ReleaseType>, IReleaseTypeRepository
{
    public ReleaseTypeRepository(DemoBaseDbContext ctx) : base(ctx) { }

    public async Task<ReleaseType?> GetByNameAsync(string name) =>
        await _ctx.ReleaseTypes.FirstOrDefaultAsync(rt => rt.Name == name);

    public async Task<IEnumerable<ReleaseTypeDto>> GetAllWithCountAsync() =>
        await _ctx.ReleaseTypes
            .AsNoTracking()
            .OrderBy(rt => rt.SortOrder).ThenBy(rt => rt.Name)
            .Select(rt => new ReleaseTypeDto
            {
                Id           = rt.Id,
                Name         = rt.Name,
                Supertype    = rt.Supertype,
                Description  = rt.Description,
                SortOrder    = rt.SortOrder,
                ReleaseCount = rt.Releases.Count,
            })
            .ToListAsync();

    public async Task<bool> IsInUseAsync(int id) =>
        await _ctx.Releases.AnyAsync(r => r.ReleaseTypeId == id);
}

// ─── Unit of Work ─────────────────────────────────────────────────────────────

public class UnitOfWork : IUnitOfWork
{
    private readonly DemoBaseDbContext _ctx;
    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _transaction;

    public IReleaseRepository     Releases     { get; }
    public IReleaserRepository    Releasers    { get; }
    public IPartyRepository       Parties      { get; }
    public IEmulatorRepository    Emulators    { get; }
    public IReleaseTypeRepository ReleaseTypes { get; }
    public IRepository<Platform>    Platforms    { get; }
    public IRepository<PartySeries> PartySeries  { get; }
    public IRepository<Competition> Competitions { get; }
    public IRepository<MediaFile>   MediaFiles   { get; }
    public IRepository<ReleaseLink> ReleaseLinks { get; }
    public IRepository<Nick>        Nicks        { get; }

    public UnitOfWork(DemoBaseDbContext ctx)
    {
        _ctx         = ctx;
        Releases     = new ReleaseRepository(ctx);
        Releasers    = new ReleaserRepository(ctx);
        Parties      = new PartyRepository(ctx);
        Emulators    = new EmulatorRepository(ctx);
        ReleaseTypes = new ReleaseTypeRepository(ctx);
        Platforms    = new Repository<Platform>(ctx);
        PartySeries  = new Repository<PartySeries>(ctx);
        Competitions = new Repository<Competition>(ctx);
        MediaFiles   = new Repository<MediaFile>(ctx);
        ReleaseLinks = new Repository<ReleaseLink>(ctx);
        Nicks        = new Repository<Nick>(ctx);
    }

    public async Task<int> SaveChangesAsync() => await _ctx.SaveChangesAsync();

    public async Task BeginTransactionAsync()
        => _transaction = await _ctx.Database.BeginTransactionAsync();
    public async Task CommitTransactionAsync()
        { if (_transaction != null) await _transaction.CommitAsync(); }
    public async Task RollbackTransactionAsync()
        { if (_transaction != null) await _transaction.RollbackAsync(); }

    public void Dispose() { _transaction?.Dispose(); _ctx.Dispose(); }
}
