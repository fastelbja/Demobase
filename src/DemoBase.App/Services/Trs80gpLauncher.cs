using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings trs80gp ────────────────────────────────────────────────

public static class Trs80gpKeys
{
    public const string Model      = "model";      // "1", "3", "4" (défaut: 3)
    public const string FullScreen = "fullscreen"; // "true"/"false"
}

// ─── Lanceur trs80gp ──────────────────────────────────────────────────────────
// trs80gp — émulateur TRS-80 Model I/II/III/4/4P/MC-10
// https://48k.ca/trs80gp.html
//
// Usage : trs80gp [options] [fichier]
//
// Options utiles :
//   -m1  -m3  -m4   Sélectionner le modèle (défaut : Model III)
//   -f            Plein écran
//   -q            Quitter après la fin du programme (pour les .cmd)
//
// Formats supportés :
//   .dmk .dsk .jv3    Images disquette
//   .cmd               Exécutable TRS-DOS
//   .cas .wav          Cassette
//   .bas               BASIC
//   .hex .bin          Binaire brut

public class Trs80gpLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md", ".jpg", ".jpeg", ".png" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".dmk", 10 }, { ".dsk", 9 }, { ".jv3", 8 },
            { ".cmd",  7 }, { ".cas", 6 }, { ".bas", 5 },
            { ".hex",  4 }, { ".bin", 3 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine($"[trs80gp] exe='{emulator.ExecutablePath}' exists={File.Exists(emulator.ExecutablePath)} romPath='{romPath}'");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"trs80gp introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await Task.Run(() => ExtractBestFile(romPath, configDir, release.Id));
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "trs80gp", friendlyName: "trs80gp");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Modèle TRS-80 (-m1, -m3, -m4)
        var model = settings.GetValueOrDefault(Trs80gpKeys.Model, "3");
        if (model is "1" or "3" or "4")
            sb.Append($"-m{model} ");

        // Plein écran
        if (settings.GetValueOrDefault(Trs80gpKeys.FullScreen) == "true")
            sb.Append("-f ");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        sb.Append($"\"{file}\"");
        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("trs80", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null; int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = ExtScore.GetValueOrDefault(e, 0);
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f => !IgnoredExtensions.Contains(
                   Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
