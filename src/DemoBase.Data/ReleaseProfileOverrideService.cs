using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>
/// Gère le réglage "profil de lancement à utiliser pour CETTE release", à la place
/// du profil par défaut de la plateforme. Stocké dans config.db (table
/// ReleaseProfileOverrides) et NON dans demobase.db : cette dernière est régénérée
/// par les imports/mises à jour Demozoo, un champ ajouté directement sur Release y
/// serait perdu à chaque rafraîchissement. Clé = DemozooId de la release (identifiant
/// stable), pas son Id interne.
/// </summary>
public class ReleaseProfileOverrideService
{
    private readonly string _connectionString;

    public ReleaseProfileOverrideService(string connectionString)
        => _connectionString = connectionString;

    /// <summary>Retourne l'Id de l'EmulatorConfig choisi pour cette release, ou null si
    /// aucun override n'est défini (→ utiliser le profil par défaut de la plateforme).</summary>
    public async Task<int?> GetOverrideConfigIdAsync(int demozooId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "EmulatorConfigId" FROM "ReleaseProfileOverrides" WHERE "ReleaseDemozooId"=@id;""";
        cmd.Parameters.AddWithValue("@id", demozooId);
        var result = await cmd.ExecuteScalarAsync();
        return result == null ? null : Convert.ToInt32(result);
    }

    /// <summary>Définit (ou retire si configId est null) l'override de profil pour cette
    /// release.</summary>
    public async Task SetOverrideAsync(int demozooId, int? configId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        if (configId == null)
        {
            cmd.CommandText = """DELETE FROM "ReleaseProfileOverrides" WHERE "ReleaseDemozooId"=@id;""";
            cmd.Parameters.AddWithValue("@id", demozooId);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO "ReleaseProfileOverrides" ("ReleaseDemozooId", "EmulatorConfigId")
                VALUES (@id, @cfg)
                ON CONFLICT("ReleaseDemozooId") DO UPDATE SET "EmulatorConfigId"=excluded."EmulatorConfigId";
                """;
            cmd.Parameters.AddWithValue("@id",  demozooId);
            cmd.Parameters.AddWithValue("@cfg", configId.Value);
        }
        await cmd.ExecuteNonQueryAsync();
    }
}
