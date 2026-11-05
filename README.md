# Rockbox on Windows

Native Windows development setup for the **original SanDisk Sansa Clip v1**:
Rockbox target `sansaclip` / `SANSA_CLIP`, 128×64 display. The physical player's
revision is still unknown. This setup builds and runs the SDL2 simulator only.

## Build and run

From PowerShell:

```powershell
Set-Location C:\RockboxDev\rockbox
.\build.ps1 -StopSimulator
.\run.ps1
```

Edit C source, save, build, and relaunch. `-StopSimulator` requests a graceful
close before Windows replaces the executable. Close the window yourself to use
plain `build.ps1`. VS Code is optional; open this folder to use its build/run tasks.

If PowerShell blocks scripts, use a process-only override:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -StopSimulator
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\run.ps1
```

## Layout

- `apps/`, `firmware/`, `lib/`, `uisimulator/`, `tools/`: versioned Rockbox source.
- `build.ps1`, `run.ps1`, `clean.ps1`, `scripts/`: Windows workflow entrypoints.
- `build-sansaclip-sim/`: ignored generated build output.
- `build-sansaclip-sim/simdisk/`: simulated storage, settings, and test music.
- `.vscode/`: optional editor tasks. See `DEVELOPMENT.md` for the source map.

The project folder itself is one Git repository. `origin` is the user's fork;
`upstream` is the official [Rockbox repository](https://github.com/Rockbox/rockbox).

## Toolchain

Uses `C:\msys64`, MSYS2 **UCRT64**, MinGW-w64 GCC 16.2.0, GNU Make 4.4.1,
Perl 5.42.3, binutils 2.47, and SDL2 2.32.10. Git for Windows supplies Git.
If restoring dependencies, update MSYS2, then run in its UCRT64 terminal:

```bash
pacman -Syu
pacman -S --needed make perl diffutils zip unzip mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-SDL2
```

Complete any MSYS2-requested shell restart before installing packages. The
wrappers select UCRT64 for their child processes; permanent PATH changes are not
needed. No WSL, VM, Docker, cross-compiler, firmware image, or bootloader is used.

## Storage, controls, and cleanup

Put audio in `build-sansaclip-sim\simdisk\Music`. A quiet original test WAV is
included locally. In the simulator use arrows to navigate, **Space** to select,
Insert for Home, and F1 for key help. Enter is not mapped to Select.

## Song-first Home

The original Sansa Clip target now opens a scrolling Home list beneath the
existing header: three liked-song suggestions, pinned playlists, then
**Settings**. That final entry opens the complete former Home menu.

Suggestions come from `/Playlists/Liked Songs.m3u8` and use three different
primary genre tags, without requiring the Rockbox database. They stay fixed
while browsing and refresh on the next Home visit after 30 minutes, or when
you press **Up while already on the first row**. Holding Up does not repeatedly
refresh. Refreshing leaves playing audio alone.

A suggestion plays once and stops, even with repeat enabled. Selecting a pinned
playlist opens its tracks and uses normal playlist playback. Under
**Settings → Playlists**, hold **Space** on a playlist and choose **Pin to Home**
or **Unpin from Home**. Up to 16 pins are saved, in the order added, in
`simdisk/.rockbox/home-pins.txt`. The same context menu is available on a pinned
playlist on Home. **Insert** returns Home; **Left** goes back.

Create the local test playlists with:

```powershell
python .\scripts\seed-home-playlists.py
```

This creates Liked Songs from all imported MP3s, five deterministic Test Mixes,
and initially pins the first two mixes. It preserves existing playlists and
pins on reruns and never copies or edits audio. The current library produces
850 liked tracks and five mixes of 170. Generated playlists and pins are ignored
by Git. Replace `simdisk/Playlists/Liked Songs.m3u8` with your prepared UTF-8
playlist when ready; relative paths are resolved from the playlist's directory.
Missing or unreadable tracks are skipped, and Home shows a message if no liked
tracks with usable genres remain. Like/Unlike and autoplay are future work.

## Homelab music

The local library is `C:\RockboxDev\MusicLibrary`, copied once from
`evanh@homelab:/srv/storage/media/music`. It is outside the Git repository.
`build-sansaclip-sim\simdisk\MusicLibrary` is a directory junction to that copy,
so the simulator uses the local files without another copy or a live homelab
connection. The original test WAV remains in `simdisk\Music`.

Verified November 5, 2026: 850 MP3 files, 788 directories, 8,840,998,986 bytes.
All file contents match the homelab originals by SHA-256, including Unicode
filenames and folders that differ only by capitalization.

Run `run.ps1`, then open **Settings → Files (Dev) → MusicLibrary**. Use arrows to browse,
**Space** to open a folder or play a track, and **Left** to go back. For browsing
by tags, open **Settings → Settings → General Settings → Database → Initialize Now**,
wait for the scan, and restart the simulator if prompted. For later additions,
use **Update Now** to preserve play counts and ratings. Database files stay in
`simdisk\.rockbox`, separate from the audio files.

The import directory is case sensitive to preserve artist folders such as
`VIOLENT VIRA` and `Violent Vira`. If recreating the import, create an empty
`C:\RockboxDev\MusicLibrary` and run the following from Administrator PowerShell
before copying any files:

```powershell
fsutil file setCaseSensitiveInfo C:\RockboxDev\MusicLibrary enable
```

The copy preserves file contents (including embedded tags and artwork), folder
names, and modification times. Import manifests and verification results are
local, ignored files under `logs\music-import-20261105`. There is no scheduled
sync or homelab service change.

## Build cleanup

`clean.ps1 -WhatIf` previews cleanup; `clean.ps1` removes generated output while
preserving the entire `simdisk` directory. Rebuild afterward. The build installs
codecs and themes as well as compiling the executable. GUI behavior and audio
playback are left for the user's manual testing, as requested.
