using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

public record DatImportProgress(
    string CurrentFile,
    int    FilesProcessed,
    int    FilesTotal,
    int    EntriesImported,
    bool   IsComplete = false);

/// <summary>
/// Ligne plate d'index DatRom, utilisée par RomScanService ("Scan ROMs", 2026-07-27,
/// demande utilisateur) pour construire un dictionnaire en mémoire (clé Size, comparaison
/// CRC32 ensuite) et faire correspondre des fichiers isolés de l'utilisateur (ex. plusieurs
/// .dsk Amstrad CPC récupérés au fil du temps) au catalogue DATs, sans une requête SQL par
/// fichier scanné.
/// </summary>
public record DatRomIndexEntry(
    int     DatEntryId,
    int     DemozooId,
    string  RomPath,
    string  SourceFile,
    string  Name,
    long    Size,
    string? Crc32);

public class DatImportService
{
    private readonly string _connectionString;
    private const    int    BatchSize = 500;
    private const    string DatsDir   = "DATS";

    // Regex compilées — performances optimales sur 351k machines
    private static readonly Regex RxVersion  = new(@"<version>\s*([^<]+?)\s*</version>",  RegexOptions.Compiled);
    private static readonly Regex RxMachine  = new(@"<machine\b[^>]*>(.*?)</machine>",    RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex RxDemozoo  = new(@"<DemozooID>\s*(\d+)\s*</DemozooID>", RegexOptions.Compiled);
    private static readonly Regex RxDesc     = new(@"<description>\s*(.*?)\s*</description>", RegexOptions.Compiled);
    private static readonly Regex RxRom      = new(@"<rom\s+([^/]+)/>",                   RegexOptions.Compiled);
    private static readonly Regex RxAttr     = new(@"(\w+)=""([^""]*)""",                 RegexOptions.Compiled);

    public DatImportService(string connectionString)
        => _connectionString = connectionString;

    // ─── Premier démarrage : tables vides ? ─────────────────────────────────────

    public async Task<bool> IsFirstRunAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT COUNT(*) FROM "DatEntries" LIMIT 1;""";
        var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        return count == 0;
    }

    // ─── Vérifie si un import est nécessaire ─────────────────────────────────

    public async Task<bool> NeedsImportAsync()
    {
        var files = GetAllXmlFiles();
        if (files.Count == 0) return false;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        foreach (var file in files)
        {
            var relPath   = GetRelPath(file);
            var version   = ReadVersion(file);
            var dbVersion = await GetVersionAsync(conn, relPath);
            if (dbVersion != version) return true;
        }
        return false;
    }

    // ─── Index complet des DatRoms (pour "Scan ROMs", 2026-07-27) ──────────────

    /// <summary>
    /// Charge en une seule requête tous les DatRom du catalogue (avec CRC32 renseigné —
    /// rien à comparer sinon) accompagnés des infos de leur DatEntry parent. Les DatEntry
    /// "Code Sources" (même règle que DatEntry.IsCodeSourceEntry côté Core : SourceFile
    /// contient "Sources Code") sont exclus — pas des fichiers "roms" à faire correspondre.
    /// Catalogue potentiellement volumineux (plusieurs centaines de milliers de lignes, cf.
    /// commentaire sur RxVersion plus haut — "351k machines") : requête unique plutôt qu'une
    /// requête par fichier scanné, à charge de l'appelant de construire un index en mémoire
    /// (dictionnaire par taille, typiquement) adapté à son usage.
    /// </summary>
    public async Task<List<DatRomIndexEntry>> GetAllRomsIndexAsync(CancellationToken ct = default)
    {
        var result = new List<DatRomIndexEntry>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e."Id", e."DemozooId", e."RomPath", e."SourceFile",
                   r."Name", r."Size", r."Crc32"
            FROM "DatRoms" r
            JOIN "DatEntries" e ON e."Id" = r."DatEntryId"
            WHERE r."Crc32" IS NOT NULL AND r."Crc32" != '';
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sourceFile = reader.IsDBNull(3) ? "" : reader.GetString(3);
            if (sourceFile.Contains("Sources Code", StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new DatRomIndexEntry(
                DatEntryId: reader.GetInt32(0),
                DemozooId:  reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                RomPath:    reader.IsDBNull(2) ? "" : reader.GetString(2),
                SourceFile: sourceFile,
                Name:       reader.IsDBNull(4) ? "" : reader.GetString(4),
                Size:       reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                Crc32:      reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return result;
    }

    // ─── Import principal ─────────────────────────────────────────────────────

    public async Task ImportAsync(
        IProgress<DatImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var files   = GetAllXmlFiles();
        int total   = files.Count;
        int done    = 0;
        int entries = 0;

        // 2026-07-31, retour utilisateur ("l'import des fichiers DATs a doublé les
        // entrées ROMs !! il fallait faire un annule et remplace. pas un ajout") :
        // ProcessFileAsync ne fait qu'ADD/UPDATE — il supprime puis réimporte les
        // DatEntries d'un fichier XML donné, mais UNIQUEMENT si ce fichier est encore
        // présent dans ce lot d'import (comparaison par SourceFile == chemin relatif du
        // fichier). Si le catalogue DAT téléchargé change de structure d'un import à
        // l'autre (fichiers renommés/réorganisés — arrive, le zip "Demobase DATs" est
        // maintenu côté Mega hors de ce projet), les anciennes DatEntries dont le
        // SourceFile ne correspond plus à AUCUN fichier du nouveau lot ne sont jamais
        // supprimées : elles restent en base pour toujours, en plus des nouvelles —
        // même DemozooId, deux DatEntries (l'ancien ET le nouveau chemin), donc des
        // ROMs "doublées" à l'affichage d'une release. Ce chemin est sûr uniquement
        // parce que DATS/ contient TOUJOURS le catalogue complet au moment où
        // ImportAsync tourne (téléchargement + extraction intégrale du zip avant
        // import, DatsPage.xaml.cs comme DatsUpdateService.cs, jamais un sous-ensemble
        // partiel) — RemoveOrphanEntriesAsync peut donc se fier sans risque à
        // "SourceFile absent de ce lot" pour identifier un fichier réellement disparu.
        var currentRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relPath = GetRelPath(file);
            currentRelPaths.Add(relPath);
            progress?.Report(new(relPath, done, total, entries));

            try { entries += await ProcessFileAsync(file, relPath, ct); }
            catch { /* ignorer fichiers invalides */ }

            done++;
        }

        if (files.Count > 0)
        {
            await RemoveOrphanEntriesAsync(currentRelPaths, ct);
            await RemoveExactDuplicateEntriesAsync(ct);
        }

        progress?.Report(new("", total, total, entries, IsComplete: true));
    }

    // ─── Nettoyage des doublons exacts (même DemozooId + même RomPath) ────────

    /// <summary>
    /// Supprime les DatEntries strictement en double (même DemozooId + même RomPath) —
    /// symptôme concret visible par l'utilisateur ("l'import des fichiers DATs a doublé
    /// les entrées ROMs"). Conserve la plus ancienne occurrence (Id le plus petit),
    /// supprime les autres et leurs DatRoms.
    ///
    /// Contrairement à <see cref="RemoveOrphanEntriesAsync"/> (basée sur le lot de
    /// fichiers XML en cours d'import, ne s'exécute qu'à l'intérieur d'ImportAsync),
    /// cette méthode est indépendante de tout fichier DATS/ — appelée ici en filet de
    /// sécurité après chaque import, MAIS AUSSI appelable seule (cf. App.xaml.cs) pour
    /// réparer immédiatement une base déjà polluée par ce bug, sans attendre le
    /// prochain téléchargement DAT (dats_version.txt peut ne pas rechanger avant
    /// longtemps côté Mega).
    ///
    /// Safe : ne touche PAS aux DatEntry légitimement multiples pour un même
    /// DemozooId (ex. "Code Sources" séparé du fichier principal, TFMX mdat./smpl.
    /// répartis sur deux DatEntry) — leur RomPath diffère (dérivé de la description du
    /// DAT, distincte par construction). Seule une paire IDENTIQUE (DemozooId,
    /// RomPath) est un vrai doublon.
    /// </summary>
    public async Task<int> RemoveExactDuplicateEntriesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var toDelete = new List<long>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT "Id" FROM "DatEntries" e
                WHERE e."Id" NOT IN (
                    SELECT MIN("Id") FROM "DatEntries"
                    GROUP BY "DemozooId", "RomPath"
                );
                """;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                toDelete.Add(reader.GetInt64(0));
        }

