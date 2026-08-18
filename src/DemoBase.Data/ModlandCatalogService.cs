using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

/// <summary>Une piste du catalogue Modland (une ligne de ModlandTracks).</summary>
public record ModlandTrackRow(int Id, string Format, string Author, string FileName, string Extension)
{
    /// <summary>Chemin relatif reconstituant l'URL/le cache local — mirroir exact de
    /// l'arborescence http://ftp.modland.com/pub/modules/&lt;Format&gt;/&lt;Author&gt;/&lt;FileName&gt;.</summary>
    public string RelativePath => $"{Format}/{Author}/{FileName}";
}

public record ModlandNameCount(string Name, int Count);

public record ModlandSnapshotInfo(DateTime ImportedAt, long SourceSize, int TrackCount);

/// <summary>
/// Accès direct SQLite (sans EF, même schéma que FavoriteSoundtrackService/DownloadAttemptService)
/// au catalogue Modland (2026-07-30, demande utilisateur : onglet "Musique (modland)").
///
/// Couche PUREMENT base de données — ne fait aucun appel réseau. Le téléchargement
/// d'allmods.zip et le parsing du listing texte qu'il contient sont à la charge de
/// l'appelant (DemoBase.App.Services.ModlandService), qui fournit ici les octets bruts
/// du ZIP (pour stockage/versionnage) et la liste déjà parsée des pistes à indexer.
/// </summary>
public class ModlandCatalogService(string connectionString)
{
    // ── Synchronisation (snapshot + reconstruction de l'index) ─────────────────

    /// <summary>
    /// Remplace intégralement le catalogue : la snapshot précédente (ZIP brut) et
    /// l'ancien index ModlandTracks sont supprimés puis reconstruits dans une seule
    /// transaction — pas de fusion incrémentale (modland ne fournit qu'un instantané
    /// complet, pas un delta), donc rien à préserver de l'ancien état une fois le
    /// nouveau prêt à être écrit.
    /// </summary>
    public async Task SaveSnapshotAndTracksAsync(
        byte[] zipBytes,
        IReadOnlyList<(string Format, string Author, string FileName, string Extension)> tracks,
        CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var del1 = conn.CreateCommand())
        {
            del1.Transaction = tx;
            del1.CommandText = """DELETE FROM "ModlandArchiveSnapshot";""";
            await del1.ExecuteNonQueryAsync(ct);
        }
        await using (var ins1 = conn.CreateCommand())
        {
            ins1.Transaction = tx;
            ins1.CommandText = """
                INSERT INTO "ModlandArchiveSnapshot" ("SourceSize","TrackCount","ImportedAt","ZipData")
                VALUES (@size, @count, @at, @data);
                """;
            ins1.Parameters.AddWithValue("@size",  zipBytes.LongLength);
            ins1.Parameters.AddWithValue("@count", tracks.Count);
            ins1.Parameters.AddWithValue("@at",    DateTime.UtcNow.ToString("o"));
            ins1.Parameters.AddWithValue("@data",  zipBytes);
            await ins1.ExecuteNonQueryAsync(ct);
        }

        await using (var del2 = conn.CreateCommand())
        {
            del2.Transaction = tx;
            del2.CommandText = """DELETE FROM "ModlandTracks";""";
            await del2.ExecuteNonQueryAsync(ct);
        }

