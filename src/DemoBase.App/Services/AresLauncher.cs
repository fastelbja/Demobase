using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings ares ───────────────────────────────────────────────────

public static class AresKeys
{
    /// <summary>Nom du système (--system). Requis quand l'extension est ambiguë.</summary>
    public const string System         = "system";
    public const string FullScreen     = "fullscreen";     // "true" / "false"
    public const string Kiosk          = "kiosk";          // "true" / "false" — UI minimale
    public const string NoFilePrompt   = "no_file_prompt"; // "true" / "false" — pas de dialog 64DD etc.
}

// ─── Lanceur ares ─────────────────────────────────────────────────────────────
// ares — émulateur multi-système open source axé sur la précision.
// https://github.com/ares-emulator/ares
//
// Commande :
//   ares.exe [--system <nom>] [--fullscreen] [--kiosk] [--no-file-prompt] "<fichier>"
//
// Notes importantes :
//   • --fullscreen ne fonctionne QUE si un fichier ROM est aussi passé.
//   • --system est nécessaire quand l'extension est partagée entre systèmes
//     (ex: .bin → Mega Drive ou ColecoVision ?).
//   • --no-file-prompt évite le dialog "64DD Disk?" au démarrage de N64.
//   • --kiosk active l'UI minimale (+ implique --no-file-prompt).
//
// Systèmes supportés (noms exacts pour --system) :
//   Nintendo : Famicom, Famicom Disk System, Super Famicom, Satellaview,
//              Game Boy, Game Boy Color, Game Boy Advance,
//              Nintendo 64, 64DD
//   Sega     : SG-1000, Master System, Game Gear,
//              Mega Drive, Mega CD, Mega CD 32X, 32X
//   NEC      : PC Engine, PC Engine CD, SuperGrafx
//   SNK      : Neo Geo Pocket, Neo Geo Pocket Color,
//              Neo Geo AES, Neo Geo MVS
//              (⚠ « Neo Geo » tout court est INVALIDE — ares le rejette.
//               Neo Geo CD via .cue/.chd : dépend de la version d'ares, vérifier.)
//   Bandai   : WonderSwan, WonderSwan Color
//   Autres   : ColecoVision, MSX, MSX2, PlayStation
//
//   ⚠ La liste faisant autorité est celle d'`ares --help` (elle varie selon la
//     version). Le --system doit correspondre EXACTEMENT à un de ces noms.
//
// Formats : dépend du système — .nes/.sfc/.gb/.gbc/.gba/.n64/.v64/.z64/
//           .md/.sms/.gg/.pce/.ngp/.ws/.col/.rom + .zip (natif)

