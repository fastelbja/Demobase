using System.IO;
using System.IO.Compression;

namespace DemoBase.App.Services;

/// <summary>
/// Build "classique" d'UnrealSpeccy (0.37.9 by SMT, MSVC/DirectDraw, celui qui répond
/// correctement à "-i &lt;fichier.ini&gt;" et au format .ini utilisé par UnrealSpeccyLauncher),
/// hébergé sur http://demobase.free.fr/DBSetup/Extras (même site que les autres packs DemoBase).
///
/// Pourquoi ce détour, alors que le catalogue de téléchargement (EmulatorDownloadCatalog)
/// sait déjà résoudre des releases GitHub : le seul dépôt GitHub disponible pour "Unreal
/// Speccy Portable" (djdron/UnrealSpeccyP) a entièrement abandonné ce build classique au
/// profit d'un portage SDL2 (versionné 0.0.7x/0.0.8x, sans rapport avec le "0.37.9"
/// classique) dont le support de "-i" n'est pas garanti — constaté en pratique : après un
/// re-téléchargement via DemoBase, l'émulateur ne répondait plus du tout aux arguments
/// passés. Le catalogue continue de pointer vers GitHub pour l'affichage/metadata, mais
/// EmulatorInstallerService.InstallAsync court-circuite ce chemin pour "Unreal Speccy" et
/// passe systématiquement par ce service à la place.
///
/// Contrairement à UnrealSpeccyTslPackService (pack complémentaire, non-destructif,
/// installation seulement si absent), ce service ÉCRASE systématiquement le contenu de
/// Emus\Unreal Speccy\ à chaque appel : le but explicite est de garantir que c'est TOUJOURS
/// le build classique qui est en place, y compris s'il a déjà été remplacé par erreur par
/// un build SDL2 téléchargé précédemment.
/// </summary>
public class UnrealSpeccyClassicBuildService
{
    public const string MegaFolderUrl = EmulatorConfigExportService.DbSetupBaseUrl; // même site que les autres ressources
    public const string MegaSubFolder = "Extras";
    public const string ZipFileName   = "Unreal Speccy Classic.zip";

    /// <summary>Version affichée/enregistrée dans versions.json — celle du build lui-même
    /// (bannière "UnrealSpeccy 0.37.9 by SMT and Others"), pas une version de ce pack.</summary>
    public const string Version = "0.37.9";

    /// <summary>Fichier attendu après extraction — utilisé uniquement pour le message de
    /// diagnostic post-installation (le pack est toujours ré-extrait, jamais "skip si déjà là",
    /// donc pas de vérification d'idempotence ici contrairement à UnrealSpeccyTslPackService).</summary>
    public const string ExpectedExe = "unreal_speccy_portable.exe";

    /// <summary>
    /// Télécharge et extrait (en écrasant tout fichier existant) le build classique dans
    /// <paramref name="destDir"/> (Emus\Unreal Speccy\).
    /// </summary>
    public async Task<(bool Success, string Message)> DownloadAndInstallAsync(
        string destDir, CancellationToken ct = default)
    {
        try
        {
            var megaService = new DbSetupDownloadService();
            var tmpZip = Path.Combine(Path.GetTempPath(), "DemoBase_UnrealSpeccy_Classic.zip");

            var result = await megaService.DownloadFirstMatchingFileAsync(
                MegaFolderUrl, ZipFileName, tmpZip, subFolder: MegaSubFolder, ct: ct);
            if (!result.Success)
                return (false, $"Téléchargement échoué : {result.Error}");

            Directory.CreateDirectory(destDir);
            ExtractZip(tmpZip, destDir);
            try { File.Delete(tmpZip); } catch { }

            // NVRAM/CMOS pré-configurés (cf. commentaire de classe) — évite le menu "EVO Reset
            // Service" au lancement pour les machines ATM3/TSL (firmware zxevo.rom).
            DeployPreconfiguredNvramCmos(destDir);

            return File.Exists(Path.Combine(destDir, ExpectedExe))
                ? (true, $"Build classique {Version} installé.")
                : (false, $"Extraction terminée mais {ExpectedExe} introuvable — " +
                          $"vérifier le contenu de \"{ZipFileName}\" sur le site.");
        }
        catch (OperationCanceledException) { return (false, "Téléchargement annulé."); }
        catch (Exception ex) { return (false, $"Erreur : {ex.Message}"); }
    }

    /// <summary>
    /// Copie les NVRAM/CMOS pré-configurés (Assets\UnrealSpeccy_NVRAM/_CMOS, embarqués dans
    /// DemoBase) vers <paramref name="destDir"/>\NVRAM et \CMOS — les noms exacts que
    /// unreal_speccy_portable.exe recherche dans son dossier de travail. Best-effort, jamais
    /// bloquant. Ne remplace QUE les fichiers absents : ne pas écraser un NVRAM/CMOS que
    /// l'utilisateur aurait déjà personnalisé (memory lock, CPU frequency, etc. — autres
    /// réglages du même menu "EVO Reset Service" que "Emu tape load").
    /// Appelé à l'installation (DownloadAndInstallAsync) ET en filet au lancement
    /// (UnrealSpeccyLauncher), pour couvrir aussi les installations déjà en place avant ce fix.
    /// </summary>
    public static void DeployPreconfiguredNvramCmos(string destDir)
    {
        try
        {
            Directory.CreateDirectory(destDir);
            CopyAssetIfMissing("UnrealSpeccy_NVRAM", Path.Combine(destDir, "NVRAM"));
            CopyAssetIfMissing("UnrealSpeccy_CMOS",  Path.Combine(destDir, "CMOS"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UNREAL] NVRAM/CMOS pré-configurés : échec — {ex.Message}");
        }
    }

    private static void CopyAssetIfMissing(string assetName, string destPath)
    {
        if (File.Exists(destPath)) return;
        var srcPath = Path.Combine(AppContext.BaseDirectory, "Assets", assetName);
        if (!File.Exists(srcPath))
        {
            System.Diagnostics.Debug.WriteLine($"[UNREAL] Asset introuvable : {srcPath}");
            return;
        }
        File.Copy(srcPath, destPath, overwrite: false);
    }

    private static void ExtractZip(string zipPath, string destDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // dossier

            if (entry.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(entry.FullName).StartsWith("._"))
                continue;

            var dest = Path.Combine(destDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var dir  = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            entry.ExtractToFile(dest, overwrite: true);
        }
    }
}
