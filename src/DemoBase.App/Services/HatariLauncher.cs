using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Hatari ─────────────────────────────────────────────────

public static class HatariSettings
{
    public const string MachineType = "machine_type"; // "st" | "megast" | "ste" | "megaste" | "tt" | "falcon"
    public const string Monitor     = "monitor";       // "mono" | "rgb" | "vga" | "tv"
    public const string TosPath     = "tos_path";       // chemin de la ROM TOS — optionnel : vide
                                                          // = Hatari utilise l'EmuTOS qu'il embarque
                                                          // (ou sa propre config globale déjà en place)
    public const string Borders     = "borders";        // "true" / "false" — bords overscan visibles
    public const string FullScreen  = "fullscreen";      // "true" / "false"
    public const string StatusBar   = "statusbar";       // "true" / "false" — barre de statut en bas
    public const string DriveLed    = "drive_led";       // "true" / "false" — LED disque en overlay
    public const string StRam       = "st_ram";          // valeur brute pour --memsize (cf. manuel :
                                                          // "256"=256 KiB, "0"=512 KiB, "2560"=2.5 MiB,
                                                          // sinon valeur directe en MiB)
    public const string TtRam       = "tt_ram";           // valeur brute pour --ttram, en MiB (0=désactivé)
    public const string FastBoot    = "fast_boot";        // "true" / "false" — --fast-boot : patch le TOS
                                                          // pour contourner le test mémoire (boot bien plus
                                                          // rapide). Activé par défaut ; à désactiver pour
                                                          // les rares progs exigeant un boot non patché.
    public const string TimerD      = "timer_d";          // "true" / "false" — --timer-d : patch le Timer-D
                                                          // (quasi ×2 la vitesse d'émulation ST/e). Activé
                                                          // par défaut ; sûr dans l'immense majorité des cas.

    // ── Paramètres CPU avancés (fenêtre "CPU emulation parameters" de Hatari) ──
    // Cf. manuel officiel : options qualifiées d'"expérimentales", à ne changer
    // que si on sait ce qu'on fait — exposées ici telles quelles pour permettre
    // de reproduire depuis un profil DemoBase exactement la config vue dans la
    // GUI Hatari (ex. cycle-exact + data cache activés pour un 030, MMU pour du
    // vrai multitâche TOS, etc.).
    public const string Prefetch     = "prefetch";       // "true" / "false" — --compatible (mode 68000 compatible : prefetch + address errors)
    public const string CpuExact     = "cpu_exact";      // "true" / "false" — --cpu-exact (émulation CPU cycle-exact)
    public const string DataCache    = "data_cache";     // "true" / "false" — --data-cache (cache données, >=030 uniquement)
    public const string Mmu          = "mmu";            // "true" / "false" — --mmu (émulation MMU, >=030 uniquement)
    public const string Addr24       = "addr24";         // "true" / "false" — --addr24 (adressage 24 bits au lieu de 32)
    public const string FpuSoftfloat = "fpu_softfloat";  // "true" / "false" — --fpu-softfloat (FPU logicielle précise, "Accurate FPU emulation")

    // ── Résolution VDI étendue (dialogue "Atari monitor" de Hatari, bas de fenêtre) ──
    // Cf. manuel officiel : surtout utile en ST/STE pour des applis GEM (99% des demos/
    // jeux ne fonctionnent PAS en VDI — à laisser désactivé pour tout le reste).
    public const string VdiEnabled = "vdi_enabled"; // "true" / "false" — --vdi
    public const string VdiWidth   = "vdi_width";   // --vdi-width  (320 < w <= 2048)
    public const string VdiHeight  = "vdi_height";  // --vdi-height (160 < h <= 1280)
    public const string VdiPlanes  = "vdi_planes";  // --vdi-planes (1=2 couleurs, 2=4 couleurs, 4=16 couleurs)

    // ── Dialogue "CPU options" de Hatari (CPU type / CPU clock / FPU) ──────────
    // Valeur "auto" (ou absente) = comportement historique DemoBase : cpulevel déduit
    // de la machine (0=68000 pour ST/STE, 3=68030 pour TT/Falcon), --cpuclock et --fpu
    // non forcés (Hatari applique ses propres défauts selon la machine choisie).
    public const string CpuType  = "cpu_type";  // "auto" | "0".."5" (68000..68060) — --cpulevel
    public const string CpuClock = "cpu_clock"; // "auto" | "8" | "16" | "32"        — --cpuclock
    public const string Fpu      = "fpu";       // "auto" | "none" | "68881" | "68882" | "internal" — --fpu
}

