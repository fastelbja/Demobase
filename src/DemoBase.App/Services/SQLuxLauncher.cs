using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings sQLux ──────────────────────────────────────────────────

public static class SQLuxKeys
{
    public const string FullScreen = "fullscreen"; // "true"/"false"
}

// ─── Lanceur sQLux ────────────────────────────────────────────────────────────
// sQLux v1.1.2 — émulateur Sinclair QL
// https://github.com/SinclairQL/sQLux
//
// sQLux se configure via sqlux.ini — pas d'arguments CLI directs pour le fichier.
// On génère un sqlux.ini temporaire avec MDV1_FILE pointant vers le .mdv,
// puis on passe ce fichier ini via -f.
//
// Formats supportés via ini :
//   MDV1_FILE / MDV2_FILE   Images Microdrive (.mdv)
//   WIN1_DIR                Répertoire Win (fichiers QL natifs)
//
// Requiert les ROMs QL dans le dossier de l'émulateur (js.rom ou minerva.rom)

public class SQLuxLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md", ".jpg", ".jpeg", ".png" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".mdv", 10 }, { ".win", 8 }, { ".img", 6 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"sQLux introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await Task.Run(() => ExtractBestFile(romPath, configDir, release.Id));
        }

        var emuDir     = Path.GetDirectoryName(emulator.ExecutablePath)!;
        var fullscreen  = settings.GetValueOrDefault(SQLuxKeys.FullScreen) == "true";
        var ext         = Path.GetExtension(actualFile).ToLowerInvariant();

        // sQLux v1.x : les périphériques se configurent UNIQUEMENT via sqlux.ini
        // (pas d'args CLI --device). On génère un ini temporaire qui :
        //   1. importe le sqlux.ini de base de l'émulateur (ROMs, clavier…)
        //   2. monte le .mdv / .win selon le fichier à lancer
        //   3. définit BOOT_DEVICE pour démarrer directement depuis le bon drive

        var baseIni  = Path.Combine(emuDir, "sqlux.ini");
        var tempIni  = Path.Combine(WorkingPaths.GetSubdir("Configs"),
                           $"sqlux_{release.Id}.ini");

        var ini = new StringBuilder();

        // Inclure le sqlux.ini de base s'il existe (ROMs, RAMTOP, SOUND…)
        if (File.Exists(baseIni))
        {
            // Lire et recopier en filtrant les lignes DEVICE/BOOT_DEVICE
            // pour qu'on puisse les redéfinir proprement en dessous
            foreach (var line in File.ReadAllLines(baseIni))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("DEVICE", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("BOOT_DEVICE", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("WIN_SIZE", StringComparison.OrdinalIgnoreCase))
                    continue;
                ini.AppendLine(line);
            }
        }
        else
        {
            // Pas de base : config minimale (la ROM sera cherchée dans emuDir)
            ini.AppendLine($"SYSROM = {Path.Combine(emuDir, "MIN198.rom")}");
            ini.AppendLine("FAST_STARTUP = 1");
            ini.AppendLine("KBD = US");
        }

        // Plein écran
        ini.AppendLine($"WIN_SIZE = {(fullscreen ? "max" : "2x")}");

        // Montage du fichier selon son type
        if (ext == ".mdv")
        {
            // DEVICE = MDV1,<chemin>,mdv-like  ← syntaxe ini de sQLux v1.x
            // Le chemin doit utiliser des slashes (pas des backslashes) sous sQLux/SDL2
            var mdvPath = actualFile.Replace('\\', '/');
            ini.AppendLine($"DEVICE = MDV1,{mdvPath},mdv-like");
            ini.AppendLine("BOOT_DEVICE = MDV1");
        }
        else if (ext == ".win")
        {
            var winPath = actualFile.Replace('\\', '/');
            ini.AppendLine($"DEVICE = WIN1,{winPath}");
            ini.AppendLine("BOOT_DEVICE = WIN1");
        }
        else
        {
            // Dossier contenant le fichier monté comme WIN1
            var dir = Path.GetDirectoryName(actualFile)!.Replace('\\', '/');
            ini.AppendLine($"DEVICE = WIN1,{dir}");
            ini.AppendLine("BOOT_DEVICE = WIN1");
        }

        await File.WriteAllTextAsync(tempIni, ini.ToString());
        System.Diagnostics.Debug.WriteLine($"[sQLux] ini généré : {tempIni}");

        var args = new StringBuilder();
        args.Append($"-f \"{tempIni}\"");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            args.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, actualFile));

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args.ToString().TrimEnd(), tag: "sQLux", friendlyName: "sQLux");
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("sqlux", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null; int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = ExtScore.GetValueOrDefault(e, 0);
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f => !IgnoredExtensions.Contains(
                   Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
