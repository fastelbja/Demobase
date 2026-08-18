using DemoBase.Data;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace DemoBase.Import;

// ─── Progress ────────────────────────────────────────────────────────────────

public record RawExportProgress(
    string  Message,
    long    BytesRead,
    long    TotalBytes,
    int     TablesCreated,
    long    RowsInserted,
    bool    IsComplete = false,
    string? Error      = null);

// ─── DemozooRawExportService ─────────────────────────────────────────────────
// Télécharge le dump SQL Demozoo et l'importe tel quel dans demozoo_raw.db
// sans aucun mapping — toutes les tables Postgres → SQLite avec leurs noms d'origine.

public class DemozooRawExportService
{
    private const string DumpUrl   = "https://data.demozoo.org/demozoo-export.sql.gz";
    private const int    BatchSize = 1000;

    private readonly string _dbPath;

    public DemozooRawExportService(string databaseDir)
    {
        _dbPath = Path.Combine(databaseDir, "demozoo_raw.db");
    }

    public string DbPath => _dbPath;

    // ─── Import principal ─────────────────────────────────────────────────────

    public async Task ExportAsync(
        IProgress<RawExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(new("Connexion à data.demozoo.org…", 0, 0, 0, 0));

        // Supprimer l'ancienne DB
        if (File.Exists(_dbPath)) File.Delete(_dbPath);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "DemoBase/1.0");
        http.Timeout = TimeSpan.FromHours(3);

        using var response = await http.GetAsync(DumpUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;

        await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
        var countingStream = new RawCountingStream(networkStream, bytes =>
            progress?.Report(new(
                $"Téléchargement… {FormatBytes(bytes)} / {FormatBytes(totalBytes)}",
                bytes, totalBytes, 0, 0)));

        await using var gzip   = new GZipStream(countingStream, CompressionMode.Decompress);
        using  var      reader = new StreamReader(gzip, Encoding.UTF8, bufferSize: 256 * 1024);

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(ct);

        // Optimisations SQLite pour l'import en masse
        await ExecAsync(conn, "PRAGMA synchronous=OFF;");
        await ExecAsync(conn, "PRAGMA journal_mode=MEMORY;");
        await ExecAsync(conn, "PRAGMA temp_store=MEMORY;");
        await ExecAsync(conn, "PRAGMA cache_size=-131072;"); // 128 MB
        await ExecAsync(conn, "PRAGMA foreign_keys=OFF;");

        await ParseAndInsertAsync(conn, reader, totalBytes, progress, ct);

        await ExecAsync(conn, "PRAGMA foreign_keys=ON;");
        await ExecAsync(conn, "PRAGMA synchronous=NORMAL;");

        // 2026-08-07, retour utilisateur (deux rapports indépendants d'utilisateurs
        // lançant DemoBase depuis un partage SMB) : ce PRAGMA passait inconditionnellement
        // en WAL, SANS la protection réseau déjà en place pour demobase.db depuis le
        // 2026-08-02 (cf. DbInitializer.InitializeAsync/IsNetworkPath) — demozoo_raw.db vit
        // pourtant dans le MÊME dossier "Database" que demobase.db, donc sur le MÊME
        // partage réseau si l'app y est lancée. Un des deux rapports montre un APPCRASH
        // (KERNELBASE.dll, code e0434352) avec procmon montrant une boucle dans
        // C:\Windows\CSC\...\namespace\<IP du serveur> juste après un import Demozoo —
        // signature typique d'un accès mmap au fichier "-shm" du mode WAL qui bascule sur
        // le cache "Offline Files" (CSC) de Windows pour ce partage, documenté par SQLite
        // lui-même comme non fiable sur SMB/NFS (sqlite.org/wal.html) ; ce genre de crash
        // survient au niveau natif (SEH Windows dans SQLite) et n'est PAS rattrapable par
        // le try/catch managé de DemozooRawExportWindow.xaml.cs (RunAsync), d'où le plantage
        // silencieux/sans message observé par les deux utilisateurs. Même remède que
        // demobase.db : DELETE (rollback journal classique, sans mmap) uniquement quand la
        // base est détectée sur un chemin réseau.
        var journalMode = DbInitializer.IsNetworkPath(_dbPath) ? "DELETE" : "WAL";
        await ExecAsync(conn, $"PRAGMA journal_mode={journalMode};");

        progress?.Report(new("Optimisation…", totalBytes, totalBytes, 0, 0));
        await ExecAsync(conn, "ANALYZE;");

        progress?.Report(new("Import terminé !", totalBytes, totalBytes, 0, 0,
            IsComplete: true));
    }

