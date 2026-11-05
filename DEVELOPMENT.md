# Development notes

Work in `C:\RockboxDev\rockbox`. Build with `build.ps1 -StopSimulator`, then
launch with `run.ps1`. The simulator is a native Windows application; it runs
shared Rockbox C code with original Clip display and button configuration.

## Source map

| Area | Files |
| --- | --- |
| Main menu and navigation | `apps/root_menu.c`, `apps/menu.c`, `apps/menu.h` |
| Clip Home, suggestions, and pins | `apps/home_screen.c`, `apps/home_data.c`, `apps/home_screen.h` |
| Genre autoplay and persistent listening scores | `apps/home_autoplay.c`, `apps/home_scores.c`, `apps/home_autoplay.h` |
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

These compile the production Home data, score store, queue builder, fixed-point
logarithm, and actual playlist progression functions with small Windows host
adapters. They cover genre/track uniqueness, whole-library genre matching,
Unicode paths, refresh/navigation, pins, repeat modes, continuous autoplay,
scoring thresholds and session ownership, duplicate finish events, decay,
restart persistence, partial/failed writes, compaction failures, weighted
selection, 20/20/5 batching without repeats, skipped versus unreached tracks,
legacy queue adoption, and 850/5,000-track memory checks. The adapters supply the
database, metadata and audio events; the checks do not drive GUI or audio playback.

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
3. Initialize the database once using Settings → Settings → General Settings →
   Database → Initialize Now; restart if prompted. Select a Home suggestion and
   confirm it plays first, then another song of the same genre, including songs
   outside Liked Songs. Try saved repeat off, all, one, shuffle, and A–B: genre
   autoplay should continue in each case and preserve the preference. Check that
   the queue contains at most 20 tracks, including the chosen song. Skip through
   batches and confirm no automatic repeats until the pool is exhausted; the
   final batch can be shorter. Check no immediate repeat at the cycle boundary
   when another song is available. A one-song genre should keep playing.
4. Open a pinned mix, choose a track, and confirm normal playlist playback.
   Back from its track list should return Home. Insert should always return Home.
5. Open **Settings → Playlists**, hold **Space** on Test Mix 3, and pin it.
   Return Home to see it after the existing pins. Unpin it and restart to check
   persistence. The playlist itself should remain available.
6. Open Files or a settings submenu through the bottom Settings entry. Back
   should return to that menu; Insert should return to the song-first Home.
7. After 30 minutes away from Home, return and confirm that suggestions refresh.
   Do not expect a refresh merely from sitting on Home.
8. During Home autoplay, skip before halfway, skip/stop after halfway, and let a
   song finish. The latest matching TSV rows should gain 0, 1, and 2 points
   respectively. Pausing should not create a row. Repeat through a pinned mix
   and confirm its playback does not update these scores.
9. Stop, restart the simulator, and resume the playlist. Check the saved queue
   order, used-song history across batches, and continued scoring. Selecting a
   new suggestion, even of the same genre, should reset history. Update the
   database after changing the library and confirm the scores persist. New
   sessions use the updated library; an existing session keeps its genre pool
   until you start a new one.

The Home sources and integration are compiled only for `SANSA_CLIP`; they share
the simulator and device code path. `home_play_suggestion(path)` validates the
database/seed, stops old playback, prepares a weighted genre playlist, and starts
it with `PLAYLIST_FLAG_HOME_AUTOPLAY`. This persisted flag overrides effective
repeat behavior without changing saved settings. At the existing repeat-shuffle
boundary, autoplay writes up to 20 unused paths to the inactive one of two M3U8
files, then loads it through the normal playlist API. The selected suggestion
occupies the first slot of a new session. The configured playlist-size limit
caps each batch, rather than the genre pool. A bounded max-heap retains only the
best weighted ranks, then sorts that batch. Every refill scans the saved pool;
used paths need no score lookup, and tags are searched only for a new session.

Two pool slots (`home-autoplay-pool-a` and `home-autoplay-pool-b`) hold UTF-8
paths and one-byte played markers. The first line is `0` or `1`, the marker for
the current cycle. Each remaining LF-terminated row is a marker immediately
followed by an absolute path. A row matching the header has already started.
After exhaustion, flipping and syncing the header resets the cycle without
rewriting all rows. Missing paths are ignored; no playable paths means stop.
An existing full queue without pool metadata is adopted at its next boundary.

