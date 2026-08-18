using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings UnrealSpeccy ───────────────────────────────────────────

public static class UnrealSpeccySettings
{
    /// <summary>
    /// Piloté exclusivement via [VIDEO] FullScr=0/1 dans l'ini généré (PrepareIni) — le
    /// switch CLI -f, utilisé avant ce fix, ne mettait pas fiablement l'émulateur en plein
    /// écran (signalé par l'utilisateur), abandonné au profit de l'ini.
    /// </summary>
    public const string FullScreen = "fullscreen"; // "true" / "false"
    /// <summary>
    /// Modèle machine HIMEM : PENTAGON (défaut), SCORPION, KAY, PROFI, ATM450, ATM710, TSL.
    /// Injecté dans la clé HIMEM= de unreal.ini via un fichier INI temporaire.
    /// </summary>
    public const string Machine = "machine";
    /// <summary>
    /// Filtre vidéo [VIDEO] video= (normal/double/triple/quad/text/resampler/bilinear/scale/
    /// advmame/tv/ch_ov/ch_hw/ch_bl/ch_b/ch4true — cf. commentaires unreal.ini). "triple" par
    /// défaut si absent.
    /// </summary>
    public const string VideoFilter = "video_filter";
}

// ─── Lanceur UnrealSpeccy ─────────────────────────────────────────────────────
// UnrealSpeccy est la référence pour les démos ZX Spectrum sous Windows.
// Il émule le Pentagon 128 (clone russe ciblé par la majorité des démos scène),
// ainsi que Sinclair 48K/128K, Scorpion 256, ATM, KAY.
//
// Commande : unreal.exe [-f] <fichier>
//   -f : plein écran (optionnel)
//
// Formats supportés nativement (pas besoin d'extraction) :
//   .sna, .z80, .szx — snapshots
//   .trd, .scl        — disques TR-DOS (Pentagon)
//   .fdi, .td0, .udi  — images disquette
//   .tap, .tzx, .csw  — cassettes
//   .rzx              — replays
//   .zip              — archive (UnrealSpeccy l'ouvre directement)
//
// La configuration du modèle machine (Pentagon, 48K, 128K, Scorpion…)
// se fait dans unreal.ini dans le répertoire de l'exécutable.
// Pentagon 128 est fortement recommandé pour les démos scène.

public class UnrealSpeccyLauncher
{
    private readonly PreferencesService _prefs;

