using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Xenia ──────────────────────────────────────────────────

public static class XeniaKeys
{
    public const string FullScreen = "fullscreen"; // "true" / "false"
    public const string VSync      = "vsync";      // "true" / "false"
    public const string Gpu        = "gpu";        // "vulkan" / "d3d12" / "any"
}

// ─── Lanceur Xenia ────────────────────────────────────────────────────────────
// Xenia — émulateur Xbox 360 expérimental open source.
// https://github.com/xenia-project/xenia
// (version Canary recommandée : https://github.com/xenia-canary/xenia-canary)
//
// Commande :
//   xenia.exe "<fichier>" [--fullscreen=true] [--vsync=false] [--gpu=vulkan]
//
// IMPORTANT : le fichier ROM vient EN PREMIER, les options après avec syntaxe
// --option=value (pas d'espace entre option et valeur).
//
// Formats supportés :
//   .xex               — Xbox 360 Executable (format extrait, recommandé)
//   .iso               — image ISO du disque Xbox 360
//   .zar               — archive Xbox 360
//   Pas de ZIP natif — pour les XBLA (Xbox Live Arcade), passer le fichier
//   exécutable du jeu directement (fichier sans extension dans la hiérarchie
//   TitleID/ContentType/Hash).
//
// Configuration :
//   Xenia Canary stocke sa config dans xenia-canary.config.toml (même dossier
//   que l'exe). Utiliser --config=<path> pour spécifier un fichier alternatif.
//   Fullscreen, GPU backend et vsync peuvent aussi être définis dans ce fichier.
//
// GPU Backend :
//   vulkan  — recommandé sur la majorité des cartes (AMD, NVIDIA, Intel)
//   d3d12   — meilleures performances sur certains jeux récents (Windows 10+)
//   any     — Xenia choisit automatiquement

public class XeniaLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".xex", 5 },
            { ".iso", 4 },
            { ".zar", 3 },
        };

    public XeniaLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[XENIA] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Xenia introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        var ext = Path.GetExtension(romPath).ToLowerInvariant();

        if (ext == ".zip")
        {
            // Xenia ne supporte pas les ZIP — extraction nécessaire
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[XENIA] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "XENIA", friendlyName: "Xenia");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Le fichier ROM doit venir EN PREMIER dans la commande Xenia
        sb.Append($"\"{file}\"");

        // --fullscreen=true
        if (settings.GetValueOrDefault(XeniaKeys.FullScreen) == "true")
            sb.Append(" --fullscreen=true");

        // --vsync=false (désactiver pour moins d'input lag sur certains jeux)
        if (settings.GetValueOrDefault(XeniaKeys.VSync, "true") == "false")
            sb.Append(" --vsync=false");

        // --gpu=vulkan|d3d12|any
        var gpu = settings.GetValueOrDefault(XeniaKeys.Gpu, "any") ?? "any";
        if (!string.IsNullOrWhiteSpace(gpu) && gpu != "any")
            sb.Append($" --gpu={gpu}");

        // Paramètres additionnels du profil (ex: --config=game.toml)
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("x360", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null;
        int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = ExtScore.GetValueOrDefault(e, 0);
            // Fichier sans extension dans une hiérarchie Xbox Live Arcade
            if (e == string.Empty && score == 0)
                score = 2;
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
