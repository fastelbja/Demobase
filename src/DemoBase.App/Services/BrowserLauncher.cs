using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Browser ────────────────────────────────────────────────

public static class BrowserSettings
{
    /// <summary>
    /// Mode navigateur : "default" (navigateur système), "custom" (ExecutablePath).
    /// </summary>
    public const string Mode            = "mode";
    /// <summary>
    /// Ajoute --allow-file-access-from-files au lancement du navigateur
    /// pour les démos qui chargent des ressources locales (WebGL, WASM, shaders…).
    /// Ignoré en mode "default" (UseShellExecute).
    /// Default : true (activé automatiquement pour les fichiers locaux).
    /// </summary>
    public const string AllowFileAccess = "allow_file_access";
}

// ─── Lanceur Browser ─────────────────────────────────────────────────────────
// Gère deux types de releases "Browser" :
//
//  1. URL en ligne — le champ CommandLine du profil contient une URL complète
//     (https://…). DemoBase l'ouvre directement dans le navigateur configuré.
//     Le fichier local (romPath) est ignoré.
//
//  2. Fichier local — HTML, JS, WASM dans un zip ou en fichier direct.
//     Le launcher extrait le fichier principal (.html/.htm) et l'ouvre dans
//     le navigateur. Un serveur local n'est PAS lancé — certaines démos
//     WebGL/WASM requièrent un serveur HTTP ; dans ce cas, préférer une URL.
//
// Navigateurs supportés :
//   default — navigateur par défaut du système (UseShellExecute = true)
//   custom  — executable défini dans EmulatorExecutablePath (chrome.exe, etc.)

public class BrowserLauncher
{
    private static readonly HashSet<string> HtmlExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".xhtml" };

    // .swf retiré (2026-07-24) : les fichiers Flash passent désormais par
    // RuffleLauncher (EmulatorType.Ruffle), pas par le navigateur — Flash n'est
    // plus supporté par aucun navigateur depuis fin 2020/2021.
    private static readonly HashSet<string> WebExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".htm", ".xhtml", ".js", ".wasm" };

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico" };

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[BROWSER] LaunchAsync : romPath={romPath}");

        // ── Cas 1 : URL dans CommandLine ──────────────────────────────────────
        var cmdLine = config.CommandLine?.Trim() ?? string.Empty;
        if (cmdLine.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            cmdLine.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine($"[BROWSER] URL détectée : {cmdLine}");
            return OpenUrl(cmdLine, emulator, settings);
        }

        // ── Cas 2 : Fichier local ─────────────────────────────────────────────
        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        var ext = Path.GetExtension(romPath).ToLowerInvariant();

        if (ext == ".zip")
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[BROWSER] ZIP extrait → {actualFile}");
        }

        // Convertir le chemin en URI locale
        var uri = new Uri(actualFile).AbsoluteUri;
        System.Diagnostics.Debug.WriteLine($"[BROWSER] URI locale : {uri}");
        return OpenUrl(uri, emulator, settings);
    }

    private static LaunchResult OpenUrl(
        string url, Emulator emulator, Dictionary<string, string?> settings)
    {
        // Défaut intelligent : si aucun mode n'a jamais été explicitement
        // sauvegardé pour ce profil (première utilisation, y compris pour un
        // profil créé automatiquement par le wizard) et qu'un exécutable
        // valide est configuré sur l'émulateur, "personnalisé" est plus
        // logique que "navigateur par défaut du système" — sinon, configurer
        // un chemin d'exe précis (ex. Chrome) n'avait visiblement aucun effet
        // tant que l'utilisateur n'allait pas explicitement changer et
        // sauvegarder le dropdown "Navigateur" dans les réglages du profil.
        // Ce défaut est calculé ici, PAS seulement dans le ViewModel des
        // Préférences, car c'est ce code-ci (pas l'UI) qui détermine le
        // comportement réel au clic sur Play.
        string mode;
        if (settings.TryGetValue(BrowserSettings.Mode, out var savedMode) && !string.IsNullOrWhiteSpace(savedMode))
            mode = savedMode;
        else
            mode = !string.IsNullOrWhiteSpace(emulator.ExecutablePath) && File.Exists(emulator.ExecutablePath)
                ? "custom"
                : "default";

        var allowFileAccess = settings.GetValueOrDefault(BrowserSettings.AllowFileAccess, "true") != "false";
        var isLocalFile     = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

        try
        {
            ProcessStartInfo psi;

            if (mode == "custom" && File.Exists(emulator.ExecutablePath))
            {
                // Navigateur personnalisé (chrome.exe, firefox.exe, msedge.exe…)
                var args = new System.Text.StringBuilder();

                // --allow-file-access-from-files : nécessaire pour Chrome/Edge quand
                // la démo charge des ressources locales (WebGL, WASM, shaders…).
                // Activé par défaut pour les fichiers locaux, ignoré pour les URLs.
                if (allowFileAccess && isLocalFile)
                    args.Append("--allow-file-access-from-files ");

                args.Append($"\"{url}\"");

                psi = new ProcessStartInfo
                {
                    FileName        = emulator.ExecutablePath,
                    Arguments       = args.ToString(),
                    UseShellExecute = false,
                };
            }
            else
            {
                // Navigateur par défaut du système (UseShellExecute — pas de contrôle des args)
                psi = new ProcessStartInfo
                {
                    FileName        = url,
                    UseShellExecute = true,
                };
            }

            var process = Process.Start(psi);
            System.Diagnostics.Debug.WriteLine(
                $"[BROWSER] Process.Start : {(process == null ? "null" : $"PID={process.Id}")}");
            return new(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BROWSER] Exception : {ex}");
            return new(false, $"Erreur ouverture navigateur : {ex.Message}");
        }
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        // Extraire dans un sous-dossier dédié (chemin stable pour les références
        // relatives JS/CSS/WASM à l'intérieur de la démo)
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("browser", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Chercher le fichier HTML principal (index.html en priorité)
        var index = files.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Equals("index", StringComparison.OrdinalIgnoreCase)
            && HtmlExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (index != null) return index;

        // Tout autre fichier HTML
        var html = files.FirstOrDefault(f =>
            HtmlExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (html != null) return html;

        // Fallback : premier fichier non-ignoré
        return files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault()
               ?? zipPath;
    }
}
