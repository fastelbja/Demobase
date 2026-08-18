using DemoBase.Data.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Text;

namespace DemoBase.Import;

public class DemozooImportService
{
    private const string DumpUrl = "https://data.demozoo.org/demozoo-export.sql.gz";

    // ─── Mapping table Postgres → SQLite ─────────────────────────────────────
    // Noms SANS schéma (on retire "public." dans ExtractTableName)
    private static readonly Dictionary<string, string> TableMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["productions_productiontype"]          = "ReleaseTypes",
        ["platforms_platform"]                  = "Platforms",
        ["demoscene_releaser"]                  = "Releasers",
        ["demoscene_nick"]                      = "Nicks",
        ["demoscene_membership"]                = "ReleaserMemberships",
        ["productions_production"]              = "Releases",
        ["productions_production_platforms"]    = "ReleasePlatforms",
        ["productions_production_author_nicks"] = "ReleaseAuthors",
        ["productions_production_types"]        = "_ReleaseTypeLinks",
        ["productions_soundtracklink"]          = "ReleaseSoundtracks",
        ["productions_credit"]                  = "ReleaseCredits",
        ["productions_productionlink"]          = "ReleaseLinks",
        ["parties_partyseries"]                 = "PartySeries",
        ["parties_party"]                       = "Parties",
        ["parties_competition"]                 = "Competitions",
        ["parties_competitionplacing"]          = "CompetitionPlacings",
        ["productions_screenshot"]              = "MediaFiles",
    };

    // ─── Mapping colonnes Postgres → SQLite ──────────────────────────────────
    private static readonly Dictionary<string, (string pg, string sqlite)[]> ColMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["productions_productiontype"] =
        [
            ("id",        "Id"),
            ("name",      "Name"),
            ("supertype", "Supertype"),
        ],
        ["platforms_platform"] =
        [
            ("id",   "Id"),
            ("name", "Name"),
        ],
        ["demoscene_releaser"] =
        [
            ("id",           "Id"),
            ("name",         "Name"),
            ("is_group",     "IsGroup"),
            ("abbreviation", "Abbreviation"),
            ("country_code", "Country"),
            ("notes",        "Notes"),
            ("website",      "Website"),
            ("differentiator", "Differentiator"),
            ("first_name",     "FirstName"),
            ("surname",        "SurName"),
            ("location",       "Location"),
        ],
        ["demoscene_nick"] =
        [
            ("id",           "Id"),
            ("releaser_id",  "ReleaserId"),
            ("name",         "Name"),
            ("abbreviation", "Abbreviation"),
        ],
        ["demoscene_membership"] =
        [
            ("member_id",  "ScenerId"),
            ("group_id",   "GroupId"),
            ("is_current", "IsCurrentMember"),
        ],
        ["productions_production"] =
        [
            ("id",                     "Id"),
            ("title",                  "Title"),
            ("supertype",              "Supertype"),
            ("release_date_date",      "ReleaseDate"),
            ("release_date_precision", "ReleaseDatePrecision"),
            ("notes",                  "Notes"),
        ],

        // Table de liaison M:N entre Productions et Types
        // On prend le premier type trouvé comme ReleaseTypeId
        ["productions_production_types"] =
        [
            ("production_id",      "ReleaseId"),
            ("productiontype_id",  "ReleaseTypeId"),
        ],

        ["productions_production_platforms"] =
        [
            ("production_id", "ReleaseId"),
            ("platform_id",   "PlatformId"),
        ],
        ["productions_production_author_nicks"] =
        [
            ("production_id", "ReleaseId"),
            ("nick_id",       "NickId"),
        ],
        // ReleaseCredits : nick_id pointe vers Nicks, pas vers Releasers directement.
        // Dans le dump Demozoo : "category" = rôle (code/graphics/music…), "description" = détail libre
        ["productions_credit"] =
        [
            ("production_id", "ReleaseId"),
            ("nick_id",       "ReleaserId"),
            ("category",      "Role"),
            ("role",          "Detail"),     // précision libre du rôle (ex: "additonal design")
        ],
        ["productions_soundtracklink"] =
        [
            ("id",            "Id"),
            ("production_id", "ReleaseId"),
            ("soundtrack_id", "SoundtrackId"),
        ],
        ["productions_productionlink"] =
        [
            ("id",              "Id"),
            ("production_id",   "ReleaseId"),
            ("url",             "Url"),
            ("is_download_link","IsMainFile"),
            ("link_class",      "LinkClass"),
            ("parameter",       "LinkParameter"),
        ],
        ["parties_partyseries"] =
        [
            ("id",      "Id"),
            ("name",    "Name"),
            ("website", "Website"),
        ],
        ["parties_party"] =
        [
            ("id",              "Id"),
            ("name",            "Name"),
            ("tagline",         "Tagline"),
            ("party_series_id", "PartySeriesId"),
            ("start_date_date", "StartDate"),
            ("end_date_date",   "EndDate"),
            ("location",        "Location"),
            ("country_code",    "CountryCode"),
            ("is_online",       "IsOnline"),
            ("website",         "Website"),
        ],
        ["parties_competition"] =
        [
            ("id",       "Id"),
            ("party_id", "PartyId"),
            ("name",     "Name"),
        ],
        ["parties_competitionplacing"] =
        [
            ("id",             "Id"),
            ("competition_id", "CompetitionId"),
            ("production_id",  "ReleaseId"),
            ("ranking",        "Ranking"),
            ("score",          "Score"),
        ],
        ["productions_screenshot"] =
        [
            ("id",            "Id"),
            ("production_id", "ReleaseId"),
            ("standard_url",  "FilePath"),
        ],
    };

    private readonly IDbContextFactory<DemoBaseDbContext> _ctxFactory;
    private readonly DemozooVersionService _versionService;
    private string _language = "fr";

    public DemozooImportService(
        IDbContextFactory<DemoBaseDbContext> ctxFactory,
        DemozooVersionService versionService)
    {
        _ctxFactory     = ctxFactory;
        _versionService = versionService;
    }

    /// <summary>Définit la langue pour la traduction des types de release.</summary>
    public void SetLanguage(string language) => _language = language;

    /// <summary>True si le catalogue Demozoo a déjà été importé (table Releases
    /// non vide) — utilisé par le wizard pour détecter, sur une réouverture,
    /// que cette étape a déjà réussi et ne doit pas être refaite.</summary>
    public Task<bool> HasExistingDataAsync() => _versionService.HasReleasesAsync();

    // ─── Point d'entrée ───────────────────────────────────────────────────────

    public async Task ImportAsync(
        IProgress<DemozooImportProgress> progress,
        CancellationToken ct = default)
    {
        // 2026-07-25 (correctif "database is locked") : on ne récupère QUE la
        // chaîne de connexion via ce DbContext, jamais utilisé pour autre chose —
        // il est donc créé puis immédiatement libéré (bloc dédié) plutôt que
        // gardé ouvert (via "await using" sur toute la méthode) pendant les
        // 30-60 minutes que peut durer l'import. Avant ce correctif, ce
        // DbContext restait ouvert en parallèle de la connexion SQLite brute
        // utilisée plus bas ET de toutes les connexions (pool Microsoft.Data.
        // Sqlite) ouvertes par le reste de l'appli en cours d'exécution — sans
        // impact quand l'import tourne au premier lancement (wizard, avant que
        // l'UI principale n'ouvre la moindre connexion), mais problématique
        // quand il est déclenché via le bouton "mise à jour Demozoo" pendant
        // que l'appli tourne déjà (sidebar, listes, fiche release… interrogent
        // la base en continu). Voir aussi les PRAGMA busy_timeout/journal_mode
        // ci-dessous pour le reste du correctif.
        string connStr;
        await using (var ctx = await _ctxFactory.CreateDbContextAsync(ct))
        {
            connStr = ctx.Database.GetConnectionString()
                ?? throw new InvalidOperationException("Connexion SQLite introuvable.");
        }

        progress.Report(new("Connecting to data.demozoo.org…", 0, 0, Phase.Download));

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "DemoBase/1.0");
        http.Timeout = TimeSpan.FromHours(2);

        using var response = await http.GetAsync(DumpUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes   = response.Content.Headers.ContentLength ?? 0;
        var lastModified = response.Content.Headers.LastModified?.UtcDateTime
                        ?? response.Headers.Date?.UtcDateTime;

        await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
        var countingStream = new CountingStream(networkStream, bytes =>
            progress.Report(new(
                $"Downloading… {FormatBytes(bytes)} / {FormatBytes(totalBytes)}",
                bytes, totalBytes, Phase.Download)));

        await using var gzip  = new GZipStream(countingStream, CompressionMode.Decompress);
        using  var reader     = new StreamReader(gzip, Encoding.UTF8, bufferSize: 256 * 1024);

        await using var sqlite = new SqliteConnection(connStr);
        await sqlite.OpenAsync(ct);

        // 2026-07-25 (correctif "SQLite Error 5: database is locked") :
        //  - busy_timeout : par défaut SQLite lève IMMÉDIATEMENT "database is
        //    locked" (code 5) dès qu'une autre connexion tient un verrou, au
        //    lieu d'attendre/réessayer. Le reste de l'appli (sidebar, listes,
        //    fiche release…) garde en permanence des connexions SQLite
        //    ouvertes/poolées (Microsoft.Data.Sqlite pool les connexions par
        //    défaut, même après leur Dispose()) dès qu'on lance cet import
        //    depuis le bouton "mise à jour" pendant que l'appli tourne — ce qui
        //    n'arrivait jamais au premier lancement (wizard, avant que l'UI
        //    principale n'ouvre la moindre connexion), d'où le "ça marchait
        //    avant". On laisse SQLite patienter jusqu'à 30 s avant d'abandonner.
        //  - journal_mode : on ne bascule PLUS en MEMORY puis WAL. Changer de
        //    journal_mode est une opération GLOBALE au fichier qui exige
        //    qu'aucune autre connexion n'ait la base ouverte (cf. doc SQLite) —
        //    exactement ce qui ne peut pas être garanti ici. La base reste déjà
        //    en WAL (mode par défaut posé par DbInitializer), qui autorise déjà
        //    un writer + des readers concurrents sans se verrouiller mutuellement
        //    et convient très bien à l'import par lots (transactions déjà
        //    utilisées dans ParseAndInsertAsync) — on garde juste synchronous=OFF
        //    / temp_store=MEMORY / un grand cache, qui eux sont sans risque.
        await ExecAsync(sqlite, "PRAGMA busy_timeout=30000;");
        await ExecAsync(sqlite, "PRAGMA synchronous=OFF;");
        await ExecAsync(sqlite, "PRAGMA temp_store=MEMORY;");
        await ExecAsync(sqlite, "PRAGMA cache_size=-65536;"); // 64 MB cache
        await ExecAsync(sqlite, "PRAGMA foreign_keys=OFF;");

        await ParseAndInsertAsync(reader, sqlite, progress, ct);

        await ExecAsync(sqlite, "PRAGMA foreign_keys=ON;");
        await ExecAsync(sqlite, "PRAGMA synchronous=NORMAL;");

        // ── Étapes de finalisation numérotées ────────────────────────────────
        const int FinalSteps = 5;

        void ReportStep(string msg, int step) =>
            progress.Report(new(msg, step, FinalSteps, Phase.Finalize));

        // Étape 1 : ANALYZE
        ReportStep("Optimizing database (ANALYZE)…", 1);
        await Task.Yield(); // laisse le dispatcher afficher le message
        await ExecAsync(sqlite, "ANALYZE;");

        // Étape 2 : DemozooId
        ReportStep("Fixing DemozooId…", 2);
        await Task.Yield();
        await ExecAsync(sqlite,
            "UPDATE \"Releases\" SET \"DemozooId\" = \"Id\" WHERE \"DemozooId\" IS NULL;");

        // Étape 3 : ReleaseTypeId
        ReportStep("Linking release types…", 3);
        await Task.Yield();
        await ExecAsync(sqlite, """
            UPDATE "Releases"
            SET "ReleaseTypeId" = (
                SELECT "ReleaseTypeId"
                FROM "_ReleaseTypeLinks"
                WHERE "_ReleaseTypeLinks"."ReleaseId" = "Releases"."Id"
                LIMIT 1
            )
            WHERE "ReleaseTypeId" IS NULL;
            """);

        // Vérification diagnostique
        await using var diagCmd = sqlite.CreateCommand();
        diagCmd.CommandText = "SELECT COUNT(*) FROM \"Releases\" WHERE \"ReleaseTypeId\" IS NOT NULL;";
        var countWithType = await diagCmd.ExecuteScalarAsync();
        System.Diagnostics.Debug.WriteLine($"[DIAG] Releases avec ReleaseTypeId: {countWithType}");

        diagCmd.CommandText = "SELECT COUNT(*) FROM \"ReleaseCredits\" WHERE \"Role\" != '' AND \"Role\" IS NOT NULL;";
        var countWithRole = await diagCmd.ExecuteScalarAsync();
        System.Diagnostics.Debug.WriteLine($"[DIAG] Credits avec Role: {countWithRole}");

        // Étape 4 : URLs YouTube / Vimeo
        ReportStep("Building video URLs (YouTube / Vimeo)…", 4);
        await Task.Yield();
        await ExecAsync(sqlite, """
            UPDATE "ReleaseLinks"
            SET "Url" = 'https://www.youtube.com/watch?v=' || "LinkParameter"
            WHERE "LinkClass" = 'YoutubeVideo'
              AND "LinkParameter" IS NOT NULL
              AND "LinkParameter" != '';
            """);
        await ExecAsync(sqlite, """
            UPDATE "ReleaseLinks"
            SET "Url" = 'https://vimeo.com/' || "LinkParameter"
            WHERE "LinkClass" = 'VimeoVideo'
              AND "LinkParameter" IS NOT NULL
              AND "LinkParameter" != '';
            """);

        // 2026-07-25 (retour utilisateur : "Return to Promised Land", Demozoo #394835) :
        // PAS de backfill "Url" pour la classe "BaseUrl" (contrairement à YoutubeVideo/
        // VimeoVideo ci-dessus, qui restent inchangés) — décision explicite de l'utilisateur
        // suite à un DbUpdateException rencontré en testant ce backfill. "Url" reste une
        // copie fidèle du champ Postgres "url" de Demozoo tel quel (souvent NULL pour cette
        // classe), sans écriture dérivée en base. La reconstruction de l'URL de
        // téléchargement à partir de "LinkClass"/"LinkParameter" se fait UNIQUEMENT à la
        // volée, côté code, via ReleaseLink.EffectiveDownloadUrl (DemoBase.Core/Models/
        // Models.cs) — c'est ce mécanisme, et lui seul, qui doit être étendu si d'autres
        // classes de lien nécessitent le même traitement à l'avenir.
        // Étape 5 : traduction des types de release selon la langue courante
        ReportStep("Translating release types…", 5);
        await Task.Yield();
        await DemoBase.Data.ReleaseTypeTranslationService.ApplyAsync(sqlite, _language, ct);

        // Étape 6 : sauvegarde de la version
        ReportStep("Saving version…", 6);
        await Task.Yield();
        await _versionService.SaveVersionAsync(lastModified, totalBytes > 0 ? totalBytes : null);

        progress.Report(new("Import complete!", 1, 1, Phase.Done));
    }

    // ─── Parser principal ─────────────────────────────────────────────────────

    private static async Task ParseAndInsertAsync(
        StreamReader reader,
        SqliteConnection sqlite,
        IProgress<DemozooImportProgress> progress,
        CancellationToken ct)
    {
        string?  currentPgTable   = null;
        string?  currentDestTable = null;
        int[]?   pgIndexes        = null;
        string[]? sqliteCols      = null;

        SqliteCommand?     insertCmd = null;
        SqliteTransaction? tx        = null;

        long rowsTotal  = 0;
        long rowsTable  = 0;
        const int BatchSize = 5000;

        // Throttle des reports UI : au max toutes les 300 ms
        var lastReport = DateTime.UtcNow;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (ct.IsCancellationRequested) break;

            // ── Début d'un bloc COPY ──────────────────────────────────────
            if (line.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase))
            {
                // Commit le batch en cours
                if (tx != null) { await tx.CommitAsync(ct); tx.Dispose(); tx = null; }
                insertCmd?.Dispose(); insertCmd = null;

                var pgTable = ExtractTableName(line);
                var pgCols  = ExtractColumns(line);

                // Debug : log toutes les tables trouvées dans le dump
                System.Diagnostics.Debug.WriteLine($"[COPY] {pgTable} ({string.Join(", ", pgCols.Take(5))}{(pgCols.Length > 5 ? "…" : "")})");

                if (TableMap.TryGetValue(pgTable, out var dest)
                    && ColMap.TryGetValue(pgTable, out var colPairs))
                {
                    // Construire le mapping index : pour chaque colonne voulue,
                    // trouver son index dans le COPY Postgres (case-insensitive)
                    var pgColsLower = pgCols.Select(c => c.ToLowerInvariant()).ToArray();

                    var valid = colPairs
                        .Select(p =>
                        {
                            var idx = Array.IndexOf(pgColsLower, p.pg.ToLowerInvariant());
                            return (idx, p.sqlite);
                        })
                        .Where(x => x.idx >= 0)
                        .ToArray();

                    if (valid.Length == 0)
                    {
                        // Aucune colonne trouvée — log et skip
                        System.Diagnostics.Debug.WriteLine(
                            $"[SKIP] {pgTable} : aucune colonne correspondante. " +
                            $"Colonnes COPY : {string.Join(", ", pgCols)}");
                        currentPgTable = null;
                        continue;
                    }

                    pgIndexes  = valid.Select(x => x.idx).ToArray();
                    sqliteCols = valid.Select(x => x.sqlite).ToArray();
                    currentPgTable   = pgTable;
                    currentDestTable = dest;
                    rowsTable = 0;

                    tx        = await sqlite.BeginTransactionAsync(ct) as SqliteTransaction;
                    insertCmd = BuildInsertCommand(sqlite, dest, sqliteCols);
                    insertCmd.Transaction = tx;

                    progress.Report(new($"Importing: {dest}…", rowsTotal, 0, Phase.Parse));
                    System.Diagnostics.Debug.WriteLine($"[OK]   {pgTable} → {dest} ({valid.Length} cols: {string.Join(", ", sqliteCols)})");

                    // Affichage visible des colonnes pour les tables critiques
                    if (pgTable is "productions_production" or "productions_credit" or "productions_production_types")
                    {
                        var mapped   = string.Join(", ", sqliteCols);
                        var unmapped = colPairs
                            .Where(p => Array.IndexOf(pgCols, p.pg.ToLowerInvariant()) < 0)
                            .Select(p => p.pg);
                        var msg = $"{pgTable} → [{mapped}]";
                        if (unmapped.Any())
                            msg += $" | NON TROUVÉES: [{string.Join(", ", unmapped)}]";
                        progress.Report(new(msg, rowsTotal, 0, Phase.Parse));
                    }
                }
                else
                {
                    currentPgTable = null;
                    System.Diagnostics.Debug.WriteLine($"[IGN]  {pgTable}");
                }
                continue;
            }

            // ── Fin du bloc COPY ──────────────────────────────────────────
            if (line == "\\.")
            {
                if (currentPgTable != null && tx != null)
                {
                    await tx.CommitAsync(ct);
                    tx.Dispose(); tx = null;
                    progress.Report(new(
                        $"{currentDestTable} : {rowsTable:N0} lignes",
                        rowsTotal, 0, Phase.Parse));
                    System.Diagnostics.Debug.WriteLine($"[DONE] {currentDestTable} : {rowsTable:N0} lignes");
                }
                currentPgTable = null;
                continue;
            }

            // ── Données ───────────────────────────────────────────────────
            if (currentPgTable is null || insertCmd is null
                || pgIndexes is null || sqliteCols is null) continue;

            try
            {
                var fields = SplitCopyRow(line);

                for (int i = 0; i < pgIndexes.Length; i++)
                {
                    var raw = pgIndexes[i] < fields.Length ? fields[pgIndexes[i]] : null;
                    insertCmd.Parameters[i].Value = ParseValue(raw, sqliteCols[i])
                                                   ?? (object)DBNull.Value;
                }

                await insertCmd.ExecuteNonQueryAsync(ct);
                rowsTable++;
                rowsTotal++;

                // Batch commit
                if (rowsTable % BatchSize == 0)
                {
                    await tx!.CommitAsync(ct);
                    tx.Dispose();
                    tx = await sqlite.BeginTransactionAsync(ct) as SqliteTransaction;
                    insertCmd.Transaction = tx;

                    // Throttle : report UI max toutes les 300 ms
                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds >= 300)
                    {
                        progress.Report(new(
                            $"{currentDestTable} : {rowsTable:N0} lignes…",
                            rowsTotal, 0, Phase.Parse));
                        lastReport = now;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERR] {currentPgTable}: {ex.Message} | line: {line[..Math.Min(80, line.Length)]}");
            }
        }

        if (tx != null) { await tx.CommitAsync(ct); tx.Dispose(); }
        insertCmd?.Dispose();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Extrait le nom de table depuis "COPY [public.]table_name (…) FROM stdin;"
    /// Gère les variantes : avec ou sans schéma, avec ou sans guillemets.
    /// </summary>
    private static string ExtractTableName(string line)
    {
        // "COPY " → chercher jusqu'au prochain espace ou '('
        var start = 5; // après "COPY "
        // Skip les espaces éventuels
        while (start < line.Length && line[start] == ' ') start++;

        var end = start;
        while (end < line.Length && line[end] != ' ' && line[end] != '(') end++;

        var full = line[start..end].Trim().Trim('"');

        // Retire le préfixe de schéma (ex: "public.")
        var dot = full.LastIndexOf('.');
        var table = dot >= 0 ? full[(dot + 1)..].Trim('"') : full;

        return table.ToLowerInvariant();
    }

    private static string[] ExtractColumns(string line)
    {
        var open  = line.IndexOf('(');
        var close = line.LastIndexOf(')');
        if (open < 0 || close < 0 || close <= open) return [];
        return line[(open + 1)..close]
            .Split(',')
            .Select(c => c.Trim().Trim('"').ToLowerInvariant())
            .ToArray();
    }

    private static string?[] SplitCopyRow(string line)
    {
        var fields = line.Split('\t');
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i] == "\\N")
                fields[i] = null!;
            else
                fields[i] = fields[i]
                    .Replace("\\t",  "\t")
                    .Replace("\\n",  "\n")
                    .Replace("\\r",  "\r")
                    .Replace("\\\\", "\\");
        }
        return fields!;
    }

    private static object? ParseValue(string? raw, string colName)
    {
        if (raw is null) return null;

        // Booléens PostgreSQL → entiers SQLite
        if (raw == "t") return 1;
        if (raw == "f") return 0;

        // Screenshots : type fixé
        if (colName == "Type") return "Screenshot";

        // Dates PostgreSQL (YYYY-MM-DD) → on garde tel quel en string
        // Dates vides → null
        if ((colName == "ReleaseDate" || colName == "StartDate" || colName == "EndDate")
            && string.IsNullOrWhiteSpace(raw))
            return null;

        return raw;
    }

    private static SqliteCommand BuildInsertCommand(
        SqliteConnection conn, string table, string[] cols)
    {
        var colList   = string.Join(", ", cols.Select(c => $"\"{c}\""));
        var paramList = string.Join(", ", cols.Select((_, i) => $"@p{i}"));
        var sql       = $"INSERT OR IGNORE INTO \"{table}\" ({colList}) VALUES ({paramList})";

        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < cols.Length; i++)
            cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));
        cmd.Prepare();
        return cmd;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1024              => $"{bytes} B",
            < 1024 * 1024       => $"{bytes / 1024:N0} KB",
            < 1024L * 1024*1024 => $"{bytes / (1024 * 1024):N1} MB",
            _                   => $"{bytes / (1024.0 * 1024 * 1024):N2} GB"
        };
}