        if (toDelete.Count == 0) return 0;

        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var id in toDelete)
        {
            ct.ThrowIfCancellationRequested();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                DELETE FROM "DatRoms" WHERE "DatEntryId"=@id;
                DELETE FROM "DatEntries" WHERE "Id"=@id;
                """;
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return toDelete.Count;
    }

    // ─── Nettoyage des entrées orphelines (fichiers DAT disparus/renommés) ─────

    private async Task RemoveOrphanEntriesAsync(HashSet<string> currentRelPaths, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var knownSourceFiles = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """SELECT DISTINCT "SourceFile" FROM "DatEntries";""";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                knownSourceFiles.Add(reader.GetString(0));
        }

        var orphans = knownSourceFiles.Where(sf => !currentRelPaths.Contains(sf)).ToList();
        if (orphans.Count == 0) return;

        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var sf in orphans)
        {
            ct.ThrowIfCancellationRequested();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            // DatRoms supprimé explicitement (pas seulement via le FK "ON DELETE
            // CASCADE" de DatRoms.DatEntryId) : ce PRAGMA est réglé PAR CONNEXION en
            // SQLite, et DatImportService ouvre ses propres connexions courtes
            // (indépendantes de DbInitializer, qui l'active pour LA SIENNE) — rien ne
            // garantit qu'il soit actif ici.
            cmd.CommandText = """
                DELETE FROM "DatRoms" WHERE "DatEntryId" IN
                    (SELECT "Id" FROM "DatEntries" WHERE "SourceFile"=@f);
                DELETE FROM "DatEntries" WHERE "SourceFile"=@f;
                DELETE FROM "DatFileVersions" WHERE "FileName"=@f;
                """;
            cmd.Parameters.AddWithValue("@f", sf);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    // ─── Traitement d'un fichier XML ──────────────────────────────────────────

    private async Task<int> ProcessFileAsync(string file, string relPath, CancellationToken ct)
    {
        var version = ReadVersion(file);
        if (version == null) return 0;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        if (await GetVersionAsync(conn, relPath) == version) return 0;

        // Supprimer les anciennes entrées de ce fichier source. DatRoms nettoyé
        // explicitement en plus du FK "ON DELETE CASCADE" (DatRoms.DatEntryId) — ce
        // dernier dépend de "PRAGMA foreign_keys=ON", réglé par connexion en SQLite et
        // non garanti sur cette connexion ouverte ici (cf. RemoveOrphanEntriesAsync).
        await ExecAsync(conn, """
            DELETE FROM "DatRoms" WHERE "DatEntryId" IN
                (SELECT "Id" FROM "DatEntries" WHERE "SourceFile"=@f);
            DELETE FROM "DatEntries" WHERE "SourceFile"=@f;
            """, ("@f", relPath));

        // Lire tout le fichier (les XML font max ~500KB)
        var text = await File.ReadAllTextAsync(file, ct);

        // Dossier du fichier = dossier des ROMs
        var romDir = Path.GetDirectoryName(relPath) ?? string.Empty;

        // Extraire toutes les machines avec un DemozooID
        var batch = new List<(int did, string romPath,
            List<(string n, long sz, string? cr, string? md, string? sh)> roms)>();
        int count = 0;

        foreach (Match mMachine in RxMachine.Matches(text))
        {
            ct.ThrowIfCancellationRequested();
            var body = mMachine.Groups[1].Value;

            var mDid  = RxDemozoo.Match(body);
            if (!mDid.Success) continue;
            if (!int.TryParse(mDid.Groups[1].Value.Trim(), out var did)) continue;

            var mDesc = RxDesc.Match(body);
            // Les DAT sont des fichiers XML : les regex ci-dessus capturent le texte brut
            // sans jamais le faire passer par un vrai parseur XML, donc les entités comme
            // "&amp;" (obligatoires en XML pour un "&" littéral, ex. "Ozone & MP") restent
            // telles quelles au lieu d'être décodées en "&". Sans ce décodage, un nom de ROM
            // se retrouve avec un ";" en plein milieu ("&amp;" se termine par ";") — inoffensif
            // partout, SAUF dans une Startup-Sequence Amiga, où ";" est un séparateur de
            // commandes Shell : AmigaDOS tronque alors la ligne et affiche "Unknown command"
            // au lieu de lancer le vrai exécutable. HtmlDecode couvre aussi &lt;/&gt;/&quot;/
            // &apos; et les entités numériques, au cas où d'autres DAT en contiennent.
            var desc  = mDesc.Success
                ? System.Net.WebUtility.HtmlDecode(mDesc.Groups[1].Value.Trim())
                : string.Empty;
            if (string.IsNullOrEmpty(desc)) continue;

            var romPath = Path.Combine(romDir, SanitizeFileName(desc) + ".zip");

            // Extraire les ROMs
            var roms = new List<(string, long, string?, string?, string?)>();
            foreach (Match mRom in RxRom.Matches(body))
            {
                var attrs = new Dictionary<string, string>();
                foreach (Match mAttr in RxAttr.Matches(mRom.Groups[1].Value))
                    attrs[mAttr.Groups[1].Value] = mAttr.Groups[2].Value;

                long.TryParse(attrs.GetValueOrDefault("size"), out var sz);
                roms.Add((
                    System.Net.WebUtility.HtmlDecode(attrs.GetValueOrDefault("name", "")),
                    sz,
                    attrs.GetValueOrDefault("crc"),
                    attrs.GetValueOrDefault("md5"),
                    attrs.GetValueOrDefault("sha1")));
            }

            batch.Add((did, romPath, roms));

            if (batch.Count >= BatchSize)
            {
                count += await FlushAsync(conn, relPath, batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            count += await FlushAsync(conn, relPath, batch);

        // Mettre à jour la version
        await ExecAsync(conn, """
            INSERT INTO "DatFileVersions"("FileName","Version")
            VALUES(@f,@v)
            ON CONFLICT("FileName") DO UPDATE SET "Version"=excluded."Version";
            """, ("@f", relPath), ("@v", version));

        return count;
    }

    // ─── Flush batch ──────────────────────────────────────────────────────────

    private static async Task<int> FlushAsync(
        SqliteConnection conn, string sourceFile,
        List<(int did, string romPath,
              List<(string n, long sz, string? cr, string? md, string? sh)> roms)> batch)
    {
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var (did, romPath, roms) in batch)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO "DatEntries"("DemozooId","RomPath","SourceFile")
                    VALUES(@did,@rp,@sf);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("@did", did);
                cmd.Parameters.AddWithValue("@rp",  romPath);
                cmd.Parameters.AddWithValue("@sf",  sourceFile);
                var eid = (long)(await cmd.ExecuteScalarAsync())!;

                foreach (var (rn, rsz, rcr, rmd, rsh) in roms)
                {
                    await using var rc = conn.CreateCommand();
                    rc.Transaction = (SqliteTransaction)tx;
                    rc.CommandText = """
                        INSERT INTO "DatRoms"("DatEntryId","Name","Size","Crc32","Md5","Sha1")
                        VALUES(@eid,@nm,@sz,@cr,@md,@sh);
                        """;
                    rc.Parameters.AddWithValue("@eid", eid);
                    rc.Parameters.AddWithValue("@nm",  rn);
                    rc.Parameters.AddWithValue("@sz",  rsz);
                    rc.Parameters.AddWithValue("@cr",  (object?)rcr ?? DBNull.Value);
                    rc.Parameters.AddWithValue("@md",  (object?)rmd ?? DBNull.Value);
                    rc.Parameters.AddWithValue("@sh",  (object?)rsh ?? DBNull.Value);
                    await rc.ExecuteNonQueryAsync();
                }
            }
            await tx.CommitAsync();
            return batch.Count;
        }
        catch { await tx.RollbackAsync(); throw; }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static List<string> GetAllXmlFiles()
    {
        var datsRoot = Path.Combine(AppContext.BaseDirectory, DatsDir);
        if (!Directory.Exists(datsRoot)) return new();
        return Directory.GetFiles(datsRoot, "*.xml", SearchOption.AllDirectories).ToList();
    }

    /// <summary>Remplace les caractères interdits dans un nom de fichier Windows.</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    private static string GetRelPath(string file)
    {
        var datsRoot = Path.Combine(AppContext.BaseDirectory, DatsDir);
        return Path.GetRelativePath(datsRoot, file);
    }

    private static string? ReadVersion(string file)
    {
        try
        {
            // Lire seulement les 2000 premiers octets pour trouver la version
            using var fs     = new FileStream(file, FileMode.Open, FileAccess.Read);
            var       buffer = new byte[2000];
            int       read   = fs.Read(buffer, 0, buffer.Length);
            var       head   = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            var       m      = RxVersion.Match(head);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    private static async Task<string?> GetVersionAsync(SqliteConnection conn, string relPath)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Version" FROM "DatFileVersions" WHERE "FileName"=@f LIMIT 1;""";
        cmd.Parameters.AddWithValue("@f", relPath);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql,
        params (string name, object? val)[] parms)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parms)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
