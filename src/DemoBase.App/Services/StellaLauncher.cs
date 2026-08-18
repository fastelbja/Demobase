using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Stella ─────────────────────────────────────────────────

public static class StellaKeys
{
    public const string FullScreen = "fullscreen"; // "true" / "false"
    public const string VSync      = "vsync";      // "true" / "false"
    public const string Palette    = "palette";    // "standard" / "z26" / "user"
    public const string Zoom       = "zoom";       // "1" / "2" / "3" / "4" / "auto"
}

// ─── Lanceur Stella ───────────────────────────────────────────────────────────
// Stella — émulateur Atari 2600 VCS multi-plateforme, référence du genre.
// https://stella-emu.github.io/
//
// Commande :
//   stella.exe [-fullscreen 1] [-vsync 1] [-zoom_tia N] [-palette P] "<rom>"
//
// Arguments utiles :
//   -fullscreen 1|0   — plein écran
//   -vsync 1|0        — synchronisation verticale
//   -zoom_tia N       — zoom de l'image TIA (1-10, mode fenêtré uniquement)
//   -palette P        — palette : standard, z26, user
//
// Formats supportés :
//   .a26, .bin, .rom  — ROMs Atari 2600 non compressées
//   .gz               — ROMs compressées gzip
//   .zip              — archives ZIP (Stella les lit nativement)
//
// Note : Stella supporte UNIQUEMENT l'Atari 2600. Pour l'Atari 7800, utiliser ProSystem.

public class StellaLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public StellaLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[STELLA] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return Task.FromResult(new LaunchResult(false,
                $"Stella introuvable : {emulator.ExecutablePath}"));

        if (!File.Exists(romPath))
            return Task.FromResult(new LaunchResult(false,
                $"Fichier introuvable : {romPath}"));

        // Stella lit les ZIP nativement
        var args = BuildArguments(config, settings, romPath);
        return ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "STELLA", friendlyName: "Stella");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // -fullscreen 1|0
        if (settings.GetValueOrDefault(StellaKeys.FullScreen) == "true")
            sb.Append("-fullscreen 1 ");

        // -vsync 1|0
        if (settings.GetValueOrDefault(StellaKeys.VSync, "true") != "false")
            sb.Append("-vsync 1 ");

        // -zoom_tia N (seulement en mode fenêtré, ignoré en fullscreen)
        var zoom = settings.GetValueOrDefault(StellaKeys.Zoom, "2") ?? "2";
        if (zoom != "auto" && !string.IsNullOrWhiteSpace(zoom))
            sb.Append($"-zoom_tia {zoom} ");

        // -palette standard|z26|user
        var palette = settings.GetValueOrDefault(StellaKeys.Palette, "standard") ?? "standard";
        sb.Append($"-palette {palette} ");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        sb.Append($"\"{file}\"");
        return sb.ToString().TrimEnd();
    }
}