// ─── Progress types ───────────────────────────────────────────────────────────

public enum Phase { Download, Parse, Finalize, Done }

public record DemozooImportProgress(
    string Message, long Current, long Total, Phase Phase)
{
    public double Percent => Total > 0 ? (double)Current / Total * 100 : 0;
}

// ─── CountingStream ───────────────────────────────────────────────────────────

internal class CountingStream : Stream
{
    private readonly Stream       _inner;
    private readonly Action<long> _onProgress;
    private long _read, _lastReport;
    private const long ReportEvery = 1024 * 1024; // toutes les 1 MB

    public CountingStream(Stream inner, Action<long> onProgress)
    { _inner = inner; _onProgress = onProgress; }

    public override bool CanRead  => _inner.CanRead;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    { var n = _inner.Read(buffer, offset, count); Tick(n); return n; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    { var n = await _inner.ReadAsync(buffer, ct); Tick(n); return n; }

    private void Tick(int n)
    {
        _read += n;
        if (_read - _lastReport >= ReportEvery) { _lastReport = _read; _onProgress(_read); }
    }

    public override void Flush()                              => _inner.Flush();
    public override long Seek(long o, SeekOrigin s)           => throw new NotSupportedException();
    public override void SetLength(long v)                    => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c)        => throw new NotSupportedException();
}
