# Clip+ Home score save/reload bugfix

## Summary

The Clip+ score reader now parses `home-scores.tsv` without relying on
`sscanf`, and every score-store I/O failure is written to the existing Rockbox
disk log. The ARM firmware and the Windows simulator both build, the native
Home suite passes with the host C library and with the firmware scanner linked
in, and the Clip+ package was installed and hash-verified.

The next hardware run identified the boot/save warning in `/rockbox_log.txt`:
`E [score_log]reload-open result=-73 errno=9`. A missing score file correctly
returns the native "not found" result, but the file-open wrapper closes a stream
already destroyed by its inner routine. That cleanup replaces `ENOENT` with
`EBADF` (9). Home treats this as a failed initial load, marks its index
incomplete, and rejects every subsequent save. The native open wrapper now
preserves the original error across cleanup; Home's protection against writing
an incomplete index remains in place.

## Parser defect

`apps/home_scores.c` used:

```c
sscanf(line, "%lu\t%lu\t%lu\t%lu\t%lu\t%n", ...)
```

Rockbox's firmware scanner in `firmware/libc/sscanf.c` has no `%u`/`%lu`
conversion and its `%n` assignment count differs from the host C library. A
valid row therefore failed to reload on the device even though the same row
parsed in the simulator.

## Changes

- Replaced the score-row `sscanf` with five checked decimal `strtoul`
  conversions. Parsing now requires tab separators, rejects signed or
  whitespace-prefixed fields, and rejects values outside `uint32_t`.
- Preserved the TSV field order, path escaping (`%25`, `%09`, `%0A`, `%0D`),
  latest-row-wins behavior, and the public score APIs.
- Added disk-logger records for reload open/seek/read/allocation/close, record
  open/truncate/allocation/seek/write/sync/rollback/close, and compaction
  open/seek/read/write/sync/close/rename/remove failures. Each entry contains
  the operation, return value, and saved `errno`; allocation failures include
  the requested byte count.
- Preserved the rule that an incompletely loaded index never overwrites the
  score store. The later "index incomplete" rejection is intentionally not
  logged again, so the original failure appears once.
- Updated `DEVELOPMENT.md` with the firmware scanner limitation and the
  `/rockbox_log.txt` location.
- Preserved `errno` across failed native-open cleanup in `firmware/common/file.c`.
  A regression compiles the actual open/close wrapper and checks missing files,
  other open failures, descriptor exhaustion, invalid flags, and successful reuse.

## Internal-drive filesystem warning

A read-only Windows check on November 6 found the internal FAT16 volume marked
dirty, an invalid first cluster for `.rockbox/langs/ukrainian.lng`, and one
32 KiB lost chain. The microSD volume was not marked dirty. These filesystem
errors explain Windows' drive warning; they do not change the traced `errno`
bug above. Before repair, the entire 1,946,157,056-byte internal volume was
imaged and its SHA256 verified under
`C:\RockboxDev\logs\score-recovery-20261106\internal-before-repair.img`.

`chkdsk D: /f /x` corrected the invalid language-file entry and recovered the
lost chain as a file. The language file was restored from the verified firmware
package. A post-install read-only `chkdsk D:` returned 0 and found no problems;
both D: and E: reported "NOT Dirty". The microSD was not modified.

## Verification

All six checks in `tools/windows-simulator/tests/test-home.py` passed. Two
generated executable launches initially encountered Windows Application Control;
both passed on an ordinary retry without changing policy. The new native-open
regression failed against the original routine and passed with the five-line
cleanup fix.
The harness now runs twice: once with the host C library and once with
`firmware/libc/sscanf.c` linked in. Coverage includes:

- first save with no score file, repeated plays, reload, and decay;
- Unicode and escaped paths, including embedded tab, `%`, and newline;
- malformed rows, oversized fields, relative paths, and interrupted rows;
- injected open, read, allocation, short-write, write, sync, close, and rename
  failures, with the expected logged stage and older records preserved;
- compaction failure and recovery, followed by a successful compaction.

The Windows simulator rebuilt successfully for the earlier parser fix. The
native filesystem change is checked by the new wrapper regression and the ARM
build; the simulator uses a different filesystem backend.

