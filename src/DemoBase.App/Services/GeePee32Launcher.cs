using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings GeePee32 ───────────────────────────────────────────────

public static class GeePee32Keys
{
    public const string NoSplash = "nosplash"; // "true" / "false" — passe l'écran de démarrage
}

// ─── Lanceur GeePee32 ────────────────────────────────────────────────────────
// GeePee32 v0.43 — émulateur GamePark GP32 (Tim Schuerewegen, 2004)
// Distribué sur Zophar's Domain, EmuTalk, etc. (plus de site officiel actif)
//
// CLI (style DOS, tiret oblique) :
//   geepee32.exe /FXE=<fichier.fxe> [/SMC=<carte.smc>] /RUN [/nosplash]
//   geepee32.exe /GXB=<fichier.gxb> /SMC=<carte.smc>  /RUN [/nosplash]
//
// Formats de ROM :
//   .fxe  — exécutable GP32 (homebrew, démos) — format le plus courant
//   .gxb  — exécutable GP32 (commercial/signé) — rare
//   .smc  — image SmartMedia (carte mémoire virtuelle) — données annexes
//            Pour les démos qui embarquent tout dans le .fxe, pas de .smc nécessaire.
//
// BIOS : un fichier firmware GP32 est requis (ex. fm_bios.bin ou fm157e.bin).
// Le chemin se configure dans geepee32.ini : firmware=<chemin>
//
// Note : /RUN démarre automatiquement la ROM sans interaction utilisateur.
// /nosplash supprime l'écran de démarrage de GeePee32.

public class GeePee32Launcher
{
    // Détecte si l'exe est gp32emu (standalone direct) ou GeePee32 (CLI /FXE= legacy)
    private static bool IsGp32Emu(string exePath) =>
        Path.GetFileName(exePath).StartsWith("GP32emu", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(exePath).StartsWith("gp32emu", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ini" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".fxe", 10 }, // exécutable GP32 — le plus courant pour les démos
            { ".gxb",  8 }, // exécutable commercial/signé
            { ".smc",  1 }, // SmartMedia — données, pas un exécutable seul
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"GeePee32 introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        string? smcFile = null;

        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            (actualFile, smcFile) = await Task.Run(() => ExtractFiles(romPath, configDir, release.Id));
        }

        string args;
        if (IsGp32Emu(emulator.ExecutablePath))
            args = BuildArgumentsGp32Emu(config, actualFile);
        else
            args = BuildArguments(config, settings, actualFile, smcFile);

        var tag = IsGp32Emu(emulator.ExecutablePath) ? "GP32EMU" : "GEEPEE32";
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: tag, friendlyName: "GP32");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings,
        string exeFile, string? smcFile)
    {
        var sb = new StringBuilder();
        var ext = Path.GetExtension(exeFile).ToLowerInvariant();

        // /FXE= ou /GXB= selon l'extension
        if (ext == ".gxb")
            sb.Append($"/GXB=\"{exeFile}\"");
        else
            sb.Append($"/FXE=\"{exeFile}\"");

        // Carte SmartMedia si présente (données annexes)
        if (smcFile != null)
            sb.Append($" /SMC=\"{smcFile}\"");

        // /RUN : démarrage automatique sans interaction
        sb.Append(" /RUN");

        if (settings.GetValueOrDefault(GeePee32Keys.NoSplash, "true") != "false")
            sb.Append(" /nosplash");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, exeFile));

        return sb.ToString().TrimEnd();
    }

    private static string BuildArgumentsGp32Emu(EmulatorConfig config, string file)
    {
        // gp32emu standalone — passe le fichier directement, pas de flags spéciaux.
        var sb = new System.Text.StringBuilder();
        sb.Append($"\"{file}\"");
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));
        return sb.ToString().TrimEnd();
    }

    private static (string ExeFile, string? SmcFile) ExtractFiles(
        string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("gp32", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null; int bestScore = -1;
        string? smcFile = null;

        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = ExtScore.GetValueOrDefault(e, 0);
            if (e == ".smc") { smcFile = f; continue; } // toujours garder la SMC
            if (score > bestScore) { bestScore = score; best = f; }
        }

        var exeFile = best
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;

        return (exeFile, smcFile);
    }
}
