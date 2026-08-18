using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Pemsa ──────────────────────────────────────────────────

public static class PemsaSettings
{
    public const string NoSplash   = "nosplash";   // "true" / "false" — passer le splash d'intro (--no-splash)
    public const string Fullscreen = "fullscreen"; // "true" / "false" — démarrer en plein écran
}

// ─── Lanceur Pemsa (PICO-8) ───────────────────────────────────────────────────
// Pemsa est un runtime PICO-8 open-source. Le frontend PC est pemsa-sdl
// (https://github.com/egordorichev/pemsa-sdl), dont l'exécutable Windows est
// pemsa.exe. Il joue des cartouches PICO-8 via la ligne de commande :
//   pemsa.exe [cartouche] [flags]
//
// Flags gérés :
//   --no-splash      : passe l'animation d'intro (recommandé pour les releases)
//   --no-fullscreen  : désactive le plein écran (pemsa démarre en plein écran
//                      par défaut ; on l'ajoute donc quand l'utilisateur NE veut
//                      PAS le plein écran, pour un comportement fenêtré cohérent
//                      avec les autres launchers de type fantasy console).
//
// Cartouches PICO-8 : .p8 (texte) ou .p8.png (PNG à données embarquées). Le
// fichier peut être dans un zip — dans ce cas on l'extrait d'abord.

public class PemsaLauncher
{
    private readonly PreferencesService _prefs;

    // Suffixes de cartouche testés sur le NOM COMPLET (pas via Path.GetExtension,
    // car .p8.png renverrait ".png" et serait pris pour un simple screenshot).
    private static readonly string[] CartSuffixes = [".p8.png", ".p8"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".md", ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc"];

    public PemsaLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PEMSA] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Pemsa introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[PEMSA] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "PEMSA", friendlyName: "Pemsa");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Le cart d'abord : pemsa attend "pemsa [cart] [flags]".
        sb.Append($"\"{file}\"");

        // --no-splash : passe l'animation d'intro (recommandé pour les releases).
        if (settings.GetValueOrDefault(PemsaSettings.NoSplash) != "false")
            sb.Append(" --no-splash");

        // Pemsa démarre en plein écran par défaut → on ajoute --no-fullscreen
        // tant que l'utilisateur n'a pas explicitement demandé le plein écran.
        if (settings.GetValueOrDefault(PemsaSettings.Fullscreen) != "true")
            sb.Append(" --no-fullscreen");

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
            WorkingPaths.GetZipSignature("pemsa", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : cartouche PICO-8 (.p8 ou .p8.png) — test sur le nom complet.
        var cart = files.FirstOrDefault(
            f => CartSuffixes.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
        if (cart != null) return cart;

        // Fallback : premier fichier non-texte
        return files.FirstOrDefault(
                   f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault()
               ?? zipPath;
    }
}
