using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Lanceur ColEm ───────────────────────────────────────────────────────────
// ColEm v5.6 — ColecoVision / Coleco Adam
// https://fms.komkon.org/ColEm/
//
// CLI : colem [-options] [filename]
//   Le fichier ROM est le dernier argument.
//   Formats : .col .cv .rom .bin (et variantes .gz)
//   BIOS requis : COLEM.ROM dans le même dossier que ColEm.exe
//
// Options utiles :
//   -pal / -ntsc     : standard vidéo (défaut -ntsc)
//   -sgm             : Super Game Module (homebrews récents)
//   -skip <percent>  : sauter des frames si trop rapide
//   -sync <freq>     : synchronisation timer
//   Full screen      : Alt+Enter (pas de flag CLI Windows)

public static class ColEmKeys
{
    public const string VideoStandard = "video_standard"; // "ntsc" | "pal"
    public const string Sgm           = "sgm";            // "true" | "false"
}

public class ColEmLauncher
{
    private static readonly HashSet<string> SupportedExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".col", ".cv", ".rom", ".bin", ".gz" };

    private static readonly HashSet<string> IgnoredExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"ColEm introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        // Vérifier la présence du BIOS
        var exeDir  = Path.GetDirectoryName(emulator.ExecutablePath)!;
        var biosPath = Path.Combine(exeDir, "COLEM.ROM");
        if (!File.Exists(biosPath))
            System.Diagnostics.Debug.WriteLine($"[COLEM] Attention : BIOS COLEM.ROM absent de {exeDir}");

        // Extraire le ZIP si nécessaire
        string actualFile;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var outDir = Path.Combine(WorkingPaths.GetSubdir("Configs"), "extracted",
                WorkingPaths.GetZipSignature("colem", release.Id, romPath));
            actualFile = await Task.Run(() => ExtractBestFile(romPath, outDir));
        }
        else
        {
            actualFile = romPath;
        }

        // Construire les arguments
        var args = new StringBuilder();

        var video = settings.GetValueOrDefault(ColEmKeys.VideoStandard, "ntsc") ?? "ntsc";
        args.Append($"-{video} ");

        if (settings.GetValueOrDefault(ColEmKeys.Sgm) == "true")
            args.Append("-sgm ");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) &&
            config.CommandLine.Trim() != "{file}")
            args.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, actualFile));
        else
            args.Append($"\"{actualFile}\"");

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath,
            args.ToString().TrimEnd(),
            tag: "COLEM",
            friendlyName: "ColEm",
            workingDir: exeDir);
    }

    private static string ExtractBestFile(string zipPath, string outDir)
    {
        var files = WorkingPaths.ExtractZipCached(zipPath, outDir);
        return files
            .Where(f => SupportedExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? files.FirstOrDefault(f => !IgnoredExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