    // Formats que UnrealSpeccy peut recevoir directement (y compris ZIP)
    private static readonly HashSet<string> DirectExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".sna", ".z80", ".szx",          // snapshots
            ".trd", ".scl",                   // disques TR-DOS
            ".fdi", ".td0", ".udi",           // images disquette
            ".tap", ".tzx", ".csw",           // cassettes
            ".rzx",                           // replays
            ".zip",                           // archive — ouverte directement
        };

    // Fichiers à ignorer lors de l'extraction
    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".nfo", ".diz", ".md", ".doc", ".pdf",
            ".jpg", ".jpeg", ".png", ".gif", ".bmp",
        };

    public UnrealSpeccyLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[UNREAL] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"UnrealSpeccy introuvable : {emulator.ExecutablePath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var ext = Path.GetExtension(romPath).ToLowerInvariant();

        // Toujours extraire le ZIP — UnrealSpeccy ne supporte pas tous les formats
        // depuis un ZIP (les .tap/.tzx échouent, seuls .trd/.sna fonctionnent parfois).
        // L'extraction garantit la compatibilité universelle.
        var actualFile = romPath;
        if (ext == ".zip")
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[UNREAL] ZIP extrait → {actualFile}");
        }

        // Filet de sécurité machine TSL : le téléchargement du pack (roms/boot.$b/wc.img) se
        // déclenche normalement dès la sélection de "TSL" dans les réglages du profil
        // (UnrealSpeccySettingsViewModel.OnMachineChanged). Si ce profil est lancé sans être
        // jamais passé par cette page (import JSON, profil créé autrement...), on retente ici
        // avant de générer l'INI. Best-effort : un échec n'empêche pas le lancement (TSL sera
        // juste non fonctionnel, comme avant ce fix).
        var machine = (settings.GetValueOrDefault(UnrealSpeccySettings.Machine) ?? "PENTAGON")
                      .ToUpperInvariant();
        if (machine == "TSL")
        {
            var exeDirForPack = Path.GetDirectoryName(emulator.ExecutablePath)!;
            if (!UnrealSpeccyTslPackService.IsInstalled(exeDirForPack))
            {
                System.Diagnostics.Debug.WriteLine("[UNREAL] Pack TSL absent — téléchargement avant lancement…");
                var (packOk, packMsg) = await new UnrealSpeccyTslPackService()
                    .DownloadAndInstallAsync(exeDirForPack);
                System.Diagnostics.Debug.WriteLine($"[UNREAL] Pack TSL : {(packOk ? "OK" : "ÉCHEC")} — {packMsg}");
            }
        }

        // Filet "menu EVO Reset Service" (machine ATM3, firmware rom\zxevo.rom) : constaté par
        // test réel — contrairement à l'hypothèse initiale, "Emu tape load" (NVRAM/CMOS) ne
        // change RIEN à l'apparition de ce menu : c'est un vrai écran de BIOS matériel, TOUJOURS
        // affiché au reset, quels que soient les réglages persistés. Le fichier .tap, lui, EST
        // bien inséré dès le lancement (chargement CLI, cf. loadsnap() dans les sources SMT) —
        // il ne manque que la sélection manuelle de "T.Tape load" dans ce menu pour qu'il
        // démarre réellement. On déploie quand même NVRAM/CMOS (fournis par l'utilisateur,
        // inoffensif, peut aider pour d'autres préférences du menu), ET on simule la frappe "T"
        // quelques instants après le lancement — seul mécanisme confirmé fonctionner par
        // l'utilisateur (testé manuellement avec succès).
        if (machine == "ATM3")
        {
            UnrealSpeccyClassicBuildService.DeployPreconfiguredNvramCmos(
                Path.GetDirectoryName(emulator.ExecutablePath)!);
            _ = SendTapeLoadKeyToEvoMenuAsync();
        }

        // Toujours générer un INI temporaire pour neutraliser les autoloads
        // qui pointent vers des fichiers inexistants sur la machine de l'utilisateur.
        var iniPath = PrepareIni(emulator, settings);

        var args = BuildArguments(config, settings, actualFile, iniPath);

        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "UNREAL", friendlyName: "UnrealSpeccy");
    }

    // ── Filet "EVO Reset Service" (machine ATM3) ───────────────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_T             = 0x54; // touche 'T'
    private const uint KEYEVENTF_KEYUP  = 0x0002;

    /// <summary>
    /// Attend que le process unreal_speccy_portable.exe le plus récemment lancé ait une
    /// fenêtre visible, la met au premier plan, puis simule DEUX appuis sur "T" espacés (menu
    /// pas toujours prêt au même instant selon la machine), pour sélectionner "T.Tape load"
    /// dans le menu "EVO Reset Service" affiché au boot d'ATM3. Best-effort, ne fait jamais
    /// échouer le lancement lui-même. Utilise keybd_event (état clavier global) plutôt que
    /// PostMessage (souvent ignoré par les applications qui pollent le clavier directement,
    /// comme semble le faire cet émulateur — cf. le switch -f plein écran, jamais fiable non
    /// plus via un mécanisme passif).
    /// </summary>
    private static async Task SendTapeLoadKeyToEvoMenuAsync()
    {
        try
        {
            System.Diagnostics.Process? proc = null;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                var candidates = System.Diagnostics.Process.GetProcessesByName("unreal_speccy_portable");
                proc = candidates
                    .Where(p => { try { return !p.HasExited && p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
                    .OrderByDescending(p => { try { return p.StartTime; } catch { return DateTime.MinValue; } })
                    .FirstOrDefault();
                if (proc != null) break;
                await Task.Delay(250);
            }

            if (proc == null)
            {
                System.Diagnostics.Debug.WriteLine("[UNREAL] Filet EVO : fenêtre introuvable, touche 'T' non envoyée.");
                return;
            }

            // Laisser le menu EVO finir de s'afficher avant d'envoyer quoi que ce soit —
            // délai généreux, ajustable si le menu met encore plus longtemps à apparaître.
            await Task.Delay(2000);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (proc.HasExited) break;
                    SetForegroundWindow(proc.MainWindowHandle);
                    await Task.Delay(150);

                    keybd_event(VK_T, 0, 0, UIntPtr.Zero);
                    await Task.Delay(50);
                    keybd_event(VK_T, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                    System.Diagnostics.Debug.WriteLine($"[UNREAL] Filet EVO : touche 'T' envoyée (essai {attempt + 1}).");
                }
                catch { /* fenêtre peut avoir disparu entre-temps — pas grave */ }

                await Task.Delay(1500);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UNREAL] Filet EVO : échec envoi touche 'T' — {ex.Message}");
        }
    }

    /// <summary>
    /// Génère TOUJOURS un INI temporaire, à partir du même template complet pour TOUTES les
    /// machines : Assets\UnrealSpeccy_TSL.ini (fourni par l'utilisateur — un vrai unreal.ini
    /// fonctionnel, avec toutes les sections sound/keys/joystick/ROM/ZC/HDD/BETA128/etc.).
    ///
    /// Avant ce fix, seule la machine TSL utilisait ce template ; les machines standard
    /// (Pentagon/Scorpion/KAY/Profi/ATM...) partaient de l'unreal.ini du dossier de
    /// l'émulateur — absent pour certains packages portables (constaté : "wrong ini-file
    /// version", puis un ini quasi vide une fois cette ligne corrigée), ce qui faisait
    /// retomber sur un squelette minimal ([MISC]/HIMEM= seuls, sans son/clavier/joystick).
    ///
    /// HIMEM= est ensuite réécrit selon la machine choisie ; les entrées propres à TSL
    /// (AUTOLOAD diskX, HDD ImageX, BETA128 BOOT, ZC SDCARD — toutes pointant vers boot.$b/
    /// wc.img, fichiers du pack TSL absents pour les autres machines) sont neutralisées pour
    /// toute machine autre que TSL. Les entrées [ROM] (PENTAGON=, SCORP=, KAY=, TSL=, ...)
    /// n'ont pas besoin d'être neutralisées : elles sont déjà nommées par machine, seule
    /// celle correspondant au HIMEM= choisi est utilisée par l'émulateur.
    /// </summary>
    private static string? PrepareIni(Emulator emulator, Dictionary<string, string?> settings)
    {
        if (!File.Exists(emulator.ExecutablePath)) return null;

        var exeDir  = Path.GetDirectoryName(emulator.ExecutablePath)!;
        var machine = (settings.GetValueOrDefault(UnrealSpeccySettings.Machine) ?? "PENTAGON")
                      .ToUpperInvariant();
        var isTsl   = machine == "TSL";

        var tslTemplate = Path.Combine(AppContext.BaseDirectory, "Assets", "UnrealSpeccy_TSL.ini");
        string baseContent;
        if (File.Exists(tslTemplate))
        {
            baseContent = File.ReadAllText(tslTemplate, System.Text.Encoding.ASCII);
        }
        else
        {
            // Template manquant (build incomplet) — repli sur unreal.ini du dossier de
            // l'émulateur s'il existe, sinon squelette minimal (le son/clavier/joystick ne
            // seront alors pas configurés, mais on ne plante pas le lancement).
            System.Diagnostics.Debug.WriteLine(
                $"[UNREAL] Template ini introuvable ({tslTemplate}) — repli sur unreal.ini de l'émulateur ou squelette minimal");
            var srcIniFallback = Path.Combine(exeDir, "unreal.ini");
            baseContent = File.Exists(srcIniFallback)
                ? File.ReadAllText(srcIniFallback, System.Text.Encoding.ASCII)
                : "[MISC]\r\nHIMEM=PENTAGON\r\n";
        }

        // 0. Version — depuis la 0.37.x, UnrealSpeccy refuse au démarrage tout ini qui n'a
        // pas la marque "UNREAL=x.y.z" correspondant exactement au binaire (message observé :
        // "error: wrong ini-file version"). Le contenu de repli minimal utilisé plus haut
        // ("[MISC]\r\nHIMEM=...") n'a jamais eu cette ligne, ce qui provoquait un rejet
        // systématique dès que unreal.ini était introuvable dans le dossier de l'émulateur
        // (ex. archive portable livrée sans config par défaut) — et rien ne garantit non plus
        // que le unreal.ini éventuellement présent porte la bonne version. On force donc
        // toujours cette ligne à la version attendue par le binaire (0.37.9, celle utilisée
        // par le catalogue de téléchargement DemoBase — cf. bannière "UnrealSpeccy 0.37.9"
        // affichée par le process au lancement).
        const string requiredIniVersion = "0.37.9";
        var versionPattern = @"(?im)^UNREAL\s*=.*$";
        baseContent = System.Text.RegularExpressions.Regex.IsMatch(baseContent, versionPattern)
            ? System.Text.RegularExpressions.Regex.Replace(baseContent, versionPattern, $"UNREAL={requiredIniVersion}")
            : $"UNREAL={requiredIniVersion}   ; make sure you don't have old INI version\r\n\r\n" + baseContent;

        // 1. Remplacer HIMEM=
        string newContent;
        if (baseContent.Contains("HIMEM=", StringComparison.OrdinalIgnoreCase))
        {
            newContent = System.Text.RegularExpressions.Regex.Replace(
                baseContent, @"(?im)^HIMEM=.*$", $"HIMEM={machine}");
        }
        else
        {
            var miscIdx = baseContent.IndexOf("[MISC]", StringComparison.OrdinalIgnoreCase);
            if (miscIdx >= 0)
            {
                var eol = baseContent.IndexOf('\n', miscIdx);
                newContent = eol >= 0
                    ? baseContent.Insert(eol + 1, $"HIMEM={machine}\r\n")
                    : baseContent + $"\r\nHIMEM={machine}\r\n";
            }
            else
                newContent = baseContent + $"\r\n[MISC]\r\nHIMEM={machine}\r\n";
        }

        // 1a. RESET= (démarrage : BASIC/DOS/MENU/SYS) — pour les machines ATM (ATM450/ATM710/
        // ATM3), le réglage par défaut du template (RESET=SYS) fait booter sur le menu
        // système/DOS propre à ATM plutôt que directement en 48K BASIC, ce qui empêche
        // l'auto-chargement de la cassette (TapeAutoStart=1) de s'enclencher tout seul —
        // l'utilisateur doit alors naviguer manuellement dans ce menu ("Load tape") avant que
        // la démo démarre. Pentagon/Scorpion/KAY/Profi bootent nativement en 48K BASIC (menu
        // 128K "Loader" compatible avec l'auto-chargement) et n'ont pas ce problème. On force
        // donc RESET=BASIC uniquement pour la famille ATM, ce qui les fait booter dans le même
        // état "48K BASIC" que les autres machines, où l'auto-chargement fonctionne déjà.
        // Non testé en conditions réelles (pas d'environnement Windows/UnrealSpeccy ici) — à
        // vérifier après build.
        if (machine is "ATM450" or "ATM710" or "ATM3")
            newContent = SetIniValue(newContent, "RESET", "BASIC", "[MISC]");

        // 1b. Plein écran ([VIDEO] FullScr) — piloté exclusivement via l'ini, cf. commentaire
        // sur UnrealSpeccySettings.FullScreen (le switch CLI -f ne fonctionnait pas fiablement).
        var fullScreen = settings.GetValueOrDefault(UnrealSpeccySettings.FullScreen) == "true";
        newContent = SetIniValue(newContent, "FullScr", fullScreen ? "1" : "0", "[VIDEO]");

        // 1c. Filtre vidéo ([VIDEO] video=) — "triple" par défaut (x3, net, recommandé pour
        // les démos scène), configurable par profil parmi les valeurs documentées dans le
        // commentaire d'unreal.ini (normal/double/triple/quad/text/resampler/bilinear/scale/
        // advmame/tv/ch_ov/ch_hw/ch_bl/ch_b/ch4true).
        var videoFilter = settings.GetValueOrDefault(UnrealSpeccySettings.VideoFilter);
        if (string.IsNullOrWhiteSpace(videoFilter)) videoFilter = "triple";
        newContent = SetIniValue(newContent, "video", videoFilter, "[VIDEO]");

        // 2. Neutraliser les entrées problématiques (SAUF en TSL, où diskA=boot.$b et
        //    Image0=wc.img sont volontairement conservés — fichiers réels du pack TSL) :

        if (!isTsl)
        {
            // [AUTOLOAD] : vider diskA/diskB/diskC/diskD (pointent vers des
            // fichiers locaux inexistants, causent "failed to autoload")
            newContent = System.Text.RegularExpressions.Regex.Replace(
                newContent,
                @"(?im)^(disk[A-Da-d]\s*=).*$",
                "$1");  // Garder la clé, vider la valeur

            // [BETA128] BOOT= et [ZC] SDCARD= : pointent vers boot.$b / wc.img, fichiers du
            // pack TSL (UnrealSpeccyTslPackService) absents pour les autres machines —
            // vidées de la même façon pour éviter un autoload/montage raté au démarrage.
            newContent = System.Text.RegularExpressions.Regex.Replace(
                newContent,
                @"(?im)^(BOOT\s*=).*$",
                "$1");
            newContent = System.Text.RegularExpressions.Regex.Replace(
                newContent,
                @"(?im)^(SDCARD\s*=).*$",
                "$1");
        }

        // [AUTOLOAD] : commenter snapshot= si non vide (sans effet en TSL, le template
        // fourni n'en déclare pas)
        newContent = System.Text.RegularExpressions.Regex.Replace(
            newContent,
            @"(?im)^(snapshot\s*=\s*\S.*)$",
            ";[DemoBase] $1");

        // [HDD] : SkipReal=1 pour éviter l'accès aux disques physiques
        // (hd0-hd7 access failed, HDD/CD emulator can't access physical drives)
        newContent = System.Text.RegularExpressions.Regex.Replace(
            newContent,
            @"(?im)^SkipReal\s*=.*$",
            "SkipReal=1");

        if (!isTsl)
        {
            // [HDD] : vider Image0/Image1/Image2 (CPM.HDD, CD-ROM physique)
            newContent = System.Text.RegularExpressions.Regex.Replace(
                newContent,
                @"(?im)^(Image\d+\s*=).*$",
                "$1");
        }

        var tmpIni = Path.Combine(WorkingPaths.GetSubdir("Configs"),
                                  $"unreal_{machine.ToLowerInvariant()}.ini");
        File.WriteAllText(tmpIni, newContent, System.Text.Encoding.ASCII);
        System.Diagnostics.Debug.WriteLine(
            $"[UNREAL] INI temporaire → '{tmpIni}' (HIMEM={machine}, TSL={isTsl}, " +
            $"FullScr={(fullScreen ? 1 : 0)}, video={videoFilter})");
        return tmpIni;
    }

    /// <summary>
    /// Remplace la valeur d'une clé "clé=valeur" existante n'importe où dans le contenu (la
    /// clé étant supposée unique dans unreal.ini — vrai pour FullScr/video/HIMEM), ou l'insère
    /// juste après l'en-tête de <paramref name="section"/> si la clé est absente (section
    /// elle-même créée en fin de fichier si absente aussi — cas du contenu minimal de repli
    /// "[MISC]\r\nHIMEM=...\r\n" utilisé quand aucun unreal.ini/template n'est trouvé).
    /// </summary>
    private static string SetIniValue(string content, string key, string value, string section)
    {
        var keyPattern = $@"(?im)^{System.Text.RegularExpressions.Regex.Escape(key)}\s*=.*$";
        if (System.Text.RegularExpressions.Regex.IsMatch(content, keyPattern))
            return System.Text.RegularExpressions.Regex.Replace(content, keyPattern, $"{key}={value}");

        var sectionIdx = content.IndexOf(section, StringComparison.OrdinalIgnoreCase);
        if (sectionIdx >= 0)
        {
            var eol = content.IndexOf('\n', sectionIdx);
            return eol >= 0
                ? content.Insert(eol + 1, $"{key}={value}\r\n")
                : content + $"\r\n{key}={value}\r\n";
        }

        return content + $"\r\n{section}\r\n{key}={value}\r\n";
    }

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings,
        string file, string? iniPath)
    {
        var sb = new StringBuilder();

        // -i <inifile> : INI temporaire avec machine spécifique
        if (iniPath != null)
            sb.Append($"-i \"{iniPath}\" ");

        // Plein écran désormais piloté via [VIDEO] FullScr= dans l'ini (cf. PrepareIni) — le
        // switch -f a été retiré, il ne mettait pas fiablement l'émulateur en plein écran.

        sb.Append($"\"{file}\"");

        // Paramètres additionnels du profil
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
        {
            var extra = EmulatorLaunchService.SubstituteVars(config.CommandLine, file);
            sb.Append(' ').Append(extra);
        }

        return sb.ToString().TrimEnd();
    }

    private static Task<string> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("unreal", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // Priorité : disques TR-DOS > snapshots > cassettes > autres
        string? best = null;
        int bestScore = -1;
        foreach (var f in files)
        {
            var e = Path.GetExtension(f).ToLowerInvariant();
            if (IgnoredExtensions.Contains(e)) continue;
            int score = e switch
            {
                ".trd" or ".scl"                   => 4, // disque TR-DOS (démos scène)
                ".fdi" or ".td0" or ".udi"          => 3, // image disquette
                ".sna" or ".z80" or ".szx"          => 2, // snapshot
                ".tap" or ".tzx" or ".csw"          => 1, // cassette
                _                                   => 0,
            };
            if (score > bestScore) { bestScore = score; best = f; }
        }

        return best
            ?? files.FirstOrDefault(f =>
                   !IgnoredExtensions.Contains(
                       Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
