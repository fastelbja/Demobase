using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Altirra ────────────────────────────────────────────────

public static class AltirraSettings
{
    public const string HardwareModel = "hardware_model"; // "800" | "800xl" | "5200"
    public const string VideoStandard = "video_standard"; // "pal" | "ntsc"
    public const string BasicEnabled  = "basic_enabled";  // "true" / "false"
    public const string Artifacting   = "artifacting";    // "none" | "ntsc" | "ntschi" | "pal" | "palhi"
    public const string FullScreen    = "fullscreen";     // "true" / "false"
    public const string NoBorderless  = "no_borderless";  // "true" / "false"
}

// ─── Lanceur Altirra (Atari 400/800/XL/XE/5200) ──────────────────────────────
// Contrairement à WinUAE, Altirra ne nécessite pas de fichier de config généré :
// tout se pilote entièrement par ligne de commande (cf. Altirra.exe /?).

public class AltirraLauncher
{
    private readonly PreferencesService _prefs;

    // Extensions reconnues par type de média Altirra (utilisées pour le routage
    // vers le bon switch ET pour choisir le fichier prioritaire dans un ZIP).
    private static readonly string[] DiskExtensions = [".atr", ".xfd", ".dcm", ".atx", ".pro", ".dsk"];
    private static readonly string[] CartExtensions = [".car", ".rom", ".bin", ".a52"];
    private static readonly string[] TapeExtensions = [".cas"];
    private static readonly string[] BasExtensions   = [".bas"];
    private static readonly string[] RunExtensions   = [".com", ".xex", ".exe"];

    public AltirraLauncher(PreferencesService prefs)
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
            $"[ALTIRRA] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        // 1. Vérifier l'exe
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Altirra introuvable : {emulator.ExecutablePath}");

        // 2. Vérifier le fichier
        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[ALTIRRA] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        // 3. Si ZIP → repérer toutes les disquettes présentes (releases multi-disk),
        //    sinon retomber sur le fichier prioritaire habituel (cart > tape > bas > exe)
        var diskFiles  = new List<string>();
        string? single = null;
        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            (diskFiles, single) = await ExtractUsableFilesAsync(romPath, configDir, release.Id);
            actualFile = diskFiles.Count > 0 ? diskFiles[0] : (single ?? romPath);

