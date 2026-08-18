using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings VICE ───────────────────────────────────────────────────

public static class ViceC64Settings
{
    public const string Region     = "region";      // "pal" / "ntsc" → -pal / -ntsc
    public const string SidEngine  = "sidengine";    // "0" FastSID / "1" ReSID → -sidengine
    public const string SidModel   = "sidmodel";     // "0" 6581 / "1" 8580 / "2" 8580+digiboost → -sidmodel
    public const string Reu        = "reu";          // "true" / "false" → -reu / +reu
    public const string ReuSize    = "reusize";      // Kio : "128".."16384" → -reusize
    public const string TrueDrive  = "truedrive";    // "true" / "false" → -drive8truedrive / +drive8truedrive
    public const string FullScreen = "fullscreen";   // "true" / "false" → -VICIIfull / +VICIIfull
}

// ─── Lanceur VICE (https://github.com/VICE-Team/svn-mirror) — Commodore 64 ──
// Exécutable cible : x64sc, l'émulateur C64 "accurate" (cycle-exact) du projet
// VICE — recommandé par le projet lui-même pour l'émulation sérieuse depuis
// VICE 2.3, et seul binaire C64 encore construit par défaut depuis VICE 3.4
// (l'ancien x64 "fast" n'est plus livré dans les paquets officiels). VICE
// fournit aussi x128/xvic/xpet/xplus4/xcbm2 pour les autres machines 8 bits
// Commodore, mais le slot EmulatorType.ViceC64 de DemoBase cible ici
// spécifiquement le C64 — pas de sélecteur de "machine" comme WinUAE : chez
// VICE chaque machine est un exécutable séparé, donc le C128/VIC-20/PET sont
// gérés par des types/launchers frères distincts (ViceC128/ViceVic20/VicePet)
// plutôt que par un réglage supplémentaire ici.
//
// Contrairement à WinUAE (Kickstart) ou Hatari (TOS), aucune ROM système à
// fournir par l'utilisateur : VICE embarque son propre jeu de ROMs C64 (et les
// ROMs de lecteur de disquette 1541/1571/1581 nécessaires à la vraie émulation
// de lecteur ci-dessous) — pas de champ "ROM" exposé ici, comme pour CSFEC.
//
// Lancement du fichier : argument positionnel brut (comme CSFEC), PAS de
// MOUNT façon DOSBox-X — la doc VICE (section "Invoking the emulators")
// documente explicitement que passer un .prg/.d64/.d71/.d81/.t64/.tap en
// dernier argument attache le support ET lance automatiquement le premier
// programme trouvé dessus ("the emulator will... run the first program on
// it"), sans configuration supplémentaire.
//
// Réglages exposés volontairement restreints aux options qui font une vraie
// différence pour FAIRE TOURNER une demo C64 (région PAL/NTSC, moteur+modèle
// SID, REU, vraie émulation de lecteur, plein écran) — pas les ~300 autres
// options de ligne de commande de VICE (cartouches spécifiques, MIDI, RS232,
// RAMLink...) qui ne concernent pas le cas d'usage "lancer une release de la
// scène demo". Documentation utilisée : manuel VICE officiel
// (https://vice-emu.sourceforge.io/vice_7.html, section "7.1 C64/128-specific
// commands and settings").

public class ViceC64Launcher
{
    private readonly PreferencesService _prefs;

    // Priorité du fichier "principal" à l'intérieur d'un ZIP : .prg en premier
    // (programme C64 nu, format le plus courant pour les intros/cracktros
    // d'une seule pièce de la scène), puis les images disque/datassette pour
    // les productions plus grosses avec chargeur multi-parties, puis les
    // images cartouche (rares pour une demo, mais existantes pour certaines
    // intros 4K/8K pensées pour EasyFlash).
    private static readonly string[] PrgExtensions   = [".prg"];
    private static readonly string[] OtherExtensions = [".d64", ".d71", ".d81", ".t64", ".tap", ".crt"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    public ViceC64Launcher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[VICE-C64] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"VICE introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[VICE-C64] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[VICE-C64] ZIP extrait → fichier choisi : {actualFile}");
        }

        var args = BuildArguments(config, settings, actualFile);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "VICEC64", friendlyName: "ViceC64");
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb = new StringBuilder();

        // Désactive systématiquement la confirmation de sortie de VICE (demande
        // utilisateur) — en dur, pas de réglage exposé dans le profil/l'émulateur.
        sb.Append("+confirmonexit ");

        var region = settings.GetValueOrDefault(ViceC64Settings.Region);
        sb.Append(region == "ntsc" ? "-ntsc" : "-pal");

        var sidEngine = settings.GetValueOrDefault(ViceC64Settings.SidEngine);
        sb.Append($" -sidengine {(string.IsNullOrWhiteSpace(sidEngine) ? "1" : sidEngine)}");

        var sidModel = settings.GetValueOrDefault(ViceC64Settings.SidModel);
        sb.Append($" -sidmodel {(string.IsNullOrWhiteSpace(sidModel) ? "0" : sidModel)}");

        // REU explicitement activé OU désactivé à chaque lancement (plutôt que de ne
        // rien dire quand décoché) — évite qu'un réglage laissé dans le vicerc/.ini de
        // l'utilisateur (modifié manuellement, ou par un lancement précédent avec un
        // autre profil) ne fuite silencieusement sur ce profil-ci.
        if (settings.GetValueOrDefault(ViceC64Settings.Reu) == "true")
        {
            var reuSize = settings.GetValueOrDefault(ViceC64Settings.ReuSize);
            sb.Append(" -reu");
            sb.Append($" -reusize {(string.IsNullOrWhiteSpace(reuSize) ? "512" : reuSize)}");
        }
        else
        {
            sb.Append(" +reu");
        }

        // Vraie émulation de lecteur (drive 8) : nécessaire pour les productions qui
        // utilisent un chargeur disque personnalisé (très répandu dans les demos/
        // megademos multi-parties de la scène C64) contournant les routines KERNAL —
        // l'émulation "rapide" par défaut (kernal traps) ne les supporte pas. Coût :
        // chargement au rythme réel d'un 1541 plutôt qu'instantané. Décoché par défaut
        // (suffit pour un .prg simple, le cas le plus courant) ; à cocher au cas par
        // cas si une demo particulière reste bloquée au chargement.
        sb.Append(settings.GetValueOrDefault(ViceC64Settings.TrueDrive) == "true"
            ? " -drive8truedrive"
            : " +drive8truedrive");

        sb.Append(settings.GetValueOrDefault(ViceC64Settings.FullScreen) == "true" ? " -VICIIfull" : " +VICIIfull");

        sb.Append($" \"{file}\"");

        // ── Ligne de commande additionnelle définie sur le profil (optionnelle) ─
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
        // Dossier d'extraction COURT (Id de la release, pas son titre complet) pour éviter
        // de dépasser MAX_PATH (260 caractères) — cf. bug constaté sur WindowsLauncher avec
        // un titre long ; même schéma vulnérable ici, x64sc pourrait échouer à ouvrir le
        // .prg/.d64 sans que .NET ne lève d'exception (Process.Start réussit quand même).
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("c64", releaseId, zipPath));
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
