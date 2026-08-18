using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings VICE/PET ───────────────────────────────────────────────

public static class VicePetSettings
{
    public const string Model      = "model";       // "8032" etc. → -model <token>
    public const string TrueDrive  = "truedrive";    // "true" / "false" → -drive8truedrive / +drive8truedrive
    public const string FullScreen = "fullscreen";   // "true" / "false" → -CRTCfull / +CRTCfull
}

// ─── Lanceur VICE — PET (xpet) ────────────────────────────────────────────────
// Frère de ViceC64Launcher (même outil VICE, exécutable séparé). Le PET est la
// machine la plus éloignée du C64 dans la famille VICE : ni SID (pas de puce
// son dédiée), ni VIC-II (affichage texte via une puce de type CRTC, section
// "7.7.2 CRTC Settings" du manuel) — donc aucun réglage SidEngine/SidModel/Reu
// ici, sans équivalent sur cette machine. Pas de réglage région PAL/NTSC non
// plus : la doc VICE ne documente pas clairement -pal/-ntsc pour xpet (les
// PET sont avant tout des machines américaines/professionnelles, la distinction
// a peu de sens pratique ici) — volontairement omis plutôt que deviné.
//
// Sélection du modèle (-model <token>) confirmée par un exemple réel du wiki
// VICE ("xpet -model 8296"). Liste réduite à 6 modèles parmi les ~12 valeurs
// possibles pour rester simple côté UI : 3032/4032/8032 (défaut, le modèle
// "gros écran" le plus représentatif)/8096/8296/superpet.
//
// Plein écran : le PET est explicitement exclu de la ressource VICIIFullscreen
// par le manuel (comme le VIC-20) — son équivalent `-CRTCfull`/`+CRTCfull` est
// confirmé directement dans l'index officiel des options de ligne de commande
// (vice_24.html). Vraie émulation de lecteur : générique, partagée avec les
// autres machines VICE.

public class VicePetLauncher
{
    private readonly PreferencesService _prefs;

    // Le PET utilise ses propres formats disque historiques (D80/D82, lecteurs
    // 8050/8250) en plus des formats standards — .prg en premier (programmes
    // BASIC/machine tokenisés, le plus courant pour les démos/outils PET),
    // puis images disque.
    private static readonly string[] PrgExtensions   = [".prg"];
    private static readonly string[] OtherExtensions = [".d64", ".d80", ".d82", ".tap"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public VicePetLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[VICE-PET] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"VICE (PET) introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[VICE-PET] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[VICE-PET] ZIP extrait → fichier choisi : {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "VICEPET", friendlyName: "VicePet");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Désactive systématiquement la confirmation de sortie de VICE (demande
        // utilisateur) — en dur, pas de réglage exposé dans le profil/l'émulateur.
        sb.Append("+confirmonexit ");

        var model = settings.GetValueOrDefault(VicePetSettings.Model);
        sb.Append($"-model {(string.IsNullOrWhiteSpace(model) ? "8032" : model)}");

        sb.Append(settings.GetValueOrDefault(VicePetSettings.TrueDrive) == "true"
            ? " -drive8truedrive"
            : " +drive8truedrive");

        sb.Append(settings.GetValueOrDefault(VicePetSettings.FullScreen) == "true" ? " -CRTCfull" : " +CRTCfull");

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
            WorkingPaths.GetZipSignature("pet", releaseId, zipPath));
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
