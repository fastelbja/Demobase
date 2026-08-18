# DemoBase

A library and launcher for demoscene productions (demos, intros, music, graphics): automatic
import of the [Demozoo](https://demozoo.org/) database, one-click launch of any release in the
right emulator, a built-in tracker/chiptune music player, and management of dozens of emulators
(download, installation, BIOS configuration).

## Table of contents

- [Overview](#overview)
- [Features](#features)
- [Installation](#installation)
- [First launch](#first-launch)
- [Tips & known limitations](#tips--known-limitations)
- [Recent changelog](#recent-changelog)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Project structure](#project-structure)

## Overview

DemoBase is a WPF (.NET 8) application for Windows that organizes and lets you (re)discover
demoscene productions: demos, intros, music and graphics, across every platform covered by
Demozoo (Amiga, Atari ST, C64, PC, consoles, and more).

It imports the Demozoo database locally (SQLite), lets you browse releases by group, artist,
platform or party, and launches each production directly in the right emulator — with automatic
download, installation and (whenever possible) BIOS/firmware configuration.

## Features

- **Browsing**: releases, groups, artists, platforms, parties, favorites (releases, soundtracks,
  graphics) — fully keyboard-navigable.
- **50+ supported emulators**, with built-in download/installation and automatic launch based on
  the release's platform (Amiga/WinUAE, Atari ST/Hatari, Atari 8-bit/Altirra, C64/VICE,
  consoles (PS1/PS2/Dreamcast/Saturn/DS/GBA...), arcade/MAME, and many more).
- **Automatic BIOS pack**: downloads the Recalbox BIOS pack and automatically configures the
  emulators that depend on it (DuckStation, PCSX2, Flycast, melonDS, XM6 TypeG...).
- **Built-in tracker/chiptune player** (TrackerPlayer): native formats (.mod/.xm/.it/.s3m/.dbm...),
  ZX/Atari/C64/console formats via ZXTune, ~150 exotic Amiga formats via UADE, and native SNDH
  playback (68000/YM2149/STE DAC emulation).
- **Emulator config/profile sync** via Mega.nz (JSON file + config files), to share the same
  setup across multiple machines.
- **Export/import of per-release profile overrides** ("Profil (debug)"): portable across
  installations (keyed by stable Demozoo/emulator IDs), synced automatically through the same
  Mega mechanism as emulator configs.
- **Advanced per-profile settings** for some emulators: CPU/memory cycle-exact (WinUAE), CPU
  emulation parameters (Prefetch, cycle-exact, data cache, MMU, 24-bit addressing, accurate
  FPU), extended VDI screen and CPU type/clock/FPU (Hatari).
- **"Quit key" info dialogs** for emulators with no standard shortcut (BlueMSX, WinUAE, Hatari,
  TR-DOS/ZEsarUX), shown once per emulator (with a "Don't show again" option), translated in
  French/English.
- **AGA HDD demo pack** for WinUAE (`Demos.zip`), downloaded and installed automatically.
- **Competition ranking** shown both on the release page and on the artist/group page.
- **Demozoo import** with version tracking and incremental updates.
- **Light/dark theme**, **French/English** UI.

## Installation

Requirements: Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
git clone https://github.com/fastelbja/Demobase.git
cd DemoBase
```

Open `DemoBase.sln` in Visual Studio 2022 (or JetBrains Rider), restore the NuGet packages,
build and run the `DemoBase.App` project.

## First launch

On first startup, a setup wizard walks you through the configuration:

1. **Folders** — location of BIOS, Configs, Database, Releases, Working (defaults to
   subfolders of the executable's directory).
2. **Database** — initial Demozoo dump import.
3. **DATs** *(optional)* — import DAT files for ROM verification.
4. **BIOS** — download the Recalbox BIOS pack.
5. **Emulators** — select and install the emulators you want.
6. **External tools** *(optional)*.
7. **Ready** — finalization, folder creation.

## Tips & known limitations

- **XM6 TypeG (Sharp X68000) — fullscreen**: `xm6g.ini` has no key to control fullscreen
  (unlike Hatari or Altirra). Manual one-time procedure (the emulator remembers the window state
  afterwards):
  1. Launch the emulator.
  2. **Tools → Options** menu.
  3. **Resume** tab.
  4. Check **Resume Window**.
  5. Press **Alt+Enter** to switch to fullscreen.
  6. Quit with **Alt+F4** (not the close button).
- **Hatari — optional TOS ROM**: the TOS ROM field can be left empty; Hatari then boots its
  built-in EmuTOS. Only fill it in if a specific TOS is required.
- **BIOS pack and emulators with no configurable path** (notably XM6 TypeG): required BIOS
  files are identified in the pack by size + CRC32 (not by file name) and copied automatically
  to the right place, as soon as both the pack is downloaded AND the emulator is installed
  (regardless of the order).
- **Multi-disk images**: Altirra accepts an unlimited number of floppies per ZIP (D1, D2, D3...);
  Hatari is limited to 2 physical drives (A:/B:).
- **Debug mode (release page)**: a "Profil (debug)" selector lets you force a specific release
  to use a different emulator than the platform's default profile. A "Reset Files" button clears
  the memorized startup file (WinUAE HD / DosBoxX, for releases with several executables) if the
  wrong one was picked by mistake — the file picker will reappear on the next launch.

## Recent changelog

- **Manual subsong navigation** (UADE and ZXTune): ◀/▶ buttons and a "Subsong N/M" label
  overlaid on the oscilloscope for tracks with multiple internal songs (e.g. `.emul`, `.ay`,
  Amiga formats with subsongs), instead of chaining through them automatically with no
  indication. The displayed duration now follows the current subsong instead of staying frozen
  on the first one played.
- **Many additional tracker formats** now routed to libopenmpt (FastTracker 2 pattern view)
  instead of the default view or the wrong engine: `.xmf`, `.amf`, `.667`/`.669`, `.digi`,
  `.dsm`, `.dtm`, `.mdl`, `.dmf`, `.ams`, `.psm`, `.gtk`/`.gt2`, `.mt2`. Several naming conflicts were
  fixed along the way (the same extension can denote a completely different PC vs. ZX
  Spectrum/Amiga format depending on context).
- **ZXTune multi-track playback reliability**: fixed a crash that could stop playback entirely
  after automatically advancing from one subsong to the next, and a detection bug that could
  wrongly report 64 subsongs on a file that had none.
- **Favorite Soundtracks**: a track added to favorites from another screen (release page,
  media library, Modland) now shows up immediately in the favorites list, no app restart
  needed.
- **Playlists in Favorite Soundtracks** (new feature, Spotify-style): group favorite tracks into
  playlists (e.g. the tracks of the same album), create/rename/delete a playlist, play a single
  playlist or all playlists chained together, reorder tracks (▲/▼). A track filed into a playlist
  disappears from the unsorted favorites list. The page is now laid out in 3 columns (playlists
  25% / unsorted favorites 25% / tracker player 50%): selecting a playlist makes it the target of
  a favorite's "➕" button (direct add, no menu), which otherwise offers "➕ New playlist…" on top
  of the existing playlists. Missing files are downloaded on demand when playing a playlist.
- **Gapless playback** for favorite soundtracks made of several consecutive `.mod` files (e.g.
  tracks from the same album): the audio engine now shares a single output device instead of
  recreating one for every track, removing the micro-gaps and latency between songs.
- **Favorite Soundtracks — display order**: the list now follows insertion order (which is also
  playback order) instead of alphabetical order.
- **Favorite Soundtracks — full-width screen**: the release detail panel (useless while listening
  to a favorite soundtrack) is now hidden on this page, giving more room to the list and the
  tracker/oscilloscope view shown alongside it.
- **"Play" button (release's Media/Soundtracks tab)**: now triggers a download if the file isn't
  present locally yet, instead of doing nothing.
- **Preferences — "Save" button**: now shown permanently in a fixed bar at the bottom of the
  page, no longer requiring a scroll all the way down to find it (a path change could previously
  be silently lost if you left the page without scrolling).
- **Media Library**: fixed a regression that incorrectly hid the release detail panel, needed to
  add a track to favorites directly from this screen.
- **"No launchable file" badge (🚫)** in release lists (main list and artist/group page):
  visually flags releases with no launchable file at all (no known DAT entry and no download
  link other than an external video reference), so you don't have to click "Launch" to find out.
  Shown in red so it stays visible in dark theme.
- **Artist/group page — scrolling**: the release list (and right panel) scroll position now
  resets to the top on every artist/group change, instead of keeping the previous releaser's
  position.
- **WinUAE — Amiga Startup-Sequence**: fixed HTML entity decoding (`&amp;`, etc.) in file names
  coming from DAT files. An undecoded `&amp;` ends with `;` (AmigaDOS's command separator), so
  file names containing "&" could silently break auto-launch ("Unknown command"). Fixed at DAT
  import, ZIP building, and ZIP extraction — the last one also fixes already-cached ZIP files,
  with no re-import or rebuild needed.
- **Console executable music** (e.g. SNES `.sfc`): now launched through the matching emulator
  (Mesen...) instead of the Windows executable player, which couldn't start them.
- **MesenCE**: the first-launch configuration wizard (storage location, gamepad mappings,
  options) is now pre-filled automatically — no need to go through it again on every install.
- **"Reset Files" button (debug mode)**: broadened to clear the memorized startup file (WinUAE
  HD / DosBoxX) across all candidate profiles/emulators for a release, not just the currently
  resolved one.

## Keyboard shortcuts

See [KEYBOARD_SHORTCUTS.md](KEYBOARD_SHORTCUTS.md) (French) for the full list.

## Project structure

| Project | Role |
|---|---|
| `DemoBase.App` | WPF application (UI, ViewModels, services) |
| `DemoBase.Core` | Models, DTOs, interfaces |
| `DemoBase.Data` | EF Core repositories, preferences |
| `DemoBase.Media` | Media handling (images, videos) |
| `DemoBase.Import` | Demozoo data import |
| `TrackerPlayer.Core` | Tracker/chiptune audio playback engine |
| `TrackerPlayer.UI` | WPF player controls (pattern view, oscilloscope) |

Tech stack: WPF / .NET 8 / MVVM (CommunityToolkit.Mvvm) / EF Core 8 / SQLite.
