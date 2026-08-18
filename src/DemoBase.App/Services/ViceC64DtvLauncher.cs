using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings VICE/C64-DTV ───────────────────────────────────────────

public static class ViceC64DtvSettings
{
    public const string Region     = "region";      // "pal" / "ntsc" → -pal / -ntsc
    public const string DtvRev     = "dtvrev";       // "2" / "3" → -dtvrev
    public const string TrueDrive  = "truedrive";    // "true" / "false" → -drive8truedrive / +drive8truedrive
    public const string FullScreen = "fullscreen";   // "true" / "false" → -VICIIfull / +VICIIfull
}

// ─── Lanceur VICE — Commodore 64 DTV (x64dtv) ────────────────────────────────
// Frère de ViceC64Launcher (même outil VICE, exécutable séparé). Le DTV
// ("Direct-to-TV", la puce intégrée au joystick-console vendu dans le
// commerce) reste un dérivé du C64 — il réutilise le même résultat
// VICIIFullscreen que le C64/C128 (confirmé par le manuel officiel :
// "Boolean specifying whether to use fullscreen mode or not (all emulators
// except xcbm2, xpet, xplus4, xvic and vsid)" — x64dtv n'est PAS dans la liste
// d'exclusion, donc `-VICIIfull`/`+VICIIfull` s'applique tel quel) — mais le
// SID n'est PAS un vrai SID : c'est une puce compatible intégrée au FPGA,
// appelée "DTVSID" dans le manuel. Le manuel documente une valeur DTVSID dédiée
// pour -sidengine/-sidmodel, mais **testé en conditions réelles : ce flag est
// rejeté par le parseur de ligne de commande de la build x64dtv de
// l'utilisateur** ("Argument '2' not valid for option `-sidengine'"). Comme le
// DTV n'a qu'une seule puce son possible de toute façon (pas un vrai choix
// comme FastSID/ReSID sur un vrai C64), -sidengine/-sidmodel ne sont PAS
// passés du tout ici — x64dtv sélectionne sa propre puce par défaut sans aide.
// Le réglage propre au DTV est la révision matérielle (`-dtvrev 2/3`,
// confirmée dans l'index officiel des options de ligne de commande).

public class ViceC64DtvLauncher
{
    private readonly PreferencesService _prefs;

    // Le DTV reste compatible C64 pour l'essentiel des formats logiciels :
    // .prg en premier, puis images disque/datassette/cartouche.
    private static readonly string[] PrgExtensions   = [".prg"];
    private static readonly string[] OtherExtensions = [".d64", ".d71", ".d81", ".t64", ".tap", ".crt"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public ViceC64DtvLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[VICE-C64DTV] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"VICE (C64-DTV) introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[VICE-C64DTV] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[VICE-C64DTV] ZIP extrait → fichier choisi : {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "VICEC64DTV", friendlyName: "ViceC64Dtv");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Désactive systématiquement la confirmation de sortie de VICE (demande
        // utilisateur) — en dur, pas de réglage exposé dans le profil/l'émulateur.
        sb.Append("+confirmonexit ");

        var region = settings.GetValueOrDefault(ViceC64DtvSettings.Region);
        sb.Append(region == "ntsc" ? "-ntsc" : "-pal");

        var dtvRev = settings.GetValueOrDefault(ViceC64DtvSettings.DtvRev);
        sb.Append($" -dtvrev {(string.IsNullOrWhiteSpace(dtvRev) ? "3" : dtvRev)}");

        // NOTE : -sidengine/-sidmodel ne sont PAS forcés ici. Testé en conditions réelles :
        // "-sidengine 2" (valeur DTVSID documentée par le manuel VICE) est rejeté par le
        // parseur de ligne de commande sur la build x64dtv de l'utilisateur ("Argument '2'
        // not valid for option `-sidengine'"), malgré la documentation officielle qui liste
        // cette valeur. Le DTV n'ayant qu'une seule puce son possible (DTVSID, intégrée au
        // FPGA, ce n'est pas un vrai choix comme FastSID/ReSID sur un vrai C64), x64dtv la
        // sélectionne déjà par défaut sans qu'il soit nécessaire de le préciser — on laisse
        // donc VICE gérer ça lui-même plutôt que de forcer un flag qui fait échouer le lancement.

        sb.Append(settings.GetValueOrDefault(ViceC64DtvSettings.TrueDrive) == "true"
            ? " -drive8truedrive"
            : " +drive8truedrive");

        sb.Append(settings.GetValueOrDefault(ViceC64DtvSettings.FullScreen) == "true" ? " -VICIIfull" : " +VICIIfull");

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
            WorkingPaths.GetZipSignature("dtv", releaseId, zipPath));
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
