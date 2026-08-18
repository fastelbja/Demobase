using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings TIC-80 ─────────────────────────────────────────────────

public static class Tic80Settings
{
    public const string Skip       = "skip";       // "true" / "false" — passer le splash screen
    public const string Fullscreen = "fullscreen"; // "true" / "false" — plein écran
}

// ─── Lanceur TIC-80 ───────────────────────────────────────────────────────────
// TIC-80 est un "fantasy computer" open-source (https://github.com/nesbox/TIC-80).
// Il joue des cartouches binaires .tic via la ligne de commande :
//   tic80.exe [--skip] [--fullscreen] <cartouche.tic>
//
// --skip    : passe le splash screen d'intro (recommandé pour les releases)
// --fullscreen : démarre en plein écran
//
// Le fichier .tic peut être dans un zip — dans ce cas on l'extrait d'abord.

public class Tic80Launcher
{
    private readonly PreferencesService _prefs;

    private static readonly string[] CartExtensions = [".tic"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".md", ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc"];

    public Tic80Launcher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[TIC80] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"TIC-80 introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[TIC80] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "TIC80", friendlyName: "Tic80");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // --skip : passe le splash screen (recommandé pour les releases scène)
        if (settings.GetValueOrDefault(Tic80Settings.Skip) != "false")
            sb.Append("--skip ");

        // --fullscreen
        if (settings.GetValueOrDefault(Tic80Settings.Fullscreen) == "true")
            sb.Append("--fullscreen ");

        sb.Append($"\"{file}\"");

        // Paramètres additionnels de la config profil
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
            WorkingPaths.GetZipSignature("tic80", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : fichier .tic
        var cart = files.FirstOrDefault(
            f => CartExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (cart != null) return cart;

        // Fallback : premier fichier non-texte
        return files.FirstOrDefault(
                   f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault()
               ?? zipPath;
    }
}
