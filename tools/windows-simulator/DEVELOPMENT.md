# Rockbox simulator development

This workspace builds the Rockbox user interface as a local Windows application. Edit the C source in `C:\RockboxDev\rockbox`, rebuild with `C:\RockboxDev\build.ps1`, and launch with `C:\RockboxDev\run.ps1`.

The source map below was checked against upstream commit `9377ad18c79d274a5abec6ee0cc536f59eb7c019` dated October 22, 2026. Line numbers are starting points and will move as the source changes. Search for the named function, macro, or symbol when a line number no longer matches.

## Daily workflow

1. Open the development workspace in VS Code as described in `C:\RockboxDev\README.md`.
2. Close the simulator before rebuilding so Windows can replace its executable and loaded codec/plugin files.
3. Edit a source file and save it.
4. In PowerShell, run:

   ```powershell
   Set-Location C:\RockboxDev
   .\build.ps1
   .\run.ps1
   ```

5. Exercise the changed screen using the keyboard mappings below. Use F5 to save a screenshot from inside the simulator when comparing changes.

Use VS Code's Terminal > Run Build Task command for the configured build task. Terminal > Run Task also lists the launch task. The PowerShell scripts are the shared entry points, so editor tasks and terminal commands use the same toolchain and build configuration.

`C:\RockboxDev\clean.ps1` cleans generated simulator build outputs. It is separate from reverting source edits. Incremental builds are normally enough after an ordinary C change. Reconfigure or clean when changing target, toolchain, generated configuration, or compiler options.

## Target being simulated

The configured target is **the original SanDisk Sansa Clip**, Rockbox name `sansaclip`, C target macro `SANSA_CLIP`. In the checked source, `C:\RockboxDev\rockbox\tools\configure` selects it with `55|sansaclip` near line 2395. The string is preferable to depending on a numeric menu choice.

This is the original Clip variant, commonly called Clip v1. It is separate from `sansaclipv2`, `sansaclipplus`, and `sansaclipzip`. The arriving physical device's hardware revision has not been established. Once it arrives, identify it before choosing any eventual device build. The simulator target does not establish that a future firmware image is appropriate for that device.

`C:\RockboxDev\rockbox\firmware\export\config\sansaclip.h` defines the player configuration: 128 by 64 pixels, one bit per pixel, with a split display. A larger simulator window scales those pixels; it does not increase the target's display resolution.

## Where to change behavior

