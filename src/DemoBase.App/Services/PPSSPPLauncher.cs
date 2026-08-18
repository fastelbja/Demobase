using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings PPSSPP ─────────────────────────────────────────────────

public static class PPSSPPKeys
{
    public const string FullScreen    = "fullscreen";     // "true" / "false"
    public const string EscapeExit    = "escape_exit";    // "true" / "false"
    public const string PauseMenuExit = "pause_menu_exit";// "true" / "false"
}

// ─── Lanceur PPSSPP ───────────────────────────────────────────────────────────
// PPSSPP — émulateur Sony PlayStation Portable, open-source, multi-plateformes.
// https://www.ppsspp.org/
//
// Commande :
//   PPSSPPWindows.exe [--fullscreen] [--escape-exit] [--pause-menu-exit] <fichier>
//
// Options utiles pour les frontends :
//   --fullscreen      — plein écran (ignoré au prochain lancement sans le flag)
//   --windowed        — mode fenêtré
//   --escape-exit     — ESC ferme PPSSPP immédiatement (recommandé frontend)
//   --pause-menu-exit — "Exit to menu" devient "Exit" dans le menu pause
//
// Formats supportés :
//   .iso  — image ISO PSP
//   .cso  — image ISO compressée (Compressed ISO)
//   .pbp  — PSP executable (EBOOT.PBP)
//   .elf  — ELF binaire PSP
//   .zip  — archive (extraction automatique)
//
// L'exécutable Windows s'appelle PPSSPPWindows.exe (32-bit) ou
// PPSSPPWindows64.exe (64-bit, recommandé).

public class PPSSPPLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> CartExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".iso", ".cso", ".pbp", ".elf" };

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public PPSSPPLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PPSSPP] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"PPSSPP introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[PPSSPP] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "PPSSPP", friendlyName: "PPSSPP");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // --fullscreen
        if (settings.GetValueOrDefault(PPSSPPKeys.FullScreen) == "true")
            sb.Append("--fullscreen ");

        // --escape-exit : ESC ferme PPSSPP (recommandé pour les frontends)
        if (settings.GetValueOrDefault(PPSSPPKeys.EscapeExit, "true") != "false")
            sb.Append("--escape-exit ");

        // --pause-menu-exit : "Exit to menu" → "Exit" dans le menu pause
        if (settings.GetValueOrDefault(PPSSPPKeys.PauseMenuExit, "true") != "false")
            sb.Append("--pause-menu-exit ");

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
            WorkingPaths.GetZipSignature("psp", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Exclure tout répertoire dont le nom contient '%' :
        //   %_SCE_GameName/  → métadonnées CFW système
        //   GUM1~1%/         → alias 8.3 Windows (tronqué avec %)
        //   GUM1~1%\EBOOT.PBP → idem
        bool IsInExcludedDir(string path)
        {
            var rel = path.Substring(extractDir.Length);
            return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      .Any(seg => seg.Contains('%'));
        }

        bool IsInSceDir(string path)
        {
            var rel = path.Substring(extractDir.Length);
            return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      .Any(seg => seg.StartsWith("__SCE_", StringComparison.OrdinalIgnoreCase));
        }

        // Priorité 1 : EBOOT.PBP dans un répertoire __SCE_*
        var sceEboot = files.FirstOrDefault(f =>
            !IsInExcludedDir(f) && IsInSceDir(f) &&
            Path.GetFileName(f).Equals("EBOOT.PBP", StringComparison.OrdinalIgnoreCase));
        if (sceEboot != null) return sceEboot;

        // Priorité 2 : .iso / .cso (hors répertoires %_)
        // Priorité 3 : tout .pbp hors %_ et __SCE_ (à la racine du zip)
        string? best = null;
        int bestScore = -1;
        foreach (var f in files)
        {
            if (IsInExcludedDir(f)) continue;  // toujours ignorer %_SCE_*
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = e switch
            {
                ".iso" => 4,
                ".cso" => 3,
                ".pbp" => IsInSceDir(f) ? 5 : 2,  // .pbp dans __SCE_ > iso
                ".elf" => 1,
                _      => 0,
            };
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f =>
                   !IsInExcludedDir(f) &&
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
