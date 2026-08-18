using DemoBase.Core.Enums;
using DemoBase.Core.Interfaces;
using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace DemoBase.App.Services;

// ─── Launch Result ────────────────────────────────────────────────────────────

public record LaunchResult(bool Success, string? Error = null);

// ─── Emulator Launch Service ──────────────────────────────────────────────────

public class EmulatorLaunchService
{
    private readonly IUnitOfWork _uow;
    private readonly string      _filesRoot;

    public EmulatorLaunchService(IUnitOfWork uow)
    {
        _uow       = uow;
        // 2026-07-25 : "NotCurated" (racine du dossier de l'exe), PAS WorkingPaths.GetSubdir
        // ("files") — ce dernier vit sous Working\, intégralement vidé à chaque démarrage de
        // l'app (DbInitializer.CleanExtractedCache), ce qui aurait fait retélécharger le ZIP à
        // chaque relance. Voir WorkingPaths.NotCuratedRoot pour le détail.
        _filesRoot = WorkingPaths.NotCuratedRoot;
    }

    // ─── Lancement principal ──────────────────────────────────────────────────

    public async Task<LaunchResult> LaunchAsync(
        Release release, EmulatorConfig config, ReleaseLink? preferredLink = null)
    {
        try
        {
            // 1. Trouver ou télécharger le fichier
            var filePath = await ResolveFileAsync(release, config, preferredLink);
            if (filePath == null)
                return new(false, "Aucun fichier disponible pour cette release.");

            // 2. Construire la ligne de commande
            var exe  = config.Emulator.ExecutablePath;
            if (!File.Exists(exe))
                return new(false, $"Émulateur introuvable : {exe}");

            var args    = SubstituteVars(config.CommandLine, filePath);
            var workDir = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                ? Path.GetDirectoryName(exe)!
                : SubstituteVars(config.WorkingDirectory, filePath);

            // 3. Script pré-lancement optionnel
            if (!string.IsNullOrWhiteSpace(config.PreLaunchScript))
                await RunScriptAsync(config.PreLaunchScript, filePath);

            // 4. Lancer l'émulateur
            var psi = new ProcessStartInfo
            {
                FileName         = exe,
                Arguments        = args,
                WorkingDirectory = Directory.Exists(workDir) ? workDir : Path.GetDirectoryName(exe)!,
                UseShellExecute  = false,
            };
            Process.Start(psi);
            return new(true);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    // ─── Résolution du fichier ────────────────────────────────────────────────

    public async Task<string?> ResolveFileAsync(
        Release release, EmulatorConfig? config, ReleaseLink? preferredLink = null,
        EmulatorType? emulatorType = null,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null,
        bool returnRawArchive = false)
    {
        // 2026-07-26 : "config" n'est en réalité utilisé nulle part dans cette méthode
        // (jamais lu ci-dessous) — rendu nullable pour permettre l'appel depuis
        // EmulatorService.ResolveAdHocFileAsync (Music/Graphics, pas de profil
        // émulateur). Ne rien y ajouter qui suppose "config" non-null sans vérifier.
        // "returnRawArchive" : voir DownloadAndExtractAsync — même besoin pour ce
        // même appelant (Music/Graphics scannent eux-mêmes le contenu du zip).
        // 1. Fichier local déjà présent
        var localLink = preferredLink
            ?? release.Links.FirstOrDefault(l => !string.IsNullOrEmpty(l.LocalFilePath)
                                              && File.Exists(l.LocalFilePath))
            ?? release.Links.FirstOrDefault(l => l.IsMainFile
                                              && !string.IsNullOrEmpty(l.LocalFilePath)
                                              && File.Exists(l.LocalFilePath));
        if (localLink?.LocalFilePath != null && File.Exists(localLink.LocalFilePath))
            return localLink.LocalFilePath;

        // 2. Télécharger depuis l'URL — UNIQUEMENT un lien explicitement marqué comme
        // fichier de téléchargement par Demozoo (IsMainFile, mappé sur "is_download_link"
        // lors de l'import). 2026-07-25 : le repli inconditionnel sur N'IMPORTE QUEL lien
        // (page Pouet, site officiel…) a été retiré — cf. RESUME_PROJET.md, il pouvait
        // faire télécharger une page web et tenter de la lancer comme si c'était le jeu.
        // 2026-07-25 (retour utilisateur : "Return to Promised Land", Demozoo #394835) :
        // EffectiveDownloadUrl plutôt que Url — certaines classes de lien Demozoo (ex.
        // "BaseUrl") ne remplissent que "LinkParameter", jamais "Url" — cf. ReleaseLink.
        // EffectiveDownloadUrl dans DemoBase.Core/Models/Models.cs pour le détail.
        var remoteLink = preferredLink
            ?? release.Links.FirstOrDefault(l => l.IsMainFile && !string.IsNullOrEmpty(l.EffectiveDownloadUrl));
        if (remoteLink?.EffectiveDownloadUrl == null) return null;

        return await DownloadAndExtractAsync(release, remoteLink, emulatorType, progress, returnRawArchive);
    }

    // Extensions jamais valables comme "fichier principal" — documentation/texte
    // d'accompagnement présent dans presque toutes les archives de la scène demo.
    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".diz", ".nfo", ".me", ".doc", ".docx", ".pdf", ".info", ".inf",
    };

    public async Task<string?> DownloadAndExtractAsync(
        Release release, ReleaseLink link, EmulatorType? emulatorType = null,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null,
        bool returnRawArchive = false)
    {
        // 2026-07-31, retour utilisateur : "lors du download d'une release ce lien ne
        // passe pas [...] Le lien tombe sur une page internet avec un bouton download
        // [...] je pense qu'il faut juste remplacer /view/ par /file/ pour le site
        // discmaster.textfiles.com" — confirmé, cf. NormalizeDownloadUrl ci-dessous.
        var downloadUrl = NormalizeDownloadUrl(link.EffectiveDownloadUrl);
        if (downloadUrl == null) return null;

        var releaseDir = Path.Combine(_filesRoot,
            release.Id.ToString(), link.Id.ToString());
        Directory.CreateDirectory(releaseDir);

        var fileName = link.FileName
            ?? Path.GetFileName(new Uri(downloadUrl).LocalPath)
            ?? "file";
        var destPath = Path.Combine(releaseDir, fileName);

        // Télécharger si pas encore présent — 2026-07-25 : progression réelle (octets
        // reçus / Content-Length si l'hébergeur le fournit) via un flux plutôt qu'un
        // GetByteArrayAsync monolithique, pour alimenter l'overlay "Téléchargement en
        // cours…" côté ReleaseDetailViewModel (releases pas encore couvertes par un DAT,
        // cf. RESUME_PROJET.md).
        if (!File.Exists(destPath))
        {
            progress?.Report(new($"Téléchargement de {fileName}…", 0));

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "DemoBase/1.0");
            using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            var tmpPath    = destPath + ".part";
            await using (var netStream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = File.Create(tmpPath))
            {
                var buffer     = new byte[81920];
                long received  = 0;
                int  read;
                while ((read = await netStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    var pct = totalBytes is > 0
                        ? (int)Math.Clamp(received * 100.0 / totalBytes.Value, 0, 99)
                        : 0;
                    progress?.Report(new($"Téléchargement de {fileName}… ({FormatBytes(received)}" +
                        (totalBytes is > 0 ? $" / {FormatBytes(totalBytes.Value)}" : "") + ")", pct));
                }
            }
            File.Move(tmpPath, destPath, overwrite: true);

            // Mettre à jour le chemin local en base
            link.LocalFilePath = destPath;
            await _uow.SaveChangesAsync();
        }

        // Extraire si archive
        var ext = Path.GetExtension(destPath).ToLowerInvariant();
        if (ext is ".zip")
        {
            progress?.Report(new("Extraction de l'archive…", 99));
            // WinUAE/Altirra/Hatari ont leur propre extraction (priorité ADF/ROM/disque,
            // exclusion des fichiers texte) appliquée directement sur un .zip — ne pas
            // deviner le "bon" fichier ici à leur place, sous peine d'incohérence entre
            // le premier lancement (qui passerait par ici) et les suivants (qui
            // renverraient directement le .zip déjà en cache, traité correctement par
            // le launcher). Leur renvoyer le .zip brut dans les deux cas.
            // 2026-07-26 : "returnRawArchive" — même besoin pour Music/Graphics
            // (ResolveAdHocFileAsync) : PlayMusicReleaseAsync/ShowGraphicsAsync savent
            // déjà ouvrir et scanner un .zip elles-mêmes (comme pour un fichier DAT
            // résolu) — deviner "le bon fichier" ici serait redondant et pourrait même
            // se tromper (ex. .zip contenant plusieurs pistes tracker, alors que ce
            // bloc ne renverrait que la première trouvée).
            if (returnRawArchive
                || emulatorType is EmulatorType.WinUAE or EmulatorType.Altirra or EmulatorType.Hatari
                or EmulatorType.Cpcec or EmulatorType.Zxsec or EmulatorType.Csfec or EmulatorType.Msxec
                or EmulatorType.DOSBox or EmulatorType.ViceC64 or EmulatorType.ViceC128
                or EmulatorType.ViceVic20 or EmulatorType.VicePet or EmulatorType.ViceC64Dtv
                or EmulatorType.VicePlus4 or EmulatorType.Windows)
                return destPath;

            var extractDir = Path.Combine(releaseDir, "extracted");
            if (!Directory.Exists(extractDir))
                ZipFile.ExtractToDirectory(destPath, extractDir);

            // Retourner l'exe principal, sinon le premier fichier qui n'est pas un
            // fichier texte/documentation (file_id.diz, readme.txt, *.nfo...), sinon
            // littéralement n'importe quoi en dernier recours.
            var files = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
            return files.FirstOrDefault(f =>
                       Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".com")
                ?? files.FirstOrDefault(f => !JunkExtensions.Contains(Path.GetExtension(f)))
                ?? files.FirstOrDefault()
                ?? extractDir;
        }

        return destPath;
    }

    // 2026-07-31, retour utilisateur : discmaster.textfiles.com/view/... est une page
    // HTML (lecteur audio + bouton "download"), pas le fichier lui-même — le même hôte
    // expose le vrai fichier à l'identique sauf le premier segment de chemin, "/file/"
    // au lieu de "/view/" (confirmé par l'utilisateur avec les deux URLs exactes d'un
    // même fichier UNBORN.S3M). Téléchargeait donc la page HTML telle quelle (déjà
    // .zip-testée en aval, une page HTML n'étant ni un zip ni un fichier tracker
    // reconnu — "aucune musique trouvée" silencieux, pas de crash, mais rien à jouer).
    private static string? NormalizeDownloadUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        if (url.Contains("discmaster.textfiles.com/view/", StringComparison.OrdinalIgnoreCase))
            return url.Replace("/view/", "/file/", StringComparison.OrdinalIgnoreCase);

        return url;
    }

