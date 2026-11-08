# Rockbox on Windows

The portable **Rockbox Manager** desktop app manages the Clip+ library, mirrored
playlists, likes and automatic monochrome covers. See [its usage and build guide](utils/rockbox-manager/README.md).

Native Windows simulator setup for the **original SanDisk Sansa Clip v1**:
Rockbox target `sansaclip` / `SANSA_CLIP`, 128×64 display. The connected physical
player was identified on November 5, 2026 as a **Sansa Clip+**, target
`sansaclipplus` / `SANSA_CLIPPLUS`. Both targets include the custom Home and
playback features. Hardware builds use the separate Linux ARM workflow below.

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
needed. This simulator workflow does not require WSL, Docker, or a cross-compiler.

## Clip+ hardware testing

The Windows `rockboxui.exe` is only a simulator. For the physical **Clip+**, run
`bash scripts/build-clipplus.sh 2` in a Linux environment with the Rockbox
`arm-elf-eabi` toolchain. It configures target `sansaclipplus`, builds matching
firmware, codecs and plugins, and writes `build-sansaclipplus/rockbox.zip`.
Alternatively, the included Dockerfile provides Debian's ARM compiler and sets
`ROCKBOX_COMPILER_PREFIX=arm-none-eabi-` for the same helper. From a Linux checkout:

```bash
docker build -t rockbox-clipplus -f scripts/Dockerfile.clipplus scripts
docker run --rm --user "$(id -u):$(id -g)" -v "$PWD:/work" rockbox-clipplus \
    bash scripts/build-clipplus.sh 2
```

This compiler differs from upstream's recommended GCC 9.5.0, so configure prints
a toolchain warning. The prebuilt `built1n/rbclient` image mentioned in upstream
documentation contains GCC 4.4.4 and cannot build this source. The homelab is
available through `ssh evanh@homelab` when a Linux build host is needed. Limit
builds to two jobs on the shared server.

