using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Lanceur ep128emu ────────────────────────────────────────────────────────
// ep128emu v2.0.11.x — émulateur Enterprise 64/128, ZX Spectrum, Amstrad CPC,
// Videoton TVC (Istvan Varga)
// https://github.com/istvan-v/ep128emu/releases
//
// Commande :
//   ep128emu.exe [-ep128|-zx|-cpc|-tvc] [-cfg <fichier.ep128cfg>]
//                [OPTION=VALUE ...] [-snapshot <fichier>]
//
// Options utiles :
//   -ep128         Enterprise 64/128 (défaut pour DemoBase)
//   -zx            ZX Spectrum 48/128
//   -cpc           Amstrad CPC
//   -tvc           Videoton TVC
//   -cfg FILE      charge une configuration ASCII avant de lancer
//   -snapshot FILE charge un snapshot ou démo .ep128d
//   OPTION=VALUE   override n'importe quelle clé de config en CLI
//
// Formats ROM Enterprise : .com .prg .trn .128 (et sans extension)
// Formats snapshot       : .ep128s (snapshot) .ep128d (démo)
// Formats tape           : .tap .tzx .wav (Enterprise tape)
// Formats floppy         : .img .dsk
//
// Chargement d'un fichier .com/.prg via epfileio.rom :
//   ep128emu configure automatiquement FILE: si epfileio.rom est installé.
//   On peut passer "fileio.workingDirectory=<dossier>" en CLI pour pointer
//   vers le dossier extrait.
//
// BIOS / ROMs : installés automatiquement par le wizard de ep128emu
//   (epmakecfg.exe). Stockés dans le dossier de l'exe sous roms\.
//
// Note : ep128emu sauvegarde sa config dans %APPDATA%\ep128emu\ep128cfg.dat.
// Les overrides CLI sont appliqués PAR-DESSUS cette config.

public static class Ep128EmuKeys
{
    public const string KEY_MACHINE  = "machine";   // "ep128" | "zx" | "cpc" | "tvc"
    public const string KEY_CFG_FILE = "cfg_file";  // chemin vers un .ep128cfg de base
}

public class Ep128EmuLauncher
{
    private static readonly HashSet<string> SnapshotExts =
        new(StringComparer.OrdinalIgnoreCase) { ".ep128s", ".ep128d", ".z80", ".sna" };

    private static readonly HashSet<string> RomExts =
        new(StringComparer.OrdinalIgnoreCase) { ".com", ".prg", ".trn", ".128" };

    private static readonly HashSet<string> TapeExts =
        new(StringComparer.OrdinalIgnoreCase) { ".tap", ".tzx", ".wav" };

    private static readonly HashSet<string> DiskExts =
        new(StringComparer.OrdinalIgnoreCase) { ".img", ".dsk" };

    private static readonly HashSet<string> IgnoredExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"ep128emu introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await Task.Run(() => ExtractBestFile(romPath, configDir, release.Id));
        }

        var args = BuildArguments(config, settings, actualFile, emulator.ExecutablePath);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "EP128EMU", friendlyName: "ep128emu");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings,
        string file, string exePath)
    {
        var sb = new StringBuilder();
        var ext = Path.GetExtension(file).ToLowerInvariant();

        // Machine type
        var machine = settings.GetValueOrDefault(
            Ep128EmuKeys.KEY_MACHINE, "ep128") ?? "ep128";
        sb.Append($"-{machine} ");

        // Config de base optionnelle
        var cfgFile = settings.GetValueOrDefault(Ep128EmuKeys.KEY_CFG_FILE);
        if (!string.IsNullOrWhiteSpace(cfgFile) && File.Exists(cfgFile))
            sb.Append($"-cfg \"{cfgFile}\" ");

        // Dispatch selon le type de fichier
        if (SnapshotExts.Contains(ext))
        {
            sb.Append($"-snapshot \"{file}\"");
        }
        else if (TapeExts.Contains(ext))
        {
            sb.Append($"tape.imageFile=\"{file}\"");
        }
        else if (DiskExts.Contains(ext))
        {
            sb.Append($"floppy.a.imageFile=\"{file}\"");
        }
        else
        {
            // ROM / exécutable Enterprise : utiliser epfileio
            // Passe le dossier du fichier comme répertoire de travail FILE:
            // et le fichier comme premier fichier à charger
            var dir = Path.GetDirectoryName(file) ?? "";
            sb.Append($"fileio.workingDirectory=\"{dir}\" ");
            sb.Append($"-snapshot \"{file}\"");
        }

        // Args additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("ep128", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : snapshot > ROM > tape > disk
        foreach (var set in new[] { SnapshotExts, RomExts, TapeExts, DiskExts })
        {
            var match = files.FirstOrDefault(f => set.Contains(
                Path.GetExtension(f).ToLowerInvariant()));
            if (match != null) return match;
        }

        // Fallback : fichier sans extension (binaires EP natifs courants)
        var noExt = files.FirstOrDefault(f =>
            string.IsNullOrEmpty(Path.GetExtension(f)) &&
            !IgnoredExts.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (noExt != null) return noExt;

        return files.FirstOrDefault(f =>
                   !IgnoredExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault()
               ?? zipPath;
    }
}
