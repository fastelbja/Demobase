using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings DOSBox-X ───────────────────────────────────────────────

public static class DosBoxXSettings
{
    public const string Machine    = "machine";     // cga/ega/tandy/pcjr/hercules/vgaonly/svga_s3
    public const string CpuType    = "cputype";      // auto/8086/286/386/486/pentium
    public const string Cycles     = "cycles";        // texte libre : "auto"/"max"/"fixed 4000"...
    public const string MemSize    = "memsize";       // Mo : 1/4/16/32/64
    public const string SbType     = "sbtype";        // none/sb1/sbpro2/sb16
    public const string Gus        = "gus";           // "true" / "false"
    public const string FullScreen = "fullscreen";    // "true" / "false"
}

// ─── Lanceur DOSBox-X (https://github.com/joncampbell123/dosbox-x) ──────────
// Architecture différente de WinUAE malgré la ressemblance (fichier de config
// .conf, lui aussi au format INI) : DOSBox-X tolère très bien un fichier de
// config PARTIEL ou MÊME ABSENT — tout réglage omis retombe simplement sur le
// défaut intégré de DOSBox-X (confirmé via dosbox-x.reference.conf : chaque
// clé y a une valeur par défaut documentée et sensée). Donc, contrairement à
// WinUAE, pas besoin de fusionner les overrides DANS un fichier de base ni de
// générer un fichier complet from scratch : le flag `-set "section clé=valeur"`
// permet de surcharger des réglages précis EN LIGNE DE COMMANDE, par-dessus le
// fichier de base chargé via `-conf` si le profil en a un configuré (champ
// générique "Fichier de config" déjà présent sur tous les profils), ou
// par-dessus les défauts intégrés de DOSBox-X sinon. Documentation utilisée :
// dosbox-x.reference.conf (sections [dosbox]/[cpu]/[sblaster]/[gus]/[sdl]) et
// la page wiki "DOSBox-X's Command-Line Options"
// (https://dosbox-x.com/wiki/DOSBox%E2%80%90X%E2%80%99s-Command%E2%80%90Line-Options).
//
// Lancement du fichier : MOUNT explicite sur la racine COMPLÈTE du ZIP extrait
// (pas seulement le dossier contenant l'exe trouvé), puis CD + lancement par
// chemin relatif. Une demo DOS de la scène a souvent plus d'un fichier
// (exécutable + données : .mod/.s3m, packs graphiques, overlays...), parfois
// rangés dans des sous-dossiers différents de celui de l'exécutable principal
// (ex. EXE/ et DATA/ séparés) — monter seulement le dossier de l'exe (ce que
// fait l'argument positionnel "dosbox-x fichier.exe" tout seul, cf. doc :
// "DOSBox-X will mount the directory of name as the C: drive") risquerait de
// laisser des fichiers de données hors de portée si l'arborescence du ZIP les
// sépare. Monter la racine entière de l'extraction couvre les deux cas (plat
// ou avec sous-dossiers) sans rien perdre.
//
// MOUNT se fait sur le chemin absolu, AVEC deux-points sur la lettre de
// lecteur (`MOUNT C: "<chemin>"`). Note de session : un échec persistant de
// MOUNT (même message "Bad command or filename" sur plusieurs syntaxes) s'est
// révélé être une régression dans le binaire DOSBox-X 2026.06.02 utilisé par
// l'utilisateur à l'époque, pas un problème de syntaxe ni de DemoBase — cf.
// RESUME_PROJET.md pour le détail complet du débogage. Résolu en revenant à
// une build antérieure (2022.12.26) fonctionnelle.
//
// EXIT ajouté en toute fin de ligne de commande (après le lancement de la
// demo, et après l'éventuelle ligne de commande additionnelle du profil) :
// ferme DOSBox-X automatiquement une fois la demo terminée et le contrôle
// rendu au prompt DOS, plutôt que de laisser la fenêtre ouverte sur un prompt
// inactif en attendant que l'utilisateur la ferme lui-même.
//
// Pas de gestion d'images disque (.img/.iso, IMGMOUNT) pour cette v1 : la grande
// majorité des releases DOS de la scène demo sont un dossier de fichiers
// (.exe/.com/.bat + données), pas une image disque — à reconsidérer si le
// besoin se présente concrètement.

