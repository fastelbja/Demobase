using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés settings WinUAE (stockées dans EmulatorSettings par config) ────────

public static class WinUAESettings
{
    public const string KickstartPath = "kickstart_path";
    public const string FloppySounds  = "floppy_sounds";   // "true" / "false"

    // Cases "CPU options" de WinUAE (clés .uae identiques, réutilisées telles quelles) —
    // "auto" (ou absent) = valeur du .uae de base laissée telle quelle ; "true"/"false" =
    // forcé explicitement, pour éviter d'avoir à dupliquer un .uae juste pour ces 2 cases.
    public const string CpuCycleExact       = "cpu_cycle_exact";        // "auto" | "true" | "false"
    public const string CpuMemoryCycleExact = "cpu_memory_cycle_exact"; // "auto" | "true" | "false"
}

// ─── Lanceur WinUAE ───────────────────────────────────────────────────────────
// Comportement :
//  1. Lit le fichier .uae de base défini dans config.ConfigFilePath (profil)
//  2. Applique uniquement les overrides nécessaires (use_gui, kickstart, floppies)
//  3. Écrit le résultat dans Working/Configs/<release>.uae  SANS BOM
//     (le parseur C de WinUAE cfgfile.cpp ne gère pas le BOM — corrompt la 1ère clé)
//  4. Lance WinUAE avec -f <fichier_généré>
//
// Multi-disques (.zip avec plusieurs .adf) :
//  - Tous les .adf sont extraits et triés (A→B ou 1→2→3)
//  - floppy0 = disque1, floppy1 = disque2, floppy2 = disque3, floppy3 = disque4
//  - Disques 5+ : diskimage0, diskimage1… (swap via Ctrl+F11)

public class WinUAELauncher
{
    private readonly PreferencesService _prefs;

    public WinUAELauncher(PreferencesService prefs) => _prefs = prefs;

