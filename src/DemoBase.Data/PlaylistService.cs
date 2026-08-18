using DemoBase.Core.Models;
using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>Service CRUD pour les playlists de soundtracks favoris (config.db).</summary>
public class PlaylistService
{
    private readonly string _connectionString;

    public PlaylistService(string connectionString)
        => _connectionString = connectionString;

    // ── Playlists ────────────────────────────────────────────────────────────

    public async Task<List<Playlist>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, SortOrder, CreatedAt FROM "Playlists" ORDER BY SortOrder, Id;
            """;
        var list = new List<Playlist>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new Playlist
            {
                Id        = r.GetInt32(0),
                Name      = r.IsDBNull(1) ? "" : r.GetString(1),
                SortOrder = r.GetInt32(2),
                CreatedAt = r.IsDBNull(3) ? DateTime.UtcNow : DateTime.Parse(r.GetString(3)),
            });
        return list;
    }

    public async Task<Playlist> CreateAsync(string name)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var maxCmd = conn.CreateCommand();
        maxCmd.CommandText = """SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM "Playlists";""";
        var sortOrder = (long)(await maxCmd.ExecuteScalarAsync() ?? 0L);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "Playlists" (Name, SortOrder) VALUES (@name, @sort);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@sort", sortOrder);
        var id = (long)(await cmd.ExecuteScalarAsync() ?? 0L);

        return new Playlist { Id = (int)id, Name = name, SortOrder = (int)sortOrder };
    }

    public async Task RenameAsync(int playlistId, string newName)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """UPDATE "Playlists" SET Name=@name WHERE Id=@id;""";
        cmd.Parameters.AddWithValue("@name", newName);
        cmd.Parameters.AddWithValue("@id", playlistId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(int playlistId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        // Suppression explicite des pistes (ne pas dépendre de ON DELETE CASCADE,
        // qui exige PRAGMA foreign_keys=ON — non garanti sur toutes les connexions).
        await using var cmdTracks = conn.CreateCommand();
        cmdTracks.CommandText = """DELETE FROM "PlaylistTracks" WHERE PlaylistId=@id;""";
        cmdTracks.Parameters.AddWithValue("@id", playlistId);
        await cmdTracks.ExecuteNonQueryAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """DELETE FROM "Playlists" WHERE Id=@id;""";
        cmd.Parameters.AddWithValue("@id", playlistId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Pistes d'une playlist ────────────────────────────────────────────────

    /// <summary>
    /// Retourne les pistes d'une playlist, dans l'ordre de Position, avec les
    /// métadonnées résolues via JOIN sur FavoriteSoundtracks (une playlist ne
    /// peut contenir que des morceaux déjà mis en favori).
    /// </summary>
    public async Task<List<FavoriteSoundtrack>> GetTracksAsync(int playlistId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fs.Id, fs.SoundtrackDemozooId, fs.Title, fs.AuthorNames, fs.RomName,
                   fs.ZipPath, fs.ReleaseTitle, fs.AddedAt
            FROM "PlaylistTracks" pt
            JOIN "FavoriteSoundtracks" fs ON fs.SoundtrackDemozooId = pt.SoundtrackDemozooId
            WHERE pt.PlaylistId = @id
            ORDER BY pt.Position, pt.Id;
            """;
        cmd.Parameters.AddWithValue("@id", playlistId);
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

    public async Task AddTrackAsync(int playlistId, int soundtrackDemozooId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var maxCmd = conn.CreateCommand();
        maxCmd.CommandText = """
            SELECT COALESCE(MAX(Position), -1) + 1 FROM "PlaylistTracks" WHERE PlaylistId=@id;
            """;
        maxCmd.Parameters.AddWithValue("@id", playlistId);
        var position = (long)(await maxCmd.ExecuteScalarAsync() ?? 0L);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO "PlaylistTracks" (PlaylistId, SoundtrackDemozooId, Position)
            VALUES (@playlistId, @trackId, @position);
            """;
        cmd.Parameters.AddWithValue("@playlistId", playlistId);
        cmd.Parameters.AddWithValue("@trackId", soundtrackDemozooId);
        cmd.Parameters.AddWithValue("@position", position);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveTrackAsync(int playlistId, int soundtrackDemozooId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM "PlaylistTracks" WHERE PlaylistId=@playlistId AND SoundtrackDemozooId=@trackId;
            """;
        cmd.Parameters.AddWithValue("@playlistId", playlistId);
        cmd.Parameters.AddWithValue("@trackId", soundtrackDemozooId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Déplace une piste d'une position (-1 = monter, +1 = descendre) en
    /// échangeant sa Position avec celle de la piste voisine.</summary>
    public async Task MoveTrackAsync(int playlistId, int soundtrackDemozooId, int direction)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var listCmd = conn.CreateCommand();
        listCmd.CommandText = """
            SELECT Id, SoundtrackDemozooId, Position FROM "PlaylistTracks"
            WHERE PlaylistId=@id ORDER BY Position, Id;
            """;
        listCmd.Parameters.AddWithValue("@id", playlistId);
        var rows = new List<(int Id, int TrackId, int Position)>();
        await using (var r = await listCmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                rows.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));

        var idx = rows.FindIndex(x => x.TrackId == soundtrackDemozooId);
        var swapIdx = idx + direction;
        if (idx < 0 || swapIdx < 0 || swapIdx >= rows.Count) return;

        var a = rows[idx];
        var b = rows[swapIdx];

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        await using (var cmd1 = conn.CreateCommand())
        {
            cmd1.Transaction = tx;
            cmd1.CommandText = """UPDATE "PlaylistTracks" SET Position=@pos WHERE Id=@id;""";
            cmd1.Parameters.AddWithValue("@pos", b.Position);
            cmd1.Parameters.AddWithValue("@id", a.Id);
            await cmd1.ExecuteNonQueryAsync();
        }
        await using (var cmd2 = conn.CreateCommand())
        {
            cmd2.Transaction = tx;
            cmd2.CommandText = """UPDATE "PlaylistTracks" SET Position=@pos WHERE Id=@id;""";
            cmd2.Parameters.AddWithValue("@pos", a.Position);
            cmd2.Parameters.AddWithValue("@id", b.Id);
            await cmd2.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
