using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>Statut d'une tentative de téléchargement.</summary>
public enum DownloadAttemptStatus
{
    SizeMismatch,   // Téléchargé mais taille ≠ DAT
    CrcMismatch,    // Taille OK mais CRC ≠ DAT
    Success,        // Tout correspond
}

public record DownloadAttempt(
    string Url,
    string FileName,
    int?   DemozooId,
    long   SizeOnServer,
    long   SizeInDat,
    string? Crc32InDat,
    DownloadAttemptStatus Status,
    DateTime AttemptedAt);

/// <summary>
/// Accès direct SQLite (sans EF) à la table DownloadAttempts.
/// Permet d'éviter de re-télécharger un fichier dont la taille ne correspond
/// pas au DAT (fichier mis à jour sur le serveur depuis la création du DAT).
/// </summary>
public class DownloadAttemptService(string connectionString)
{
    /// <summary>
    /// Enregistre (ou met à jour) le résultat d'une tentative de téléchargement.
    /// </summary>
    public async Task SaveAsync(DownloadAttempt attempt, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "DownloadAttempts"
                ("Url","FileName","DemozooId","SizeOnServer","SizeInDat","Crc32InDat","Status","AttemptedAt")
            VALUES
                (@url,@fn,@dzid,@sos,@sid,@crc,@status,@at)
            ON CONFLICT("Url") DO UPDATE SET
                "FileName"     = excluded."FileName",
                "DemozooId"    = excluded."DemozooId",
                "SizeOnServer" = excluded."SizeOnServer",
                "SizeInDat"    = excluded."SizeInDat",
                "Crc32InDat"   = excluded."Crc32InDat",
                "Status"       = excluded."Status",
                "AttemptedAt"  = excluded."AttemptedAt";
            """;
        cmd.Parameters.AddWithValue("@url",    attempt.Url);
        cmd.Parameters.AddWithValue("@fn",     attempt.FileName);
        cmd.Parameters.AddWithValue("@dzid",   (object?)attempt.DemozooId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sos",    attempt.SizeOnServer);
        cmd.Parameters.AddWithValue("@sid",    attempt.SizeInDat);
        cmd.Parameters.AddWithValue("@crc",    (object?)attempt.Crc32InDat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", attempt.Status.ToString());
        cmd.Parameters.AddWithValue("@at",     attempt.AttemptedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Retourne le statut connu pour une URL, ou null si jamais tentée.
    /// </summary>
    public async Task<DownloadAttempt?> GetAsync(string url, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "FileName","DemozooId","SizeOnServer","SizeInDat","Crc32InDat","Status","AttemptedAt"
            FROM "DownloadAttempts" WHERE "Url" = @url LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@url", url);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new DownloadAttempt(
            Url:          url,
            FileName:     reader.GetString(0),
            DemozooId:    reader.IsDBNull(1) ? null : reader.GetInt32(1),
            SizeOnServer: reader.GetInt64(2),
            SizeInDat:    reader.GetInt64(3),
            Crc32InDat:   reader.IsDBNull(4) ? null : reader.GetString(4),
            Status:       Enum.TryParse<DownloadAttemptStatus>(reader.GetString(5), out var s) ? s : DownloadAttemptStatus.SizeMismatch,
            AttemptedAt:  DateTime.TryParse(reader.GetString(6), out var d) ? d : DateTime.MinValue);
    }

    /// <summary>
    /// Toutes les tentatives en échec pour une release donnée (DemozooId).
    /// 2026-07-30, retour utilisateur : le panneau "Fichiers incompatibles avec le DAT" (et son
    /// bouton "✕ Réessayer") de ReleaseDetailViewModel cherchait les tentatives connues en les
    /// recherchant par "link.Url" (le champ brut, non résolu) — pour les link_class qui
    /// construisent leur URL réelle à partir de LinkParameter (ex. scene.org, Modland), cette
    /// clé ne correspondait JAMAIS à la véritable URL résolue utilisée comme clé de cache par
    /// ReleaseBuilderService, donc le panneau restait vide et le bouton invisible même quand un
    /// mismatch était bel et bien enregistré. Comme DownloadAttempt stocke déjà le DemozooId,
    /// on peut interroger directement par release plutôt que de deviner l'URL.
    /// </summary>
    public async Task<List<DownloadAttempt>> GetForDemozooIdAsync(int demozooId, CancellationToken ct = default)
    {
        var result = new List<DownloadAttempt>();
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Url","FileName","DemozooId","SizeOnServer","SizeInDat","Crc32InDat","Status","AttemptedAt"
            FROM "DownloadAttempts"
            WHERE "DemozooId" = @dzid AND "Status" != 'Success'
            ORDER BY "AttemptedAt" DESC;
            """;
        cmd.Parameters.AddWithValue("@dzid", demozooId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DownloadAttempt(
                Url:          reader.GetString(0),
                FileName:     reader.GetString(1),
                DemozooId:    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                SizeOnServer: reader.GetInt64(3),
                SizeInDat:    reader.GetInt64(4),
                Crc32InDat:   reader.IsDBNull(5) ? null : reader.GetString(5),
                Status:       Enum.TryParse<DownloadAttemptStatus>(reader.GetString(6), out var s) ? s : DownloadAttemptStatus.SizeMismatch,
                AttemptedAt:  DateTime.TryParse(reader.GetString(7), out var d) ? d : DateTime.MinValue));
        }
        return result;
    }

    /// <summary>Toutes les tentatives avec un problème (SizeMismatch ou CrcMismatch).</summary>
    public async Task<List<DownloadAttempt>> GetFailedAsync(CancellationToken ct = default)
    {
        var result = new List<DownloadAttempt>();
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Url","FileName","DemozooId","SizeOnServer","SizeInDat","Crc32InDat","Status","AttemptedAt"
            FROM "DownloadAttempts"
            WHERE "Status" != 'Success'
            ORDER BY "AttemptedAt" DESC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DownloadAttempt(
                Url:          reader.GetString(0),
                FileName:     reader.GetString(1),
                DemozooId:    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                SizeOnServer: reader.GetInt64(3),
                SizeInDat:    reader.GetInt64(4),
                Crc32InDat:   reader.IsDBNull(5) ? null : reader.GetString(5),
                Status:       Enum.TryParse<DownloadAttemptStatus>(reader.GetString(6), out var s) ? s : DownloadAttemptStatus.SizeMismatch,
                AttemptedAt:  DateTime.TryParse(reader.GetString(7), out var d) ? d : DateTime.MinValue));
        }
        return result;
    }

    /// <summary>Supprime toutes les tentatives échouées (pour forcer un re-essai).</summary>
    public async Task ClearFailedAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """DELETE FROM "DownloadAttempts" WHERE "Status" != 'Success';""";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// 2026-07-30, retour utilisateur ("Starstruck", tous les sets passés au vert après le
    /// correctif du early-exit) : "pourquoi ces messages en dessous ? reliquat d'avant non
    /// réinitialisés ?" — confirmé : le panneau "Fichiers incompatibles avec le DAT" affichait
    /// des mismatches enregistrés lors de tentatives précédentes (échouées), jamais nettoyés
    /// une fois le rom en question réellement trouvé par un essai ultérieur — rien ne les
    /// marquait "Success" automatiquement en dehors d'un clic explicite sur "Réessayer".
    /// Appelée après un build réussi pour chaque rom désormais satisfait (taille+CRC32 du DAT,
    /// identifiant fiable indépendant du nom — cf. mismatches de ce jour) : marque comme
    /// "Success" toute tentative antérieure concernant CE rom précis, qui n'a donc plus lieu
    /// d'être affichée comme un problème actuel.
    /// </summary>
    public async Task MarkResolvedAsync(
        int demozooId, long sizeInDat, string? crc32InDat, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "DownloadAttempts" SET "Status" = 'Success'
            WHERE "DemozooId" = @dzid AND "SizeInDat" = @size AND "Status" != 'Success'
              AND ((@crc IS NULL AND "Crc32InDat" IS NULL) OR "Crc32InDat" = @crc);
            """;
        cmd.Parameters.AddWithValue("@dzid", demozooId);
        cmd.Parameters.AddWithValue("@size", sizeInDat);
        cmd.Parameters.AddWithValue("@crc", (object?)crc32InDat ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
