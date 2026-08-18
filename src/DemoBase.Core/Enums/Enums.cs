namespace DemoBase.Core.Enums;

// ReleaseType est une entité en base (DemoBase.Core.Models.ReleaseType).
// CreditRole est stocké comme string libre depuis le dump Demozoo
// (valeurs : "code", "music", "graphics", "font", "text", "other"…)

public enum MediaType
{
    Screenshot,
    Video,
    ModMusic,       // MOD / S3M / IT / XM / FT2
    AudioMusic,     // MP3 / FLAC / WAV / OGG
    Nfo,
    Cover,
    Other
}

public enum EmulatorStatus
{
    Active,
    Inactive,
    NotInstalled
}

public enum EmulatorType
{
    Generic,    // Émulateur générique — formulaire standard
    WinUAE,     // WinUAE — Amiga OCS/ECS/AGA (multi-disques .adf/.dms/.ipf)
    DOSBox,     // MS-DOS
    ViceC64,    // Commodore 64 (VICE, x64sc) — anciennement "VICE" ; renommé en prévision
                // des autres machines VICE ci-dessous (même outil, exécutables séparés)
    Mame,       // Arcade / multi-plateforme
    ScummVM,    // Jeux d'aventure
    Stella,     // Stella    — Atari 2600 VCS
    Hatari,     // Atari ST/STE/TT/Falcon
    Altirra,    // Atari 400/800/XL/XE/5200 (8-bit)
    Cpcec,      // Amstrad CPC 464/664/6128/Plus (CPCEC)
    Zxsec,      // Sinclair Spectrum 48K/128K/+2/+3 (ZXSEC, frère de CPCEC)
    Csfec,      // Commodore 64 (CSFEC, frère de CPCEC)
    Msxec,      // MSX/MSX2/MSX2+ (MSXEC, frère de CPCEC)
    ViceC128,   // Commodore 128 (VICE, x128)
    ViceVic20,  // VIC-20 (VICE, xvic)
    VicePet,    // PET (VICE, xpet)
    ViceC64Dtv, // Commodore 64 DTV (VICE, x64dtv)
    VicePlus4,  // Commodore Plus/4 et C16 (VICE, xplus4 — un seul exécutable, 2 modèles)
    Windows,    // Windows natif (PC) — pas d'émulateur, exécution directe de l'exe extrait
    Tic80,      // TIC-80 — fantasy computer (cartouches .tic)
    MicroW8,       // MicroW8 — fantasy console WebAssembly (cartouches .uw8 / .w8)
    UnrealSpeccy,  // UnrealSpeccy — ZX Spectrum / Pentagon 128 (démos scène)
    EightyOne,     // EightyOne   — ZX-80 / ZX-81 / TS1000
    ZEsarUX,       // ZEsarUX     — Spectrum 48K/128K/Next, ZX80, ZX81 et bien d'autres
    KegaFusion,    // Kega Fusion — Sega (Genesis/MD, Master System, Game Gear, CD, 32X…)
    Browser,       // Browser    — démos HTML5/WebGL/WASM (local ou URL en ligne)
    Java,          // Java       — démos .jar (javaw.exe -jar)
    Fuse,          // Fuse    — ZX Spectrum 16K/48K/128K/Pentagon/Scorpion…
    BlastEm,       // BlastEm   — Sega Genesis/Mega Drive (haute précision, démos scène)
    Arculator,     // Arculator — Acorn Archimedes (RISC OS, ARM2/ARM3)
    PPSSPP,        // PPSSPP    — Sony PlayStation Portable (PSP)
    BlueMSX,       // BlueMSX     — MSX1/2/2+/TurboR, SVI, ColecoVision, SG-1000
    DuckStation,   // DuckStation — Sony PlayStation 1 (PSX)
    PuNES,         // puNES — Nintendo Entertainment System (NES/FDS/NSF)
    Ares,          // ares       — Multi-système : NES/SNES/GB/N64/MD/PCE/MSX/Neo Geo…
    ProSystem,     // ProSystem — Atari 7800 (+ rétrocompat 2600)
    Xenia,         // Xenia         — Microsoft Xbox 360
    CxbxReloaded,  // CXBX-Reloaded — Microsoft Xbox original (OG Xbox)
    AppleWin,      // AppleWin      — Apple II / II+ / IIe / IIe Enhanced
    GSplus,        // GSplus        — Apple IIgs (basé sur KEGS)
    Kegs,          // KEGS32        — Apple IIgs (outil original de Kent Dickey, distinct de GSplus)
    Pemsa,         // Pemsa (pemsa-sdl) — runtime PICO-8 open-source (cartouches .p8 / .p8.png)
    Mesen,         // Mesen / MesenCE — multi-système haute précision (SNES/NES/GB/GBA/PCE/SMS/GG/WS)
    MelonDS,       // melonDS       — Nintendo DS / DSi (cycle-accurate, Wi-Fi)
    Azahar,        // Azahar        — Nintendo 3DS (successeur de Citra : fusion Lime3DS + fork PabloMK7)
    Pcsx2,         // PCSX2         — Sony PlayStation 2
    Trs80gp,       // trs80gp       — TRS-80 Model I/III/4
    Oricutron,     // Oricutron     — Oric-1 / Atmos / Telestrat
    Dolphin,       // Dolphin       — Nintendo GameCube / Wii
    SimCoupe,      // SimCoupe      — SAM Coupé
    Flycast,       // Flycast       — Sega Dreamcast / Naomi / Atomiswave
    JzIntv,        // jzIntv        — Mattel Intellivision
    Dcmoto,        // DCMOTO        — Thomson MO5/MO6/TO7/TO8/TO9
    Xm6TypeG,      // XM6 TypeG     — Sharp X68000
    BeebEm,        // BeebEm        — BBC Micro / Master 128
    SQLux,         // sQLux         — Sinclair QL
    Ryujinx,       // Ryujinx       — Nintendo Switch
    Rpcs3,         // RPCS3         — Sony PlayStation 3
    BigPEmu,       // BigPEmu       — Atari Jaguar / Jaguar CD
    Handy,         // Handy         — Atari Lynx
    GeePee32,      // GeePee32      — GamePark GP32
    Ep128Emu,      // ep128emu      — Enterprise 64/128
    Mz800Emu,      // mz800emu      — Sharp MZ-700 / MZ-800 / MZ-1500
    ColEm,         // ColEm         — ColecoVision
    Ruffle,        // Ruffle        — Adobe Flash (.swf), lecteur standalone open source
}
