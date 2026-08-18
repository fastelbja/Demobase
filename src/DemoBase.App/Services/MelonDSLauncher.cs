using DemoBase.Core.Models;
using DemoBase.Data;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Lanceur melonDS (Nintendo DS / DSi) ──────────────────────────────────────
// melonDS — émulateur Nintendo DS/DSi haute précision. https://github.com/melonDS-emu/melonDS
//
// Ligne de commande : melonDS.exe "<rom>"
// melonDS détecte le format depuis la ROM ; le plein écran est un raccourci/réglage
// interne (pas d'option CLI), donc le launcher passe simplement la ROM.
//
// Note : melonDS en « firmware boot » nécessite un dump BIOS/firmware d'une vraie
// DS ; en « direct boot » (défaut) il lance la ROM sans BIOS.

public class MelonDSLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly string[] RomExtensions =
        [".nds", ".srl", ".dsi", ".ids", ".app"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".md", ".doc", ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp"];

    public MelonDSLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[MELONDS] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"melonDS introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[MELONDS] ZIP extrait → {actualFile}");
        }

        var sb = new StringBuilder();
        sb.Append($"\"{actualFile}\"");
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, actualFile));

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, sb.ToString().TrimEnd(), tag: "MELONDS", friendlyName: "melonDS");
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("melonds", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        foreach (var ext in RomExtensions)
        {
            var rom = files.FirstOrDefault(
                f => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase));
            if (rom != null) return rom;
        }

        return files.FirstOrDefault(
                   f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault()
               ?? zipPath;
    }
}
