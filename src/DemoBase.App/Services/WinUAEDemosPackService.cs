using System.IO;
using System.IO.Compression;

namespace DemoBase.App.Services;

/// <summary>
/// Pack "Demos" (filesystem AmigaOS de base : C, S, Devs) requis par les profils WinUAE
/// utilisant un disque dur virtuel de type "dir" (ex. uaehf0=dir,rw,Demos:Demos:.\Demos,0) —
/// le cas des démos AGA HDD-only (ex. Starstruck, The Black Lotus, cf. WinUAELauncher.
/// FallbackSingleAsync). Sans ce pack, le dossier cible ne contient ni commandes AmigaOS (C:),
/// ni Startup-Sequence (S:), ni pilotes (Devs:), et le "disque dur" virtuel ne peut pas
/// démarrer. Hébergé sur http://demobase.free.fr/DBSetup/Extras (même site que les autres
/// ressources DemoBase, dossier DBSetup\Extras\Demos.zip).
///
/// Contrairement au pack BIOS Recalbox (BiosPackService, partagé entre plusieurs émulateurs
/// et extrait dans AppPaths.Bios), ce pack est dédié à WinUAE uniquement et s'extrait
/// directement à la racine du dossier d'installation (Emus\WinUAE\), recréant
/// Emus\WinUAE\Demos\{C,S,Devs,...}.
/// </summary>
public class WinUAEDemosPackService
{
    public const string MegaFolderUrl = EmulatorConfigExportService.DbSetupBaseUrl; // même site que les autres ressources
    public const string MegaSubFolder = "Extras";
    public const string ZipFileName   = "Demos.zip";

    // Présence de ces dossiers = pack déjà installé, pas la peine de re-télécharger.
    private static readonly string[] ExpectedDirs =
    {
        Path.Combine("Demos", "C"),
        Path.Combine("Demos", "S"),
        Path.Combine("Demos", "Devs"),
    };

    /// <summary>Vrai si le pack Demos est déjà présent dans <paramref name="exeDir"/> (dossier WinUAE).</summary>
    public static bool IsInstalled(string exeDir)
    {
        try { return ExpectedDirs.All(d => Directory.Exists(Path.Combine(exeDir, d))); }
        catch { return false; }
    }

    /// <summary>
    /// Télécharge (si nécessaire) et extrait le pack Demos à la racine de <paramref name="exeDir"/>
    /// (dossier de l'émulateur WinUAE). Idempotent : ne fait rien si déjà installé.
    /// </summary>
    public async Task<(bool Success, string Message)> DownloadAndInstallAsync(
        string exeDir, CancellationToken ct = default)
    {
        try
        {
            if (IsInstalled(exeDir))
                return (true, "Pack Demos déjà présent.");

            var megaService = new DbSetupDownloadService();
            var tmpZip = Path.Combine(Path.GetTempPath(), "DemoBase_WinUAE_Demos.zip");

            var result = await megaService.DownloadFirstMatchingFileAsync(
                MegaFolderUrl, ZipFileName, tmpZip, subFolder: MegaSubFolder, ct: ct);
            if (!result.Success)
                return (false, $"Téléchargement échoué : {result.Error}");

            Directory.CreateDirectory(exeDir);
            ExtractZip(tmpZip, exeDir);
            try { File.Delete(tmpZip); } catch { }

            return IsInstalled(exeDir)
                ? (true, "Pack Demos installé (C, S, Devs).")
                : (false, "Extraction terminée mais des dossiers attendus (Demos/C, S, Devs) " +
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
