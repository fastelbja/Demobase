using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings DCMOTO ──────────────────────────────────────────────────

public static class DcmotoKeys
{
    public const string Machine = "machine";
    // Valeurs : "mo5" "mo5e" "mo5nr" "mo6" "pc128" "t9000"
    //           "to7" "to770" "to8" "to8d" "to9" "to9p"
}

// ─── Lanceur DCMOTO ───────────────────────────────────────────────────────────
// DCMOTO — émulateur Thomson MO5/MO6/TO7/TO8/TO9 par Daniel Coulom
// https://dcmoto.pages-perso.free.fr/
//
// Usage : dcmoto.exe [machine] [fichier]
//
// Argument 1 optionnel : nom de la machine (mo5, to7, to8, etc.)
// Argument 2 : fichier à charger
//
// Formats supportés :
//   .fd .qd          Images disquette Thomson
//   .k7              Images cassette Thomson
//   .m5  .to         ROM cartouche
//   .bas .bin .sap   Fichiers programmes

public class DcmotoLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf", ".md", ".jpg", ".jpeg", ".png" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".fd",  10 }, { ".qd",  9 }, { ".k7",  8 },
            { ".m5",   7 }, { ".to",  6 }, { ".sap", 5 },
            { ".bas",  4 }, { ".bin", 3 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"DCMOTO introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await Task.Run(() => ExtractBestFile(romPath, configDir, release.Id));
        }

        var args = BuildArguments(config, settings, actualFile);
        var result = await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "DCMOTO", friendlyName: "DCMOTO");

        // DCMOTO ne supporte pas le chargement CLI — afficher le chemin du fichier extrait
        // pour que l'utilisateur puisse le charger manuellement via le menu Fichier
        if (result.Success)
            DemoBase.App.Controls.StatusScrollerControl.Post(
                $"DCMOTO : charger le fichier via Fichier > Charger disquette/cassette → {actualFile}",
                isError: false);

        return result;
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Machine optionnelle en premier argument
        var machine = settings.GetValueOrDefault(DcmotoKeys.Machine, string.Empty)?.Trim();
        if (!string.IsNullOrEmpty(machine))
            sb.Append($"{machine} ");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        // Note : DCMOTO ne supporte pas le chargement de fichier en ligne de commande.
        // Le fichier doit être chargé manuellement via Fichier > Charger disquette/cassette.
        // On affiche un message informatif dans la barre de statut.

        return sb.ToString().TrimEnd();
    }

    private static string ExtractBestFile(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("thomson", releaseId, zipPath));
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
