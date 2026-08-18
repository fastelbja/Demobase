using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings CSFEC ──────────────────────────────────────────────────

public static class CsfecSettings
{
    public const string Ram        = "ram";         // "0".."6" — -kX, défaut "0" (64K, config standard C64)
    public const string FullScreen = "fullscreen";   // "true" / "false"
    public const string Indicators = "indicators";   // "true" / "false" (-o/-O)
}

// ─── Lanceur CSFEC (Commodore 64) ────────────────────────────────────────────
// Frère de CPCEC (même moteur). Différences documentées (cf. cpcec.txt, section
// "CSFEC") prises en compte : PAS d'option -mX (un seul modèle de C64 émulé,
// donc pas de sélecteur de modèle ici) ; fichiers PRG/TAP/T64/CRT au lieu de
// DSK/CDT/SNA (pas de lecteur de disquette émulé par CSFEC, contrairement à
// CPCEC/ZXSEC/MSXEC) ; RAM par défaut mise à 64K (-k0) plutôt que de reprendre
// le défaut "-k1" générique de CPCEC — un C64 standard a 64K (c'est même dans
// son nom), l'extension mémoire (GeoRAM/REU, -g/-G) est une rareté que la
// quasi-totalité des productions de la scène C64 n'utilisent pas. Ce choix
// GeoRAM/REU n'est pas exposé ici (pertinent seulement au-delà de 64K).

public class CsfecLauncher
{
    private readonly PreferencesService _prefs;

    // CSFEC ne lit pas de disquette (.d64) — son format "principal" est le programme
    // .prg, le plus courant pour les releases crackées/demos de la scène C64.
    private static readonly string[] PrgExtensions   = [".prg"];
    private static readonly string[] OtherExtensions = [".tap", ".t64", ".crt"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public CsfecLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[CSFEC] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"CSFEC introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[CSFEC] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[CSFEC] ZIP extrait → fichier choisi : {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "CSFEC", friendlyName: "Csfec");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        var ram = settings.GetValueOrDefault(CsfecSettings.Ram);
        sb.Append($"-k{(string.IsNullOrWhiteSpace(ram) ? "0" : ram)}");
        sb.Append(settings.GetValueOrDefault(CsfecSettings.FullScreen) == "true" ? " -W" : " -+");
        sb.Append(settings.GetValueOrDefault(CsfecSettings.Indicators) == "true" ? " -o" : " -O");
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
            WorkingPaths.GetZipSignature("csfec", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        var prg = files.FirstOrDefault(f => PrgExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (prg != null) return prg;

        var other = files.FirstOrDefault(f => OtherExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (other != null) return other;

        return files.FirstOrDefault(f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
