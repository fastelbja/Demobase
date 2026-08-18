using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Lanceur mz800emu ────────────────────────────────────────────────────────
// mz800emu v2.x (Michal Hučík) — Sharp MZ-700 / MZ-800 / MZ-1500
// https://github.com/michalhucik/mz800emu
//
// Structure : 3 exes distincts dans le même dossier :
//   mz800emu.exe          — MZ-800
//   mz700emu-pal.exe      — MZ-700 PAL
//   mz700emu-ntsc.exe     — MZ-700 NTSC
//   mz1500emu.exe         — MZ-1500
//
// Chaque exe charge automatiquement son propre ini (même nom, même dossier) :
//   mz800emu.ini, mz700emu.ini (pour pal et ntsc), mz1500emu.ini
// Il n'existe PAS d'argument CLI pour passer un chemin d'ini alternatif.
//
// Chargement de fichier :
//   mz800emu v2.x ne supporte pas le chargement automatique via ini ou CLI.
//   DemoBase extrait le ZIP si nécessaire et ouvre l'émulateur dans le bon
//   répertoire de travail. L'utilisateur charge le fichier depuis l'UI.
//   Pour les formats CMT (.mzf/.mzt/.tap), utiliser File > Open in CMT.
//   Pour les formats FDC (.dsk), utiliser File > Open in FDC.
//
// Formats supportés :
//   CMT : .mzf .mzt .tap .wav
//   FDC : .dsk
//   Quick Disk : (via UI)
//   Snapshot : .mzs

public static class Mz800EmuKeys
{
    public const string Machine = "machine"; // "mz800" | "mz700-pal" | "mz700-ntsc" | "mz1500"
}

public class Mz800EmuLauncher
{
    private static readonly HashSet<string> IgnoredExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".mzs", 10 }, // snapshot — chargement auto possible via quickload
            { ".mzf",  8 }, { ".mzt",  8 },
            { ".tap",  6 }, { ".wav",  5 },
            { ".dsk",  7 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        var exeDir = Path.GetDirectoryName(emulator.ExecutablePath)!;

        // Résoudre le bon exe selon la machine configurée
        var machine = settings.GetValueOrDefault(Mz800EmuKeys.Machine, "mz800") ?? "mz800";
        var exePath = ResolveExe(emulator.ExecutablePath, machine);

        if (!File.Exists(exePath))
            return new(false, $"mz800emu introuvable : {exePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        string? actualFile = null;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await Task.Run(() => ExtractBestFile(romPath, configDir, release.Id));
        }
        else
        {
            actualFile = romPath;
        }

        // Lancer l'émulateur avec le répertoire de travail = dossier de l'exe
        // (pour qu'il trouve son ini). Pas d'argument de fichier possible.
        var args = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            args.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, actualFile ?? ""));

        // Log pour info de l'utilisateur
        System.Diagnostics.Debug.WriteLine(
            $"[MZ800EMU] Machine={machine} Exe={Path.GetFileName(exePath)} " +
            $"File={actualFile ?? "(non extrait)"} " +
            $"— charger manuellement depuis File > Open in CMT/FDC");

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            exePath,
            args.ToString().TrimEnd(),
            tag: "MZ800EMU",
            friendlyName: $"mz800emu ({machine})",
            workingDir: exeDir);
    }

    /// <summary>
    /// Résout le chemin de l'exe selon la machine.
    /// Le SeedCatalog peut pointer vers n'importe lequel des 4 exes —
    /// on dérive les autres à partir du dossier de l'exe détecté.
    /// </summary>
    private static string ResolveExe(string detectedExePath, string machine)
    {
        var dir = Path.GetDirectoryName(detectedExePath)!;
        return machine switch
        {
            "mz700-pal"  => Path.Combine(dir, "mz700emu-pal.exe"),
            "mz700-ntsc" => Path.Combine(dir, "mz700emu-ntsc.exe"),
            "mz1500"     => Path.Combine(dir, "mz1500emu.exe"),
            _            => Path.Combine(dir, "mz800emu.exe"),
        };
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("mz800", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null; int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExts.Contains(e)) continue;
            var score = ExtScore.GetValueOrDefault(e, 0);
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f => !IgnoredExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
