using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings AppleWin ───────────────────────────────────────────────

public static class AppleWinKeys
{
    /// <summary>Modèle de machine : apple2, apple2p, apple2jp, apple2e, apple2ee</summary>
    public const string Model    = "model";
    public const string PowerOn  = "power_on"; // "true" / "false" — démarrage auto
    public const string Freq     = "freq";      // "50hz" (PAL) / "60hz" (NTSC)
}

// ─── Lanceur AppleWin ─────────────────────────────────────────────────────────
// AppleWin — émulateur Apple II complet pour Windows.
// https://github.com/AppleWin/AppleWin
//
// Commande :
//   AppleWin.exe -d1 "<disk.dsk>" [-d2 "<disk2.dsk>"] [-model <m>]
//                [-power-on] [-50hz | -60hz]
//
// Arguments principaux :
//   -d1 <img>  — disquette slot 6 drive 1 (5.25" ou 3.5" selon le modèle)
//   -d2 <img>  — disquette slot 6 drive 2
//   -h1 <img>  — disque dur (slot 7)
//   -model <m> — modèle machine :
//                  apple2     — Apple II (48K)
//                  apple2p    — Apple II+ (48K)
//                  apple2jp   — Apple II J-Plus
//                  apple2e    — Apple //e
//                  apple2ee   — Enhanced Apple //e (défaut recommandé)
//   -power-on  — allume automatiquement (utile si pas de disque d'accueil)
//   -50hz      — mode PAL 50Hz  /  -60hz — mode NTSC 60Hz (défaut)
//
// ⚠ Fullscreen : pas d'argument CLI — configurer via le menu AppleWin
//   (Video → Fullscreen) qui sauvegarde la préférence dans le registre.
//
// ⚠ AppleWin ne supporte PAS l'Apple IIgs — utiliser GSplus pour l'IIgs.
//
// Formats supportés :
//   .dsk, .do   — images DOS 3.3
//   .po, .2mg   — images ProDOS
//   .nib        — images nibblisées (5.25")
//   .woz        — format WOZ haute fidélité
//   .hdv        — images disque dur ProDOS
//   .zip, .gz   — AppleWin ouvre ces archives nativement

public class AppleWinLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".dsk", 4 }, { ".do", 4 },
            { ".woz", 4 }, { ".2mg", 3 }, { ".po", 3 },
            { ".nib", 2 }, { ".hdv", 1 },
        };

    public AppleWinLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[APPLEWIN] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return Task.FromResult(new LaunchResult(false, $"AppleWin introuvable : {emulator.ExecutablePath}"));

        if (!File.Exists(romPath))
            return Task.FromResult(new LaunchResult(false, $"Fichier introuvable : {romPath}"));

        var actualFile = romPath;
        var ext = Path.GetExtension(romPath).ToLowerInvariant();

        // AppleWin ouvre les ZIP et GZ nativement
        if (ext != ".zip" && ext != ".gz")
        {
            // Pas d'extraction nécessaire — AppleWin gère les archives
        }

        var args = BuildArguments(config, settings, actualFile);
        return ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "APPLEWIN", friendlyName: "AppleWin");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb  = new StringBuilder();
        var ext = Path.GetExtension(file).ToLowerInvariant();

        // -model <modèle> (Enhanced Apple //e par défaut)
        var model = settings.GetValueOrDefault(AppleWinKeys.Model, "apple2ee") ?? "apple2ee";
        sb.Append($"-model {model} ");

        // Fréquence : -50hz (PAL) ou -60hz (NTSC, défaut)
        var freq = settings.GetValueOrDefault(AppleWinKeys.Freq, "60hz") ?? "60hz";
        if (freq == "50hz") sb.Append("-50hz ");

        // -power-on : démarrage automatique sans attendre Ctrl+OpenApple
        if (settings.GetValueOrDefault(AppleWinKeys.PowerOn, "true") != "false")
            sb.Append("-power-on ");

        // Détection du type de lecteur selon l'extension
        // .hdv → -h1 (disque dur), tout le reste → -d1 (disquette)
        if (ext == ".hdv")
            sb.Append($"-h1 \"{file}\"");
        else
            sb.Append($"-d1 \"{file}\"");

        // Paramètres additionnels du profil (ex: -d2 "side2.dsk")
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }
}
