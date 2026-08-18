using System.IO;

namespace DemoBase.App.Services;

/// <summary>
/// Déploie un <c>settings.json</c> Mesen (MesenCE) pré-rempli pour éviter que l'assistant
/// "MesenCE - Emulator Configuration" (Data Storage Location / Input Mappings / Other Options)
/// ne s'affiche à chaque nouvelle installation.
///
/// Mesen n'a aucun flag CLI ni aucune clé "SetupWizardCompleted" séparée pour sauter cet
/// assistant (vérifié : le settings.json fourni par l'utilisateur, dump réel après avoir validé
/// l'assistant une fois, ne contient qu'un preset "AutomaticallyCheckForUpdates" et les mappings
/// clavier/manette déjà résolus — pas de drapeau dédié). L'assistant n'apparaît en réalité que
/// lorsqu'aucun settings.json n'existe encore à l'emplacement attendu : y déposer un fichier
/// valide à l'avance suffit donc à le sauter entièrement, exactement comme le pattern déjà en
/// place pour les NVRAM/CMOS d'Unreal Speccy (cf. UnrealSpeccyClassicBuildService).
///
/// Le fichier embarqué (Assets\Mesen_settings.json) correspond au choix "Store the data in the
/// same folder as the application" — d'après la documentation officielle de portabilité Mesen
/// (astuce "Mesen_P.exe"), ce mode stocke ses données dans un sous-dossier "Mesen" créé à côté
/// de l'exécutable. Par prudence (comportement non 100% documenté pour le choix fait via
/// l'assistant plutôt que via le suffixe _P), le fichier est déposé à la fois dans ce
/// sous-dossier ET directement à côté de l'exe : au pire l'un des deux est ignoré par Mesen,
/// jamais bloquant.
/// </summary>
public static class MesenSetupService
{
    private const string AssetName = "Mesen_settings.json";

    /// <summary>
    /// Copie le settings.json pré-rempli vers <paramref name="destDir"/> (dossier contenant
    /// Mesen.exe) si aucun settings.json n'y existe déjà — jamais destructif, jamais bloquant.
    /// </summary>
    public static void DeployPreconfiguredSettingsIfNeeded(string destDir)
    {
        try
        {
            var srcPath = Path.Combine(AppContext.BaseDirectory, "Assets", AssetName);
            if (!File.Exists(srcPath))
            {
                System.Diagnostics.Debug.WriteLine($"[MESEN] Asset introuvable : {srcPath}");
                return;
            }

            Directory.CreateDirectory(destDir);

            // Emplacement le plus probable : sous-dossier "Mesen" à côté de l'exe (mode
            // "same folder as application", même convention que le suffixe portable _P).
            var subFolderTarget = Path.Combine(destDir, "Mesen", "settings.json");
            CopyIfMissing(srcPath, subFolderTarget);

            // Filet : directement à côté de l'exe, au cas où cette version de MesenCE ne crée
            // pas le sous-dossier "Mesen" lorsque le choix est fait via l'assistant.
            var directTarget = Path.Combine(destDir, "settings.json");
            CopyIfMissing(srcPath, directTarget);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MESEN] Déploiement settings.json pré-rempli : échec — {ex.Message}");
        }
    }

    /// <summary>Ne réalisée que le déploiement pour le dossier d'émulateur "Mesen" — no-op pour
    /// tous les autres, même appel générique que BiosPackService.ConfigureEmulatorBiosIfNeeded.</summary>
    public static void DeployIfMesenFolder(string folderName, string destDir)
    {
        if (!string.Equals(folderName, "Mesen", StringComparison.OrdinalIgnoreCase)) return;
        DeployPreconfiguredSettingsIfNeeded(destDir);
    }

    private static void CopyIfMissing(string srcPath, string destPath)
    {
        if (File.Exists(destPath)) return;
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(srcPath, destPath, overwrite: false);
    }
}
