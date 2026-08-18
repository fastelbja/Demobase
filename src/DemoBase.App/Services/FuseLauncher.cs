using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Fuse ───────────────────────────────────────────────────

public static class FuseSettings
{
    /// <summary>
    /// Machine à émuler (--machine).
    /// Valeurs : 48, 128, plus2, plus2a, plus3, pentagon, pentagon512,
    ///           pentagon1024, scorpion, tc2048, tc2068, ts2068, 16, se
    /// Défaut : 48
    /// </summary>
    public const string Machine = "machine";
}

// ─── Lanceur Fuse ─────────────────────────────────────────────────────────────
// Fuse — Free Unix Spectrum Emulator (aussi disponible pour Windows/macOS)
// https://fuse-emulator.sourceforge.net/
//
// Commande :
//   fuse.exe [--machine <type>] <fichier>
//
// Machines supportées :
//   16, 48, 128, plus2, plus2a, plus3, plus3e, pentagon, pentagon512,
//   pentagon1024, scorpion, tc2048, tc2068, ts2068, se
// (Fuse ne supporte PAS ZX Spectrum Next — utiliser ZEsarUX pour ça)
//
// Formats supportés :
//   .z80, .sna, .szx           — snapshots
//   .tap, .tzx, .pzx           — cassettes
//   .trd, .scl                 — disques TR-DOS (Pentagon)
//   .dsk, .udi, .fdi, .td0     — images disquette +3 / Beta 128
//   .mgt, .img, .sad, .opd     — images D80/D40/DISCiPLE/Opus
//   .rzx                       — enregistrements input
//   .zip                       — archive (extraction automatique)

public class FuseLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public FuseLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[FUSE] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Fuse introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[FUSE] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "FUSE", friendlyName: "Fuse");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // --machine <type> (défaut 48 si non spécifié)
        var machine = settings.GetValueOrDefault(FuseSettings.Machine, "48") ?? "48";
        if (!string.IsNullOrWhiteSpace(machine) && machine != "48")
            sb.Append($"--machine {machine} ");

        // Fichier à charger
        sb.Append($"\"{file}\"");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("fuse", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité d'extraction
        string? best = null;
        int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = e switch
            {
                ".trd" or ".scl"                    => 6, // TR-DOS (démos Pentagon)
                ".dsk" or ".udi" or ".fdi" or ".td0"=> 5, // disques +3/Beta128
                ".sna" or ".z80" or ".szx"          => 4, // snapshots
                ".tzx" or ".pzx"                    => 3, // cassettes (format riche)
                ".tap"                              => 2, // cassettes simples
                ".rzx"                              => 1, // replays
                _                                   => 0,
            };
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
