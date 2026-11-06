# Clip+ Home score save/reload bugfix

## Summary

The Clip+ score reader now parses `home-scores.tsv` without relying on
`sscanf`, and every score-store I/O failure is written to the existing Rockbox
disk log. The ARM firmware and the Windows simulator both build, the native
Home suite passes with the host C library and with the firmware scanner linked
in, and the Clip+ package was installed and hash-verified.

The missing `/.rockbox/home-scores.tsv` and the user-visible warning cannot yet
be attributed to a specific device operation. The old code hid the failing
operation; this build records it for the next hardware run. The parser defect
explains why previously written scores would fail to reload, not why the first
file creation failed.

## Root cause

`apps/home_scores.c` used:

```c
sscanf(line, "%lu\t%lu\t%lu\t%lu\t%lu\t%n", ...)
```

Rockbox's firmware scanner in `firmware/libc/sscanf.c` has no `%u`/`%lu`
conversion and its `%n` position differs from the host C library. A valid row
therefore failed to reload on the device even though the same row parsed in the
simulator.

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

## Verification

Local host checks (`python tools/windows-simulator/tests/test-home.py`) pass.
The harness now runs twice: once with the host C library and once with
`firmware/libc/sscanf.c` linked in. Coverage includes:

- first save with no score file, repeated plays, reload, and decay;
- Unicode and escaped paths, including embedded tab, `%`, and newline;
- malformed rows, oversized fields, relative paths, and interrupted rows;
- injected open, read, allocation, short-write, write, sync, close, and rename
  failures, with the expected logged stage and older records preserved;
- compaction failure and recovery, followed by a successful compaction.

The Windows simulator rebuilt successfully.

The Clip+ ARM firmware was rebuilt in the homelab Docker toolchain with two
jobs:

```text
Target: sansaclipplus
Version: 2679012cb1-261106
Binary size: 993396
rockbox.zip SHA256:
0bad8cfc33c8e4d7f8e4b0d069f9ad431e2a78b48e96d853767ab036cffee087
rockbox.sansa SHA256:
64332ff9893a14d20265c16e289e629f70f60b8c1f49f8abfd39da77fb8079fc
```

## Device delivery

Player backup:
`C:\RockboxDev\logs\device-transfer-20261106`

The backup contains the previous `D:\.rockbox` (except the unreadable
`database_tmp.tcd` temporary file), internal playlists, card playlists, and
`version.sdk`. Settings, pins, database indexes, resume state, autoplay queue,
and pool were preserved.

Installed package:
`C:\RockboxDev\logs\device-transfer-20261106\rockbox.zip`

Extracted to `D:\`; all 342 package files matched their SHA256 values after
installation. `D:\.rockbox\rockbox-info.txt` reports
`Version: 2679012cb1-261106`. The installed `rockbox.sansa` hash matches the
build above. The firmware contains the new operation strings, including
`record-open`, `reload-read`, `record-alloc`, and `rockbox_log.txt`.

At the end of the USB session, `/.rockbox/home-scores.tsv` and
`/rockbox_log.txt` were still absent because no playback had occurred since
installation. This is expected before the first successful save.

## Remaining hardware tests

These require physical use and were not performed during the USB session:

1. Start a Home recommendation, return Home while playback continues, then
   confirm returning Home alone does not credit or stop the play.
2. Stop after half the track and confirm one point; let a track finish and
   confirm two points.
3. Switch Quickplay sessions and confirm scores continue without mixing queues.
4. Restart the player and confirm scores and counters reload.
5. Reconnect USB and inspect `/.rockbox/home-scores.tsv` and
   `/rockbox_log.txt`.

The save warning is explained only if a new `/rockbox_log.txt` entry names the
failing operation, or if this build visibly saves and reloads scores on the
player. Until then the device-side cause remains open.
