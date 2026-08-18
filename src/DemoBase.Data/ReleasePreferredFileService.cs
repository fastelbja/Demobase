using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>
/// Mémorise, pour une release Demozoo donnée, quel fichier (DatEntry, identifié par son
/// RomPath stable) l'utilisateur a effectivement choisi de lancer — utile pour les
/// releases multi-fichier où l'auto-sélection heuristique (AutoSelectDatEntry, premier
/// fichier non-vidéo) ne reflète pas forcément le bon fichier (2026-07-25, retour
/// utilisateur : "Starstruck" Amiga AGA + Atari Falcon, 4 fichiers).
///
/// Une fois choisi (soit via le bouton "Utiliser" dans l'onglet Fichiers, soit via la
/// fenêtre de choix de fichier affichée au clic sur "Lancer" quand aucun choix explicite
/// n'a encore été fait), ce choix est mémorisé durablement — la release ne redemande plus
/// jamais quel fichier utiliser tant qu'il reste présent parmi les DatEntry de la release.
///
/// Clé = DemozooId (une seule préférence par release), valeur = RomPath, PAS DatEntry.Id
/// (non stable — cf. DatEntryProfileOverrideService pour le même raisonnement). Stocké
/// dans demobase.db, table ReleasePreferredFiles.
/// </summary>
public class ReleasePreferredFileService
{
    private readonly string _connectionString;

    public ReleasePreferredFileService(string connectionString)
        => _connectionString = connectionString;

    /// <summary>Retourne le RomPath du fichier préféré pour cette release, ou null si
    /// aucun choix n'a encore été mémorisé.</summary>
    public async Task<string?> GetPreferredFileAsync(int demozooId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "RomPath" FROM "ReleasePreferredFiles" WHERE "DemozooId"=@id;
            """;
        cmd.Parameters.AddWithValue("@id", demozooId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>Mémorise (ou remplace) le fichier préféré pour cette release.</summary>
    public async Task SetPreferredFileAsync(int demozooId, string romPath)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "ReleasePreferredFiles" ("DemozooId", "RomPath")
            VALUES (@id, @path)
            ON CONFLICT("DemozooId") DO UPDATE SET "RomPath"=excluded."RomPath";
            """;
        cmd.Parameters.AddWithValue("@id",   demozooId);
        cmd.Parameters.AddWithValue("@path", romPath);
        await cmd.ExecuteNonQueryAsync();
    }
}
