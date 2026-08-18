using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings ProSystem ──────────────────────────────────────────────

public static class ProSystemKeys
{
    public const string FullScreen = "fullscreen"; // "true" / "false"
    // Résolution fenêtrée/plein écran, stockée "LARGEURxHAUTEUR" (ex. "1280x720") —
    // valeurs proposées dans le profil, cf. ProSystemSettingsViewModel.ResolutionOptions.
    public const string Resolution = "resolution";
    public const string DefaultResolution = "640x480";
}

// ─── Lanceur ProSystem ────────────────────────────────────────────────────────
// ProSystem — émulateur Atari 7800 ProSystem, avec rétrocompatibilité 2600.
// https://gstanton.github.io/ProSystem1_3/
//
// Commande : ProSystem.exe "<fichier.a78>"
//
// ProSystem a une CLI minimale : il accepte le chemin du fichier ROM en
// argument et le charge automatiquement. Il ne supporte pas les options
// fullscreen ou résolution en ligne de commande — contrairement aux autres
// émulateurs de cette appli (arguments CLI), ces réglages n'existent QUE dans
// ProSystem.ini (généré par ProSystem.exe lui-même à côté de son exe, au
// premier lancement). Ajouté le 2026-07-24 à la demande de l'utilisateur (qui
// a fourni le contenu réel de son ProSystem.ini) : Fullscreen/Mode.Width/
// Mode.Height sont maintenant patchés dans ce fichier juste avant chaque
// lancement (cf. ApplyDisplaySettings), à partir des réglages du profil —
// même principe que BiosPackService pour PCSX2/DuckStation, mais ici à CHAQUE
// lancement plutôt qu'au "Pack BIOS", puisque ce n'est pas lié au BIOS mais à
// un réglage d'affichage propre au profil d'émulateur.
//
// Formats supportés :
//   .a78  — ROMs Atari 7800 (format standard avec header 128 octets)
//   .bin  — ROMs binaires (2600 ou 7800, détection automatique par ProSystem)
//   Pas de support ZIP natif → extraction automatique par DemoBase si .zip
//
// BIOS Atari 7800 : optionnel pour les demos/homebrew (confirmé par test
// utilisateur) — recommandé quand même pour l'écran d'accueil Atari et la
// compatibilité maximale ("7800.rom" dans le répertoire de ProSystem.exe).

public class ProSystemLauncher
{
    private readonly PreferencesService _prefs;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        { { ".a78", 4 }, { ".bin", 2 } };

    public ProSystemLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PROSYSTEM] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"ProSystem introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        ApplyDisplaySettings(emulator, settings);

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // ProSystem ne lit pas les ZIP — extraction nécessaire
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[PROSYSTEM] ZIP extrait → {actualFile}");
        }

        var extraArgs = (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            ? EmulatorLaunchService.SubstituteVars(config.CommandLine, actualFile) + " "
            : string.Empty;

        var args = $"{extraArgs}\"{actualFile}\"";
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "PROSYSTEM", friendlyName: "ProSystem");
    }

    /// <summary>
    /// Patche ProSystem.ini (à côté de l'exe) avec les réglages Fullscreen/résolution du
    /// profil, juste avant chaque lancement. Ne fait rien si le fichier n'existe pas encore
    /// (ProSystem.exe le crée lui-même à son tout premier lancement, jamais avant) — même
    /// approche prudente que pour DuckStation (BiosPackService.ConfigureDuckStation) :
    /// patcher un ini existant plutôt que d'en imposer un nouveau, ProSystem n'ayant jamais
    /// été testé avec un ini "fragment" écrit de toutes pièces.
    /// </summary>
    private static void ApplyDisplaySettings(Emulator emulator, Dictionary<string, string?> settings)
    {
        try
        {
            var exeDir  = Path.GetDirectoryName(emulator.ExecutablePath);
            if (string.IsNullOrEmpty(exeDir)) return;
            var iniPath = Path.Combine(exeDir, "ProSystem.ini");
            if (!File.Exists(iniPath)) return;

            var fullScreen = settings.GetValueOrDefault(ProSystemKeys.FullScreen) == "true";
            var resolution = settings.GetValueOrDefault(ProSystemKeys.Resolution,
                ProSystemKeys.DefaultResolution) ?? ProSystemKeys.DefaultResolution;

            var parts = resolution.Split('x', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], out var width)
                || !int.TryParse(parts[1], out var height))
            {
                width = 640; height = 480; // secours si valeur stockée corrompue
            }

            UpdateProSystemIniValue(iniPath, "Display", "Fullscreen",  fullScreen ? "true" : "false");
            UpdateProSystemIniValue(iniPath, "Display", "Mode.Width",  width.ToString());
            UpdateProSystemIniValue(iniPath, "Display", "Mode.Height", height.ToString());

            System.Diagnostics.Debug.WriteLine(
                $"[PROSYSTEM] ProSystem.ini mis à jour : Fullscreen={fullScreen} {width}x{height}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PROSYSTEM] ApplyDisplaySettings a échoué : {ex.Message}");
        }
    }

    /// <summary>
    /// Met à jour (ou ajoute) une clé "clé=valeur" dans une section [Section] d'un ini —
    /// SANS espace autour du '=' (contrairement à BiosPackService.UpdateIniValue), pour
    /// coller exactement au format natif écrit par ProSystem.exe lui-même
    /// (ex. "Fullscreen=false", "Mode.Height=480" — vu dans le fichier fourni par
    /// l'utilisateur). Section/clé créées si absentes.
    /// </summary>
    private static void UpdateProSystemIniValue(string iniPath, string section, string key, string value)
    {
        var lines = new List<string>(File.ReadAllLines(iniPath, Utf8NoBom));

        var sectionHeader  = $"[{section}]";
        int sectionIdx     = -1;
        int keyIdx         = -1;
        int nextSectionIdx = lines.Count;

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionIdx = i;
                continue;
            }
            if (sectionIdx >= 0 && trimmed.StartsWith("[") && i > sectionIdx)
            {
                nextSectionIdx = i;
                break;
            }
            if (sectionIdx >= 0)
            {
                var eq = trimmed.IndexOf('=');
                if (eq > 0 && trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    keyIdx = i;
            }
        }

        var newLine = $"{key}={value}";

        if (keyIdx >= 0)
            lines[keyIdx] = newLine;
        else if (sectionIdx >= 0)
            lines.Insert(nextSectionIdx, newLine);
        else
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add(sectionHeader);
            lines.Add(newLine);
        }

        File.WriteAllLines(iniPath, lines, Utf8NoBom);
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("atari7800", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        string? best = null;
        int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = ExtScore.GetValueOrDefault(e, 0);
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
