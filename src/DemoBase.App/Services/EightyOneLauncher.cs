using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Lanceur EightyOne (ZX-80 / ZX-81 / TS1000) ─────────────────────────────
// EightyOne est l'émulateur de référence pour les machines pré-Spectrum Sinclair.
// https://github.com/charlierobson/EightyOne
//
// Commande : EightyOne.exe <fichier>
//   Le fichier peut être passé directement en argument ; EightyOne le charge
//   automatiquement en détectant son format d'après l'extension.
//
// Formats supportés :
//   .p, .81   — snapshot ZX-81 (format natif le plus courant pour les démos)
//   .o, .80   — snapshot ZX-80
//   .tzx, .tap — cassettes (ZX-80, ZX-81, Spectrum)
//   .zip       — archive (extraction automatique)
//
// La machine cible (ZX-80, ZX-81, TS1000, etc.) et les options graphiques
// se configurent dans EightyOne.ini dans le répertoire de l'exécutable.

public class EightyOneLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly string[] CartExtensions =
        [".p", ".81", ".o", ".80", ".tzx", ".tap"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".md", ".doc", ".pdf", ".jpg", ".jpeg", ".png", ".gif"];

    public EightyOneLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[81] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"EightyOne introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[81] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "81", friendlyName: "EightyOne");
    }

    private static string BuildArguments(EmulatorConfig config, string file)
    {
        var sb = new StringBuilder($"\"{file}\"");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("zx81", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : .p / .81 (ZX-81) > .o / .80 (ZX-80) > .tzx / .tap
        string? best = null;
        int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = e switch
            {
                ".p" or ".81"  => 4,
                ".o" or ".80"  => 3,
                ".tzx"         => 2,
                ".tap"         => 1,
                _              => 0,
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
