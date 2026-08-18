using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Dolphin ─────────────────────────────────────────────────

public static class DolphinKeys
{
    public const string FullScreen  = "fullscreen";
    public const string BatchMode   = "batch";
}

// ─── Lanceur Dolphin ──────────────────────────────────────────────────────────
// Dolphin — émulateur Nintendo GameCube et Wii
// https://dolphin-emu.org
//
// Arguments :
//   -e <fichier>    Ouvre et lance le fichier
//   -b              Batch mode (quitte à l'arrêt)
//   --no-gui        Sans interface graphique
//   -f              Plein écran
//
// Formats supportés :
//   .iso .gcz .rvz .wbfs .ciso .gcm   GameCube / Wii
//   .wad                               WiiWare / VC
//   .elf .dol                          Homebrew

public class DolphinLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md", ".jpg", ".jpeg", ".png" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".rvz",  10 }, // format recommandé (compression + vérification)
            { ".iso",   9 },
            { ".gcz",   8 }, // GameCube compressed
            { ".wbfs",  7 },
            { ".ciso",  6 },
            { ".gcm",   5 },
            { ".wad",   4 },
            { ".elf",   3 },
            { ".dol",   2 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Dolphin introuvable : {emulator.ExecutablePath}");

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
            emulator.ExecutablePath, args, tag: "Dolphin", friendlyName: "Dolphin");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Batch mode (-b)
        if (settings.GetValueOrDefault(DolphinKeys.BatchMode, "true") != "false")
            sb.Append("-b ");

        // Plein écran via -C (config override)
        if (settings.GetValueOrDefault(DolphinKeys.FullScreen) == "true")
            sb.Append("-C Dolphin.Display.Fullscreen=True ");

        // Pas de popup de confirmation à l'arrêt
        sb.Append("-C Dolphin.Interface.ConfirmStop=False ");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        // Fichier à lancer
        sb.Append($"-e \"{file}\"");
        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("dolphin", releaseId, zipPath));
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
