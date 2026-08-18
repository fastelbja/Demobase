using DemoBase.Core.Models;
using DemoBase.Data;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Azahar ─────────────────────────────────────────────────

public static class AzaharKeys
{
    public const string Fullscreen = "fullscreen"; // "true" / "false" — --fullscreen
}

// ─── Lanceur Azahar (Nintendo 3DS) ────────────────────────────────────────────
// Azahar — émulateur Nintendo 3DS, successeur de Citra (fusion de Lime3DS et du
// fork PabloMK7). https://github.com/azahar-emu/azahar
//
// Ligne de commande (héritée de Citra) : azahar.exe "<rom>" [--fullscreen]
// Azahar détecte le format depuis la ROM ; démarre en fenêtré par défaut.
//
// Formats 3DS : .cci / .3ds (renommer si nécessaire), .cxi, .cia, .app, .3dsx et
// .elf (homebrew), .z3ds (format compressé propre à Azahar).

public class AzaharLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly string[] RomExtensions =
        [".cci", ".3ds", ".cxi", ".cia", ".app", ".3dsx", ".elf", ".axf", ".z3ds"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".md", ".doc", ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp"];

    public AzaharLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[AZAHAR] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Azahar introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[AZAHAR] ZIP extrait → {actualFile}");
        }

        // Azahar exige l'extension .cci pour les ROMs décryptées (même si le contenu
        // est identique à un .3ds). Si le fichier extrait porte l'extension .3ds, on
        // le renomme en .cci dans le même dossier — c'est un simple rename, aucune
        // conversion de données (cf. documentation officielle Azahar).
        if (Path.GetExtension(actualFile).Equals(".3ds", StringComparison.OrdinalIgnoreCase))
        {
            var cci = Path.ChangeExtension(actualFile, ".cci");
            if (!File.Exists(cci))
                File.Move(actualFile, cci);
            else
                File.Copy(actualFile, cci, overwrite: true);
            actualFile = cci;
            System.Diagnostics.Debug.WriteLine($"[AZAHAR] Renommé .3ds → .cci : {actualFile}");
        }

        // [CORRECTIF 2026-07-24] Azahar (src/citra_qt/citra_qt.cpp, GMainWindow::GMainWindow)
        // n'utilise PAS un parseur d'arguments standard (QCommandLineParser) : c'est une boucle
        // artisanale qui ne reconnaît le chemin de la ROM que dans deux cas précis :
        //   1. Un seul argument au total (cas glisser-déposer) : args.size() == 2.
        //   2. Le TOUT DERNIER argument de la ligne de commande, s'il ne commence pas par '-'.
        // Tout flag (--fullscreen, etc.) ajouté APRÈS le fichier fait donc que la ROM n'est plus
        // le dernier argument → elle n'est JAMAIS chargée, silencieusement (Azahar démarre
        // normalement, sur son menu vide, sans la moindre erreur). C'est exactement le
        // symptôme observé : le lancement ne fait rien, sans message d'erreur, alors que le
        // même fichier ouvert à la main dans Azahar fonctionne. Le fichier ROM doit donc
        // TOUJOURS être le DERNIER token de la ligne de commande — tous les flags avant.
        var sb = new StringBuilder();

        // Azahar démarre en fenêtré → --fullscreen seulement si demandé.
        if (settings.GetValueOrDefault(AzaharKeys.Fullscreen) == "true")
            sb.Append("--fullscreen ");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, actualFile)).Append(' ');

        // Le fichier ROM en dernier, toujours — cf. commentaire ci-dessus.
        sb.Append($"\"{actualFile}\"");

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, sb.ToString().TrimEnd(), tag: "AZAHAR", friendlyName: "Azahar");
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("azahar", releaseId, zipPath));
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
