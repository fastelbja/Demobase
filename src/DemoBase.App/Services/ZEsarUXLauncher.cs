using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings ZEsarUX ────────────────────────────────────────────────

public static class ZEsarUXSettings
{
    public const string Machine    = "machine";    // ID machine (voir liste complète)
    public const string FullScreen = "fullscreen"; // "true" / "false"
}

// ─── Lanceur ZEsarUX ─────────────────────────────────────────────────────────
// ZEsarUX 13.0 — émulateur multi-machines Sinclair et compatibles.
// https://github.com/chernandezba/zesarux
//
// Commande de base :
//   zesarux.exe [--machine <ID>] [--fullscreen] <fichier>
//
// Le fichier est passé directement sans flag — ZEsarUX détecte le type
// (équivalent à SmartLoad). Pour .mmc/.img (Spectrum Next) on utilise
// les options expert --enable-mmc / --enable-divmmc-ports / --mmc-file.
//
// IDs machine (extraits du --help officiel v13.0) :
//   ZX80, ZX81, 16k, 48k, 128k, P2, P2A40, P340, Pentagon, TBBlue, Sam, ACE…
//   Voir ZEsarUXSettingsViewModel.MachineOptions pour la liste complète.

public class ZEsarUXLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public ZEsarUXLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[ZESARUX] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"ZEsarUX introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[ZESARUX] ZIP extrait → {actualFile}");
        }

        // ZEsarUX ne charge les disques TR-DOS que via .trd (--trd-file) — le .scl doit
        // être converti au préalable (sinon uniquement possible à la main dans le
        // sélecteur interne, touche espace). Conversion best-effort : en cas d'échec, on
        // retombe sur le .scl original tel quel (comportement inchangé par rapport à avant).
        if (Path.GetExtension(actualFile).Equals(".scl", StringComparison.OrdinalIgnoreCase))
        {
            var trdPath = Path.ChangeExtension(actualFile, ".trd");
            var (converted, error) = await Task.Run(() => SclToTrdConverter.Convert(actualFile, trdPath));
            if (converted)
            {
                System.Diagnostics.Debug.WriteLine($"[ZESARUX] .scl converti en .trd → {trdPath}");
                actualFile = trdPath;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ZESARUX] Conversion .scl→.trd échouée ({error}) — envoi du .scl tel quel");
            }
        }

        var machine = settings.GetValueOrDefault(ZEsarUXSettings.Machine) ?? "48k";
        if (string.IsNullOrWhiteSpace(machine)) machine = "48k";

        // TR-DOS (.trd, y compris les .scl convertis ci-dessus) : sur un Spectrum standard
        // (48k/128k/+2/+3...), TR-DOS n'est accessible que via NMI ("Magic Button") — sans
        // ça, SmartLoad monte le disque mais la machine démarre en BASIC normal en
        // l'ignorant complètement (même cause que le problème rencontré avec Speccy).
        //
        // Deux tentatives d'automatisation abandonnées :
        //  1. Rebinder une touche sur NMI via --def-f-function puis la simuler : ZEsarUX a
        //     refusé la syntaxe ("invalid key for f-function").
        //  2. Protocole distant ZRCP (--enable-remoteprotocol + commande "generate-nmi") :
        //     fonctionnel mais demande une exception pare-feu Windows au premier lancement
        //     — trop intrusif pour les utilisateurs.
        // Solution retenue : expliquer les deux commandes TR-DOS standard à l'utilisateur
        // (RANDOMIZE USR 15616 puis RUN "NOM"), via une popup ponctuelle. Pas d'automation,
        // pas de pare-feu — juste la manip telle qu'elle se fait sur un vrai Spectrum.
        var isTrDos = Path.GetExtension(actualFile).Equals(".trd", StringComparison.OrdinalIgnoreCase);
        if (isTrDos)
            await MaybeShowTrDosInfoAsync();

        var args = BuildArguments(config, settings, machine, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "ZESARUX", friendlyName: "ZEsarUX");
    }

    // ── Popup d'info TR-DOS (.trd) ───────────────────────────────────────────────

    private const string TrDosInfoDialogPrefKey = "zesarux.trdos_info_dialog.hidden";

    /// <summary>
    /// Affiche la popup expliquant RANDOMIZE USR 15616 / RUN "NOM", sauf si l'utilisateur
    /// a déjà coché "Ne plus afficher ce message" lors d'un lancement précédent.
    /// </summary>
    private async Task MaybeShowTrDosInfoAsync()
    {
        try
        {
            var hidden = await _prefs.GetAsync(TrDosInfoDialogPrefKey);
            if (hidden == "true") return;

            var dontShowAgain = false;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dlg = new DemoBase.App.Views.TrDosInfoDialog
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };
                dlg.ShowDialog();
                dontShowAgain = dlg.DontShowAgain;
            });

            if (dontShowAgain)
                await _prefs.SetAsync(TrDosInfoDialogPrefKey, "true");
        }
        catch (Exception ex)
        {
            // Best-effort — une popup ratée ne doit jamais empêcher le lancement.
            Debug.WriteLine($"[ZESARUX] Popup info TR-DOS : échec — {ex.Message}");
        }
    }

    private static string BuildArguments(
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      machine,
        string                      file)
    {
        var sb  = new StringBuilder();
        var ext = Path.GetExtension(file).ToLowerInvariant();

        // --noconfigfile en premier pour éviter que ZEsarUX interprète
        // le chemin du fichier comme un paramètre de config inconnu
        sb.Append("--noconfigfile ");

        // Masque le splash/logo de démarrage et supprime la confirmation "yes/no" +
        // le fondu à la fermeture (options officielles, confirmées via --experthelp
        // v13.0 : section "General Settings").
        sb.Append("--nowelcomemessage ");
        sb.Append("--quickexit ");

        sb.Append($"--machine {machine} ");

        if (settings.GetValueOrDefault(ZEsarUXSettings.FullScreen) == "true")
            sb.Append("--fullscreen ");

        // Image MMC Spectrum Next : options expert nécessaires
        if (ext is ".mmc" or ".img")
        {
            sb.Append("--enable-mmc ");
            sb.Append("--enable-divmmc-ports ");
            sb.Append($"--mmc-file \"{file}\"");
        }
        else if (ext == ".trd")
        {
            // SmartLoad brut d'un .trd NE suffit PAS : sans l'interface Beta Disk
            // explicitement activée, l'adresse 15616 (entrée TR-DOS standard) ne fait
            // rien sur une machine 48k/128k — d'où l'absence de catalogue constatée.
            // Il faut activer le matériel Beta Disk lui-même, pas juste monter le fichier.
            sb.Append("--enable-betadisk ");
            sb.Append("--enable-trd ");
            sb.Append($"--trd-file \"{file}\"");
        }
        else
        {
            // Tous les autres formats (dont .mdv QL) : SmartLoad — ZEsarUX détecte le type
            sb.Append($"\"{file}\"");
        }

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("zesarux", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null;
        int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = e switch
            {
                ".nex"            => 8,
                ".mmc" or ".img"  => 7,
                ".mdv"            => 6,  // Microdrive QL
                // .trd/.scl repassés en tête (au niveau d'avant le contournement tape) :
                // le .scl est maintenant converti en .trd avant lancement (cf. LaunchAsync
                // → SclToTrdConverter), donc les deux fonctionnent désormais aussi bien
                // qu'un .tap/.tzx — et un disque est en général la version "complète"
                // d'une release plutôt que la version tape.
                ".trd" or ".scl"  => 5,
                ".sna" or ".z80"  => 4,
                ".szx" or ".zsf"  => 3,
                ".tzx" or ".tap"  => 2,
                ".p" or ".81"     => 2,
                _                 => 1,
            };
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
