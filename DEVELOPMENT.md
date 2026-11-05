# Development notes

Work in `C:\RockboxDev\rockbox`. Build with `build.ps1 -StopSimulator`, then
launch with `run.ps1`. The simulator is a native Windows application; it runs
shared Rockbox C code with original Clip display and button configuration.

## Source map

| Area | Files |
| --- | --- |
| Main menu and navigation | `apps/root_menu.c`, `apps/menu.c`, `apps/menu.h` |
| Clip Home, suggestions, and pins | `apps/home_screen.c`, `apps/home_data.c`, `apps/home_screen.h` |
| Settings and submenus | `apps/menus/`, `apps/settings_list.c`, `apps/settings.c` |
| Lists, status bar, and layout | `apps/gui/`, including `list.c` and `viewport.c` |
| Playback screen and skins | `apps/gui/wps.c`, `apps/gui/skin_engine/` |
| Translated labels | `apps/lang/english.lang` and other language files |
| File browser | `apps/tree.c`, `apps/filetree.c` |
| Clip buttons to application actions | `apps/keymaps/keymap-clip.c`, `apps/action.c` |
| Windows keys to simulated buttons | `uisimulator/buttonmap/sansa-clip.c` |
| Playback, buffering, and codecs | `apps/playback.c`, `apps/pcmbuf.c`, `lib/rbcodec/` |
| Windows SDL display/audio/events | `firmware/target/hosted/sdl/` |
| Clip display and features | `firmware/export/config/sansaclip.h` |
| Simulator configuration | `tools/configure`, `tools/root.make`, `docs/UISIMULATOR` |

Edit source files rather than generated files in `build-sansaclip-sim`.
Incremental builds normally suffice; use `build.ps1 -Reconfigure` after build
configuration changes, or clean and rebuild for a fresh compilation.

## Reversible example

The existing simulator-only C demonstration changes the Files menu label to
**Files (Dev)** in `apps/root_menu.c`. It is isolated in commit `b14eacc693`.
To remove only the demonstration, inspect any newer edits first, then run:

```powershell
git revert b14eacc693
.\build.ps1 -StopSimulator
.\run.ps1
```

The normal non-simulator label remains translated. Use the language files for
future permanent labels. This example only proves the development workflow;
the song-first Home implementation is independent of that demonstration.

## Checks and boundaries

### Home checks

Run the focused host checks with:

```powershell
python .\tools\windows-simulator\tests\test-home.py
```

These compile the production Home data code and actual playlist progression
functions with small Windows host adapters. They cover genre and track
uniqueness, case-insensitive genres, Unicode paths, 30-minute expiry and timer
wraparound, single-press refresh, Home/Back parent state, pin ordering and save
failures, all repeat modes, normal playlist progression, and non-destructive
test-library seeding. They do not drive the simulator's GUI or audio.

The existing regression checks run in the MSYS2 UCRT64 environment:

```powershell
. .\scripts\Rockbox.Common.ps1
& C:\msys64\usr\bin\perl.exe .\tools\windows-simulator\tests\test-dependencies.pl
Invoke-RockboxBash -Msys2Root C:\msys64 -Script "$PWD\tools\windows-simulator\tests\test-genlang.sh"
```

After building and seeding, the user should check the following with `run.ps1`:

1. Press **Insert** for Home. Confirm the existing header, three song rows,
   Test Mix 1 and 2, then Settings. Long selected titles should scroll.
2. Press **Up** on the first song to refresh. Hold Up to confirm it does not
   repeatedly refresh. Start a song, return Home, and refresh without interrupting it.
3. Let a Home suggestion finish with repeat off, all, one, shuffle, and A–B
   selected in turn. Each should stop after that song, and the saved repeat
   preference should remain unchanged.
4. Open a pinned mix, choose a track, and confirm normal playlist playback.
   Back from its track list should return Home. Insert should always return Home.
5. Open **Settings → Playlists**, hold **Space** on Test Mix 3, and pin it.
   Return Home to see it after the existing pins. Unpin it and restart to check
   persistence. The playlist itself should remain available.
6. Open Files or a settings submenu through the bottom Settings entry. Back
   should return to that menu; Insert should return to the song-first Home.
7. After 30 minutes away from Home, return and confirm that suggestions refresh.
   Do not expect a refresh merely from sitting on Home.

The Home sources and integration are compiled only for `SANSA_CLIP`; they share
the simulator and device code path. One-song playback uses a persisted playlist
flag, not a change to global repeat settings. `home_play_suggestion(path)` is
the playback entry point to extend when autoplay is added. Pins use a checked
temporary file, sync, and replacement rename; failed writes retain the old file.

The setup compiled the simulator, codecs, and plugins and launched a responding
Sansa Clip window. Manual button navigation, visible labels, file browsing, and
audio playback remain user tests. Return to the main menu after relaunching to
inspect the label; Rockbox may restore its previous screen.

Windows compatibility fixes in `tools/addtargetdir.pl` and `tools/genlang`, plus
LF handling for theme packaging, are preserved in Git. The regression checks
remain under `tools/windows-simulator/tests/`.

The simulator does not emulate the ARM CPU or verify physical hardware. Device
firmware, USB devices, bootloaders, and flashing are outside this workflow.
