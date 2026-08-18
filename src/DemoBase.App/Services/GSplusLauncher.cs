using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings GSplus ─────────────────────────────────────────────────

public static class GSplusKeys
{
    public const string Resizeable = "resizeable"; // "true" / "false"
    public const string Slot       = "slot";       // "s5" (5.25") / "s6" (Disk II) / "s7" (HD)
}

// ─── Lanceur GSplus ───────────────────────────────────────────────────────────
// GSplus — émulateur Apple IIgs (basé sur KEGS / GSport).
// https://github.com/applemu/gsplus
//
// GSplus ne supporte pas le chargement de disques en CLI : les images sont
// montées via config.txt (format KEGS) dans le répertoire de l'exe.
//
// DemoBase :
//  1. Extrait le ZIP dans Working/Configs/extracted/iigs_<id>/
//  2. Modifie config.txt dans le répertoire de gsplus.exe pour monter l'image
//  3. Lance GSplus.exe
//
// Format config.txt (KEGS) :
//   s5d1 /chemin/absolu/vers/image.2mg    ← slot 5 drive 1 (3.5" ProDOS)
//   s6d1 /chemin/absolu/vers/image.dsk    ← slot 6 drive 1 (5.25" Disk II)
//   s7d1 /chemin/absolu/vers/image.hdv    ← slot 7 drive 1 (disque dur)
//
// ROM requise : ROM.01 ou ROM.03 dans le répertoire de gsplus.exe.
//
// Fullscreen : F11. Reset : Ctrl+F12. Config disques interactifs : F4.

public class GSplusLauncher
{
    private readonly PreferencesService _prefs;

    // Extensions → slot KEGS recommandé
    private static readonly Dictionary<string, string> ExtToSlot =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // 3.5" ProDOS → slot 5
            { ".2mg", "s5d1" }, { ".po", "s5d1" },
            // 5.25" DOS 3.3 → slot 6
            { ".dsk", "s6d1" }, { ".do", "s6d1" }, { ".nib", "s6d1" }, { ".woz", "s6d1" },
            // Disque dur → slot 7
            { ".hdv", "s7d1" },
        };

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static readonly Dictionary<string, int> ExtScore =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".2mg", 5 }, { ".po", 4 }, { ".hdv", 3 },
            { ".woz", 4 }, { ".dsk", 2 }, { ".do", 2 }, { ".nib", 1 },
        };

    public GSplusLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[GSPLUS] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"GSplus introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        // ── 1. Extraire le ZIP si nécessaire ─────────────────────────────────
        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[GSPLUS] ZIP extrait → {actualFile}");
        }

        // ── 2. Écrire config.txt dans le répertoire de GSplus ────────────────
        var exeDir    = Path.GetDirectoryName(emulator.ExecutablePath)!;
        var configTxt = Path.Combine(exeDir, "config.kegs");
        try
        {
            PrepareConfig(configTxt, actualFile, settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GSPLUS] Erreur config.txt : {ex}");
            return new(false, $"Impossible d'écrire config.txt : {ex.Message}");
        }

        // ── 3. Lancer GSplus ─────────────────────────────────────────────────
        var args = new StringBuilder();
        if (settings.GetValueOrDefault(GSplusKeys.Resizeable, "true") != "false")
            args.Append("-resizeable");

        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
            args.Append(' ').Append(EmulatorLaunchService.SubstituteVars(config.CommandLine, actualFile));

        var argsStr = args.ToString().TrimEnd();
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, argsStr, tag: "GSPLUS", friendlyName: "GSplus",
            workingDir: exeDir);
    }

    /// <summary>
    /// Lit config.kegs, remplace ou ajoute la ligne du slot sélectionné
    /// au format KEGS : "s5d1 = /chemin/absolu/disk.2mg"
    /// Préserve toutes les autres lignes (ROM, bram1, bram3…).
    /// </summary>
    private static void PrepareConfig(string configPath, string diskFile, Dictionary<string, string?> settings)
    {
        var ext     = Path.GetExtension(diskFile).ToLowerInvariant();
        var slotKey = settings.GetValueOrDefault(GSplusKeys.Slot);
        if (string.IsNullOrWhiteSpace(slotKey))
            slotKey = ExtToSlot.GetValueOrDefault(ext, "s5d1");

        // GSplus/KEGS accepte les chemins Windows avec backslashes
        // Format : "s5d1 = C:\path\to\disk.2mg"
        var diskPath = diskFile;

        // Lire config.kegs existant (ou squelette minimal)
        var lines = File.Exists(configPath)
            ? File.ReadAllLines(configPath, Encoding.ASCII).ToList()
            : new List<string>
              {
                  "# KEGS configuration file version 1.38",
                  "",
                  "s5d1 = ",
                  "s5d2 = ",
                  "",
                  "s6d1 = ",
                  "s6d2 = ",
                  "",
                  "s7d1 = ",
                  "",
                  "g_cfg_rom_path = rom03",
              };

        // Remplacer ou ajouter la ligne du slot (format "key = value")
        SetKegsValue(lines, slotKey, diskPath);

        // ROM requise (cf. BiosPackService.ConfigureGSPlus, qui copie le fichier "rom03"
        // depuis le pack BIOS Recalbox à côté de GSplus.exe) : sans cette clé — ou avec la
        // valeur vide du squelette initial — GSplus ne sait pas quel fichier ROM charger.
        SetKegsValue(lines, "g_cfg_rom_path", "rom03");

        File.WriteAllLines(configPath, lines, Encoding.ASCII);
        System.Diagnostics.Debug.WriteLine(
            $"[GSPLUS] config.kegs mis à jour : {slotKey} = {diskPath}, g_cfg_rom_path = rom03");
    }

    /// <summary>Remplace la valeur d'une clé "key = value" existante, ou l'ajoute si absente.</summary>
    private static void SetKegsValue(List<string> lines, string key, string value)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > key.Length
                && (trimmed[key.Length] == ' ' || trimmed[key.Length] == '='))
            {
                lines[i] = $"{key} = {value}";
                return;
            }
        }
        lines.Add($"{key} = {value}");
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("iigs", releaseId, zipPath));
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
