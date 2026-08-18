using DemoBase.Core.Models;
using DemoBase.Data;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings MAME ───────────────────────────────────────────────────

public static class MameKeys
{
    /// <summary>Nom de la machine/driver MAME (ex. "neocdz", "neogeo"). REQUIS —
    /// MAME ne devine pas le système à partir du fichier.</summary>
    public const string Machine    = "machine";
    /// <summary>Slot média : "cdrm", "cart", "flop1"… ou vide/"auto" (déduit de
    /// l'extension : image CD → cdrm, disquette → flop1, sinon fichier positionnel).</summary>
    public const string MediaSlot  = "mediaslot";
    /// <summary>Dossier(s) de ROMs/BIOS supplémentaires (-rompath). Indispensable
    /// pour les systèmes à BIOS comme Neo Geo CD (neocdz.zip).</summary>
    public const string RomPath    = "rompath";
    public const string FullScreen = "fullscreen";  // "true" / "false"
    public const string SkipInfo   = "skipinfo";     // "true" (défaut) / "false" — -skip_gameinfo
}

// ─── Lanceur MAME ─────────────────────────────────────────────────────────────
// MAME — émulateur multi-système/arcade. https://github.com/mamedev/mame
//
// Ligne de commande :
//   mame <machine> [-<slot média> "<fichier>"] [-rompath "<dossiers>"]
//        [-skip_gameinfo] [-window]
//
// Exemples :
//   Neo Geo CD :  mame neocdz -cdrm "jeu.cue" -rompath "C:\...\bios" -skip_gameinfo
//   Arcade     :  mame <romset>            (le fichier romset est cherché dans le rompath)
//
// Notes importantes :
//   • <machine> est OBLIGATOIRE (le réglage 'machine' du profil). MAME ne déduit
//     pas le système depuis l'extension.
//   • Les systèmes à BIOS (Neo Geo CD → neocdz.zip, Neo Geo → neogeo.zip) exigent
//     que le BIOS soit trouvable via -rompath (réglage 'rompath') OU via mame.ini.
//   • MAME démarre en plein écran par défaut → on ajoute -window quand l'option
//     plein écran n'est pas cochée.
//   • -skip_gameinfo passe l'écran d'avertissement/infos au démarrage.

public class MameLauncher
{
    private readonly PreferencesService _prefs;

    // Extensions par type de média (pour la déduction automatique du slot).
    private static readonly HashSet<string> CdExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cue", ".chd", ".iso", ".gdi", ".cdi", ".cdr" };

    private static readonly HashSet<string> FloppyExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".dsk", ".d88", ".d77", ".2d", ".mfi", ".img", ".st", ".msa" };

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public MameLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[MAME] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"MAME introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var machine = settings.GetValueOrDefault(MameKeys.Machine, string.Empty)?.Trim();
        if (string.IsNullOrWhiteSpace(machine))
            return new(false,
                "MAME : aucune machine/driver définie. Renseignez le champ « machine » "
                + "du profil (ex. « neocdz » pour Neo Geo CD).");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[MAME] ZIP extrait → {actualFile}");
        }

        var args = BuildArguments(config, settings, machine!, actualFile);

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "MAME", friendlyName: "MAME");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string machine, string file)
    {
        var sb = new StringBuilder();

        // <machine> en premier.
        sb.Append($"\"{machine}\"");

        // Slot média : réglage explicite prioritaire, sinon déduction par extension.
        var slot = settings.GetValueOrDefault(MameKeys.MediaSlot, string.Empty)?.Trim();
        if (string.IsNullOrWhiteSpace(slot) || slot.Equals("auto", StringComparison.OrdinalIgnoreCase))
            slot = AutoMediaSlot(file);
        slot = slot.TrimStart('-'); // tolère que l'utilisateur ait tapé "-cdrm"

        if (!string.IsNullOrWhiteSpace(slot))
            sb.Append($" -{slot} \"{file}\"");
        else
            sb.Append($" \"{file}\""); // positionnel (romset arcade / software list)

        // -rompath pour trouver le BIOS (neocdz.zip, neogeo.zip…).
        var rompath = settings.GetValueOrDefault(MameKeys.RomPath, string.Empty)?.Trim();
        if (!string.IsNullOrWhiteSpace(rompath))
            sb.Append($" -rompath \"{rompath}\"");

        // -skip_gameinfo (activé par défaut).
        if (settings.GetValueOrDefault(MameKeys.SkipInfo, "true") != "false")
            sb.Append(" -skip_gameinfo");

        // MAME démarre en plein écran par défaut → -window si non coché.
        if (settings.GetValueOrDefault(MameKeys.FullScreen) != "true")
            sb.Append(" -window");

        // Paramètres additionnels du profil.
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    /// <summary>Déduit le slot média MAME depuis l'extension du fichier. Chaîne
    /// vide = pas de slot (le fichier est passé en argument positionnel).</summary>
    private static string AutoMediaSlot(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (CdExtensions.Contains(ext))     return "cdrm";
        if (FloppyExtensions.Contains(ext)) return "flop1";
        return string.Empty;
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("mame", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : image CD (.cue en tête — c'est le descripteur, pas les .bin/.wav),
        // puis disquette, puis premier fichier non-texte.
        var cue = files.FirstOrDefault(f =>
            Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase));
        if (cue != null) return cue;

        var cd = files.FirstOrDefault(f => CdExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (cd != null) return cd;

        var floppy = files.FirstOrDefault(f => FloppyExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (floppy != null) return floppy;

        return files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault()
               ?? zipPath;
    }
}
