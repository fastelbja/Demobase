using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings ZXSEC ──────────────────────────────────────────────────

public static class ZxsecSettings
{
    public const string MachineModel = "machine_model"; // "0"=48K "1"=128K "2"=+2 "3"=+3
    public const string FullScreen   = "fullscreen";     // "true" / "false"
    public const string Indicators   = "indicators";     // "true" / "false" (-o/-O)
}

// ─── Lanceur ZXSEC (Sinclair Spectrum 48K/128K/+2/+3) ────────────────────────
// Frère de CPCEC (même moteur, même esprit d'intégration : pas de fichier de
// config généré, pas de réglage firmware — les .rom de ZXSEC (spectrum.rom,
// spec128k.rom, spec-p-2.rom, spec-p-3.rom) doivent être copiés dans le même
// dossier que l'exécutable). Différences documentées avec CPCEC (cf. cpcec.txt,
// section "ZXSEC") prises en compte : PAS de réglage RAM exposé (la mémoire est
// fixée par le modèle choisi, contrairement au CPC qui permet une extension
// mémoire indépendante du modèle) ; PAS de réglage CRTC — sur Spectrum, les
// options -g0..-g4 changent de sens et pilotent le type de joystick, pas
// pertinent pour la lecture passive d'une demo/intro, donc non exposé ici pour
// rester cohérent (mieux ne rien afficher qu'afficher un réglage mal nommé).

public class ZxsecLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly string[] DiskExtensions  = [".dsk"];
    private static readonly string[] OtherExtensions = [".cdt", ".csw", ".wav", ".sna", ".cpr", ".rom"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public ZxsecLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[ZXSEC] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"ZXSEC introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[ZXSEC] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[ZXSEC] ZIP extrait → fichier choisi : {actualFile}");
        }

        var model = DetectModel(release, settings);
        System.Diagnostics.Debug.WriteLine($"[ZXSEC] Modèle détecté : -m{model}");

        var args = BuildArguments(config, settings, model, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "ZXSEC", friendlyName: "Zxsec");
    }

    private static string DetectModel(Release release, Dictionary<string, string?> settings)
    {
        if (settings.TryGetValue(ZxsecSettings.MachineModel, out var manual)
            && !string.IsNullOrWhiteSpace(manual))
            return manual;

        var platformNames = release.ReleasePlatforms
            .Where(rp => rp.Platform != null)
            .Select(rp => rp.Platform!.Name.ToLowerInvariant())
            .ToList();

        if (platformNames.Any(p => p.Contains("+3") || p.Contains("plus 3") || p.Contains("plus3")))
            return "3";
        if (platformNames.Any(p => p.Contains("+2") || p.Contains("plus 2") || p.Contains("plus2")))
            return "2";
        if (platformNames.Any(p => p.Contains("48")))
            return "0";

        // Défaut : 128K — la grande majorité des demos/musicales de la scène Spectrum
        // ciblent le 128K pour son puce sonore AY-3-8912, absente du 48K de base.
        return "1";
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string model, string file)
    {
        var sb = new StringBuilder();

        sb.Append($"-m{model}");
        sb.Append(settings.GetValueOrDefault(ZxsecSettings.FullScreen) == "true" ? " -W" : " -+");
        sb.Append(settings.GetValueOrDefault(ZxsecSettings.Indicators) == "true" ? " -o" : " -O");
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
            WorkingPaths.GetZipSignature("zx", releaseId, zipPath));
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