    private static string FormatBytes(long size)
    {
        if (size < 1024)        return $"{size} o";
        if (size < 1024 * 1024) return $"{size / 1024.0:F1} Ko";
        return $"{size / (1024.0 * 1024):F1} Mo";
    }

    // ─── Substitution de variables ────────────────────────────────────────────

    public static string SubstituteVars(string template, string filePath)
    {
        var dir      = Path.GetDirectoryName(filePath) ?? "";
        var filename = Path.GetFileNameWithoutExtension(filePath);
        var ext      = Path.GetExtension(filePath).TrimStart('.');
        return template
            .Replace("{file}",     filePath, StringComparison.OrdinalIgnoreCase)
            .Replace("{dir}",      dir,      StringComparison.OrdinalIgnoreCase)
            .Replace("{filename}", filename, StringComparison.OrdinalIgnoreCase)
            .Replace("{ext}",      ext,      StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test de l'exécutable ─────────────────────────────────────────────────

    public static bool TestExecutable(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    // ─── Script pré-lancement ─────────────────────────────────────────────────

    private static async Task RunScriptAsync(string script, string filePath)
    {
        var expanded = SubstituteVars(script, filePath);
        var tmpBat   = Path.Combine(WorkingPaths.GetSubdir("Scripts"), $"{Guid.NewGuid():N}.bat");
        await File.WriteAllTextAsync(tmpBat, expanded);
        try
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{tmpBat}\"")
            {
                UseShellExecute  = false,
                CreateNoWindow   = true,
            });
            p?.WaitForExit(5000);
        }
        finally { File.Delete(tmpBat); }
    }
}