The Clip+ ARM firmware was rebuilt in the homelab Docker toolchain with two
jobs:

```text
Target: sansaclipplus
Version: d3594414a5-261106
Binary size: 993412
rockbox.zip SHA256:
99213a44268a90fbbb2a36574f53ef1b63258e685829ff68c5e6e42ecae036e8
rockbox.sansa SHA256:
4ce02311d8b5538af535de9583b29d1ad44e45434fc19e3e3cc51cf1b8d3422f
```

## Device delivery

Current backup, original error log, build log, CHKDSK results, installed-file
hashes, and transfer manifest:
`C:\RockboxDev\logs\score-recovery-20261106`

The full volume image preserves the pre-repair filesystem, including damaged
entries. Earlier file backups, including internal/card playlists, remain under
`C:\RockboxDev\logs\device-transfer-20261106`.

Installed package:
`C:\RockboxDev\logs\score-recovery-20261106\rockbox.zip`

Only changed build files and the damaged language resource were written to
`D:\`. All 342 package files matched their SHA256 values after installation;
38 configuration, playlist, database, resume, and queue files were hash-checked
and unchanged. `D:\.rockbox\rockbox-info.txt` reports
`Version: d3594414a5-261106`. The installed `rockbox.sansa` hash matches the
build above, and the volume's write buffers were flushed.

The old `/rockbox_log.txt` was archived locally and removed from the player so
any new log describes the corrected build. No score file was pre-created.

At the end of the USB session, `/.rockbox/home-scores.tsv` and
`/rockbox_log.txt` were still absent because no playback had occurred since
installation. This is expected before the first successful save.

## Remaining hardware tests

These require physical use and were not performed during the USB session:

1. Confirm the player boots without the score warning. Its previous log dates
   were in 2100; set the correct player date/time for score decay.
2. Start a Home recommendation, return Home while playback continues, then
   confirm returning Home alone does not credit or stop the play.
3. Stop after half the track and confirm one point; let a track finish and
   confirm two points.
4. Switch Quickplay sessions and confirm scores continue without mixing queues.
5. Restart the player and confirm scores and counters reload.
6. Reconnect USB and inspect `/.rockbox/home-scores.tsv` and
   `/rockbox_log.txt`.

The original boot/save warning is now traced to native-open cleanup replacing
`ENOENT` with `EBADF`. Successful saving and reloading with the corrected native
open routine still requires the hardware steps above.

## November 7: playback panic during database updates

The reported `audio_reset_bu...` panic on Study → PRIDE. matches the audio
allocator's fatal out-of-memory path. The song fully decodes without errors;
the connected internal filesystem passes CHKDSK and the rebuilt 802-track
database is clean. No panic dump was saved, so the precise hardware timing is
inferred from the code and the regression reproduction.

Even a scan with no new songs previously rebuilt every index and marked the
database dirty. Its maximum-sized work allocation left no memory for audio
started during a yield. A panic could interrupt that dirty commit, causing the
database initialization prompt after reboot. Empty commits now discard only the
empty temporary scan. Clip/Clip+ commits request one eighth of RAM (256 KiB /
1 MiB), retaining memory for playback; failed work-buffer allocations still
defer the commit before marking the database dirty.

The regression compiles the production commit and audio allocation routines,
starts playback during index construction, checks no-op scans and failed
allocations, and runs for both Clip RAM sizes. The old allocator reproduces
`audio_reset_buffer(): OOM!` in this scenario. Player backup, song verification,
regression/build logs and installation hashes are under
`C:\RockboxDev\logs\playback-panic-20261107`. Physical playback after USB
disconnection remains a user check.

Installed Clip+ build **6f74a3d25a-261107** on November 7. All 336 installed
build files match the package; 69 personal files, including both volumes'
playlists and the database/score/resume data, match their backups. All 802
database paths resolve, including PRIDE. The internal filesystem passes the
post-install CHKDSK check and both volumes report not dirty. The seven focused
checks pass; the old-allocator and old-empty-commit negative controls each fail
as expected. The source fix is pushed to the user's `main` branch. Safely eject
both volumes, disconnect USB and restart before the physical playback check.
