# Rockbox Windows setup verification

Date: October 23, 2026. Manual behavior testing was handed to the user at their request.

| Check | Observed result |
| --- | --- |
| Source | C:\RockboxDev\rockbox; official baseline 9377ad18c79d274a5abec6ee0cc536f59eb7c019 |
| Build directory | C:\RockboxDev\build-sansaclip-sim |
| Target | Original SanDisk Sansa Clip v1, sansaclip, SANSA_CLIP, 2 MB target configuration |
| Toolchain | MSYS2 UCRT64; GCC 16.2.0; binutils 2.47; GNU Make 4.4.1; Perl 5.42.3; SDL2 2.32.10; diffutils 3.12 |
| Native executable | rockboxui.exe; PE x86-64, native Windows; system Windows DLL imports, no msys-2.0.dll dependency |
| Full build | Passed, codecs/plugins linked |
| Final PowerShell build/install | Exit 0; build-final.log; required installed resources checked |
| Dependency path regression | 12/12 passed |
| Generated language regression | Native GCC syntax and preprocessing checks passed |
| C edit/rebuild proof | apps/root_menu.c rebuilt; Files (Dev) found in linked executable |
| Reversible proof commit | b14eacc693; git revert retains setup fixes |
| Startup | run.ps1 succeeded; process 14592 still running, Responding=True; window title Sansa Clip; nonzero window handle |
| Storage | simdisk\Music\Rockbox Test Tone.wav, 12 s mono PCM16 22050 Hz, quiet original tone |
| Manual behavior checks | Not performed: visible menu rendering, button input, browsing, playback/audio; user will test |
| Device/homelab actions | No firmware download, flashing, bootloader installation, USB device modification, or homelab deployment |

The final runtime was left open for manual testing. See README.md and DEVELOPMENT.md for daily commands, controls, source map, exact fixes, and safe demo reversal.