public class EmulatorService : IEmulatorService
{
    private readonly IUnitOfWork        _uow;
    private readonly PreferencesService _prefs;
    private readonly WinUAELauncher     _winuae;
    private readonly AltirraLauncher    _altirra;
    private readonly HatariLauncher     _hatari;
    private readonly CpcecLauncher      _cpcec;
    private readonly ZxsecLauncher      _zxsec;
    private readonly CsfecLauncher      _csfec;
    private readonly MsxecLauncher      _msxec;
    private readonly DosBoxXLauncher    _dosboxx;
    private readonly ViceC64Launcher    _vice64;
    private readonly ViceC128Launcher   _vice128;
    private readonly ViceVic20Launcher  _vicevic20;
    private readonly VicePetLauncher    _vicepet;
    private readonly ViceC64DtvLauncher _vicedtv;
    private readonly VicePlus4Launcher  _viceplus4;
    private readonly WindowsLauncher    _windows;
    private readonly Tic80Launcher      _tic80;
    private readonly MicroW8Launcher    _microW8;
    private readonly UnrealSpeccyLauncher _unrealSpeccy;
    private readonly EightyOneLauncher    _eightyOne;
    private readonly ZEsarUXLauncher      _zesarux;
    private readonly KegaFusionLauncher   _fusion;
    private readonly BrowserLauncher      _browser;
    private readonly JavaLauncher         _java;
    private readonly FuseLauncher         _fuse;
    private readonly BlastEmLauncher      _blastEm;
    private readonly ArculatorLauncher    _arculator;
    private readonly PPSSPPLauncher       _ppsspp;
    private readonly BlueMSXLauncher      _blueMsx;
    private readonly DuckStationLauncher  _duckStation;
    private readonly Pcsx2Launcher        _pcsx2;
    private readonly Trs80gpLauncher      _trs80gp;
    private readonly OricutronLauncher    _oricutron;
    private readonly DolphinLauncher      _dolphin;
    private readonly SimCoupeLauncher     _simcoupe;
    private readonly FlycastLauncher      _flycast;
    private readonly JzIntvLauncher       _jzintv;
    private readonly DcmotoLauncher       _dcmoto;
    private readonly Xm6TypeGLauncher     _xm6TypeG;
    private readonly BeebEmLauncher       _beebEm;
    private readonly SQLuxLauncher        _sqlux;
    private readonly PuNESLauncher        _puNes;
    private readonly AresLauncher         _ares;
    private readonly RuffleLauncher       _ruffle;
    private readonly MameLauncher         _mame;
    private readonly MesenLauncher        _mesen;
    private readonly MelonDSLauncher      _melonds;
    private readonly AzaharLauncher       _azahar;
    private readonly StellaLauncher       _stella;
    private readonly ProSystemLauncher    _proSystem;
    private readonly XeniaLauncher        _xenia;
    private readonly CxbxReloadedLauncher _cxbx;
    private readonly AppleWinLauncher     _appleWin;
    private readonly GSplusLauncher       _gsPlus;
    private readonly PemsaLauncher         _pemsa;
    private readonly BigPEmuLauncher       _bigPEmu;
    private readonly HandyLauncher         _handy;
    private readonly GeePee32Launcher      _geePee32;
    private readonly Ep128EmuLauncher      _ep128Emu;
    private readonly Mz800EmuLauncher      _mz800Emu;
    private readonly ColEmLauncher         _colEm;
    private readonly EmulatorLaunchService _generic;

