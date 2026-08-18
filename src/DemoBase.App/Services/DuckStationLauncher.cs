using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings DuckStation ────────────────────────────────────────────

public static class DuckStationKeys
{
    public const string FullScreen = "fullscreen"; // "true" / "false"
    public const string Batch      = "batch";      // "true" / "false" — quitte après power-off
    public const string FastBoot   = "fastboot";   // "true" / "false"
    public const string NoGui      = "nogui";      // "true" / "false"
}

// ─── Lanceur DuckStation ──────────────────────────────────────────────────────
// DuckStation — émulateur Sony PlayStation 1 (PSX), très précis et performant.
// https://github.com/stenzek/duckstation
//
// Commande :
//   duckstation-qt.exe [-fullscreen] [-batch] [-fastboot] [-nogui] [--] <fichier>
//
// Paramètres utiles pour les frontends :
//   -batch      — quitte automatiquement après que le jeu s'éteint (recommandé)
//   -fullscreen — plein écran immédiat
//   -fastboot   — pas de logo PSX au démarrage
//   -nogui      — masque la fenêtre principale Qt, quitte à l'arrêt
//   --          — séparateur : ce qui suit est le nom de fichier
//                 (obligatoire si le chemin contient des tirets ou des espaces)
//
// Formats supportés :
//   .cue (+ .bin)  — BIN/CUE — charger le .cue, pas le .bin
//   .chd           — Compressed Hunks of Data (format compact recommandé)
//   .iso           — image ISO standard
//   .img           — image IMG
//   .mds (+ .mdf)  — format Alcohol 120%
//   .pbp           — PSP/PSX format
//   .ecm           — Error Code Modeler (compressé)
//   .zip           — extraction automatique (DuckStation ne lit pas les ZIP)
//
// Un BIOS PlayStation (scph####.bin) est requis dans le répertoire configuré.

public class DuckStationLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".cue",   5 },
            { ".chd",   4 },
            { ".mds",   3 },
            { ".iso",   2 },
            { ".img",   2 },
            { ".pbp",   1 },
            { ".ecm",   1 },
            { ".psexe", 1 }, // PS-EXE — chargé via -exe
            { ".exe",   0 }, // exécutable générique PSX
        };

    public DuckStationLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[DUCKSTATION] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"DuckStation introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[DUCKSTATION] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "DUCKSTATION", friendlyName: "DuckStation");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // -batch : quitte après power-off (recommandé pour les frontends)
        if (settings.GetValueOrDefault(DuckStationKeys.Batch, "true") != "false")
            sb.Append("-batch ");

        // -fullscreen
        if (settings.GetValueOrDefault(DuckStationKeys.FullScreen) == "true")
            sb.Append("-fullscreen ");

        // -fastboot : pas d'animation BIOS au démarrage
        if (settings.GetValueOrDefault(DuckStationKeys.FastBoot) == "true")
            sb.Append("-fastboot ");

        // -nogui : masque la fenêtre principale Qt
        if (settings.GetValueOrDefault(DuckStationKeys.NoGui) == "true")
            sb.Append("-nogui ");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        // -- <fichier> : séparateur explicite (fonctionne pour tous les formats
        // y compris .psexe, .cue, .chd, .iso…)
        sb.Append($"-- \"{file}\"");

        return sb.ToString().TrimEnd();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("psx", releaseId, zipPath));
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
