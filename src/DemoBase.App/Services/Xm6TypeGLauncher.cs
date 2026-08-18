using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings XM6 TypeG ──────────────────────────────────────────────

public static class Xm6TypeGKeys
{
    public const string FullScreen = "fullscreen"; // "true"/"false"
}

// ─── Lanceur XM6 TypeG ────────────────────────────────────────────────────────
// XM6 TypeG — émulateur Sharp X68000
// http://retropc.net/pi/xm6/index.html
//
// Usage : xm6g.exe [options] [fichier]
//
// Options :
//   -f    Plein écran
//
// Formats supportés :
//   .xdf .2hd .hdm   Images disquette X68000
//   .hdf .hds .mos   Images disque dur / MO
//   .dim              DIM image (format standard)
//
// Note : XM6 TypeG nécessite CGROM.TMP et un Human68k pour fonctionner.
// Utiliser XM6Util.exe pour générer CGROM.TMP depuis les ROMs du BIOS.

public class Xm6TypeGLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md", ".jpg", ".jpeg", ".png" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".xdf", 10 }, { ".2hd", 9 }, { ".hdm", 8 },
            { ".dim",  7 }, { ".hdf", 6 }, { ".hds", 5 },
            { ".mos",  4 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"XM6 TypeG introuvable : {emulator.ExecutablePath}");

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
            emulator.ExecutablePath, args, tag: "XM6TypeG", friendlyName: "XM6 TypeG");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // XM6 TypeG n'a pas d'argument CLI pour le fullscreen.
        // Il se configure via Tools > Options dans l'UI, ou dans xm6g.ini.
        // On configure l'ini si la case est cochée.
        if (settings.GetValueOrDefault(Xm6TypeGKeys.FullScreen) == "true")
            TrySetFullscreenInIni(config);

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        sb.Append($"\"{file}\"");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Configure le fullscreen dans xm6g.ini si possible.</summary>
    private static void TrySetFullscreenInIni(EmulatorConfig config)
    {
        // L'ini se trouve dans le même dossier que l'exe
        // Section [Window], clé FullScreen=1
        // On ne le fait que si l'ini existe déjà (sinon XM6 le crée à la 1ère exécution)
        try
        {
            var iniCandidates = new[]
            {
                Path.Combine(WorkingPaths.GetSubdir(string.Empty), "..", "Emus", "XM6TypeG", "xm6g.ini"),
            };
            foreach (var ini in iniCandidates)
            {
                if (!File.Exists(ini)) continue;
                var content = File.ReadAllText(ini);
                if (content.Contains("[Window]"))
                {
                    content = System.Text.RegularExpressions.Regex.Replace(
                        content, @"(\[Window\][^\[]*)FullScreen=\d",
                        m => { var idx = m.Value.LastIndexOf('=') + 1; return m.Value[..idx] + "1"; });
                    if (!content.Contains("FullScreen="))
                        content = content.Replace("[Window]", "[Window]\nFullScreen=1");
                    File.WriteAllText(ini, content);
                }
                break;
            }
        }
        catch { /* non bloquant */ }
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("x68000", releaseId, zipPath));
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
