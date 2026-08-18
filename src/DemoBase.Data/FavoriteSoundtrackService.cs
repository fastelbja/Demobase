using DemoBase.Core.Models;
using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>Service CRUD pour les soundtracks favoris (config.db).</summary>
public class FavoriteSoundtrackService
{
    private readonly string _connectionString;

    // Cache en mémoire des IDs favoris — évite N ouvertures de connexion ADO.NET
    // dans BuildSoundtrackDtosAsync (1 IsFavoriteAsync par soundtrack = N×50ms).
    // Invalidé à chaque Add/Remove. Chargé paresseusement au premier appel.
    private HashSet<int>? _favoriteIds;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public FavoriteSoundtrackService(string connectionString)
        => _connectionString = connectionString;

    // ── Cache ─────────────────────────────────────────────────────────────────

    private async Task<HashSet<int>> GetFavoriteIdsAsync()
    {
        if (_favoriteIds != null) return _favoriteIds;
        await _cacheLock.WaitAsync();
        try
        {
            if (_favoriteIds != null) return _favoriteIds;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """SELECT SoundtrackDemozooId FROM "FavoriteSoundtracks";""";
            var ids = new HashSet<int>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
            _favoriteIds = ids;
            return ids;
        }
        finally { _cacheLock.Release(); }
    }

    private void InvalidateCache() => _favoriteIds = null;

    // ── API publique ──────────────────────────────────────────────────────────

    public async Task<List<FavoriteSoundtrack>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Ordre d'insertion (Id croissant) plutôt qu'alphabétique — c'est aussi
        // l'ordre utilisé par PlayAll (FavoriteSoundtracksViewModel), pour que la
        // liste affichée corresponde exactement à l'ordre de lecture.
        cmd.CommandText = """
            SELECT Id, SoundtrackDemozooId, Title, AuthorNames, RomName, ZipPath, ReleaseTitle, AddedAt
            FROM "FavoriteSoundtracks" ORDER BY Id;
            """;
        var list = new List<FavoriteSoundtrack>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new FavoriteSoundtrack
            {
                Id                  = r.GetInt32(0),
                SoundtrackDemozooId = r.GetInt32(1),
                Title               = r.IsDBNull(2) ? "" : r.GetString(2),
                AuthorNames         = r.IsDBNull(3) ? null : r.GetString(3),
                RomName             = r.IsDBNull(4) ? null : r.GetString(4),
                ZipPath             = r.IsDBNull(5) ? null : r.GetString(5),
                ReleaseTitle        = r.IsDBNull(6) ? null : r.GetString(6),
                AddedAt             = r.IsDBNull(7) ? DateTime.UtcNow : DateTime.Parse(r.GetString(7)),
            });
        return list;
    }

    /// <summary>
    /// Vérifie si une soundtrack est favorite — O(1) via le cache en mémoire,
    /// sans ouvrir de connexion SQLite (contrairement à l'ancienne implémentation
    /// qui coûtait ~50 ms par appel à cause de l'ouverture de connexion ADO.NET).
    /// </summary>
    public async Task<bool> IsFavoriteAsync(int soundtrackDemozooId)
        => (await GetFavoriteIdsAsync()).Contains(soundtrackDemozooId);

    public async Task AddAsync(FavoriteSoundtrack fav)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO "FavoriteSoundtracks"
                (SoundtrackDemozooId, Title, AuthorNames, RomName, ZipPath, ReleaseTitle)
            VALUES (@id, @title, @authors, @rom, @zip, @release);
            """;
        cmd.Parameters.AddWithValue("@id",      fav.SoundtrackDemozooId);
        cmd.Parameters.AddWithValue("@title",   fav.Title);
        cmd.Parameters.AddWithValue("@authors", (object?)fav.AuthorNames ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rom",     (object?)fav.RomName     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@zip",     (object?)fav.ZipPath     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@release", (object?)fav.ReleaseTitle ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        InvalidateCache();
    }

    public async Task RemoveAsync(int soundtrackDemozooId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """DELETE FROM "FavoriteSoundtracks" WHERE SoundtrackDemozooId=@id;""";
        cmd.Parameters.AddWithValue("@id", soundtrackDemozooId);
        await cmd.ExecuteNonQueryAsync();
        InvalidateCache();
    }

    public async Task ToggleAsync(FavoriteSoundtrack fav)
    {
        if (await IsFavoriteAsync(fav.SoundtrackDemozooId))
            await RemoveAsync(fav.SoundtrackDemozooId);
        else
            await AddAsync(fav);
    }
}
