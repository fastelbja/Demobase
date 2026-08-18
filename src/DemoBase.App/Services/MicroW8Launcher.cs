using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings MicroW8 ────────────────────────────────────────────────

public static class MicroW8Settings
{
    public const string Filter = "filter";  // nearest|fast_crt|ss_crt|chromatic_crt|auto_crt
}

// ─── Lanceur MicroW8 (uw8.exe) ───────────────────────────────────────────────
// MicroW8 est une fantasy console WebAssembly (https://exoticorn.github.io/microw8/).
// L'exécutable natif est uw8.exe (dev tool + runtime inclus).
// Commande : uw8.exe run [--filter <filter>] <cartouche.uw8|.w8>
//
// Filtres d'affichage disponibles :
//   nearest      — pixel doubling sans anti-aliasing (look rétro net)
//   fast_crt     — filtre CRT simple (peu coûteux, éviter < 960×720)
//   ss_crt       — CRT super-samplé (meilleure qualité)
//   chromatic_crt— CRT avec décalage RGB (look phosphore)
//   auto_crt     — ss_crt < 960×720, chromatic_crt sinon (défaut)
//
// Formats supportés : .uw8 (format natif compressé), .w8 (alias ancien / variante),
// .wasm (module WebAssembly brut), .wat (text format WebAssembly).

public class MicroW8Launcher
{
    private readonly PreferencesService _prefs;

    private static readonly string[] CartExtensions =
        [".uw8", ".w8", ".wasm"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".md", ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc"];

    public MicroW8Launcher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[UW8] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"uw8.exe introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[UW8] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "UW8", friendlyName: "MicroW8");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder("run ");

        // --filter <filtre> — défaut : auto_crt
        var filter = settings.GetValueOrDefault(MicroW8Settings.Filter);
        if (!string.IsNullOrWhiteSpace(filter) && filter != "auto_crt")
            sb.Append($"--filter {filter} ");

        sb.Append($"\"{file}\"");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
        {
            var extra = EmulatorLaunchService.SubstituteVars(config.CommandLine, file);
            sb.Append(' ').Append(extra);
        }

        return sb.ToString().TrimEnd();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("uw8", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : .uw8 > .w8 > .wasm
        foreach (var ext in CartExtensions)
        {
            var cart = files.FirstOrDefault(
                f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (cart != null) return cart;
        }

        return files.FirstOrDefault(
                   f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault()
               ?? zipPath;
    }
}
