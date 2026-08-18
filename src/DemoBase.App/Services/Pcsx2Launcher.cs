using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings PCSX2 ──────────────────────────────────────────────────

public static class Pcsx2Keys
{
    public const string FullScreen = "fullscreen";
    public const string NoGui      = "nogui";
    public const string FastBoot   = "fastboot";
    public const string BatchMode  = "batch";
}

// ─── Lanceur PCSX2 ────────────────────────────────────────────────────────────
// PCSX2 Qt (v2.x) — Sony PlayStation 2
// https://github.com/PCSX2/pcsx2
//
// Arguments (tiret simple) :
//   -portable   force le mode portable — réglages/inis dans le dossier de l'exe
//   -batch      quitte après power-off
//   -fullscreen plein écran immédiat
//   -fastboot   passe le logo BIOS
//   -nogui      masque la fenêtre Qt (implique -batch)
//   --          séparateur avant le nom de fichier
//
// -portable (doc officielle : "Force enable portable mode to store data in local PCSX2 path
// instead of the default configuration path") ajouté systématiquement le 2026-07-24 — cause
// probable du BIOS PS2 "introuvable" malgré une configuration en apparence correcte : sans ce
// flag, PCSX2 lit ses réglages (dont inis/PCSX2.ini [Folders] Bios) depuis Documents\PCSX2, pas
// depuis Emus\PCSX2\inis\ où BiosPackService.ConfigurePcsx2 écrit le pointeur BIOS — celui-ci
// n'était donc jamais lu. ConfigurePcsx2 gère aussi : la copie directe des BIOS PS2 identifiés
// (taille+CRC32) dans Emus\PCSX2\bios, le déploiement d'un PCSX2.ini complet en 1ère
// installation (Assets\Pcsx2_PCSX2.ini — un ini "fragment" est refusé par PCSX2), et la
// désactivation de la confirmation de fermeture ([UI] ConfirmShutdown = false).
//
// Formats supportés : .iso .chd .cso .zso .cue .mds .gz .elf
// Pour les démos ELF + données : extraire le ZIP en préservant la structure —
// PCSX2 résout host:data/ relatif au dossier de l'ELF automatiquement.

public class Pcsx2Launcher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".chd", 10 }, { ".cso", 9 }, { ".zso", 9 },
            { ".iso",  8 }, { ".cue", 7 }, { ".mds", 7 },
            { ".gz",   6 }, { ".elf", 1 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"PCSX2 introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await Task.Run(() => ExtractBestFile(romPath, configDir, release.Id));
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "PCSX2", friendlyName: "PCSX2");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        sb.Append("-portable ");

        if (settings.GetValueOrDefault(Pcsx2Keys.BatchMode, "true") != "false")
            sb.Append("-batch ");
        if (settings.GetValueOrDefault(Pcsx2Keys.FullScreen) == "true")
            sb.Append("-fullscreen ");
        if (settings.GetValueOrDefault(Pcsx2Keys.FastBoot) == "true")
            sb.Append("-fastboot ");
        if (settings.GetValueOrDefault(Pcsx2Keys.NoGui) == "true")
            sb.Append("-nogui ");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        sb.Append($"-- \"{file}\"");
        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("ps2", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité 1 : image disque
        string? best = null; int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = ExtScore.GetValueOrDefault(e, 0);
            if (score > bestScore) { bestScore = score; best = f; }
        }

        // Priorité 2 : ELF — PCSX2 résout host:data/ relatif au dossier de l'ELF
        var elf = files.FirstOrDefault(f => f.EndsWith(".elf", StringComparison.OrdinalIgnoreCase));
        if (elf != null && (best == null || bestScore <= 1))
            return elf;

        return best
            ?? files.FirstOrDefault(f => !IgnoredExtensions.Contains(
                   Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