| Area | Source files and purpose |
| --- | --- |
| Top-level menu | `C:\RockboxDev\rockbox\apps\root_menu.c`: menu items, default ordering, screen dispatch, startup screen. Search for `file_browser`, `menu_table`, and `root_menu`. |
| Menu definitions and dispatch | `C:\RockboxDev\rockbox\apps\menu.h` defines `MENUITEM_RETURNVALUE`, `MENUITEM_FUNCTION`, and related macros. `C:\RockboxDev\rockbox\apps\menu.c` resolves labels and processes menu actions. |
| Settings and submenus | `C:\RockboxDev\rockbox\apps\menus\main_menu.c`, `C:\RockboxDev\rockbox\apps\menus\settings_menu.c`, `C:\RockboxDev\rockbox\apps\menus\display_menu.c`, `C:\RockboxDev\rockbox\apps\menus\playback_menu.c`, and neighboring files. |
| Lists and selection | `C:\RockboxDev\rockbox\apps\gui\list.c` handles shared list behavior. `C:\RockboxDev\rockbox\apps\gui\bitmap\list.c` draws lists on bitmap displays. |
| Status bar and viewports | `C:\RockboxDev\rockbox\apps\gui\statusbar.c`, `C:\RockboxDev\rockbox\apps\gui\statusbar-skinned.c`, and `C:\RockboxDev\rockbox\apps\gui\viewport.c`. |
| Playback screen | `C:\RockboxDev\rockbox\apps\gui\wps.c` handles the While Playing Screen. `C:\RockboxDev\rockbox\apps\gui\skin_engine\` parses and renders skin definitions. |
| Strings and translations | `C:\RockboxDev\rockbox\apps\lang\english.lang` and the other language files. `C:\RockboxDev\rockbox\apps\lang\lang.make` generates language build products. Edit the source language files rather than generated `lang.h` or generated C files in the build directory. |
| Settings values and persistence | `C:\RockboxDev\rockbox\apps\settings_list.c` describes settings; `C:\RockboxDev\rockbox\apps\settings.c` loads and saves them. |
| File browser | `C:\RockboxDev\rockbox\apps\tree.c` and `C:\RockboxDev\rockbox\apps\filetree.c`. |
| Buttons to UI actions | `C:\RockboxDev\rockbox\apps\keymaps\keymap-clip.c` maps Clip buttons to context-dependent actions. `C:\RockboxDev\rockbox\apps\action.c` processes actions, repeats, and context handling. |
| PC keys to Clip buttons | `C:\RockboxDev\rockbox\uisimulator\buttonmap\sansa-clip.c` maps keyboard keys to `BUTTON_*` values and defines clickable regions on the player background. |
| Button constants | `C:\RockboxDev\rockbox\firmware\target\arm\as3525\sansa-clip\button-target.h` defines the original Clip's button bit values. Hardware GPIO handling is separate from the simulator keyboard map. |
| Playback engine | `C:\RockboxDev\rockbox\apps\playback.c`, `C:\RockboxDev\rockbox\apps\pcmbuf.c`, and `C:\RockboxDev\rockbox\lib\rbcodec\`. These manage playback, PCM buffering, metadata, DSP, and codecs. |
| Windows audio and display backend | `C:\RockboxDev\rockbox\firmware\target\hosted\sdl\pcm-sdl.c`, `C:\RockboxDev\rockbox\firmware\target\hosted\sdl\lcd-sdl.c`, `C:\RockboxDev\rockbox\firmware\target\hosted\sdl\window-sdl.c`, and neighboring files. |
| Simulator host integration | `C:\RockboxDev\rockbox\firmware\target\hosted\sdl\system-sdl.c` parses runtime arguments; `C:\RockboxDev\rockbox\firmware\target\hosted\sdl\button-sdl.c` receives SDL events and handles simulator shortcuts. |
| Simulated filesystem | `C:\RockboxDev\rockbox\uisimulator\common\filesystem-sim.c` and `C:\RockboxDev\rockbox\firmware\target\hosted\sdl\filesystem-sdl.c`. |
| Simulator player image | `C:\RockboxDev\rockbox\uisimulator\bitmaps\UI-sansaclip.bmp` and `C:\RockboxDev\rockbox\firmware\target\hosted\sdl\sim-ui-defines.h`. This surround image is separate from the C code drawing the Rockbox screen. |
| Build configuration | `C:\RockboxDev\rockbox\tools\configure`, `C:\RockboxDev\rockbox\tools\root.make`, `C:\RockboxDev\rockbox\uisimulator\uisimulator.make`, and `C:\RockboxDev\rockbox\firmware\firmware.make`. |

## Keyboard controls

These mappings were read directly from `C:\RockboxDev\rockbox\uisimulator\buttonmap\sansa-clip.c`. Give the simulator window keyboard focus first. A short press and a held key can produce different actions.

| Windows keyboard key | Simulated Clip button | Common use |
| --- | --- | --- |
| Up arrow / numeric keypad 8 | Up / Play | Previous menu row; play/pause on the playback screen. |
| Down arrow / numeric keypad 2 | Down | Next menu row; playlist-related actions on the playback screen. |
| Left arrow / numeric keypad 4 | Left | Back/cancel; previous track or hold to seek. |
| Right arrow / numeric keypad 6 | Right | Enter; next track or hold to seek. |
| Space / numeric keypad 5 | Select | Enter a menu or folder; hold for context menu. |
| Insert / numeric keypad multiply | Home | Menu; hold for quickscreen where supported. |
| Page Up / numeric keypad 9 | Volume Up | Increase playback volume. |
| Page Down / numeric keypad 3 | Volume Down | Decrease playback volume. |
| Escape / numeric keypad 1 | Power | Back/cancel; stop on the playback screen; long-press power behavior depends on context. |

Enter is not a Select key in the checked Clip simulator map. Use Space or Right arrow. Num Lock can affect how Windows interprets numeric keypad keys; the arrow keys and Space avoid that ambiguity.

Simulator shortcuts, handled separately in `C:\RockboxDev\rockbox\firmware\target\hosted\sdl\button-sdl.c`:

- F1: show key help.
- F5: save a Rockbox screenshot in simulated storage.
- Tab: show or hide the player background.
- 1, 2, 3: change LCD zoom; 0 selects half size; 4 toggles scaling quality.
- H: toggle the original Clip's simulated hold switch. If buttons stop responding, check that hold is off.
- U: toggle a simulated USB connection. This affects simulator state only.

## Simulated storage

Rockbox sees the storage directory passed by `C:\RockboxDev\run.ps1` as `/`. The default in upstream simulator code is a `simdisk` directory relative to the process working directory; the wrapper supplies the intended build/storage location. See `C:\RockboxDev\README.md` for its exact path.

Put test music in a subdirectory such as `Music`. In the Rockbox main menu, open Files, select Music, and select an audio file to start playback. Directory names and audio files are ordinary Windows files under the simulated root. Use the bundled test audio for setup verification; do not point the simulator at a real player or a broad system directory.

The `.rockbox` subdirectory contains installed simulator resources, codecs, plugins, themes, and settings. The rebuild script installs the resources after compiling. The Rockbox Database is a separate indexing feature; opening Files does not require building the database.

To compare a label change, return to the top-level menu after relaunching. Rockbox may restore the last screen, so a successful relaunch can resume a file browser or playback screen instead of displaying the main menu immediately.

## Reversible C source proof

The small setup demonstration changes only the Files menu label for `SIMULATOR` builds. It is a proof that editing C, rebuilding, and relaunching changes the visible program. It does not implement a custom interface.

In the unmodified checked source, `C:\RockboxDev\rockbox\apps\root_menu.c` near line 509 contains:

```c
MENUITEM_RETURNVALUE(file_browser, ID2P(LANG_DIR_BROWSER), GO_TO_FILEBROWSER,
                        NULL, Icon_file_view_menu);
