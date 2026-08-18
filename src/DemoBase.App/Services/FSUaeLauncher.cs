using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings FS-UAE ─────────────────────────────────────────────────

public static class FSUaeKeys
{
    public const string Model      = "model";      // "A500" / "A500+" / "A1200" / "A4000"
    public const string ChipRam    = "chip_ram";   // "512K" / "1M" / "2M"
    public const string FastRam    = "fast_ram";   // "0" / "1M" / "2M" / "4M" / "8M"
    public const string FullScreen = "fullscreen"; // "true" / "false"
}

// ─── Lanceur FS-UAE ───────────────────────────────────────────────────────────
// FS-UAE — émulateur Amiga cross-platform basé sur WinUAE.
// https://fs-uae.net/
//
// Stratégie multi-disques :
//   • floppy_drive_0 = disk1.adf      (DF0, disque de boot)
//   • floppy_image_0 = disk1.adf  \
//     floppy_image_1 = disk2.adf   ›  liste de swap (F12 → Floppy)
//     floppy_image_2 = disk3.adf  /
//
// Commande : fs-uae.exe "generated.fs-uae"
//
// ⚠ La ROM Kickstart doit être configurée dans FS-UAE (Preferences → Kickstart).

public class FSUaeLauncher
{
    private readonly PreferencesService _prefs;
    public FSUaeLauncher(PreferencesService prefs) => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator emulator, EmulatorConfig config,
        Dictionary<string, string?> settings, string romPath, Release release)
    {
        System.Diagnostics.Debug.WriteLine($"[FSUAE] exe={emulator.ExecutablePath} rom={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"FS-UAE introuvable : {emulator.ExecutablePath}");
        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        List<string> disks;
        var ext = Path.GetExtension(romPath).ToLowerInvariant();

        if (ext == ".zip")
        {
            var extractDir = Path.Combine(WorkingPaths.GetSubdir("Configs"),
                "extracted", $"amiga_{release.Id}");
            disks = await AmigaMultiDiskHelper.ExtractAndSortAsync(romPath, extractDir);
            if (disks.Count == 0)
                return new(false, "Aucun fichier .adf/.dms trouvé dans le ZIP.");
            System.Diagnostics.Debug.WriteLine(
                $"[FSUAE] {disks.Count} disque(s) : " + string.Join(", ", disks.Select(Path.GetFileName)));
        }
        else
        {
            disks = [romPath];
        }

        var cfgPath = Path.Combine(WorkingPaths.GetSubdir("Configs"),
            $"demobase_amiga_{release.Id}.fs-uae");
        GenerateFSUaeConfig(cfgPath, disks, settings);

        var args = $"\"{cfgPath}\"";
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "FSUAE", friendlyName: "FSUae");
    }

    private static void GenerateFSUaeConfig(
        string cfgPath, List<string> disks, Dictionary<string, string?> settings)
    {
        var sb    = new StringBuilder();
        var model = settings.GetValueOrDefault(FSUaeKeys.Model, "A500")    ?? "A500";
        var chip  = settings.GetValueOrDefault(FSUaeKeys.ChipRam, "512K")  ?? "512K";
        var fast  = settings.GetValueOrDefault(FSUaeKeys.FastRam, "0")     ?? "0";

        sb.AppendLine("[fs-uae]");
        sb.AppendLine($"; DemoBase — config auto-generee FS-UAE");
        sb.AppendLine($"amiga_model = {model}");
        sb.AppendLine($"chip_memory = {chip}");

        if (fast != "0" && !string.IsNullOrWhiteSpace(fast))
            sb.AppendLine($"fast_memory = {fast}");

        if (settings.GetValueOrDefault(FSUaeKeys.FullScreen) == "true")
            sb.AppendLine("fullscreen = 1");

        sb.AppendLine();

        // DF0 = premier disque
        if (disks.Count > 0)
            sb.AppendLine($"floppy_drive_0 = {disks[0]}");

        // Liste de swap : tous les disques accessibles via F12 → Floppy
        for (int i = 0; i < disks.Count; i++)
            sb.AppendLine($"floppy_image_{i} = {disks[i]}");

        File.WriteAllText(cfgPath, sb.ToString(), Encoding.UTF8);
        System.Diagnostics.Debug.WriteLine($"[FSUAE] Config :\n{sb}");
    }
}
