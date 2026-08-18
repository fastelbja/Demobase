using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>
/// Crée le schéma SQLite directement via SQL pur.
/// Contourne EF Core migrations et EnsureCreated (tous deux défaillants
/// avec les index filtrés et les relations complexes en SQLite).
/// Utiliser avant tout accès au DbContext.
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// Crée les dossiers principaux de DemoBase.
    /// Appelé par le wizard de configuration lors du premier lancement.
    /// (Plus appelé automatiquement au démarrage — seul Database/ l'est, car
    ///  requis pour SQLite avant tout autre accès.)
    /// </summary>
    public static void EnsureDirectories()
    {
        var root = AppContext.BaseDirectory;
        foreach (var dir in new[]
        {
            Path.Combine(root, "BIOS"),
            Path.Combine(root, "Configs"),
            Path.Combine(root, "Database"),
            Path.Combine(root, "Releases"),
            Path.Combine(root, "Working"),
            Path.Combine(root, "Emus"),
        })
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Détecte si <paramref name="path"/> pointe vers un emplacement réseau : un
    /// chemin UNC direct (\\serveur\partage\...) ou une lettre de lecteur mappée
    /// sur un partage réseau (ex. Z: → \\serveur\partage). Utilisé pour éviter le
    /// mode WAL de SQLite (cf. InitializeAsync ci-dessous), non fiable sur SMB/NFS.
    /// Best-effort : en cas de doute (chemin invalide, lecteur inaccessible...), on
    /// répond "non réseau" plutôt que de risquer un faux positif qui changerait le
    /// comportement historique (WAL) pour un usage purement local.
    ///
    /// 2026-08-07, retour utilisateur (deux rapports distincts : hang silencieux
    /// "loading circle ... then nothing" et crash APPCRASH/KERNELBASE avec une
    /// boucle procmon dans C:\Windows\CSC\...\namespace\&lt;IP du serveur SMB&gt;,
    /// tous deux en lançant l'app depuis un partage SMB) : passée de `internal` à
    /// `public` pour que <c>DemoBase.Import.DemozooRawExportService</c> puisse
    /// aussi s'en servir — cf. son commentaire pour le second cas concret trouvé
    /// (journal_mode=WAL non protégé sur demozoo_raw.db, contrairement à
    /// demobase.db ci-dessous, protégé depuis le 2026-08-02).
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return true; // UNC direct : \\serveur\partage\...

            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;

            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Vide le cache d'extraction ZIP utilisé par WinUAELauncher/AltirraLauncher/
    /// HatariLauncher/CpcecLauncher (sous-dossier "extracted" du répertoire Configs configuré par
    /// l'utilisateur). Ce contenu est entièrement reconstructible — il est régénéré
    /// automatiquement au prochain lancement de la même release — donc sans risque
    /// à supprimer. Appelée à chaque démarrage de l'application pour éviter une
    /// accumulation indéfinie (un sous-dossier par ZIP déjà lancé, jamais nettoyé
    /// sinon). Best-effort : un échec (fichier verrouillé par exemple) ne doit pas
    /// empêcher le démarrage de l'application ; le nettoyage sera simplement
    /// retenté au lancement suivant.
    /// </summary>
    /// <summary>
    /// Vide entièrement le dossier de travail de l'application (<c>Working\</c>) au
    /// démarrage — fichiers et sous-dossiers compris. Ce dossier ne contient que des
    /// fichiers temporaires/reconstructibles (ZIPs extraits pour les émulateurs, fichiers
    /// WAV générés par ZXTune/UADE, images décodées par Recoil, scripts de pré-lancement…)
    /// donc le vider sans discrimination est sûr.
    ///
    /// Le paramètre <paramref name="workingRoot"/> est le chemin racine du dossier Working
    /// (ex. <c>C:\...\bin\Working</c>). Le dossier lui-même est conservé ; seul son contenu
    /// est supprimé. Les erreurs sont ignorées silencieusement pour ne pas bloquer le
    /// démarrage de l'application.
    /// </summary>
    public static void CleanExtractedCache(string workingRoot)
    {
        try
        {
            if (!Directory.Exists(workingRoot)) return;

            foreach (var file in Directory.GetFiles(workingRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(file); } catch { /* ignorer les fichiers verrouillés */ }
            }

            foreach (var dir in Directory.GetDirectories(workingRoot))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* idem */ }
            }
        }
        catch
        {
            // Ne jamais bloquer le démarrage pour un nettoyage de cache.
        }
    }

    // InitializeConfigAsync et InitializeDatsAsync ont été retirées : leurs tables
    // (ReleaseProfileOverrides, Preferences, FavoriteSoundtracks, FavoriteGraphics,
    // DatEntries, DatRoms, DatFileVersions) vivent maintenant directement dans
    // CreateTablesAsync (base unique demobase.db) — voir plus bas dans ce fichier.
    // Application non encore déployée au moment de cette fusion : aucune migration
    // de données existantes n'était nécessaire.


    public static async Task InitializeAsync(string connectionString)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        // 2026-08-02, retour utilisateur ("un utilisateur me dit que le logiciel ne
        // se lance pas si il est déposé sur un partage SMB") : le mode WAL de SQLite
        // repose sur un fichier "-shm" en mémoire partagée mappée (mmap) pour
        // coordonner lecteurs/écrivains — mécanisme documenté par SQLite lui-même
        // comme non fiable sur les systèmes de fichiers réseau (SMB, NFS), cf.
        // sqlite.org/wal.html : "the WAL journal mode ... does not work over a
        // network filesystem". Si demobase.db se retrouve sur un partage réseau
        // (ce qui arrive automatiquement si l'exe lui-même y est lancé, puisque le
        // dossier Database/ est relatif à AppContext.BaseDirectory), l'ouverture de
        // la base ou une opération d'E/S ultérieure peut échouer — et comme cet
        // InitializeAsync est appelé tout au début d'OnStartup (async void, donc
        // rien ne catchait l'exception avant le correctif ci-dessous dans
        // App.xaml.cs), ça se traduisait par un crash silencieux au lancement.
        // Bascule sur journal_mode=DELETE (rollback journal classique, sans mmap)
        // UNIQUEMENT quand la base est détectée sur un chemin réseau — perte de
        // perf mineure sur les écritures concurrentes, sans impact réel pour un
        // usage mono-utilisateur/mono-process comme DemoBase.
        var dbPath = new SqliteConnectionStringBuilder(connectionString).DataSource;
        var journalMode = IsNetworkPath(dbPath) ? "DELETE" : "WAL";
        await ExecAsync(conn, $"PRAGMA journal_mode={journalMode};");
        await ExecAsync(conn, "PRAGMA synchronous=NORMAL;");
        await ExecAsync(conn, "PRAGMA foreign_keys=OFF;");
        await ExecAsync(conn, "PRAGMA cache_size=-32000;");  // 32 Mo de cache page SQLite
        await ExecAsync(conn, "PRAGMA temp_store=MEMORY;");  // tables temp en RAM

        await CreateTablesAsync(conn);

        // ── Migrations pour bases existantes ──────────────────────────────────
        await MigrateAsync(conn);

        // Corrige les bases existantes qui ont des '' dans les colonnes datetime
        // (résidu des versions précédentes de DbInitializer)
        await FixEmptyDatesAsync(conn);

        await ExecAsync(conn, "PRAGMA foreign_keys=ON;");
    }

    /// <summary>
    /// Remplace les chaînes vides '' par NULL dans toutes les colonnes CreatedAt/UpdatedAt.
    /// Compatible avec les bases créées par les versions précédentes.
    /// </summary>
    /// <summary>
    /// Ajoute les colonnes manquantes sur les bases existantes (ALTER TABLE).
    /// Chaque appel est idempotent — ignore l'erreur si la colonne existe déjà.
    /// </summary>
    /// <summary>
    /// Peuple ReleaseSoundtracks depuis demozoo_raw.db sans refaire l'import complet.
    /// À appeler une seule fois après le premier import si la table est vide.
    /// </summary>
    public static async Task ImportSoundtracksFromRawAsync(string mainConnStr, string rawDbPath)
    {
        if (!File.Exists(rawDbPath)) return;

        await using var conn = new SqliteConnection(mainConnStr);
        await conn.OpenAsync();

        // Vérifier si déjà peuplé
        await using var chk = conn.CreateCommand();
        chk.CommandText = """SELECT COUNT(*) FROM "ReleaseSoundtracks";""";
        var count = (long)(await chk.ExecuteScalarAsync() ?? 0L);
        if (count > 0) return;  // déjà importé

        // Attacher demozoo_raw.db
        await using var attach = conn.CreateCommand();
        attach.CommandText = $"ATTACH DATABASE '{rawDbPath.Replace("'", "''")}' AS raw;";
        await attach.ExecuteNonQueryAsync();

        // Insérer les soundtracks depuis raw
        // productions_production_soundtracks : id, production_id, soundtrack_id
        await using var ins = conn.CreateCommand();
        ins.CommandText = """
            INSERT OR IGNORE INTO "ReleaseSoundtracks" ("Id", "ReleaseId", "SoundtrackId")
            SELECT s.id, s.production_id, s.soundtrack_id
            FROM raw."productions_production_soundtracks" s
            WHERE EXISTS (SELECT 1 FROM "Releases" r WHERE r.DemozooId = s.production_id)
              AND EXISTS (SELECT 1 FROM "Releases" r WHERE r.DemozooId = s.soundtrack_id);
            """;
        await ins.ExecuteNonQueryAsync();

        // Détacher
        await using var det = conn.CreateCommand();
        det.CommandText = "DETACH DATABASE raw;";
        await det.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(SqliteConnection conn)
    {
        // Ne rien faire si la DB est vide (premier lancement)
        await using var chk = conn.CreateCommand();
        chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Releases';";
        var tableExists = (long)(await chk.ExecuteScalarAsync() ?? 0L) > 0;
        if (!tableExists) return;

        // Colonnes ajoutées à Emulators
        await AddColumnIfMissingAsync(conn, "Emulators",      "Notes",            "TEXT");
        await AddColumnIfMissingAsync(conn, "Emulators",      "EmulatorType",     "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(conn, "Releasers",     "Abbreviation",     "TEXT");
        await AddColumnIfMissingAsync(conn, "Releasers",     "Differentiator",   "TEXT");
        await AddColumnIfMissingAsync(conn, "Releasers",     "FirstName",        "TEXT");
        await AddColumnIfMissingAsync(conn, "Releasers",     "SurName",          "TEXT");
        await AddColumnIfMissingAsync(conn, "Releasers",     "Location",         "TEXT");
        await AddColumnIfMissingAsync(conn, "Nicks",         "Abbreviation",     "TEXT");

        // ── Index manquants ────────────────────────────────────────────────
        // ReleaseAuthors
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleaseAuthors_NickId"    ON "ReleaseAuthors"("NickId");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleaseAuthors_ReleaseId" ON "ReleaseAuthors"("ReleaseId");""");

        // ReleasePlatforms
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleasePlatforms_PlatformId" ON "ReleasePlatforms"("PlatformId");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleasePlatforms_ReleaseId"  ON "ReleasePlatforms"("ReleaseId");""");

        // Releasers — IsGroup très utilisé en filtre, lower(Name) pour la recherche
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releasers_IsGroup"    ON "Releasers"("IsGroup");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releasers_IsGroup_Name" ON "Releasers"("IsGroup", "Name");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releasers_NameLower"  ON "Releasers"(lower("Name"));""");

        // Nicks — recherche par nom (insensible à la casse)
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Nicks_NameLower" ON "Nicks"(lower("Name"));""");

        // MediaFiles — index composé ReleaseId+Type (requête fréquente Screenshots)
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_MediaFiles_ReleaseId_Type" ON "MediaFiles"("ReleaseId", "Type");""");

        // CompetitionPlacings
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_CompetitionPlacings_CompetitionId" ON "CompetitionPlacings"("CompetitionId");""");

        // ReleaseSoundtracks — créer la table si absente (migration depuis ancienne DB)
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleaseSoundtracks" (
                "Id"           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "ReleaseId"    INTEGER NOT NULL REFERENCES "Releases"("Id") ON DELETE CASCADE,
                "SoundtrackId" INTEGER NOT NULL REFERENCES "Releases"("Id") ON DELETE RESTRICT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleaseSoundtracks_ReleaseId"    ON "ReleaseSoundtracks"("ReleaseId");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleaseSoundtracks_SoundtrackId" ON "ReleaseSoundtracks"("SoundtrackId");""");

        // Releases — IsFavorite pour filtres futurs
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releases_IsFavorite" ON "Releases"("IsFavorite") WHERE "IsFavorite" = 1;""");

        // Tables DAT (simplifiées)
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "DatFileVersions" (
                "Id"       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "FileName" TEXT    NOT NULL,
                "Version"  TEXT    NOT NULL DEFAULT ''
            );
            """);
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_DatFileVersions_FileName" ON "DatFileVersions"("FileName");""");

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "DatEntries" (
                "Id"         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "DemozooId"  INTEGER NOT NULL,
                "RomPath"    TEXT    NOT NULL DEFAULT '',
                "SourceFile" TEXT    NOT NULL DEFAULT ''
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_DatEntries_DemozooId" ON "DatEntries"("DemozooId");""");

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "DatRoms" (
                "Id"         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "DatEntryId" INTEGER NOT NULL REFERENCES "DatEntries"("Id") ON DELETE CASCADE,
                "Name"       TEXT    NOT NULL DEFAULT '',
                "Size"       INTEGER NOT NULL DEFAULT 0,
                "Crc32"      TEXT,
                "Md5"        TEXT,
                "Sha1"       TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_DatRoms_DatEntryId" ON "DatRoms"("DatEntryId");""");

        // ── EmulatorSettings : passage de "par émulateur" à "par profil" ───────
        // Historique : cette table vit ici, dans demobase.db (pas dans config.db
        // malgré son thème — cf. note plus haut sur le "shadowing" SQLite), donc
        // c'est ICI que la vraie migration de données doit avoir lieu.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "EmulatorSettings" (
                "Id"          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "EmulatorId"  INTEGER NOT NULL REFERENCES "Emulators"("Id") ON DELETE CASCADE,
                "Key"         TEXT    NOT NULL,
                "Value"       TEXT
            );
            """);
        await MigrateEmulatorSettingsToConfigIdAsync(conn);

        // Colonnes ajoutées à EmulatorConfigs
        await AddColumnIfMissingAsync(conn, "EmulatorConfigs", "WorkingDirectory", "TEXT");
        await AddColumnIfMissingAsync(conn, "EmulatorConfigs", "FullScreen",       "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(conn, "EmulatorConfigs", "PreLaunchScript",  "TEXT");
        await AddColumnIfMissingAsync(conn, "EmulatorConfigs", "Notes",            "TEXT");

        // ── IDs manuels : démarrer à 100 ─────────────────────────────────────
        // Les IDs 0-99 sont réservés aux émulateurs gérés (valeur fixe = (int)EmulatorType).
        // Sur les bases existantes créées avec AUTOINCREMENT, sqlite_sequence peut
        // avoir un seq < 100 ; on le force à 99 pour que le prochain INSERT sans
        // Id explicite reçoive 100.
        // Note : ON CONFLICT ne fonctionne pas sur sqlite_sequence (table système).
        // On utilise INSERT OR IGNORE + UPDATE séparés — idempotent et sans risque
        // de descendre le seq si l'utilisateur a déjà des entrées au-delà de 100.
        await ExecAsync(conn, """
            INSERT OR IGNORE INTO sqlite_sequence (name, seq) VALUES ('Emulators', 99);
            """);
        await ExecAsync(conn, """
            UPDATE sqlite_sequence SET seq = MAX(seq, 99) WHERE name = 'Emulators';
            """);

        // ── Émulateurs gérés manquants (bases existantes) ─────────────────────
        // Pour les bases où le seed EF Core a tourné avec AUTOINCREMENT (sans
        // respecter l'Id imposé), on insère les entrées manquantes directement.
        // INSERT OR IGNORE : idempotent, ne touche jamais une ligne déjà présente.
        await ExecAsync(conn, """
            INSERT OR IGNORE INTO "Emulators" ("Id", "Name", "Version", "ExecutablePath", "Status", "EmulatorType")
            VALUES (58, 'BigPEmu',  '', '', 'Active', 58),
                   (59, 'Handy',    '', '', 'Active', 59),
                   (60, 'GeePee32', '', '', 'Active', 60),
                   (61, 'ep128emu', '', '', 'Active', 61),
                   (62, 'mz800emu', '', '', 'Active', 62),
                   (63, 'ColEm',    '', '', 'Active', 63);
            """);

        // Les configs et settings émulateurs sont seédés en fin de wizard
        // (ReadyPage → SeedAllAsync) — pas ici, pour ne pas pré-remplir
        // EmulatorConfigs/EmulatorSettings avant que les émulateurs soient installés.
    }

    /// <summary>
    /// Insère (INSERT OR IGNORE) les profils EmulatorConfigs et leurs EmulatorSettings
    /// par défaut. Appelé en fin de wizard (ReadyPage) une fois les émulateurs installés,
    /// puis à chaque démarrage via SeedAllAsync pour couvrir les nouvelles entrées.
    /// </summary>
    public static async Task SeedEmulatorConfigsAsync(string connectionString)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await SeedEmulatorConfigsAsync(conn);
    }

    private static Task SeedEmulatorConfigsAsync(SqliteConnection conn)
    {
        // Contenu supprimé — les configs et settings émulateurs seront
        // chargés via import JSON (EmulatorConfigExportService), pas codés en dur.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Migration EmulatorSettings.EmulatorId → EmulatorSettings.EmulatorConfigId.
    /// Avant ce changement, un réglage (ex. ROM TOS, modèle de machine Hatari)
    /// était partagé par TOUS les profils d'un même émulateur. Désormais chaque
    /// profil a ses propres réglages — donc les anciennes valeurs sont reportées
    /// sur le profil par défaut de l'émulateur concerné (ou son premier profil
    /// s'il n'y a pas de défaut). Depuis la fusion config.db/dats.db/demobase.db
    /// en un seul fichier, EmulatorConfigs vit dans la même base que
    /// EmulatorSettings par construction, donc une simple requête locale suffit.
    /// Idempotent : chaque étape se revérifie indépendamment des autres.
    /// </summary>
    private static async Task MigrateEmulatorSettingsToConfigIdAsync(SqliteConnection conn)
    {
        try
        {
            bool hasConfigIdColumn;
            await using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('EmulatorSettings') WHERE name='EmulatorConfigId';";
                hasConfigIdColumn = (long)(await check.ExecuteScalarAsync() ?? 0L) > 0;
            }

            if (!hasConfigIdColumn)
            {
                // Étape 1 : schéma seulement (la donnée est rattachée séparément à l'étape 2
                // ci-dessous, qui tourne à CHAQUE démarrage, indépendamment de ce early-exit).
                await ExecAsync(conn, """ALTER TABLE "EmulatorSettings" ADD COLUMN "EmulatorConfigId" INTEGER;""");

                // Bascule de l'index unique : (EmulatorId, Key) → (EmulatorConfigId, Key)
                await ExecAsync(conn, """DROP INDEX IF EXISTS "IX_EmulatorSettings_EmulatorId_Key";""");
                await ExecAsync(conn, """
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_EmulatorSettings_EmulatorConfigId_Key"
                    ON "EmulatorSettings"("EmulatorConfigId", "Key");
                    """);
            }

            // Étape 2 : rattacher les réglages orphelins (EmulatorConfigId NULL) à un profil,
            // via la colonne EmulatorId encore présente — SÉPARÉE de l'étape 1 (même logique
            // que pour la suppression de colonne plus bas) pour rattraper toute base où l'étape
            // 1 a déjà tourné une fois sans que celle-ci ait pu aboutir correctement.
            // IMPORTANT — bug corrigé ici : la version précédente de cette migration ATTACHAIT
            // config.db et lisait "cfgmig.EmulatorConfigs" pour trouver le profil par défaut de
            // chaque émulateur. Or EmulatorConfigs est mappé SANS qualification de schéma côté
            // EF Core (`ToTable("EmulatorConfigs")`), donc EF Core lit/écrit en réalité la copie
            // de CETTE table dans demobase.db (résolution SQLite : un nom non qualifié désigne
            // toujours "main" avant toute base attachée — cf. sqlite.org/lang_attach.html). La
            // copie dans config.db est restée vide tout du long ; la requête ATTACHée ne pouvait
            // donc jamais trouver de correspondance. Fix : utiliser la table LOCALE non qualifiée
            // (même connexion, même fichier que EmulatorSettings) — plus besoin d'ATTACH du tout.
            await using (var oldColCheck = conn.CreateCommand())
            {
                oldColCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('EmulatorSettings') WHERE name='EmulatorId';";
                var hasOldColumnForBackfill = (long)(await oldColCheck.ExecuteScalarAsync() ?? 0L) > 0;
                if (hasOldColumnForBackfill)
                {
                    await ExecAsync(conn, """
                        UPDATE "EmulatorSettings" SET "EmulatorConfigId" = (
                            SELECT ec."Id" FROM "EmulatorConfigs" ec
                            WHERE ec."EmulatorId" = "EmulatorSettings"."EmulatorId"
                            ORDER BY ec."IsDefault" DESC, ec."Id" ASC LIMIT 1
                        ) WHERE "EmulatorConfigId" IS NULL;
                        """);
                }
                // Si la colonne EmulatorId a déjà été supprimée (étape 3 ci-dessous, sur un
                // démarrage précédent) : on ne peut plus rattacher d'éventuels réglages restés
                // orphelins faute de la version corrigée ci-dessus — ce n'était possible que pour
                // les bases ayant déjà tourné l'ancienne version bugée ET dont CETTE étape 2
                // n'avait jamais pu tourner avant la suppression de colonne. Cas résiduel,
                // accepté : mieux qu'un crash, et la donnée concernée (réglages d'un émulateur
                // jamais réouvert/resauvegardé depuis le refactor) était de toute façon déjà
                // inaccessible depuis l'UI.
            }

            // Étape 3 : suppression de l'ancienne colonne EmulatorId, APRÈS l'étape 2 ci-dessus
            // (qui en a besoin) — indépendante elle aussi, cf. commentaire détaillé plus haut
            // dans l'historique de cette fonction : sa présence en NOT NULL fait échouer toute
            // INSERTION d'un nouveau réglage (profil sans réglages migrés depuis l'ancien schéma).
            await using (var checkOld = conn.CreateCommand())
            {
                checkOld.CommandText = "SELECT COUNT(*) FROM pragma_table_info('EmulatorSettings') WHERE name='EmulatorId';";
                var hasOldColumn = (long)(await checkOld.ExecuteScalarAsync() ?? 0L) > 0;
                if (hasOldColumn)
                    await ExecAsync(conn, """ALTER TABLE "EmulatorSettings" DROP COLUMN "EmulatorId";""");
            }
        }
        catch (Exception ex)
        {
            // Best-effort : un souci de migration (verrou sur config.db, permissions...) ne
            // doit jamais empêcher le démarrage de l'application. Au pire, les anciens
            // réglages restent rattachés à EmulatorId seul (donc invisibles tant que la
            // colonne EmulatorConfigId n'est pas peuplée) et devront être ressaisis ; ce
            // n'est jamais pire qu'un crash au démarrage. Nouvelle tentative au prochain
            // lancement (chaque étape ci-dessus est elle-même idempotente).
            System.Diagnostics.Debug.WriteLine($"[DbInitializer] Migration EmulatorSettings→EmulatorConfigId échouée (non bloquant) : {ex.Message}");
        }
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn, string table, string column, string definition)
    {
        // Vérifier que la table existe avant tout
        await using var tblChk = conn.CreateCommand();
        tblChk.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';";
        if ((long)(await tblChk.ExecuteScalarAsync() ?? 0L) == 0) return;

        // Vérifier si la colonne existe déjà
        await using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';";
        var exists = (long)(await check.ExecuteScalarAsync() ?? 0L) > 0;
        if (!exists)
            await ExecAsync(conn, $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};");
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection conn, string table, string column)
    {
        await using var tblChk = conn.CreateCommand();
        tblChk.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';";
        if ((long)(await tblChk.ExecuteScalarAsync() ?? 0L) == 0) return false;

        await using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';";
        return (long)(await check.ExecuteScalarAsync() ?? 0L) > 0;
    }

    private static async Task FixEmptyDatesAsync(SqliteConnection conn)
    {
        var tables = new[]
        {
            "ReleaseTypes", "Platforms", "Emulators", "EmulatorConfigs",
            "Releases", "ReleaseLinks", "Releasers", "Nicks",
            "PartySeries", "Parties", "Competitions", "CompetitionPlacings", "MediaFiles"
        };

        foreach (var table in tables)
        {
            // Utilise une requête safe qui ne plante pas si la table n'existe pas encore
            await ExecAsync(conn,
                $"UPDATE \"{table}\" SET \"CreatedAt\" = NULL WHERE \"CreatedAt\" = '' OR \"CreatedAt\" = '2000-01-01T00:00:00';");
            await ExecAsync(conn,
                $"UPDATE \"{table}\" SET \"UpdatedAt\" = NULL WHERE \"UpdatedAt\" = '' OR \"UpdatedAt\" = '2000-01-01T00:00:00';");
        }
    }

    private static async Task CreateTablesAsync(SqliteConnection conn)
    {
        // ── ReleaseTypes ──────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleaseTypes" (
                "Id"          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Name"        TEXT    NOT NULL DEFAULT '',
                "Supertype"   TEXT    NOT NULL DEFAULT 'production',
                "Description" TEXT,
                "SortOrder"   INTEGER NOT NULL DEFAULT 0,
                "DemozooId"   INTEGER,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReleaseTypes_Name" ON "ReleaseTypes"("Name");""");
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReleaseTypes_DemozooId" ON "ReleaseTypes"("DemozooId") WHERE "DemozooId" IS NOT NULL;""");

        // ── Platforms ─────────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "Platforms" (
                "Id"        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Name"      TEXT    NOT NULL DEFAULT '',
                "ShortName" TEXT,
                "DemozooId" INTEGER,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Platforms_Name" ON "Platforms"("Name");""");
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Platforms_DemozooId" ON "Platforms"("DemozooId") WHERE "DemozooId" IS NOT NULL;""");

        // ── Emulators ─────────────────────────────────────────────────────────
        // Pas d'AUTOINCREMENT : les IDs 0–99 sont réservés aux émulateurs gérés
        // (valeur fixe = (int)EmulatorType). Les IDs manuels démarrent à 100.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "Emulators" (
                "Id"             INTEGER NOT NULL PRIMARY KEY,
                "Name"           TEXT    NOT NULL DEFAULT '',
                "Version"        TEXT    NOT NULL DEFAULT '',
                "ExecutablePath" TEXT    NOT NULL DEFAULT '',
                "DefaultArgs"    TEXT,
                "Website"        TEXT,
                "Status"         TEXT    NOT NULL DEFAULT 'Active',
                "EmulatorType"   INTEGER NOT NULL DEFAULT 0,
                "Notes"          TEXT,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);

        // ── EmulatorConfigs ───────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "EmulatorConfigs" (
                "Id"               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "EmulatorId"       INTEGER NOT NULL REFERENCES "Emulators"("Id") ON DELETE CASCADE,
                "PlatformId"       INTEGER NOT NULL REFERENCES "Platforms"("Id"),
                "ProfileName"      TEXT    NOT NULL DEFAULT 'Default',
                "CommandLine"      TEXT    NOT NULL DEFAULT '',
                "WorkingDirectory" TEXT,
                "ConfigFilePath"   TEXT,
                "IsDefault"        INTEGER NOT NULL DEFAULT 0,
                "FullScreen"       INTEGER NOT NULL DEFAULT 0,
                "PreLaunchScript"  TEXT,
                "Notes"            TEXT,
                "CreatedAt"        TEXT,
                "UpdatedAt"        TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_EmulatorConfigs_PlatformId" ON "EmulatorConfigs"("PlatformId");""");

        // ── Releases ──────────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "Releases" (
                "Id"                   INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Title"                TEXT    NOT NULL DEFAULT '',
                "ReleaseDate"          TEXT,
                "ReleaseDatePrecision" TEXT,
                "Supertype"            TEXT    NOT NULL DEFAULT 'production',
                "ReleaseTypeId"        INTEGER REFERENCES "ReleaseTypes"("Id") ON DELETE RESTRICT,
                "Notes"                TEXT,
                "IsFavorite"           INTEGER NOT NULL DEFAULT 0,
                "Rating"               INTEGER,
                "DemozooUrl"           TEXT,
                "DemozooId"            INTEGER,
                "PouetUrl"             TEXT,
                "CsdbUrl"              TEXT,
                "Tags"                 TEXT,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releases_Title"         ON "Releases"("Title");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releases_ReleaseDate"   ON "Releases"("ReleaseDate");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releases_ReleaseTypeId" ON "Releases"("ReleaseTypeId");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releases_Supertype"     ON "Releases"("Supertype");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releases_DemozooId" ON "Releases"("DemozooId") WHERE "DemozooId" IS NOT NULL;""");

        // Migrations colonnes Releases (colonnes ajoutées après la création initiale)
        await AddColumnIfMissingAsync(conn, "Releases", "AuthorNamesCache",   "TEXT");
        await AddColumnIfMissingAsync(conn, "Releases", "ThumbnailPathCache", "TEXT");
        await AddColumnIfMissingAsync(conn, "Releases", "ViewCount",          "INTEGER NOT NULL DEFAULT 0");

        // Index composites pour les filtres combinés les plus fréquents
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS `IX_Releases_Supertype_Title` ON `Releases`(`Supertype`, `Title`);""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS `IX_Releases_TypeId_Title` ON `Releases`(`ReleaseTypeId`, `Title`);""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS `IX_Releases_Supertype_Date` ON `Releases`(`Supertype`, `ReleaseDate`);""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS `IX_Releases_TitleLower` ON `Releases`(lower(`Title`));""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS `IX_Releases_AuthorNamesCache` ON `Releases`(`AuthorNamesCache`) WHERE `AuthorNamesCache` IS NOT NULL;""");

        // Index sur ReleaserMemberships — critiques pour l'affichage des groupes/sceners
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS `IX_Memberships_GroupId` ON `ReleaserMemberships`(`GroupId`);""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS `IX_Memberships_ScenerId` ON `ReleaserMemberships`(`ScenerId`);""");

        // Index sur ReleaseAuthors — lookup releases par nick
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS `IX_ReleaseAuthors_ReleaseId` ON `ReleaseAuthors`(`ReleaseId`);""");

        // ── ReleasePlatforms ──────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleasePlatforms" (
                "ReleaseId"  INTEGER NOT NULL REFERENCES "Releases"("Id"),
                "PlatformId" INTEGER NOT NULL REFERENCES "Platforms"("Id"),
                PRIMARY KEY ("ReleaseId", "PlatformId")
            );
            """);

        // ── Releasers ─────────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "Releasers" (
                "Id"           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Name"         TEXT    NOT NULL DEFAULT '',
                "IsGroup"      INTEGER NOT NULL DEFAULT 0,
                "Abbreviation" TEXT,
                "Country"      TEXT,
                "Website"      TEXT,
                "Notes"        TEXT,
                "LogoPath"     TEXT,
                "DemozooId"    INTEGER,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releasers_Name"      ON "Releasers"("Name");""");
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Releasers_DemozooId" ON "Releasers"("DemozooId") WHERE "DemozooId" IS NOT NULL;""");

        // ── Nicks ─────────────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "Nicks" (
                "Id"           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "ReleaserId"   INTEGER NOT NULL REFERENCES "Releasers"("Id"),
                "Name"         TEXT    NOT NULL DEFAULT '',
                "Abbreviation" TEXT,
                "IsPrimary"    INTEGER NOT NULL DEFAULT 0,
                "DemozooId"    INTEGER,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Nicks_Name"      ON "Nicks"("Name");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Nicks_ReleaserId" ON "Nicks"("ReleaserId");""");
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Nicks_DemozooId" ON "Nicks"("DemozooId") WHERE "DemozooId" IS NOT NULL;""");

        // ── ReleaserMemberships ───────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleaserMemberships" (
                "ScenerId"        INTEGER NOT NULL REFERENCES "Releasers"("Id"),
                "GroupId"         INTEGER NOT NULL REFERENCES "Releasers"("Id"),
                "IsCurrentMember" INTEGER NOT NULL DEFAULT 1,
                "JoinYear"        INTEGER,
                "LeaveYear"       INTEGER,
                PRIMARY KEY ("ScenerId", "GroupId")
            );
            """);

        // ── ReleaseAuthors ────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleaseAuthors" (
                "ReleaseId"         INTEGER NOT NULL REFERENCES "Releases"("Id"),
                "NickId"            INTEGER NOT NULL REFERENCES "Nicks"("Id"),
                "AffiliationNickId" INTEGER REFERENCES "Nicks"("Id"),
                PRIMARY KEY ("ReleaseId", "NickId")
            );
            """);

        // ── ReleaseCredits ────────────────────────────────────────────────────
        // ReleaserId stocke en réalité un NickId (structure Demozoo)
        // FK vers Releasers désactivée — résolution via JOIN à l'affichage
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleaseCredits" (
                "ReleaseId"  INTEGER NOT NULL,
                "ReleaserId" INTEGER NOT NULL,
                "Role"       TEXT    NOT NULL DEFAULT '',
                "Detail"     TEXT,
                PRIMARY KEY ("ReleaseId", "ReleaserId", "Role")
            );
            """);

        // ── ReleaseLinks ──────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleaseLinks" (
                "Id"               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "ReleaseId"        INTEGER NOT NULL REFERENCES "Releases"("Id"),
                "Url"              TEXT,
                "LocalFilePath"    TEXT,
                "FileName"         TEXT,
                "Format"           TEXT,
                "FileSizeBytes"    INTEGER,
                "IsMainFile"       INTEGER NOT NULL DEFAULT 0,
                "IsLocalCopy"      INTEGER NOT NULL DEFAULT 0,
                "EmulatorConfigId" INTEGER REFERENCES "EmulatorConfigs"("Id"),
                "LinkClass"        TEXT,
                "LinkParameter"   TEXT,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleaseLinks_ReleaseId" ON "ReleaseLinks"("ReleaseId");""");

        // ── Migration : ajout colonnes LinkClass / LinkParameter si absentes ──
        await AddColumnIfMissingAsync(conn, "ReleaseLinks", "LinkClass",      "TEXT");
        await AddColumnIfMissingAsync(conn, "ReleaseLinks", "LinkParameter",  "TEXT");

        // ── Migration : renommage VideoParameter → LinkParameter (installs existantes) ──
        // Si l'ancienne colonne existe encore (base créée avant ce renommage),
        // on copie ses valeurs puis on la supprime.
        if (await ColumnExistsAsync(conn, "ReleaseLinks", "VideoParameter"))
        {
            await ExecAsync(conn, """
                UPDATE "ReleaseLinks" SET "LinkParameter" = "VideoParameter"
                WHERE "LinkParameter" IS NULL AND "VideoParameter" IS NOT NULL;
                """);
            await ExecAsync(conn, """ALTER TABLE "ReleaseLinks" DROP COLUMN "VideoParameter";""");
        }

        // ReleaseAuthors
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleaseAuthors_NickId"    ON "ReleaseAuthors"("NickId");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleaseAuthors_ReleaseId" ON "ReleaseAuthors"("ReleaseId");""");

        // ReleasePlatforms
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleasePlatforms_PlatformId" ON "ReleasePlatforms"("PlatformId");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleasePlatforms_ReleaseId"  ON "ReleasePlatforms"("ReleaseId");""");

        // Releasers
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releasers_IsGroup"      ON "Releasers"("IsGroup");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releasers_IsGroup_Name" ON "Releasers"("IsGroup", "Name");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releasers_NameLower"    ON "Releasers"(lower("Name"));""");

        // Nicks
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Nicks_NameLower" ON "Nicks"(lower("Name"));""");

        // MediaFiles composé
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_MediaFiles_ReleaseId_Type" ON "MediaFiles"("ReleaseId", "Type");""");

        // CompetitionPlacings + ReleaseSoundtracks
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_CompetitionPlacings_CompetitionId" ON "CompetitionPlacings"("CompetitionId");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleaseSoundtracks_SoundtrackId"   ON "ReleaseSoundtracks"("SoundtrackId");""");

        // Releases
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Releases_IsFavorite" ON "Releases"("IsFavorite") WHERE "IsFavorite" = 1;""");

        // ── PartySeries ───────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "PartySeries" (
                "Id"        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Name"      TEXT    NOT NULL DEFAULT '',
                "Website"   TEXT,
                "Notes"     TEXT,
                "Country"   TEXT,
                "DemozooId" INTEGER,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_PartySeries_DemozooId" ON "PartySeries"("DemozooId") WHERE "DemozooId" IS NOT NULL;""");

        // ── Parties ───────────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "Parties" (
                "Id"            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Name"          TEXT    NOT NULL DEFAULT '',
                "Tagline"       TEXT,
                "PartySeriesId" INTEGER REFERENCES "PartySeries"("Id"),
                "StartDate"     TEXT,
                "EndDate"       TEXT,
                "Location"      TEXT,
                "IsOnline"      INTEGER NOT NULL DEFAULT 0,
                "CountryCode"   TEXT,
                "Latitude"      REAL,
                "Longitude"     REAL,
                "Website"       TEXT,
                "Notes"         TEXT,
                "DemozooId"     INTEGER,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_Parties_Name" ON "Parties"("Name");""");
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Parties_DemozooId" ON "Parties"("DemozooId") WHERE "DemozooId" IS NOT NULL;""");

        // ── Competitions ──────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "Competitions" (
                "Id"        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "PartyId"   INTEGER NOT NULL REFERENCES "Parties"("Id"),
                "Name"      TEXT    NOT NULL DEFAULT '',
                "DemozooId" INTEGER,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Competitions_DemozooId" ON "Competitions"("DemozooId") WHERE "DemozooId" IS NOT NULL;""");

        // ── CompetitionPlacings ───────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "CompetitionPlacings" (
                "Id"            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "CompetitionId" INTEGER NOT NULL REFERENCES "Competitions"("Id"),
                "ReleaseId"     INTEGER NOT NULL REFERENCES "Releases"("Id"),
                "Ranking"       INTEGER,
                "Score"         TEXT,
                "DemozooId"     INTEGER,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_CompetitionPlacings_ReleaseId" ON "CompetitionPlacings"("ReleaseId");""");
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompetitionPlacings_DemozooId" ON "CompetitionPlacings"("DemozooId") WHERE "DemozooId" IS NOT NULL;""");

        // ── MediaFiles ────────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "MediaFiles" (
                "Id"              INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "ReleaseId"       INTEGER NOT NULL REFERENCES "Releases"("Id"),
                "Type"            TEXT    NOT NULL DEFAULT '',
                "FilePath"        TEXT    NOT NULL DEFAULT '',
                "Title"           TEXT,
                "SortOrder"       INTEGER NOT NULL DEFAULT 0,
                "Format"          TEXT,
                "DurationSeconds" INTEGER,
                "CreatedAt"   TEXT,
                "UpdatedAt"   TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_MediaFiles_ReleaseId" ON "MediaFiles"("ReleaseId");""");

        // ── ReleaseSoundtracks : liens Release → Soundtrack ──────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleaseSoundtracks" (
                "Id"           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "ReleaseId"    INTEGER NOT NULL REFERENCES "Releases"("Id"),
                "SoundtrackId" INTEGER NOT NULL REFERENCES "Releases"("Id")
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ReleaseSoundtracks_ReleaseId" ON "ReleaseSoundtracks"("ReleaseId");""");

        // ── Tables DAT ───────────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "DatFileVersions" (
                "Id"       INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "FileName" TEXT    NOT NULL,
                "Version"  TEXT    NOT NULL DEFAULT ''
            );
            """);
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_DatFileVersions_FileName" ON "DatFileVersions"("FileName");""");

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "DatEntries" (
                "Id"         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "DemozooId"  INTEGER NOT NULL,
                "RomPath"    TEXT    NOT NULL DEFAULT '',
                "SourceFile" TEXT    NOT NULL DEFAULT ''
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_DatEntries_DemozooId" ON "DatEntries"("DemozooId");""");

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "DatRoms" (
                "Id"         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "DatEntryId" INTEGER NOT NULL REFERENCES "DatEntries"("Id") ON DELETE CASCADE,
                "Name"       TEXT    NOT NULL DEFAULT '',
                "Size"       INTEGER NOT NULL DEFAULT 0,
                "Crc32"      TEXT,
                "Md5"        TEXT,
                "Sha1"       TEXT
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_DatRoms_DatEntryId" ON "DatRoms"("DatEntryId");""");

        // ── Table temporaire : liens Release ↔ Type (M:N Demozoo) ────────────
        // Importée depuis productions_production_types, puis utilisée pour
        // remplir Releases.ReleaseTypeId via UPDATE post-import.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "_ReleaseTypeLinks" (
                "ReleaseId"     INTEGER NOT NULL,
                "ReleaseTypeId" INTEGER NOT NULL,
                PRIMARY KEY ("ReleaseId", "ReleaseTypeId")
            );
            """);

        // ── ReleaseProfileOverrides ───────────────────────────────────────────
        // Réglage par RELEASE du profil de lancement à utiliser, à la place du
        // profil par défaut de la plateforme. Clé = DemozooId (identifiant
        // stable, contrairement à l'Id interne qui pourrait changer si la
        // stratégie d'import évolue) plutôt que ReleaseId.
        //
        // Base unique (demobase.db) : EmulatorConfigs vit maintenant dans le
        // même fichier que cette table, donc la FK est enfin vérifiable par
        // SQLite — contrairement à l'ancienne architecture à 3 fichiers où
        // cette contrainte ne pouvait jamais être validée entre deux bases
        // séparées (cf. historique dans RESUME_PROJET.md si besoin).
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleaseProfileOverrides" (
                "Id"               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "ReleaseDemozooId" INTEGER NOT NULL UNIQUE,
                "EmulatorConfigId" INTEGER NOT NULL REFERENCES "EmulatorConfigs"("Id") ON DELETE CASCADE
            );
            """);

        // Migration : bases créées avant la fusion en base unique, où cette
        // table avait été créée dans config.db SANS la FK (impossible à
        // vérifier entre deux fichiers à l'époque). On la recrée ici avec la
        // FK, en conservant les données si la table existe déjà sans elle.
        try
        {
            await using var fkCheck = conn.CreateCommand();
            fkCheck.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_list('ReleaseProfileOverrides');";
            var hasFk = (long)(await fkCheck.ExecuteScalarAsync() ?? 0L) > 0;
            if (!hasFk)
            {
                await using var existsCheck = conn.CreateCommand();
                existsCheck.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ReleaseProfileOverrides';";
                var alreadyExists = (long)(await existsCheck.ExecuteScalarAsync() ?? 0L) > 0;
                if (alreadyExists)
                {
                    await ExecAsync(conn, "PRAGMA foreign_keys=OFF;");
                    await ExecAsync(conn, """ALTER TABLE "ReleaseProfileOverrides" RENAME TO "ReleaseProfileOverrides_old";""");
                    await ExecAsync(conn, """
                        CREATE TABLE "ReleaseProfileOverrides" (
                            "Id"               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                            "ReleaseDemozooId" INTEGER NOT NULL UNIQUE,
                            "EmulatorConfigId" INTEGER NOT NULL REFERENCES "EmulatorConfigs"("Id") ON DELETE CASCADE
                        );
                        """);
                    // Ne reprendre que les lignes dont l'EmulatorConfigId existe encore
                    // (une FK désormais appliquée rejetterait silencieusement les autres).
                    await ExecAsync(conn, """
                        INSERT INTO "ReleaseProfileOverrides" ("Id", "ReleaseDemozooId", "EmulatorConfigId")
                        SELECT o."Id", o."ReleaseDemozooId", o."EmulatorConfigId"
                        FROM "ReleaseProfileOverrides_old" o
                        WHERE EXISTS (SELECT 1 FROM "EmulatorConfigs" ec WHERE ec."Id" = o."EmulatorConfigId");
                        """);
                    await ExecAsync(conn, """DROP TABLE "ReleaseProfileOverrides_old";""");
                    await ExecAsync(conn, "PRAGMA foreign_keys=ON;");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DbInitializer] Migration ReleaseProfileOverrides (ajout FK) échouée (non bloquant) : {ex.Message}");
        }

        // ── DatEntryProfileOverrides ───────────────────────────────────────────
        // Ajoutée le 2026-07-25 (retour utilisateur) : une release peut être multi-
        // plateforme (ex. Amiga AGA + Atari Falcon) ET multi-fichier (plusieurs
        // DatEntry — versions/variantes distinctes) — dans ce cas, un SEUL override
        // par release (ReleaseProfileOverrides ci-dessus) ne suffit pas : chaque
        // fichier peut avoir besoin d'un profil (donc d'une plateforme) différent.
        // Clé = (DemozooId, RomPath) et NON DatEntry.Id : ce dernier n'est pas
        // stable, DatImportService recrée les DatEntry (DELETE + INSERT, nouveaux
        // Id auto-incrémentés) à chaque réimport du fichier DAT source concerné.
        // RomPath (chemin relatif du .zip, tel que fourni par Demozoo/le DAT) est
        // en revanche stable d'un import à l'autre pour un même fichier réel.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "DatEntryProfileOverrides" (
                "Id"               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "DemozooId"        INTEGER NOT NULL,
                "RomPath"          TEXT    NOT NULL,
                "EmulatorConfigId" INTEGER NOT NULL REFERENCES "EmulatorConfigs"("Id") ON DELETE CASCADE,
                UNIQUE("DemozooId", "RomPath")
            );
            """);

        // ── ReleasePreferredFiles ───────────────────────────────────────────────
        // Ajoutée le 2026-07-25 : mémorise, pour une release multi-fichier, QUEL
        // fichier (DatEntry, via son RomPath stable) l'utilisateur a choisi de
        // lancer — soit via le bouton "Utiliser" dans l'onglet Fichiers, soit via
        // la fenêtre de choix affichée au clic sur "Lancer" quand plusieurs
        // fichiers sont lançables et qu'aucun choix explicite n'a encore été fait.
        // Une seule préférence par release (clé = DemozooId) — cf.
        // ReleasePreferredFileService.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ReleasePreferredFiles" (
                "DemozooId" INTEGER NOT NULL PRIMARY KEY,
                "RomPath"   TEXT    NOT NULL
            );
            """);

        // ── Preferences (clé/valeur générique) ────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "Preferences" (
                "Id"    INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Key"   TEXT    NOT NULL UNIQUE,
                "Value" TEXT
            );
            """);

        // ── FavoriteSoundtracks ────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "FavoriteSoundtracks" (
                "Id"                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "SoundtrackDemozooId" INTEGER NOT NULL UNIQUE,
                "Title"               TEXT    NOT NULL DEFAULT '',
                "AuthorNames"         TEXT,
                "RomName"             TEXT,
                "ZipPath"             TEXT,
                "ReleaseTitle"        TEXT,
                "AddedAt"             TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """);

        // ── Playlists (regroupement de soundtracks favoris, façon Spotify) ──────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "Playlists" (
                "Id"        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Name"      TEXT    NOT NULL,
                "SortOrder" INTEGER NOT NULL DEFAULT 0,
                "CreatedAt" TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """);

        // ── PlaylistTracks (lien playlist → soundtrack favori, avec position) ───
        // Référence SoundtrackDemozooId (pas de FK stricte) — les métadonnées
        // (titre, auteurs, fichier…) sont résolues via JOIN sur FavoriteSoundtracks ;
        // une playlist ne peut donc contenir que des morceaux déjà mis en favori.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "PlaylistTracks" (
                "Id"                  INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "PlaylistId"          INTEGER NOT NULL REFERENCES "Playlists"("Id") ON DELETE CASCADE,
                "SoundtrackDemozooId" INTEGER NOT NULL,
                "Position"            INTEGER NOT NULL DEFAULT 0,
                "AddedAt"             TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE("PlaylistId", "SoundtrackDemozooId")
            );
            """);

        // ── FavoriteGraphics ───────────────────────────────────────────────────
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "FavoriteGraphics" (
                "Id"               INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "ReleaseDemozooId" INTEGER NOT NULL UNIQUE,
                "Title"            TEXT    NOT NULL DEFAULT '',
                "AuthorNames"      TEXT,
                "ZipPath"          TEXT,
                "FileInZip"        TEXT,
                "AddedAt"          TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """);

        // ── DownloadAttempts : historique des tentatives de téléchargement ──────
        // Permet d'éviter de re-télécharger un fichier dont la taille ne correspond
        // pas au DAT (fichier mis à jour sur le serveur depuis la création du DAT).
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "DownloadAttempts" (
                "Id"           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Url"          TEXT    NOT NULL,
                "FileName"     TEXT    NOT NULL DEFAULT '',
                "DemozooId"    INTEGER,
                "SizeOnServer" INTEGER NOT NULL DEFAULT 0,
                "SizeInDat"    INTEGER NOT NULL DEFAULT 0,
                "Crc32InDat"   TEXT,
                "Status"       TEXT    NOT NULL DEFAULT 'SizeMismatch',
                "AttemptedAt"  TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """);
        // Migration : ajouter DemozooId si la table existait sans cette colonne
        await ExecAsync(conn, """ALTER TABLE "DownloadAttempts" ADD COLUMN "DemozooId" INTEGER;""");
        await ExecAsync(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_DownloadAttempts_Url" ON "DownloadAttempts"("Url");""");

        // ── Modland (2026-07-30, demande utilisateur : onglet "Musique (modland)") ──
        // ModlandArchiveSnapshot : stocke le ZIP brut allmods.zip (blob) téléchargé depuis
        // http://ftp.modland.com/allmods.zip lors d'un rafraîchissement manuel — "pour plus de
        // souplesse" (retour utilisateur) : permet de re-parser sans re-télécharger, et garde
        // une trace de la dernière synchronisation. Une seule ligne conservée à la fois (le
        // service vide la table avant d'insérer la nouvelle snapshot) — un historique complet
        // gonflerait la base de plusieurs Mo à chaque sync sans utilité identifiée.
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ModlandArchiveSnapshot" (
                "Id"         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "SourceSize" INTEGER NOT NULL DEFAULT 0,
                "TrackCount" INTEGER NOT NULL DEFAULT 0,
                "ImportedAt" TEXT    NOT NULL DEFAULT CURRENT_TIMESTAMP,
                "ZipData"    BLOB
            );
            """);

        // ModlandTracks : index à plat de l'arborescence /pub/modules/<Format>/<Auteur>/<fichier>
        // extraite du listing texte contenu dans allmods.zip — reconstruit entièrement à chaque
        // sync (DELETE + bulk INSERT dans une seule transaction, cf. ModlandCatalogService).
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS "ModlandTracks" (
                "Id"        INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "Format"    TEXT    NOT NULL,
                "Author"    TEXT    NOT NULL,
                "FileName"  TEXT    NOT NULL,
                "Extension" TEXT    NOT NULL DEFAULT ''
            );
            """);
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ModlandTracks_Format" ON "ModlandTracks"("Format");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ModlandTracks_Author" ON "ModlandTracks"("Author");""");
        await ExecAsync(conn, """CREATE INDEX IF NOT EXISTS "IX_ModlandTracks_FormatAuthor" ON "ModlandTracks"("Format","Author");""");

        // Migration : sanitiser les RomPath existants contenant des caractères
        // interdits dans les noms de fichiers Windows (ex: '?' dans les titres Demozoo).
        // On cible uniquement le nom de fichier (après le dernier séparateur).
        await ExecAsync(conn, """
            UPDATE "DatEntries"
            SET "RomPath" = REPLACE("RomPath", '?', '_')
            WHERE "RomPath" LIKE '%?%';
            """);
        await ExecAsync(conn, """
            UPDATE "DatEntries"
            SET "RomPath" = REPLACE("RomPath", '*', '_')
            WHERE "RomPath" LIKE '%*%';
            """);
        await ExecAsync(conn, """
            UPDATE "DatEntries"
            SET "RomPath" = REPLACE("RomPath", ':', '_')
            WHERE "RomPath" LIKE '%:%';
            """);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
            when (ex.Message.Contains("no such table") ||
                  ex.Message.Contains("duplicate column") ||
                  ex.Message.Contains("already exists"))
        {
            // Silencieux : table absente, colonne déjà là, ou index déjà existant
        }
    }
}
