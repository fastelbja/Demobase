using DemoBase.Data.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DemoBase.Media;

/// <summary>
/// Télécharge les screenshots Demozoo en local en respectant le serveur :
/// - 1 requête / 500ms (2 req/s max)
/// - Pause de 5s toutes les 100 images
/// - Reprise : ignore les fichiers déjà présents
/// - Structure miroir : images/screens/s/05/10/b46d.1695.png
/// </summary>
public class ScreenshotDownloadService
{
    private const string BaseUrl     = "https://media.demozoo.org/";
    private const int    DelayMs     = 500;      // délai entre chaque image
    private const int    PauseEvery  = 100;      // pause toutes les N images
    private const int    PauseMs     = 5_000;    // durée de la pause
    private const int    TimeoutSec  = 20;

    private readonly IDbContextFactory<DemoBaseDbContext> _ctxFactory;

    public ScreenshotDownloadService(IDbContextFactory<DemoBaseDbContext> ctxFactory)
        => _ctxFactory = ctxFactory;

    // ─── Téléchargement principal ─────────────────────────────────────────────

    public async Task DownloadAllAsync(
        string             imagesRoot,
        IProgress<ScreenshotDownloadProgress> progress,
        CancellationToken  ct = default)
    {
        // 1. Récupère toutes les URLs depuis la base
        await using var ctx   = await _ctxFactory.CreateDbContextAsync(ct);
        var connStr            = ctx.Database.GetConnectionString()!;
        var urls               = await GetAllScreenshotUrlsAsync(connStr, ct);

        var total     = urls.Count;
        var done      = 0;
        var skipped   = 0;
        var errors    = 0;

        progress.Report(new($"0 / {total}", 0, total, done, skipped, errors));

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent",
            "DemoBase/1.0 (personal archive tool; contact: demobase@local)");
        http.Timeout = TimeSpan.FromSeconds(TimeoutSec);

        foreach (var (id, url) in urls)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Chemin local miroir (enlever le préfixe https://media.demozoo.org/)
                var relative  = url.Replace(BaseUrl, "").TrimStart('/');
                var localPath = Path.Combine(imagesRoot, relative.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(localPath))
                {
                    skipped++;
                    done++;
                    if (done % 50 == 0)
                        progress.Report(new($"{done} / {total} ({skipped} déjà présents)",
                            done, total, done, skipped, errors));
                    continue;
                }

                // Créer le répertoire si nécessaire
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                // Télécharger
                var bytes = await http.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(localPath, bytes, ct);

                done++;
                progress.Report(new(
                    $"{done} / {total} — {Path.GetFileName(localPath)}",
                    done, total, done, skipped, errors));

                // Délai poli entre chaque requête
                await Task.Delay(DelayMs, ct);

                // Pause prolongée toutes les N images pour éviter le ban
                if (done % PauseEvery == 0 && done < total)
                {
                    progress.Report(new(
                        $"Pause {PauseMs / 1000}s (prévention anti-ban)…",
                        done, total, done, skipped, errors));
                    await Task.Delay(PauseMs, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                errors++;
                done++;
            }
        }

        // Met à jour les chemins locaux en base
        if (done - skipped - errors > 0)
        {
            progress.Report(new("Mise à jour des chemins en base…",
                done, total, done, skipped, errors));
            await UpdateLocalPathsAsync(connStr, imagesRoot, ct);
        }

        progress.Report(new(
            $"Terminé — {done - skipped - errors} téléchargés, {skipped} ignorés, {errors} erreurs",
            total, total, done, skipped, errors));
    }

    // ─── Nombre d'images à télécharger ───────────────────────────────────────

    public async Task<(int Total, int AlreadyLocal)> GetStatsAsync(
        string imagesRoot, CancellationToken ct = default)
    {
        await using var ctx = await _ctxFactory.CreateDbContextAsync(ct);
        var connStr          = ctx.Database.GetConnectionString()!;
        var urls = await GetAllScreenshotUrlsAsync(connStr, ct);

        var alreadyLocal = urls.Count(u =>
        {
            var relative  = u.Url.Replace(BaseUrl, "").TrimStart('/');
            var localPath = Path.Combine(imagesRoot,
                relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(localPath);
        });

        return (urls.Count, alreadyLocal);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<List<(int Id, string Url)>> GetAllScreenshotUrlsAsync(
        string connStr, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", "FilePath"
            FROM "MediaFiles"
            WHERE "FilePath" LIKE 'http%'
            ORDER BY "Id";
            """;

        var result = new List<(int, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetInt32(0), reader.GetString(1)));

        return result;
    }

    /// <summary>
    /// Met à jour FilePath en base pour pointer vers le fichier local
    /// quand celui-ci existe — l'URL reste en fallback si absent.
    /// </summary>
    private static async Task UpdateLocalPathsAsync(
        string connStr, string imagesRoot, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;

        // Récupère toutes les URLs encore distantes
        cmd.CommandText = """
            SELECT "Id", "FilePath" FROM "MediaFiles"
            WHERE "FilePath" LIKE 'http%';
            """;

        var toUpdate = new List<(int Id, string LocalPath)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id  = reader.GetInt32(0);
                var url = reader.GetString(1);
                var rel = url.Replace(BaseUrl, "").TrimStart('/');
                var loc = Path.Combine(imagesRoot,
                    rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loc))
                    toUpdate.Add((id, loc));
            }
        }

        // UPDATE en batch
        cmd.CommandText = "UPDATE \"MediaFiles\" SET \"FilePath\" = @p WHERE \"Id\" = @id;";
        cmd.Parameters.Add(new SqliteParameter("@p",  ""));
        cmd.Parameters.Add(new SqliteParameter("@id", 0));
        foreach (var (id, loc) in toUpdate)
        {
            cmd.Parameters["@p"].Value  = loc;
            cmd.Parameters["@id"].Value = id;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}

// ─── Progress ─────────────────────────────────────────────────────────────────

public record ScreenshotDownloadProgress(
    string Message,
    long   Current,
    long   Total,
    int    Downloaded,
    int    Skipped,
    int    Errors)
{
    public double Percent => Total > 0 ? (double)Current / Total * 100 : 0;
}
