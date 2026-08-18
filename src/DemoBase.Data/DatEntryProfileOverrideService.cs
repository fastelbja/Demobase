using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>
/// Gère le réglage "profil de lancement à utiliser pour CE FICHIER précis" (une
/// release Demozoo peut être multi-plateforme ET multi-fichier — ex. Amiga AGA +
/// Atari Falcon, avec plusieurs DatEntry/variantes — un seul override par release
/// (cf. ReleaseProfileOverrideService) ne suffit pas dans ce cas : chaque fichier
/// peut viser une plateforme différente). Stocké dans demobase.db, table
/// DatEntryProfileOverrides.
///
/// Clé = (DemozooId, RomPath), PAS DatEntry.Id — ce dernier n'est pas stable
/// (DatImportService supprime/recrée les DatEntry, avec de nouveaux Id
/// auto-incrémentés, à chaque réimport du fichier DAT source concerné). RomPath
/// (chemin relatif du .zip) reste lui identique d'un import à l'autre pour un même
/// fichier réel.
/// </summary>
public class DatEntryProfileOverrideService
{
    private readonly string _connectionString;

    public DatEntryProfileOverrideService(string connectionString)
        => _connectionString = connectionString;

    /// <summary>Retourne l'Id de l'EmulatorConfig choisi pour ce fichier précis, ou null si
    /// aucun override n'est défini pour lui (→ retomber sur l'override de release, puis sur
    /// le profil par défaut de la plateforme).</summary>
    public async Task<int?> GetOverrideConfigIdAsync(int demozooId, string romPath)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "EmulatorConfigId" FROM "DatEntryProfileOverrides"
            WHERE "DemozooId"=@id AND "RomPath"=@path;
            """;
        cmd.Parameters.AddWithValue("@id", demozooId);
        cmd.Parameters.AddWithValue("@path", romPath);
        var result = await cmd.ExecuteScalarAsync();
        return result == null ? null : Convert.ToInt32(result);
    }

    /// <summary>Définit (ou retire si configId est null) l'override de profil pour ce fichier
    /// précis.</summary>
    public async Task SetOverrideAsync(int demozooId, string romPath, int? configId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        if (configId == null)
        {
            cmd.CommandText = """
                DELETE FROM "DatEntryProfileOverrides" WHERE "DemozooId"=@id AND "RomPath"=@path;
                """;
            cmd.Parameters.AddWithValue("@id", demozooId);
            cmd.Parameters.AddWithValue("@path", romPath);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO "DatEntryProfileOverrides" ("DemozooId", "RomPath", "EmulatorConfigId")
                VALUES (@id, @path, @cfg)
                ON CONFLICT("DemozooId", "RomPath") DO UPDATE SET "EmulatorConfigId"=excluded."EmulatorConfigId";
                """;
            cmd.Parameters.AddWithValue("@id",  demozooId);
            cmd.Parameters.AddWithValue("@path", romPath);
            cmd.Parameters.AddWithValue("@cfg", configId.Value);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Toutes les entrées pour une release (utilisé par la fenêtre de choix de
    /// plateforme pour savoir si CE fichier précis a déjà un profil enregistré,
    /// sans faire un aller-retour DB par fichier lors de l'affichage de la liste).
    /// </summary>
    public async Task<Dictionary<string, int>> GetOverridesForReleaseAsync(int demozooId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "RomPath", "EmulatorConfigId" FROM "DatEntryProfileOverrides" WHERE "DemozooId"=@id;
            """;
        cmd.Parameters.AddWithValue("@id", demozooId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }
}