public class AresLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public AresLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[ARES] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"ares introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        // Complète settings.bml avec les BIOS déjà copiés dans Emus/Ares/bios/ (par le
        // bouton "Pack BIOS" → BiosPackService.ConfigureAres) — À CHAQUE lancement plutôt
        // qu'une seule fois au moment du "Pack BIOS", car il y aura plusieurs systèmes à BIOS
        // au fil du temps ; ne touche jamais une valeur déjà renseignée (à la main ou par un
        // lancement précédent), cf. BiosPackService.SyncAresFirmwareSettings.
        BiosPackService.SyncAresFirmwareSettings(emulator.ExecutablePath);

        // Complète également le Hotkey "Quitter" (F12) et, pour Neo Geo Pocket/Pocket Color,
        // les contrôles clavier par défaut — cf. BiosPackService.SyncAresControlsAndHotkeys.
        // aresSystem calculé ici (avant BuildArguments, qui le recalcule pour --system) pour
        // savoir tout de suite si le système en cours est Neo Geo Pocket(Color).
        var aresSystem = ResolveAresSystem(settings, release);
        var isFirstNeoGeoPocketRun =
            BiosPackService.SyncAresControlsAndHotkeys(emulator.ExecutablePath, aresSystem);

        if (isFirstNeoGeoPocketRun)
        {
            System.Windows.MessageBox.Show(
                "C'est la première utilisation de l'émulateur.\n" +
                "Vous allez devoir initialiser le système.\n\n" +
                "Pour cela utiliser :\n\n" +
                "Touches du curseur pour naviguer\n" +
                "Espace pour valider.\n\n" +
                "Enfin appuyer sur la touche F12 pour quitter l'émulateur.",
                $"Première utilisation — {aresSystem}",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // ares ne supporte pas les ZIP nativement — extraction nécessaire
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[ARES] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile, release);

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "ARES", friendlyName: "ares");
    }

    // Déduit le --system ares à partir du nom de plateforme DemoBase (Demozoo), quand le
    // profil ne le précise pas explicitement lui-même (cf. AresKeys.System). Corrige le cas
    // constaté : "Multiple possible game types detected for: xxx.gb" — ares n'arrive pas à
    // trancher tout seul entre les systèmes partageant une extension (ex. .gb), même si
    // DemoBase connaît déjà la plateforme exacte de la release. Vérifié du plus spécifique
    // au moins spécifique (ex. "Game Boy Color" avant "Game Boy") pour éviter les faux
    // positifs par sous-chaîne. Table non exhaustive : complète au besoin si un autre
    // système bute sur la même ambiguïté (cf. liste des noms valides en tête de fichier).
    private static readonly (string Contains, string AresSystem)[] PlatformNameToAresSystem =
    {
        ("Game Boy Advance",        "Game Boy Advance"),
        ("Game Boy Color",          "Game Boy Color"),
        ("Game Boy",                "Game Boy"),
        ("Famicom Disk System",     "Famicom Disk System"),
        ("Super Famicom",           "Super Famicom"),
        ("Super Nintendo",          "Super Famicom"),
        ("SNES",                    "Super Famicom"),
        ("Satellaview",             "Satellaview"),
        ("Nintendo 64DD",           "64DD"),
        ("Nintendo 64",             "Nintendo 64"),
        ("Nintendo Entertainment",  "Famicom"),
        ("Famicom",                 "Famicom"),
        ("NES",                     "Famicom"),
        ("Mega CD 32X",             "Mega CD 32X"),
        ("Mega CD",                 "Mega CD"),
        ("Sega CD",                 "Mega CD"),
        ("32X",                     "32X"),
        ("Mega Drive",              "Mega Drive"),
        ("Genesis",                 "Mega Drive"),
        ("Master System",           "Master System"),
        ("Game Gear",               "Game Gear"),
        ("SG-1000",                 "SG-1000"),
        ("SuperGrafx",              "SuperGrafx"),
        ("PC Engine CD",            "PC Engine CD"),
        ("PC Engine",               "PC Engine"),
        ("TurboGrafx",              "PC Engine"),
        ("Neo Geo Pocket Color",    "Neo Geo Pocket Color"),
        ("Neo Geo Pocket",          "Neo Geo Pocket"),
        ("Neo Geo AES",             "Neo Geo AES"),
        ("Neo Geo MVS",             "Neo Geo MVS"),
        ("WonderSwan Color",        "WonderSwan Color"),
        ("WonderSwan",              "WonderSwan"),
        ("ColecoVision",            "ColecoVision"),
        ("MSX2",                    "MSX2"),
        ("MSX",                     "MSX"),
        ("PlayStation",             "PlayStation"),
    };

    private static string? InferAresSystem(Release release)
    {
        var platformName = release.ReleasePlatforms?.FirstOrDefault()?.Platform?.Name;
        if (string.IsNullOrWhiteSpace(platformName)) return null;

        foreach (var (contains, aresSystem) in PlatformNameToAresSystem)
            if (platformName.Contains(contains, StringComparison.OrdinalIgnoreCase))
                return aresSystem;

        return null;
    }

    // Résout le --system ares (priorité au réglage manuel du profil, sinon déduit de la
    // plateforme de la release via InferAresSystem, avec la correction "Neo Geo" → "Neo Geo
    // AES"). Factorisé pour être appelé à la fois par BuildArguments (valeur CLI) et par
    // LaunchAsync (savoir si le système en cours est Neo Geo Pocket/Pocket Color, pour le
    // message de première utilisation et la synchro des contrôles clavier).
    private static string? ResolveAresSystem(Dictionary<string, string?> settings, Release release)
    {
        var system = settings.GetValueOrDefault(AresKeys.System, string.Empty)?.Trim();
        if (string.IsNullOrWhiteSpace(system))
            system = InferAresSystem(release);
        // Correction : "Neo Geo" tout court est invalide dans ares — utiliser "Neo Geo AES"
        if (system == "Neo Geo") system = "Neo Geo AES";
        return string.IsNullOrWhiteSpace(system) ? null : system;
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file, Release release)
    {
        var sb = new StringBuilder();

        // --system <nom> : obligatoire pour les extensions ambiguës. Priorité au réglage
        // manuel du profil s'il existe ; sinon déduit automatiquement de la plateforme de
        // la release (InferAresSystem) — évite d'avoir à le configurer à la main pour
        // chaque profil/plateforme.
        var system = ResolveAresSystem(settings, release);
        if (!string.IsNullOrWhiteSpace(system))
            sb.Append($"--system \"{system}\" ");

        // --kiosk : UI minimale (implique --no-file-prompt)
        var kiosk = settings.GetValueOrDefault(AresKeys.Kiosk) == "true";
        if (kiosk)
        {
            sb.Append("--kiosk ");
        }
        else
        {
            // --no-file-prompt : pas de dialog pour les ROMs secondaires (64DD, Super GameBoy…)
            if (settings.GetValueOrDefault(AresKeys.NoFilePrompt, "true") != "false")
                sb.Append("--no-file-prompt ");
        }

        // <fichier> doit venir AVANT --fullscreen (ares l'exige)
        sb.Append($"\"{file}\"");

        // --fullscreen : APRÈS le fichier (obligatoire pour ares)
        if (settings.GetValueOrDefault(AresKeys.FullScreen) == "true")
            sb.Append(" --fullscreen");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("ares", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : extensions les plus courantes de ROMs
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".nes", ".sfc", ".smc", ".gb", ".gbc", ".gba",
            ".n64", ".v64", ".z64",
            ".md",  ".sms", ".gg",  ".32x",
            ".pce", ".ngp", ".ngc", ".ws", ".wsc",
            ".col", ".rom",
            ".cue", ".chd", ".iso",
        };

        var best = files
            .Where(f => preferred.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => preferred.Contains(Path.GetExtension(f).ToLowerInvariant()) ? 0 : 1)
            .FirstOrDefault();

        return best
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
