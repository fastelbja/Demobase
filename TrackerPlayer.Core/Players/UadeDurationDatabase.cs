using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace TrackerPlayer.Core.Players
{
    // ════════════════════════════════════════════════════════════════════════
    // Cache persistant des durées UADE (par fichier + par sous-chanson).
    // Port de DurationDatabase.cs (projet UadeWpfPlayer fourni par l'utilisateur,
    // 2026-08-06), inchangé dans ses grandes lignes.
    //
    // UADE a son propre mécanisme interne pour ça ("contentdb" dans le
    // basedir), mais il ne stocke qu'UNE SEULE durée par fichier (indexée par
    // le MD5 du contenu) — inutilisable pour un TFMX ou un tracker à
    // plusieurs sous-chansons, où chaque nouvelle sous-chanson scannée
    // écraserait la précédente dans cette même case. Il n'est de toute façon
    // pas accessible depuis l'API publique (libuade.dll) que ce projet
    // utilise. D'où cette base séparée, propre à DemoBase, indexée par
    // (MD5 du fichier, numéro de sous-chanson).
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cache SQLite persistant des durées mesurées par sous-chanson, keyé par
    /// (MD5 du contenu du fichier, numéro de sous-chanson) — un fichier
    /// renommé/déplacé (ou présent sous plusieurs chemins/collections)
    /// retrouve donc quand même ses durées déjà connues, comme le fait UADE
    /// lui-même en interne pour son propre cache.
    /// </summary>
    internal sealed class UadeDurationDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly object _lock = new();

        /// <summary>Raisons qui reflètent la vraie fin naturelle du morceau (une
        /// durée mesurée fiable) par opposition au cap configuré par
        /// sous-chanson (juste "on a abandonné après N secondes", pas la
        /// longueur réelle du morceau).</summary>
        public static bool IsReliableEndReason(string reason) =>
            reason is "silence" or "player" or "no more subsongs left";

        public UadeDurationDatabase(string dbPath)
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = dbPath };
            _connection = new SqliteConnection(builder.ConnectionString);
            _connection.Open();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS durations (
                    file_md5         TEXT NOT NULL,
                    subsong          INTEGER NOT NULL,
                    duration_seconds REAL NOT NULL,
                    end_reason       TEXT NOT NULL,
                    cap_seconds      INTEGER NOT NULL,
                    file_name        TEXT NOT NULL,
                    scanned_at       TEXT NOT NULL,
                    PRIMARY KEY (file_md5, subsong)
                );";
            cmd.ExecuteNonQuery();
        }

        /// <summary>MD5 des octets du fichier — même schéma d'identité que le
        /// cache interne d'UADE, pour qu'un fichier renommé/déplacé (mêmes
        /// octets) retombe quand même sur le cache.</summary>
        public static string ComputeFileMd5(string filePath)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = md5.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public readonly record struct CachedDuration(TimeSpan Duration, string EndReason, int CapSeconds, DateTime ScannedAt);

        /// <summary>Toutes les durées de sous-chansons en cache pour un MD5 de
        /// fichier donné, keyées par numéro de sous-chanson. Vide si rien n'a
        /// jamais été scanné/enregistré pour lui.</summary>
        public Dictionary<int, CachedDuration> GetCached(string fileMd5)
        {
            var result = new Dictionary<int, CachedDuration>();
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"SELECT subsong, duration_seconds, end_reason, cap_seconds, scanned_at
                                     FROM durations WHERE file_md5 = $md5";
                cmd.Parameters.AddWithValue("$md5", fileMd5);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int subsong = reader.GetInt32(0);
                    double seconds = reader.GetDouble(1);
                    string reason = reader.GetString(2);
                    int cap = reader.GetInt32(3);
                    DateTime scannedAt = DateTime.Parse(reader.GetString(4),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind);
                    result[subsong] = new CachedDuration(TimeSpan.FromSeconds(seconds), reason, cap, scannedAt);
                }
            }
            return result;
        }

        /// <summary>
        /// Vrai si chaque sous-chanson de [min..max] a déjà une entrée en
        /// cache suffisamment fiable pour être réutilisée sans rescanner :
        /// soit une vraie fin mesurée, soit une mesure limitée par un cap au
        /// moins aussi généreux que celui demandé actuellement (pour ne
        /// jamais réutiliser silencieusement une mesure plus courte, plus
        /// plafonnée).
        /// </summary>
        public bool IsFullyCovered(Dictionary<int, CachedDuration> cached, int min, int max, int requestedCapSeconds)
        {
            for (int s = min; s <= max; s++)
            {
                if (!cached.TryGetValue(s, out var c))
                    return false;
                if (!IsReliableEndReason(c.EndReason) && c.CapSeconds < requestedCapSeconds)
                    return false;
            }
            return true;
        }

        /// <summary>Upsert d'une ligne par résultat de sous-chanson.
        /// <paramref name="subsongMin"/> est le vrai numéro de sous-chanson
        /// correspondant à results[0].</summary>
        public void SaveResults(string fileMd5, string fileName, int subsongMin,
            (TimeSpan Duration, string EndReason)[] results, int capSecondsUsed)
        {
            string nowIso = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    INSERT INTO durations (file_md5, subsong, duration_seconds, end_reason, cap_seconds, file_name, scanned_at)
                    VALUES ($md5, $subsong, $seconds, $reason, $cap, $name, $scannedAt)
                    ON CONFLICT(file_md5, subsong) DO UPDATE SET
                        duration_seconds = excluded.duration_seconds,
                        end_reason = excluded.end_reason,
                        cap_seconds = excluded.cap_seconds,
                        file_name = excluded.file_name,
                        scanned_at = excluded.scanned_at;";
                var pMd5 = cmd.Parameters.Add("$md5", SqliteType.Text);
                var pSubsong = cmd.Parameters.Add("$subsong", SqliteType.Integer);
                var pSeconds = cmd.Parameters.Add("$seconds", SqliteType.Real);
                var pReason = cmd.Parameters.Add("$reason", SqliteType.Text);
                var pCap = cmd.Parameters.Add("$cap", SqliteType.Integer);
                var pName = cmd.Parameters.Add("$name", SqliteType.Text);
                var pScannedAt = cmd.Parameters.Add("$scannedAt", SqliteType.Text);

                for (int i = 0; i < results.Length; i++)
                {
                    pMd5.Value = fileMd5;
                    pSubsong.Value = subsongMin + i;
                    pSeconds.Value = results[i].Duration.TotalSeconds;
                    pReason.Value = results[i].EndReason;
                    pCap.Value = capSecondsUsed;
                    pName.Value = fileName;
                    pScannedAt.Value = nowIso;
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void Dispose() => _connection.Dispose();
    }

    /// <summary>
    /// Emplacement + instance partagée du cache de durées UADE, sur le même
    /// principe que <see cref="TempDir"/> (ExternalPlayers.cs, Override
    /// appelé depuis DemoBase.App au démarrage). Contrairement à TempDir
    /// (Working/Tracker, vidé à chaque démarrage), ce fichier doit être
    /// PERSISTANT — par défaut à côté de demobase.db (dossier "Database",
    /// jamais nettoyé), pas dans Working/.
    /// </summary>
    // Classe PUBLIQUE (contrairement à UadeDurationDatabase) : Override() est appelé
    // depuis DemoBase.App (assembly différente) au démarrage, comme
    // TrackerPlayer.Core.Players.TempDir.Override(). Instance reste internal (type de
    // retour UadeDurationDatabase lui-même internal) — seul UadePlayer (même assembly)
    // s'en sert réellement pour lire/écrire le cache.
    public static class UadeDurationCache
    {
        private static string? _override;
        private static UadeDurationDatabase? _instance;
        private static readonly object _lock = new();

        /// <summary>Surcharge le chemin du fichier (appelé depuis l'app hôte au démarrage).</summary>
        public static void Override(string path) => _override = path;

        public static string Path =>
            _override ?? System.IO.Path.Combine(AppContext.BaseDirectory, "uade_durations.db");

        internal static UadeDurationDatabase Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance ??= new UadeDurationDatabase(Path);
                }
            }
        }
    }
}
