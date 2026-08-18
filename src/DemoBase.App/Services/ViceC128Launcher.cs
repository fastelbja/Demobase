using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings VICE/C128 ──────────────────────────────────────────────

public static class ViceC128Settings
{
    public const string Region     = "region";      // "pal" / "ntsc" → -pal / -ntsc
    public const string SidEngine  = "sidengine";    // "0" FastSID / "1" ReSID → -sidengine
    public const string SidModel   = "sidmodel";     // "0" 6581 / "1" 8580 / "2" 8580+digiboost → -sidmodel
    public const string Reu        = "reu";          // "true" / "false" → -reu / +reu
    public const string ReuSize    = "reusize";      // Kio : "128".."16384" → -reusize
    public const string TrueDrive  = "truedrive";    // "true" / "false" → -drive8truedrive / +drive8truedrive
    public const string Go64       = "go64";         // "true" / "false" → -go64 / +go64
    public const string Columns    = "columns";      // "40" / "80" → -40col / -80col
    public const string FullScreen = "fullscreen";   // "true" / "false" → -VICIIfull / +VICIIfull
}

// ─── Lanceur VICE — Commodore 128 (x128) ─────────────────────────────────────
// Frère de ViceC64Launcher (même outil VICE, exécutable séparé — chez VICE
// chaque machine Commodore a son propre binaire, contrairement à WinUAE où
// un seul exe gère plusieurs modèles via un flag). Confirmé via le manuel
// officiel (vice_7.html, section "7.1.4 VIC-II settings" : "these settings
// control the emulation of the VIC-II... used in BOTH the C64 and the C128")
// que le C128 PARTAGE les mêmes puces VIC-II et SID que le C64 — donc région,
// moteur/modèle SID, REU, vraie émulation de lecteur et plein écran (-VICIIfull,
// lui aussi explicitement documenté comme valable pour le C128) sont identiques
// au C64 et repris à l'identique ici. Deux réglages s'ajoutent, propres au
// C128 : `-40col`/`-80col` (mode colonnes) et `-go64`/`+go64` (démarrer
// directement en mode compatible C64 plutôt qu'en mode natif 80 colonnes —
// confirmé via un exemple réel du wiki VICE et une feature-request du tracker
// officiel). Pas de réglage VDC (puce 80 colonnes) séparé exposé pour cette v1
// — pertinent seulement en mode natif, et les valeurs par défaut de VICE
// suffisent pour l'écrasante majorité des cas.

public class ViceC128Launcher
{
    private readonly PreferencesService _prefs;

    // Mêmes extensions que le C64 (x128 reste compatible IEC/disquette C64) :
    // .prg en premier, puis images disque/datassette/cartouche.
    private static readonly string[] PrgExtensions   = [".prg"];
    private static readonly string[] OtherExtensions = [".d64", ".d71", ".d81", ".t64", ".tap", ".crt"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public ViceC128Launcher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[VICE-C128] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"VICE (C128) introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[VICE-C128] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[VICE-C128] ZIP extrait → fichier choisi : {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "VICEC128", friendlyName: "ViceC128");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Désactive systématiquement la confirmation de sortie de VICE (demande
        // utilisateur) — en dur, pas de réglage exposé dans le profil/l'émulateur.
        sb.Append("+confirmonexit ");

        var region = settings.GetValueOrDefault(ViceC128Settings.Region);
        sb.Append(region == "ntsc" ? "-ntsc" : "-pal");

        var sidEngine = settings.GetValueOrDefault(ViceC128Settings.SidEngine);
        sb.Append($" -sidengine {(string.IsNullOrWhiteSpace(sidEngine) ? "1" : sidEngine)}");

        var sidModel = settings.GetValueOrDefault(ViceC128Settings.SidModel);
        sb.Append($" -sidmodel {(string.IsNullOrWhiteSpace(sidModel) ? "0" : sidModel)}");

        if (settings.GetValueOrDefault(ViceC128Settings.Reu) == "true")
        {
            var reuSize = settings.GetValueOrDefault(ViceC128Settings.ReuSize);
            sb.Append(" -reu");
            sb.Append($" -reusize {(string.IsNullOrWhiteSpace(reuSize) ? "512" : reuSize)}");
        }
        else
        {
            sb.Append(" +reu");
        }

        sb.Append(settings.GetValueOrDefault(ViceC128Settings.TrueDrive) == "true"
            ? " -drive8truedrive"
            : " +drive8truedrive");

        // Mode au démarrage : C128 natif (80 colonnes) par défaut, ou compatible C64
        // si une production l'exige explicitement (beaucoup de demos C128 de la scène
        // sont en réalité des demos C64 qui tournent en mode go64 sur cette machine).
        sb.Append(settings.GetValueOrDefault(ViceC128Settings.Go64) == "true" ? " -go64" : " +go64");

        var columns = settings.GetValueOrDefault(ViceC128Settings.Columns);
        sb.Append(columns == "40" ? " -40col" : " -80col");

        sb.Append(settings.GetValueOrDefault(ViceC128Settings.FullScreen) == "true" ? " -VICIIfull" : " +VICIIfull");

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
            WorkingPaths.GetZipSignature("c128", releaseId, zipPath));
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
