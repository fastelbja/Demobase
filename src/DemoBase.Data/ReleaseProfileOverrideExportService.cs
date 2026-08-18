using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DemoBase.Data;

/// <summary>
/// Export/import JSON des overrides de profil par release (table ReleaseProfileOverrides,
/// cf. ReleaseProfileOverrideService). Même principe que EmulatorConfigExportService, mais
/// pour la table ReleaseProfileOverrides plutôt que EmulatorConfigs/EmulatorSettings.
///
/// Portable entre installations car les deux identifiants stockés sont stables :
///   - ReleaseDemozooId : identifiant Demozoo de la release, mirroré tel quel depuis le
///     dump Demozoo (DemozooImportService, table productions_production → Releases) —
///     identique sur toute installation DemoBase, contrairement à un éventuel Id local
///     auto-incrémenté qui dépendrait de l'ordre d'import.
///   - EmulatorConfigId : Id fixe des profils émulateur (catalogue seedé, cf.
///     EmulatorSeedCatalog) — également identique sur toute installation.
///
/// Un JSON exporté sur une machine peut donc être réimporté tel quel sur une autre
/// installation DemoBase : chaque override "retrouve ses petits" (la release ET le
/// profil émulateur visés existent avec les mêmes Id des deux côtés).
/// </summary>
public class ReleaseProfileOverrideExportService(string connectionString)
{
    // Mêmes conventions que EmulatorConfigExportService (DemoBase.App, cf.
    // DbSetupBaseUrl/DbSetupSubFolder) — dupliquées ici en constantes littérales plutôt que
    // référencées, car DemoBase.Data ne peut pas dépendre de DemoBase.App (dépendance
    // inverse : c'est DemoBase.App qui référence DemoBase.Data). Même sous-dossier
    // Configs, sous un nom de fichier distinct.
    //
    // 2026-08-17 : migration Mega.nz → HTTP direct sur http://demobase.free.fr/DBSetup
    // (cf. DbSetupDownloadService) — nom de fichier désormais EXACT et fixe (plus de
    // recherche par sous-chaîne, aucun listing de répertoire disponible sur le site).
    public const string DbSetupBaseUrl   = "http://demobase.free.fr/DBSetup";
    public const string DbSetupSubFolder = "Configs";
    public const string DbSetupFileName  = "release_profile_overrides.json";

    // ── Export ────────────────────────────────────────────────────────────────

    public async Task<string> ExportToJsonAsync(string outputPath)
    {
        var overrides = new List<ReleaseProfileOverrideDto>();

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT "ReleaseDemozooId", "EmulatorConfigId"
                FROM "ReleaseProfileOverrides"
                ORDER BY "ReleaseDemozooId";
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                overrides.Add(new ReleaseProfileOverrideDto
                {
                    ReleaseDemozooId = reader.GetInt32(0),
                    EmulatorConfigId = reader.GetInt32(1),
                });
            }
        }

        var export = new ReleaseProfileOverrideExport
        {
            ExportedAt = DateTime.UtcNow,
            Overrides  = overrides,
        };

        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json);
        return outputPath;
    }

    // ── Import ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Importe le JSON via INSERT OR UPDATE (idempotent, comme SetOverrideAsync).
    /// Une entrée dont l'EmulatorConfigId ne correspond à aucun profil existant sur
    /// cette installation (émulateur jamais installé, etc.) est ignorée proprement —
    /// plutôt que de laisser la contrainte FK "ON DELETE CASCADE" de la table
    /// rejeter silencieusement l'insertion.
    /// </summary>
    public async Task<(int imported, int skipped)> ImportFromJsonAsync(string jsonPath)
    {
        var json   = await File.ReadAllTextAsync(jsonPath);
        var export = JsonSerializer.Deserialize<ReleaseProfileOverrideExport>(json)
                     ?? throw new InvalidDataException("JSON invalide.");

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        int imported = 0, skipped = 0;
        foreach (var o in export.Overrides)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO "ReleaseProfileOverrides" ("ReleaseDemozooId", "EmulatorConfigId")
                SELECT @rid, @cfg
                WHERE EXISTS (SELECT 1 FROM "EmulatorConfigs" WHERE "Id" = @cfg)
                ON CONFLICT("ReleaseDemozooId") DO UPDATE SET "EmulatorConfigId" = excluded."EmulatorConfigId";
                """;
            cmd.Parameters.AddWithValue("@rid", o.ReleaseDemozooId);
            cmd.Parameters.AddWithValue("@cfg", o.EmulatorConfigId);
            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected > 0) imported++; else skipped++;
        }

        tx.Commit();
        return (imported, skipped);
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public class ReleaseProfileOverrideExport
{
    public DateTime ExportedAt { get; set; }
    public List<ReleaseProfileOverrideDto> Overrides { get; set; } = [];
}

public class ReleaseProfileOverrideDto
{
    public int ReleaseDemozooId { get; set; }
    public int EmulatorConfigId { get; set; }
}