    // ─── Parser le dump SQL Postgres ─────────────────────────────────────────

    private static async Task ParseAndInsertAsync(
        SqliteConnection             conn,
        StreamReader                 reader,
        long                         totalBytes,
        IProgress<RawExportProgress>? progress,
        CancellationToken             ct)
    {
        string?  currentTable   = null;
        string[] currentCols    = [];
        int      tablesCreated  = 0;
        long     rowsInserted   = 0;
        var      batch          = new List<string[]>();
        long     bytesRead      = 0;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            bytesRead += line.Length + 1;

            // ── CREATE TABLE ──────────────────────────────────────────────────
            if (line.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase))
            {
                var tableName = ExtractTableName(line);
                if (tableName == null) continue;

                // Lire les colonnes jusqu'au );
                var sb = new StringBuilder(line).AppendLine();
                while ((line = await reader.ReadLineAsync(ct)) != null && !line.TrimStart().StartsWith(");"))
                {
                    sb.AppendLine(line);
                    bytesRead += line.Length + 1;
                }

                var createSql = BuildCreateTable(tableName, sb.ToString());
                if (createSql != null)
                {
                    try
                    {
                        await ExecAsync(conn, $"DROP TABLE IF EXISTS \"{tableName}\";");
                        await ExecAsync(conn, createSql);
                        tablesCreated++;
                        progress?.Report(new($"Table créée : {tableName}",
                            bytesRead, totalBytes, tablesCreated, rowsInserted));
                    }
                    catch { /* ignorer les tables non supportées */ }
                }
                continue;
            }

