using DemoBase.Core.Models;
using DemoBase.Data;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Mesen ──────────────────────────────────────────────────

public static class MesenKeys
{
    public const string Fullscreen = "fullscreen"; // "true" / "false" — --fullscreen
}

// ─── Lanceur Mesen / MesenCE ──────────────────────────────────────────────────
// Mesen (MesenCE) est un émulateur multi-système haute précision
// (SNES/NES/GB/GBA/PCE/SMS/GG/WS). https://github.com/nesdev-org/MesenCE
//
// Ligne de commande :
//   Mesen.exe "<rom>" [--fullscreen]
//
// Mesen DÉTECTE AUTOMATIQUEMENT le système à partir de la ROM : aucun réglage de
// « machine » n'est nécessaire (contrairement à MAME). Il démarre en fenêtré par
// défaut ; on ajoute --fullscreen si l'utilisateur le demande.

public class MesenLauncher
{
    private readonly PreferencesService _prefs;

    // Extensions de ROM reconnues par Mesen, SNES en tête (usage principal ici).
    private static readonly string[] RomExtensions =
    [
        ".sfc", ".smc", ".fig", ".swc", ".bs", ".st",          // SNES / Super Famicom
        ".nes", ".fds", ".unf", ".unif",                       // NES / Famicom
        ".gb", ".gbc", ".sgb", ".gba",                         // Game Boy / GBA
        ".pce", ".sgx",                                        // PC Engine
        ".sms", ".gg", ".sg",                                  // Master System / Game Gear / SG-1000
        ".ws", ".wsc",                                         // WonderSwan
    ];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".md", ".doc", ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp"];

    public MesenLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[MESEN] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Mesen introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        // Filet best-effort : couvre les installations Mesen déjà en place avant l'ajout de
        // MesenSetupService (install-time), sinon l'assistant "MesenCE - Emulator Configuration"
        // continuerait d'apparaître pour elles. Ne fait rien si un settings.json existe déjà.
        var mesenDir = Path.GetDirectoryName(emulator.ExecutablePath);
        if (!string.IsNullOrEmpty(mesenDir))
            MesenSetupService.DeployPreconfiguredSettingsIfNeeded(mesenDir);

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[MESEN] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "MESEN", friendlyName: "Mesen");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // La ROM d'abord : "Mesen.exe [rom] [options]".
        sb.Append($"\"{file}\"");

        // Mesen démarre en fenêtré → --fullscreen seulement si demandé.
        if (settings.GetValueOrDefault(MesenKeys.Fullscreen) == "true")
            sb.Append(" --fullscreen");

        // Paramètres additionnels du profil.
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("mesen", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : une ROM console reconnue (SNES d'abord via l'ordre de RomExtensions).
        foreach (var ext in RomExtensions)
        {
            var rom = files.FirstOrDefault(
                f => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase));
            if (rom != null) return rom;
        }

        // Fallback : premier fichier non-texte.
        return files.FirstOrDefault(
                   f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault()
               ?? zipPath;
    }
}
