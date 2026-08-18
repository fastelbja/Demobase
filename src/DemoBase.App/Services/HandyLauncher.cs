using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Handy ──────────────────────────────────────────────────

public static class HandyKeys
{
    // Handy a très peu d'options CLI. Le fullscreen et le zoom se configurent
    // depuis le menu Options de l'émulateur.
}

// ─── Lanceur Handy ────────────────────────────────────────────────────────────
// Handy — émulateur Atari Lynx (Keith Wilkins / open source)
// http://handy.sf.net
//
// Commande :
//   handy.exe "<rom>"
//
// CLI très minimaliste : seul le chemin du ROM est passé en argument.
// Toutes les options (fullscreen, zoom, rotation) se configurent dans l'UI.
//
// Formats supportés : .lnx .lyx
//
// BIOS requis : lynxboot.img (512 octets) dans le même dossier que handy.exe.
// Sans ce fichier, Handy refuse de démarrer.
//
// Formats ROM : les ROMs Lynx utilisent typiquement l'extension .lnx (format
// avec header) ou .lyx (format raw). Les deux sont supportés.

public class HandyLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".lnx", 10 },
            { ".lyx",  9 },
            { ".o",    5 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Handy introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await Task.Run(() => ExtractBestFile(romPath, configDir, release.Id));
        }

        var args = BuildArguments(config, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "HANDY", friendlyName: "Handy");
    }

    private static string BuildArguments(EmulatorConfig config, string file)
    {
        var sb = new StringBuilder();
        sb.Append($"\"{file}\"");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("lynx", releaseId, zipPath));
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
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
