using DemoBase.Core.Enums;

namespace DemoBase.App.Services;

/// <summary>
/// Une entrée du seed : un émulateur "connu" de DemoBase, avec son dossier
/// d'installation (relatif à Emus/) et les noms d'exécutable possibles à
/// rechercher dedans (recherche récursive insensible à la casse — tolère que
/// l'archive extraite ait une structure de sous-dossiers légèrement différente
/// d'une version à l'autre).
/// </summary>
public record EmulatorSeedEntry(
    EmulatorType Type,
    string       DefaultName,
    string?      FolderName,
    string[]     ExeCandidates);

/// <summary>
/// Catalogue canonique de tous les émulateurs gérés par DemoBase.
///
/// L'Id en base est TOUJOURS (int)EmulatorType — pas un AUTOINCREMENT classique.
/// C'est ce qui garantit l'exigence "même Id qu'il soit installé ou non" : la
/// valeur de l'enum est stable par construction (définie dans le code, jamais
/// dépendante de l'ordre de création), contrairement à un Id auto-incrémenté qui
/// dépendrait de quels émulateurs l'utilisateur a créés et dans quel ordre.
///
/// Generic (0) est volontairement exclu : c'est un type "aucun profil
/// particulier", pas un émulateur à proprement parler.
/// </summary>
public static class EmulatorSeedCatalog
{
    public static readonly IReadOnlyList<EmulatorSeedEntry> All =
    [
        new(EmulatorType.WinUAE,        "WinUAE",            "WinUAE",        ["winuae64.exe", "winuae.exe"]),
        new(EmulatorType.DOSBox,        "DOSBox-X",          "DosBox-X",      ["dosbox-x.exe"]),
        new(EmulatorType.ViceC64,       "VICE C64",          "Vice",          ["x64sc.exe"]),
        new(EmulatorType.Stella,        "Stella",            "Stella",        ["Stella.exe"]),
        new(EmulatorType.Hatari,        "Hatari",            "Hatari",        ["hatari.exe"]),
        new(EmulatorType.Altirra,       "Altirra",           "Altirra",       ["Altirra64.exe", "Altirra.exe"]),
        new(EmulatorType.Cpcec,         "CPCEC",             "CPCec",         ["CPCEC.EXE", "cpcec.exe"]),
        new(EmulatorType.ViceC128,      "VICE C128",         "Vice",          ["x128.exe"]),
        new(EmulatorType.ViceVic20,     "VICE VIC-20",       "Vice",          ["xvic.exe"]),
        new(EmulatorType.VicePet,       "VICE PET",          "Vice",          ["xpet.exe"]),
        new(EmulatorType.ViceC64Dtv,    "VICE C64-DTV",      "Vice",          ["x64dtv.exe"]),
        new(EmulatorType.VicePlus4,     "VICE C16+4",        "Vice",          ["xplus4.exe"]),
        new(EmulatorType.Windows,       "Windows",           null,            []),
        new(EmulatorType.Tic80,         "TIC-80",            "TIC80",         ["tic80.exe"]),
        new(EmulatorType.MicroW8,       "MicroW8",           "Microw8",       ["uw8.exe", "microw8.exe", "microm8.exe"]),
        new(EmulatorType.UnrealSpeccy,  "UnrealSpeccy Portable", "Unreal Speccy", ["unreal_speccy_portable.exe"]),
        new(EmulatorType.EightyOne,     "EightyOne",         "EightyOne",     ["EightyOne.exe"]),
        new(EmulatorType.ZEsarUX,       "ZEsarUX",           "ZEsarUX",       ["zesarux.exe"]),
        new(EmulatorType.KegaFusion,    "Kega Fusion",       "Fusion",        ["Fusion.exe"]),
        new(EmulatorType.Browser,       "Browser",           null,            []),
        new(EmulatorType.Java,          "Java",              null,            []),
        new(EmulatorType.Fuse,          "Fuse",              "Fuse",          ["fuse.exe"]),
        new(EmulatorType.BlastEm,       "BlastEm",           "Blastem",       ["blastem.exe"]),
        new(EmulatorType.Arculator,     "Arculator",         "Arculator",     ["arculator.exe", "Arculator.exe"]),
        new(EmulatorType.PPSSPP,        "PPSSPP",            "PPSSPP",        ["PPSSPPWindows64.exe", "PPSSPPWindows.exe"]),
        new(EmulatorType.BlueMSX,       "BlueMSX",           "BlueMsx",       ["blueMSX.exe"]),
        new(EmulatorType.DuckStation,   "DuckStation",       "Duckstation",   ["duckstation-qt-x64-ReleaseLTCG.exe"]),
        new(EmulatorType.PuNES,         "puNES",             "puNes",         ["punes32.exe", "punes64.exe"]),
        new(EmulatorType.Ares,          "Ares",              "Ares",          ["ares.exe"]),
        new(EmulatorType.Ruffle,        "Ruffle",            "Ruffle",        ["ruffle.exe"]),
        new(EmulatorType.Mesen,         "Mesen",             "Mesen",         ["Mesen.exe"]),
        new(EmulatorType.MelonDS,       "melonDS",           "melonDS",       ["melonDS.exe"]),
        new(EmulatorType.Azahar,        "Azahar",            "Azahar",        ["azahar.exe"]),
        new(EmulatorType.ProSystem,     "ProSystem",         "ProSystem",     ["ProSystem.exe"]),
        new(EmulatorType.Xenia,         "Xenia Canary",      "Xenia",         ["xenia_canary.exe", "xenia.exe"]),
        new(EmulatorType.CxbxReloaded,  "CXBX-Reloaded",     "CXBX",          ["cxbxr-ldr.exe", "cxbx.exe"]),
        new(EmulatorType.AppleWin,      "AppleWin",          "AppleWin",      ["AppleWin.exe", "AppleWin-x64.exe"]),
        new(EmulatorType.GSplus,        "GSplus",            "GSPlus",        ["GSplus.exe", "gsplus.exe"]),
        new(EmulatorType.Kegs,          "KEGS32",            "Kegs",          ["kegswin.exe"]),
        new(EmulatorType.Pemsa,         "Pemsa (PICO-8)",    "Pemsa",         ["pemsa.exe"]),
        new(EmulatorType.Pcsx2,         "PCSX2",             "PCSX2",         ["pcsx2-qt.exe"]),
        new(EmulatorType.Trs80gp,       "trs80gp",           "trs80gp",       ["trs80gp.exe"]),
        new(EmulatorType.Oricutron,     "Oricutron",         "Oricutron",     ["oricutron.exe"]),
        new(EmulatorType.Dolphin,       "Dolphin",           "Dolphin",       ["Dolphin.exe"]),
        new(EmulatorType.SimCoupe,      "SimCoupe",          "SimCoupe",      ["SimCoupe.exe"]),
        new(EmulatorType.Flycast,       "Flycast",           "Flycast",       ["flycast.exe"]),
        new(EmulatorType.JzIntv,        "jzIntv",            "jzIntv",        ["jzIntv.exe", "jzintv.exe"]),
        new(EmulatorType.Dcmoto,        "DCMOTO",            "DCMOTO",        ["dcmoto-64_*.exe", "dcmoto-32_*.exe", "dcmoto.exe"]),
        new(EmulatorType.Xm6TypeG,      "XM6 TypeG",         "XM6TypeG",      ["xm6g.exe"]),
        new(EmulatorType.BeebEm,        "BeebEm",            "BeebEm",        ["BeebEm.exe"]),
        new(EmulatorType.SQLux,         "sQLux",             "sQLux",         ["sqlux.exe"]),
        // Ryujinx (Nintendo Switch) et RPCS3 (PlayStation 3) retirés le 2026-07-24 à la
        // demande de l'utilisateur (consoles de dernière génération, hors périmètre
        // DemoBase). EmulatorType.Ryujinx/.Rpcs3 restent dans l'enum (Enums.cs) — ne
        // JAMAIS les supprimer ni les réutiliser : Id BDD = (int)EmulatorType, un retrait
        // décalerait les Id de toutes les valeurs suivantes pour les installations
        // existantes. Simplement plus seedés/téléchargeables/lançables désormais.
        new(EmulatorType.BigPEmu,       "BigPEmu",           "BigPEmu",       ["BigPEmu.exe"]),
        new(EmulatorType.Handy,         "Handy",             "Handy",         ["handy.exe"]),
        new(EmulatorType.GeePee32,      "gp32emu / GeePee32","GeePee32",      ["GP32emu-win64.exe", "gp32emu.exe", "geepee32.exe"]),
        new(EmulatorType.Ep128Emu,       "ep128emu",          "ep128emu",      ["ep128emu.exe"]),
        new(EmulatorType.Mz800Emu,       "mz800emu",          "mz800emu",      ["mz800emu.exe", "mz700emu-pal.exe", "mz700emu-ntsc.exe", "mz1500emu.exe"]),
        new(EmulatorType.ColEm,          "ColEm",             "ColEm",         ["ColEm.exe"]),
    ];
}
