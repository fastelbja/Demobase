using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings BlastEm ────────────────────────────────────────────────

public static class BlastEmSettings
{
    public const string FullScreen = "fullscreen"; // "true" / "false"
}

// ─── Lanceur BlastEm ─────────────────────────────────────────────────────────
// BlastEm — émulateur Sega Genesis/Mega Drive haute précision.
// https://www.retrodev.com/blastem/
//
// Le SEUL émulateur (avec Exodus) à passer les tests VDP FIFO de Nemesis,
// à afficher les démos "Direct Color DMA" et à émuler les CRAM dots.
// Premier émulateur à faire tourner Overdrive 2 de Titan correctement.
//
// Commande : blastem.exe [-f] <fichier>
//   -f : plein écran (toggle — si déjà défini "on" dans default.cfg, le désactive)
//
// Formats supportés :
//   .bin, .md, .gen, .smd — ROMs Genesis/Mega Drive
//   .sms, .gg             — Master System / Game Gear (support partiel)
//   .zip                  — archive (BlastEm ouvre les ZIP nativement)
//
// Important : blastem.exe doit être lancé depuis son propre répertoire car il
// cherche rom.db, default.cfg et les autres ressources dans le répertoire courant.

public class BlastEmLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".bin", 4 }, { ".md", 4 }, { ".gen", 4 }, { ".smd", 3 },
            { ".sms", 2 }, { ".gg", 1 },
        };

    public BlastEmLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[BLASTEM] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"BlastEm introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        var ext = Path.GetExtension(romPath).ToLowerInvariant();

        // BlastEm ouvre les ZIP nativement mais l'extraction est plus fiable
        if (ext == ".zip")
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[BLASTEM] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "BLASTEM", friendlyName: "BlastEm");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // -f : plein écran
        if (settings.GetValueOrDefault(BlastEmSettings.FullScreen) == "true")
            sb.Append("-f ");

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
            WorkingPaths.GetZipSignature("blastem", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null;
        int bestScore = -1;
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
