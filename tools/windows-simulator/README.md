# Rockbox Sansa Clip simulator on Windows

This workspace builds Rockbox's original **SanDisk Sansa Clip v1** target as a native Windows SDL2 application. The source lives in `C:\RockboxDev\rockbox`; generated simulator files live in `C:\RockboxDev\build-sansaclip-sim`. All development, compilation, and execution happen locally on Windows.

The simulator target is `sansaclip` (`SANSA_CLIP`, 2 MB target memory configuration). It is the original Clip, rather than Clip v2, Clip+, or Clip Zip. The physical device's revision is still unknown; confirm the model when it arrives. The simulator exercises the selected target's interface and software; it does not establish compatibility with an unidentified physical device.

## Daily workflow

Open **C:\RockboxDev** as the VS Code folder:

```powershell
code C:\RockboxDev
```

Edit C files under `C:\RockboxDev\rockbox`, close the simulator window, then run:

```powershell
Set-Location C:\RockboxDev
.\build.ps1
.\run.ps1
```

If your current PowerShell execution policy blocks locally saved scripts, use these equivalent commands. `-ExecutionPolicy Bypass` applies only to that PowerShell process; it does not change the user or machine policy:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\RockboxDev\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\RockboxDev\run.ps1
```

In VS Code, **Ctrl+Shift+B** runs the default build task. **Terminal > Run Task** also offers **Rockbox: Run simulator**, **Rockbox: Build and run simulator**, and **Rockbox: Clean simulator (preserve storage)**. The tasks use the process-only execution-policy option above.

The build wrapper configures automatically when necessary, compiles incrementally, then runs `make install` to update simulator resources. It defaults to at most eight parallel jobs. Examples:

```powershell
.\build.ps1 -Jobs 4
.\build.ps1 -Reconfigure
.\build.ps1 -StopSimulator
.\run.ps1 -Zoom 3
.\run.ps1 -Zoom 1
.\run.ps1 -Wait
```

Build and clean refuse to operate while this workspace's simulator executable is running. Close its window, or use `-StopSimulator` to request a graceful close. If the simulator does not close, the scripts stop with an error instead of forcibly terminating it. `-Reconfigure` regenerates the simulator configuration and refreshes the generated dependency database; use clean followed by build for a full rebuild.

Zoom 3 enlarges the 128-by-64-pixel Clip display and hides the device background. Zoom 1 retains the device background. While the simulator has keyboard focus, the number keys 1, 2, and 3 change display scale; Tab toggles the background. The simulator must retain focus to receive buttons.

## Buttons and screenshots

These mappings were read from the checked-out `uisimulator/buttonmap/sansa-clip.c`:

| Computer key | Simulated Clip button |
| --- | --- |
| Arrow Up / keypad 8 | Up / Play |
| Arrow Down / keypad 2 | Down / Menu |
| Arrow Left / keypad 4 | Left |
| Arrow Right / keypad 6 | Right |
| Space / keypad 5 | Select |
| Insert / keypad multiply | Home |
| Page Up / keypad 9 | Volume up |
| Page Down / keypad 3 | Volume down |
| Escape / keypad 1 | Power |
| F1 | Simulator key help |
| F5 / keypad 0 | Save a screen dump |

Short presses and long presses can have different actions. Space is the Select key; Enter is not mapped to Select for this target. Use F1 to view the current simulator's help, and close its help dialog to resume navigation.

F5 saves a Rockbox BMP screen dump through the simulated filesystem, normally at the root of `simdisk` as `dump*.bmp`. Windows Snipping Tool can capture the complete SDL window. At zoom 1, mouse clicks on mapped device buttons also generate input.

## Simulated storage and audio

The simulator's root is:

```text
C:\RockboxDev\build-sansaclip-sim\simdisk
```

The launch wrapper passes that exact path with `--root`; it also sets the executable's working directory to the build directory. It does not expose a connected player's storage.

Copy audio you have permission to use into a directory such as:

```text
C:\RockboxDev\build-sansaclip-sim\simdisk\Music
```

Open **Files** in Rockbox to browse this directory. Installed codecs, plugins, themes, and simulator settings are under `simdisk\.rockbox`. `make install` updates packaged resources while retaining existing user settings and audio. No media is downloaded automatically by the workflow scripts.

Setup generated an original, quiet test signal at `simdisk\Music\Rockbox Test Tone.wav`: 12 seconds, mono PCM16, 22,050 Hz, amplitude 0.02. It provides a small local file for browser and playback checks; no personal music was copied. Runtime playback verification is recorded separately below.

## Safe cleaning

```powershell
.\clean.ps1 -WhatIf
.\clean.ps1
```

Clean removes generated children of `C:\RockboxDev\build-sansaclip-sim` while preserving **the entire `simdisk` directory**, including audio and `.rockbox` settings. It checks the absolute cleanup boundary and refuses symbolic links or junctions in deletion candidates. It does not clean or reset source files. The next build configures and recompiles from scratch. `-WhatIf` previews the operations.

The installed scripts were checked with PowerShell's parser; the Bash helper passed `bash -n`. A separate scratch fixture verified that cleanup preview does not remove output, actual cleanup removes generated output, and source, audio, and `.rockbox\config.cfg` remain unchanged. These script checks are separate from GUI and playback verification.

## Installed tools

The following environment was inspected/prepared on **October 23, 2026**. These are the observed versions for this setup, rather than a promise that future package-manager installs will resolve to the same versions.

| Component | Version / location |
| --- | --- |
| Windows | Native Windows, build 26200 |
| Git for Windows | 2.55.0; `C:\Program Files\Git\cmd\git.exe` |
| VS Code | 1.141.2 |
| Microsoft C/C++ VS Code extension | 1.34.4 |
| MSYS2 installer | winget package 20260611; MSYS2 base upgraded October 23, 2026 |
| MSYS2 installation | `C:\msys64` |
| Native compiler | UCRT64 `mingw-w64-ucrt-x86_64-gcc`, 16.2.0-6 |
| Compiler target | `x86_64-w64-mingw32` |
| GNU binutils | 2.47-4 |
| GNU Make | 4.4.1, MSYS `/usr/bin/make` |
| Perl | 5.42.3, MSYS Perl |
| SDL2 development package | `mingw-w64-ucrt-x86_64-SDL2`, 2.32.10-1 |
| Source baseline | `9377ad18c79d274a5abec6ee0cc536f59eb7c019`, October 22, 2026 |

The VS Code winget manifest advertised 1.142.0, while the installer/binary available during setup reported 1.141.2. The installed binary's reported version is recorded above. This does not require changing the editor before using the simulator.

UCRT64 GCC and SDL2 are used together. MSYS Bash, Make, Perl, and utility programs supply the Unix-compatible build shell; the resulting simulator is a native Windows executable. The build wrapper sets `MSYSTEM=UCRT64`, `CHERE_INVOKING=1`, and `MSYS2_PATH_TYPE=strict` only for its subprocess, and selects `/ucrt64/bin:/usr/bin:/c/Program Files/Git/cmd`. The launch wrapper adds `C:\msys64\ucrt64\bin` to the child process's DLL search path. The scripts do not edit permanent PATH or ExecutionPolicy settings.

## Recreating the installation

These steps document bootstrap and recovery. The machine already has the setup tools listed above; do not reinstall them for ordinary rebuilds.

1. In PowerShell, inspect tools before installing missing ones:

   ```powershell
   Get-Command git, code, winget -ErrorAction SilentlyContinue
   git --version
   code --version
   Test-Path C:\msys64\usr\bin\bash.exe
   Test-Path C:\msys64\ucrt64\bin\gcc.exe
   ```

2. If required, install missing applications through winget:

   ```powershell
   winget install --exact --id Git.Git --source winget --accept-package-agreements --accept-source-agreements
   winget install --exact --id Microsoft.VisualStudioCode --source winget --accept-package-agreements --accept-source-agreements
   winget install --exact --id MSYS2.MSYS2 --source winget --accept-package-agreements --accept-source-agreements
   ```

   Keep MSYS2 at `C:\msys64` for the supplied defaults. An installer may require a Windows UAC approval. After an installation that updates PATH, reopen PowerShell or VS Code so that it receives the updated environment. The wrappers can also locate MSYS2 explicitly with `-Msys2Root`.

3. Open the **MSYS2 UCRT64** terminal and update the base installation:

   ```bash
   pacman -Syu
   ```

   If pacman tells you to close the terminal for an MSYS runtime/core update, close all MSYS2 shells, open UCRT64 again, and repeat `pacman -Syu` until the update is complete. Install the development packages:

   ```bash
   pacman -S --needed make perl zip unzip mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-SDL2
   gcc --version
   gcc -dumpmachine
   ld --version
   make --version
   perl --version
   sdl2-config --version
   sdl2-config --cflags --libs
   ```

   Expected compiler target: `x86_64-w64-mingw32`. `which gcc` should resolve within `/ucrt64/bin`. Retain the existing Windows Git installation; another MSYS Git package is unnecessary. The supplied Bash helper uses `sdl2-config`, so an additional pkg-config package is unnecessary.

4. Restore the user's development fork and official upstream in PowerShell:

   ```powershell
   New-Item -ItemType Directory -Force C:\RockboxDev | Out-Null
   git -c core.autocrlf=false -c core.eol=lf clone --branch main https://github.com/evchuk4018/rockbox.git C:\RockboxDev\rockbox
   git -C C:\RockboxDev\rockbox remote add upstream https://github.com/Rockbox/rockbox.git
   git -C C:\RockboxDev\rockbox remote -v
   ```

   During initial setup, the source was cloned from the [official Rockbox repository](https://github.com/Rockbox/rockbox). The user's fork is `origin`, on `main`; official Rockbox is `upstream`, whose development branch is `master`. The baseline listed above identifies the upstream revision used to prepare this environment. The fork stores Windows workflow additions for repeatable setup.

   If an existing checkout already exists, inspect it with `git status` and `git remote -v`; preserve current work instead of cloning over it or resetting it.

5. Copy the maintained workflow files from the repository to the development root. Run these commands only after reviewing whether existing root copies contain changes you want to keep:

   ```powershell
   $devRoot = 'C:\RockboxDev'
   $templateRoot = Join-Path $devRoot 'rockbox\tools\windows-simulator'
   foreach ($name in @('README.md', 'DEVELOPMENT.md', 'build.ps1', 'run.ps1', 'clean.ps1', 'scripts', '.vscode')) {
       Copy-Item -LiteralPath (Join-Path $templateRoot $name) -Destination $devRoot -Recurse -Force
   }
   code --install-extension ms-vscode.cpptools
   code C:\RockboxDev
   ```

   The files under `rockbox\tools\windows-simulator` are the versioned copies. The ones at `C:\RockboxDev` are the convenient working entrypoints. If you improve the workflow, keep those copies synchronized before committing.

6. Build and run using the commands in **Daily workflow**. The first compilation takes longer than an incremental rebuild. Build failures return a nonzero process exit code.

Use the requested space-free development directory. The wrappers quote Windows paths, but upstream Rockbox Makefiles contain unquoted source/build prerequisites; they do not reliably support whitespace in the development root. The build helper checks for this case explicitly.

## How the simulator build was selected

The checked-out source, rather than historical wiki commands alone, determines the procedure:

- `docs/UISIMULATOR` describes a separate build directory, `tools/configure`, `make`, `make install`, and local `simdisk` storage. It still contains old SVN wording, so its platform details were cross-checked against the scripts.
- `tools/configure` contains the named `sansaclip` target, separate `sansaclipv2`, `sansaclipplus`, and `sansaclipzip` targets, the simulation build type, SDL2 discovery through `sdl2-config`, and the MinGW host branch.
- `tools/root.make` defines simulator `make install` and the `simdisk` default installation prefix.
- `firmware/target/hosted/sdl/system-sdl.c` implements the current `--root` and `--zoom` options.
- `uisimulator/buttonmap/sansa-clip.c` defines the keyboard controls above.

The generated Makefile records `MODELNAME=sansaclip`, `APP_TYPE=sdl-sim`, `BINARY=rockboxui.exe`, and UCRT64 GCC. The chosen target is passed by name; the workflow does not depend on a guessed menu number or device ID.

The underlying commands, issued by the helper from the build directory, are:

```bash
/c/RockboxDev/rockbox/tools/configure --target=sansaclip --type=s --no-ccache
make -j8
make install
```

The compiler mode from this configuration is GNU C11. The VS Code C/C++ configuration uses GNU C11 and includes the selected Clip, AS3525, ARM, and hosted SDL directories from the generated Makefile. IntelliSense is an editor aid; `build.ps1` is the authoritative compile check.

## Troubleshooting and logs

- **Build says simulator is running:** close its window, or run `build.ps1 -StopSimulator`. A Windows executable in use cannot be safely replaced during linking.
- **Missing SDL2/compiler/library:** use MSYS2 UCRT64, verify the package names and `sdl2-config` output above, and keep GCC and SDL2 in the same `/ucrt64` prefix.
- **PowerShell blocks scripts:** use the process-only `powershell.exe -ExecutionPolicy Bypass -File ...` commands above. No permanent policy change is needed.
- **New code is missing:** confirm you edited `C:\RockboxDev\rockbox`, then close the simulator, build successfully, and relaunch through `C:\RockboxDev\run.ps1`. Rebuild updates both the executable and the installed `.rockbox` resources.
- **Keys do nothing:** focus the SDL window, dismiss any F1 help dialog, and use Space for Select. Press `h` only if deliberately testing the hold switch; it toggles simulated hold.
- **A sandbox reports `NtCreateDirectoryObject ... C0000022`:** that is a restriction on MSYS runtime initialization in the automation sandbox. Run the documented command in ordinary local PowerShell. It is not a Rockbox compilation diagnostic or a reason to change the development platform.

The initial build failed because `tools/addtargetdir.pl` did not understand the Windows drive-letter paths emitted by native UCRT64 GCC in dependency data (for example, `C:/...`). `C:\RockboxDev\build-initial.log` records that diagnostic failure. A minimal MSYS2-scoped patch is applied in `tools/addtargetdir.pl` to normalize native drive prefixes before parsing. All 12 dependency regression checks pass, including correct generated-header prerequisites and unchanged existing POSIX paths. Keep this compatibility fix when updating from upstream. Run its regression suite in UCRT64 with `perl /c/RockboxDev/rockbox/tools/windows-simulator/tests/test-dependencies.pl`. The corrected build and runtime results are recorded below.

A second source fix in `tools/genlang` emits the language enum header as a path relative to generated `lang.h`, instead of embedding an absolute `/c/...` include that native GCC cannot resolve. The saved `tests/test-genlang.sh` regression regenerates the language code and passes native GCC syntax/preprocessor checks. Run it in UCRT64 with `bash /c/RockboxDev/rockbox/tools/windows-simulator/tests/test-genlang.sh`.

To save a later build log from PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\RockboxDev\build.ps1 2>&1 |
    Tee-Object -FilePath C:\RockboxDev\build-latest.log
if ($LASTEXITCODE -ne 0) { throw 'Rockbox build failed; inspect build-latest.log.' }
```

Windows checkout line endings also affected `wps/WPSLIST`: the packager treated a CR-terminated disabled marker as a filename. That data file is normalized to LF, and a Git attribute preserves LF for it in future checkouts. The wrapper saves `build-sansaclip-sim/install.log`, rejects the known copy/conversion error diagnostics, and checks required installed codec, language, and theme resources. A successful link by itself is not treated as a successful installation.

## Verification record

GUI launch, navigation, storage browsing, and the reversible C-source proof are **pending the setup agent's runtime verification**. Successful compilation alone does not establish that these work. Replace this paragraph with the observed results and any remaining limitations after testing.

For a guided source map and a reversible interface-change exercise, see `C:\RockboxDev\DEVELOPMENT.md`.

## Scope

This setup creates only a development simulator. It does not install a bootloader, flash a player, download a device firmware image, or access connected USB devices. No homelab deployment applies to this Windows-local environment.
