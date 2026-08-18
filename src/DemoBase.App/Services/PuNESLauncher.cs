using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings puNES ──────────────────────────────────────────────────

public static class PuNESKeys
{
    public const string FullScreen = "fullscreen"; // "true" / "false"
    public const string Scale      = "scale";      // "1x" / "2x" / "3x" / "4x"
    public const string Filter     = "filter";     // "none" / "hq2x" / etc.
    public const string VSync      = "vsync";      // "true" / "false"
    public const string Portable   = "portable";   // "true" / "false"
}

// ─── Lanceur puNES ───────────────────────────────────────────────────────────
// puNES — émulateur NES/Famicom haute précision (2ème plus précis après Mesen).
// https://github.com/punesemu/puNES
//
// Commande :
//   punes64.exe [--portable] [-u yes|no] [-s 1x|2x|3x|4x] [-i <filter>]
//               [-v on|off] "<fichier>"
//
// Options :
//   --portable   — mode portable : configs/saves dans le répertoire de l'exe
//                  (alternative : renommer punes64.exe → punes64_p.exe)
//   -u yes|no    — fullscreen
//   -s 1x|2x|3x|4x — échelle d'affichage (mode fenêtré)
//   -i <filter>  — filtre vidéo (voir FilterOptions)
//   -v on|off    — VSync
//
// Formats supportés nativement (y compris depuis archives ZIP/7z/RAR) :
//   .nes          — ROM iNES/NES 2.0
//   .unf / .unif  — UNIF
//   .fds          — Famicom Disk System (requiert disksys.rom dans bios/)
//   .nsf / .nsfe / .nsf2 — NSF music player
//   .fm2          — input recording
//   .zip / .7z / .rar    — archives (lecture native, pas d'extraction)

public class PuNESLauncher
{
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private readonly PreferencesService _prefs;

    public PuNESLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PUNES] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"puNES introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        // puNES sait ouvrir un .zip contenant UN SEUL rom, mais les zips de la scène
        // démo contiennent souvent aussi un .nfo/.diz/une image en plus de la .nes —
        // dans ce cas puNES n'arrive pas à choisir et affiche l'écran "pas de
        // cartouche" (neige), quel que soit le contenu. On extrait donc et on choisit
        // nous-mêmes le fichier ROM, comme pour les autres émulateurs.
        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[PUNES] ZIP extrait → {actualFile}");
        }

        // Testé manuellement par l'utilisateur : "punes32 --fullscreen yes fichier.nes"
        // ne charge QUE si le répertoire courant est celui du fichier ET que le nom
        // passé est un simple nom de fichier — pas un chemin complet. Notre chemin
        // absolu ("C:\-= CodeSources =-\...\Working\Configs\extracted\...\NOM.nes")
        // contient des espaces et des "=" ; le parseur de ligne de commande de puNES
        // semble s'y perdre. On reproduit donc exactement le cas qui marche : dossier
        // de travail = dossier du fichier, argument = nom de fichier seul.
        var fileDir  = Path.GetDirectoryName(actualFile)!;
        var fileName = Path.GetFileName(actualFile);

        var args = BuildArguments(config, settings, actualFile, fileName);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "PUNES", friendlyName: "puNES",
            workingDir: fileDir);
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("punes", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null;
        int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = e switch
            {
                ".nes"                       => 5,
                ".unf" or ".unif"            => 4,
                ".fds"                       => 3,
                ".nsf" or ".nsfe" or ".nsf2" => 2,
                ".fm2"                       => 1,
                _                            => 0,
            };
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file, string fileNameForArg)
    {
        var sb = new StringBuilder();

        // --portable : configs/saves dans le répertoire de l'exe
        if (settings.GetValueOrDefault(PuNESKeys.Portable, "true") != "false")
            sb.Append("--portable ");

        // -u / --fullscreen : TOUJOURS DÉSACTIVÉ (seul flag concerné par le bug).
        // Confirmé par test manuel de l'utilisateur : même avec la bonne syntaxe
        // ("--fullscreen yes" — alias long de "-u yes", donc ce n'était pas un
        // problème de nom de flag), le rendu plein écran reste corrompu sur cette
        // machine (deux adaptateurs D3D9 détectés pour le même GPU, cf. logs). C'est
        // un bug de puNES lui-même, pas quelque chose qu'on peut corriger depuis la
        // ligne de commande. On reste donc en fenêtré ; contournement utilisateur :
        // cocher "Save settings on exit" dans puNES, Alt+Entrée une fois, fermer — le
        // plein écran est alors persisté dans SA config et fonctionne correctement.
        //
        // if (settings.GetValueOrDefault(PuNESKeys.FullScreen) == "true")
        //     sb.Append("--fullscreen yes ");

        // -s / -i / -v : réactivés le 2026-07-24 — seul le fullscreen était impliqué
        // dans le bug de rendu D3D9 ci-dessus, scale/filtre/vsync avaient été
        // désactivés par prudence à l'époque, pas parce qu'ils posaient problème.
        var scale = settings.GetValueOrDefault(PuNESKeys.Scale, "2x") ?? "2x";
        if (!string.IsNullOrWhiteSpace(scale) && scale != "auto")
            sb.Append($"-s {scale} ");

        var filter = settings.GetValueOrDefault(PuNESKeys.Filter, "none") ?? "none";
        sb.Append($"-i {filter} ");

        var vsync = settings.GetValueOrDefault(PuNESKeys.VSync, "true") != "false" ? "on" : "off";
        sb.Append($"-v {vsync} ");

        // Paramètres additionnels du profil (chemin complet disponible pour {file})
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file)).Append(' ');

        // Nom de fichier SEUL (pas le chemin complet) : testé et confirmé par
        // l'utilisateur — avec le répertoire de travail réglé sur le dossier du
        // fichier (cf. LaunchAsync), c'est la seule combinaison qui charge la ROM.
        sb.Append($"\"{fileNameForArg}\"");
        return sb.ToString().TrimEnd();
    }
}
