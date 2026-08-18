using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text;

namespace DemoBase.App.Services;

// ─── Service de téléchargement du pack BIOS Recalbox ─────────────────────────
// Source : https://github.com/Abdess/retrobios/releases/latest
// Le pack Recalbox contient 346 fichiers BIOS vérifiés MD5 couvrant :
//   PS1, PS2, Dreamcast, Saturn, DS, GBA, GB/GBC, Neo Geo, MSX, Amiga…
//
// Après extraction dans AppPaths.Bios (à plat, structure préservée depuis le zip),
// on configure automatiquement les émulateurs qui lisent un ini/cfg portable :
//   DuckStation → Emus/Duckstation/settings.ini [BIOS] SearchDirectory
//   PCSX2       → Emus/PCSX2/inis/PCSX2.ini [Folders] Bios
//   Flycast     → Emus/Flycast/emu.cfg [config] bios_path
//   melonDS     → Emus/melonDS/melonDS.ini [Paths] BIOS7Path / BIOS9Path / FirmwarePath
//   Ares        → Emus/Ares/settings.bml GameBoyAdvance/Firmware/BIOS.World (format BML,
//                 indentation 2 espaces/niveau — pas de [Section] ; portable par défaut sur
//                 Windows, settings.bml généré par ares.exe lui-même à côté de son exe)
//
// Complément 2026-07-24 : le pack Recalbox/Abdess ci-dessus n'inclut PAS le firmware Neo Geo
// Pocket / Neo Geo Pocket Color (marqué "required missing" par Abdess lui-même). Un second
// téléchargement, ciblé et minimal (2 fichiers seulement), complète automatiquement le pack
// depuis archive.org/details/firmware_202310 — cf. DownloadNeoGeoPocketFirmwareAsync, appelé
// juste après le pack Recalbox dans DownloadAndInstallAsync. Non bloquant : une panne réseau
// sur cette étape n'empêche jamais l'installation du pack Recalbox principal.
//
// [CORRECTIF 2026-07-24, suite] Le réseau du bac à sable ne pouvant pas atteindre archive.org,
// les noms de fichiers internes au zip avaient été devinés à partir du code source d'ares
// ("ngp_bios.rom"/"ngpc_bios.rom") — ce qui ne correspondait PAS au contenu réel du pack
// (l'utilisateur a confirmé que le téléchargement se lançait mais qu'aucun fichier n'apparaissait
// dans Emus/Ares/bios/). L'utilisateur a alors uploadé Firmware.zip directement ; inspection
// directe (unzip -v) a révélé les VRAIS noms internes : "Neo Geo Pocket - BIOS (World).bin" et
// "Neo Geo Pocket Color - BIOS (World).bin" (pack curé à la main, convention "<Système> - BIOS
// (<Région>).bin", rien à voir avec les noms internes d'ares), avec taille et CRC32 désormais
// vérifiés (65536 octets, CRC32 0x6232DF8D et 0x6EEB6F40). Les deux entrées ont donc été
// déplacées dans AresBiosFiles (taille+CRC32, même mécanisme fiable que gba_bios.bin) au lieu de
// AresBiosFilesByName (nom seul, supprimé) — DownloadNeoGeoPocketFirmwareAsync extrait
// maintenant les 2 fichiers par leur vrai nom exact et les dépose dans biosDir sous ce même nom ;
// c'est ensuite ConfigureAres/CopyBiosFilesBySizeCrc32 (déjà existant, déjà utilisé pour
// gba_bios.bin) qui les retrouve par contenu et les copie sous le nom attendu par ares
// (ngp_bios.rom/ngpc_bios.rom) dans Emus/Ares/bios/.

