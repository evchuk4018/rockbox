# Development notes

Work in `C:\RockboxDev\rockbox`. Build with `build.ps1 -StopSimulator`, then
launch with `run.ps1`. The simulator is a native Windows application; it runs
shared Rockbox C code with original Clip display and button configuration.
The physical player is a Clip+. Build its ARM firmware with
`scripts/build-clipplus.sh` in a Linux Rockbox toolchain environment; see
README.md for installation and the device test procedure.

## Source map

| Area | Files |
| --- | --- |
| Main menu and navigation | `apps/root_menu.c`, `apps/menu.c`, `apps/menu.h` |
| Clip Home, suggestions, and pins | `apps/home_screen.c`, `apps/home_data.c`, `apps/home_liked.c`, `apps/home_screen.h` |
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
| Clip and Clip+ display and features | `firmware/export/config/sansaclip.h`, `firmware/export/config/sansaclipplus.h` |
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
restart persistence, malformed/interrupted rows, injected open/read/allocation/
write/sync/close/rename failures, compaction failures, weighted selection,
20/20/5 batching without repeats, skipped versus unreached tracks, legacy queue
adoption, and 850/5,000-track memory checks. They also cover playlist-backed
likes, relative/Unicode/case-sensitive paths, duplicate removal and save
failures, custom short/long button events for Clip and Clip+, unchanged Clip v2
button behavior, and deferred genre changes through production playlist
look-ahead and progression. The native harness runs twice: once with the host C
library and once with the firmware scanner (`firmware/libc/sscanf.c`) linked in.

The same checks compile the production Add to Playlist writer and WPS action.
They cover duplicate suppression, relative/nested and Unicode paths, legacy M3U
conversion, BOMs, comments, line endings, empty/short files, cancellation, read
and save failures, liked-song revision/cache updates, and unchanged queue/score
state. Both shortcut release orders and a track change during the picker are
checked with host button/UI adapters; USB during release handling exits safely.
The runner also accepts Linux host GCC (`python3 tools/windows-simulator/tests/test-home.py`),
so the same suite can run in the homelab build container when Windows Application
Control blocks generated helper executables. No security-policy change is needed.

The firmware C library does not implement `%u` or `%lu` conversions and its
`%n` assignment count differs from the host library. The score reader therefore parses
the five decimal fields with checked `strtoul` calls, requires tab separators,
and rejects values above `uint32_t`; do not reintroduce `sscanf` for score rows.
Home score I/O failures are written through Rockbox's disk logger to
`/rockbox_log.txt` on the player. Each entry names the failing operation and
records the return value and saved `errno`; allocation failures also include the
requested byte count. After reconnecting the player, inspect that log before
treating a score warning as explained.

The native file-open regression also compiles the production open/close wrapper.
A failed open can already have destroyed its stream; cleanup must preserve the
original `errno` instead of replacing `ENOENT` with `EBADF`. Otherwise a missing
score file makes the initial index incomplete and blocks all subsequent saves.

The adapters supply the database, metadata and audio events; the checks do not drive GUI or audio playback.

The database/playback regression compiles the production commit and audio-buffer
allocation functions for both Clip targets. It starts audio while a nonempty
commit holds its work buffer, checks empty auto-updates do not dirty/rewrite the
database, and checks allocation failure defers the commit with the old database
clean. Clip commit work memory is bounded to one eighth of target RAM (256 KiB
on Clip, 1 MiB on Clip+); do not replace it with `core_alloc_maximum`, which can
starve a simultaneous playback start. An interrupted dirty commit leaves the
database unavailable on reboot. Hardware check: start Study → PRIDE. soon after
boot, repeat during Database → Update Now, then reboot and confirm the database
is still usable. The host regression checks allocation overlap, not PCM decoding
or physical controls.

The existing regression checks run in the MSYS2 UCRT64 environment:

```powershell
. .\scripts\Rockbox.Common.ps1
& C:\msys64\usr\bin\perl.exe .\tools\windows-simulator\tests\test-dependencies.pl
Invoke-RockboxBash -Msys2Root C:\msys64 -Script "$PWD\tools\windows-simulator\tests\test-genlang.sh"
```

After building and seeding, the user should check the following with `run.ps1`:

1. Press **Insert** for Home. Confirm the existing header, three song rows,
   Test Mix 1 and 2, then Settings. Long selected titles should scroll. **Right**
   selects an entry; **middle/Space** pauses/resumes background playback without
   leaving Home or selecting the highlighted item. With no audio it does nothing.
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

