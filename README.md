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

`clean.ps1 -WhatIf` previews cleanup; `clean.ps1` removes generated output while
preserving the entire `simdisk` directory. Rebuild afterward. The build installs
codecs and themes as well as compiling the executable. GUI behavior and audio
playback are left for the user's manual testing, as requested.