Check the player's `version.sdk` and `.rockbox/rockbox-info.txt` before choosing
a target. A Clip, Clip v2, and Clip+ require different firmware. On the connected
player these identify `Product: Clip+` and `Target: sansaclipplus`; the internal
storage mounted at `D:\` during this transfer. Drive letters can change.

For a player with Rockbox already installed, back up `.rockbox`, then extract
the new archive into the player's root, replacing its `.rockbox` build files.
Keep existing settings, fonts, and personal data. This updates Rockbox through
the existing bootloader; a new bootloader or stock firmware update is unnecessary.
Do not copy simulator codecs or executables to the device.

Copy a small set of MP3s into `/MusicLibrary`, preserving tags and artist folders,
then seed test playlists with `python scripts/seed-home-playlists.py --simdisk D:\`.
The helper preserves existing playlists. On a newly seeded test player,
all copied tracks start in Liked Songs and every test mix appears on Home.
Eject the player through Windows, disconnect USB, and restart it. Open
**Settings → Settings → General Settings → Database → Initialize Now** and
restart if requested before testing Home genre autoplay. Saved playlists can
play immediately. On the player, the middle button pauses/resumes, Up toggles
the like, and Home returns to the custom Home screen. Set the player's date and
time if needed so listening-score decay uses the correct clock.

The November 5 transfer's original Rockbox backup and song hash manifest are
local, ignored files under `logs/device-transfer-20261105`. Restore the backed-up
`.rockbox` folder to return to the previous installation. Hardware playback and
button testing must be done on the player after disconnecting USB.

## Storage, controls, and cleanup

Put audio in `build-sansaclip-sim\simdisk\Music`. A quiet original test WAV is
included locally. In the simulator use arrows to navigate, **Space** to select,
Insert for Home, and F1 for key help. Enter is not mapped to Select.

## Song-first Home

The Sansa Clip and Clip+ targets open a scrolling Home list beneath the
existing header: **Search**, three liked-song suggestions, every playlist in the configured
catalog (normally `/Playlists`), **Artists**, **Albums**, then **Settings**.
Playlists are alphabetized, nested folders are included, and duplicate names
show their relative folder. The final Settings entry opens the former Home menu.

On Home, press **Right** to select the highlighted entry. Tap **middle**
(**Space** in the simulator) to return to Now Playing while music is playing or
paused, preserving its state. With playback stopped it does nothing. Inside
Now Playing, middle still pauses/resumes. Other menus keep middle-button selection.

Suggestions come from `/Playlists/Liked Songs.m3u8` and use three different
primary genre tags, without requiring the Rockbox database. They stay fixed
while browsing and refresh on the next Home visit after 30 minutes, or when
you press **Up while already on the first row**. Holding Up does not repeatedly
refresh. Refreshing leaves playing audio alone.

A suggestion starts **genre autoplay**: the selected song plays first, followed
by songs with the same genre from the whole indexed music library, in batches
of **up to 20 songs** including your selection. Finishing a batch automatically
loads the next one. A song counts as used once it starts, even if you skip it;
it cannot be automatically selected again until the genre's playable pool is
exhausted. Songs queued but never reached remain eligible. The last batch can
be shorter than 20, then a fresh cycle begins without immediately repeating the
last song when another is available. A one-song genre repeats that song.

Stopping and resuming, including after a restart, preserves queue order and
used-song history. Selecting another Home suggestion starts a fresh session,
even for the same genre. You can still deliberately replay tracks by going
backward. Your saved repeat and shuffle preferences are preserved. Selecting a
playlist opens its tracks and uses normal playlist playback. **Insert** returns
Home; **Left** goes back. Old `home-pins.txt` files are ignored and left untouched.

**Artists** groups by album artist (falling back to track artist), then shows
albums and All songs. **Albums** lists album title and album artist together;
same-named albums by different artists remain separate. Album tracks use disc
and track order, then title. The main Albums page and its Search show only albums
with at least two indexed songs. Single-song releases remain available in song
search, playlists, artist browsing, and Go to album. Artist pages are unchanged.
These pages use the existing music database; run
Database → Update Now after adding music or correcting tags.

**Search** is the first Home row and opens the existing on-screen keyboard.
The search keyboard has one large uppercase alphabet, digits, and an outlined space
key. Move with the arrows and tap middle (**Space** in the simulator) to enter a
character. **Hold middle/Space to submit**, tap Home (**Insert**) to delete, or
press Power (**Esc**) to cancel.
It finds songs by part of their title, track/album artist, or album name,
ignoring letter case. Results show title and artist, sorted by the existing Home
listening score, highest first. Scores are captured when results are rebuilt;
unplayed songs have score zero and ties use title, artist, album, then path.
Select a
song to play it followed by the remaining results, or hold middle for the shared
song menu. Search uses the same database across internal storage and the SD card.

**Artists** and **Albums** each start with their own Search row, filtering only
artist names or album titles respectively. Opening a match shows its complete
contents; Left restores the filtered parent and selected row. Search remains at
the top so you can edit your query, including when there are no matches.
Canceling an edit keeps the old query; submitting an empty query clears the
artist/album filter or returns Home from song search. Queries last only until
you leave the browser. Unicode text is supported with Rockbox's existing
matching rules; accents are not removed. Typing and browsing keep music playing.
Search includes only indexed files. The simulator's local library can differ
from the player; after adding a file, use Database → Update Now and restart if
prompted before searching for it.

The search update was installed on the Clip+ on November 8, 2026, firmware
**29b328cecb-261108**. All 338 installed build files were hash-verified, and
68 existing personal/settings/playlist files were verified unchanged. The
12-pixel search font is included in normal firmware packages. Backup and
verification records are in `C:\RockboxDev\logs\search-20261108`. Safely eject
both player volumes, disconnect USB, and restart to load it. Physical keyboard
and playback checks remain for the disconnected player.

Hold **middle** on a song in Home, Now Playing, playlists, the queue, or database/
file browsing for the same menu: Play next, Add to queue, Add to playlist,
Like/Unlike, Go to artist, Go to album, and More. More keeps the original advanced
menu. The selected song is captured when opening the menu, even if playback
advances. Play next inserts immediately after the current track; Add to queue
appends. Both preserve playing/paused state and do not start stopped audio.
Explicit queue additions cancel a pending genre change so it cannot discard
the requested song, and genre autoplay continues after the batch finishes.

Create the local test playlists with:

```powershell
python .\scripts\seed-home-playlists.py
```

This creates Liked Songs from all imported MP3s, five deterministic Test Mixes,
and shows every mix on Home. It preserves existing playlists on reruns and
never copies or edits audio. The current library produces
850 liked tracks and five mixes of 170. Generated playlists are ignored
by Git. Replace `simdisk/Playlists/Liked Songs.m3u8` with your prepared UTF-8
playlist when ready; relative paths are resolved from the playlist's directory.
Missing or unreadable tracks are skipped, and Home shows a message if no liked
tracks with usable genres remain.

### Now Playing controls and Likes

On the Clip or Clip+ Now Playing screen, tap the **middle button (Space in the simulator)**
to pause or resume and tap **Up** to like or unlike the song. Holding the middle
button still opens the context menu; holding Up still opens pitch control and
does not also toggle the like. Menu and list controls stay the same. **Down**
opens the queue, **Left/Right** skip tracks, and holding Left/Right seeks.

Hold **middle and press Up** (**Space + Up** in the simulator) to open the
full-screen **Playlists** picker. Release both buttons, scroll with Up/Down,
then press middle to append the song or Left to cancel. Playback continues,
and the picker remembers the song that was playing when you opened it, even
if the next track starts while you browse. An existing song is left unchanged
with **Already in playlist**; a new entry shows **Added to playlist**.
Choosing Liked Songs fills the heart and refreshes Home suggestions on the next
visit. This shortcut selects existing playlists from the configured catalog
(normally `/Playlists`); create playlists through the existing menus or Manager.

Likes are saved directly in `/Playlists/Liked Songs.m3u8`. The blue heart below
the progress bar is outlined when unliked and filled when liked. Unliking removes
all matching entries without changing the current queue or listening scores.
Home refreshes its suggestions on your next visit after a like change. The
playlist is created on the first like if absent; prepared relative paths and
Unicode entries are supported. Saving normalizes it to a plain UTF-8 playlist
of absolute paths, preserving other tracks and their order.

Now Playing uses a single **side-by-side** layout: a full **61×61 cover** on
the left, with **song title and artist wrapping** on the right. The title has up
to four lines and the artist two. Text wraps at measured word boundaries; only
overflow on the final line scrolls. The three-pixel progress bar spans the bottom.
Battery, shuffle, playback state, Add to Playlist, and the like heart share the
row above it on the right. These icons retain the existing physical shortcuts.
The cover uses every available row above the bar, without an added border.
The stacked layout and its setting have been removed. Older saved layout keys
are ignored. Pause, seek, like and Middle + Up for Add to Playlist keep working.

Rockbox Manager automatically prepares covers from embedded/local artwork or
verified MusicBrainz / Cover Art Archive matches, then uses the song's exact
source YouTube thumbnail when release artwork is unavailable. Thumbnail crops
are centered squares. Select a song to preview its
cover, then Send to Player to install the draft. Artist folders may contain
multiple albums: track-specific `.61x61.bmp` files prevent sharing the wrong
cover. Songs without usable release artwork or a source thumbnail use a record placeholder.
No tag changes or database rebuild are needed for artwork. See the
[Manager guide](utils/rockbox-manager/README.md#album-artwork) for defaults,
backfill reports, caching and recovery.

During Home genre autoplay, **hold Home (Insert), then press Left** in the Quick
Screen to select **Change Genre**. A different genre is selected from playable
Liked Songs, using the existing listening weights. The current song finishes;
the next song begins a fresh mix in the selected genre. The Quick Screen shows
the selected genre. Further presses leave the same request queued. **Next** starts it early;
pause and seek preserve it. Stop, backward track navigation, another playlist or
Home session, and restarting clear it. If no other liked genre is available,
the existing mix continues. In ordinary playlists the same shortcut toggles
normal shuffle. The artwork layouts are specific to the Clip's Cabbie theme.

Initialize the music database once under **Settings → Settings → General
Settings → Database → Initialize Now**, then restart if prompted. Genre autoplay
uses this existing index instead of scanning audio files. Use **Update Now**
after adding, removing, or retagging music; normal listening and restarting do
not require an update. Home suggestions themselves still work without the
database, but selecting one asks you to initialize/update it before autoplay.
Empty automatic updates now leave the database untouched. Database updates on
Clip/Clip+ use bounded work memory so starting a saved mix while an update is
running does not exhaust the audio buffer.

Only Home-autoplay listening earns recommendation points, including a resumed
Home session. A natural finish earns **2 points**. Skipping, stopping, shutting
down, or replacing the session at or beyond halfway earns **1 point**; an
earlier exit earns none. These thresholds use the playhead, so seeking forward
can qualify. Pausing does not end a play. Each play is credited once.

Points gradually halve every **30 days**, including time the player is off.
Both Home suggestions and autoplay use `(1 + score)²` as their random-selection
weight: favorites are much more likely to appear early, while new and
lower-scored tracks still get chances. Home keeps three distinct genres.
Scores do not change an already generated batch; they influence the next batch
or session and the next Home refresh. Batch selection is weighted random, so
favorites tend to appear early without a fixed quota of lower-scored songs.

Listening scores and completion/exit counts are saved independently of the
database in `simdisk/.rockbox/home-scores.tsv`. Database updates and simulator
rebuilds preserve them. External recommendation tools can read this documented
UTF-8 format; see **Listening data for external tools** in `DEVELOPMENT.md`.
Generated `home-autoplay-a.m3u8` and `home-autoplay-b.m3u8` files in the same
directory preserve queue order for resume. Their `home-autoplay-pool-a` and
`home-autoplay-pool-b` companion files hold the session's paths and played
markers; keep them with the queues. Missing tracks are ignored, while additions
and tag changes enter autoplay when you start a new session. An older saved
full queue can finish normally and switches to batching at its next boundary.
No network connection is needed.

## Homelab music

The local library is `C:\RockboxDev\MusicLibrary`, copied once from
`evanh@homelab:/srv/storage/media/music`. It is outside the Git repository.
`build-sansaclip-sim\simdisk\MusicLibrary` is a directory junction to that copy,
so the simulator uses the local files without another copy or a live homelab
connection. The original test WAV remains in `simdisk\Music`.

Verified November 5, 2026: 850 MP3 files, 788 directories, 8,840,998,986 bytes.
All file contents match the homelab originals by SHA-256, including Unicode
filenames and folders that differ only by capitalization.

Run `run.ps1`, then open **Settings → Files (Dev) → MusicLibrary**. Use arrows to browse,
**Space** to open a folder or play a track, and **Left** to go back. For browsing
by tags, open **Settings → Settings → General Settings → Database → Initialize Now**,
wait for the scan, and restart the simulator if prompted. For later additions,
use **Update Now** to preserve play counts and ratings. Database files stay in
`simdisk\.rockbox`, separate from the audio files.

The import directory is case sensitive to preserve artist folders such as
`VIOLENT VIRA` and `Violent Vira`. If recreating the import, create an empty
`C:\RockboxDev\MusicLibrary` and run the following from Administrator PowerShell
before copying any files:

```powershell
fsutil file setCaseSensitiveInfo C:\RockboxDev\MusicLibrary enable
```

The copy preserves file contents (including embedded tags and artwork), folder
names, and modification times. Import manifests and verification results are
local, ignored files under `logs\music-import-20261105`. There is no scheduled
sync or homelab service change.

## Pending album transfer

Prepared November 6, 2026: **TV Girl - Who Really Cares**, all ten tracks in
album order, including **Song About Me (feat. Maddie Acid)**. The clean album
audio came from [TV Girl's official Bandcamp release](https://tvgirl.bandcamp.com/album/who-really-cares)
using yt-dlp, without video intros, outros, artwork, or extra tracks.

The local album is `C:\RockboxDev\MusicLibrary\TV Girl\Who Really Cares`.
Both existing transfer trees contain `Playlists\TV Girl - Who Really Cares.m3u8`
and its matching `M\TV Girl\Who Really Cares` folder:

- `C:\RockboxDev\RockboxCardReady`: original 128 kbps MP3 audio, about 34 MiB.
- `C:\RockboxDev\RockboxInternalReady`: compact 64 kbps VBR Opus audio,
  about 17 MiB, matching the internal storage preparation.

A simulator playlist uses the existing `MusicLibrary` junction. All ten tracks
in each format passed full decoding, artist/album tag checks, source-duration
checks, and playlist path validation. The local verification record and source
metadata are under `C:\RockboxDev\logs\tv-girl-who-really-cares-20261106`.

No player was mounted during preparation. On the next connection, identify the
Clip+ internal drive using `version.sdk` and identify any separate microSD
volume; drive letters can change. Copy only the new album folder and matching
playlist from the appropriate transfer tree into the same paths on the chosen
volume. Existing playlists, likes, listening scores, pins, and firmware do not
need replacing. After ejecting, open **Settings → Playlists → TV Girl - Who
Really Cares**. Use database **Update Now** if the new album should also appear
in database browsing and Home genre autoplay. Transfer and physical playback
remain pending. `C:\RockboxDev\NEXT-TRANSFER.md` records the local handoff.

## Build cleanup

`clean.ps1 -WhatIf` previews cleanup; `clean.ps1` removes generated output while
preserving the entire `simdisk` directory. Rebuild afterward. The build installs
codecs and themes as well as compiling the executable. GUI behavior and audio
playback are left for the user's manual testing, as requested.

## Artist and album tag review

`scripts/review-music-library.py` inventories the active local libraries and
creates an evidence-backed correction manifest using cached MusicBrainz searches.
Use `inventory`, then `research`, then `apply`, each with `--work <audit-folder>`.
Use repeated `--root` arguments for explicit libraries. Research does not edit
music. Apply requires a verified recording, matching duration and release track
position. It changes only the reviewed tags, preserves paths and unrelated tags,
backs up changed files once, and verifies encoded-audio hashes before and after.
Unresolved matches keep their existing tags and remain visible in `review.json`.
Full originals and `applied.json` provide the restore set; keep the audit outside
Git. Update the player's database after retagging. The homelab music library is
outside this cleanup's write scope.

## Artwork and Manager update installed November 8, 2026

Installed Clip+ firmware **5f4150ecf6-261108**, built from the committed source
through the homelab. All **337 package files** passed SHA-256 read-back verification.

- Accounted for all **785 current player songs** using Sansa Cover Studio defaults.
- Installed and verified **506 track-specific monochrome covers**.
- Left **277 unavailable/ambiguous matches** on the built-in placeholder.
- Two songs still had temporary artwork-service failures after bounded retries;
  Manager's Retry artwork action can try them later. Their exact paths and errors
  are recorded in the local report.
- Verified all **785 audio files**, totaling **5,100,388,034 bytes**, are unchanged.
- Verified all **70 protected settings, database, playlist and personal files**
  match the pre-transfer hashes. No audio or tags changed; no database rebuild.

This initial version offered two 44×44 layouts. It is superseded by the
side-by-side-only 61×61 cover, title and artist update below.

The updated portable Manager is `C:/RockboxDev/rockbox/build-rockbox-manager/RockboxManager.exe`.
It includes Artists/Albums browsing, album artist and disc/track metadata, exact
genre filtering, cover previews, automatic artwork preparation, verified sends,
caching and retry/recovery. Scanned artwork hints avoid rereading tags for files
without embedded pictures. Temporary HTTP 500/502/503/504 and rate-limit responses
use bounded retries.

Reports and backups: `C:/RockboxDev/logs/album-art-20261108`.
`verification.json` records final totals; `backfill-final.json` accounts for every
song and source; `audio-verification.json` proves unchanged audio;
`firmware-transfer.json` records installed build hashes. Original firmware/settings
are in `device-before/.rockbox`; local artwork operation manifests preserve the
new-file mappings. The final package is `rockbox.zip` in this directory and in
`rockbox/build-sansaclipplus`. The earlier invalid archive is explicitly named
`rockbox-initial-invalid.zip` and must not be installed.

Validation: 33 Manager regressions; eight Home and two library checks on Windows
and Linux; three metadata checks; simulator and ARM builds; portable publish;
embedded/local artwork fixtures and an unchanged second fixture send. Package
validation rejects malformed paths before transfer and checks the bundled search
font. Visual, button and audio acceptance are left to the user as requested.

Safely eject **both player volumes**, disconnect USB, restart, and check both
layouts, progress, liking, long titles and a missing-cover placeholder. The
separate pending album import remains outside this artwork update.

## Side-by-side cover correction installed November 8, 2026

Installed firmware **b47ff0ce6c-261108**. Now Playing has one layout: a full
**61×61 cover** at the left, scrolling **song title and artist** at the right,
a **128×3 progress bar** across the bottom, and a small like heart above the bar.
This is the largest square cover that fits above that bar. Album text is omitted
so albums whose names match the song no longer duplicate the title. The stacked
layout, its setting and unused labels were removed; older saved layout keys are
ignored. Existing button shortcuts remain available.

Manager generates borderless `.61x61.bmp` covers and its preview uses an exact
2× scale. All **506 existing covers** were regenerated and hash-verified. Original
cached images are preferred; an offline legacy 44×44 cover is cropped to remove
its old padding before enlargement. Older cover files and recovery support remain.
The prior **277 unmatched/unavailable** and **two temporary lookup errors** retain
the placeholder; no unverified artwork was substituted.

Verified **337 firmware files** and **101 protected settings, database, playlist
and personal files**. All **785 music files** retained their size and modification
time; music was opened only for reading. No tags or database records were changed.

Checks: eight native Home/skin checks, Windows simulator build, 34 Manager Core
regressions on Linux, Windows portable publish and embedded/local artwork fixtures.
Repeating the fixture send made no writes. Windows Application Control blocked the
standalone console assembly, so the Core harness ran in the .NET 10 SDK container
on the homelab; no Windows policy was changed. Visual checks are left to the user.

Reports and verified originals: `C:/RockboxDev/logs/side-cover-20261108`.
See `verification.json`, `backfill.json`, `installed-file-hashes.json` and
`device-before/.rockbox`. The matching package is `rockbox.zip` there and in
`rockbox/build-sansaclipplus`. Safely eject both volumes, disconnect USB and restart
the Clip+ to load the corrected layout.

## Quality-of-life firmware prepared November 8, 2026

Firmware **cfc4853983-261108** is built and packaged for **Clip+**. It is ready at
`build-sansaclipplus/rockbox.zip`; no player was connected during this update.

The next deployment also includes the centered artwork repair described below.

- Albums and its album-name search show groups with at least two indexed songs.
  Artist pages and direct access to single-song releases retain their behavior.
- Song search uses Home listening scores, highest first, with deterministic ties.
  Playback follows the ranked result order.
- The 61×61 cover stays full size. Title and artist wrap into four and two rows;
  only overflow on the final row scrolls. Battery, shuffle, playback state,
  Add to Playlist, and the like heart share the right-hand footer.

Validation: all **nine Home/skin checks and two library checks** passed on Linux,
including production text drawing, Unicode, score ordering and playback queues.
The Windows simulator compiled and installed its updated skin and assets. Windows
Application Control blocked three existing test executions; the complete suites
passed on the homelab without changing Windows policy. The homelab ARM build used
two jobs. The firmware archive's **341 files**, target, version, CRCs, skin, six
bitmap assets and search font passed read-back checks. Downloaded and homelab ZIP
hashes match: `24991ff575ffe53068ed4d5beae3888a8a1d199571f44f63ab2d7d39d46544f1`.

Source, build/test logs, the package and `verification.json` are preserved under
`C:/RockboxDev/logs/qol-20261108`. The homelab source/build is
`/home/evanh/rockbox-qol-20261108`. Connect the Clip+, verify its target, back up
`.rockbox`, and extract the ZIP into the internal volume's root, merging its
`.rockbox` folder. Safely eject both volumes, disconnect USB, and restart.
Visual, button and audio acceptance remain for the user. This firmware update
does not require a music database rebuild.

## Centered artwork repair prepared November 8, 2026

All **785 existing player songs** now have prepared covers: **553 album covers**
and **232 exact-source YouTube thumbnails**. This fills all **279 prior gaps**
with 47 newly found album covers and 232 thumbnails. The previous 506 cover hashes
are unchanged. Thumbnail crops are centered squares, as requested, with no manual
offsets. The player remains disconnected; this update is prepared for later.

Manager now reads original YouTube source tags, checks additional release and
recording matches, caches thumbnails independently per video, and retries misses
or upgrades fallbacks through Retry artwork. It refuses conflicting shared covers
and safely handles the one overlong artwork filename. PC-only preparation writes
the cache and review previews without sending or changing music.

The combined next-deployment bundle is
`C:/RockboxDev/logs/artwork-fallback-20261108/next-deployment.zip`. It includes all
341 pending **cfc4853983-261108** firmware files, the **279 new covers**, and the
updated portable Windows Manager. `deployment.json` maps and hashes every payload
file; `INSTALL.md` explains installing firmware and artwork in the same deployment.
The seven additional staging tracks and separate pending album import are excluded.

Validation: **50/50 Manager tests on Windows and Linux**, portable Windows publish,
all 785 prepared covers reproduced identically, and all **792 staging audio files**
(5,126,560,122 bytes) retained their SHA-256 hashes, sizes and modification times.
A real three-song isolated send verified every cover, including the overlong-name
fallback; repeating it made **zero writes**. Source JPEGs and centered 61x61 BMPs
passed decoding/format checks. Reports, contact sheets, deployment manifest and
ZIP verification are under `C:/RockboxDev/logs/artwork-fallback-20261108`.

See [handoff.md](handoff.md) for the current state and remaining USB deployment.
