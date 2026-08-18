using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings VICE/Plus4+C16 ─────────────────────────────────────────

public static class VicePlus4Settings
{
    public const string Model      = "model";       // "plus4" / "c16" → -model <token>
    public const string Region     = "region";      // "pal" / "ntsc" → -pal / -ntsc
    public const string TrueDrive  = "truedrive";    // "true" / "false" → -drive8truedrive / +drive8truedrive
    public const string FullScreen = "fullscreen";   // "true" / "false" → -TEDfull / +TEDfull
}

// ─── Lanceur VICE — Commodore Plus/4 et C16 (xplus4) ─────────────────────────
// Frère de ViceC64Launcher (même outil VICE). Contrairement à C128/VIC-20/PET,
// le Plus/4 et le C16 ne sont PAS deux exécutables séparés : VICE les émule
// tous les deux via le MÊME binaire `xplus4`, la différence n'étant qu'un
// modèle (mémoire 64 Kio pour le Plus/4, 16 Kio pour le C16 — confirmé par le
// changelog officiel VICE qui mentionne explicitement la sélection de modèle
// "c16/c116/c232" sur cet exécutable). D'où un seul type/launcher DemoBase
// pour les deux, avec un simple choix de modèle plutôt que deux entrées
// d'émulateur distinctes.
//
// Le Plus/4 utilise sa propre puce TED qui combine vidéo ET son (contrairement
// au C64/C128 où VIC-II et SID sont deux puces séparées) — donc PAS de réglage
// SID ici, le Plus/4 n'a pas de SID intégré (un SID est disponible uniquement
// via une cartouche d'extension optionnelle et rare, non exposée pour cette
// v1, cf. manuel : "Sid Cartridge on the Plus4 (xplus4)" routé comme un simple
// port joystick). Plein écran : confirmé par le manuel comme une ressource
// dédiée "TEDFullscreenMode (xplus4 only)" — la puce TED a donc bien sa propre
// famille `-TEDfull`/`+TEDfull`, comme VICII pour le C64/C128, VIC pour le
// VIC-20 et CRTC pour le PET ; explicitement confirmé exclu de
// VICIIFullscreen par le manuel ("all emulators except xcbm2, xpet, xplus4,
// xvic and vsid").

public class VicePlus4Launcher
{
    private readonly PreferencesService _prefs;

    // Le Plus/4 et le C16 utilisent un lecteur 1541-compatible via IEC comme
    // le C64 — .prg en premier, puis disque/datassette/cartouche.
    private static readonly string[] PrgExtensions   = [".prg"];
    private static readonly string[] OtherExtensions = [".d64", ".t64", ".tap", ".crt"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public VicePlus4Launcher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[VICE-PLUS4] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"VICE (Plus/4) introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[VICE-PLUS4] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[VICE-PLUS4] ZIP extrait → fichier choisi : {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "VICEPLUS4", friendlyName: "VicePlus4");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Désactive systématiquement la confirmation de sortie de VICE (demande
        // utilisateur) — en dur, pas de réglage exposé dans le profil/l'émulateur.
        sb.Append("+confirmonexit ");

        var model = settings.GetValueOrDefault(VicePlus4Settings.Model);
        sb.Append($"-model {(string.IsNullOrWhiteSpace(model) ? "plus4" : model)}");

        var region = settings.GetValueOrDefault(VicePlus4Settings.Region);
        sb.Append(region == "ntsc" ? " -ntsc" : " -pal");

        sb.Append(settings.GetValueOrDefault(VicePlus4Settings.TrueDrive) == "true"
            ? " -drive8truedrive"
            : " +drive8truedrive");

        sb.Append(settings.GetValueOrDefault(VicePlus4Settings.FullScreen) == "true" ? " -TEDfull" : " +TEDfull");

        sb.Append($" \"{file}\"");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
        {
            var extra = EmulatorLaunchService.SubstituteVars(config.CommandLine, file);
            sb.Append(' ').Append(extra);
        }

        return sb.ToString();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        // Dossier court (Id) plutôt que le titre complet — cf. bug MAX_PATH constaté.
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("plus4", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        var prg = files.FirstOrDefault(f => PrgExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (prg != null) return prg;

        var other = files.FirstOrDefault(f => OtherExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (other != null) return other;

        return files.FirstOrDefault(f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
