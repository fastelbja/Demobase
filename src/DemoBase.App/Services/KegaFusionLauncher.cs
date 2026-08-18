using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Kega Fusion ────────────────────────────────────────────

public static class KegaFusionSettings
{
    /// <summary>
    /// Console cible : auto (détection auto), -sms, -gg, -gen, -md, -32x, -scd, -mcd
    /// "auto" = pas de flag passé, Fusion détecte depuis l'extension du fichier.
    /// </summary>
    public const string Console    = "console";
    /// <summary>
    /// Région : -auto, -usa, -jap, -eur
    /// </summary>
    public const string Country    = "country";
    public const string FullScreen = "fullscreen";
}

// ─── Lanceur Kega Fusion ─────────────────────────────────────────────────────
// Kega Fusion est l'émulateur de référence pour les consoles Sega 8/16/32 bits.
// https://segaretro.org/Kega_Fusion
//
// Systèmes supportés :
//   SG-1000, SC-3000, SF-7000, Master System, Game Gear,
//   Genesis / Mega Drive (+ SVP + Pico), Sega CD / Mega CD, 32X, CD+32X
//
// Commande :
//   Fusion.exe "<fichier>" [-sms|-gg|-gen|-md|-32x|-scd|-mcd] [-usa|-jap|-eur|-auto] [-fullscreen]
//
// La console est auto-détectée depuis l'extension si aucun flag n'est spécifié.
// Extensions reconnues :
//   .sms .sg .sc          → Master System / SG-1000 / SC-3000
//   .gg                   → Game Gear
//   .bin .md .gen .smd    → Genesis / Mega Drive
//   .32x                  → 32X
//   .cue .iso .bin        → Sega CD / Mega CD  (distingué de Genesis par contexte)
//   .zip                  → auto-détecté à l'intérieur par Fusion

public class KegaFusionLauncher
{
    private readonly PreferencesService _prefs;

    // Extensions → flag console pour l'auto-sélection si "auto" dans les settings
    private static readonly Dictionary<string, string> ExtToConsole =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".sms",  "-sms"  },
            { ".sg",   "-sms"  },
            { ".sc",   "-sms"  },
            { ".gg",   "-gg"   },
            { ".md",   "-gen"  },
            { ".gen",  "-gen"  },
            { ".smd",  "-gen"  },
            { ".32x",  "-32x"  },
            { ".cue",  "-scd"  },
            { ".iso",  "-scd"  },
        };

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    // Priorité d'extraction depuis un ZIP
    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".cue",  7 }, // Sega CD (pointeur de pistes)
            { ".32x",  6 }, // 32X
            { ".gen",  5 }, { ".md",  5 }, { ".smd", 5 }, // Genesis
            { ".bin",  4 }, // Genesis ou Sega CD — priorité plus basse (ambigu)
            { ".sms",  3 }, { ".sg",  3 }, { ".sc",  3 },
            { ".gg",   2 },
            { ".iso",  1 },
        };

    public KegaFusionLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[FUSION] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Kega Fusion introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        var ext = Path.GetExtension(romPath).ToLowerInvariant();

        // Fusion ouvre les ZIP nativement — extraction seulement si multi-fichiers
        if (ext == ".zip")
        {
            if (!ShouldPassZipDirect(romPath))
            {
                var configDir = WorkingPaths.GetSubdir("Configs");
                actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
                System.Diagnostics.Debug.WriteLine($"[FUSION] ZIP extrait → {actualFile}");
            }
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "FUSION", friendlyName: "KegaFusion");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb  = new StringBuilder();
        var ext = Path.GetExtension(file).ToLowerInvariant();

        // Fichier en premier
        sb.Append($"\"{file}\"");

        // Console — flag optionnel (auto si vide ou "auto")
        var console = settings.GetValueOrDefault(KegaFusionSettings.Console, "auto") ?? "auto";
        if (console != "auto" && !string.IsNullOrWhiteSpace(console))
        {
            sb.Append($" {console}");
        }
        else if (ExtToConsole.TryGetValue(ext, out var autoFlag))
        {
            sb.Append($" {autoFlag}");
        }

        // Pays
        var country = settings.GetValueOrDefault(KegaFusionSettings.Country, "-auto") ?? "-auto";
        if (!string.IsNullOrWhiteSpace(country))
            sb.Append($" {country}");

        // Plein écran
        if (settings.GetValueOrDefault(KegaFusionSettings.FullScreen) == "true")
            sb.Append(" -fullscreen");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    private static bool ShouldPassZipDirect(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            int usable = zip.Entries.Count(e =>
                !string.IsNullOrEmpty(e.Name) &&
                !IgnoredExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()));
            return usable <= 1;
        }
        catch { return true; }
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("fusion", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null;
        int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = ExtScore.GetValueOrDefault(e, 0);
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