        // Insertion en masse — une seule commande paramétrée réutilisée pour les ~500k
        // lignes (une transaction par ligne serait beaucoup trop lente ; SQLite absorbe
        // très bien des centaines de milliers d'INSERT dans UNE SEULE transaction).
        await using (var ins2 = conn.CreateCommand())
        {
            ins2.Transaction = tx;
            ins2.CommandText = """
                INSERT INTO "ModlandTracks" ("Format","Author","FileName","Extension")
                VALUES (@format, @author, @file, @ext);
                """;
            var pFormat = ins2.CreateParameter(); pFormat.ParameterName = "@format"; ins2.Parameters.Add(pFormat);
            var pAuthor = ins2.CreateParameter(); pAuthor.ParameterName = "@author"; ins2.Parameters.Add(pAuthor);
            var pFile   = ins2.CreateParameter(); pFile.ParameterName   = "@file";   ins2.Parameters.Add(pFile);
            var pExt    = ins2.CreateParameter(); pExt.ParameterName    = "@ext";    ins2.Parameters.Add(pExt);
            await ins2.PrepareAsync(ct);

            foreach (var t in tracks)
            {
                ct.ThrowIfCancellationRequested();
                pFormat.Value = t.Format;
                pAuthor.Value = t.Author;
                pFile.Value   = t.FileName;
                pExt.Value    = t.Extension;
                await ins2.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task<ModlandSnapshotInfo?> GetSnapshotInfoAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "ImportedAt","SourceSize","TrackCount" FROM "ModlandArchiveSnapshot"
            ORDER BY "Id" DESC LIMIT 1;
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ModlandSnapshotInfo(
            ImportedAt: DateTime.TryParse(r.GetString(0), out var d) ? d : DateTime.MinValue,
            SourceSize: r.GetInt64(1),
            TrackCount: r.GetInt32(2));
    }

    /// <summary>Octets bruts du dernier ZIP importé — permet de reparser sans
    /// retélécharger (ex. après une évolution du parseur), demande utilisateur
    /// explicite ("tu peux stocker et versionner le fichier allmods.zip [...]
    /// pour plus de souplesse").</summary>
    public async Task<byte[]?> GetLatestZipBytesAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "ZipData" FROM "ModlandArchiveSnapshot" ORDER BY "Id" DESC LIMIT 1;""";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as byte[];
    }

    // ── Navigation / recherche ──────────────────────────────────────────────────

    /// <summary>Tous les formats connus avec leur nombre de pistes — environ 200
    /// lignes, chargées en une fois (pas besoin de pagination/recherche côté SQL,
    /// le filtrage se fait en mémoire côté ViewModel).</summary>
    public async Task<List<ModlandNameCount>> GetFormatsAsync(CancellationToken ct = default)
    {
        var result = new List<ModlandNameCount>();
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Format", COUNT(*) FROM "ModlandTracks"
            GROUP BY "Format" ORDER BY "Format" COLLATE NOCASE;
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new ModlandNameCount(r.GetString(0), r.GetInt32(1)));
        return result;
    }

    /// <summary>
    /// Auteurs correspondant au filtre. <paramref name="format"/> nul = mode "Par
    /// auteur" (recherche sur l'ensemble du catalogue, potentiellement des dizaines de
    /// milliers d'auteurs — <paramref name="limit"/> s'applique, jamais chargé en une
    /// fois) ; renseigné = mode "Par format" (auteurs de CE format uniquement, un
    /// sous-ensemble borné et raisonnable même pour un gros format — <paramref
    /// name="limit"/> est alors IGNORÉ, cf. 2026-08-06 ci-dessous).
    ///
    /// 2026-08-06, retour utilisateur (capture d'écran, mode "Par format", format
    /// "Protracker", 80645 pistes) : "sur 'protracker' la liste s'arrete à 'Antiriad'
    /// et je ne peux pas aller plus bas" — la limite fixe de 300 (passée par
    /// ModlandBrowserViewModel.LoadAuthorsAsync) coupait la liste en plein milieu de
    /// l'alphabet dès qu'un format avait plus de 300 auteurs distincts, SANS aucun
    /// signal visuel (pas de "..." ni de scrollbar indiquant qu'il en manque) — les
    /// auteurs situés après le 300e alphabétique étaient purement et simplement
    /// inatteignables, y compris par la recherche (qui filtre mais n'étend pas la
    /// limite). Là où la limite avait un sens réel (mode "Par auteur" global, sans
    /// format, potentiellement des dizaines de milliers d'auteurs), la colonne Auteurs
    /// est un `ListBox` WPF standard — virtualisé par défaut (`VirtualizingStackPanel`
    /// via son propre template, contrairement à l'`ItemsControl` brut de la colonne
    /// Pistes corrigé le même jour) — donc afficher TOUS les auteurs d'un format précis
    /// (borné par construction : jamais plus que le nombre de pistes de ce format) ne
    /// présente pas le même risque de blocage. Limite conservée uniquement pour le mode
    /// "Par auteur" global, qui reste un vrai risque de volumétrie.
    ///
    /// 2026-07-31, retour utilisateur (capture d'écran, mode "Par auteur") : "il m'affiche
    /// cette liste mais pas la liste des auteurs !?" — la liste était polluée d'entrées
    /// du genre "unknown/100disk Vol. 4", "unknown/1500 DS Spirits Vol. 1..." qui ne sont
    /// PAS des noms d'auteur mais des sous-dossiers de compilation/jeu sous un auteur
    /// générique "unknown" (Modland regroupe ainsi les pistes de compositeur inconnu par
    /// jeu/compilation — cf. ModlandService.ParseListingLine, "Cas plus rare" : un chemin
    /// à 4+ segments joint TOUT le milieu en une seule valeur "Author" composite, ex.
    /// "unknown/100disk Vol. 4"). Résultat : chaque sous-dossier devenait son PROPRE
    /// "auteur" distinct au lieu d'être regroupé sous "unknown". Fix : grouper ici sur la
    /// RACINE de l'auteur (avant le premier "/" s'il y en a un) — la valeur "Author"
    /// complète (nécessaire pour reconstituer RelativePath/télécharger le bon fichier)
    /// n'est pas modifiée, seul le regroupement d'affichage change. Cf.
    /// GetTracksByAuthorAsync/GetTracksAsync ci-dessous pour le pendant (retrouver TOUTES
    /// les pistes d'un auteur racine, y compris ses sous-dossiers).
    /// </summary>
    public async Task<List<ModlandNameCount>> GetAuthorsAsync(
        string? format, string? search, int limit = 200, CancellationToken ct = default)
    {
        var result = new List<ModlandNameCount>();
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var hasFormat = !string.IsNullOrEmpty(format);

        var where = new List<string>();
        if (hasFormat) where.Add("""t."Format" = @format""");
        if (!string.IsNullOrWhiteSpace(search)) where.Add("""t."Author" LIKE @search ESCAPE '\'""");
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        // 2026-08-06 : LIMIT retiré quand un format précis est sélectionné — cf.
        // commentaire de la méthode, un format donné a un nombre d'auteurs borné et
        // raisonnable (jamais plus que son nombre de pistes), et la colonne Auteurs
        // (ListBox virtualisé) encaisse sans problème d'en afficher plusieurs milliers.
        var limitSql = hasFormat ? "" : "LIMIT @limit";

        cmd.CommandText = $"""
            SELECT
                CASE WHEN instr(t."Author", '/') > 0
                     THEN substr(t."Author", 1, instr(t."Author", '/') - 1)
                     ELSE t."Author" END AS AuthorRoot,
                COUNT(*)
            FROM "ModlandTracks" t
            {whereSql}
            GROUP BY AuthorRoot
            ORDER BY AuthorRoot COLLATE NOCASE
            {limitSql};
            """;
        if (hasFormat)
            cmd.Parameters.AddWithValue("@format", format);
        if (!string.IsNullOrWhiteSpace(search))
            cmd.Parameters.AddWithValue("@search", "%" + EscapeLike(search) + "%");
        if (!hasFormat)
            cmd.Parameters.AddWithValue("@limit", limit);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new ModlandNameCount(r.GetString(0), r.GetInt32(1)));
        return result;
    }

    /// <summary>Pistes d'un couple (Format, Auteur) précis — un auteur a rarement
    /// plus de quelques dizaines de morceaux dans un format donné, pas de limite.
    /// 2026-07-31 : <paramref name="author"/> est maintenant une racine d'auteur
    /// (cf. GetAuthorsAsync) — le WHERE couvre donc à la fois l'égalité exacte (auteurs
    /// "normaux", 3 segments) ET le préfixe "author/..." (sous-dossiers compilation/jeu
    /// d'un auteur générique comme "unknown", cf. ModlandService.ParseListingLine).</summary>
    public async Task<List<ModlandTrackRow>> GetTracksAsync(
        string format, string author, CancellationToken ct = default)
    {
        var result = new List<ModlandTrackRow>();
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id","Format","Author","FileName","Extension" FROM "ModlandTracks"
            WHERE "Format" = @format
              AND ("Author" = @author OR "Author" LIKE @authorPrefix ESCAPE '\')
            ORDER BY "FileName" COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("@format", format);
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@authorPrefix", EscapeLike(author) + "/%");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new ModlandTrackRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return result;
    }

    /// <summary>
    /// Pistes d'un auteur dans TOUS les formats (mode "Par auteur", où l'utilisateur
    /// choisit directement un auteur sans passer par un format) — un même nom
    /// d'auteur peut apparaître dans plusieurs formats (ex. quelqu'un qui a composé
    /// à la fois en MOD et en AHX), toutes ses pistes sont regroupées ici.
    /// 2026-07-31 : même élargissement égalité-ou-préfixe que GetTracksAsync ci-dessus,
    /// pour les mêmes raisons (auteur = racine, cf. GetAuthorsAsync).
    /// </summary>
    public async Task<List<ModlandTrackRow>> GetTracksByAuthorAsync(
        string author, CancellationToken ct = default)
    {
        var result = new List<ModlandTrackRow>();
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // 2026-07-31, retour utilisateur ("dans la liste des fichiers, classe les par nom
        // de fichier stp, actuellement si je choisi un auteur il les classes par
        // format/nom. je les veux juste par nom") : tri secondaire par Format retiré.
        cmd.CommandText = """
            SELECT "Id","Format","Author","FileName","Extension" FROM "ModlandTracks"
            WHERE "Author" = @author OR "Author" LIKE @authorPrefix ESCAPE '\'
            ORDER BY "FileName" COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("@author", author);
        cmd.Parameters.AddWithValue("@authorPrefix", EscapeLike(author) + "/%");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new ModlandTrackRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return result;
    }

    /// <summary>
    /// 2026-08-06, retour utilisateur ("dans le repertoire HSVC il y a un repertoire
    /// 'MUSICIANS' mais il ne fait pas apparaitre les repertoires sous ce sous
    /// repertoire") : certains auteurs "racine" (cf. GetAuthorsAsync) sont en réalité
    /// des dossiers virtuels à PLUSIEURS niveaux de profondeur — la collection HVSC
    /// mirrorée par Modland range ses pistes sous "MUSICIANS/&lt;Lettre&gt;/&lt;Artiste&gt;/fichier",
    /// donc le champ "Author" complet vaut par ex. "MUSICIANS/H/Hubbard_Rob" (3
    /// segments), pas juste "MUSICIANS". Jusqu'ici, sélectionner la racine "MUSICIANS"
    /// aplatissait D'UN COUP la totalité des pistes de TOUS les artistes de TOUTES les
    /// lettres dans la colonne Pistes (des dizaines de milliers de lignes pour HVSC) —
    /// à la fois une UX qui cache l'arborescence réelle, ET la cause probable du
    /// blocage applicatif signalé pour les listes de plus de 1000 entrées.
    ///
    /// Cette méthode retourne les segments DISTINCTS immédiatement sous
    /// <paramref name="path"/> (un niveau de plus, pas toute la profondeur restante),
    /// avec le nombre de pistes qu'ils contiennent récursivement — permet à
    /// ModlandBrowserViewModel de faire descendre l'utilisateur niveau par niveau
    /// (fil d'Ariane) au lieu de tout aplatir. Renvoie une liste VIDE quand
    /// <paramref name="path"/> est déjà un niveau "feuille" (aucun auteur n'a de
    /// segment supplémentaire après ce préfixe) — c'est ce signal que le ViewModel
    /// utilise pour savoir quand charger enfin les pistes plutôt que redescendre.
    /// </summary>
    public async Task<List<ModlandNameCount>> GetAuthorSubfoldersAsync(
        string path, string? format = null, CancellationToken ct = default)
    {
        var result = new List<ModlandNameCount>();
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var where = new List<string> { """t."Author" LIKE @prefixLike ESCAPE '\'""" };
        if (!string.IsNullOrEmpty(format)) where.Add("""t."Format" = @format""");
        var whereSql = string.Join(" AND ", where);

        // "reste" = portion du champ Author après "path/" pour toutes les pistes dont
        // Author commence STRICTEMENT par "path/" (plus profond que path lui-même) —
        // même principe de regroupement sur le premier segment que GetAuthorsAsync,
        // mais appliqué à une profondeur arbitraire au lieu de la racine.
        cmd.CommandText = $"""
            SELECT
                CASE WHEN instr(reste, '/') > 0
                     THEN substr(reste, 1, instr(reste, '/') - 1)
                     ELSE reste END AS NextSegment,
                COUNT(*)
            FROM (
                SELECT substr(t."Author", length(@prefix) + 1) AS reste
                FROM "ModlandTracks" t
                WHERE {whereSql}
            )
            GROUP BY NextSegment
            ORDER BY NextSegment COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("@prefix", path + "/");
        cmd.Parameters.AddWithValue("@prefixLike", EscapeLike(path) + "/%");
        if (!string.IsNullOrEmpty(format))
            cmd.Parameters.AddWithValue("@format", format);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new ModlandNameCount(r.GetString(0), r.GetInt32(1)));
        return result;
    }

    /// <summary>
    /// 2026-08-06, retour utilisateur ("si je choisi un repertoire qui a des sous
    /// repertoires il n'affiche pas les fichiers qui se trouvent à la racine") : un
    /// dossier virtuel composé (cf. GetAuthorSubfoldersAsync) peut contenir À LA FOIS
    /// des sous-dossiers ET des pistes placées directement à ce niveau — cas réel
    /// observé sur l'auteur générique "unknown" (pistes isolées ET sous-dossiers de
    /// compilation "unknown/&lt;jeu&gt;" au même niveau). Contrairement à
    /// GetTracksAsync/GetTracksByAuthorAsync (égalité OU préfixe, donc TOUT ce qui est
    /// en dessous), cette méthode ne renvoie QUE les pistes dont "Author" vaut EXACTEMENT
    /// <paramref name="path"/> — jamais celles d'un sous-dossier — pour pouvoir les
    /// afficher EN PLUS de la liste de sous-dossiers sans revenir à l'aplatissement
    /// complet que la navigation par fil d'Ariane cherche justement à éviter.
    /// </summary>
    public async Task<List<ModlandTrackRow>> GetTracksAtExactAuthorAsync(
        string path, string? format = null, CancellationToken ct = default)
    {
        var result = new List<ModlandTrackRow>();
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        // Alias "t." devant chaque colonne — évite qu'un littéral raw string ($""" ... """)
        // commence son contenu par un guillemet littéral (ambigu avec le délimiteur
        // d'ouverture), même principe que GetAuthorSubfoldersAsync ci-dessus.
        var where = new List<string> { """t."Author" = @author""" };
        if (!string.IsNullOrEmpty(format)) where.Add("""t."Format" = @format""");
        var whereSql = string.Join(" AND ", where);

        cmd.CommandText = $"""
            SELECT t."Id",t."Format",t."Author",t."FileName",t."Extension"
            FROM "ModlandTracks" t
            WHERE {whereSql}
            ORDER BY t."FileName" COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("@author", path);
        if (!string.IsNullOrEmpty(format))
            cmd.Parameters.AddWithValue("@format", format);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new ModlandTrackRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return result;
    }

    /// <summary>
    /// 2026-08-01, retour utilisateur ("je ne peux pas faire de recherche sur le nom
    /// du fichier dans le browser modland") : recherche par nom de fichier sur
    /// L'ENSEMBLE du catalogue (tous formats/auteurs confondus), indépendamment de la
    /// sélection courante dans les colonnes Formats/Auteurs — contrairement à
    /// GetTracksAsync/GetTracksByAuthorAsync ci-dessus, qui exigent un auteur déjà
    /// choisi. <paramref name="limit"/> par défaut à 300 : une recherche large (ex.
    /// "mod") peut matcher des dizaines de milliers de lignes sur les ~500k pistes du
    /// catalogue — même principe de plafond que GetAuthorsAsync.
    /// </summary>
    public async Task<List<ModlandTrackRow>> SearchTracksByFileNameAsync(
        string search, int limit = 300, CancellationToken ct = default)
    {
        var result = new List<ModlandTrackRow>();
        if (string.IsNullOrWhiteSpace(search)) return result;

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id","Format","Author","FileName","Extension" FROM "ModlandTracks"
            WHERE "FileName" LIKE @search ESCAPE '\'
            ORDER BY "FileName" COLLATE NOCASE
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@search", "%" + EscapeLike(search) + "%");
        cmd.Parameters.AddWithValue("@limit", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new ModlandTrackRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return result;
    }

    /// <summary>Résout une piste par Id — utilisé pour reconstruire le chemin/l'URL
    /// d'une piste Modland mise en favori (cf. FavoriteSoundtracksViewModel, Id
    /// synthétique négatif = -ModlandTrackRow.Id).</summary>
    public async Task<ModlandTrackRow?> GetTrackByIdAsync(int id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id","Format","Author","FileName","Extension" FROM "ModlandTracks"
            WHERE "Id" = @id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ModlandTrackRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
