using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings CPCEC ──────────────────────────────────────────────────

public static class CpcecSettings
{
    public const string MachineModel = "machine_model"; // "0"=464 "1"=664 "2"=6128(défaut) "3"=Plus/GX4000
    public const string Ram          = "ram";            // "0"=64K "1"=128K(défaut) "2"=192K "3"=320K
                                                            // "4"=576K "5"=1088K "6"=2112K — valeurs CPCEC -kX
    public const string FullScreen   = "fullscreen";      // "true" / "false"
    public const string Indicators   = "indicators";      // "true" / "false" — disquette/cassette/
                                                            // oscilloscope audio à l'écran (-o/-O)
    public const string CrtcType     = "crtc_type";        // "0".."4" — type de CRTC, défaut "1" (-gX)
}

// ─── Lanceur CPCEC (Amstrad CPC 464/664/6128/Plus) ───────────────────────────
// Comme Altirra/Hatari, CPCEC ne nécessite pas de fichier de config généré :
// tout se pilote en ligne de commande (cf. cpcec.txt, section "Configuration
// and files"). Pas de ROM/firmware à configurer non plus : contrairement au
// Kickstart WinUAE ou à la TOS Hatari, les .rom de CPCEC (cpc464.rom,
// cpc6128.rom, cpcplus.rom...) doivent être copiés dans le même dossier que
// l'exécutable et sont chargés automatiquement par CPCEC lui-même — cf. doc :
// "All these files must be copied in a single directory".

public class CpcecLauncher
{
    private readonly PreferencesService _prefs;

    // CPCEC reconnaît directement CDT/CPR/CSW/DSK/SNA/WAV/ROM en argument positionnel
    // (cf. cpcec.txt : "the emulator will try opening and running the specified
    // files"). DSK (disquette) est de loin le format le plus courant pour les
    // releases de la scène CPC — priorité dessus comme pour les autres lanceurs.
    private static readonly string[] DiskExtensions  = [".dsk"];
    private static readonly string[] OtherExtensions = [".cdt", ".csw", ".wav", ".sna", ".cpr", ".rom"];

    // Fichiers compagnons jamais lancés (présents dans beaucoup de ZIP de la scène à
    // côté du vrai programme : readme, nfo, diz, images de présentation...) — exclus
    // du tout dernier repli pour ne pas risquer de les passer à CPCEC par erreur.
    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public CpcecLauncher(PreferencesService prefs)
        => _prefs = prefs;

    // ─── Lancement principal ──────────────────────────────────────────────────

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[CPCEC] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        // 1. Vérifier l'exe
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"CPCEC introuvable : {emulator.ExecutablePath}");

        // 2. Vérifier le fichier
        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[CPCEC] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        // 3. Si ZIP → repérer le fichier à lancer (priorité .dsk, sinon autre format
        //    reconnu, sinon premier fichier non-compagnon). Pas de gestion multi-disque
        //    dédiée : contrairement à WinUAE/Hatari/Altirra, CPCEC n'expose pas de flag
        //    --disk-a/--disk-b ou équivalent en ligne de commande — une release
        //    multi-disquettes ne montera que la première ici ; le changement de
        //    disquette en cours de route se fait depuis l'interface de CPCEC lui-même.
        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[CPCEC] ZIP extrait → fichier choisi : {actualFile}");
        }

        // 4. Déterminer le modèle de machine (CPC464/664/6128/Plus)
        var model = DetectModel(release, settings);
        System.Diagnostics.Debug.WriteLine($"[CPCEC] Modèle détecté : -m{model}");

        // 5. Construire la ligne de commande
        var args = BuildArguments(config, settings, model, actualFile);
        // 6. Lancer CPCEC
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "CPCEC", friendlyName: "Cpcec");
    }

    // ─── Détection du modèle de machine ───────────────────────────────────────

    private static string DetectModel(Release release, Dictionary<string, string?> settings)
    {
        // 1. Setting manuel prioritaire (valeur déjà "propre" : "0"/"1"/"2"/"3", cf.
        //    Tag des ComboBoxItem dans CpcecSettingsControl)
        if (settings.TryGetValue(CpcecSettings.MachineModel, out var manual)
            && !string.IsNullOrWhiteSpace(manual))
            return manual;

        // 2. Détection depuis les plateformes de la release
        var platformNames = release.ReleasePlatforms
            .Where(rp => rp.Platform != null)
            .Select(rp => rp.Platform!.Name.ToLowerInvariant())
            .ToList();

        if (platformNames.Any(p => p.Contains("plus") || p.Contains("gx4000")))
            return "3";
        if (platformNames.Any(p => p.Contains("664")))
            return "1";
        if (platformNames.Any(p => p.Contains("464")))
            return "0";

        // Défaut : CPC6128, le modèle par défaut de CPCEC lui-même et le plus répandu
        // dans la scène demo/jeu (mémoire suffisante pour la quasi-totalité du catalogue).
        return "2";
    }

    // ─── Construction de la ligne de commande ─────────────────────────────────

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string model, string file)
    {
        var sb = new StringBuilder();

        sb.Append($"-m{model}");

        var ram = settings.GetValueOrDefault(CpcecSettings.Ram);
        sb.Append($" -k{(string.IsNullOrWhiteSpace(ram) ? "1" : ram)}");

        // Type de CRTC : certaines productions de la scène CPC exigent un CRTC précis pour
        // s'afficher correctement (cf. cpcec.txt — "PhX" pour CRTC0, "Madness Demo"/"S&KOH"
        // pour CRTC1, "Eerie Forest" pour CRTC3...). Défaut CPCEC lui-même : type 1.
        var crtc = settings.GetValueOrDefault(CpcecSettings.CrtcType);
        sb.Append($" -g{(string.IsNullOrWhiteSpace(crtc) ? "1" : crtc)}");

        // Toujours explicite (plein écran OU fenêtré), jamais implicite : un .cpcecrc
        // local déjà présent sur la machine de l'utilisateur (CPCEC mémorise son
        // dernier état entre deux lancements) pourrait sinon imposer un mode différent
        // de celui choisi dans le profil DemoBase, silencieusement.
        sb.Append(settings.GetValueOrDefault(CpcecSettings.FullScreen) == "true" ? " -W" : " -+");

        // Indicateurs à l'écran (état disquette/cassette + oscilloscope audio depuis une
        // version récente de CPCEC, cf. cpcec.txt) : affichés par défaut chez CPCEC lui-même
        // (-o), masqués ici par défaut côté DemoBase (-O) — cf. demande utilisateur. Toujours
        // explicite, comme fullscreen, pour ne pas dépendre d'un .cpcecrc local existant.
        sb.Append(settings.GetValueOrDefault(CpcecSettings.Indicators) == "true" ? " -o" : " -O");

        sb.Append($" \"{file}\"");

        // ── Ligne de commande additionnelle définie sur le profil (optionnelle) ─
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
        {
            var extra = EmulatorLaunchService.SubstituteVars(config.CommandLine, file);
            sb.Append(' ').Append(extra);
        }

        return sb.ToString();
    }

    // ─── Extraction ZIP (même esprit que les autres lanceurs, mais un seul fichier
    //     retourné — pas de multi-disque côté CPCEC) ───────────────────────────

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("cpc", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité disquette (.dsk), sinon un autre format directement reconnu par
        // CPCEC, sinon n'importe quel fichier qui n'est pas un compagnon évident.
        var disk = files.FirstOrDefault(f => DiskExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (disk != null) return disk;

        var other = files.FirstOrDefault(f => OtherExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (other != null) return other;

        return files.FirstOrDefault(f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