    public EmulatorService(IUnitOfWork uow, PreferencesService prefs)
    {
        _uow     = uow;
        _prefs   = prefs;
        _winuae  = new WinUAELauncher(prefs);
        _altirra = new AltirraLauncher(prefs);
        _hatari  = new HatariLauncher(prefs);
        _cpcec   = new CpcecLauncher(prefs);
        _zxsec   = new ZxsecLauncher(prefs);
        _csfec   = new CsfecLauncher(prefs);
        _msxec   = new MsxecLauncher(prefs);
        _dosboxx = new DosBoxXLauncher(prefs);
        _vice64    = new ViceC64Launcher(prefs);
        _vice128   = new ViceC128Launcher(prefs);
        _vicevic20 = new ViceVic20Launcher(prefs);
        _vicepet   = new VicePetLauncher(prefs);
        _vicedtv   = new ViceC64DtvLauncher(prefs);
        _viceplus4 = new VicePlus4Launcher(prefs);
        _windows   = new WindowsLauncher();
        _tic80     = new Tic80Launcher(prefs);
        _microW8      = new MicroW8Launcher(prefs);
        _unrealSpeccy = new UnrealSpeccyLauncher(prefs);
        _eightyOne    = new EightyOneLauncher(prefs);
        _zesarux      = new ZEsarUXLauncher(prefs);
        _fusion       = new KegaFusionLauncher(prefs);
        _browser      = new BrowserLauncher();
        _java         = new JavaLauncher(prefs);
        _fuse         = new FuseLauncher(prefs);
        _blastEm      = new BlastEmLauncher(prefs);
        _arculator    = new ArculatorLauncher(prefs);
        _ppsspp       = new PPSSPPLauncher(prefs);
        _blueMsx      = new BlueMSXLauncher(prefs);
        _duckStation  = new DuckStationLauncher(prefs);
        _pcsx2        = new Pcsx2Launcher();
        _trs80gp      = new Trs80gpLauncher();
        _oricutron    = new OricutronLauncher();
        _dolphin      = new DolphinLauncher();
        _simcoupe     = new SimCoupeLauncher();
        _flycast      = new FlycastLauncher();
        _jzintv       = new JzIntvLauncher();
        _dcmoto       = new DcmotoLauncher();
        _xm6TypeG     = new Xm6TypeGLauncher();
        _beebEm       = new BeebEmLauncher();
        _sqlux        = new SQLuxLauncher();
        _puNes        = new PuNESLauncher(prefs);
        _ares         = new AresLauncher(prefs);
        _ruffle       = new RuffleLauncher();
        _mame         = new MameLauncher(prefs);
        _mesen        = new MesenLauncher(prefs);
        _melonds      = new MelonDSLauncher(prefs);
        _azahar       = new AzaharLauncher(prefs);
        _stella       = new StellaLauncher(prefs);
        _proSystem    = new ProSystemLauncher(prefs);
        _xenia        = new XeniaLauncher(prefs);
        _cxbx         = new CxbxReloadedLauncher(prefs);
        _appleWin     = new AppleWinLauncher(prefs);
        _gsPlus       = new GSplusLauncher(prefs);
        _pemsa        = new PemsaLauncher(prefs);
        _bigPEmu = new BigPEmuLauncher();
        _handy    = new HandyLauncher();
        _geePee32  = new GeePee32Launcher();
        _ep128Emu  = new Ep128EmuLauncher();
        _mz800Emu  = new Mz800EmuLauncher();
        _colEm     = new ColEmLauncher();
        _generic = new EmulatorLaunchService(uow);
    }