10. In Now Playing, tap Space twice to pause/resume. Tap Up to toggle the heart,
    then re-enter the screen/restart to check persistence. Hold Up for pitch and
    hold Space for the context menu; releasing either must not toggle a like or
    pause. Confirm list Up/Space navigation still works.
11. Check the Clip Cabbie reference layout: yellow title, blue artist, smaller
    album, taller animated audio bars, left elapsed/right total time, progress, then battery,
    shuffle, playback, playlist-plus, and heart. Check scrolling and a long album title;
    album/title duplicates should hide the album line. Low bars should stay low,
    while strong peaks jump higher for a more energetic contrast. Pause and
    silence should show the baseline. Check the playlist-plus icon, seeking, pause,
    and the end of a track. The counter, lock/volume icons, and Next stay absent.
12. Start Home autoplay, hold Insert, then press Left in the Quick Screen. Confirm
    Change Genre shows its target and the asterisk appears. Let the current song
    finish: it must not restart or briefly
    play an old-genre successor. Repeat using manual Next. Pause/seek should keep
    the request; Stop, backward navigation, another playlist and restart should
    clear it. Try a liked playlist with one genre and check the explanatory message.
    Check ordinary-playlist shuffle still toggles with the same shortcut.
13. During Now Playing, hold middle and press Up (Space + Up in the simulator),
    then release both keys. Confirm the picker does not move or select an entry
    on release. Scroll Up/Down and choose a playlist with middle; verify the
    saved song and Added to playlist message. Try it again for Already in
    playlist, and use Left to cancel without changes. Open the picker near a
    track boundary and confirm it adds the original song. Try Liked Songs and
    check the heart and Home refresh. Playback and the active queue must continue
    normally. Repeat remains available in playback settings and the Quick Screen.

Likes use a bounded streaming reader and the same path resolver as Rockbox M3U
playback, with exact case-sensitive identity. Writes use a synced temporary file
and replacement rename; failure preserves the previous playlist and displayed
membership. Membership is cached per displayed track, invalidated on track change
or WPS re-entry. A revision counter invalidates Home suggestions on the next visit.
The Clip/Clip+ Middle + Up action captures the current path before consuming the
whole button chord. `catalog_add_track_to_a_playlist` uses the existing catalog
picker and a bounded streaming replacement writer; a duplicate leaves the file
unchanged. UTF-8 playlists retain their bytes, comments and line endings. Legacy
M3U text uses Rockbox's configured codepage and is converted to UTF-8 with a BOM
when adding, retaining line endings and relative entries. Temporary `.tmp` files
are synced and closed before rename; failed saves retain the existing playlist.
Successful Liked Songs additions invalidate membership and advance its revision.
Path dot-segment removal walks back each parent component, including consecutive
`..` segments in nested playlists.
The Clip/Clip+ skin conditions are `%hL` (liked), `%hA` (Home autoplay), and `%hG`
(pending genre). The reference bars and footer icons are small monochrome bitmap
assets. Built-in `%pL`/`%pR` conditions select twelve bar frames per channel in
adjacent 39-by-10 viewports. Two shape phases alternate every 0.1 seconds while
playing (24 images per strip). Their existing peak-meter refresh path animates them;
no custom PCM renderer or new skin token is needed. The bar-image height mapping
keeps original 1/2/3/4-pixel bars unchanged, expands 5 to 7 and 6 to 10, and does
not amplify the quiet frames. This increases peak contrast instead of lifting
the entire display. Paused/stopped
playback uses the baseline frame. Source palette uses black ink on white, which
the mono loader maps to lit Clip pixels. `clip_home` selects the Clip Cabbie variant during packaging.

Change Genre prepares the inactive pool/queue under the playlist lock without
stopping audio. Pending positive peeks/checks see that queue; forward progression
adopts it before announcing the new track. Flushing buffered successors preserves
the playing track and updates buffered successor metadata. Stop and playlist replacement clear
the volatile request; no extra persistent pending-request file is needed. Old-track
finish scoring remains attached to its original play. A queued genre starts a fresh
used-song cycle, then uses the existing 20-song batching and persistence.

The Home sources and integration are compiled for `SANSA_CLIP` and
`SANSA_CLIPPLUS`; they share the simulator and device code path. Both targets
select the custom Cabbie skin through `clip_home`. `home_play_suggestion(path)` validates the
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

The simulator does not emulate the ARM CPU or verify physical hardware. The
separate Clip+ build compiles the actual ARM firmware, codecs, plugins, and
theme. Installation replaces build files under `.rockbox` through the existing
bootloader; it does not flash a bootloader. Verify copied files against the build
archive and music sources, then leave physical controls and audio playback to
manual testing after USB disconnection.
