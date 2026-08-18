using DemoBase.Core.Models;
using DemoBase.Data;
using System.IO;

namespace DemoBase.App.Services;

// ─── Clés de settings Ruffle ─────────────────────────────────────────────────

public static class RuffleKeys
{
    /// <summary>Lance en plein écran ("true" / "false"). Correspond au flag -f / --fullscreen.</summary>
    public const string FullScreen = "fullscreen";
}

// ─── Lanceur Ruffle ───────────────────────────────────────────────────────────
// Ruffle (https://ruffle.rs) — émulateur Flash Player open source, activement
// maintenu. Lecteur desktop standalone (Windows/macOS/Linux), pas besoin de
// navigateur ni du (défunt) Flash Player d'Adobe. Ouvre un .swf directement en
// argument de ligne de commande, comme n'importe quel lecteur multimédia.
//
// Commande :
//   ruffle.exe [--fullscreen] "<fichier.swf>"
//
// Convention DemoBase : Emus/Ruffle/ruffle.exe (cf. EmulatorSeedCatalog).
// Couvre bien l'AVM1 (ActionScript 1/2, la grande majorité des vieilles
// démos/intros Flash de la scène) ; l'AVM2 (ActionScript 3) est correct mais
// moins complet — cf. ruffle.rs/compatibility en cas de souci de rendu.

public class RuffleLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[RUFFLE] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Ruffle introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[RUFFLE] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "RUFFLE", friendlyName: "Ruffle");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new System.Text.StringBuilder();

        if (settings.GetValueOrDefault(RuffleKeys.FullScreen) == "true")
            sb.Append("--fullscreen ");

        sb.Append($"\"{file}\"");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("ruffle", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        var swf = files.FirstOrDefault(f =>
            Path.GetExtension(f).Equals(".swf", StringComparison.OrdinalIgnoreCase));

        return swf
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
