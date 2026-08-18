using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Flycast ─────────────────────────────────────────────────

public static class FlycastKeys
{
    public const string FullScreen = "fullscreen"; // "true"/"false"
    public const string Region     = "region";     // "0"=Japan "1"=USA "2"=Europe "3"=Default
}

// ─── Lanceur Flycast ──────────────────────────────────────────────────────────
// Flycast — émulateur Sega Dreamcast, Naomi, Naomi 2, Atomiswave
// https://github.com/flyinghead/flycast
//
// Usage : flycast.exe [fichier]
//
// Flycast n'a pas d'arguments CLI documentés pour le fullscreen —
// le plein écran est configuré dans les settings de l'émulateur.
//
// Formats supportés :
//   .gdi .cdi .chd .cue   Images disque Dreamcast
//   .zip .7z              Archives
//   .bin .lst             Binaires

public class FlycastLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md", ".jpg", ".jpeg", ".png" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".chd", 10 }, { ".gdi",  9 }, { ".cdi", 8 },
            { ".cue",  7 }, { ".bin",  3 }, { ".lst", 2 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Flycast introuvable : {emulator.ExecutablePath}");

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
            emulator.ExecutablePath, args, tag: "Flycast", friendlyName: "Flycast");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Plein écran via -config window:fullscreen=yes
        if (settings.GetValueOrDefault(FlycastKeys.FullScreen) == "true")
            sb.Append("-config window:fullscreen=yes ");

        // Région : 0=Japan, 1=USA, 2=Europe, 3=Default
        var region = settings.GetValueOrDefault(FlycastKeys.Region, "3");
        if (region is "0" or "1" or "2")
            sb.Append($"-config config:region={region} ");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        sb.Append($"\"{file}\"");
        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("dc", releaseId, zipPath));
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
