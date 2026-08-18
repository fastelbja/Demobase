using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DemoBase.App.Services;

public record ModlandSyncProgress(string Message, int Percent);

/// <summary>
/// Couche réseau du catalogue Modland (2026-07-30, demande utilisateur : onglet
/// "Musique (modland)") — télécharge/parse allmods.zip (bouton "Rafraîchir" manuel,
/// pas de vérification automatique au démarrage, retour utilisateur explicite) et
/// télécharge les pistes individuelles à la demande, avec cache local persistant
/// (<see cref="WorkingPaths.ModlandRoot"/>).
///
/// La couche base de données (<see cref="DemoBase.Data.ModlandCatalogService"/>) ne
/// fait, elle, aucun appel réseau — même séparation que ReleaseBuilderService
/// (App.Services, réseau) vs IReleaseService/DbContext (accès données).
/// </summary>
public class ModlandService(DemoBase.Data.ModlandCatalogService catalog)
{
    private const string RootUrl    = "http://ftp.modland.com/";
    private const string ArchiveUrl = "http://ftp.modland.com/allmods.zip";
    private const string ModulesUrl = "http://ftp.modland.com/pub/modules/";

    private static readonly HttpClient _http = BuildHttpClient();

    private static HttpClient BuildHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        return c;
    }

    // ── Synchronisation (bouton "Rafraîchir") ───────────────────────────────────

    public Task<DemoBase.Data.ModlandSnapshotInfo?> GetSnapshotInfoAsync(CancellationToken ct = default)
        => catalog.GetSnapshotInfoAsync(ct);

    /// <summary>
    /// Télécharge allmods.zip, parse le listing texte qu'il contient et remplace
    /// intégralement le catalogue local. Retourne le nombre de pistes indexées.
    /// Ne lève pas d'exception réseau vers l'appelant — celle-ci est propagée telle
    /// quelle (contrairement aux services "silencieux" comme DatsUpdateService) car
    /// ici la synchronisation est un geste EXPLICITE de l'utilisateur (bouton
    /// "Rafraîchir") qui doit voir l'échec, pas une vérification de fond.
    /// </summary>
    public async Task<int> SyncAsync(IProgress<ModlandSyncProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new("Téléchargement d'allmods.zip…", 5));

        using var resp = await _http.GetAsync(ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        await using var ms = new MemoryStream();
        await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await ms.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                {
                    var pct = 5 + (int)(50.0 * read / total.Value);
                    progress?.Report(new($"Téléchargement… {read / 1024.0 / 1024.0:F1} Mo", Math.Min(pct, 55)));
                }
            }
        }

        var zipBytes = ms.ToArray();
        progress?.Report(new("Analyse du listing…", 60));

        var tracks = await Task.Run(() => ParseAllModsZip(zipBytes), ct);
        progress?.Report(new($"Mise à jour du catalogue ({tracks.Count} pistes)…", 85));

        await catalog.SaveSnapshotAndTracksAsync(zipBytes, tracks, ct);
        progress?.Report(new("Terminé.", 100));

        return tracks.Count;
    }

    /// <summary>
    /// Parse le fichier texte contenu dans allmods.zip (un chemin par ligne, ex.
    /// "/pub/modules/AHX/451/song.ahx") en (Format, Author, FileName, Extension).
    /// Prend la plus grosse entrée du ZIP comme fichier de listing plutôt qu'un nom
    /// fixe — robuste si modland renomme le fichier interne d'une version à l'autre.
    /// </summary>
    private static List<(string Format, string Author, string FileName, string Extension)> ParseAllModsZip(byte[] zipBytes)
    {
        var result = new List<(string, string, string, string)>();

        using var ms  = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        System.Diagnostics.Debug.WriteLine(
            $"[MODLAND] allmods.zip : {zip.Entries.Count} entrée(s) — " +
            string.Join(", ", zip.Entries.Select(e => $"{e.Name}({e.Length})")));

        // 2026-07-30, retour utilisateur : ModlandTracks vide après sync — confirmé en
        // inspectant le vrai allmods.zip fourni par l'utilisateur (auparavant jamais
        // accessible depuis ce sandbox). Le fichier interne s'appelle "allmods.txt" et
        // chaque ligne a la forme "<taille en octets>\tFormat/Auteur[/sous-dossiers]/fichier"
        // (ex. "259547\tAce Tracker/505/bekiffte maschinen.am") — PAS de préfixe
        // "/pub/modules/" (l'ancienne exigence qui causait le 0 piste), mais une taille de
        // fichier + tabulation avant le chemin, non gérée du tout jusqu'ici. On préfère
        // désormais la plus grosse entrée .txt s'il y en a une, sinon la plus grosse entrée
        // tout court comme avant (robuste si modland renomme le fichier interne).
        var listingEntry = zip.Entries
            .Where(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .FirstOrDefault()
            ?? zip.Entries.OrderByDescending(e => e.Length).FirstOrDefault();
        if (listingEntry == null) return result;

        System.Diagnostics.Debug.WriteLine(
            $"[MODLAND] Fichier de listing retenu : {listingEntry.FullName} ({listingEntry.Length} octets)");

        using var entryStream = listingEntry.Open();
        using var sr = new StreamReader(entryStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        const string prefix = "/pub/modules/";
        int totalLines = 0, kept = 0;
        var sampleSkipped = new List<string>();

        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            totalLines++;
            if (line.Length == 0) continue;

            // Chaque ligne commence par "<taille en octets>\t" avant le chemin réel
            // (confirmé sur le vrai allmods.txt) — retiré ici. C'était la cause du
            // "0 piste" : le champ Format se retrouvait pollué par "<taille>\t<format>"
            // (aucune exception levée, juste des données incorrectes) tant que le très
            // ancien bug (préfixe "/pub/modules/" exigé, absent du fichier réel) ne
            // faisait pas déjà tout rejeter avant même d'arriver ici.
            var tab = line.IndexOf('\t');
            var path = (tab >= 0 ? line[(tab + 1)..] : line).Trim().Replace('\\', '/');
            if (path.Length == 0) continue;

            // Préfixe "/pub/modules/" retiré s'il est présent — jamais observé dans le
            // fichier réel, mais gardé par prudence/rétrocompatibilité (pas exigé).
            var idx = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                path = path[(idx + prefix.Length)..];
            else
                path = path.TrimStart('/');

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                if (sampleSkipped.Count < 5) sampleSkipped.Add(line);
                continue; // il faut au moins Auteur/fichier (Format seul si jamais absent)
            }

            string format, author, fileName;
            if (segments.Length >= 3)
            {
                format   = segments[0];
                fileName = segments[^1];
                // Cas standard : Format/Auteur/fichier (3 segments). Cas plus rare :
                // sous-dossiers supplémentaires entre l'auteur et le fichier — on les
                // rejoint dans "Author" plutôt que de perdre la ligne.
                author   = string.Join("/", segments[1..^1]);
            }
            else
            {
                // 2 segments seulement (pas de Format identifiable dans la ligne elle-même) —
                // dégradation gracieuse : Format="?" plutôt que de perdre la piste.
                format   = "?";
                author   = segments[0];
                fileName = segments[^1];
            }
            var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();

            result.Add((format, author, fileName, ext));
            kept++;
        }

        System.Diagnostics.Debug.WriteLine(
            $"[MODLAND] Parsing terminé : {totalLines} ligne(s) lues, {kept} piste(s) retenues" +
            (sampleSkipped.Count > 0 ? $" — exemples ignorés : {string.Join(" | ", sampleSkipped)}" : ""));

        return result;
    }

    // ── Téléchargement d'une piste (lecture directe + favoris) ──────────────────

    /// <summary>
    /// Retourne le chemin local d'une piste — déjà en cache (téléchargée lors d'une
    /// écoute précédente) ou fraîchement téléchargée depuis modland.com. Le cache est
    /// persistant (<see cref="WorkingPaths.ModlandRoot"/>, jamais purgé), sous la MÊME
    /// arborescence relative que le site (retour utilisateur, choix explicite : cache
    /// local plutôt que retéléchargement à chaque lecture).
    /// </summary>
    public Task<string> DownloadTrackAsync(DemoBase.Data.ModlandTrackRow track, CancellationToken ct = default)
        => DownloadByRelativePathAsync(track.RelativePath, ct);

    /// <summary>
    /// Même téléchargement, à partir d'un chemin relatif "Format/Auteur/fichier" brut
    /// (pas besoin de résoudre la piste en base) — utilisé pour rejouer un favori
    /// Modland (FavoriteSoundtrack.ZipPath stocke directement ce chemin relatif,
    /// cf. FavoriteSoundtracksViewModel.BuildPlaylistAsync).
    ///
    /// 2026-07-30, retour utilisateur : "tout comme les releases, le format tmfx a
    /// besoin qu'on télécharge les 2 fichiers pour être joué. l'un sans l'autre on
    /// peut rien faire." — même contrainte que côté DAT (cf.
    /// ReleaseViewModels.CompanionFilePairs/ResolveCompanionFiles) : un "mdat.xxx"
    /// (TFMX) n'est jouable qu'accompagné du "smpl.xxx" correspondant, cherché par
    /// UADE (UadePlayer.SetCwdToFileDir, TrackerPlayer.Core — résolution nativement
    /// via le répertoire courant du process depuis le 2026-08-06) dans le MÊME
    /// dossier. Sur Modland,
    /// mdat.xxx et smpl.xxx sont deux pistes DISTINCTES du catalogue (même
    /// Format/Auteur, juste un préfixe de nom différent) — donc quand on
    /// télécharge un mdat.*, son compagnon smpl.* est téléchargé EN PLUS,
    /// silencieusement, dans le même dossier de cache, pour qu'il soit déjà présent
    /// au moment où UADE le cherchera (pas besoin que l'utilisateur clique dessus
    /// séparément dans la liste des pistes).
    ///
    /// 2026-07-31, retour utilisateur : "pour modland, tout comme le tfmx, le format
    /// thomas hermann a besoin de 2 fichiers : smp.x et thm.x" — même mécanisme,
    /// généralisé via TrackerPlayer.Core.Players.UadeCompanionFormats (liste partagée
    /// avec la lecture UADE elle-même, cf. son commentaire) plutôt que de dupliquer un
    /// second bloc "if (fileName.StartsWith(...))" spécifique à THM ici.
    /// </summary>
    public async Task<string> DownloadByRelativePathAsync(string relativePath, CancellationToken ct = default)
    {
        var localPath = await DownloadSingleFileAsync(relativePath, ct);

        var fileName = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segs
            ? segs[^1] : relativePath;
        if (TrackerPlayer.Core.Players.UadeCompanionFormats.Match(fileName) is { } pair)
        {
            var suffix = fileName[pair.MainPrefix.Length..];
            var companionSegments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            companionSegments[^1] = pair.CompanionPrefix + suffix;
            var companionRelativePath = string.Join("/", companionSegments);
            try
            {
                await DownloadSingleFileAsync(companionRelativePath, ct);
            }
            catch (Exception ex)
            {
                // Non bloquant : la piste principale reste retournée telle quelle, la
                // lecture échouera juste (comme avant ce correctif) si le compagnon
                // n'existe vraiment pas sur modland.com pour ce morceau.
                System.Diagnostics.Debug.WriteLine(
                    $"[MODLAND] Compagnon '{companionRelativePath}' introuvable/échec de téléchargement : {ex.Message}");
            }
        }

        return localPath;
    }

    /// <summary>Téléchargement d'UN SEUL fichier Modland (cache-check → HTTP GET →
    /// écriture atomique) — factorisé pour être appelé à la fois pour la piste
    /// demandée et pour son éventuel compagnon TFMX (cf.
    /// <see cref="DownloadByRelativePathAsync"/> ci-dessus).</summary>
    private async Task<string> DownloadSingleFileAsync(string relativePath, CancellationToken ct)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
            throw new ArgumentException($"Chemin Modland invalide : '{relativePath}'", nameof(relativePath));

        var localPath = System.IO.Path.Combine(
            WorkingPaths.ModlandRoot,
            System.IO.Path.Combine(segments.Select(SanitizeSegment).ToArray()));

        if (System.IO.File.Exists(localPath) && new System.IO.FileInfo(localPath).Length > 0)
            return localPath;

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(localPath)!);

        var url = ModulesUrl + string.Join("/", segments.Select(Uri.EscapeDataString));
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        var tmpPath = localPath + ".part";
        await System.IO.File.WriteAllBytesAsync(tmpPath, bytes, ct);
        System.IO.File.Move(tmpPath, localPath, overwrite: true);

        return localPath;
    }

    /// <summary>Neutralise les caractères interdits dans un nom de fichier/dossier
    /// Windows (certains formats/auteurs Modland en contiennent, ex. "AY/YM").</summary>
    private static string SanitizeSegment(string segment)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return invalid.Aggregate(segment, (current, c) => current.Replace(c, '_'));
    }
}
