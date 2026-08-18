using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings BlueMSX ────────────────────────────────────────────────

public static class BlueMSXKeys
{
    public const string Machine    = "machine";
    public const string FullScreen = "fullscreen";
    public const string Freq       = "freq";
}

// ─── Lanceur BlueMSX ─────────────────────────────────────────────────────────
// BlueMSX — émulateur cycle-accurate MSX, SVI, ColecoVision, Sega SG-1000.
// http://www.bluemsx.com/
//
// Commande complète (source : https://www.msxblue.com/manual/commandlineargs_c.htm) :
//   blueMSX.exe [/fullscreen] [/machine "<nom>"] [/rom1 <file>] [/diskA <file>] [/cas <file>]
//
// Arguments supportés :
//   /rom1 <file>    — ROM cartouche slot 1   /rom2 <file> — slot 2
//   /rom1zip <file> — ROM depuis un ZIP      /rom2zip
//   /diskA <file>   — Disquette drive A      /diskB — drive B
//   /diskAzip <file>— Disquette depuis ZIP   /diskBzip
//   /cas <file>     — Cassette               /caszip — cassette depuis ZIP
//   /machine "<n>"  — Nom de machine (dossier dans Machines/ de l'installation)
//   /fullscreen     — Plein écran au démarrage
//
// BlueMSX gère les ZIP nativement → on utilise /rom1zip, /diskAzip, /caszip
// au lieu d'extraire manuellement.

public class BlueMSXLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public BlueMSXLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[BLUEMSX] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"blueMSX introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        // blueMSX n'a pas de bouton "Fermer" ni de raccourci Alt+F4 classique — seule
        // sa propre combinaison (Ctrl gauche + Pause) quitte l'émulateur. Info pas
        // évidente pour l'utilisateur → popup ponctuelle, même principe que le TR-DOS
        // de ZEsarUX.
        await MaybeShowQuitInfoAsync();

        var args = BuildArguments(config, settings, romPath);
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "BLUEMSX", friendlyName: "blueMSX");
    }

    // ── Popup d'info sur le raccourci pour quitter ──────────────────────────────

    private const string QuitInfoDialogPrefKey = "bluemsx.quit_info_dialog.hidden";

    /// <summary>
    /// Affiche la popup expliquant Ctrl (gauche) + Pause pour quitter blueMSX, sauf si
    /// l'utilisateur a déjà coché "Ne plus afficher ce message" lors d'un lancement précédent.
    /// </summary>
    private async Task MaybeShowQuitInfoAsync()
    {
        try
        {
            var hidden = await _prefs.GetAsync(QuitInfoDialogPrefKey);
            if (hidden == "true") return;

            var dontShowAgain = false;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dlg = new DemoBase.App.Views.BlueMSXInfoDialog
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };
                dlg.ShowDialog();
                dontShowAgain = dlg.DontShowAgain;
            });

            if (dontShowAgain)
                await _prefs.SetAsync(QuitInfoDialogPrefKey, "true");
        }
        catch (Exception ex)
        {
            // Best-effort — une popup ratée ne doit jamais empêcher le lancement.
            Debug.WriteLine($"[BLUEMSX] Popup info quitter : échec — {ex.Message}");
        }
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string file)
    {
        var sb  = new StringBuilder();
        var ext = Path.GetExtension(file).ToLowerInvariant();

        // /fullscreen
        if (settings.GetValueOrDefault(BlueMSXKeys.FullScreen) == "true")
            sb.Append("/fullscreen ");

        // /machine "<nom>" (guillemets obligatoires car le nom peut contenir des espaces)
        var machine = settings.GetValueOrDefault(BlueMSXKeys.Machine, "MSX2 - C-BIOS") ?? "MSX2 - C-BIOS";
        if (!string.IsNullOrWhiteSpace(machine))
            sb.Append($"/machine \"{machine}\" ");

        // Slot/drive selon l'extension — /rom1 lit aussi les ZIP directement
        var slot = ext == ".zip" ? GuessSlotFromZip(file) : ExtToSlot(ext);
        sb.Append($"{slot} \"{file}\"");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            sb.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, file));

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Retourne le flag blueMSX correct selon l'extension du fichier.
    /// Source : https://www.msxblue.com/manual/commandlineargs_c.htm
    /// </summary>
    private static string ExtToSlot(string ext) => ext switch
    {
        ".rom" or ".mx1" or ".mx2" or ".ri" => "/rom1",   // cartouche ROM slot 1
        ".dsk" or ".di1" or ".di2"           => "/diskA",  // disquette drive A
        ".cas"                               => "/cas",    // cassette
        _                                    => "/rom1",   // défaut → slot 1
    };

    /// <summary>
    /// Pour un ZIP, inspecte l'extension du premier fichier utile pour deviner le flag.
    /// </summary>
    private static string GuessSlotFromZip(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                var e = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (IgnoredExtensions.Contains(e) || string.IsNullOrEmpty(entry.Name)) continue;
                return e switch
                {
                    ".rom" or ".mx1" or ".mx2" or ".ri" => "/rom1",
                    ".dsk" or ".di1" or ".di2"           => "/diskA",
                    ".cas"                               => "/cas",
                    _                                    => "/rom1",
                };
            }
        }
        catch { }
        return "/rom1";
    }
}
