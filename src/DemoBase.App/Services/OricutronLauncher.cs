using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Oricutron ──────────────────────────────────────────────

public static class OricutronKeys
{
    public const string Machine    = "machine";    // "atmos" / "oric1" / "telestrat" (défaut: atmos)
    public const string FullScreen = "fullscreen"; // "true"/"false"
}

// ─── Lanceur Oricutron ────────────────────────────────────────────────────────
// Oricutron — émulateur Oric-1 / Atmos / Telestrat / Pravetz 8D
// https://github.com/pete-gordon/oricutron
//
// Usage : oricutron [options] [fichier]
//
// Options utiles :
//   --oric1        Simuler un Oric-1
//   --atmos        Simuler un Atmos (défaut)
//   --telestrat    Simuler un Telestrat
//   --fullscreen   Démarrer en plein écran
//   --disk0 <f>    Insérer image disquette dans le lecteur 0
//   --tape <f>     Insérer cassette (.tap/.ort/.wav)
//
// Formats supportés :
//   .dsk .tap .ort .wav .bas .bin .com

public class OricutronLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md", ".jpg", ".jpeg", ".png" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".dsk", 10 }, { ".tap", 8 }, { ".ort", 7 },
            { ".bas",  5 }, { ".bin", 4 }, { ".com", 3 },
            { ".wav",  2 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Oricutron introuvable : {emulator.ExecutablePath}");

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
            emulator.ExecutablePath, args, tag: "Oricutron", friendlyName: "Oricutron");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Machine
        var machine = settings.GetValueOrDefault(OricutronKeys.Machine, "atmos");
        if (machine is "oric1" or "atmos" or "telestrat")
            sb.Append($"--{machine} ");

        // Plein écran
        if (settings.GetValueOrDefault(OricutronKeys.FullScreen) == "true")
            sb.Append("--fullscreen ");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        // Passer le fichier avec le bon argument selon le format
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".dsk")
            sb.Append($"--disk0 \"{file}\"");
        else if (ext is ".tap" or ".ort" or ".wav")
            sb.Append($"--tape \"{file}\"");
        else
            sb.Append($"\"{file}\"");

        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("oric", releaseId, zipPath));
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