    // ─── Test exécutable ──────────────────────────────────────────────────────

    public async Task<bool> TestExecutableAsync(int emulatorId)
    {
        var emu = await _uow.Emulators.GetByIdAsync(emulatorId);
        return emu != null && EmulatorLaunchService.TestExecutable(emu.ExecutablePath);
    }

    // ─── Lancement via lien (téléchargement) ──────────────────────────────────
    //
    // Réécrit le 2026-07-24 : cette méthode ne gérait explicitement (if/else en dur)
    // qu'une poignée d'émulateurs "anciens" (WinUAE, Altirra, Hatari, Cpcec, Zxsec,
    // Csfec, Msxec, DOSBox, Vice*, Windows) ; tout le reste (PCSX2, DuckStation,
    // Flycast, MelonDS, Dolphin, etc. — tous les émulateurs "consoles
    // modernes" ajoutés depuis, qui ont chacun leur propre Launcher gérant mode
    // portable/BIOS/arguments spécifiques) tombait dans un unique "else" générique
    // (_generic.LaunchAsync) qui : (1) n'utilisait PAS le Launcher dédié — pour PCSX2
    // par exemple, ça veut dire pas de -portable, donc le BIOS configuré dans
    // Emus\PCSX2\bios n'était jamais trouvé ; (2) n'exploitait PAS Process.Start
    // via ProcessLaunchHelper (pas de suivi/monitoring du process) ; (3) surtout,
    // n'inspectait JAMAIS le LaunchResult retourné — un échec (émulateur introuvable,
    // téléchargement qui échoue, fichier non résolu...) ne produisait alors RIEN à
    // l'écran, ni message d'erreur ni lancement (bug rapporté : "si j'appuie sur F5
    // il ne se passe rien", sur une release PS2 sans DAT mais avec un lien
    // scene.org IsMainFile).
    //
    // Fix : cette méthode se contente maintenant de résoudre le fichier (téléchargement
    // + extraction si besoin, logique déjà dans EmulatorLaunchService.ResolveFileAsync,
    // inchangée) puis délègue à LaunchReleaseAsync(romPath, release, config) ci-dessous
    // — le même chemin, exhaustif, déjà emprunté par le lancement via DAT local, qui
    // route vers CHAQUE Launcher dédié (dont _pcsx2, qui ajoute -portable) et vérifie
    // toujours result.Success pour afficher une erreur si besoin.

