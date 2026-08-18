using DemoBase.Data.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DemoBase.Import;

/// <summary>
/// Vérifie si une nouvelle version du dump Demozoo est disponible
/// en comparant le Last-Modified / Content-Length du fichier distant
/// avec la version stockée dans la base locale.
/// </summary>
public class DemozooVersionService
{
    private const string DumpUrl    = "https://data.demozoo.org/demozoo-export.sql.gz";
    private const string TableName  = "DbVersion";
    private const string KeyDump    = "demozoo_dump";

    private readonly IDbContextFactory<DemoBaseDbContext> _ctxFactory;

    public DemozooVersionService(IDbContextFactory<DemoBaseDbContext> ctxFactory)
        => _ctxFactory = ctxFactory;

    /// <summary>Vrai si la table Releases existe et contient au moins une ligne.
    /// Rendu public pour que le wizard puisse détecter, sur une réouverture,
    /// que l'étape Base de données a déjà été complétée sans redéclencher un
    /// téléchargement inutile.</summary>
    public async Task<bool> HasReleasesAsync()
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var connStr = ctx.Database.GetConnectionString()!;
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Releases';";
        var tableExists = (long)(await cmd.ExecuteScalarAsync() ?? 0L) > 0;
        if (!tableExists) return false;
        cmd.CommandText = "SELECT COUNT(*) FROM \"Releases\" LIMIT 1;";
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L) > 0;
    }

    // ─── Vérification de version ──────────────────────────────────────────────

    public async Task<DemozooVersionInfo> CheckAsync(CancellationToken ct = default)
    {
        // 1. Lire la version locale
        var local = await GetLocalVersionAsync();

        // 2. Interroger le serveur (HEAD uniquement — pas de téléchargement)
        DemozooVersionInfo remote;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "DemoBase/1.0");
            http.Timeout = TimeSpan.FromSeconds(10);

            using var req = new HttpRequestMessage(HttpMethod.Head, DumpUrl);
            using var resp = await http.SendAsync(req, ct);

            var lastModified = resp.Content.Headers.LastModified?.UtcDateTime
                            ?? resp.Headers.Date?.UtcDateTime;
            var size = resp.Content.Headers.ContentLength;

            remote = new DemozooVersionInfo
            {
                LastModified  = lastModified,
                ContentLength = size,
                IsAvailable   = resp.IsSuccessStatusCode,
            };
        }
        catch
        {
            remote = new DemozooVersionInfo { IsAvailable = false };
        }

        remote.LocalVersion  = local;
        remote.HasUpdate     = remote.IsAvailable
            && local != null
            && (remote.LastModified > local.LastModified
                || remote.ContentLength != local.ContentLength);

        // Premier import = aucune version ET la base est vide
        var hasData = await HasReleasesAsync();
        remote.IsFirstImport = !hasData;

        return remote;
    }

    // ─── Sauvegarde de la version après import ────────────────────────────────

    public async Task SaveVersionAsync(DateTime? lastModified, long? contentLength)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var connStr = ctx.Database.GetConnectionString()!;

        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        // Crée la table si besoin
        await ExecAsync(conn, $"""
            CREATE TABLE IF NOT EXISTS "{TableName}" (
                "Key"          TEXT NOT NULL PRIMARY KEY,
                "LastModified" TEXT,
                "ContentLength" INTEGER,
                "ImportedAt"   TEXT NOT NULL
            );
            """);

        await ExecAsync(conn, $"""
            INSERT OR REPLACE INTO "{TableName}" ("Key","LastModified","ContentLength","ImportedAt")
            VALUES ('{KeyDump}',
                    '{lastModified?.ToString("O") ?? ""}',
                    {contentLength?.ToString() ?? "NULL"},
                    '{DateTime.UtcNow:O}');
            """);
    }

    // ─── Lecture de la version locale ─────────────────────────────────────────

    public async Task<LocalVersionInfo?> GetLocalVersionAsync()
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync();
        var connStr = ctx.Database.GetConnectionString()!;

        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        // Vérifie si la table existe
        await using var check = conn.CreateCommand();
        check.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{TableName}';";
        var exists = await check.ExecuteScalarAsync() != null;
        if (!exists) return null;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT "LastModified","ContentLength","ImportedAt"
            FROM "{TableName}" WHERE "Key"='{KeyDump}';
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new LocalVersionInfo
        {
            LastModified  = reader.IsDBNull(0) ? null
                : DateTime.TryParse(reader.GetString(0), out var d) ? d : null,
            ContentLength = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            ImportedAt    = reader.IsDBNull(2) ? null
                : DateTime.TryParse(reader.GetString(2), out var d2) ? d2 : null,
        };
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

public record LocalVersionInfo
{
    public DateTime? LastModified  { get; init; }
    public long?     ContentLength { get; init; }
    public DateTime? ImportedAt    { get; init; }
}

public record DemozooVersionInfo
{
    public LocalVersionInfo? LocalVersion   { get; set; }
    public DateTime?         LastModified   { get; set; }
    public long?             ContentLength  { get; set; }
    public bool              IsAvailable    { get; set; }
    public bool              HasUpdate      { get; set; }
    public bool              IsFirstImport  { get; set; }

    public string RemoteSizeLabel => ContentLength.HasValue
        ? $"{ContentLength.Value / (1024 * 1024):N0} MB"
        : "taille inconnue";

    public string LocalImportedLabel => LocalVersion?.ImportedAt.HasValue == true
        ? $"Importé le {LocalVersion.ImportedAt.Value:dd/MM/yyyy}"
        : "Jamais importé";
}