public class DosBoxXLauncher
{
    private readonly PreferencesService _prefs;

    // Pas de hiérarchie disque/programme comme pour les autres systèmes : un
    // dossier de release DOS est généralement un mélange plat d'exécutables et
    // de fichiers de données, donc une seule priorité (le premier exécutable
    // trouvé, par ordre alphabétique), .bat inclus (beaucoup de demos DOS
    // utilisent un script de lancement plutôt qu'un .exe direct).
    private static readonly string[] MainExtensions = [".exe", ".com", ".bat"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".md"];

    // Runtimes/librairies DOS courants qui ne sont jamais le point d'entrée d'une démo.
    // Exclus de la sélection automatique (mais utilisables si c'est le seul candidat
    // ou si l'utilisateur les choisit manuellement via le sélecteur).
    private static readonly HashSet<string> ExcludedExeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dos4gw.exe", "dos32a.exe", "dos32x.exe", "pmodew.exe", "cwsdpmi.exe", "hdpmi32.exe",
        "dpmi16bi.ovl", "dpmi32vm.ovl", "cwsdpr0.exe", "go32.exe", "emu387.exe",
        "uninst.exe", "install.exe", "setup.exe", "readme.exe", "register.exe",
    };

    public DosBoxXLauncher(PreferencesService prefs)
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
            $"[DOSBOX-X] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        // 1. Vérifier l'exe
        if (!File.Exists(emulator.ExecutablePath))
            return new(false, $"DOSBox-X introuvable : {emulator.ExecutablePath}");

        // 2. Vérifier le fichier
        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[DOSBOX-X] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        // 3. Fichier de config de base : optionnel (contrairement à WinUAE où il est
        //    quasi indispensable). S'il est configuré mais introuvable, on bloque —
        //    plus clair qu'un DOSBox-X qui démarrerait silencieusement avec ses
        //    réglages par défaut à la place de ceux attendus par l'utilisateur.
        var baseConf = config.ConfigFilePath;
        if (!string.IsNullOrWhiteSpace(baseConf) && !File.Exists(baseConf))
        {
            System.Diagnostics.Debug.WriteLine($"[DOSBOX-X] Fichier de config configuré introuvable : {baseConf}");
            return new(false, $"Fichier de config configuré introuvable : {baseConf}");
        }

        // 4. Déterminer la racine à monter en C: et le fichier principal à
        //    l'intérieur — toute l'arborescence extraite du ZIP si c'en est un
        //    (couvre exe + données dans des sous-dossiers séparés), sinon
        //    simplement le dossier du fichier déjà résolu.
        string mountRoot;
        string? mainFileAbsolute;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            (mountRoot, mainFileAbsolute) = await PickMainFileAsync(romPath, configDir, release.Id, config.Id);
            if (mainFileAbsolute == null)
            {
                System.Diagnostics.Debug.WriteLine("[DOSBOX-X] Sélection annulée par l'utilisateur.");
                return new(false, "Lancement annulé.");
            }
            System.Diagnostics.Debug.WriteLine(
                $"[DOSBOX-X] ZIP extrait → racine à monter : {mountRoot} ; fichier choisi : {mainFileAbsolute}");
        }
        else
        {
            mountRoot        = Path.GetDirectoryName(romPath)!;
            mainFileAbsolute = romPath;
        }

        // Chemin du fichier à lancer, RELATIF à la racine montée — c'est ce chemin
        // (et non le chemin absolu de la machine hôte) que DOSBox-X reçoit côté
        // invité, une fois positionné sur C:. Converti en antislash, convention DOS.
        var relativeMain = Path.GetRelativePath(mountRoot, mainFileAbsolute!).Replace('/', '\\');

        // 5. Construire la ligne de commande
        var args = BuildArguments(config, settings, baseConf, mountRoot, relativeMain, mainFileAbsolute);

        // Log de diagnostic pour la boîte de dialogue de confirmation à la fermeture. Retour
        // utilisateur du 2026-07-24 : malgré -set "sdl quit warning=false", DOSBox-X logge lui-
        // même "Cannot set "quit warning=false"" au démarrage — sur la build utilisée
        // (2026.06.02, MinGW Low-end SDL1 32-bit), ce réglage refuse la surcharge en ligne de
        // commande (raison exacte non identifiée : pas de source à disposition pour cette build
        // précise). Le flag est laissé en place (inoffensif, peut fonctionner sur d'autres
        // builds/plateformes) mais on ne peut plus compter dessus pour supprimer la boîte de
        // dialogue — cf. RESUME_PROJET.md pour le détail de l'investigation.
        System.Diagnostics.Debug.WriteLine($"[DOSBOX-X] Ligne de commande complète : {args}");

        // Popup d'info "comment quitter" à la place : DOSBox-X refusant la surcharge
        // "quit warning=false" sur la build de l'utilisateur, on ne peut pas supprimer la
        // boîte de dialogue de DOSBox-X elle-même — au lieu de ça, on informe une seule fois
        // (checkbox "ne plus afficher") que Ctrl+F9 quitte sans confirmation, et que sinon
        // DOSBox-X se ferme tout seul via le -c "EXIT" ajouté en fin de ligne de commande dès
        // que la demo se termine d'elle-même. Même principe que WinUAE/Hatari/blueMSX/TR-DOS.
        await MaybeShowQuitInfoAsync();

        // 6. Lancer DOSBox-X
        return await ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "DOSBOXX", friendlyName: "DosBoxX");
    }

    // ── Popup d'info sur le raccourci pour quitter ──────────────────────────────

    private const string QuitInfoDialogPrefKey = "dosboxx.quit_info_dialog.hidden";

    /// <summary>
    /// Affiche la popup expliquant Ctrl+F9 pour quitter DOSBox-X sans confirmation, sauf si
    /// l'utilisateur a déjà coché "Ne plus afficher ce message" lors d'un lancement précédent.
    /// Même principe que WinUAELauncher.MaybeShowQuitInfoAsync / Hatari / blueMSX / TR-DOS.
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
                var dlg = new DemoBase.App.Views.DosBoxXInfoDialog
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
            System.Diagnostics.Debug.WriteLine($"[DOSBOX-X] Popup info quitter : échec — {ex.Message}");
        }
    }

    // ─── Construction de la ligne de commande ─────────────────────────────────

    private static string BuildArguments(
        EmulatorConfig config, Dictionary<string, string?> settings, string? baseConf,
        string mountRoot, string relativeMain, string mainFileAbsolute)
    {
        var sb = new StringBuilder();

        // Fichier de config de base, s'il y en a un — toutes les clés non
        // explicitement surchargées ci-dessous (input, mapper, sections non
        // listées...) restent celles de ce fichier.
        if (!string.IsNullOrWhiteSpace(baseConf))
            sb.Append($"-conf \"{baseConf}\" ");

        // -noautoexec : TOUJOURS ajouté, que baseConf soit renseigné ou non.
        // Hypothèse de cause de l'échec systématique de MOUNT (cf. commentaire
        // détaillé plus bas) : la section [autoexec] du fichier chargé — celui
        // du profil OU un dosbox-x.conf par défaut chargé par DOSBox-X lui-même
        // sans qu'on le lui demande — démarre un DOS réel via BOOT ("version
        // avec le dos intégré"). Or nos commandes -c (MOUNT/CD/lancement)
        // s'exécutent EN PLUS de [autoexec], mais APRÈS elle (confirmé par la
        // doc DOSBox-X : "-c ... in addition to the [autoexec] section"). Si
        // [autoexec] a déjà basculé sur un DOS réel avant que nos -c ne
        // s'exécutent, MOUNT n'existe alors plus du tout (limitation documentée
        // du DOS réel dans DOSBox-X, indépendante de toute syntaxe) — ce qui
        // expliquerait l'échec IDENTIQUE des 3 syntaxes MOUNT testées en
        // session. -noautoexec saute cette section dans tous les cas, donc on
        // est garanti de rester sur le DOS simulé de DOSBox-X (celui où MOUNT
        // existe) jusqu'à nos propres commandes ci-dessous. Seul effet de bord
        // possible : perte d'un éventuel contenu utile de cet [autoexec]
        // (driver souris, etc.) — DemoBase couvre déjà machine/cpu/cycles/
        // mémoire/son via -set, donc impact attendu nul ou faible. À confirmer
        // par le prochain test utilisateur.
        sb.Append("-noautoexec ");

        var machine = settings.GetValueOrDefault(DosBoxXSettings.Machine);
        sb.Append($"-set \"dosbox machine={(string.IsNullOrWhiteSpace(machine) ? "svga_s3" : machine)}\"");

        var cpuType = settings.GetValueOrDefault(DosBoxXSettings.CpuType);
        sb.Append($" -set \"cpu cputype={(string.IsNullOrWhiteSpace(cpuType) ? "auto" : cpuType)}\"");

        var cycles = settings.GetValueOrDefault(DosBoxXSettings.Cycles);
        sb.Append($" -set \"cpu cycles={(string.IsNullOrWhiteSpace(cycles) ? "auto" : cycles)}\"");

        var memSize = settings.GetValueOrDefault(DosBoxXSettings.MemSize);
        sb.Append($" -set \"dosbox memsize={(string.IsNullOrWhiteSpace(memSize) ? "16" : memSize)}\"");

        var sbType = settings.GetValueOrDefault(DosBoxXSettings.SbType);
        sb.Append($" -set \"sblaster sbtype={(string.IsNullOrWhiteSpace(sbType) ? "sb16" : sbType)}\"");

        sb.Append(settings.GetValueOrDefault(DosBoxXSettings.Gus) == "true"
            ? " -set \"gus gus=true\""
            : " -set \"gus gus=false\"");

        if (settings.GetValueOrDefault(DosBoxXSettings.FullScreen) == "true")
            sb.Append(" -fullscreen");

        // Saute l'écran BIOS et la bannière de bienvenue : sans intérêt dans un
        // contexte de lancement automatisé depuis DemoBase.
        sb.Append(" -fastlaunch");

        // Désactive la boîte de dialogue "Voulez-vous vraiment quitter ?" (que ce soit en
        // fermant la fenêtre à la main ou via la commande EXIT ajoutée en fin de ligne de
        // commande ci-dessous) — demande utilisateur. Réglage [sdl] quit warning, par défaut
        // "auto" (prévient si un programme DOS est en cours). Toujours ajouté, comme les
        // autres -set ci-dessus : aucun fichier dosbox-x.conf n'est généré/modifié par
        // DemoBase pour DOSBox-X (contrairement à WinUAE/BIOS), donc l'override doit passer
        // par la ligne de commande pour s'appliquer systématiquement, avec ou sans -conf.
        sb.Append(" -set \"sdl quit warning=false\"");

        // ── Montage + lancement ─────────────────────────────────────────────────
        // MOUNT sur le chemin absolu, AVEC deux-points sur la lettre de lecteur
        // (`MOUNT C: "<chemin>"`). Note de session : un échec persistant de MOUNT
        // observé par l'utilisateur (testé avec plusieurs syntaxes, dont celle-ci)
        // s'est avéré être une régression dans le binaire DOSBox-X 2026.06.02
        // utilisé à l'époque (MOUNT absent même au prompt natif Z:\>, confirmé par
        // capture d'écran), pas un problème de syntaxe ni de DemoBase — cf.
        // RESUME_PROJET.md pour le détail. Résolu côté utilisateur en revenant à
        // une build DOSBox-X antérieure (2022.12.26) fonctionnelle.
        sb.Append($" -c \"MOUNT C: \\\"{mountRoot}\\\"\"");
        sb.Append(" -c \"C:\"");
        sb.Append($" -c \"{relativeMain}\"");

        // ── Ligne de commande additionnelle définie sur le profil (optionnelle) ─
        if (!string.IsNullOrWhiteSpace(config.CommandLine) && config.CommandLine.Trim() != "{file}")
        {
            var extra = EmulatorLaunchService.SubstituteVars(config.CommandLine, mainFileAbsolute);
            sb.Append(' ').Append(extra);
        }

        // EXIT toujours ajouté EN DERNIER : une fois la demo terminée (sortie via
        // sa propre touche de sortie, typiquement ESC — convention quasi
        // universelle dans la scène demo — ou simplement parce qu'elle se termine
        // d'elle-même) et le contrôle rendu au prompt DOS, cette commande ferme
        // DOSBox-X automatiquement au lieu de laisser la fenêtre ouverte sur un
        // prompt inactif. Placée après la ligne de commande additionnelle du
        // profil (et non juste après le lancement de la demo) pour lui laisser
        // une chance de s'exécuter avant la fermeture, si le profil en définit une.
        sb.Append(" -c \"EXIT\"");

        return sb.ToString();
    }

    // ─── Extraction ZIP ────────────────────────────────────────────────────────
    // Retourne la racine d'extraction ET le fichier principal repéré à l'intérieur
    // (n'importe où dans l'arborescence, pas seulement à la racine).

    /// <summary>
    /// Extrait le ZIP, choisit le fichier principal en excluant les runtimes connus,
    /// mémorise et réutilise le choix si plusieurs candidats existent (même logique que WinUAE).
    /// </summary>
    private async Task<(string root, string? mainFile)> PickMainFileAsync(
        string zipPath, string outDir, int releaseId, int configId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("dos", releaseId, zipPath));
        var files = await Task.Run(() => WorkingPaths.ExtractZipCached(zipPath, extractDir));

        // Candidats : exécutables hors runtimes connus
        var candidates = files
            .Where(f => MainExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
                     && !ExcludedExeNames.Contains(Path.GetFileName(f)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Fallback : si tout est runtime, au moins quelque chose
        if (candidates.Count == 0)
            candidates = files
                .Where(f => MainExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (candidates.Count == 0)
        {
            var any = files.FirstOrDefault(f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                   ?? files.FirstOrDefault()
                   ?? zipPath;
            return (extractDir, any);
        }

        // Un seul candidat — pas besoin de demander
        if (candidates.Count == 1)
            return (extractDir, candidates[0]);

        // Plusieurs candidats — vérifier si un choix a déjà été mémorisé
        var prefKey = $"dosboxx_startup:{configId}:{releaseId}";
        var saved   = await _prefs.GetAsync(prefKey);
        if (!string.IsNullOrEmpty(saved))
        {
            var savedMatch = candidates.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), saved, StringComparison.OrdinalIgnoreCase));
            if (savedMatch != null)
            {
                System.Diagnostics.Debug.WriteLine($"[DOSBOX-X] Fichier mémorisé réutilisé : {saved}");
                return (extractDir, savedMatch);
            }
        }

        // Afficher le sélecteur sur le thread UI
        string? chosen = null;
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new DemoBase.App.Views.StartupFilePickerDialog(
                candidates.Select(Path.GetFileName).Where(n => n != null).Select(n => n!));
            if (dlg.ShowDialog() == true)
                chosen = dlg.SelectedFile;
        });

        if (chosen == null)
            return (extractDir, null); // annulé

        var picked = candidates.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f), chosen, StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];

        await _prefs.SetAsync(prefKey, Path.GetFileName(picked));
        System.Diagnostics.Debug.WriteLine($"[DOSBOX-X] Fichier choisi et mémorisé : {Path.GetFileName(picked)}");
        return (extractDir, picked);
    }
}