            // ── COPY … FROM stdin ─────────────────────────────────────────────
            if (line.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase))
            {
                currentTable = ExtractCopyTable(line);
                currentCols  = ExtractCopyColumns(line);
                batch.Clear();
                continue;
            }

            // ── Fin du bloc COPY ──────────────────────────────────────────────
            if (line == "\\." && currentTable != null)
            {
                if (batch.Count > 0)
                    await FlushBatchAsync(conn, currentTable, currentCols, batch);
                rowsInserted += batch.Count;
                batch.Clear();
                currentTable = null;

                progress?.Report(new($"Table importée ({rowsInserted:N0} lignes total)",
                    bytesRead, totalBytes, tablesCreated, rowsInserted));
                continue;
            }

            // ── Ligne de données ──────────────────────────────────────────────
            if (currentTable != null && line.Length > 0)
            {
                batch.Add(ParseTsvLine(line));

                if (batch.Count >= BatchSize)
                {
                    await FlushBatchAsync(conn, currentTable, currentCols, batch);
                    rowsInserted += batch.Count;
                    batch.Clear();

                    if (rowsInserted % 50000 == 0)
                        progress?.Report(new($"Import {currentTable}…",
                            bytesRead, totalBytes, tablesCreated, rowsInserted));
                }
            }
        }
    }

    // ─── Flush d'un batch ─────────────────────────────────────────────────────

    private static async Task FlushBatchAsync(
        SqliteConnection conn,
        string           table,
        string[]         cols,
        List<string[]>   batch)
    {
        if (batch.Count == 0 || cols.Length == 0) return;

        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var colList    = string.Join(",", cols.Select(c => $"\"{c}\""));
            var paramList  = string.Join(",", cols.Select((_, i) => $"@p{i}"));
            var sql        = $"INSERT OR IGNORE INTO \"{table}\" ({colList}) VALUES ({paramList});";

            foreach (var row in batch)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction  = (SqliteTransaction)tx;
                cmd.CommandText  = sql;
                for (int i = 0; i < cols.Length; i++)
                {
                    var val = i < row.Length ? row[i] : null;
                    cmd.Parameters.AddWithValue($"@p{i}",
                        val == "\\N" || val == null ? DBNull.Value : (object)val);
                }
                try { await cmd.ExecuteNonQueryAsync(); }
                catch { /* ignorer les erreurs de ligne */ }
            }
            await tx.CommitAsync();
        }
        catch { await tx.RollbackAsync(); }
    }

    // ─── Helpers SQL ──────────────────────────────────────────────────────────

    private static string? BuildCreateTable(string tableName, string body)
    {
        try
        {
            var cols = new List<string>();
            foreach (var rawLine in body.Split('\n'))
            {
                var l = rawLine.Trim().TrimEnd(',');
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (l.StartsWith("--")) continue;
                if (l.StartsWith("CONSTRAINT") || l.StartsWith("PRIMARY KEY") ||
                    l.StartsWith("UNIQUE") || l.StartsWith("CHECK") ||
                    l.StartsWith("FOREIGN KEY") || l.StartsWith(")")) continue;

                // Extraire nom + type simplifié
                var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var colName = parts[0].Trim('"');
                var pgType  = parts[1].ToUpperInvariant();
                var sqlType = pgType switch
                {
                    "INTEGER" or "INT" or "BIGINT" or "SMALLINT" or "SERIAL" or
                    "BIGSERIAL" or "BOOLEAN" or "BOOL"     => "INTEGER",
                    "REAL" or "FLOAT" or "DOUBLE" or
                    "NUMERIC" or "DECIMAL"                  => "REAL",
                    _                                       => "TEXT",
                };

                var notNull = l.Contains("NOT NULL") ? " NOT NULL" : "";
                cols.Add($"    \"{colName}\" {sqlType}{notNull}");
            }

            if (cols.Count == 0) return null;
            return $"CREATE TABLE IF NOT EXISTS \"{tableName}\" (\n{string.Join(",\n", cols)}\n);";
        }
        catch { return null; }
    }

    // ─── Parsing TSV (format COPY Postgres) ──────────────────────────────────

    private static string[] ParseTsvLine(string line)
    {
        var fields = new List<string>();
        var sb     = new StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\t')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else if (line[i] == '\\' && i + 1 < line.Length)
            {
                i++;
                fields.Add(sb.Append(line[i] switch
                {
                    'n'  => '\n', 'r' => '\r', 't' => '\t',
                    '\\' => '\\', _   => line[i],
                }).ToString());
                sb.Clear();
                fields.RemoveAt(fields.Count - 1);
                sb.Append(line[i] switch
                {
                    'n'  => '\n', 'r' => '\r', 't' => '\t',
                    '\\' => '\\', _   => line[i],
                });
            }
            else sb.Append(line[i]);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    private static string? ExtractTableName(string line)
    {
        // CREATE TABLE [IF NOT EXISTS] [public.]table_name (
        var m = Regex.Match(line,
            @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?""?(\w+)""?\s*\(",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ExtractCopyTable(string line)
    {
        var m = Regex.Match(line,
            @"COPY\s+(?:public\.)?""?(\w+)""?\s*\(",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string[] ExtractCopyColumns(string line)
    {
        var m = Regex.Match(line, @"\(([^)]+)\)");
        if (!m.Success) return [];
        return m.Groups[1].Value
            .Split(',')
            .Select(c => c.Trim().Trim('"'))
            .ToArray();
    }

    private static string FormatBytes(long b) => b switch
    {
        < 1024        => $"{b} o",
        < 1024 * 1024 => $"{b / 1024} Ko",
        _             => $"{b / (1024.0 * 1024):F1} Mo",
    };

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

// ─── CountingStream ────────────────────────────────────────────────────────────

internal class RawCountingStream(Stream inner, Action<long> onRead) : Stream
{
    private long _total;
    public override bool  CanRead  => inner.CanRead;
    public override bool  CanSeek  => false;
    public override bool  CanWrite => false;
    public override long  Length   => inner.Length;
    public override long  Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void  Flush()  => inner.Flush();
    public override long  Seek(long o, SeekOrigin r) => throw new NotSupportedException();
    public override void  SetLength(long v)           => throw new NotSupportedException();
    public override void  Write(byte[] b, int o, int c) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        _total += n; onRead(_total); return n;
    }
    public override async Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct)
    {
        var n = await inner.ReadAsync(b, o, c, ct);
        _total += n; onRead(_total); return n;
    }
}
