using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings MSXEC ──────────────────────────────────────────────────

public static class MsxecSettings
{
    public const string MachineModel = "machine_model"; // "0"=MSX1 "1"=MSX2 "2"=MSX2+
    public const string Ram          = "ram";            // "0".."6" — -kX
    public const string FullScreen   = "fullscreen";      // "true" / "false"
    public const string Indicators   = "indicators";      // "true" / "false" (-o/-O)
}

// ─── Lanceur MSXEC (MSX/MSX2/MSX2+) ──────────────────────────────────────────
// Frère de CPCEC (même moteur). Différences documentées (cf. cpcec.txt, section
// "MSXEC") prises en compte : seulement 3 modèles (-m0/-m1/-m2, pas de -m3) ;
// RAM -k0..-k6 mais plafond 2048K (pas 2112K comme CPCEC/CSFEC), avec une
// nuance propre à MSXEC sur les deux premiers paliers — -k0 = 64K SANS mapper
// RAM (compatible avec des jeux pré-MSX2), -k1 = 64K AVEC mapper RAM (attendu
// sur MSX2) ; cassettes en WAV/CSW/TSX/CAS plutôt que CDT (extension absente
// de la doc MSXEC). Pas de réglage type CRTC : MSXEC utilise un TMS9918/V9938,
// pas de CRTC Hitachi, et la doc ne mentionne aucun usage de -gX pour MSXEC.

public class MsxecLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly string[] DiskExtensions  = [".dsk"];
    private static readonly string[] OtherExtensions = [".wav", ".csw", ".tsx", ".cas", ".rom"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public MsxecLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[MSXEC] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"MSXEC introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[MSXEC] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[MSXEC] ZIP extrait → fichier choisi : {actualFile}");
        }

        var model = DetectModel(release, settings);
        System.Diagnostics.Debug.WriteLine($"[MSXEC] Modèle détecté : -m{model}");

        var args = BuildArguments(config, settings, model, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "MSXEC", friendlyName: "Msxec");
    }

    private static string DetectModel(Release release, Dictionary<string, string?> settings)
    {
        if (settings.TryGetValue(MsxecSettings.MachineModel, out var manual)
            && !string.IsNullOrWhiteSpace(manual))
            return manual;

        var platformNames = release.ReleasePlatforms
            .Where(rp => rp.Platform != null)
            .Select(rp => rp.Platform!.Name.ToLowerInvariant())
            .ToList();

        if (platformNames.Any(p => p.Contains("msx2+") || p.Contains("msx 2+") || p.Contains("msx2p")))
            return "2";
        if (platformNames.Any(p => p.Contains("msx2") || p.Contains("msx 2")))
            return "1";
        if (platformNames.Any(p => p.Contains("msx1") || p.Contains("msx 1")))
            return "0";

        // Défaut : MSX2 — bon compromis entre compatibilité large et capacités graphiques
        // (V9938) que ciblent la plupart des productions de la scène MSX.
        return "1";
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string model, string file)
    {
        var sb = new StringBuilder();

        sb.Append($"-m{model}");

        var ram = settings.GetValueOrDefault(MsxecSettings.Ram);
        sb.Append($" -k{(string.IsNullOrWhiteSpace(ram) ? "1" : ram)}");

        sb.Append(settings.GetValueOrDefault(MsxecSettings.FullScreen) == "true" ? " -W" : " -+");
        sb.Append(settings.GetValueOrDefault(MsxecSettings.Indicators) == "true" ? " -o" : " -O");
        sb.Append($" \"{file}\"");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
        {
            var extra = EmulatorLaunchService.SubstituteVars(config.CommandLine, file);
            sb.Append(' ').Append(extra);
        }

        return sb.ToString();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        // Dossier court (Id) plutôt que le titre complet — cf. bug MAX_PATH constaté.
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("msx", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        var disk = files.FirstOrDefault(f => DiskExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (disk != null) return disk;

        var other = files.FirstOrDefault(f => OtherExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (other != null) return other;

        return files.FirstOrDefault(f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
