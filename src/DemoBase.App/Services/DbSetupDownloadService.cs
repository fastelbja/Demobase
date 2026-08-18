using DemoBase.Core.Diagnostics;
using System.IO;
using System.Net.Http;

namespace DemoBase.App.Services;

/// <summary>
/// Télécharge les fichiers de mise à jour de DemoBase (DATs, configs émulateurs, paquets
/// "Extras", mises à jour de l'appli) depuis http://demobase.free.fr/DBSetup — remplace
/// l'ancien <c>MegaDownloadService</c> (API Mega.nz, package <c>CG.Web.MegaApiClient</c>).
///
/// 2026-08-17, demande utilisateur : "le logiciel a maintenant son site internet. plus
/// besoin de downloader depuis Mega.nz. pour tout ce qui a trait au repertoire DBSetup de
/// mega j'ai tout remis dans http://demobase.free.fr/DBSetup/ avec la même arborescence et
/// les mêmes noms de fichiers". Les sous-dossiers (Updates/DATS/Configs/Extras) sont
/// inchangés. Deux différences de fond avec l'ancien mécanisme Mega, actées avec
/// l'utilisateur avant ce correctif :
///
///   1. Mega.nz exigeait de charger l'arbre COMPLET du dossier partagé puis de chercher un
///      nœud par correspondance PARTIELLE de nom (les fichiers portaient une date/version
///      dans leur nom, ex. "Demobase DATs (2026-07-30).zip", "DemoBase_Update_0.2.0.zip") —
///      nécessaire car le nom exact n'était jamais connu à l'avance. http://demobase.free.fr
///      ne propose PAS de listing de répertoire (confirmé par l'utilisateur : 403/page
///      vide sur http://demobase.free.fr/DBSetup/DATS/) — impossible de reproduire une
///      recherche "par sous-chaîne" sans connaître les noms. Tous les fichiers concernés
///      portent donc maintenant un nom FIXE, réécrasé à chaque publication (le fichier
///      &lt;nom&gt;_version.txt à côté sert déjà à détecter qu'une nouvelle version existe —
///      le nom du zip/JSON lui-même n'a plus besoin de changer). Les anciens paramètres
///      "fileNameContains"/"extensionOrMatch" des méthodes ci-dessous sont donc maintenant
///      des noms de fichiers EXACTS — tous les appelants ont été mis à jour en conséquence
///      (cf. RESUME_PROJET.md pour la table complète ancien nom → nouveau nom fixe).
///
///   2. Le seul cas qui listait VRAIMENT tout un dossier sans connaître les noms à l'avance
///      (<see cref="DownloadAllMatchingFilesAsync"/>, utilisé par ConfigsUpdateService pour
///      récupérer tous les .uae/.cfg du dossier Configs) s'appuie maintenant sur un petit
///      fichier manifeste texte ("configs_files.txt", un nom de fichier par ligne, lignes
///      vides ou commençant par '#' ignorées) que l'utilisateur doit maintenir à jour dans
///      le dossier Configs de son site, à côté des .uae/.cfg eux-mêmes.
///
/// La classe garde le même rôle dans l'architecture (mêmes points d'injection DI que
/// l'ancien MegaDownloadService — AppUpdateService/ConfigsUpdateService/DatsUpdateService/
/// DatsPage/ReadyPage/SetupWizard*) mais son fonctionnement interne est un simple
/// HttpClient.GetAsync, sans dépendance Mega.
/// </summary>
public class DbSetupDownloadService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Télécharge <paramref name="baseUrl"/>/<paramref name="subFolder"/>/<paramref name="fileName"/>
    /// (nom EXACT désormais — plus de recherche par sous-chaîne, cf. commentaire de classe)
    /// vers <paramref name="destFilePath"/>. Le nom de la méthode est conservé tel quel
    /// (identique à l'ancien MegaDownloadService) pour ne pas devoir toucher la signature
    /// de tous les appelants existants.
    /// </summary>
    public async Task<DbSetupDownloadResult> DownloadFirstMatchingFileAsync(
        string baseUrl,
        string fileName,
        string destFilePath,
        string? subFolder = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl(baseUrl, subFolder, fileName);
        return await DownloadOneAsync(url, destFilePath, progress, ct, context: "DownloadFirstMatchingFileAsync");
    }

    /// <summary>
    /// Télécharge un fichier par chemin relatif complet (un ou plusieurs segments) sous
    /// <paramref name="baseUrl"/> — équivalent HTTP direct de l'ancienne navigation Mega par
    /// chemin (aucun appelant actuel ne l'utilise, conservée pour la même API publique).
    /// </summary>
    public async Task<DbSetupDownloadResult> DownloadByPathAsync(
        string baseUrl,
        string relativePath,
        string destFilePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl(baseUrl, subFolder: null, fileName: relativePath);
        return await DownloadOneAsync(url, destFilePath, progress, ct, context: "DownloadByPathAsync");
    }

    /// <summary>
    /// Télécharge tous les fichiers listés dans le manifeste
    /// "<paramref name="baseUrl"/>/<paramref name="subFolder"/>/configs_files.txt" (un nom
    /// de fichier par ligne, lignes vides ou commençant par '#' ignorées) dont le nom
    /// contient <paramref name="extensionOrMatch"/> — remplace l'ancien listing de
    /// répertoire Mega, impossible à reproduire sans autoindex (cf. commentaire de classe).
    /// Retourne le nombre de fichiers effectivement téléchargés.
    /// </summary>
    public async Task<int> DownloadAllMatchingFilesAsync(
        string baseUrl,
        string extensionOrMatch,
        string destDir,
        string? subFolder = null,
        CancellationToken ct = default)
    {
        const string ManifestFileName = "configs_files.txt";
        int count = 0;
        try
        {
            var manifestUrl = BuildUrl(baseUrl, subFolder, ManifestFileName);
            PerfLogger.Mark($"DBSETUP: DownloadAllMatchingFilesAsync('{extensionOrMatch}', subFolder='{subFolder}') — lecture du manifeste '{manifestUrl}'");

            using var resp = await _http.GetAsync(manifestUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                PerfLogger.Mark($"DBSETUP: manifeste '{ManifestFileName}' introuvable (HTTP {(int)resp.StatusCode}) — 0 fichier téléchargé. " +
                    "Vérifier qu'il existe bien à côté des .uae/.cfg sur le site.");
                return 0;
            }
            var manifestText = await resp.Content.ReadAsStringAsync(ct);
            var names = manifestText
                .Split('\n')
                .Select(l => l.Trim().TrimEnd('\r'))
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToList();

            var matches = names.Where(n => n.Contains(extensionOrMatch, StringComparison.OrdinalIgnoreCase)).ToList();
            PerfLogger.Mark($"DBSETUP: {matches.Count}/{names.Count} fichier(s) du manifeste correspondent à '{extensionOrMatch}'");

            Directory.CreateDirectory(destDir);
            foreach (var name in matches)
            {
                ct.ThrowIfCancellationRequested();
                var dest   = Path.Combine(destDir, name);
                var url    = BuildUrl(baseUrl, subFolder, name);
                var result = await DownloadOneAsync(url, dest, null, ct, context: "DownloadAllMatchingFilesAsync");
                if (result.Success) count++;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DbSetup] DownloadAllMatching error: {ex.Message}");
            PerfLogger.Mark($"DBSETUP: erreur DownloadAllMatchingFilesAsync — {ex.GetType().Name}: {ex.Message}");
        }
        return count;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static string BuildUrl(string baseUrl, string? subFolder, string fileName)
    {
        var parts = new List<string> { baseUrl.TrimEnd('/') };
        if (!string.IsNullOrEmpty(subFolder)) parts.Add(Uri.EscapeDataString(subFolder));
        // fileName peut contenir plusieurs segments (DownloadByPathAsync) — encoder segment
        // par segment (au lieu du chemin entier d'un coup) pour préserver les séparateurs.
        foreach (var seg in fileName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            parts.Add(Uri.EscapeDataString(seg));
        return string.Join('/', parts);
    }

    private static async Task<DbSetupDownloadResult> DownloadOneAsync(
        string url, string destFilePath, IProgress<double>? progress, CancellationToken ct, string context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PerfLogger.Mark($"DBSETUP: téléchargement démarré ({context}) — '{url}'");
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var error = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} — {url}";
                PerfLogger.Mark($"DBSETUP: échec ({context}) — {error}");
                return new(false, Error: error);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);
            DeleteIfExists(destFilePath);

            var totalBytes = resp.Content.Headers.ContentLength;
            await using (var httpStream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = File.Create(destFilePath))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;
                    if (totalBytes is > 0)
                        progress?.Report(100.0 * totalRead / totalBytes.Value);
                }
            }

            PerfLogger.Mark($"DBSETUP: téléchargement terminé ({context}) — '{Path.GetFileName(destFilePath)}' en {sw.Elapsed.TotalSeconds:0.0}s");
            return new(true, FileName: Path.GetFileName(destFilePath));
        }
        catch (OperationCanceledException)
        {
            return new(false, Error: "Download canceled.");
        }
        catch (Exception ex)
        {
            PerfLogger.Mark($"DBSETUP: exception ({context}) — {ex.GetType().Name}: {ex.Message}, après {sw.Elapsed.TotalSeconds:0.0}s");
            return new(false, Error: $"HTTP error: {ex.Message}");
        }
    }

    /// <summary>Supprime <paramref name="path"/> s'il existe déjà avant d'écrire par-dessus
    /// (même précaution que l'ancien MegaDownloadService — fichiers temporaires réutilisés
    /// d'un lancement à l'autre, re-téléchargements de mise à jour).</summary>
    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* fichier verrouillé → l'écriture suivante échouera avec une erreur explicite */ }
    }
}

public record DbSetupDownloadResult(bool Success, string? FileName = null, string? Error = null);
