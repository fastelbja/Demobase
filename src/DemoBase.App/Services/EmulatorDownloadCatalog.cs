using System.Text.Json.Serialization;

namespace DemoBase.App.Services;

// ─── Stratégie de téléchargement ─────────────────────────────────────────────

public enum DownloadStrategy
{
    /// <summary>GitHub releases API — détecte la dernière version automatiquement.</summary>
    GitHub,
    /// <summary>SourceForge — utilise l'URL /files/latest/download.</summary>
    SourceForge,
    /// <summary>URL directe fixe — version non détectable automatiquement.</summary>
    DirectUrl,
    /// <summary>Parse une page HTML pour trouver le lien de téléchargement
    /// correspondant à AssetPattern. Source = URL de la page.</summary>
    PageScrape,
    /// <summary>Téléchargement manuel requis — Source = URL de la page officielle à ouvrir.</summary>
    ManualDownload,
}

// ─── Entrée du catalogue ──────────────────────────────────────────────────────

public class EmulatorDownloadEntry
{
    /// <summary>Nom du dossier dans Emus/ — doit correspondre à ce que l'utilisateur attend.</summary>
    public string FolderName    { get; init; } = "";
    public string DisplayName   { get; init; } = "";
    public DownloadStrategy Strategy { get; init; }

    /// <summary>
    /// Dossier racine sous lequel FolderName est créé, relatif au dossier de
    /// l'application. "Emus" pour les émulateurs (défaut), "Externals" pour les
    /// outils tiers (ZXTune, RECOIL…) utilisés en coulisses par DemoBase.
    /// </summary>
    public string RootFolder    { get; init; } = "Emus";

    /// <summary>
    /// GitHub : "owner/repo" | SourceForge : "project-name" | DirectUrl : URL complète.
    /// </summary>
    public string Source        { get; init; } = "";

    /// <summary>
    /// Pattern (glob-like) pour sélectionner l'asset Windows parmi les releases GitHub.
    /// Ex: "*windows*x64*.zip"  "*Win64*.zip"
    /// </summary>
    public string AssetPattern  { get; init; } = "";
    /// <summary>Referer HTTP à envoyer pour contourner le hotlink protection (optionnel).</summary>
    public string? Referer      { get; init; }

    /// <summary>Nom de l'exécutable principal (pour vérification post-install).</summary>
    public string ExeName       { get; init; } = "";

    /// <summary>Remarque affichée dans l'UI (ex. "Abandonné 2013").</summary>
    public string? Note         { get; init; }

    /// <summary>Courte description des systèmes émulés, affichée dans la liste
    /// du wizard (ex. "Atari ST/STE/Falcon"). Optionnel.</summary>
    public string? Systems      { get; init; }

    /// <summary>
    /// Force la version affichée (au lieu de la déduire par regex depuis l'URL
    /// via ParseVersionFromUrl). Utile pour DirectUrl quand le nom de fichier
    /// contient une séquence de chiffres parasite AVANT le vrai numéro de
    /// version (ex. "dosbox-x-mingw32_lowend-2026.06.02-portable.zip" — le
    /// regex naïf capturerait "32" de "mingw32" au lieu de "2026.06.02").
    /// </summary>
    public string? VersionOverride { get; init; }
}

// ─── Catalogue complet ────────────────────────────────────────────────────────