    // ── Lancement principal ───────────────────────────────────────────────────

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"WinUAE introuvable : {emulator.ExecutablePath}");
        if (!File.Exists(romPath))
            return new(false, $"Fichier ROM introuvable : {romPath}");

        var kickstart = settings.GetValueOrDefault(WinUAESettings.KickstartPath);
        if (string.IsNullOrWhiteSpace(kickstart) || !File.Exists(kickstart))
            return new(false,
                "Kickstart ROM manquant. Configurez le chemin dans Émulateurs → Settings WinUAE.");

        var uaePath = await GenerateUaeAsync(emulator, config, settings, kickstart, romPath, release);

        // Construire les args : -f <fichier> puis -s clé=valeur pour les overrides
        // qui doivent primer sur la config globale WinUAE (%AppData%\WinUAE).
        // gfx_display_name et gfx_display_friendlyname en particulier sont mémorisés
        // par WinUAE dans sa propre config et peuvent écraser le .uae si on ne
        // les force pas aussi en CLI.
        var displayIndex   = settings.GetValueOrDefault("gfx_display");
        var displayFriendly= settings.GetValueOrDefault("gfx_display_friendlyname");
        var displayName    = settings.GetValueOrDefault("gfx_display_name");

        var sbArgs = new System.Text.StringBuilder();
        sbArgs.Append($"-f \"{uaePath}\"");
        if (!string.IsNullOrWhiteSpace(displayIndex))
            sbArgs.Append($" -s gfx_display={displayIndex}");
        if (!string.IsNullOrWhiteSpace(displayFriendly))
            sbArgs.Append($" -s gfx_display_friendlyname={displayFriendly}");
        if (!string.IsNullOrWhiteSpace(displayName))
            sbArgs.Append($" -s gfx_display_name={displayName}");

        // WinUAE n'a pas de bouton "Fermer" ni de raccourci Alt+F4 classique en plein écran —
        // il faut Ctrl+F11. Info pas évidente pour l'utilisateur → popup ponctuelle, même
        // principe que le TR-DOS de ZEsarUX / le raccourci quitter de blueMSX.
        await MaybeShowQuitInfoAsync();

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, sbArgs.ToString(), tag: "WINUAE", friendlyName: "WinUAE");
    }

    // ── Popup d'info sur le raccourci pour quitter ──────────────────────────────

    private const string QuitInfoDialogPrefKey = "winuae.quit_info_dialog.hidden";

    /// <summary>
    /// Affiche la popup expliquant Ctrl+F11 pour quitter WinUAE, sauf si l'utilisateur a déjà
    /// coché "Ne plus afficher ce message" lors d'un lancement précédent.
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
                var dlg = new DemoBase.App.Views.WinUAEInfoDialog
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
            Debug.WriteLine($"[WINUAE] Popup info quitter : échec — {ex.Message}");
        }
    }

    // ── Génération du .uae ────────────────────────────────────────────────────

    private async Task<string> GenerateUaeAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      kickstart,
        string                      romPath,
        Release                     release)
    {
        var configDir = WorkingPaths.GetSubdir("Configs");
        var safeName  = string.Concat(release.Title.Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var uaePath   = Path.Combine(configDir, $"{safeName}.uae");

        var floppySounds = settings.GetValueOrDefault(WinUAESettings.FloppySounds) != "false";
        var ext          = Path.GetExtension(romPath).ToLowerInvariant();
        bool isZip       = ext == ".zip";
        bool isFloppy    = ext is ".adf" or ".adz" or ".dms" or ".fdi" or ".ipf";
        bool isHdf       = ext == ".hdf";

        // ── Résoudre la liste de disques ─────────────────────────────────────
        List<string> disks;

        if (isZip)
        {
            // ZIP multi-disques : extraire et trier (A→B ou 1→2→3)
            var extractDir = Path.Combine(configDir, "extracted", $"amiga_{release.Id}");
            disks = await AmigaMultiDiskHelper.ExtractAndSortAsync(romPath, extractDir);

            if (disks.Count == 0)
            {
                var winUaeExeDir = Path.GetDirectoryName(emulator.ExecutablePath)!;
                return await FallbackSingleAsync(romPath, configDir, release, kickstart,
                    floppySounds, config, uaePath, winUaeExeDir, _prefs, settings);
            }

            System.Diagnostics.Debug.WriteLine(
                $"[WINUAE] {disks.Count} disque(s) : " +
                string.Join(", ", disks.Select(Path.GetFileName)));
        }
        else
        {
            disks    = [romPath];
            isFloppy = isFloppy || ext is ".adf" or ".adz" or ".dms" or ".fdi" or ".ipf";
        }

        // ── Construire les overrides ──────────────────────────────────────────
        // Moniteur cible (optionnel — si configuré dans les settings du profil)
        var displayFriendly = settings.GetValueOrDefault("gfx_display_friendlyname");
        var displayName     = settings.GetValueOrDefault("gfx_display_name");

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["use_gui"]              = "no",         // pas d'interface au lancement
            ["kickstart_rom_file"]   = kickstart,
            ["kickstart_rom_file_id"]= "",            // évite la substitution par WinUAE
            ["config_description"]   = $"DemoBase {release.DemozooId?.ToString() ?? release.Id.ToString()}",
        };

        // Injecter le moniteur cible si défini
        var displayIndex = settings.GetValueOrDefault("gfx_display");
        if (!string.IsNullOrWhiteSpace(displayIndex))
            overrides["gfx_display"] = displayIndex;
        if (!string.IsNullOrWhiteSpace(displayFriendly))
            overrides["gfx_display_friendlyname"] = displayFriendly;
        if (!string.IsNullOrWhiteSpace(displayName))
            overrides["gfx_display_name"] = displayName;

        System.Diagnostics.Debug.WriteLine($"[WINUAE:display] index='{displayIndex}' friendly='{displayFriendly}' name='{displayName}'");

        ApplyCycleExactOverrides(overrides, settings);

        // Toujours vider floppy1-3 (le fichier de base peut en avoir des anciens)
        overrides["floppy1"] = "";
        overrides["floppy2"] = "";
        overrides["floppy3"] = "";

        if (disks.Count > 0 && (isFloppy || isZip))
        {
            // DF0-DF3 pour les 4 premiers disques
            int drives = Math.Min(disks.Count, 4);
            for (int i = 0; i < drives; i++)
            {
                overrides[$"floppy{i}"]     = disks[i];
                overrides[$"floppy{i}type"] = "0"; // DD 3.5"
            }
            overrides["nr_floppies"]          = drives.ToString();
            overrides["floppy_sounds"]         = floppySounds ? "1" : "0";
            overrides["floppy_sounds_vol"]     = "100";

            // Disques 5+ dans la liste de swap (Ctrl+F11 dans WinUAE)
            for (int i = 4; i < disks.Count; i++)
                overrides[$"diskimage{i - 4}"] = disks[i];
        }
        else if (isHdf)
        {
            overrides["hardfile2"] = $"rw,DH0:{romPath},0,0,0,512,0,,uae0";
        }
        else if (disks.Count > 0)
        {
            overrides["floppy0"]     = disks[0];
            overrides["nr_floppies"] = "1";
        }

        // ── Fusionner avec le .uae de base du profil ─────────────────────────
        string finalContent;
        var baseFile = config.ConfigFilePath;
        if (!string.IsNullOrWhiteSpace(baseFile) && File.Exists(baseFile))
        {
            var baseContent = await File.ReadAllTextAsync(baseFile);
            finalContent    = MergeIntoBaseUae(baseContent, overrides);
            System.Diagnostics.Debug.WriteLine($"[WINUAE] Fusion sur : '{baseFile}'");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(
                "[WINUAE] Aucun fichier de base défini (ConfigFilePath) — .uae minimal. " +
                "Recommandé : configurer un fichier .uae dans le profil.");
            var sb = new StringBuilder();
            sb.AppendLine("; Généré par DemoBase (aucun fichier de base — voir profil)");
            foreach (var kv in overrides)
                if (!string.IsNullOrEmpty(kv.Value))
                    sb.AppendLine($"{kv.Key}={kv.Value}");
            finalContent = sb.ToString();
        }

        // Écrire SANS BOM — le parseur C de WinUAE (cfgfile.cpp) ne gère pas le BOM
        // et corrompt la première clé lue (EF BB BF s'intercale dans la valeur).
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(uaePath, finalContent, utf8NoBom);
        // Log des lignes gfx_display pour diagnostic moniteur
        foreach (var l in finalContent.Split('\n').Where(l => l.StartsWith("gfx_display")))
            System.Diagnostics.Debug.WriteLine($"[WINUAE:uae] {l}");
        System.Diagnostics.Debug.WriteLine(
            $"[WINUAE] .uae généré : '{uaePath}' " +
            $"({disks.Count} disque(s), kickstart='{kickstart}')");
        return uaePath;
    }

    // ── Cases "CPU options" de WinUAE (cpu_cycle_exact / cpu_memory_cycle_exact) ────
    // Appliqué aux deux chemins de génération de .uae (floppy et fallback HDD-only) —
    // "auto" (ou absent) laisse la valeur du .uae de base inchangée.
    private static void ApplyCycleExactOverrides(
        Dictionary<string, string> overrides, Dictionary<string, string?> settings)
    {
        var cycleExactFull = settings.GetValueOrDefault(WinUAESettings.CpuCycleExact);
        if (!string.IsNullOrWhiteSpace(cycleExactFull) && cycleExactFull != "auto")
            overrides["cpu_cycle_exact"] = cycleExactFull;

        var cycleExactMemory = settings.GetValueOrDefault(WinUAESettings.CpuMemoryCycleExact);
        if (!string.IsNullOrWhiteSpace(cycleExactMemory) && cycleExactMemory != "auto")
            overrides["cpu_memory_cycle_exact"] = cycleExactMemory;
    }

    // ── Fallback : le ZIP ne contient pas de .adf ─────────────────────────────
    // Si le .uae de base déclare un disque dur virtuel GEMDOS (uaehf0=dir,...),
    // on extrait le contenu du zip dans le dossier cible du HD virtuel et on
    // ne touche pas aux lecteurs floppy — c'est le cas des prods HDD-only comme
    // Starstruck (The Black Lotus, AGA) dont le zip contient un .exe + un .dat.
    // Sinon (aucun HD virtuel configuré), fallback sur floppy0 = romPath (zip brut).
    private static async Task<string> FallbackSingleAsync(
        string romPath, string configDir, Release release, string kickstart,
        bool floppySounds, EmulatorConfig config, string uaePath, string winUaeExeDir,
        PreferencesService prefs, Dictionary<string, string?> settings)
    {
        System.Diagnostics.Debug.WriteLine("[WINUAE] ZIP sans .adf — détection HD virtuel");

        // Lire le .uae de base pour détecter un uaehf0=dir,...
        string? hddTarget = null;
        if (!string.IsNullOrWhiteSpace(config.ConfigFilePath) && File.Exists(config.ConfigFilePath))
        {
            hddTarget = await Task.Run(() =>
                DetectHddDir(config.ConfigFilePath, winUaeExeDir));
        }

        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["use_gui"]               = "no",
            ["kickstart_rom_file"]    = kickstart,
            ["kickstart_rom_file_id"] = "",
            ["config_description"]    = $"DemoBase {release.Id}",
            ["floppy1"] = "", ["floppy2"] = "", ["floppy3"] = "",
        };
        ApplyCycleExactOverrides(overrides, settings);

        if (hddTarget != null)
        {
            // Pack "Demos" (filesystem AmigaOS de base C/S/Devs, cf. WinUAEDemosPackService) —
            // obligatoire pour que le disque dur virtuel puisse démarrer (les démos AGA
            // HDD-only comme Starstruck n'embarquent que leur propre exécutable, pas un
            // Workbench complet). Téléchargé une seule fois depuis Mega à la racine du
            // dossier WinUAE ; idempotent, best-effort (un échec n'empêche pas le lancement,
            // il sera juste non fonctionnel comme avant ce fix).
            var demosPack = await new WinUAEDemosPackService().DownloadAndInstallAsync(winUaeExeDir);
            System.Diagnostics.Debug.WriteLine(
                $"[WINUAE] Pack Demos : {(demosPack.Success ? "OK" : "ÉCHEC")} — {demosPack.Message}");

            // HD virtuel détecté → extraire le zip dans le dossier cible.
            Directory.CreateDirectory(hddTarget);

            // Nettoyer le dossier Demos avant d'extraire la nouvelle release.
            // On préserve les dossiers système AmigaOS : C, S, Devs (commandes,
            // Startup-Sequence, périphériques) qui font partie de la config de
            // base et ne doivent pas être effacés entre deux lancements.
            await Task.Run(() => CleanHddTarget(hddTarget));

            List<string> extractedFiles = [];
            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(romPath);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // dossier
                    // Décoder les entités XML/HTML ("&amp;" → "&", etc.) éventuellement
                    // présentes dans le nom d'entrée — arrive pour des ZIP construits par
                    // ReleaseBuilderService à partir de métadonnées DAT (elles-mêmes du XML,
                    // où "&" doit être échappé) avant le fix de DatImportService/
                    // ReleaseBuilderService. Sans ce décodage, le fichier extrait garde
                    // littéralement "&amp;" dans son nom — et son ";" final casse la
                    // Startup-Sequence Amiga (";" = séparateur de commandes Shell). Décoder ici,
                    // à l'extraction, corrige aussi les ZIP déjà en cache sans devoir les
                    // reconstruire ni forcer un ré-import DAT.
                    var decodedName = System.Net.WebUtility.HtmlDecode(entry.Name);
                    var dest = Path.Combine(hddTarget, decodedName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                    extractedFiles.Add(dest);
                }
            });
            System.Diagnostics.Debug.WriteLine(
                $"[WINUAE] HD virtuel : contenu extrait dans '{hddTarget}'");

            // Mettre à jour le Startup-Sequence pour lancer le bon exécutable.
            await UpdateStartupSequenceAsync(hddTarget, extractedFiles,
                DetectVolumeName(config.ConfigFilePath),
                configId: config.Id, releaseId: release.Id, prefs: prefs,
                askUser: async fileNames =>
                {
                    // Afficher le dialog de sélection sur le thread UI.
                    return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var dlg = new DemoBase.App.Views.StartupFilePickerDialog(fileNames)
                        {
                            Owner = System.Windows.Application.Current.MainWindow
                        };
                        return dlg.ShowDialog() == true ? dlg.SelectedFile : null;
                    });
                });
        }
        else
        {
            // Pas de HD virtuel configuré → floppy0 = zip brut (comportement original).
            System.Diagnostics.Debug.WriteLine("[WINUAE] Pas de HD virtuel détecté — floppy0 = zip");
            overrides["floppy0"]    = romPath;
            overrides["nr_floppies"] = "1";
            overrides["floppy_sounds"] = floppySounds ? "1" : "0";
        }

        string finalContent;
        if (!string.IsNullOrWhiteSpace(config.ConfigFilePath) && File.Exists(config.ConfigFilePath))
            finalContent = MergeIntoBaseUae(await File.ReadAllTextAsync(config.ConfigFilePath), overrides);
        else
        {
            var sb = new StringBuilder();
            foreach (var kv in overrides)
                if (!string.IsNullOrEmpty(kv.Value))
                    sb.AppendLine($"{kv.Key}={kv.Value}");
            finalContent = sb.ToString();
        }

        // Patcher les chemins relatifs (filesystem2= et uaehf0=) avec le chemin
        // absolu basé sur le dossier de l'exe WinUAE, pour que WinUAE trouve
        // toujours le bon dossier HD quelle que soit la position du .uae généré.
        finalContent = PatchHddPaths(finalContent, winUaeExeDir);

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await File.WriteAllTextAsync(uaePath, finalContent, utf8NoBom);
        return uaePath;
    }

    /// <summary>
    /// Détecte la présence d'un disque dur virtuel de type "dir" dans un .uae
    /// et retourne le chemin absolu du dossier cible, ou null si absent.
    ///
    /// Format WinUAE : uaehf0=dir,rw,&lt;label&gt;:&lt;label&gt;:&lt;path&gt;,&lt;flags&gt;
    ///   ex. uaehf0=dir,rw,Demos:Demos:.\Demos,0
    ///       → chemin = .\Demos  → résolu par rapport au dossier du .uae
    ///
    /// On accepte uaehf0 à uaehf9 (plusieurs HD peuvent être configurés ;
    /// on retourne le premier trouvé de type "dir").
    /// </summary>
    private static string? DetectHddDir(string uaePath, string winUaeExeDir)
    {
        foreach (var line in File.ReadLines(uaePath))
        {
            var t = line.TrimStart();
            if (t.StartsWith(";") || t.StartsWith("#")) continue;

            // uaehf0=dir,... uaehf1=dir,... etc.
            if (!System.Text.RegularExpressions.Regex.IsMatch(t,
                    @"^uaehf\d+=dir\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;

            // Format : uaehfN=dir,<rw|ro>,<DevName>:<VolName>:<path>,<flags>
            var eq = t.IndexOf('=');
            if (eq < 0) continue;
            var val = t[(eq + 1)..].Trim();   // "dir,rw,Demos:Demos:.\Demos,0"
            var parts = val.Split(',');        // ["dir","rw","Demos:Demos:.\\Demos","0"]
            if (parts.Length < 3) continue;

            // Le 3e champ (index 2) = "DevName:VolName:path"
            var tripart = parts[2].Split(':');
            if (tripart.Length < 3) continue;
            // Tout ce qui suit le 2e ':' est le chemin (peut contenir des ':' sur Windows)
            var rawPath = string.Join(":", tripart[2..]);

            // Résoudre par rapport au dossier de l'exe WinUAE (pas du .uae généré)
            var resolved = Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.GetFullPath(Path.Combine(winUaeExeDir, rawPath));

            System.Diagnostics.Debug.WriteLine(
                $"[WINUAE] HD virtuel détecté : uaehfN=dir → '{resolved}'");
            return resolved;
        }
        return null;
    }

    // ── Fusion clé=valeur sur le .uae de base ────────────────────────────────
    // Remplace toutes les occurrences des clés présentes dans `overrides`,
    // ajoute à la fin celles qui n'existaient pas.
    // Tout le reste du fichier (commentaires, host-config, sound…) reste intact.

    private static string MergeIntoBaseUae(
        string baseContent, Dictionary<string, string> overrides)
    {
        var lines       = baseContent.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();
        var appliedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(";") || trimmed.StartsWith("#")) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();

            if (overrides.TryGetValue(key, out var newValue))
            {
                lines[i] = string.IsNullOrEmpty(newValue) ? $"{key}=" : $"{key}={newValue}";
                appliedKeys.Add(key);
            }
        }

        var missing = overrides
            .Where(kv => !appliedKeys.Contains(kv.Key) && !string.IsNullOrEmpty(kv.Value))
            .ToList();
        if (missing.Count > 0)
        {
            lines.Add("");
            lines.Add("; --- Ajouté par DemoBase (absent du fichier de base) ---");
            foreach (var kv in missing)
                lines.Add($"{kv.Key}={kv.Value}");
        }

        return string.Join("\n", lines);
    }

    // ── Extraction ZIP (single file) ─────────────────────────────────────────

    private static async Task<string> ExtractFirstUsableAsync(
        string zipPath, string configDir, int releaseId)
        => await Task.Run(() =>
        {
            var extractDir = Path.Combine(configDir, "extracted", $"amiga_{releaseId}");
            Directory.CreateDirectory(extractDir);

            using var zip = ZipFile.OpenRead(zipPath);
            // Priorité : .adf, .adz, .dms, .fdi, .ipf, .hdf
            var preferred = new[] { ".adf", ".adz", ".dms", ".fdi", ".ipf", ".hdf" };
            var entry = zip.Entries
                .Where(e => preferred.Contains(
                    Path.GetExtension(e.Name).ToLowerInvariant()))
                .OrderBy(e => Array.IndexOf(preferred,
                    Path.GetExtension(e.Name).ToLowerInvariant()))
                .FirstOrDefault()
                ?? zip.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));

            if (entry == null) return zipPath;

            var dest = Path.Combine(extractDir, entry.Name);
            if (!File.Exists(dest))
                entry.ExtractToFile(dest, overwrite: false);
            return dest;
        });
    /// <summary>
    /// Patche les lignes <c>filesystem2=</c> et <c>uaehfN=dir,</c> du .uae
    /// généré pour remplacer les chemins relatifs par des chemins absolus basés
    /// sur le dossier de l exe WinUAE.
    ///
    /// Sans ce patch, un .uae écrit dans Working/Configs/ avec un chemin relatif
    /// comme <c>.\Demos</c> est résolu par WinUAE depuis son propre répertoire
    /// courant, ce qui peut varier. On force le chemin absolu.
    /// </summary>
    private static string PatchHddPaths(string uaeContent, string winUaeExeDir)
    {
        // Matche les deux formats :
        //   filesystem2=rw,Demos:Demos:.\Demos,0
        //   uaehf0=dir,rw,Demos:Demos:.\Demos,0  (et uaehf1..9)
        // Groupe 1 = "key=...DevName:VolName:"  Groupe 2 = chemin brut  Groupe 3 = ",flags..."
        var rx = new System.Text.RegularExpressions.Regex(
            @"^((?:filesystem2|uaehf\d+)=(?:dir,)?(?:rw|ro),(?:[^:]+:[^:]+:))([^,
]+)((?:,.*)?)$",
            System.Text.RegularExpressions.RegexOptions.Multiline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return rx.Replace(uaeContent, m =>
        {
            var rawPath = m.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(rawPath)) return m.Value;

            var resolved = Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.GetFullPath(Path.Combine(winUaeExeDir, rawPath));

            if (resolved == rawPath) return m.Value;

            System.Diagnostics.Debug.WriteLine(
                $"[WINUAE] PatchHddPaths : '{rawPath}' → '{resolved}'");
            return m.Groups[1].Value + resolved + m.Groups[3].Value;
        });
    }

    // ── Startup-Sequence ─────────────────────────────────────────────────────

    // Extensions de fichiers jamais lancés directement (données, docs, images…).
    private static readonly HashSet<string> HddIgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".dat", ".txt", ".nfo", ".diz", ".doc", ".pdf", ".jpg", ".jpeg",
          ".png", ".gif", ".bmp", ".iff", ".ilbm", ".readme", ".md" };

    /// <summary>
    /// Lit le fichier .uae de base et extrait le nom de volume Amiga du
    /// premier disque dur virtuel de type "dir" (le champ VolName de
    /// "uaehfN=dir,rw,DevName:VolName:path,flags").
    /// Retourne "Demos" si absent ou non trouvé.
    /// </summary>
    private static string DetectVolumeName(string? uaePath)
    {
        if (string.IsNullOrWhiteSpace(uaePath) || !File.Exists(uaePath))
            return "Demos";

        var rx = new System.Text.RegularExpressions.Regex(
            @"^uaehf\d+=dir,[^,]+,([^:]+):([^:]+):",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Multiline);

        foreach (var line in File.ReadLines(uaePath))
        {
            var m = rx.Match(line);
            if (m.Success)
            {
                var vol = m.Groups[2].Value.Trim(); // VolName (ex. "Demos")
                System.Diagnostics.Debug.WriteLine($"[WINUAE] Volume Amiga détecté : '{vol}'");
                return string.IsNullOrWhiteSpace(vol) ? "Demos" : vol;
            }
        }
        return "Demos";
    }

    /// <summary>
    /// Détermine si un fichier est un binaire exécutable Amiga (ou tout autre
    /// binaire) ou un script texte AmigaOS (Amiga Script / AmigaDOS).
    ///
    /// Heuristique : un fichier est traité comme binaire s'il commence par les
    /// magic bytes AmigaOS ($000003F3 = Amiga Hunk, ou $7F454C46 = ELF),
    /// ou s'il contient des octets NUL dans les 512 premiers octets.
    /// Sinon on le considère comme un script texte (Execute).
    /// </summary>
    private static bool IsAmigaBinary(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            var buf = new byte[Math.Min(512, (int)fs.Length)];
            int read = fs.Read(buf, 0, buf.Length);
            if (read < 2) return false;

            // Amiga Hunk ($000003F3)
            if (read >= 4 && buf[0] == 0x00 && buf[1] == 0x00 &&
                buf[2] == 0x03 && buf[3] == 0xF3)
                return true;

            // ELF ($7F454C46)
            if (read >= 4 && buf[0] == 0x7F && buf[1] == 0x45 &&
                buf[2] == 0x4C && buf[3] == 0x46)
                return true;

            // Présence d'octets NUL → probablement binaire
            for (int i = 0; i < read; i++)
                if (buf[i] == 0x00)
                    return true;

            return false; // Tout ASCII → script texte
        }
        catch { return false; }
    }

    /// <summary>
    /// Met à jour (ou crée) le fichier S/Startup-Sequence dans le dossier HD
    /// virtuel pour lancer automatiquement le fichier principal de la release.
    ///
    /// Règle AmigaOS :
    ///   - Binaire exécutable → écrire "VolName:NomFichier"
    ///   - Script texte       → écrire "Execute VolName:NomFichier"
    ///
    /// Le dossier S/ est créé s'il n'existe pas.
    /// </summary>
    private static async Task UpdateStartupSequenceAsync(
        string hddTarget, List<string> extractedFiles, string volumeName,
        int configId, int releaseId, PreferencesService prefs,
        Func<IEnumerable<string>, Task<string?>>? askUser = null)
    {
        // Identifier le fichier principal : premier fichier non ignoré et
        // non situé dans un sous-dossier S/ (le S-S lui-même).
        var candidates = extractedFiles
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                var ext  = Path.GetExtension(f);
                if (string.IsNullOrEmpty(name)) return false;
                // Exclure les fichiers dans S/ (Startup-Sequence, etc.)
                var rel = Path.GetRelativePath(hddTarget, f);
                if (rel.StartsWith("S" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false;
                // Exclure les extensions jamais lancées
                if (HddIgnoredExtensions.Contains(ext)) return false;
                return true;
            })
            .OrderBy(f =>
            {
                // Priorité : .exe > autres > sans extension
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext == ".exe" ? 0 : ext == "" ? 2 : 1;
            })
            .ToList();

        if (candidates.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine(
                "[WINUAE] UpdateStartupSequence : aucun fichier principal trouvé.");
            return;
        }

        // Clé de mémorisation du choix : spécifique au profil ET à la release.
        var prefKey = $"winuae_startup:{configId}:{releaseId}";

        string? mainFile;
        if (candidates.Count == 1)
        {
            // Un seul candidat → pas de doute, on mémorise quand même pour cohérence.
            mainFile = candidates[0];
            await prefs.SetAsync(prefKey, Path.GetFileName(mainFile));
        }
        else
        {
            // Plusieurs candidats → vérifier si un choix a déjà été mémorisé.
            var saved = await prefs.GetAsync(prefKey);
            var savedMatch = string.IsNullOrEmpty(saved)
                ? null
                : candidates.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), saved, StringComparison.OrdinalIgnoreCase));

            if (savedMatch != null)
            {
                // Choix mémorisé trouvé parmi les candidats → on le réutilise.
                mainFile = savedMatch;
                System.Diagnostics.Debug.WriteLine(
                    $"[WINUAE] Fichier mémorisé réutilisé : '{Path.GetFileName(mainFile)}'");
            }
            else if (askUser != null)
            {
                // Pas de mémorisation (ou fichier disparu) → demander.
                var chosen = await askUser(candidates.Select(Path.GetFileName)!);
                mainFile = chosen == null
                    ? null
                    : candidates.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), chosen, StringComparison.OrdinalIgnoreCase));

                // Mémoriser le choix de l'utilisateur.
                if (mainFile != null)
                    await prefs.SetAsync(prefKey, Path.GetFileName(mainFile));
            }
            else
            {
                // Pas de dialog → prendre le premier.
                mainFile = candidates[0];
                await prefs.SetAsync(prefKey, Path.GetFileName(mainFile));
            }
        }

        if (mainFile == null)
        {
            System.Diagnostics.Debug.WriteLine("[WINUAE] Sélection annulée par l'utilisateur.");
            return;
        }

        var fileName   = Path.GetFileName(mainFile);
        bool isBinary  = IsAmigaBinary(mainFile);
        string launchLine = isBinary
            ? $"{volumeName}:{fileName}"
            : $"Execute {volumeName}:{fileName}";

        System.Diagnostics.Debug.WriteLine(
            $"[WINUAE] Startup-Sequence → '{launchLine}' " +
            $"(fichier={fileName}, binaire={isBinary})");

        // Lire le Startup-Sequence existant et remplacer la dernière ligne de lancement,
        // ou l'ajouter à la fin si absent.
        var sDir  = Path.Combine(hddTarget, "S");
        var ssPath = Path.Combine(sDir, "Startup-Sequence");
        Directory.CreateDirectory(sDir);

        string newContent;
        if (File.Exists(ssPath))
        {
            var lines = (await File.ReadAllTextAsync(ssPath)).TrimEnd();

            // Remplace la dernière ligne non-vide et non-commentaire qui ressemble
            // à une commande de lancement (contient ":" sans espace avant le ":").
            var lineList = lines.Split('\n').ToList();
            int lastLaunch = -1;
            for (int i = lineList.Count - 1; i >= 0; i--)
            {
                var l = lineList[i].TrimEnd();
                if (string.IsNullOrWhiteSpace(l) || l.TrimStart().StartsWith(";"))
                    continue;
                // Ligne de lancement = contient "DevName:..." ou "Execute DevName:..."
                if (System.Text.RegularExpressions.Regex.IsMatch(l,
                        @"(?i)^(?:Execute\s+)?\w+:[^\s]"))
                {
                    lastLaunch = i;
                    break;
                }
            }

            if (lastLaunch >= 0)
                lineList[lastLaunch] = launchLine;
            else
                lineList.Add(launchLine);

            newContent = string.Join("\n", lineList) + "\n";
        }
        else
        {
            // Startup-Sequence minimaliste si le dossier S/ était absent du zip.
            newContent = $"; Generated by DemoBase\n{launchLine}\n";
        }

        await File.WriteAllTextAsync(ssPath, newContent,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }


    // ── Nettoyage du dossier HD virtuel ──────────────────────────────────────

    // Dossiers AmigaOS système à préserver entre deux lancements.
    // C = commandes Amiga (dir, copy, list…)
    // S = scripts système (Startup-Sequence, Shell-Startup…)
    // Devs = périphériques et drivers (monitors, keymaps…)
    private static readonly HashSet<string> HddPreservedDirs =
        new(StringComparer.OrdinalIgnoreCase) { "C", "S", "Devs" };

    /// <summary>
    /// Nettoie le dossier HD virtuel Amiga avant d'y extraire une nouvelle release.
    /// Supprime tous les fichiers et dossiers du premier niveau SAUF les dossiers
    /// système AmigaOS (C, S, Devs) qui font partie de la config de base WinUAE.
    /// </summary>
    private static void CleanHddTarget(string hddTarget)
    {
        if (!Directory.Exists(hddTarget)) return;

        // Supprimer les fichiers à la racine
        foreach (var file in Directory.GetFiles(hddTarget))
        {
            try { File.Delete(file); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[WINUAE] CleanHddTarget : impossible de supprimer '{file}' : {ex.Message}");
            }
        }

        // Supprimer les sous-dossiers non préservés (avec leur contenu)
        foreach (var dir in Directory.GetDirectories(hddTarget))
        {
            var name = Path.GetFileName(dir);
            if (HddPreservedDirs.Contains(name))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[WINUAE] CleanHddTarget : dossier système préservé '{name}'");
                continue;
            }
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[WINUAE] CleanHddTarget : impossible de supprimer '{dir}' : {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine(
            $"[WINUAE] CleanHddTarget : '{hddTarget}' nettoyé (C/S/Devs préservés)");
    }

}
