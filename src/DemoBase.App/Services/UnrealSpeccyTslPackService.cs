using System.IO;
using System.IO.Compression;

namespace DemoBase.App.Services;

/// <summary>
/// Pack "TS-Config / TSL" pour UnrealSpeccy — ROMs zxevo, boot.$b (autoload TR-DOS), wc.img
/// (image disque dur GEMDOS-like pour le firmware TS-Config), hébergé sur http://demobase.free.fr/DBSetup/Extras (même site
/// que les autres ressources DemoBase, dossier DBSetup\Extras\Unreal Speccy.zip).
///
/// Contrairement au pack BIOS Recalbox (BiosPackService, partagé entre plusieurs émulateurs et
/// extrait dans AppPaths.Bios), ce pack est dédié à UnrealSpeccy uniquement et s'extrait
/// directement dans son dossier d'installation (Emus\Unreal Speccy\) — UnrealSpeccy résout les
/// chemins relatifs de son .ini (rom\zxevo.rom, boot.$b, wc.img) par rapport au répertoire de
/// travail du process, qui est toujours le dossier de l'exe (cf. ProcessLaunchHelper,
/// WorkingDirectory par défaut = Path.GetDirectoryName(exePath)).
/// </summary>
public class UnrealSpeccyTslPackService
{
    public const string MegaFolderUrl = EmulatorConfigExportService.DbSetupBaseUrl; // même site que les autres ressources
    public const string MegaSubFolder = "Extras";
    public const string ZipFileName   = "Unreal Speccy.zip";

    // Présence de ces 3 fichiers = pack déjà installé, pas la peine de re-télécharger.
    private static readonly string[] ExpectedFiles =
    {
        "boot.$b",
        "wc.img",
        Path.Combine("rom", "zxevo.rom"),
    };

    /// <summary>Vrai si les fichiers attendus du pack TSL sont déjà présents dans <paramref name="exeDir"/>.</summary>
    public static bool IsInstalled(string exeDir)
    {
        try { return ExpectedFiles.All(f => File.Exists(Path.Combine(exeDir, f))); }
        catch { return false; }
    }

    /// <summary>
    /// Télécharge (si nécessaire) et extrait le pack TSL dans <paramref name="exeDir"/>
    /// (dossier de l'émulateur UnrealSpeccy). Idempotent : ne fait rien si déjà installé.
    /// </summary>
    public async Task<(bool Success, string Message)> DownloadAndInstallAsync(
        string exeDir, CancellationToken ct = default)
    {
        try
        {
            if (IsInstalled(exeDir))
                return (true, "Pack TSL déjà présent.");

            var megaService = new DbSetupDownloadService();
            var tmpZip = Path.Combine(Path.GetTempPath(), "DemoBase_UnrealSpeccy_TSL.zip");

            var result = await megaService.DownloadFirstMatchingFileAsync(
                MegaFolderUrl, ZipFileName, tmpZip, subFolder: MegaSubFolder, ct: ct);
            if (!result.Success)
                return (false, $"Téléchargement échoué : {result.Error}");

            Directory.CreateDirectory(exeDir);
            ExtractZip(tmpZip, exeDir);
            try { File.Delete(tmpZip); } catch { }

            return IsInstalled(exeDir)
                ? (true, "Pack TSL installé (roms, boot.$b, wc.img).")
                : (false, "Extraction terminée mais des fichiers attendus (roms/boot.$b/wc.img) " +
                          "restent introuvables — vérifier le contenu du ZIP sur le site.");
        }
        catch (OperationCanceledException) { return (false, "Téléchargement annulé."); }
        catch (Exception ex) { return (false, $"Erreur : {ex.Message}"); }
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