// ─── Lanceur Hatari (Atari ST/STE/TT/Falcon) ─────────────────────────────────
// Comme Altirra, Hatari ne nécessite pas de fichier de config généré : tout se
// pilote en ligne de commande (cf. Hatari --help). Contrairement à Altirra,
// Hatari n'a pas d'OS interne — il a besoin d'une ROM TOS, mais embarque sa
// propre EmuTOS de secours, donc on ne bloque PAS le lancement si l'utilisateur
// n'a pas configuré de TOS (à la différence du Kickstart WinUAE, obligatoire).

public class HatariLauncher
{
    private readonly PreferencesService _prefs;

    // Hatari ne reconnaît officiellement que .st/.msa/.stx comme images disquette
    // (cf. manuel officiel). Programmes exécutables directement : .prg/.tos/.ttp/.app —
    // passés en argument positionnel (pas de switch dédié) : Hatari détecte lui-même
    // le type, et pour un programme, monte son dossier parent en C: et le lance.
    private static readonly string[] DiskExtensions = [".st", ".msa", ".stx"];
    // .exe = binaire Falcon (executables TOS/Falcon — pas des exe Windows !), très
    // fréquent sur les demos Falcon (ex. Starstruck-Final.exe de The Black Lotus).
    private static readonly string[] PrgExtensions  = [".prg", ".tos", ".ttp", ".app", ".exe"];

