using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>
/// Applique les traductions françaises sur la table ReleaseTypes.
/// Peut être appelé après un import Demozoo ou depuis les Préférences.
/// Idempotent : les noms déjà traduits ne sont pas retouchés.
/// </summary>
public static class ReleaseTypeTranslationService
{
    private static readonly (string En, string Fr)[] Translations =
    [
        ("100K Intro",               "Intro 100K"),
        ("128b Intro",               "Intro 128o"),
        ("16K Intro",                "Intro 16K"),
        ("16b intro",                "Intro 16o"),
        ("1K Executable Graphics",   "Graphique Exécutable 1K"),
        ("1K Intro",                 "Intro 1K"),
        ("256b Executable Graphics", "Graphique Exécutable 256o"),
        ("256b Intro",               "Intro 256o"),
        ("2K Intro",                 "Intro 2K"),
        ("32K Executable Music",     "Musique Exécutable 32K"),
        ("32K Intro",                "Intro 32K"),
        ("32b Intro",                "Intro 32o"),
        ("3D Model",                 "Modèle 3D"),
        ("40k Intro",                "Intro 40K"),
        ("4K Executable Graphics",   "Graphique Exécutable 4K"),
        ("4K Intro",                 "Intro 4K"),
        ("512b Intro",               "Intro 512o"),
        ("64K Executable Music",     "Musique Exécutable 64K"),
        ("64K Intro",                "Intro 64K"),
        ("64b Intro",                "Intro 64o"),
        ("8K Intro",                 "Intro 8K"),
        ("8b intro",                 "Intro 8o"),
        ("96K Intro",                "Intro 96K"),
        ("ASCII Collection",         "Collection ASCII"),
        ("Chip Music Pack",          "Pack de Musiques Chip"),
        ("Code Challenge",           "Défi de code"),
        ("Demo",                     "Démo"),
        ("Executable Graphics",      "Graphique Exécutable"),
        ("Executable Music",         "Musique Exécutable"),
        ("Game",                     "Jeu"),
        ("Graphics",                 "Graphique"),
        ("Music",                    "Musique"),
        ("Music Pack",               "Pack de Musiques"),
        ("Report",                   "Rapport"),
        ("Streaming Music",          "Musiques"),
        ("Tool",                     "Outil"),
        ("Tracked Music",            "Musique Trackers"),
        ("Video",                    "Vidéo"),
    ];

    /// <summary>Applique les traductions via une connexion SQLite déjà ouverte.</summary>
    public static async Task ApplyAsync(SqliteConnection conn, string language = "fr", CancellationToken ct = default)
    {
        // En français : EN → FR. En anglais : FR → EN (sens inverse).
        bool isFr = language.Equals("fr", StringComparison.OrdinalIgnoreCase);
        foreach (var (en, fr) in Translations)
        {
            var from = isFr ? en : fr;
            var to   = isFr ? fr : en;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE "ReleaseTypes" SET "Name" = @to
                WHERE "Name" = @from
                """;
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to",   to);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Applique les traductions via une chaîne de connexion.</summary>
    public static async Task ApplyAsync(string connectionString, string language = "fr", CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await ApplyAsync(conn, language, ct);
    }
}
