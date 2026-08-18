using DemoBase.Core.Enums;

namespace DemoBase.App.Services;

/// <summary>Un choix de type d'émulateur présenté dans le sélecteur de l'UI.</summary>
public record EmulatorTypeOption(EmulatorType Type, string Label);

/// <summary>
/// Source unique de la liste des types d'émulateur proposés à l'ajout/édition
/// d'un émulateur (ComboBox de <c>EmulatorSettingsView.xaml</c>).
///
/// La liste est GÉNÉRÉE depuis <see cref="EmulatorType"/> via
/// <c>Enum.GetValues</c> : tout nouveau membre de l'enum apparaît donc
/// automatiquement dans le sélecteur, sans édition XAML — c'est ce qui évite le
/// bug historique où un type ajouté au code (ex. Pemsa, Kegs) restait invisible
/// parce que la liste XAML était écrite en dur.
///
/// <see cref="_labels"/> ne sert QU'À l'affichage (libellé + emoji). Si un type
/// n'y figure pas, on retombe sur le nom brut de l'enum : le type reste
/// sélectionnable même si personne n'a pensé à lui donner un joli libellé.
/// </summary>
public static class EmulatorTypeCatalog
{
    private static readonly Dictionary<EmulatorType, string> _labels = new()
    {
        [EmulatorType.Generic]      = "🔧 Générique",
        [EmulatorType.WinUAE]       = "🖥 WinUAE (Amiga — multi-disques .adf/.dms)",
        [EmulatorType.DOSBox]       = "💾 DOSBox-X (MS-DOS)",
        [EmulatorType.ViceC64]      = "📼 VICE (C64)",
        [EmulatorType.Mame]         = "🕹 MAME (Arcade)",
        [EmulatorType.ScummVM]      = "🎮 ScummVM",
        [EmulatorType.Stella]       = "👾 Stella (Atari 2600 VCS)",
        [EmulatorType.Hatari]       = "🦅 Hatari (Atari ST)",
        [EmulatorType.Altirra]      = "🐢 Altirra (Atari 8-bit)",
        [EmulatorType.Cpcec]        = "🎨 CPCEC (Amstrad CPC)",
        [EmulatorType.Zxsec]        = "🌈 ZXSEC (Spectrum)",
        [EmulatorType.Csfec]        = "🍞 CSFEC (Commodore 64)",
        [EmulatorType.Msxec]        = "🟦 MSXEC (MSX)",
        [EmulatorType.ViceC128]     = "📼 VICE (C128)",
        [EmulatorType.ViceVic20]    = "📼 VICE (VIC-20)",
        [EmulatorType.VicePet]      = "📼 VICE (PET)",
        [EmulatorType.ViceC64Dtv]   = "📼 VICE (C64-DTV)",
        [EmulatorType.VicePlus4]    = "📼 VICE (Plus/4, C16)",
        [EmulatorType.Windows]      = "🪟 Windows (natif, sans émulateur)",
        [EmulatorType.Tic80]        = "🎮 TIC-80 (fantasy console)",
        [EmulatorType.MicroW8]      = "⚡ MicroW8 (WebAssembly)",
        [EmulatorType.UnrealSpeccy] = "🕹 UnrealSpeccy (ZX Spectrum / Pentagon)",
        [EmulatorType.EightyOne]    = "8️⃣ EightyOne (ZX-80 / ZX-81)",
        [EmulatorType.ZEsarUX]      = "⚛ ZEsarUX (Spectrum Next + 48K/128K/ZX81)",
        [EmulatorType.KegaFusion]   = "🎮 Kega Fusion (Sega Genesis/MD, SMS, GG, CD, 32X)",
        [EmulatorType.Browser]      = "🌐 Browser (HTML5 / WebGL / WASM)",
        [EmulatorType.Java]         = "☕ Java (.jar)",
        [EmulatorType.Fuse]         = "🔵 Fuse (ZX Spectrum 16K/48K/128K/Pentagon…)",
        [EmulatorType.BlastEm]      = "💥 BlastEm (Sega Genesis/Mega Drive — haute précision)",
        [EmulatorType.Arculator]    = "🦅 Arculator (Acorn Archimedes / RISC OS)",
        [EmulatorType.PPSSPP]       = "🎮 PPSSPP (Sony PSP)",
        [EmulatorType.BlueMSX]      = "📺 blueMSX (MSX1/2/2+/TurboR, SVI, ColecoVision)",
        [EmulatorType.DuckStation]  = "🎮 DuckStation (Sony PlayStation 1 / PSX)",
        [EmulatorType.Pcsx2]        = "🎮 PCSX2 (Sony PlayStation 2)",
        [EmulatorType.Trs80gp]      = "💾 trs80gp (TRS-80 Model I/III/4)",
        [EmulatorType.Oricutron]    = "🎮 Oricutron (Oric-1 / Atmos / Telestrat)",
        [EmulatorType.Dolphin]      = "🎮 Dolphin (Nintendo GameCube / Wii)",
        [EmulatorType.SimCoupe]     = "💾 SimCoupe (SAM Coupé)",
        [EmulatorType.Flycast]      = "🎮 Flycast (Sega Dreamcast / Naomi / Atomiswave)",
        [EmulatorType.JzIntv]       = "🎮 jzIntv (Mattel Intellivision)",
        [EmulatorType.Dcmoto]       = "💾 DCMOTO (Thomson MO5/MO6/TO7/TO8/TO9)",
        [EmulatorType.Xm6TypeG]     = "💾 XM6 TypeG (Sharp X68000)",
        [EmulatorType.BeebEm]       = "💾 BeebEm (BBC Micro / Master 128)",
        [EmulatorType.SQLux]        = "💾 sQLux (Sinclair QL)",
        // Ryujinx et RPCS3 retirés le 2026-07-24 (consoles de dernière génération) — cf.
        // commentaire dans EmulatorSeedCatalog.cs. Enum conservé, plus de libellé ici.
        [EmulatorType.BigPEmu]      = "🎮 BigPEmu (Atari Jaguar / Jaguar CD)",
        [EmulatorType.Handy]        = "🕹️ Handy (Atari Lynx)",
        [EmulatorType.GeePee32]     = "🕹️ gp32emu / GeePee32 (GamePark GP32)",
        [EmulatorType.Ep128Emu]     = "💻 ep128emu (Enterprise 64/128)",
        [EmulatorType.Mz800Emu]     = "💻 mz800emu (Sharp MZ-700 / MZ-800 / MZ-1500)",
        [EmulatorType.ColEm]        = "🕹 ColEm (ColecoVision)",
        [EmulatorType.PuNES]        = "🎮 puNES (NES / Famicom / FDS / NSF)",
        [EmulatorType.Ares]         = "🌟 ares (multi-système : NES/SNES/GB/N64/MD/PCE…)",
        [EmulatorType.Ruffle]       = "🕸️ Ruffle (Adobe Flash .swf)",
        [EmulatorType.ProSystem]    = "👾 ProSystem (Atari 7800 + rétrocompat 2600)",
        [EmulatorType.Xenia]        = "🟢 Xenia (Microsoft Xbox 360)",
        [EmulatorType.CxbxReloaded] = "🟩 CXBX-Reloaded (Xbox original / OG Xbox)",
        [EmulatorType.AppleWin]     = "🍎 AppleWin (Apple II / II+ / IIe)",
        [EmulatorType.GSplus]       = "🍎 GSplus (Apple IIgs)",
        [EmulatorType.Kegs]         = "🍏 KEGS32 (Apple IIgs — outil de Kent Dickey)",
        [EmulatorType.Pemsa]        = "🎲 Pemsa (PICO-8)",
        [EmulatorType.Mesen]        = "🎯 Mesen 2 (SNES/NES/GB/GBA/PCE…)",
        [EmulatorType.MelonDS]      = "🎮 melonDS (Nintendo DS / DSi)",
        [EmulatorType.Azahar]       = "🎮 Azahar (Nintendo 3DS)",
    };

    /// <summary>
    /// Tous les types, dans l'ordre de déclaration de l'enum. Généré une fois.
    /// </summary>
    public static IReadOnlyList<EmulatorTypeOption> All { get; } =
        Enum.GetValues<EmulatorType>()
            .Select(t => new EmulatorTypeOption(t, _labels.GetValueOrDefault(t, t.ToString())))
            .ToList();
}