    // Fichiers compagnons jamais lancés (présents dans beaucoup de ZIP de la scène à
    // côté du vrai programme : readme, nfo, diz, images de présentation...) — exclus
    // du tout dernier repli pour ne pas risquer de les passer à Hatari par erreur.
    // .dat = fichiers de données Falcon (ex. Starstruck-Final.dat, 18 Mo de données
    // graphiques/audio) : toujours accompagnés d'un .exe ou .prg principal, ne
    // jamais sélectionner comme fichier à lancer.
    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md",
         ".dat"];

    public HatariLauncher(PreferencesService prefs)
        => _prefs = prefs;

    // ─── Lancement principal ──────────────────────────────────────────────────

    public async Task<LaunchResult> LaunchAsync(
        Emulator            emulator,
        EmulatorConfig      config,
        Dictionary<string, string?> settings,
        string              romPath,
        Release             release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[HATARI] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        // 1. Vérifier l'exe
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"Hatari introuvable : {emulator.ExecutablePath}");

        // 2. Vérifier le fichier
        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[HATARI] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        // 3. ROM TOS : optionnelle. Si l'utilisateur en a configuré une mais que le
        //    fichier n'existe plus, on bloque (mieux qu'un échec silencieux/confus
        //    côté Hatari) ; si le champ est vide, Hatari se débrouille seul (EmuTOS
        //    embarqué ou config globale déjà en place sur la machine de l'utilisateur).
        var tosPath = settings.GetValueOrDefault(HatariSettings.TosPath);
        if (!string.IsNullOrWhiteSpace(tosPath) && !File.Exists(tosPath))
        {
            System.Diagnostics.Debug.WriteLine($"[HATARI] ROM TOS configurée introuvable : {tosPath}");
            return new(false, $"ROM TOS configurée introuvable : {tosPath}");
        }

        // 4. Si ZIP → repérer toutes les disquettes présentes (releases multi-disk),
        //    sinon retomber sur le fichier tel quel
        var diskFiles  = new List<string>();
        string? single = null;
        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            (diskFiles, single) = await ExtractUsableFilesAsync(romPath, configDir, release.Id);
            actualFile = diskFiles.Count > 0 ? diskFiles[0] : (single ?? romPath);

            if (diskFiles.Count > 1)
                System.Diagnostics.Debug.WriteLine(
                    $"[HATARI] ZIP multi-disk : {diskFiles.Count} disquette(s) trouvée(s) (montées sur A: puis B: ; au-delà de 2, à insérer manuellement depuis Hatari — AltGr+D) : {string.Join(", ", diskFiles.Select(Path.GetFileName))}");
            else
                System.Diagnostics.Debug.WriteLine($"[HATARI] ZIP extrait → fichier choisi : {actualFile}");
        }

        // 5. Déterminer le modèle de machine (st / ste / tt / falcon, ...)
        var machine = DetectMachine(release, settings);
        System.Diagnostics.Debug.WriteLine($"[HATARI] Machine détectée : {machine}");

        // 6. Construire la ligne de commande
        var args = BuildArguments(config, settings, machine, tosPath, diskFiles, actualFile);

        // 7. Hatari n'a pas de bouton "Fermer" ni de raccourci Alt+F4 classique — il faut
        //    AltGr+Q (ou F12 → Quit). Info pas évidente pour l'utilisateur → popup
        //    ponctuelle, même principe que le TR-DOS de ZEsarUX / blueMSX / WinUAE.
        await MaybeShowQuitInfoAsync();

        // 8. Lancer Hatari
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "HATARI", friendlyName: "Hatari");
    }

    // ── Popup d'info sur le raccourci pour quitter ──────────────────────────────

    private const string QuitInfoDialogPrefKey = "hatari.quit_info_dialog.hidden";

    /// <summary>
    /// Affiche la popup expliquant AltGr+Q pour quitter Hatari, sauf si l'utilisateur a déjà
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
                var dlg = new DemoBase.App.Views.HatariInfoDialog
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
            Debug.WriteLine($"[HATARI] Popup info quitter : échec — {ex.Message}");
        }
    }

    // ─── Détection du modèle de machine ───────────────────────────────────────

    private static string DetectMachine(Release release, Dictionary<string, string?> settings)
    {
        // 1. Setting manuel prioritaire (valeur déjà "propre" : "st" / "ste" / "tt" /
        //    "falcon" / ... cf. HatariSettingsControl — Tag du ComboBox)
        if (settings.TryGetValue(HatariSettings.MachineType, out var manual)
            && !string.IsNullOrWhiteSpace(manual))
            return manual;

        // 2. Détection depuis les plateformes de la release
        var platformNames = release.ReleasePlatforms
            .Where(rp => rp.Platform != null)
            .Select(rp => rp.Platform!.Name.ToLowerInvariant())
            .ToList();

        if (platformNames.Any(p => p.Contains("falcon")))
            return "falcon";
        if (platformNames.Any(p => p.Contains("tt")))
            return "tt";
        if (platformNames.Any(p => p.Contains("ste")))
            return "ste";

        // Défaut : Atari ST de base (la grande majorité des demos/jeux anciens)
        return "st";
    }

    // ─── Construction de la ligne de commande ─────────────────────────────────

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings,
        string machine, string? tosPath, List<string> diskFiles, string file)
    {
        var sb = new StringBuilder();

        sb.Append($"--machine {machine}");

        // CPU type (dialogue "CPU options" de Hatari) : priorité au réglage manuel du
        // profil s'il existe ("auto" ou absent = comportement historique, déduit de la
        // machine pour ne pas hériter d'un réglage 68000 laissé par une session Hatari
        // précédente si on bascule sur TT/Falcon — cf. manuel : "Atari TT and Falcon
        // computers were using the 68030 CPU").
        var cpuType = settings.GetValueOrDefault(HatariSettings.CpuType);
        if (string.IsNullOrWhiteSpace(cpuType) || cpuType == "auto")
            cpuType = machine is "tt" or "falcon" ? "3" : "0";
        sb.Append($" --cpulevel {cpuType}");

        // CPU clock et FPU : seulement si explicitement configurés ("auto" = on laisse
        // Hatari appliquer ses propres défauts selon la machine, cf. manuel — le FPU
        // 68882 n'est par exemple activé par défaut que pour la machine TT).
        var cpuClock = settings.GetValueOrDefault(HatariSettings.CpuClock);
        if (!string.IsNullOrWhiteSpace(cpuClock) && cpuClock != "auto")
            sb.Append($" --cpuclock {cpuClock}");
        var fpu = settings.GetValueOrDefault(HatariSettings.Fpu);
        if (!string.IsNullOrWhiteSpace(fpu) && fpu != "auto")
            sb.Append($" --fpu {fpu}");

        // DSP : nécessaire à beaucoup de demos Falcon (musique/effets temps réel)
        if (machine == "falcon")
            sb.Append(" --dsp emu");

        // Paramètres CPU avancés (fenêtre "CPU emulation parameters" de la GUI Hatari) —
        // toujours passés explicitement pour que le profil DemoBase reproduise fidèlement
        // ce que l'utilisateur a configuré, plutôt que de dépendre des défauts internes de
        // Hatari (qui varient selon le --cpulevel choisi). Défauts DemoBase alignés sur
        // ceux de la GUI Hatari pour un CPU >=030 : cycle-exact et data-cache activés,
        // le reste désactivé (cf. HatariSettingsViewModel).
        var prefetch = settings.GetValueOrDefault(HatariSettings.Prefetch) == "true";
        sb.Append(prefetch ? " --compatible true" : " --compatible false");
        var cpuExact = settings.GetValueOrDefault(HatariSettings.CpuExact) != "false";
        sb.Append(cpuExact ? " --cpu-exact true" : " --cpu-exact false");
        var dataCache = settings.GetValueOrDefault(HatariSettings.DataCache) != "false";
        sb.Append(dataCache ? " --data-cache true" : " --data-cache false");
        var mmu = settings.GetValueOrDefault(HatariSettings.Mmu) == "true";
        sb.Append(mmu ? " --mmu true" : " --mmu false");
        var addr24 = settings.GetValueOrDefault(HatariSettings.Addr24) == "true";
        sb.Append(addr24 ? " --addr24 true" : " --addr24 false");
        var fpuSoftfloat = settings.GetValueOrDefault(HatariSettings.FpuSoftfloat) == "true";
        sb.Append(fpuSoftfloat ? " --fpu-softfloat true" : " --fpu-softfloat false");

        var monitor = settings.GetValueOrDefault(HatariSettings.Monitor) ?? "rgb";
        sb.Append($" --monitor {monitor}");

        // Mémoire : valeur Tag du ComboBox déjà au format brut attendu par Hatari (cf.
        // HatariSettingsControl et manuel : pas un simple nombre de MiB pour 256 KiB et
        // 2.5 MiB, qui se codent en KiB). TT-RAM toujours passée (no-op hors TT/Falcon).
        var stRam = settings.GetValueOrDefault(HatariSettings.StRam);
        sb.Append($" --memsize {(string.IsNullOrWhiteSpace(stRam) ? "1" : stRam)}");
        var ttRam = settings.GetValueOrDefault(HatariSettings.TtRam);
        sb.Append($" --ttram {(string.IsNullOrWhiteSpace(ttRam) ? "0" : ttRam)}");

        if (!string.IsNullOrWhiteSpace(tosPath))
            sb.Append($" --tos \"{tosPath}\"");

        // Bords overscan visibles par défaut : beaucoup de demos ST/STE/Falcon
        // jouent justement sur leur suppression, intérêt à les voir apparaître.
        var borders = settings.GetValueOrDefault(HatariSettings.Borders) != "false";
        sb.Append(borders ? " --borders true" : " --borders false");

        // Résolution VDI étendue (bas du dialogue "Atari monitor" de Hatari) — désactivée
        // par défaut : 99% des demos/jeux de la scène ne fonctionnent pas en VDI (seules
        // les applis GEM le supportent), cf. manuel officiel. Largeur/hauteur/profondeur
        // ignorées par Hatari tant que --vdi est à false.
        var vdiEnabled = settings.GetValueOrDefault(HatariSettings.VdiEnabled) == "true";
        sb.Append(vdiEnabled ? " --vdi true" : " --vdi false");
        if (vdiEnabled)
        {
            var vdiWidth  = settings.GetValueOrDefault(HatariSettings.VdiWidth);
            var vdiHeight = settings.GetValueOrDefault(HatariSettings.VdiHeight);
            var vdiPlanes = settings.GetValueOrDefault(HatariSettings.VdiPlanes);
            sb.Append($" --vdi-width {(string.IsNullOrWhiteSpace(vdiWidth) ? "640" : vdiWidth)}");
            sb.Append($" --vdi-height {(string.IsNullOrWhiteSpace(vdiHeight) ? "480" : vdiHeight)}");
            sb.Append($" --vdi-planes {(string.IsNullOrWhiteSpace(vdiPlanes) ? "4" : vdiPlanes)}");
        }

        sb.Append(settings.GetValueOrDefault(HatariSettings.FullScreen) == "true"
            ? " --fullscreen" : " --window");

        // Indicateurs (section "Indicators" de la GUI Hatari, deux options CLI
        // indépendantes) : barre de statut visible par défaut, LED disque masquée.
        var statusBar = settings.GetValueOrDefault(HatariSettings.StatusBar) != "false";
        sb.Append(statusBar ? " --statusbar true" : " --statusbar false");
        var driveLed = settings.GetValueOrDefault(HatariSettings.DriveLed) == "true";
        sb.Append(driveLed ? " --drive-led true" : " --drive-led false");

        sb.Append(" --confirm-quit false");

        // Accélération du boot / de l'émulation (activées par défaut) :
        //  --fast-boot : patch le TOS pour sauter le test mémoire → démarrage bien
        //                plus rapide (surtout sensible au lancement d'un .prg via
        //                le disque dur virtuel GEMDOS).
        //  --timer-d   : patch le Timer-D, qui sinon génère un flot d'interruptions
        //                inutiles ralentissant fortement l'émulation ST/STE.
        var fastBoot = settings.GetValueOrDefault(HatariSettings.FastBoot) != "false";
        sb.Append(fastBoot ? " --fast-boot true" : " --fast-boot false");
        var timerD = settings.GetValueOrDefault(HatariSettings.TimerD) != "false";
        sb.Append(timerD ? " --timer-d true" : " --timer-d false");

        // ── Montage du média ────────────────────────────────────────────────────
        // Toujours en dernier sur la ligne : Hatari démarre sur ce qui est donné
        // en dernier (option ou argument), cf. manuel officiel.
        if (diskFiles.Count > 1)
        {
            // Hatari n'a que 2 lecteurs physiques A:/B: — contrairement à Altirra
            // (D1:, D2:, D3:...), pas de 3e lecteur disponible. Au-delà de 2
            // disquettes, les suivantes ne sont pas montées ici ; l'utilisateur
            // les insère depuis Hatari (AltGr+D) si le programme les redemande.
            sb.Append($" --disk-a \"{diskFiles[0]}\"");
            sb.Append($" --disk-b \"{diskFiles[1]}\"");
        }
        else
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (DiskExtensions.Contains(ext))
                sb.Append($" --disk-a \"{file}\"");
            else
                // .prg/.tos/.ttp/.app, ou type non reconnu : argument positionnel —
                // Hatari détecte lui-même le type, et pour un programme, monte son
                // dossier parent en C: et le lance automatiquement (cf. manuel).
                sb.Append($" \"{file}\"");
        }

        // ── Ligne de commande additionnelle définie sur le profil (optionnelle) ─
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
        {
            var extra = EmulatorLaunchService.SubstituteVars(config.CommandLine, file);
            sb.Append(' ').Append(extra);
        }

        return sb.ToString();
    }

    // ─── Extraction ZIP (même logique que AltirraLauncher, détection multi-disk
    //     incluse, adaptée aux extensions Atari ST) ─────────────────────────────

    private static Task<(List<string> diskFiles, string? singleFile)> ExtractUsableFilesAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFilesSync(zipPath, outDir, releaseId));

    private static (List<string> diskFiles, string? singleFile) ExtractUsableFilesSync(string zipPath, string outDir, int releaseId)
    {
        // Dossier court (Id) plutôt que le titre complet — cf. bug MAX_PATH constaté.
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("atarist", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Toutes les disquettes du ZIP, triées par nom de fichier (disk1 avant disk2)
        var disks = files
            .Where(f => DiskExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (disks.Count > 0)
            return (disks, null);

        // Pas de disquette : priorité aux extensions programme reconnues (.prg/.tos/
        // .ttp/.app) — sinon, n'importe quel fichier énuméré par le système de fichiers
        // pourrait être choisi (l'ordre n'est PAS garanti alphabétique), au risque de
        // sélectionner un readme/nfo livré à côté du vrai programme dans le ZIP.
        var prg = files.FirstOrDefault(f => PrgExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (prg != null)
            return ([], prg);

        // Dernier repli : un fichier qui n'est pas un compagnon évidemment non lançable
        // (readme/nfo/diz/image...), sinon vraiment n'importe lequel.
        var candidate = files.FirstOrDefault(f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault();
        return ([], candidate);
    }
}