            if (diskFiles.Count > 1)
                System.Diagnostics.Debug.WriteLine(
                    $"[ALTIRRA] ZIP multi-disk : {diskFiles.Count} disquettes trouvées (ordre de montage D1:, D2:, ... selon le nom de fichier) : {string.Join(", ", diskFiles.Select(Path.GetFileName))}");
            else
                System.Diagnostics.Debug.WriteLine($"[ALTIRRA] ZIP extrait → fichier choisi : {actualFile}");
        }

        // 4. Déterminer le modèle de machine (800 / 800xl / 5200)
        var hardware = DetectHardware(release, settings);
        System.Diagnostics.Debug.WriteLine($"[ALTIRRA] Hardware détecté : {hardware}");

        // 5. Construire la ligne de commande
        var args = BuildArguments(config, settings, hardware, diskFiles, actualFile);
        // 6. Lancer Altirra
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "ALTIRRA", friendlyName: "Altirra");
    }

    // ─── Détection du modèle de machine ───────────────────────────────────────

    private static string DetectHardware(Release release, Dictionary<string, string?> settings)
    {
        // 1. Setting manuel prioritaire (valeur déjà "propre" : "800" / "800xl" / "5200",
        //    cf. AltirraSettingsControl — Tag du ComboBox, pas le texte affiché)
        if (settings.TryGetValue(AltirraSettings.HardwareModel, out var manual)
            && !string.IsNullOrWhiteSpace(manual))
            return manual;

        // 2. Détection depuis les plateformes de la release
        var platformNames = release.ReleasePlatforms
            .Where(rp => rp.Platform != null)
            .Select(rp => rp.Platform!.Name.ToLowerInvariant())
            .ToList();

        if (platformNames.Any(p => p.Contains("5200")))
            return "5200";
        if (platformNames.Any(p => p.Contains("800xl") || p.Contains("xe") || p.Contains("xl")))
            return "800xl";

        // Défaut : Atari 800 (48K, OS-B)
        return "800";
    }

    // ─── Construction de la ligne de commande ─────────────────────────────────

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings,
        string hardware, List<string> diskFiles, string file)
    {
        var sb = new StringBuilder();

        // Repart d'un état matériel par défaut à chaque lancement, pour ne pas
        // hériter d'un réglage laissé par une session précédente dans Altirra.
        sb.Append("/baseline");

        sb.Append($" /hardware:{hardware}");

        var video = settings.GetValueOrDefault(AltirraSettings.VideoStandard) ?? "pal";
        sb.Append(video.Equals("ntsc", StringComparison.OrdinalIgnoreCase) ? " /ntsc" : " /pal");

        var basicOn = settings.GetValueOrDefault(AltirraSettings.BasicEnabled) == "true";
        sb.Append(basicOn ? " /basic" : " /nobasic");

        var artifact = settings.GetValueOrDefault(AltirraSettings.Artifacting);
        if (!string.IsNullOrWhiteSpace(artifact) && artifact != "none")
            sb.Append($" /artifact:{artifact}");

        sb.Append(settings.GetValueOrDefault(AltirraSettings.FullScreen) == "true" ? " /f" : " /w");

        var noBorderless = settings.GetValueOrDefault(AltirraSettings.NoBorderless) == "true";
        if (noBorderless) sb.Append(" /noborderless");

        // Chaque release testée doit ouvrir sa propre instance, plutôt que de
        // réutiliser une fenêtre Altirra déjà ouverte (qui aurait gardé l'état —
        // et le média monté — de la release précédente).
        sb.Append(" /nosingleinstance");

        // ── Montage du média ────────────────────────────────────────────────────
        // Releases multi-disk (plusieurs .atr/.xfd/... dans le ZIP) : un /disk par
        // disquette, dans l'ordre du nom de fichier — Altirra monte chaque /disk
        // supplémentaire sur le lecteur suivant (D1:, D2:, D3:, ...).
        if (diskFiles.Count > 0)
        {
            foreach (var disk in diskFiles)
                sb.Append($" /disk \"{disk}\"");
        }
        else
        {
            // Cas normal (un seul fichier) : switch choisi selon l'extension
            var ext = Path.GetExtension(file).ToLowerInvariant();
            string mountSwitch;
            if (DiskExtensions.Contains(ext))      mountSwitch = "/disk";
            else if (CartExtensions.Contains(ext)) mountSwitch = "/cart";
            else if (TapeExtensions.Contains(ext)) mountSwitch = "/tape";
            else if (BasExtensions.Contains(ext))  mountSwitch = "/runbas";
            else if (RunExtensions.Contains(ext))  mountSwitch = "/run";
            else                                    mountSwitch = "/disk";  // par défaut : tenter comme disquette
            sb.Append($" {mountSwitch} \"{file}\"");
        }

        // ── Ligne de commande additionnelle définie sur le profil (optionnelle) ─
        // Le profil par défaut créé par "+ Profil" vaut "{file}" — on l'ignore
        // ici puisque le fichier est déjà pris en charge par le switch ci-dessus.
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
        {
            var extra = EmulatorLaunchService.SubstituteVars(config.CommandLine, file);
            sb.Append(' ').Append(extra);
        }

        return sb.ToString();
    }

    // ─── Extraction ZIP (même logique que WinUAELauncher.ExtractFirstUsableSync,
    //     adaptée aux extensions Atari 8-bit, + détection multi-disk) ──────────
    // Retourne :
    //   - diskFiles : TOUTES les images disquette trouvées dans le ZIP (triées par
    //     nom de fichier — couvre le cas usuel "disk1.atr"/"disk2.atr" où l'ordre
    //     alphabétique correspond à l'ordre de montage voulu). Vide s'il n'y a
    //     aucune disquette dans le ZIP.
    //   - singleFile : fichier de repli si diskFiles est vide (priorité
    //     cart > tape > bas > exe > autre), comme avant. Null si diskFiles non vide.

    private static Task<(List<string> diskFiles, string? singleFile)> ExtractUsableFilesAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFilesSync(zipPath, outDir, releaseId));

    private static (List<string> diskFiles, string? singleFile) ExtractUsableFilesSync(string zipPath, string outDir, int releaseId)
    {
        // Dossier court (Id) plutôt que le titre complet — cf. bug MAX_PATH constaté.
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("atari8", releaseId, zipPath));
        Directory.CreateDirectory(extractDir);

        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Toutes les disquettes du ZIP, triées par nom de fichier (disk1 avant disk2)
        var disks = files
            .Where(f => DiskExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (disks.Count > 0)
            return (disks, null);

        // Pas de disquette : priorité cartouche > cassette > BASIC > exécutable > autres
        var single = files.FirstOrDefault(f => CartExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault(f => TapeExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault(f => BasExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault(f => RunExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault();

        return ([], single);
    }
}
