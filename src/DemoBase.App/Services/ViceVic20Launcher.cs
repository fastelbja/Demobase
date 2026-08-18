using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings VICE/VIC-20 ────────────────────────────────────────────

public static class ViceVic20Settings
{
    public const string Region     = "region";      // "pal" / "ntsc" → -pal / -ntsc
    public const string Memory     = "memory";       // "" / "0" / "1" / "1,2" / "1,2,3" / "0,1,2,3,5" → -memory <liste>
    public const string TrueDrive  = "truedrive";    // "true" / "false" → -drive8truedrive / +drive8truedrive
    public const string FullScreen = "fullscreen";   // "true" / "false" → -VICfull / +VICfull
}

// ─── Lanceur VICE — VIC-20 (xvic) ────────────────────────────────────────────
// Frère de ViceC64Launcher (même outil VICE, exécutable séparé). Contrairement
// au C64/C128, le VIC-20 n'a PAS de puce SID (son minimal généré par la puce
// vidéo VIC elle-même) — donc aucun réglage SidEngine/SidModel ici, ce serait
// sans effet. Pas de REU non plus : l'extension mémoire du VIC-20 fonctionne
// par blocs RAM individuels (0/1/2/3/5, 3 Kio chacun sauf le bloc 5 qui vaut
// 8 Kio), activés via `-memory <liste>` — confirmé par l'exemple officiel du
// manuel VICE ("xvic -memory 3,5") et par le wiki libretro qui documente le
// mapping standard utilisé ici (Aucune/+3K/+8K/+16K/+24K/Toute la mémoire).
// Plein écran : le VIC-20 utilise sa propre puce vidéo "VIC" (pas "VIC-II"),
// d'ailleurs explicitement EXCLU de la ressource VICIIFullscreen par le manuel
// — son équivalent `-VICfull`/`+VICfull` est confirmé par un développeur VICE
// sur le forum officiel Individual Computers (cf. RESUME_PROJET.md pour le
// lien). Vraie émulation de lecteur : générique, partagée avec le C64/C128.

public class ViceVic20Launcher
{
    private readonly PreferencesService _prefs;

    // Le VIC-20 n'a pas de lecteur de disquette intégré comme le C64/C128,
    // mais peut en émuler un via IEC (mêmes formats) — .prg en premier (le
    // plus courant pour les jeux/intros de la scène VIC-20), puis disque/datassette.
    private static readonly string[] PrgExtensions   = [".prg"];
    private static readonly string[] OtherExtensions = [".d64", ".t64", ".tap", ".crt"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public ViceVic20Launcher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[VICE-VIC20] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"VICE (VIC-20) introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[VICE-VIC20] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[VICE-VIC20] ZIP extrait → fichier choisi : {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "VICEVIC20", friendlyName: "ViceVic20");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Désactive systématiquement la confirmation de sortie de VICE (demande
        // utilisateur) — en dur, pas de réglage exposé dans le profil/l'émulateur.
        sb.Append("+confirmonexit ");

        var region = settings.GetValueOrDefault(ViceVic20Settings.Region);
        sb.Append(region == "ntsc" ? "-ntsc" : "-pal");

        var memory = settings.GetValueOrDefault(ViceVic20Settings.Memory);
        if (!string.IsNullOrWhiteSpace(memory))
            sb.Append($" -memory {memory}");

        sb.Append(settings.GetValueOrDefault(ViceVic20Settings.TrueDrive) == "true"
            ? " -drive8truedrive"
            : " +drive8truedrive");

        sb.Append(settings.GetValueOrDefault(ViceVic20Settings.FullScreen) == "true" ? " -VICfull" : " +VICfull");

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
            WorkingPaths.GetZipSignature("vic20", releaseId, zipPath));
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
