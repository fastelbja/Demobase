using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings jzIntv ─────────────────────────────────────────────────

public static class JzIntvKeys
{
    public const string FullScreen = "fullscreen"; // "true"/"false"
}

// ─── Lanceur jzIntv ───────────────────────────────────────────────────────────
// jzIntv — émulateur Mattel Intellivision (1979)
// http://spatula-city.org/~im14u2c/intv/
//
// Usage : jzIntv.exe [options] <fichier>
//
// Options utiles :
//   -f            Plein écran
//   --display-resolution=NxM  Résolution
//
// Formats supportés :
//   .rom .bin .int .itv   ROM Intellivision
//   (avec .cfg associé pour les .bin)
//
// Note : requiert exec.bin et grom.bin dans le dossier de l'émulateur.

public class JzIntvLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md", ".jpg", ".jpeg", ".png", ".cfg" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".rom", 10 }, { ".int", 9 }, { ".itv", 8 }, { ".bin", 7 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"jzIntv introuvable : {emulator.ExecutablePath}");

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
            emulator.ExecutablePath, args, tag: "jzIntv", friendlyName: "jzIntv");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        if (settings.GetValueOrDefault(JzIntvKeys.FullScreen) == "true")
            sb.Append("-f ");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        sb.Append($"\"{file}\"");
        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("intv", releaseId, zipPath));
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
