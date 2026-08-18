using DemoBase.Core.Models;
using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>Service CRUD pour les graphiques favoris (config.db).</summary>
public class FavoriteGraphicService
{
    private readonly string _connectionString;

    public FavoriteGraphicService(string connectionString)
        => _connectionString = connectionString;

    public async Task<List<FavoriteGraphic>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ReleaseDemozooId, Title, AuthorNames, ZipPath, FileInZip, AddedAt
            FROM "FavoriteGraphics" ORDER BY lower(Title);
            """;
        var list = new List<FavoriteGraphic>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new FavoriteGraphic
            {
                Id               = r.GetInt32(0),
                ReleaseDemozooId = r.GetInt32(1),
                Title            = r.IsDBNull(2) ? "" : r.GetString(2),
                AuthorNames      = r.IsDBNull(3) ? null : r.GetString(3),
                ZipPath          = r.IsDBNull(4) ? null : r.GetString(4),
                FileInZip        = r.IsDBNull(5) ? null : r.GetString(5),
                AddedAt          = r.IsDBNull(6) ? DateTime.UtcNow : DateTime.Parse(r.GetString(6)),
            });
        return list;
    }

    public async Task<bool> IsFavoriteAsync(int releaseDemozooId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT COUNT(*) FROM "FavoriteGraphics" WHERE ReleaseDemozooId=@id;""";
        cmd.Parameters.AddWithValue("@id", releaseDemozooId);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L) > 0;
    }

    public async Task AddAsync(FavoriteGraphic fav)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO "FavoriteGraphics"
                (ReleaseDemozooId, Title, AuthorNames, ZipPath, FileInZip)
            VALUES (@id, @title, @authors, @zip, @file);
            """;
        cmd.Parameters.AddWithValue("@id",      fav.ReleaseDemozooId);
        cmd.Parameters.AddWithValue("@title",   fav.Title);
        cmd.Parameters.AddWithValue("@authors", (object?)fav.AuthorNames ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@zip",     (object?)fav.ZipPath     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@file",    (object?)fav.FileInZip   ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveAsync(int releaseDemozooId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """DELETE FROM "FavoriteGraphics" WHERE ReleaseDemozooId=@id;""";
        cmd.Parameters.AddWithValue("@id", releaseDemozooId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ToggleAsync(FavoriteGraphic fav)
    {
        if (await IsFavoriteAsync(fav.ReleaseDemozooId))
            await RemoveAsync(fav.ReleaseDemozooId);
        else
            await AddAsync(fav);
    }
}