```

A reversible simulator-only form is:

```c
#ifdef SIMULATOR
MENUITEM_RETURNVALUE(file_browser, "Files (Dev)", GO_TO_FILEBROWSER,
                        NULL, Icon_file_view_menu);
#else
MENUITEM_RETURNVALUE(file_browser, ID2P(LANG_DIR_BROWSER), GO_TO_FILEBROWSER,
                        NULL, Icon_file_view_menu);
#endif
```

Why this changes the screen: `MENUITEM_RETURNVALUE` stores the description in the item. `get_menu_item_name` in `C:\RockboxDev\rockbox\apps\menu.c` returns `P2STR(menu->callback_and_desc->desc)`. `P2STR`, defined in `C:\RockboxDev\rockbox\apps\settings.h`, resolves an encoded language ID or leaves an ordinary string pointer unchanged. This item has a `NULL` callback and no dynamic-text flag. Its label therefore comes from this C definition. The main-menu callback handles item visibility and does not substitute a different Files label.

To revert just this demonstration, replace that conditional block with the original two-line definition shown above, save, run `C:\RockboxDev\build.ps1`, and relaunch with `C:\RockboxDev\run.ps1`. Inspect `git diff -- apps/root_menu.c` first so any later edits in the same file are preserved. Avoid restoring the entire source tree to revert one demonstration.

The demonstration is also isolated in Git commit `b14eacc693`. With the simulator closed, `git -C C:\RockboxDev\rockbox revert b14eacc693` reverts only that commit; then build and relaunch. This preserves the toolchain fixes. Manual display, navigation, and audio testing is left to you as requested.

For production strings, use the language system. A literal is appropriate for this temporary, simulator-only proof; the original translated label remains in the non-simulator branch.

## What the simulator verifies

The simulator runs shared Rockbox C code, target-specific display configuration, Clip button/action mappings, menus, file browsing, playback, and software codecs on the Windows host. It is useful for developing and reviewing UI behavior before the player arrives.

It does not emulate the Clip's ARM CPU, electrical buttons, flash layout, real LCD timing, battery, storage controller, USB implementation, or hardware audio characteristics. Runtime speed, memory behavior, alignment, interrupt timing, and available host resources can differ from a physical player. Successful simulation is not a hardware firmware or bootloader test.

This setup uses the simulator build type. Keep device firmware builds, bootloader work, and flashing outside this workflow until the exact hardware is identified and separate work is requested.

## Upstream build documentation

`C:\RockboxDev\rockbox\docs\UISIMULATOR` documents creating a separate build directory, configuring a target simulator, running Make, installing to simulated storage, and launching. Its version-control references are dated, so the actual checked `tools\configure` and Makefiles determine this setup's SDL version, Windows host handling, target names, and generated executable.

`C:\RockboxDev\rockbox\docs\README` primarily describes physical firmware builds. Its cross-compiler, firmware ZIP, and player-installation steps are not needed for this Windows simulator.