public class BiosPackService
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
    })
    {
        DefaultRequestHeaders = { { "User-Agent", "DemoBase/1.0" } },
        Timeout = TimeSpan.FromMinutes(10),
    };

    private const string GitHubApiUrl =
        "https://api.github.com/repos/Abdess/retrobios/releases/latest";

    /// <summary>
    /// Télécharge le pack BIOS Recalbox depuis GitHub, extrait les fichiers dans
    /// AppPaths.Bios, puis configure automatiquement les émulateurs installés.
    /// </summary>
    public async Task<(bool Success, string Message)> DownloadAndInstallAsync(
        IProgress<(string Label, int Percent)>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            // ── 1. Résoudre l'URL de téléchargement ──────────────────────────
            progress?.Report(("Recherche de la dernière version…", 2));

            var (url, version) = await ResolveDownloadUrlAsync(ct);
            if (string.IsNullOrEmpty(url))
                return (false, "Pack Recalbox introuvable dans les releases GitHub.");

            progress?.Report(($"Téléchargement du pack {version}…", 5));

            // ── 2. Télécharger le ZIP dans un dossier temporaire ─────────────
            var tmpDir  = Path.Combine(Path.GetTempPath(), "DemoBaseBios");
            Directory.CreateDirectory(tmpDir);
            var tmpFile = Path.Combine(tmpDir, $"Recalbox_{version}.zip");

            await DownloadAsync(url, tmpFile, progress, ct);

            // ── 3. Extraire dans AppPaths.Bios ───────────────────────────────
            progress?.Report(("Extraction des fichiers BIOS…", 80));

            var biosDir = AppPaths.Bios;
            Directory.CreateDirectory(biosDir);
            await Task.Run(() => ExtractBiosPack(tmpFile, biosDir), ct);

            // ── 4. Supprimer le fichier temporaire ───────────────────────────
            try { File.Delete(tmpFile); } catch { }

            // ── 4bis. Compléter avec le firmware Neo Geo Pocket / Pocket Color ────
            // Absent du pack Recalbox/Abdess ci-dessus (marqué "required missing" côté
            // Abdess/retrobios lui-même — cf. abdess.github.io/retrobios/emulators/ares/).
            // Signalé par l'utilisateur (archive.org/details/firmware_202310, pack complet
            // de firmware ares). Best-effort et non bloquant : une panne réseau ou un
            // changement de mise en page du pack ne doit jamais faire échouer le
            // téléchargement du pack Recalbox principal.
            try
            {
                await DownloadNeoGeoPocketFirmwareAsync(biosDir, progress, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BIOS] Firmware Neo Geo Pocket (archive.org) : échec non bloquant : {ex.Message}");
            }

            // ── 5. Configurer les émulateurs ─────────────────────────────────
            progress?.Report(("Configuration des émulateurs…", 92));
            ConfigureEmulators(biosDir);

            progress?.Report(("Installation terminée.", 100));
            return (true, $"Pack BIOS Recalbox {version} installé avec succès ({ToRelative(biosDir)}).");
        }
        catch (OperationCanceledException)
        {
            return (false, "Téléchargement annulé.");
        }
        catch (Exception ex)
        {
            return (false, $"Erreur : {ex.Message}");
        }
    }

    // ── Résolution de l'URL ────────────────────────────────────────────────────

    private static string ToRelative(string absolute)
    {
        var base_ = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var norm  = absolute.TrimEnd('\\', '/');
        if (norm.StartsWith(base_, StringComparison.OrdinalIgnoreCase))
        {
            var rel = norm[base_.Length..].TrimStart('\\', '/');
            return string.IsNullOrEmpty(rel) ? ".\\" : $".\\{rel}";
        }
        return absolute;
    }

    private static async Task<(string Url, string Version)> ResolveDownloadUrlAsync(
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(GitHubApiUrl, ct);
        response.EnsureSuccessStatusCode();

        var json    = await response.Content.ReadAsStringAsync(ct);
        var release = JsonNode.Parse(json);
        var tag     = release?["tag_name"]?.GetValue<string>() ?? "latest";
        var assets  = release?["assets"]?.AsArray() ?? [];

        // Chercher l'asset Recalbox*.zip
        foreach (var asset in assets)
        {
            var name = asset?["name"]?.GetValue<string>() ?? "";
            if (name.StartsWith("Recalbox", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var url = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                return (url, tag.TrimStart('v'));
            }
        }

        return ("", "");
    }

    // ── Téléchargement avec progression ───────────────────────────────────────

    private static async Task DownloadAsync(
        string url, string destFile,
        IProgress<(string, int)>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total      = response.Content.Headers.ContentLength ?? 0L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file   = File.Create(destFile);

        var buffer     = new byte[81920];
        long downloaded = 0;
        int  read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                int pct = 5 + (int)(70 * downloaded / total);
                var mb  = downloaded / 1024 / 1024;
                var tot = total     / 1024 / 1024;
                progress?.Report(($"Téléchargement… {mb} Mo / {tot} Mo", pct));
            }
        }
    }

    // ── Extraction ────────────────────────────────────────────────────────────

    private static void ExtractBiosPack(string zipPath, string biosDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        // Le ZIP Recalbox a une structure plate à la racine (pas de dossier racine
        // unique) : les fichiers BIOS sont directement à la racine ou dans des
        // sous-dossiers système (ex: MSX/, Amiga/…).
        // On extrait tout en préservant la structure interne.
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // dossier

            // Ignorer les artefacts macOS
            if (entry.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(entry.FullName).StartsWith("._"))
                continue;

            var dest = Path.Combine(biosDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var dir  = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    // ── Firmware Neo Geo Pocket / Pocket Color (complément au pack Recalbox) ─────
    // Source : https://archive.org/details/firmware_202310 ("Firmware.zip", ~11 Mo, pack
    // complet de firmware pour ares) — signalé par l'utilisateur, puis uploadé directement pour
    // inspection (voir correctif du 2026-07-24 ci-dessus). Contrairement au pack Recalbox
    // ci-dessus, on ne télécharge/extrait QUE les deux entrées utiles à DemoBase
    // (NeoGeoPocketFirmwareEntries) — pas la totalité du pack — puisque tout le reste (GBA, PSX,
    // MegaCD…) est déjà couvert par le pack Recalbox. Noms d'entrées, taille et CRC32 vérifiés
    // directement sur le fichier réel (unzip -v) — plus une hypothèse.
    private const string NeoGeoPocketFirmwareUrl =
        "https://archive.org/download/firmware_202310/Firmware.zip";

    private static readonly (string ZipEntryName, long Size, uint Crc32)[] NeoGeoPocketFirmwareEntries =
    {
        ("Neo Geo Pocket - BIOS (World).bin",       65536, 0x6232DF8D),
        ("Neo Geo Pocket Color - BIOS (World).bin", 65536, 0x6EEB6F40),
    };

    private static async Task DownloadNeoGeoPocketFirmwareAsync(
        string biosDir, IProgress<(string Label, int Percent)>? progress, CancellationToken ct)
    {
        // Idempotent : si les deux fichiers sont déjà là sous leur nom d'origine (téléchargement
        // précédent, ou l'utilisateur les a placés lui-même à la main dans le pack partagé), pas
        // la peine de re-télécharger 11 Mo à chaque clic sur "Pack BIOS".
        if (NeoGeoPocketFirmwareEntries.All(f => IsValidBiosFile(Path.Combine(biosDir, f.ZipEntryName), f.Size, f.Crc32)))
            return;

        progress?.Report(("Téléchargement du firmware Neo Geo Pocket…", 82));

        var tmpDir  = Path.Combine(Path.GetTempPath(), "DemoBaseBios");
        Directory.CreateDirectory(tmpDir);
        var tmpFile = Path.Combine(tmpDir, "ares_firmware_202310.zip");

        System.Diagnostics.Debug.WriteLine(
            $"[BIOS] Téléchargement firmware NGP/NGPC depuis {NeoGeoPocketFirmwareUrl}…");
        await DownloadAsync(NeoGeoPocketFirmwareUrl, tmpFile, null, ct);
        System.Diagnostics.Debug.WriteLine(
            $"[BIOS] Téléchargement terminé → {tmpFile} ({new FileInfo(tmpFile).Length} octets)");

        try
        {
            using var zip = ZipFile.OpenRead(tmpFile);
            System.Diagnostics.Debug.WriteLine(
                $"[BIOS] Archive.org : {zip.Entries.Count} entrée(s) dans le zip");

            foreach (var entry in NeoGeoPocketFirmwareEntries)
            {
                var zipEntry = zip.GetEntry(entry.ZipEntryName);
                if (zipEntry == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BIOS] '{entry.ZipEntryName}' introuvable dans le zip archive.org (contenu du pack modifié ?)");
                    continue;
                }

                var dest = Path.Combine(biosDir, entry.ZipEntryName);
                zipEntry.ExtractToFile(dest, overwrite: true);
                System.Diagnostics.Debug.WriteLine(
                    $"[BIOS] '{entry.ZipEntryName}' extrait du pack archive.org ({zipEntry.Length} octets) → {dest}");
            }
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

    // ── Configuration des émulateurs ──────────────────────────────────────────

    private static void ConfigureEmulators(string biosDir)
    {
        var emusRoot = EmulatorInstallerService.EmusRoot;

        ConfigureDuckStation(biosDir, emusRoot);
        ConfigurePcsx2(biosDir, emusRoot);
        ConfigureFlycast(biosDir, emusRoot);
        ConfigureMelonDS(biosDir, emusRoot);
        ConfigureXm6TypeG(biosDir, emusRoot);
        ConfigureGSPlus(biosDir, emusRoot);
        ConfigureHandy(biosDir, emusRoot);
        ConfigureJzIntv(biosDir, emusRoot);
        ConfigureAres(biosDir, emusRoot);
        DeployEmbeddedJzIntvEcs(emusRoot);
    }

    /// <summary>
    /// Émulateurs dont la configuration BIOS dépend du pack Recalbox — clés = FolderName
    /// exact tel qu'utilisé par EmulatorDownloadCatalog/EmulatorInstallerService (même valeur
    /// que celle codée en dur dans chaque ConfigureXxx ci-dessus). Tous les autres émulateurs
    /// n'ont rien à voir avec ce pack (Kickstart/TOS/ROMs gérés séparément, etc.) — pas la
    /// peine de les scanner à chaque installation.
    /// </summary>
    private static readonly HashSet<string> FolderNamesNeedingBios = new(StringComparer.OrdinalIgnoreCase)
    {
        "Duckstation", "PCSX2", "Flycast", "melonDS", "XM6TypeG", "GSPlus", "Handy", "jzIntv", "Ares",
    };

    /// <summary>
    /// À appeler juste après l'installation RÉUSSIE d'un émulateur (EmulatorInstallerService.
    /// InstallAsync) — configure immédiatement son BIOS depuis le pack déjà téléchargé, sans
    /// attendre un re-téléchargement manuel du pack. No-op si cet émulateur n'a pas besoin du
    /// pack BIOS, ou si le pack n'a encore jamais été téléchargé (AppPaths.Bios absent).
    /// Best-effort : ne lève jamais — appelant (InstallAsync) doit rester non bloqué.
    /// </summary>
    public static void ConfigureEmulatorBiosIfNeeded(string folderName)
    {
        try
        {
            // jzIntv : ecs.bin est embarqué dans l'appli (Assets\JzIntv_ecs.bin), pas dans le
            // pack Recalbox — déployé indépendamment du téléchargement du pack BIOS, donc AVANT
            // le "return" ci-dessous qui ne s'applique qu'aux fichiers sourcés depuis le pack.
            if (string.Equals(folderName, "jzIntv", StringComparison.OrdinalIgnoreCase))
                DeployEmbeddedJzIntvEcs(EmulatorInstallerService.EmusRoot);

            if (!FolderNamesNeedingBios.Contains(folderName)) return;

            var biosDir = AppPaths.Bios;
            if (!Directory.Exists(biosDir)) return; // pack pas encore téléchargé

            var emusRoot = EmulatorInstallerService.EmusRoot;

            switch (folderName)
            {
                case "Duckstation": ConfigureDuckStation(biosDir, emusRoot); break;
                case "PCSX2":       ConfigurePcsx2(biosDir, emusRoot);       break;
                case "Flycast":     ConfigureFlycast(biosDir, emusRoot);     break;
                case "melonDS":     ConfigureMelonDS(biosDir, emusRoot);     break;
                case "XM6TypeG":    ConfigureXm6TypeG(biosDir, emusRoot);    break;
                case "GSPlus":      ConfigureGSPlus(biosDir, emusRoot);      break;
                case "Handy":       ConfigureHandy(biosDir, emusRoot);       break;
                case "jzIntv":      ConfigureJzIntv(biosDir, emusRoot);      break;
                case "Ares":        ConfigureAres(biosDir, emusRoot);        break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BIOS] Configuration post-install de '{folderName}' échouée (non bloquant) : {ex.Message}");
        }
    }

    // XM6 TypeG (Sharp X68000) : cgrom.dat + 4 iplromXX.dat, requis pour démarrer.
    // Contrairement à DuckStation/PCSX2/Flycast/melonDS, xm6g.ini n'expose AUCUNE clé de
    // chemin ROM/BIOS — les fichiers doivent être physiquement présents à côté de xm6g.exe.
    // Le pack BIOS Recalbox ne les nomme pas forcément comme XM6 TypeG les attend (ni ne les
    // range à un emplacement fixe) : identification par taille + CRC32 (table fournie par
    // l'utilisateur, vérifiée sur son pack), pas par nom de fichier.
    private static readonly (string FileName, long Size, uint Crc32)[] Xm6TypeGBiosFiles =
    {
        ("cgrom.dat",    786432, 0x9F3195F1),
        ("iplrom.dat",   131072, 0x72BDF532),
        ("iplrom30.dat", 131072, 0xE8F8FDAD),
        ("iplromco.dat", 131072, 0x6C7EF608),
        ("iplromxv.dat", 131072, 0x00EEB408),
    };

    private static void ConfigureXm6TypeG(string biosDir, string emusRoot)
        => CopyBiosFilesBySizeCrc32(biosDir, Path.Combine(emusRoot, "XM6TypeG"), Xm6TypeGBiosFiles, "XM6 TypeG");

    // GSPlus (Apple IIGS) : rom03 (firmware Apple IIGS), requis pour démarrer. Même situation
    // que XM6 TypeG : pas de clé de chemin BIOS exposée, le fichier doit être physiquement
    // présent à côté de l'exe. Dans le pack Recalbox, ce même firmware est présent sous le nom
    // "apple2gs3.rom" à la racine de BIOS\bios — identification par taille + CRC32 (fourni par
    // l'utilisateur), pas par nom de fichier.
    private static readonly (string FileName, long Size, uint Crc32)[] GSPlusBiosFiles =
    {
        ("rom03", 262144, 0xDE7DDF29),
    };

    private static void ConfigureGSPlus(string biosDir, string emusRoot)
        => CopyBiosFilesBySizeCrc32(biosDir, Path.Combine(emusRoot, "GSPlus"), GSPlusBiosFiles, "GSPlus");

    // Handy (Atari Lynx) : lynxboot.img (BIOS boot ROM), requis pour démarrer.
    private static readonly (string FileName, long Size, uint Crc32)[] HandyBiosFiles =
    {
        ("lynxboot.img", 512, 0x0D973C9D),
    };

    private static void ConfigureHandy(string biosDir, string emusRoot)
        => CopyBiosFilesBySizeCrc32(biosDir, Path.Combine(emusRoot, "Handy"), HandyBiosFiles, "Handy");

    // jzIntv (Mattel Intellivision) : exec.bin (EXEC, ROM système) + grom.bin (GROM, jeu de
    // caractères/graphismes), requis pour démarrer — voir commentaire dans JzIntvLauncher.cs.
    // Même situation que XM6 TypeG/GSPlus/Handy : jzIntv ne lit ni ini ni clé de chemin BIOS,
    // les deux fichiers doivent être physiquement présents à côté de jzIntv.exe.
    // Identification par taille + CRC32 (fournis par l'utilisateur, vérifiés sur son pack).
    private static readonly (string FileName, long Size, uint Crc32)[] JzIntvBiosFiles =
    {
        ("exec.bin", 8192, 0xCBCE86F7),
        ("grom.bin", 2048, 0x683A4158),
    };

    // [CORRECTIF] L'exécutable jzIntv se trouve dans un sous-dossier "bin" de son dossier
    // d'installation (Emus\jzIntv\bin\jzIntv.exe), pas à la racine — contrairement à XM6 TypeG/
    // GSPlus/Handy. La première version de ce correctif copiait exec.bin/grom.bin à la racine de
    // Emus\jzIntv, ce qui ne fonctionnait pas puisque jzIntv les cherche à côté de son .exe.
    private static void ConfigureJzIntv(string biosDir, string emusRoot)
        => CopyBiosFilesBySizeCrc32(biosDir, Path.Combine(emusRoot, "jzIntv", "bin"), JzIntvBiosFiles, "jzIntv");

    // jzIntv : ecs.bin (BIOS de l'extension ECS — Entertainment Computer System), également
    // requis par certaines productions. Absent du pack BIOS Recalbox (vérifié : ni le nom ni une
    // taille/CRC32 correspondante n'y figurent) — fourni directement par l'utilisateur et
    // embarqué dans l'application (Assets\JzIntv_ecs.bin), même convention que
    // Mesen_settings.json / UnrealSpeccy_NVRAM-CMOS (cf. MesenSetupService). Déployé
    // indépendamment de l'état du pack Recalbox — voir DeployEmbeddedJzIntvEcs.
    private const string JzIntvEcsAssetName = "JzIntv_ecs.bin";

    /// <summary>
    /// Copie ecs.bin (embarqué dans l'appli, PAS dans le pack Recalbox) vers Emus\jzIntv\bin,
    /// si jzIntv est installé et que le fichier n'y est pas déjà présent. Jamais destructif,
    /// jamais bloquant — même philosophie que MesenSetupService.CopyIfMissing.
    /// </summary>
    private static void DeployEmbeddedJzIntvEcs(string emusRoot)
    {
        try
        {
            var srcPath = Path.Combine(AppContext.BaseDirectory, "Assets", JzIntvEcsAssetName);
            if (!File.Exists(srcPath))
            {
                System.Diagnostics.Debug.WriteLine($"[BIOS] jzIntv : asset ecs.bin introuvable : {srcPath}");
                return;
            }

            var targetDir = Path.Combine(emusRoot, "jzIntv", "bin");
            if (!Directory.Exists(targetDir)) return; // jzIntv pas (encore) installé

            var destPath = Path.Combine(targetDir, "ecs.bin");
            if (File.Exists(destPath)) return; // déjà présent

            File.Copy(srcPath, destPath, overwrite: false);
            System.Diagnostics.Debug.WriteLine($"[BIOS] jzIntv : ecs.bin déployé → {destPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BIOS] jzIntv : déploiement ecs.bin échoué (non bloquant) : {ex.Message}");
        }
    }

    /// <summary>
    /// Cherche dans le pack BIOS (récursivement) des fichiers identifiés par taille+CRC32
    /// (peu importe leur nom dans le pack) et les copie sous le nom attendu par l'émulateur
    /// dans targetDir. Un seul passage sur le pack (potentiellement des centaines de
    /// fichiers) : le CRC32 n'est calculé que pour les fichiers dont la taille correspond à
    /// l'un des ROMs recherchés, pour éviter de hasher tout le pack. Utilisé par les
    /// émulateurs dont le format de config n'expose aucune clé de chemin BIOS/ROM (XM6 TypeG,
    /// GSPlus) — les fichiers doivent être physiquement présents à côté de l'exe.
    /// </summary>
    private static void CopyBiosFilesBySizeCrc32(
        string biosDir, string targetDir,
        (string FileName, long Size, uint Crc32)[] biosFiles, string logTag)
    {
        if (!Directory.Exists(targetDir)) return; // émulateur pas (encore) installé

        var missing = biosFiles
            .Where(f => !IsValidBiosFile(Path.Combine(targetDir, f.FileName), f.Size, f.Crc32))
            .ToList();
        if (missing.Count == 0) return; // déjà tous présents et corrects

        var neededSizes = new HashSet<long>(missing.Select(f => f.Size));
        var foundNames  = new HashSet<string>();

        foreach (var path in Directory.EnumerateFiles(biosDir, "*", SearchOption.AllDirectories))
        {
            if (foundNames.Count == missing.Count) break;

            long len;
            try { len = new FileInfo(path).Length; } catch { continue; }
            if (!neededSizes.Contains(len)) continue;

            uint crc;
            try { crc = DatMaker.Crc32.GetFileCRC32(path); }
            catch { continue; }

            foreach (var f in missing)
            {
                if (foundNames.Contains(f.FileName) || f.Size != len || f.Crc32 != crc) continue;

                try
                {
                    File.Copy(path, Path.Combine(targetDir, f.FileName), overwrite: true);
                    foundNames.Add(f.FileName);
                    System.Diagnostics.Debug.WriteLine(
                        $"[BIOS] {logTag} : {f.FileName} trouvé et copié ← {path}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BIOS] {logTag} : échec copie {f.FileName} : {ex.Message}");
                }
                break;
            }
        }

        var stillMissing = missing.Where(f => !foundNames.Contains(f.FileName)).Select(f => f.FileName).ToList();
        if (stillMissing.Count > 0)
            System.Diagnostics.Debug.WriteLine(
                $"[BIOS] {logTag} : ROM(s) introuvable(s) dans le pack (taille+CRC32 non trouvés) : {string.Join(", ", stillMissing)}");
    }

    /// <summary>Vrai si le fichier existe déjà à cet emplacement avec la taille et le CRC32 attendus.</summary>
    private static bool IsValidBiosFile(string path, long expectedSize, uint expectedCrc32)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (new FileInfo(path).Length != expectedSize) return false;
            return DatMaker.Crc32.GetFileCRC32(path) == expectedCrc32;
        }
        catch { return false; }
    }

    // DuckStation : settings.ini [BIOS] SearchDirectory = <biosDir>
    //
    // Ajout du 2026-07-24 : [Main] ConfirmPowerOff = false — demande utilisateur, même
    // besoin que pour PCSX2 (cf. ConfigurePcsx2 plus bas) : éviter la boîte de dialogue
    // de confirmation à la fermeture/l'extinction de l'émulateur. Clé et section
    // confirmées via le code source DuckStation (settings.cpp :
    // si.GetBoolValue("Main", "ConfirmPowerOff", true) / SetBoolValue(...) — vrai par
    // défaut). Contrairement à PCSX2, DuckStation ne semble pas exiger un fichier ini
    // complet pour démarrer (l'utilisateur confirme qu'il fonctionne déjà, y compris
    // sans BIOS configuré pour les homebrews) — patcher un ini existant partiel suffit
    // donc ici, pas besoin d'un template complet comme Pcsx2_PCSX2.ini.
    private static void ConfigureDuckStation(string biosDir, string emusRoot)
    {
        var iniPath = Path.Combine(emusRoot, "Duckstation", "settings.ini");
        if (!File.Exists(iniPath)) return;
        UpdateIniValue(iniPath, "BIOS", "SearchDirectory", biosDir);
        UpdateIniValue(iniPath, "Main", "ConfirmPowerOff", "false");
        System.Diagnostics.Debug.WriteLine($"[BIOS] DuckStation configuré → {biosDir} (ConfirmPowerOff=false)");
    }

    // Ares — ajouté le 2026-07-24, PUIS RECONÇU le même jour à la demande de l'utilisateur.
    // Version initiale : référençait directement un fichier du pack partagé (AppPaths.Bios)
    // depuis settings.bml, et écrivait ce chemin au moment du "Pack BIOS". Changement demandé :
    //   1. Le BIOS doit être COPIÉ dans le répertoire d'ares (Emus/Ares/bios/), comme les
    //      autres émulateurs, pas référencé en place dans le pack partagé.
    //   2. settings.bml doit être vérifié/complété à CHAQUE LANCEMENT d'ares (il y aura
    //      plusieurs systèmes à BIOS à terme — NeoGeoPocket, MegaCD, PlayStation...), pas
    //      seulement au moment du "Pack BIOS" — et seulement si la valeur est encore vide,
    //      pour ne jamais écraser un réglage que l'utilisateur aurait fait à la main.
    //   3. Le bouton "Pack BIOS" ne sert donc plus, pour ares, qu'à COPIER les fichiers
    //      identifiés dans Emus/Ares/bios/ — plus aucune écriture dans settings.bml à cet
    //      instant (cf. ConfigureAres, qui ne fait plus que copier). L'écriture réelle dans
    //      settings.bml se fait dans SyncAresFirmwareSettings, appelée par AresLauncher à
    //      chaque LaunchAsync.
    //
    // Format ares (BML — indentation 2 espaces/niveau, PAS de crochets [Section] comme un ini
    // classique) non géré par UpdateIniValue — cf. UpdateAresBmlFirmware/GetAresBmlFirmwareValue
    // ci-dessous, un patch dédié à ce format, dérivé de la structure réelle observée dans le
    // settings.bml fourni par l'utilisateur (portable par défaut sur Windows : settings.bml
    // généré par ares.exe lui-même à côté de son exe).
    //
    // Game Boy Advance : taille+CRC32 fournis par l'utilisateur, gba_bios.bin.
    //
    // Neo Geo Pocket / Neo Geo Pocket Color — ajoutés le 2026-07-24, d'abord identifiés par
    // NOM DE FICHIER SEUL (ngp_bios.rom/ngpc_bios.rom, extrapolés du code source d'ares, faute
    // de pouvoir télécharger le zip archive.org depuis le bac à sable réseau de cet
    // environnement) — ce qui s'est avéré FAUX à l'usage (voir correctif du 2026-07-24 en tête
    // de fichier) : les vrais noms dans le pack archive.org sont "Neo Geo Pocket - BIOS
    // (World).bin" / "Neo Geo Pocket Color - BIOS (World).bin", avec taille 65536 et CRC32
    // 0x6232DF8D / 0x6EEB6F40 — vérifiés directement sur le fichier réel uploadé par
    // l'utilisateur (unzip -v). Migré ici (taille+CRC32, comme gba_bios.bin) : le FileName
    // ci-dessous ("ngp_bios.rom"/"ngpc_bios.rom") n'est que le nom de DESTINATION dans
    // Emus/Ares/bios/ (ce qu'ares attend) — CopyBiosFilesBySizeCrc32 retrouve le fichier source
    // dans le pack par contenu, peu importe son nom d'origine.
    //   AresSystem "NeoGeoPocket"/"NeoGeoPocketColor" et FirmwareKey "BIOS.World" : confirmés
    //   en relisant le settings.bml complet fourni par l'utilisateur — il contient bien les deux
    //   blocs scaffoldés par ares lui-même (NeoGeoPocket/Firmware/BIOS.World et
    //   NeoGeoPocketColor/Firmware/BIOS.World, vides, jamais renseignés).
    // D'autres entrées (MegaCD, PlayStation, MSX, Nintendo64DD, LaserActive...) pourront être
    // ajoutées dès que leurs identités seront confirmées — même démarche que pour les 4 BIOS PS2
    // de ConfigurePcsx2 plus bas.
    private static readonly (string AresSystem, string FirmwareKey, string FileName, long Size, uint Crc32)[] AresBiosFiles =
    {
        ("GameBoyAdvance",    "BIOS.World", "gba_bios.bin",  16384, 0x81977335),
        ("NeoGeoPocket",      "BIOS.World", "ngp_bios.rom",  65536, 0x6232DF8D),
        ("NeoGeoPocketColor", "BIOS.World", "ngpc_bios.rom", 65536, 0x6EEB6F40),
    };

    /// <summary>
    /// Appelé par "Pack BIOS" — copie dans Emus/Ares/bios/ chaque fichier de
    /// <see cref="AresBiosFiles"/> (taille+CRC32) trouvé dans le pack, sans toucher à
    /// settings.bml (cf. commentaire ci-dessus : c'est <see cref="SyncAresFirmwareSettings"/>,
    /// appelée à chaque lancement d'ares, qui s'en charge).
    /// </summary>
    private static void ConfigureAres(string biosDir, string emusRoot)
    {
        var aresRoot   = Path.Combine(emusRoot, "Ares");
        var aresBiosDir = Path.Combine(aresRoot, "bios");
        if (!Directory.Exists(aresRoot))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BIOS] Ares : dossier {aresRoot} introuvable — ares pas (encore) installé, rien à copier");
            return;
        }

        Directory.CreateDirectory(aresBiosDir);
        var files = AresBiosFiles.Select(f => (f.FileName, f.Size, f.Crc32)).ToArray();
        CopyBiosFilesBySizeCrc32(biosDir, aresBiosDir, files, "Ares");
    }

    /// <summary>
    /// Appelée par <see cref="AresLauncher"/> à CHAQUE lancement d'ares (pas seulement au
    /// "Pack BIOS") — pour chaque BIOS de <see cref="AresBiosFiles"/> déjà copié dans
    /// Emus/Ares/bios/ (par ConfigureAres), vérifie la valeur actuelle dans settings.bml et ne
    /// la renseigne QUE si elle est encore vide (jamais d'écrasement d'un réglage déjà fait —
    /// à la main ou par un run précédent). No-op silencieux si settings.bml n'existe pas encore
    /// (ares pas encore lancé une 1ère fois) ou si un BIOS n'a pas encore été copié.
    /// </summary>
    public static void SyncAresFirmwareSettings(string exePath)
    {
        try
        {
            var aresRoot = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(aresRoot)) return;

            var bmlPath = Path.Combine(aresRoot, "settings.bml");
            if (!File.Exists(bmlPath)) return;

            var aresBiosDir = Path.Combine(aresRoot, "bios");

            foreach (var entry in AresBiosFiles)
            {
                var biosPath = Path.Combine(aresBiosDir, entry.FileName);
                if (!File.Exists(biosPath)) continue; // pas encore copié — cf. ConfigureAres

                var current = GetAresBmlFirmwareValue(bmlPath, entry.AresSystem, entry.FirmwareKey);
                if (!string.IsNullOrWhiteSpace(current)) continue; // déjà réglé — ne jamais écraser

                var biosPathBml = Path.GetFullPath(biosPath).Replace('\\', '/');
                UpdateAresBmlFirmware(bmlPath, entry.AresSystem, entry.FirmwareKey, biosPathBml);
                System.Diagnostics.Debug.WriteLine(
                    $"[BIOS] Ares : {entry.AresSystem}/{entry.FirmwareKey} renseigné au lancement → {biosPathBml}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BIOS] SyncAresFirmwareSettings a échoué : {ex.Message}");
        }
    }

    // ── Contrôles Neo Geo Pocket / Pocket Color + Hotkey "Quitter" (F12) ─────────
    // Ajouté le 2026-07-24, à la demande de l'utilisateur : le système Neo Geo Pocket
    // nécessite une initialisation manuelle au premier lancement (menu de configuration
    // interne au firmware). Contrairement au BIOS (identifié par taille+CRC32), les valeurs
    // ci-dessous sont des ASSIGNATIONS CLAVIER ares (format "0x1/0/<inputID>;;") — elles ne
    // peuvent PAS être devinées ou recalculées : elles ont été capturées et fournies
    // directement par l'utilisateur depuis SON settings.bml réel (touches qu'il a lui-même
    // configurées dans ares : flèches pour le D-pad, Espace pour A, Entrée pour B, F12 pour
    // quitter). Les DEUX systèmes (NeoGeoPocket et NeoGeoPocketColor) reçoivent le même
    // mapping — même contrôleur physique, confirmé par l'utilisateur.
    private static readonly (string ControlKey, string Value)[] NeoGeoPocketControlDefaults =
    {
        ("Up",    "0x1/0/86;;"),
        ("Down",  "0x1/0/87;;"),
        ("Left",  "0x1/0/88;;"),
        ("Right", "0x1/0/89;;"),
        ("A",     "0x1/0/92;;"),
        ("B",     "0x1/0/91;;"),
    };

    // Table de correspondance --system ares (avec espaces, cf. AresLauncher.PlatformNameToAresSystem)
    // → (clé BML premier niveau sans espace, sous-clé Input.<DeviceKey>).
    private static readonly Dictionary<string, (string BmlSystemKey, string DeviceKey)> NeoGeoPocketAresSystems = new()
    {
        ["Neo Geo Pocket"]       = ("NeoGeoPocket",      "Neo.Geo.Pocket"),
        ["Neo Geo Pocket Color"] = ("NeoGeoPocketColor",  "Neo.Geo.Pocket.Color"),
    };

    // Touche de sortie (Hotkey QuitEmulator) — F12, capturée et fournie par l'utilisateur
    // depuis son settings.bml réel. Concerne tous les systèmes (le Hotkey est global à
    // ares, pas par système) — renseignée dès le premier lancement d'ares, quel qu'il soit.
    private const string AresQuitEmulatorHotkeyValue = "0x1/0/12;;";

    /// <summary>
    /// Appelée par <see cref="AresLauncher"/> à chaque lancement — complète le Hotkey
    /// "QuitEmulator" (F12, tous systèmes) et, si <paramref name="aresSystem"/> correspond à
    /// Neo Geo Pocket ou Neo Geo Pocket Color, les Contrôles clavier par défaut de ce
    /// système précis. Ne renseigne jamais un champ déjà rempli (à la main ou par un
    /// lancement précédent) — même philosophie que <see cref="SyncAresFirmwareSettings"/>.
    /// Retourne vrai si les Contrôles Neo Geo Pocket(Color) viennent d'être renseignés pour
    /// la TOUTE PREMIÈRE FOIS lors de cet appel (tous les champs étaient encore vides avant) —
    /// signal utilisé par AresLauncher pour afficher le message d'initialisation une seule fois.
    /// </summary>
    public static bool SyncAresControlsAndHotkeys(string exePath, string? aresSystem)
    {
        bool firstRunNeoGeoPocket = false;
        try
        {
            var aresRoot = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(aresRoot)) return false;

            var bmlPath = Path.Combine(aresRoot, "settings.bml");
            if (!File.Exists(bmlPath)) return false;

            // Hotkey QuitEmulator (F12) — tous systèmes, indépendant de aresSystem.
            var currentQuit = GetAresBmlTopLevelValue(bmlPath, "Hotkey", "QuitEmulator");
            if (IsUnsetBmlValue(currentQuit))
            {
                UpdateAresBmlTopLevelValue(bmlPath, "Hotkey", "QuitEmulator", AresQuitEmulatorHotkeyValue);
                System.Diagnostics.Debug.WriteLine("[ARES] Hotkey QuitEmulator renseigné (F12).");
            }

            // Contrôles Neo Geo Pocket / Pocket Color — seulement si c'est le système en cours.
            if (aresSystem != null && NeoGeoPocketAresSystems.TryGetValue(aresSystem, out var target))
            {
                var wasAllEmpty = NeoGeoPocketControlDefaults.All(c =>
                    IsUnsetBmlValue(GetAresBmlControlValue(bmlPath, target.BmlSystemKey, target.DeviceKey, c.ControlKey)));

                foreach (var (controlKey, value) in NeoGeoPocketControlDefaults)
                {
                    var current = GetAresBmlControlValue(bmlPath, target.BmlSystemKey, target.DeviceKey, controlKey);
                    if (!IsUnsetBmlValue(current)) continue; // déjà réglé — ne jamais écraser
                    UpdateAresBmlControl(bmlPath, target.BmlSystemKey, target.DeviceKey, controlKey, value);
                }

                if (wasAllEmpty)
                {
                    firstRunNeoGeoPocket = true;
                    System.Diagnostics.Debug.WriteLine(
                        $"[ARES] Contrôles {aresSystem} renseignés pour la première fois.");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ARES] SyncAresControlsAndHotkeys a échoué : {ex.Message}");
        }
        return firstRunNeoGeoPocket;
    }

    /// <summary>Vrai si une valeur lue dans settings.bml représente un champ non configuré
    /// (absent, vide, ou le sentinel ";;" utilisé par ares pour les assignations Input/Hotkey
    /// sans binding).</summary>
    private static bool IsUnsetBmlValue(string? value)
        => string.IsNullOrWhiteSpace(value) || value == ";;";

    /// <summary>
    /// Localise, dans un bloc système BML à 3 niveaux d'imbrication
    /// (systemKey → "  Input" → "    deviceKey" → "      Controls"), les bornes du sous-bloc
    /// "Controls" — structure utilisée par Neo Geo Pocket/Pocket Color (et probablement
    /// d'autres systèmes multi-touches à l'avenir). Retourne (deviceIdx, deviceEnd, ctrlIdx,
    /// ctrlEnd) où deviceIdx &lt; 0 si systemKey/Input/deviceKey introuvable, et ctrlIdx = -1
    /// si le sous-bloc "Controls" est absent (ne devrait pas arriver : ares le scaffold
    /// toujours, mais on reste défensif).
    /// </summary>
    private static (int deviceIdx, int deviceEnd, int ctrlIdx, int ctrlEnd) LocateAresControlsBlock(
        List<string> lines, string systemKey, string deviceKey)
    {
        int sysIdx = lines.FindIndex(l => l == systemKey);
        if (sysIdx < 0) return (-1, -1, -1, -1);

        int sysEnd = lines.Count;
        for (int i = sysIdx + 1; i < lines.Count; i++)
            if (lines[i].Length > 0 && lines[i][0] != ' ') { sysEnd = i; break; }

        int inputIdx = -1;
        for (int i = sysIdx + 1; i < sysEnd; i++)
            if (lines[i] == "  Input") { inputIdx = i; break; }
        if (inputIdx < 0) return (-1, -1, -1, -1);

        int inputEnd = sysEnd;
        for (int i = inputIdx + 1; i < sysEnd; i++)
        {
            var indent = lines[i].Length - lines[i].TrimStart(' ').Length;
            if (lines[i].Length == 0 || indent <= 2) { inputEnd = i; break; }
        }

        int deviceIdx = -1;
        var deviceLine = "    " + deviceKey;
        for (int i = inputIdx + 1; i < inputEnd; i++)
            if (lines[i] == deviceLine) { deviceIdx = i; break; }
        if (deviceIdx < 0) return (-1, -1, -1, -1);

        int deviceEnd = inputEnd;
        for (int i = deviceIdx + 1; i < inputEnd; i++)
        {
            var indent = lines[i].Length - lines[i].TrimStart(' ').Length;
            if (lines[i].Length == 0 || indent <= 4) { deviceEnd = i; break; }
        }

        int ctrlIdx = -1;
        for (int i = deviceIdx + 1; i < deviceEnd; i++)
            if (lines[i] == "      Controls") { ctrlIdx = i; break; }

        int ctrlEnd = deviceEnd;
        if (ctrlIdx >= 0)
        {
            for (int i = ctrlIdx + 1; i < deviceEnd; i++)
            {
                var indent = lines[i].Length - lines[i].TrimStart(' ').Length;
                if (lines[i].Length == 0 || indent <= 6) { ctrlEnd = i; break; }
            }
        }

        return (deviceIdx, deviceEnd, ctrlIdx, ctrlEnd);
    }

    private static string? GetAresBmlControlValue(string bmlPath, string systemKey, string deviceKey, string controlKey)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(bmlPath));
            var (_, _, ctrlIdx, ctrlEnd) = LocateAresControlsBlock(lines, systemKey, deviceKey);
            if (ctrlIdx < 0) return null;

            for (int i = ctrlIdx + 1; i < ctrlEnd; i++)
            {
                var trimmed = lines[i].TrimStart(' ');
                if (trimmed == controlKey) return "";
                if (trimmed.StartsWith(controlKey + ":"))
                    return trimmed[(controlKey.Length + 1)..].Trim();
            }
            return null;
        }
        catch { return null; }
    }

    private static void UpdateAresBmlControl(
        string bmlPath, string systemKey, string deviceKey, string controlKey, string value)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(bmlPath));
            var (deviceIdx, _, ctrlIdx, ctrlEnd) = LocateAresControlsBlock(lines, systemKey, deviceKey);
            if (deviceIdx < 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ARES] Contrôles : bloc '{systemKey}/Input/{deviceKey}' introuvable dans settings.bml");
                return;
            }

            var newLine = $"        {controlKey}: {value}";

            if (ctrlIdx >= 0)
            {
                int keyIdx = -1;
                for (int i = ctrlIdx + 1; i < ctrlEnd; i++)
                {
                    var trimmed = lines[i].TrimStart(' ');
                    if (trimmed == controlKey || trimmed.StartsWith(controlKey + ":"))
                    {
                        keyIdx = i;
                        break;
                    }
                }
                if (keyIdx >= 0) lines[keyIdx] = newLine;
                else              lines.Insert(ctrlEnd, newLine);
            }
            else
            {
                // Pas de section "Controls" : ne devrait pas arriver (ares la scaffold
                // toujours), mais on la crée par prudence, juste après la ligne deviceKey.
                lines.Insert(deviceIdx + 1, newLine);
                lines.Insert(deviceIdx + 1, "      Controls");
            }

            File.WriteAllLines(bmlPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ARES] Erreur UpdateAresBmlControl {bmlPath}: {ex.Message}");
        }
    }

    /// <summary>Lit une clé directement enfant d'une section BML de premier niveau (0 espace),
    /// elle-même à 2 espaces d'indentation — structure utilisée par "Hotkey" (pas de
    /// sous-section intermédiaire, contrairement à Firmware/Controls ci-dessus).</summary>
    private static string? GetAresBmlTopLevelValue(string bmlPath, string sectionKey, string key)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(bmlPath));
            int sysIdx = lines.FindIndex(l => l == sectionKey);
            if (sysIdx < 0) return null;

            int sysEnd = lines.Count;
            for (int i = sysIdx + 1; i < lines.Count; i++)
                if (lines[i].Length > 0 && lines[i][0] != ' ') { sysEnd = i; break; }

            for (int i = sysIdx + 1; i < sysEnd; i++)
            {
                var trimmed = lines[i].TrimStart(' ');
                if (trimmed == key) return "";
                if (trimmed.StartsWith(key + ":"))
                    return trimmed[(key.Length + 1)..].Trim();
            }
            return null;
        }
        catch { return null; }
    }

    private static void UpdateAresBmlTopLevelValue(string bmlPath, string sectionKey, string key, string value)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(bmlPath));
            int sysIdx = lines.FindIndex(l => l == sectionKey);
            if (sysIdx < 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ARES] Section '{sectionKey}' introuvable dans settings.bml");
                return;
            }

            int sysEnd = lines.Count;
            for (int i = sysIdx + 1; i < lines.Count; i++)
                if (lines[i].Length > 0 && lines[i][0] != ' ') { sysEnd = i; break; }

            int keyIdx = -1;
            for (int i = sysIdx + 1; i < sysEnd; i++)
            {
                var trimmed = lines[i].TrimStart(' ');
                if (trimmed == key || trimmed.StartsWith(key + ":")) { keyIdx = i; break; }
            }

            var newLine = $"  {key}: {value}";
            if (keyIdx >= 0) lines[keyIdx] = newLine;
            else              lines.Insert(sysEnd, newLine);

            File.WriteAllLines(bmlPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ARES] Erreur UpdateAresBmlTopLevelValue {bmlPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Localise le bloc <paramref name="systemKey"/> (clé de premier niveau, 0 espace
    /// d'indentation) puis son éventuel sous-bloc "  Firmware" (2 espaces) — retourne
    /// (sysIdx, sysEnd, fwIdx, fwEnd) où fwIdx/fwEnd valent -1/sysEnd si "Firmware" est absent.
    /// Factorisation commune à <see cref="GetAresBmlFirmwareValue"/> et
    /// <see cref="UpdateAresBmlFirmware"/> (même traversée, l'une en lecture, l'autre en
    /// écriture).
    /// </summary>
    private static (int sysIdx, int sysEnd, int fwIdx, int fwEnd) LocateAresFirmwareBlock(
        List<string> lines, string systemKey)
    {
        int sysIdx = lines.FindIndex(l => l == systemKey);
        if (sysIdx < 0) return (-1, -1, -1, -1);

        int sysEnd = lines.Count;
        for (int i = sysIdx + 1; i < lines.Count; i++)
        {
            if (lines[i].Length > 0 && lines[i][0] != ' ') { sysEnd = i; break; }
        }

        int fwIdx = -1;
        for (int i = sysIdx + 1; i < sysEnd; i++)
        {
            if (lines[i] == "  Firmware") { fwIdx = i; break; }
        }

        int fwEnd = sysEnd;
        if (fwIdx >= 0)
        {
            for (int i = fwIdx + 1; i < sysEnd; i++)
            {
                var indent = lines[i].Length - lines[i].TrimStart(' ').Length;
                if (lines[i].Length == 0 || indent <= 2) { fwEnd = i; break; }
            }
        }

        return (sysIdx, sysEnd, fwIdx, fwEnd);
    }

    /// <summary>Lit la valeur actuelle d'une clé de firmware BML (ex. "BIOS.World" sous
    /// GameBoyAdvance/Firmware) — null si le bloc système, la section Firmware, ou la clé
    /// elle-même n'existe pas encore ; chaîne vide si la clé existe mais sans valeur (cas des
    /// blocs "scaffold" générés par défaut par ares, ex. "ColecoVision/Firmware/BIOS.World"
    /// sans ":" ni valeur).</summary>
    private static string? GetAresBmlFirmwareValue(string bmlPath, string systemKey, string firmwareKey)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(bmlPath));
            var (sysIdx, _, fwIdx, fwEnd) = LocateAresFirmwareBlock(lines, systemKey);
            if (sysIdx < 0 || fwIdx < 0) return null;

            for (int i = fwIdx + 1; i < fwEnd; i++)
            {
                var trimmed = lines[i].TrimStart(' ');
                if (trimmed == firmwareKey) return "";
                if (trimmed.StartsWith(firmwareKey + ":"))
                    return trimmed[(firmwareKey.Length + 1)..].Trim();
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Met à jour (ou ajoute) une clé de firmware dans un fichier BML (format ares —
    /// indentation 2 espaces/niveau, PAS de crochets [Section] comme un ini classique).
    /// Structure ciblée, observée dans le settings.bml réel de l'utilisateur (même
    /// convention pour tous les systèmes ayant du firmware — NeoGeoPocket, MSX, MegaCD…) :
    ///
    ///   GameBoyAdvance          ← systemKey, 0 espace d'indentation
    ///     ...
    ///     Visible: true
    ///     Path: ...
    ///     Firmware               ← 2 espaces
    ///       BIOS.World: ...      ← 4 espaces, firmwareKey
    ///
    /// La section "Firmware" est toujours le DERNIER enfant du bloc système, juste après
    /// "Path" — si elle n'existe pas encore (cas du tout premier réglage), elle est insérée
    /// à cet endroit précis pour rester cohérente avec ce qu'ares écrit lui-même.
    /// </summary>
    private static void UpdateAresBmlFirmware(
        string bmlPath, string systemKey, string firmwareKey, string value)
    {
        try
        {
            var lines = new List<string>(File.ReadAllLines(bmlPath));
            var (sysIdx, sysEnd, fwIdx, fwEnd) = LocateAresFirmwareBlock(lines, systemKey);
            if (sysIdx < 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BIOS] Ares : bloc '{systemKey}' introuvable dans settings.bml (jamais vu par ares ?)");
                return;
            }

            var newFwLine = $"    {firmwareKey}: {value}";

            if (fwIdx >= 0)
            {
                int keyIdx = -1;
                for (int i = fwIdx + 1; i < fwEnd; i++)
                {
                    var trimmed = lines[i].TrimStart(' ');
                    if (trimmed == firmwareKey || trimmed.StartsWith(firmwareKey + ":"))
                    {
                        keyIdx = i;
                        break;
                    }
                }

                if (keyIdx >= 0) lines[keyIdx] = newFwLine;
                else              lines.Insert(fwEnd, newFwLine);
            }
            else
            {
                // Pas de section Firmware : l'insérer juste après "  Path" (ou "  Path: ..."),
                // qui est toujours le dernier champ avant Firmware dans les blocs existants.
                int pathIdx = -1;
                for (int i = sysIdx + 1; i < sysEnd; i++)
                {
                    var trimmed = lines[i].TrimStart(' ');
                    if (trimmed == "Path" || trimmed.StartsWith("Path:"))
                        pathIdx = i; // dernier "Path" rencontré (au cas où, prudence)
                }
                int insertAt = pathIdx >= 0 ? pathIdx + 1 : sysEnd;
                lines.Insert(insertAt,     newFwLine);
                lines.Insert(insertAt,     "  Firmware");
            }

            File.WriteAllLines(bmlPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BIOS] Erreur UpdateAresBmlFirmware {bmlPath}: {ex.Message}");
        }
    }


    // PCSX2 — reconstruit le 2026-07-24 après test réel utilisateur (voir RESUME_PROJET.md) :
    // le premier correctif (-portable + copie des BIOS + pointeur [Folders] Bios absolu) ne
    // suffisait pas. L'utilisateur a lancé PCSX2 en mode portable manuellement, configuré le
    // BIOS via l'UI (choix "Europe"), puis fourni le contenu généré — ça a révélé 2 éléments
    // qui manquaient :
    //   1. [Folders] Bios doit être RELATIF ("bios", pas le chemin absolu vers le pack Recalbox
    //      partagé) — c'est la valeur que PCSX2 lui-même écrit en mode portable.
    //   2. PCSX2 exige un fichier BIOS EXPLICITEMENT sélectionné via [Filenames] BIOS = <nom
    //      exact> — contrairement à DuckStation/Flycast/melonDS (un simple dossier de
    //      recherche suffit), un dossier "bios" rempli sans cette clé ne suffit pas.
    //
    // [SUITE, 2026-07-24] Un ini "minimal" (juste [Folders]/[Filenames] écrits via
    // UpdateIniValue, sans le reste) ne suffit toujours pas : PCSX2 refuse de le charger
    // ("Settings failed to load, or are the incorrect version. Clicking Yes will reset all
    // settings to defaults.") — il attend visiblement un fichier complet, pas un fragment.
    // Plutôt que de deviner quelles clés sont strictement obligatoires, on part du fichier
    // RÉEL généré par une session PCSX2 portable fonctionnelle chez l'utilisateur (BIOS Europe
    // déjà sélectionné) — embarqué tel quel dans Assets\Pcsx2_PCSX2.ini (DemoBase.App.csproj),
    // copié en 1ère installation (fichier absent) puis complété par les mêmes UpdateIniValue
    // Bios/Filenames qu'avant (redondant avec le contenu déjà correct du template, mais garde
    // le comportement cohérent si jamais le template venait à diverger). Un ini DÉJÀ existant
    // (PCSX2 déjà lancé au moins une fois, éventuellement personnalisé) n'est PAS écrasé — seules
    // les 2 clés Bios/Filenames y sont patchées, comme pour les autres émulateurs de ce fichier.
    private static readonly (string FileName, long Size, uint Crc32)[] Ps2BiosFiles =
    {
        ("ps2-0230a-20080220.bin", 4_194_304, 0x286897C2),
        ("ps2-0230e-20080220.bin", 4_194_304, 0x19EB1081),
        ("ps2-0230h-20080220.bin", 4_194_304, 0x191174D4),
        ("ps2-0230j-20080220.bin", 4_194_304, 0x2912FAA5),
    };

    // Région Europe par défaut — même choix que l'utilisateur a fait manuellement dans l'UI
    // PCSX2 lors de son test ("il faut en choisir un, j'ai pris celui de l'europe").
    private const string Ps2DefaultBiosFileName = "ps2-0230e-20080220.bin";
    private const string Pcsx2IniTemplateAssetName = "Pcsx2_PCSX2.ini";

    private static void ConfigurePcsx2(string biosDir, string emusRoot)
    {
        var pcsx2Root = Path.Combine(emusRoot, "PCSX2");
        if (!Directory.Exists(pcsx2Root)) return; // PCSX2 pas (encore) installé

        // 1. Copier les BIOS PS2 identifiés (taille+CRC32) dans le dossier portable "bios" —
        //    n'existe pas forcément après une simple installation PCSX2 classique, à créer.
        var portableBiosDir = Path.Combine(pcsx2Root, "bios");
        Directory.CreateDirectory(portableBiosDir);
        CopyBiosFilesBySizeCrc32(biosDir, portableBiosDir, Ps2BiosFiles, "PCSX2");

        // 2. inis/PCSX2.ini : copier le template complet SEULEMENT s'il n'existe pas encore
        //    (1ère installation) — jamais écraser un ini déjà présent, potentiellement déjà
        //    personnalisé par l'utilisateur.
        var inisDir = Path.Combine(pcsx2Root, "inis");
        Directory.CreateDirectory(inisDir);
        var iniPath = Path.Combine(inisDir, "PCSX2.ini");
        if (!File.Exists(iniPath))
        {
            var templatePath = Path.Combine(AppContext.BaseDirectory, "Assets", Pcsx2IniTemplateAssetName);
            if (File.Exists(templatePath))
            {
                File.Copy(templatePath, iniPath, overwrite: false);
                System.Diagnostics.Debug.WriteLine($"[BIOS] PCSX2 : template ini déployé → {iniPath}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[BIOS] PCSX2 : template ini introuvable : {templatePath}");
            }
        }

        // 3. Patcher Bios/Filenames/ConfirmShutdown par-dessus (déjà corrects dans le template
        //    si on vient de le copier, mais harmless — et nécessaire si l'ini existait déjà
        //    sans ces clés, ou avec ConfirmShutdown=true réglé manuellement).
        UpdateIniValue(iniPath, "Folders", "Bios", "bios");
        if (File.Exists(Path.Combine(portableBiosDir, Ps2DefaultBiosFileName)))
            UpdateIniValue(iniPath, "Filenames", "BIOS", Ps2DefaultBiosFileName);

        // Désactive la boîte de dialogue de confirmation à la fermeture ("Are you sure you
        // want to exit?") — demande utilisateur du 2026-07-24. Contrairement à DOSBox-X (où le
        // -set équivalent était refusé par l'émulateur lui-même, cf. plus haut dans ce fichier),
        // rien ne laisse penser que ConfirmShutdown poserait un problème similaire : c'est une
        // clé ini standard, dans un fichier dont on sait maintenant qu'il charge correctement.
        UpdateIniValue(iniPath, "UI", "ConfirmShutdown", "false");

        System.Diagnostics.Debug.WriteLine($"[BIOS] PCSX2 configuré (mode portable) → {portableBiosDir}");
    }

    // Flycast : emu.cfg [config] bios_path = <biosDir>
    private static void ConfigureFlycast(string biosDir, string emusRoot)
    {
        var cfgPath = Path.Combine(emusRoot, "Flycast", "emu.cfg");
        if (!File.Exists(cfgPath)) return;
        UpdateIniValue(cfgPath, "config", "bios_path", biosDir + Path.DirectorySeparatorChar);
        System.Diagnostics.Debug.WriteLine($"[BIOS] Flycast configuré → {biosDir}");
    }

    // melonDS : melonDS.ini [Paths] BIOS7Path / BIOS9Path / FirmwarePath
    // Les fichiers DS BIOS sont extraits à plat dans biosDir (ou sous-dossier DS/)
    private static void ConfigureMelonDS(string biosDir, string emusRoot)
    {
        var iniPath = Path.Combine(emusRoot, "melonDS", "melonDS.ini");
        if (!File.Exists(iniPath)) return;

        // Chercher les fichiers BIOS DS dans le dossier BIOS
        var bios7    = FindBiosFile(biosDir, "bios7.bin");
        var bios9    = FindBiosFile(biosDir, "bios9.bin");
        var firmware = FindBiosFile(biosDir, "firmware.bin");

        if (bios7    != null) UpdateIniValue(iniPath, "Paths", "BIOS7Path",    bios7);
        if (bios9    != null) UpdateIniValue(iniPath, "Paths", "BIOS9Path",    bios9);
        if (firmware != null) UpdateIniValue(iniPath, "Paths", "FirmwarePath", firmware);

        if (bios7 != null || bios9 != null || firmware != null)
            System.Diagnostics.Debug.WriteLine($"[BIOS] melonDS configuré → {biosDir}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Encoding.UTF8 (propriété statique .NET) émet un BOM en écriture par défaut
    // (encoderShouldEmitUTF8Identifier=true) — repéré le 2026-07-24 : PCSX2 refuse de charger
    // un PCSX2.ini écrit avec ce BOM ("Settings failed to load, or are the incorrect version",
    // proposant de tout réinitialiser aux valeurs par défaut). Même classe de bug déjà
    // rencontrée pour WinUAE (cfgfile.cpp, cf. commentaire dans WinUAELauncher.cs) — les
    // parseurs ini "maison" de ces émulateurs C/C++ ne gèrent pas le BOM, qui se retrouve
    // alors incorporé à la 1ère clé du fichier. UpdateIniValue est le helper partagé par TOUS
    // les Configure* de ce fichier (DuckStation/PCSX2/Flycast/melonDS) — un seul correctif ici
    // les couvre tous.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Met à jour (ou crée) une clé dans un fichier ini-like.
    /// Si la section n'existe pas, elle est créée à la fin.
    /// Si la clé n'existe pas dans la section, elle est ajoutée.
    /// </summary>
    private static void UpdateIniValue(string iniPath, string section, string key, string value)
    {
        try
        {
            var lines = File.Exists(iniPath)
                ? new List<string>(File.ReadAllLines(iniPath, Utf8NoBom))
                : new List<string>();

            var sectionHeader = $"[{section}]";
            int sectionIdx    = -1;
            int keyIdx        = -1;
            int nextSectionIdx = lines.Count;

            // Trouver la section
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
                    if (eq > 0)
                    {
                        var k = trimmed[..eq].Trim();
                        if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            keyIdx = i;
                        }
                    }
                }
            }

            var newLine = $"{key} = {value}";

            if (keyIdx >= 0)
            {
                // Mettre à jour la ligne existante
                lines[keyIdx] = newLine;
            }
            else if (sectionIdx >= 0)
            {
                // Insérer juste avant la prochaine section (ou à la fin si pas de suivante)
                lines.Insert(nextSectionIdx, newLine);
            }
            else
            {
                // Créer la section à la fin
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                    lines.Add("");
                lines.Add(sectionHeader);
                lines.Add(newLine);
            }

            File.WriteAllLines(iniPath, lines, Utf8NoBom);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BIOS] Erreur UpdateIniValue {iniPath}: {ex.Message}");
        }
    }

    /// <summary>Cherche un fichier BIOS par nom dans biosDir et ses sous-dossiers.</summary>
    private static string? FindBiosFile(string biosDir, string fileName)
    {
        try
        {
            var files = Directory.GetFiles(biosDir, fileName,
                SearchOption.AllDirectories);
            return files.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