M3U comments `#HOME-POOL:a` or `#HOME-POOL:b` bind a queue to its pool, and
`#HOME-OFFSET:<byte offset>` before each path permits a direct marker update.
Track-change events search only the small batch, validate the referenced path,
and sync its marker. Queuing alone does not consume a song. Resume uses these
same files; selecting a new suggestion builds the unused pool slot before
publishing the new queue. Failed preparation leaves the previous queue and its
pool intact. A failed progress write prevents automatic refill for that session;
missing or malformed pool data also stops refill instead of clearing history.

Track-change events capture session ownership, so replacing the playlist still
credits the old Home track correctly. Finish events append one small score row;
rewind notifications and duplicate finishes are ignored. A one-second end
tolerance accommodates codec rounding; an automatic decoder failure far from
the end is not a full completion. Explicit stops clear the automatic-finish flag
on the Clip. Successful appends are synced; short/failed writes are rolled back.
Home shows a save-failure message on the next visit if storage rejected a score.

The score index stores hashes, numbers, and file offsets, with full paths kept
on disk and compared to resolve hash collisions. Its capacity doubles as needed.
An 850-track host check retains 28 KiB for the score index and adds only 320 bytes
while constructing a batch (20 records of 16 bytes on the Windows host), versus
13,600 bytes for the former full-genre sort. Index growth can still temporarily
retain old and new allocations. A 5,000-track pool uses the same 20-record queue
allocation. There is no recommendation thread or periodic scan.
Decay and weighted selection use fixed-point arithmetic, calculated only when
reading/updating scores or generating picks. A bad/backward clock does not
increase a score through decay.

Pins and generated queues use checked temporary files, sync, and replacement
rename. Score rows are consolidated only on Home refresh after their count
exceeds four times the number of tracked songs plus 64. Failed consolidation
preserves the original file. A power loss can discard the last unfinished
append; older complete records remain readable.

### Listening data for external tools

Copy `.rockbox/home-scores.tsv` after stopping playback. It is a UTF-8,
tab-separated file with no header. Ignore blank/malformed lines and an
unterminated final line. The **last complete row for each decoded path wins**;
rows are cumulative snapshots, not additive events. The six columns are:

| Column | Meaning |
| --- | --- |
| `score_q16` | Unsigned fractional points; divide by 65,536 for points |
| `updated` | Player-clock epoch seconds since 1970; 0 means its clock was invalid |
| `completed` | Cumulative natural completions |
| `late_exits` | Cumulative non-completion exits at or beyond halfway |
| `early_exits` | Cumulative other exits, including unknown-duration partial plays |
| `path` | Exact, case-sensitive Rockbox music path in UTF-8 |

The path escapes `%`, tab, carriage return and newline as `%25`, `%09`, `%0D`
and `%0A`. Decode once with a percent decoder, such as Python's
`urllib.parse.unquote`. Map `/MusicLibrary/...` to the corresponding local or
homelab music root and read artist/title/album tags from the music itself.
Different capitalization identifies different paths. Moving/renaming a file
starts a separate score; database rebuilds do not change path identity.

For current recommendations, apply
`(score_q16 / 65536) * 2 ** (-max(0, now - updated) / 2592000)` using a clock
consistent with the player's. If either timestamp is invalid, keep the stored
balance. The device uses an integer approximation, with exact halving per full
30-day interval, before adding new points. Counts never decay. This version
provides readable data for external tools; automatic score import and external
service integration are outside the feature.

The setup compiled the simulator, codecs, and plugins and launched a responding
Sansa Clip window. Manual button navigation, visible labels, file browsing, and
audio playback remain user tests. Return to the main menu after relaunching to
inspect the label; Rockbox may restore its previous screen.

Windows compatibility fixes in `tools/addtargetdir.pl` and `tools/genlang`, plus
LF handling for theme packaging, are preserved in Git. The regression checks
remain under `tools/windows-simulator/tests/`.

The simulator does not emulate the ARM CPU or verify physical hardware. Device
firmware, USB devices, bootloaders, and flashing are outside this workflow.