    public async Task LaunchReleaseAsync(ReleaseLink file, EmulatorConfig config,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null)
    {
        var emulator = await _uow.Emulators.GetByIdAsync(config.EmulatorId);
        if (emulator == null)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Émulateur introuvable.", isError: true);
            return;
        }

        var romPath = await _generic.ResolveFileAsync(file.Release, config, file, emulator.EmulatorType, progress);
        if (romPath == null)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Impossible de résoudre le fichier ROM.", isError: true);
            return;
        }

        await LaunchReleaseAsync(romPath, file.Release, config);
    }

    // ─── Résolution ad-hoc SANS lancement (Music/Graphics) ────────────────────
    //
    // 2026-07-26, retour utilisateur : le badge "Fichier externe (pas encore de DAT)"
    // s'affichait sur des releases Music/Graphics, mais le bouton Play ne déclenchait
    // aucun téléchargement — LaunchAsync (ReleaseDetailViewModel) route directement vers
    // PlayMusicReleaseAsync/ShowGraphicsAsync pour ces releases, sans jamais passer par
    // le chemin émulateur générique (LaunchReleaseAsync ci-dessus) où vit tout le
    // système de téléchargement ad-hoc. Cette méthode expose le même mécanisme de
    // résolution/téléchargement (ResolveFileAsync/DownloadAndExtractAsync, inchangés)
    // sans l'étape "config émulateur"/lancement qui ne concerne pas Music/Graphics —
    // "config" est passé à null (jamais utilisé par ResolveFileAsync, cf. plus haut) et
    // "returnRawArchive: true" pour renvoyer le .zip brut, que PlayMusicReleaseAsync/
    // ShowGraphicsAsync savent déjà scanner elles-mêmes (comme un fichier DAT résolu).
    public Task<string?> ResolveAdHocFileAsync(Release release, ReleaseLink link,
        IProgress<DemoBase.Core.DTOs.LaunchDownloadProgress>? progress = null)
        => _generic.ResolveFileAsync(release, null, preferredLink: link,
            emulatorType: null, progress: progress, returnRawArchive: true);

    // ─── Lancement via chemin ROM direct (DATs) ───────────────────────────────

    public async Task LaunchReleaseAsync(string romPath, Release release, EmulatorConfig config)
    {
        var emulator = await _uow.Emulators.GetByIdAsync(config.EmulatorId);
        if (emulator == null)
        {
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Émulateur introuvable.", isError: true);
            return;
        }
        var settings = await _uow.Emulators.GetSettingsAsync(config.Id);

        // Guard : vérifier que l'exe est configuré avant d'appeler le launcher.
        // Exception : Windows/Browser/Java/MicroW8/TIC-80 n'ont pas d'émulateur
        // séparé — l'exe est celui de la release elle-même ou un runtime système.
        var needsExe = emulator.EmulatorType is not (
            EmulatorType.Windows or EmulatorType.Browser or EmulatorType.Java or
            EmulatorType.MicroW8 or EmulatorType.Tic80);
        if (needsExe && (string.IsNullOrWhiteSpace(emulator.ExecutablePath) || !File.Exists(emulator.ExecutablePath)))
        {
            var msg = string.IsNullOrWhiteSpace(emulator.ExecutablePath)
                ? $"Émulateur '{emulator.Name}' non configuré — rendez-vous dans Émulateurs pour configurer le chemin."
                : $"Émulateur introuvable : {emulator.ExecutablePath}";
            DemoBase.App.Controls.StatusScrollerControl.Post(msg, isError: true);
            System.Windows.MessageBox.Show(msg, "Émulateur non configuré",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        LaunchResult result = emulator.EmulatorType switch
        {
            EmulatorType.WinUAE =>
                await _winuae.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Altirra =>
                await _altirra.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Hatari =>
                await _hatari.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Cpcec =>
                await _cpcec.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Zxsec =>
                await _zxsec.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Csfec =>
                await _csfec.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Msxec =>
                await _msxec.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.DOSBox =>
                await _dosboxx.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.ViceC64 =>
                await _vice64.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.ViceC128 =>
                await _vice128.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.ViceVic20 =>
                await _vicevic20.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.VicePet =>
                await _vicepet.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.ViceC64Dtv =>
                await _vicedtv.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.VicePlus4 =>
                await _viceplus4.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Windows =>
                await _windows.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Tic80 =>
                await _tic80.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.MicroW8 =>
                await _microW8.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.UnrealSpeccy =>
                await _unrealSpeccy.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.EightyOne =>
                await _eightyOne.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.ZEsarUX =>
                await _zesarux.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.KegaFusion =>
                await _fusion.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Browser =>
                await _browser.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Java =>
                await _java.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Fuse =>
                await _fuse.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.BlastEm =>
                await _blastEm.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Arculator =>
                await _arculator.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.PPSSPP =>
                await _ppsspp.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.BlueMSX =>
                await _blueMsx.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.DuckStation =>
                await _duckStation.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Pcsx2 =>
                await _pcsx2.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Trs80gp =>
                await _trs80gp.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Oricutron =>
                await _oricutron.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Dolphin =>
                await _dolphin.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.SimCoupe =>
                await _simcoupe.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Flycast =>
                await _flycast.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.JzIntv =>
                await _jzintv.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Dcmoto =>
                await _dcmoto.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Xm6TypeG =>
                await _xm6TypeG.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.BeebEm =>
                await _beebEm.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.SQLux =>
                await _sqlux.LaunchAsync(emulator, config, settings, romPath, release),
            // EmulatorType.Ryujinx / .Rpcs3 retirés le 2026-07-24 — tombent désormais sur
            // le cas générique (LaunchGenericAsync) ci-dessous si un profil existant y
            // fait encore référence. Enum conservé (cf. EmulatorSeedCatalog.cs).
            EmulatorType.BigPEmu =>
                await _bigPEmu.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Handy =>
                await _handy.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.GeePee32 =>
                await _geePee32.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Ep128Emu =>
                await _ep128Emu.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Mz800Emu =>
                await _mz800Emu.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.ColEm =>
                await _colEm.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.PuNES =>
                await _puNes.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Ares =>
                await _ares.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Ruffle =>
                await _ruffle.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Mame =>
                await _mame.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Mesen =>
                await _mesen.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.MelonDS =>
                await _melonds.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Azahar =>
                await _azahar.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Stella =>
                await _stella.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.ProSystem =>
                await _proSystem.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Xenia =>
                await _xenia.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.CxbxReloaded =>
                await _cxbx.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.AppleWin =>
                await _appleWin.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.GSplus =>
                await _gsPlus.LaunchAsync(emulator, config, settings, romPath, release),
            EmulatorType.Pemsa =>
                await _pemsa.LaunchAsync(emulator, config, settings, romPath, release),
            _ =>
                await LaunchGenericAsync(emulator, config, romPath),
        };

        if (!result.Success)
            DemoBase.App.Controls.StatusScrollerControl.Post(
                result.Error ?? "Erreur au lancement.", isError: true);
    }

    // ─── Lancement générique (ligne de commande) ──────────────────────────────

    private static Task<LaunchResult> LaunchGenericAsync(
        Emulator emulator, EmulatorConfig config, string romPath)
    {
        try
        {
            var args    = EmulatorLaunchService.SubstituteVars(config.CommandLine, romPath);
            var workDir = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                ? Path.GetDirectoryName(emulator.ExecutablePath)!
                : EmulatorLaunchService.SubstituteVars(config.WorkingDirectory, romPath);

            Process.Start(new ProcessStartInfo
            {
                FileName         = emulator.ExecutablePath,
                Arguments        = args,
                WorkingDirectory = Directory.Exists(workDir) ? workDir
                                 : Path.GetDirectoryName(emulator.ExecutablePath)!,
                UseShellExecute  = false,
            });
            return Task.FromResult(new LaunchResult(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new LaunchResult(false, ex.Message));
        }
    }

    // ─── Construction ligne de commande ───────────────────────────────────────

    public Task<string> BuildCommandLineAsync(EmulatorConfig config, string filePath)
        => Task.FromResult(EmulatorLaunchService.SubstituteVars(config.CommandLine, filePath));
}
