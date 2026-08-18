using DemoBase.Core.Models;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings BigPEmu ────────────────────────────────────────────────

public static class BigPEmuKeys
{
    public const string LocalData = "localdata"; // "true" / "false"
    public const string PalMode   = "palmode";   // "true" / "false"
    // Fullscreen : se configure dans BigPEmu lui-même (Settings → Video)
    // Il n'existe pas d'argument CLI dédié pour le forcer au lancement.
}

// ─── Lanceur BigPEmu ──────────────────────────────────────────────────────────
// BigPEmu — émulateur Atari Jaguar / Jaguar CD (closed-source, Rich Whitehouse)
// https://www.richwhitehouse.com/jaguar/
//
// Commande :
//   BigPEmu.exe "<rom>" [-localdata] [-setcfgprop key value ...]
//
// IMPORTANT : le fichier ROM doit être le PREMIER argument.
// Les options viennent après.
//
// Options utiles :
//   -localdata   stocke la config dans le dossier de l'exe plutôt que dans
//                %APPDATA% — recommandé pour DemoBase (installation portable)
//
// Formats supportés (cartouche) : .j64 .jag .rom .abs .cof .zip
// Formats supportés (CD)        : .cue .cdi .bigpimg
//
// Pas de BIOS requis pour les cartouches. Pour Jaguar CD, BigPEmu peut
// générer des fichiers .bigpimg depuis son menu développeur.
//
// Note : BigPEmu supporte les ZIP natifs — il scanne le premier niveau du ZIP
// et charge le premier fichier avec une extension reconnue.
//
// Mode PAL :
//   -setcfgpropcat PALMode 1 System  (catégorie "System", clé "PALMode", valeur 1)
// Équivaut à activer Settings → System → PAL Mode dans l'UI de BigPEmu.

public class BigPEmuLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".bigpimg", 10 },
            { ".cdi",      9 }, { ".cue",  9 },
            { ".j64",      8 }, { ".jag",  8 },
            { ".rom",      7 }, { ".abs",  6 }, { ".cof", 5 },
            { ".zip",      4 },
        };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"BigPEmu introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        // BigPEmu supporte les ZIP natifs — pas besoin d'extraire
        var args = BuildArguments(config, settings, romPath);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "BIGPEMU", friendlyName: "BigPEmu");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Le fichier ROM DOIT être le premier argument
        sb.Append($"\"{file}\"");

        // -localdata : config dans le dossier de l'exe (mode portable)
        if (settings.GetValueOrDefault(BigPEmuKeys.LocalData, "true") != "false")
            sb.Append(" -localdata");

        // -setcfgpropcat PALMode 1 System : force le mode PAL (50 Hz)
        if (settings.GetValueOrDefault(BigPEmuKeys.PalMode, "true") != "false")
            sb.Append(" -setcfgpropcat PALMode 1 System");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }
}
