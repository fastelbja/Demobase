using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace DemoBase.App.Services;

// ─── Lanceur CXBX-Reloaded ───────────────────────────────────────────────────
// CXBX-Reloaded — émulateur Xbox original (OG Xbox), approche HLE.
// https://github.com/cxbx-reloaded/cxbx-reloaded
//
// Commande :
//   cxbxr-ldr.exe /load "<chemin/default.xbe>"
//
// Architecture de lancement :
//   CXBX-Reloaded fonctionne en deux étapes :
//   1. cxbxr-ldr.exe démarre avec /load <xbe> et affiche l'interface
//   2. Il se relance lui-même pour lancer l'émulation réelle
//   Si un processus parent lance cxbxr-ldr.exe avec /load, à la fin de
//   l'émulation le processus se termine entièrement (sans revenir à l'UI).
//
// Formats supportés :
//   .xbe  — Xbox Executable (fichier principal du jeu, ex : default.xbe)
//           Le jeu est un DOSSIER contenant le .xbe et ses données associées.
//           Passer uniquement le .xbe en argument, pas le dossier.
//
// ⚠ Pas de support ISO ni ZIP natif — les jeux doivent être extraits sous
//   forme de dossiers avec le .xbe accessible.
//
// Fullscreen : pas de CLI — configurer dans l'interface CXBX-Reloaded
//   (Settings > Config Video) ou via les settings de l'émulateur.
//
// Pas de BIOS requis — CXBX-Reloaded émule son propre kernel Xbox.
//
// Compatibilité (début 2025) :
//   ~16% de la bibliothèque Xbox est pleinement jouable
//   ~49% atteint le stade "in-game"

public class CxbxReloadedLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    public CxbxReloadedLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[CXBX] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"CXBX-Reloaded introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        var ext = Path.GetExtension(romPath).ToLowerInvariant();

        if (ext == ".zip")
        {
            // CXBX ne supporte pas les ZIP — extraction nécessaire
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[CXBX] ZIP extrait → {actualFile}");
        }

        // Vérifier que le fichier est un .xbe
        var actualExt = Path.GetExtension(actualFile).ToLowerInvariant();
        if (actualExt != ".xbe")
        {
            System.Diagnostics.Debug.WriteLine(
                $"[CXBX] Avertissement : fichier n'est pas un .xbe ({actualFile})");
        }

        // Paramètres additionnels du profil
        var extra = (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            ? " " + EmulatorLaunchService.SubstituteVars(config.CommandLine, actualFile)
            : string.Empty;

        var args = $"/load \"{actualFile}\"{extra}";
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "CXBX", friendlyName: "CxbxReloaded");
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("ogxbox", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : default.xbe en premier, sinon tout autre .xbe
        var defaultXbe = files.FirstOrDefault(f =>
            Path.GetFileName(f).Equals("default.xbe", StringComparison.OrdinalIgnoreCase));
        if (defaultXbe != null) return defaultXbe;

        var anyXbe = files.FirstOrDefault(f =>
            Path.GetExtension(f).Equals(".xbe", StringComparison.OrdinalIgnoreCase));
        if (anyXbe != null) return anyXbe;

        return files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
               ?? files.FirstOrDefault()
               ?? zipPath;
    }
}