public static class EmulatorDownloadCatalog
{
    public static readonly IReadOnlyList<EmulatorDownloadEntry> AllEmulators =
    [
        // ── Altirra (Atari 8-bit) ──────────────────────────────────────────
        // virtualdub.org — scraping non implémenté, URL fixe mise à jour manuellement
        new()
        {
            FolderName   = "Altirra",
            DisplayName  = "Altirra",
            Systems      = "Atari 8-bit",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://www.virtualdub.org/downloads/Altirra-4.40.zip",
            ExeName      = "Altirra64.exe",
            Note         = "URL fixe — vérifier https://www.virtualdub.org/altirra.html pour les mises à jour",
        },

        // ── AppleWin (Apple II) ────────────────────────────────────────────
        new()
        {
            FolderName   = "AppleWin",
            DisplayName  = "AppleWin",
            Systems      = "Apple II",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "AppleWin/AppleWin",
            AssetPattern = "*AppleWin*.zip",
            ExeName      = "AppleWin.exe",
        },

        // ── Arculator (Acorn Archimedes) ───────────────────────────────────
        new()
        {
            FolderName   = "Arculator",
            DisplayName  = "Arculator",
            Systems      = "Acorn Archimedes",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://b-em.bbcmicro.com/arculator/Arculator_V2.2_Windows.zip",
            ExeName      = "arculator.exe",
            Note         = "Version 2.2 — https://b-em.bbcmicro.com/arculator/download.html",
        },

        // ── Ares (multi-système) ───────────────────────────────────────────
        new()
        {
            FolderName   = "Ares",
            DisplayName  = "Ares",
            Systems      = "NES/SNES/N64/GB/MD/Neo Geo…",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "ares-emulator/ares",
            AssetPattern = "ares-windows-x64.zip",
            ExeName      = "ares.exe",
            Note         = "Les builds nightly incluent aussi un zip de symboles de debug (-pdb) — exclu explicitement",
        },
        // ── Ruffle (Adobe Flash .swf) ──────────────────────────────────────
        new()
        {
            FolderName   = "Ruffle",
            DisplayName  = "Ruffle",
            Systems      = "Adobe Flash (.swf)",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "ruffle-rs/ruffle",
            AssetPattern = "ruffle-nightly-*-windows-x86_64.zip",
            ExeName      = "ruffle.exe",
            Note         = "Ruffle ne publie que des builds nightly (pre-releases) — géré par le fallback " +
                            "/releases habituel. Tag de version au format \"nightly-YYYY-MM-DD\" (change tous " +
                            "les jours, donc une mise à jour sera quasi toujours signalée disponible).",
        },
        // ── BlastEm (Sega Genesis/Mega Drive) ─────────────────────────────
        new()
        {
            FolderName   = "Blastem",
            DisplayName  = "BlastEm",
            Systems      = "Sega Genesis/Mega Drive",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://www.retrodev.com/blastem/blastem-win32-0.6.2.zip",
            ExeName      = "blastem.exe",
            Note         = "Version 0.6.2 — https://www.retrodev.com/blastem/",
        },

        // ── BlueMSX (MSX/ColecoVision) ────────────────────────────────────
        new()
        {
            FolderName   = "BlueMsx",
            DisplayName  = "BlueMSX",
            Systems      = "MSX / ColecoVision",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://www.msxblue.com/bluemsx/rel_download/blueMSXv282.zip",
            ExeName      = "blueMSX.exe",
            Note         = "Abandonné depuis 2009 — dernière version 2.8.2",
        },

        // ── CPCec (Amstrad CPC) ────────────────────────────────────────────
        new()
        {
            FolderName   = "CPCec",
            DisplayName  = "CPCec",
            Systems      = "Amstrad CPC",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "http://cngsoft.no-ip.org/cpcec-20241224.zip",
            ExeName      = "cpcec.exe",
            Note         = "URL fixe — vérifier http://cngsoft.no-ip.org/cpcec.htm (lien \"Download the current release\") pour les mises à jour. Inclut aussi ZXSEC, CSFEC, MSXEC.",
        },

        // ── CXBX-Reloaded (Xbox OG) ───────────────────────────────────────
        new()
        {
            FolderName   = "CXBX",
            DisplayName  = "CXBX-Reloaded",
            Systems      = "Xbox (original)",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "Cxbx-Reloaded/Cxbx-Reloaded",
            AssetPattern = "*Release*.zip",
            ExeName      = "cxbxr-ldr.exe",
        },

        // ── DosBox-X (MS-DOS) ──────────────────────────────────────────────
        new()
        {
            FolderName   = "DosBox-X",
            DisplayName  = "DOSBox-X",
            Systems      = "MS-DOS / PC",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://github.com/joncampbell123/dosbox-x/releases/download/dosbox-x-v2026.06.02/dosbox-x-mingw32_lowend-2026.06.02-portable.zip",
            ExeName      = "dosbox-x.exe",
            Note         = "Version 2026.06.02 (branche standard, PAS osfree — émulation MS-DOS intégrée), variante \"lowend\" (mingw32, SDL1) — URL directe vérifiée manuellement. À mettre à jour manuellement à chaque nouvelle release si besoin (changer la date dans le tag ET dans le nom de fichier).",
            VersionOverride = "2026.06.02",
        },

        // ── DuckStation (PlayStation 1) ───────────────────────────────────
        new()
        {
            FolderName   = "Duckstation",
            DisplayName  = "DuckStation",
            Systems      = "PlayStation 1",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "stenzek/duckstation",
            AssetPattern = "*windows*x64*.zip",
            ExeName      = "duckstation-qt-x64-ReleaseLTCG.exe",
        },

        // ── PCSX2 (PlayStation 2) ─────────────────────────────────────────
        new()
        {
            FolderName   = "PCSX2",
            DisplayName  = "PCSX2",
            Systems      = "PlayStation 2",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "PCSX2/pcsx2",
            AssetPattern = "*windows-x64-Qt.7z",  // exclut *-symbols.7z (PDB uniquement)
            ExeName      = "pcsx2-qt.exe",
        },

        // ── trs80gp (TRS-80) ─────────────────────────────────────────────
        new()
        {
            FolderName   = "trs80gp",
            DisplayName  = "trs80gp",
            Systems      = "TRS-80",
            Strategy     = DownloadStrategy.PageScrape,
            Source       = "https://48k.ca/trs80gp.html",  // page contenant le lien de DL
            AssetPattern = "trs80gp-*.zip",                // pattern pour trouver le lien
            ExeName      = "trs80gp.exe",
        },

        // ── Oricutron (Oric-1/Atmos/Telestrat) ───────────────────────────
        new()
        {
            FolderName   = "Oricutron",
            DisplayName  = "Oricutron",
            Systems      = "Oric-1 / Atmos",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://www.emucamp.com/oric/oricutron/windows/Oricutron_win32_v12.zip",
            AssetPattern = "Oricutron_win32_v12.zip",
            ExeName      = "oricutron.exe",
        },

        // ── Dolphin (GameCube / Wii) ─────────────────────────────────────
        new()
        {
            FolderName   = "Dolphin",
            DisplayName  = "Dolphin",
            Systems      = "GameCube / Wii",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://dl.dolphin-emu.org/releases/2512/dolphin-2512-x64.7z",
            AssetPattern = "dolphin-2512-x64.7z",
            ExeName      = "Dolphin.exe",
        },

        // ── SimCoupe (SAM Coupé) ─────────────────────────────────────────
        new()
        {
            FolderName   = "SimCoupe",
            DisplayName  = "SimCoupe",
            Systems      = "SAM Coupé",
            Strategy     = DownloadStrategy.PageScrape,
            Source       = "https://simonowen.com/simcoupe/",
            AssetPattern = "SimCoupe-*-win_x64.zip",
            ExeName      = "SimCoupe.exe",
        },

        // ── Flycast (Dreamcast / Naomi / Atomiswave) ─────────────────────
        new()
        {
            FolderName   = "Flycast",
            DisplayName  = "Flycast",
            Systems      = "Dreamcast / Naomi",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "flyinghead/flycast",
            AssetPattern = "flycast-win64-*.zip",
            ExeName      = "flycast.exe",
        },

        // ── jzIntv (Intellivision) ────────────────────────────────────────
        new()
        {
            FolderName   = "jzIntv",
            DisplayName  = "jzIntv",
            Systems      = "Mattel Intellivision",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "http://spatula-city.org/~im14u2c/intv/dl/jzintv-20200712-win32-sdl2.zip",
            AssetPattern = "jzintv-20200712-win32-sdl2.zip",
            ExeName      = "jzIntv.exe",
        },

        // ── DCMOTO (Thomson MO5/TO7) ─────────────────────────────────────
        new()
        {
            FolderName   = "DCMOTO",
            DisplayName  = "DCMOTO",
            Systems      = "Thomson MO5/TO7/TO8",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://dcmoto.pages-perso.free.fr/emulateur/prog/dcmoto_20260114.zip",
            AssetPattern = "dcmoto_20260114.zip",
            Referer      = "https://dcmoto.pages-perso.free.fr/emulateur/index.html",
            ExeName      = "dcmoto-64_*.exe",
        },

        // ── XM6 TypeG (Sharp X68000) ─────────────────────────────────────
        new()
        {
            FolderName   = "XM6TypeG",
            DisplayName  = "XM6 TypeG",
            Systems      = "Sharp X68000",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "http://retropc.net/pi/xm6/xm6_typeg_338_20260223.zip",
            AssetPattern = "xm6_typeg_338_20260223.zip",
            ExeName      = "xm6g.exe",
        },

        // ── BeebEm (BBC Micro) ───────────────────────────────────────────
        new()
        {
            FolderName   = "BeebEm",
            DisplayName  = "BeebEm",
            Systems      = "BBC Micro",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://codeberg.org/chrisn/beebem-windows/releases/download/4.21/BeebEm.zip",
            AssetPattern = "BeebEm.zip",
            ExeName      = "BeebEm.exe",
        },

        // ── sQLux (Sinclair QL) ──────────────────────────────────────────
        new()
        {
            FolderName   = "sQLux",
            DisplayName  = "sQLux",
            Systems      = "Sinclair QL",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "SinclairQL/sQLux",
            AssetPattern = "sqlux-x86_64-mingw64-*.zip",
            ExeName      = "sqlux.exe",
        },


        // ── EightyOne (ZX80/ZX81) ─────────────────────────────────────────
        new()
        {
            FolderName   = "EightyOne",
            DisplayName  = "EightyOne",
            Systems      = "ZX80 / ZX81",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://master.dl.sourceforge.net/project/eightyone-sinclair-emulator/EightyOne%20V1.41.zip?viasf=1",
            ExeName      = "EightyOne.exe",
            Note         = "Version 1.41 — master.dl.sourceforge.net contourne la page HTML de sélection de mirroir",
        },

        // ── Fuse (ZX Spectrum Windows port) ───────────────────────────────
        new()
        {
            FolderName   = "Fuse",
            DisplayName  = "Fuse",
            Systems      = "ZX Spectrum",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://master.dl.sourceforge.net/project/fuse-emulator/fuse/1.9.0/fuse-1.9.0-win32.zip?viasf=1",
            ExeName      = "fuse.exe",
            Note         = "Version 1.9.0 — master.dl.sourceforge.net contourne la page HTML de sélection de mirroir",
        },

        // ── Kega Fusion (Sega Genesis/MD/CD/32X) ──────────────────────────
        new()
        {
            FolderName   = "Fusion",
            DisplayName  = "Kega Fusion",
            Systems      = "Sega MD/CD/32X/SMS/GG",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://www.carpeludum.com/download/Fusion364.zip",
            ExeName      = "Fusion.exe",
            Note         = "Abandonné depuis 2014 — dernière version 3.64 — https://www.carpeludum.com/kega-fusion/",
        },

        // ── GSplus (Apple IIgs) ────────────────────────────────────────────
        new()
        {
            FolderName   = "GSPlus",
            DisplayName  = "GSplus",
            Systems      = "Apple IIgs",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "digarok/gsplus",
            AssetPattern = "*win*.zip",
            ExeName      = "GSplus.exe",
            Note         = "Projet réactivé en 2026 (CI GitHub Actions) — anciennement distribué uniquement via apple2.gs/plus",
        },

        // ── Hatari (Atari ST/STE/TT/Falcon) ──────────────────────────────
        new()
        {
            FolderName   = "Hatari",
            DisplayName  = "Hatari",
            Systems      = "Atari ST/STE/Falcon",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://master.dl.sourceforge.net/project/hatari/hatari/2.6.1/hatari-2.6.1_windows64.zip?viasf=1",
            ExeName      = "hatari.exe",
            Note         = "Version 2.6.1 — le repo GitHub n'est qu'un miroir source, les releases officielles sont sur framagit.org",
        },

        // ── KEGS (Apple IIgs) ──────────────────────────────────────────────
        new()
        {
            FolderName   = "Kegs",
            DisplayName  = "KEGS32",
            Systems      = "Apple IIgs",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://kegs.sourceforge.net/kegs.1.38.zip",
            ExeName      = "kegswin.exe",
            Note         = "Version 1.38 — archive combinée Windows 10+/macOS/Linux/sources, lien statique direct (pas de page de sélection de mirroir)",
        },

        // ── MicroW8 (fantasy console) ─────────────────────────────────────
        new()
        {
            FolderName   = "Microw8",
            DisplayName  = "MicroW8",
            Systems      = "MicroW8 (fantasy console)",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "exoticorn/microw8",
            AssetPattern = "*windows*.zip",
            ExeName      = "uw8.exe",
        },

        // ── Pemsa / pemsa-sdl (runtime PICO-8) ────────────────────────────
        new()
        {
            FolderName   = "Pemsa",
            DisplayName  = "Pemsa (PICO-8)",
            Systems      = "PICO-8",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "egordorichev/pemsa-sdl",
            AssetPattern = "*win*",
            ExeName      = "pemsa.exe",
            Note         = "Frontend PC = pemsa-sdl (le dépôt 'pemsa' seul n'est que le runtime). "
                         + "Dernière release v0.2 (avr. 2022) — asset Windows À VÉRIFIER : le pattern "
                         + "'*win*' cible un binaire Windows dans la release, sinon compilation depuis "
                         + "les sources (CMake + SDL2) ou récupération sur itch.io. Le zip Windows doit "
                         + "inclure SDL2.dll à côté de pemsa.exe.",
        },

        // ── Mesen / MesenCE (multi-système haute précision) ───────────────
        new()
        {
            FolderName   = "Mesen",
            DisplayName  = "Mesen (MesenCE)",
            Systems      = "NES/SNES/GB/GBA/PCE/SMS",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "nesdev-org/MesenCE",
            AssetPattern = "Mesen_*_Windows.zip",
            ExeName      = "Mesen.exe",
            Note         = "MesenCE = fork communautaire maintenu de Mesen 2 (le dépôt SourMesen/Mesen2 "
                         + "est archivé). Le build « _Windows.zip » est auto-contenu (ne nécessite PAS "
                         + "l'installation de .NET, contrairement aux variantes _net8.0). Mesen détecte "
                         + "automatiquement le système depuis la ROM — aucun réglage de machine requis.",
        },

        // ── melonDS (Nintendo DS / DSi) ───────────────────────────────────
        new()
        {
            FolderName   = "melonDS",
            DisplayName  = "melonDS (Nintendo DS)",
            Systems      = "Nintendo DS / DSi",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "melonDS-emu/melonDS",
            AssetPattern = "melonDS-windows-x86_64.zip",
            ExeName      = "melonDS.exe",
            Note         = "Émulateur Nintendo DS/DSi haute précision (Wi-Fi, DSi). Le « direct boot » "
                         + "lance les ROMs sans BIOS ; le « firmware boot » nécessite un dump BIOS/firmware "
                         + "d'une vraie DS.",
        },

        // ── Azahar (Nintendo 3DS) ─────────────────────────────────────────
        new()
        {
            FolderName   = "Azahar",
            DisplayName  = "Azahar (Nintendo 3DS)",
            Systems      = "Nintendo 3DS",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "azahar-emu/azahar",
            AssetPattern = "azahar-*-windows-msvc.zip",
            ExeName      = "azahar.exe",
            Note         = "Successeur maintenu de Citra (fusion Lime3DS + fork PabloMK7 ; le dépôt Citra "
                         + "est fermé). Build zip MSVC (variante MSYS2 aussi dispo). Formats : .cci, .cxi, "
                         + ".cia, .3dsx, .z3ds ; pour du .3ds non chiffré, le renommer en .cci. Certains "
                         + "jeux commerciaux exigent les clés/AES du système.",
        },

        // ── PPSSPP (PlayStation Portable) ─────────────────────────────────
        new()
        {
            FolderName   = "PPSSPP",
            DisplayName  = "PPSSPP",
            Systems      = "PlayStation Portable",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "hrydgard/ppsspp",
            AssetPattern = "*PPSSPPWindows*.zip",
            ExeName      = "PPSSPPWindows64.exe",
            Note         = "L'exclusion globale des builds ARM64 (cf. EmulatorInstallerService) évite de récupérer PPSSPPWindowsARM64.zip à la place de la version x64",
        },

        // ── ProSystem (Atari 7800) ─────────────────────────────────────────
        new()
        {
            FolderName   = "ProSystem",
            DisplayName  = "ProSystem",
            Systems      = "Atari 7800",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "gstanton/ProSystem1_3",
            AssetPattern = "*.zip",
            ExeName      = "ProSystem.exe",
        },

        // ── puNES (NES) ────────────────────────────────────────────────────
        new()
        {
            FolderName   = "puNes",
            DisplayName  = "puNES",
            Systems      = "NES / Famicom",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "punesemu/puNES",
            AssetPattern = "*Win64*.zip",
            ExeName      = "punes32.exe",
        },


        // ── Stella (Atari 2600) ────────────────────────────────────────────
        new()
        {
            FolderName   = "Stella",
            DisplayName  = "Stella",
            Systems      = "Atari 2600",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "stella-emu/stella",
            AssetPattern = "*windows-x64.zip",
            ExeName      = "Stella.exe",
        },

        // ── TIC-80 (fantasy computer) ─────────────────────────────────────
        new()
        {
            FolderName   = "TIC80",
            DisplayName  = "TIC-80",
            Systems      = "TIC-80 (fantasy computer)",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "nesbox/TIC-80",
            AssetPattern = "*windows*.zip",
            ExeName      = "tic80.exe",
        },

        // ── UnrealSpeccy (ZX Spectrum) ────────────────────────────────────
        // Strategy/Source/AssetPattern ci-dessous ne sont PLUS utilisés pour le
        // téléchargement réel (conservés pour affichage/référence) : le seul dépôt GitHub
        // disponible (djdron/UnrealSpeccyP) a abandonné le build "classique" 0.37.9 (celui
        // compatible avec -i/notre format ini) au profit d'un portage SDL2 récent dont le
        // support de -i n'est pas garanti — constaté : l'émulateur ne répondait plus du tout
        // aux arguments après un re-téléchargement via ce chemin. EmulatorInstallerService.
        // InstallAsync court-circuite donc ce catalogue pour FolderName == "Unreal Speccy" et
        // installe systématiquement le build classique hébergé sur le site DemoBase
        // (UnrealSpeccyClassicBuildService, DBSetup\Extras\Unreal Speccy Classic.zip).
        new()
        {
            FolderName   = "Unreal Speccy",
            DisplayName  = "UnrealSpeccy Portable",
            Systems      = "ZX Spectrum / Pentagon",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "djdron/UnrealSpeccyP",
            AssetPattern = "*win32*.zip",
            ExeName      = "unreal_speccy_portable.exe",
            Note         = "Installé depuis le site DemoBase (build classique 0.37.9), pas depuis GitHub — le dépôt GitHub ne propose plus qu'un portage SDL2 incompatible avec -i. Voir UnrealSpeccyClassicBuildService.",
        },

        // ── VICE (Commodore C64/C128/VIC20/PET/Plus4) ────────────────────
        new()
        {
            FolderName   = "Vice",
            DisplayName  = "VICE",
            Systems      = "Commodore C64/C128/VIC20/PET",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://master.dl.sourceforge.net/project/vice-emu/releases/binaries/windows/GTK3VICE-3.10-win64.zip?viasf=1",
            ExeName      = "x64sc.exe",
            Note         = "Version 3.10 (GTK3, win64) — contient tous les émulateurs VICE",
        },

        // ── WinUAE (Amiga) ────────────────────────────────────────────────
        new()
        {
            FolderName   = "WinUAE",
            DisplayName  = "WinUAE",
            Systems      = "Amiga",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://download.abime.net/winuae/releases/WinUAE6030_x64.zip",
            ExeName      = "winuae64.exe",
            Note         = "URL fixe 6.0.3 — vérifier https://www.winuae.net/download/ pour les mises à jour",
        },

        // ── Xenia Canary (Xbox 360) ────────────────────────────────────────
        new()
        {
            FolderName   = "Xenia",
            DisplayName  = "Xenia Canary",
            Systems      = "Xbox 360",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "xenia-canary/xenia-canary-releases",
            AssetPattern = "xenia_canary.zip",
            ExeName      = "xenia_canary.exe",
            Note         = "Releases déplacées de xenia-canary/xenia-canary vers ce repo",
        },

        // RPCS3 (PlayStation 3) retiré le 2026-07-24 (consoles de dernière génération,
        // hors périmètre DemoBase) — cf. commentaire dans EmulatorSeedCatalog.cs.

        // ── ColEm (ColecoVision) ─────────────────────────────────────────────
        new()
        {
            FolderName   = "ColEm",
            DisplayName  = "ColEm",
            Systems      = "ColecoVision",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://fms.komkon.org/ColEm/ColEm56-Windows-bin.zip",
            ExeName      = "ColEm.exe",
            Note         = "v5.6 — BIOS requis : COLEM.ROM dans le dossier ColEm",
        },

                // ── BigPEmu (Atari Jaguar / Jaguar CD) ───────────────────────────────
        // URL directe : le site officiel est instable (timeouts, 4xx fréquents).
        // Pattern d'URL : richwhitehouse.com/jaguar/builds/BigPEmu_v{version}.zip
        // Mettre à jour la Source lors d'une nouvelle version.
        // Dernière version vérifiée : 1.221 (15 mai 2026)
        new()
        {
            FolderName   = "BigPEmu",
            DisplayName  = "BigPEmu",
            Systems      = "Atari Jaguar / Jaguar CD",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://www.richwhitehouse.com/jaguar/builds/BigPEmu_v1221.zip",
            ExeName      = "BigPEmu.exe",
            Note         = "v1.221 — màj : richwhitehouse.com/jaguar/index.php?content=download",
        },

        // ── Handy (Atari Lynx) ────────────────────────────────────────────────
        // Distribué sur SourceForge (plus maintenu, dernière version 0.98b).
        // master.dl.sourceforge.net contourne la page HTML de sélection de miroir.
        // BIOS requis : lynxboot.img dans le dossier de Handy.
        new()
        {
            FolderName   = "Handy",
            DisplayName  = "Handy",
            Systems      = "Atari Lynx",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://master.dl.sourceforge.net/project/handy/handy/Handy%200.95/Handy-0.95.zip?viasf=1",
            ExeName      = "handy.exe",
            Note         = "v0.95 — https://sourceforge.net/projects/handy/",
        },

        // ── gp32emu (GamePark GP32) ──────────────────────────────────────────
        // gp32emu (gameblabla) : émulateur GP32 récent, plus précis que GeePee32.
        // https://github.com/gameblabla/gp32emu/releases
        // Les assets Windows de la release sont nommés *Win64*.zip (à vérifier).
        new()
        {
            FolderName   = "GeePee32",
            DisplayName  = "gp32emu",
            Systems      = "GamePark GP32",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "gameblabla/gp32emu",
            AssetPattern = "GP32emu-win64.exe",
            ExeName      = "GP32emu-win64.exe",
            Note         = "Préféré à GeePee32 — https://github.com/gameblabla/gp32emu/releases",
        },

        // ── GeePee32 legacy (GamePark GP32) ──────────────────────────────────
        // Fallback si gp32emu n'est pas disponible — v0.44 de Tim Schuerewegen.
        new()
        {
            FolderName   = "GeePee32_legacy",
            DisplayName  = "GeePee32 (legacy)",
            Systems      = "GamePark GP32",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://www.schuerewegen.tk/download/geepee32_044_win32_directx.zip",
            ExeName      = "geepee32.exe",
            Note         = "v0.44 — fallback si gp32emu non disponible",
        },

        // ── ep128emu (Enterprise 64/128) ─────────────────────────────────────────
        // 2026-08-04, demande utilisateur ("peux tu voir si peux implementer le
        // download de l'émulateur ep128emu") : recherche faite, mais chaque
        // release Windows du dépôt actif (github.com/istvan-v/ep128emu/releases,
        // dernière version 2.0.11.2), sans exception depuis 2016, ne distribue
        // qu'un installeur NSIS auto-exécutable (ex. ep128emu-2.0.11.1-x64.exe) —
        // PAS un ZIP/7z portable. Un NSIS n'est pas une archive 7-Zip (format de
        // conteneur différent, aucune signature 7z à trouver), donc
        // ExtractSevenZipFlat (qui gère déjà les SFX 7-Zip comme MAME) ne peut pas
        // s'en servir. Le lien SourceForge donné par l'utilisateur
        // (sourceforge.net/projects/ep128emu) est par ailleurs un ancien miroir à
        // l'arrêt depuis 2017, qui n'héberge que du code source, aucun binaire.
        // 2026-08-04 (suite), retour utilisateur ("inutile de l'avoir dans la
        // liste de download si c'est manuel") : entrée ManualDownload ajoutée puis
        // retirée — ManualDownload se contente d'ouvrir un lien dans le navigateur
        // (aucun téléchargement ni extraction réels), ce qui n'apporte rien de
        // plus qu'un simple commentaire ici. L'utilisateur doit installer
        // manuellement depuis https://github.com/istvan-v/ep128emu/releases puis
        // configurer le chemin de ep128emu.exe dans DemoBase (EmulatorType
        // .Ep128Emu est déjà seedé en base, cf. EmulatorSeedCatalog.cs).

        // ── ZEsarUX (ZX Spectrum/etc.) ────────────────────────────────────────
        new()
        {
            FolderName   = "ZEsarUX",
            DisplayName  = "ZEsarUX",
            Systems      = "ZX Spectrum / ZX81 / Next",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "chernandezba/zesarux",
            AssetPattern = "*Windows*.zip",
            ExeName      = "zesarux.exe",
        },
    ];

