# Development notes

Work in `C:\RockboxDev\rockbox`. Build with `build.ps1 -StopSimulator`, then
launch with `run.ps1`. The simulator is a native Windows application; it runs
shared Rockbox C code with original Clip display and button configuration.

## Source map

| Area | Files |
| --- | --- |
| Main menu and navigation | `apps/root_menu.c`, `apps/menu.c`, `apps/menu.h` |
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
it does not start a custom interface implementation.

## Checks and boundaries

The setup compiled the simulator, codecs, and plugins and launched a responding
Sansa Clip window. Manual button navigation, visible labels, file browsing, and
audio playback remain user tests. Return to the main menu after relaunching to
inspect the label; Rockbox may restore its previous screen.

Windows compatibility fixes in `tools/addtargetdir.pl` and `tools/genlang`, plus
LF handling for theme packaging, are preserved in Git. The regression checks
remain under `tools/windows-simulator/tests/`.

The simulator does not emulate the ARM CPU or verify physical hardware. Device
firmware, USB devices, bootloaders, and flashing are outside this workflow.
