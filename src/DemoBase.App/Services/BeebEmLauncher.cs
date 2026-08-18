using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings BeebEm ─────────────────────────────────────────────────

public static class BeebEmKeys
{
    public const string FullScreen = "fullscreen"; // "true"/"false"
    public const string Model      = "model";      // "b" "bplus" "master128"
}

// ─── Lanceur BeebEm ───────────────────────────────────────────────────────────
// BeebEm — émulateur BBC Micro Model B / B Plus / Master 128
// http://www.mkw.me.uk/beebem/
//
// Usage : BeebEm.exe [options] [fichier]
//
// Options :
//   -f              Plein écran
//   -model b        Modèle BBC Micro B (défaut)
//   -model bplus    Modèle BBC Micro B Plus
//   -model master128 Modèle Master 128
//
// Formats supportés :
//   .ssd .dsd    Images disquette BBC Micro simple/double face
//   .adf .adl    Images disquette ADFS
//   .uef .csw    Images cassette
//   .rom         ROM cartouche

public class BeebEmLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md", ".jpg", ".jpeg", ".png" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".ssd", 10 }, { ".dsd", 9 }, { ".adf", 8 },
            { ".adl",  7 }, { ".uef", 6 }, { ".csw", 5 },
            { ".rom",  4 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"BeebEm introuvable : {emulator.ExecutablePath}");

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
            emulator.ExecutablePath, args, tag: "BeebEm", friendlyName: "BeebEm");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        if (settings.GetValueOrDefault(BeebEmKeys.FullScreen) == "true")
            sb.Append("-FullScreen ");

        var model = settings.GetValueOrDefault(BeebEmKeys.Model, "ModelB");
        if (model is "ModelB" or "BPlus" or "IntegraB" or "Master128" or "MasterET")
            sb.Append($"-Model {model} ");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        sb.Append($"\"{file}\"");
        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("bbc", releaseId, zipPath));
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