    // ─── Outils tiers ("Externals") ────────────────────────────────────────────
    // Pas des émulateurs au sens strict — des outils en coulisses utilisés par
    // DemoBase lui-même : ZXTune/UADE pour la restitution audio de formats
    // exotiques (TrackerPlayer.Core/Players/ExternalPlayers.cs), RECOIL pour
    // l'affichage des formats graphiques de vieux ordinateurs. Téléchargés dans
    // Externals/ (RootFolder), pas Emus/.
    public static readonly IReadOnlyList<EmulatorDownloadEntry> AllExternals =
    [
        // ── ZXTune : retiré de ce catalogue le 2026-08-06 ───────────────────
        // Avant ce correctif : téléchargeait zxtune123.exe (build r5100 mingw
        // x86_64, storage.zxtune.ru) dans Externals/ZXTune/ — process externe
        // lancé en ligne de commande par TrackerPlayer pour les formats
        // chiptune exotiques (AHX, SAP, etc.), avec génération d'un fichier
        // WAV temporaire à chaque lecture ET à chaque sondage de subsong.
        //
        // Retour utilisateur : "j'ai réussi à compiler une DLL pour utiliser
        // zxtune sans externals [...] il faudra aussi penser à l'enlever des
        // externals". ZXTunePlayer utilise désormais zxtune.dll, un pont
        // natif P/Invoke compilé par l'utilisateur (cf.
        // TrackerPlayer.Core/Players/ZxTuneNative.cs), à placer manuellement
        // à côté de l'exécutable (ou dans Externals/, cf. App.xaml.cs/
        // ConfigureExternalPaths) — comme libopenmpt.dll. Plus de process
        // externe, plus de fichier WAV temporaire, découverte des subsongs
        // instantanée. Aucun mécanisme de téléchargement automatique n'a de
        // sens ici : la DLL est un artefact compilé par l'utilisateur
        // lui-même (pas un binaire officiel distribué en ligne), d'où la
        // suppression pure et simple de cette entrée plutôt qu'un remplacement
        // par une entrée ManualDownload (cf. le retrait similaire de l'entrée
        // ep128emu le même jour : "inutile de l'avoir dans la liste de
        // download si c'est manuel").

        // ── RECOIL (affichage de formats graphiques rétro) ─────────────────
        new()
        {
            FolderName   = "RECOIL",
            DisplayName  = "RECOIL",
            Systems      = "Retro graphics viewer",
            RootFolder   = "Externals",
            Strategy     = DownloadStrategy.DirectUrl,
            Source       = "https://master.dl.sourceforge.net/project/recoil/recoil/6.4.5/recoil-6.4.5-win64.zip?viasf=1",
            ExeName      = "recoil2png.exe",
            Note         = "Version 6.4.5 (win64) — convertisseur recoil2png pour les formats graphiques rétro",
        },

        // ── UADE : retiré de ce catalogue le 2026-08-06 ─────────────────────
        // Avant ce correctif : téléchargeait uade123.exe (build Cygwin 2.11,
        // zakalwe.fi) dans Externals/UADE/ — process externe streaming du PCM
        // brut sur stdout (`-e raw -f - --stderr -1 -s N --disable-timeouts`),
        // avec copies de fichiers compagnons (TFMX mdat/smpl, Thomas Hermann
        // thm/smp, Dirk Bialluch tpu/smp) renommés par GUID pour éviter les
        // collisions entre subsongs.
        //
        // Retour utilisateur : "j'ai fait de même avec uade. j'ai crée une
        // dll." UadePlayer utilise désormais libuade.dll + uadecore.exe, un
        // pont natif P/Invoke compilé par l'utilisateur (cf.
        // TrackerPlayer.Core/Players/UadeNative.cs), toujours à placer dans
        // Externals/UADE/ (aux côtés des ressources UADE existantes —
        // eagleplayer.conf/uaerc/score/players/, elles restent nécessaires)
        // mais plus via ce catalogue de téléchargement automatique : comme
        // pour zxtune.dll et pour la même raison que le retrait de l'entrée
        // ep128emu (2026-08-04, "inutile de l'avoir dans la liste de download
        // si c'est manuel"), libuade.dll/uadecore.exe sont des artefacts
        // compilés par l'utilisateur lui-même, pas des binaires officiels
        // distribués en ligne — aucun mécanisme de "téléchargement", même
        // manuel, n'a de sens ici.

        // ── JRE (Java Runtime, dédié aux démos .jar) ────────────────────────
        // Eclipse Temurin 21 LTS, build officiel distribué en releases GitHub par
        // le projet Adoptium — permet à DemoBase de lancer les démos Java sans
        // dépendre du Java installé (ou pas) sur le poste, ni jamais y toucher.
        // Même principe que ZXTune/UADE : extrait à plat dans Externals/JRE/, et
        // toujours prioritaire sur un java système (cf. JavaLauncher.cs et
        // App.xaml.cs/ConfigureExternalPaths). Le zip Windows d'Adoptium contient
        // un seul dossier racine (ex. "jdk-21.0.x+y-jre/") qui est retiré par
        // l'extraction "à plat" habituelle → bin/javaw.exe se retrouve bien à
        // Externals/JRE/bin/javaw.exe.
        new()
        {
            FolderName   = "JRE",
            DisplayName  = "JRE (Eclipse Temurin 21)",
            Systems      = "Démos Java (.jar)",
            RootFolder   = "Externals",
            Strategy     = DownloadStrategy.GitHub,
            Source       = "adoptium/temurin21-binaries",
            AssetPattern = "OpenJDK21U-jre_x64_windows_hotspot_*.zip",
            ExeName      = "javaw.exe",
            Note         = "JRE portable (pas de JDK complet) — Java 21 LTS, suffisant pour la quasi-totalité des démos Java de la scène.",
        },
    ];
}

// ─── Version installée (stockée dans Emus/versions.json) ─────────────────────

public class InstalledEmulatorVersion
{
    [JsonPropertyName("version")]       public string  Version       { get; set; } = "";
    [JsonPropertyName("installedAt")]   public DateTime InstalledAt  { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("lastChecked")]   public DateTime LastChecked  { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("downloadUrl")]   public string  DownloadUrl   { get; set; } = "";
}
