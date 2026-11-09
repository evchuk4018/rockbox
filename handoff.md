# Rockbox Manager and Clip+ handoff

## Boot crash reported and player recovered — November 9, 2026

After the firmware update and SD dirty-flag repair, the user reported a crash on
boot, followed by a dark/unresponsive player. Windows initially detected only
SanDisk USB `0781:6200` with Device Manager error 10 and neither storage volume.
The user performed a 30-second Power reset with USB disconnected, then held Left
while reconnecting. The player showed the SanDisk firmware-update message and
subsequently booted Rockbox; both original storage volumes are accessible again.

Read-only inspection checked all **341 installed firmware files** against their
installation SHA-256 hashes: all match **796b6a80e3-261109**. Both volumes report
Healthy / OK and NOT Dirty. A full file-name scan found only `.rockbox/rbutil.log`,
the installation history, and no saved panic/crash log. `.rockbox/debug` contains
the symbol map only. `FOUND.000/FILE0000.CHK` contains an old Home autoplay pool;
its hash matches the pre-install snapshot, so it is not evidence of this crash.

The current panic handler displays panic/backtrace information on the LCD rather
than saving a persistent crash dump. The cause remains **undetermined** after the
reset. Capture the exact on-screen panic/abort text and what the player was doing
if it recurs; hardware playback stability is not yet established. The player
still runs the podcast build, not a rollback. Read-only evidence and copied state
files: `C:/RockboxDev/logs/crash-inspection-20261109/inspection.json` and `evidence/`.

**Persistent crash logging added:** Panics and ARM aborts now save a checksummed
record to internal `/.rockbox/crashlog.txt` before the error screen, which reports
whether the save succeeded. Two preallocated 512-byte records alternate; the newest
valid record survives a restart. The emergency path performs one bounded polled
write to the file's mapped internal sectors without scheduler, locks, DMA, the
removable card, or FAT metadata changes. Startup pins two handles to prevent
cluster recycling. Logging is disarmed for USB/hotswap/shutdown (rearmed after USB),
and is unavailable before mount or during an active disk transfer; those attempts
report **not saved**. Host regression checks cover mapping, fragmentation, sequence
selection, torn writes, nested faults, I/O/controller failures and USB exclusion.
Source commit `fd26078a5a`; firmware built on the homelab with two jobs.


## Weekly YouTube podcasts installed — November 9, 2026

Installed **796b6a80e3-261109** on the identified Clip+ (internal volume serial
506F1958; SD serial 11F13253). All **341 firmware files** passed SHA-256 read-back
verification. All **1,407 protected files** match the before snapshot; all **785
music files** retain their sizes and modification times. No podcast subscription
was added and no audio/card content was transferred during firmware deployment.

Portable Manager: `C:/RockboxDev/rockbox/build-rockbox-manager/RockboxManager.exe`.
Publishable packages, installation guide and checksums are under
`C:/RockboxDev/logs/podcasts-20261109/release/`. Release:
https://github.com/evchuk4018/rockbox/releases/tag/podcasts-2026-11-09
Use the entire Manager ZIP, including the pinned/checksummed yt-dlp, Deno, FFmpeg
and ffprobe tools and notices. Initial subscriptions are empty. Open Podcasts
from the top bar to add channels, including while disconnected.

Manager prepares the latest completed America/New_York Monday–Sunday week and
automatically transfers while running. A successful drop replaces the entire old
one, including unfinished episodes; same-week reconnects retain it and positions.
Subscription changes replace that week's drop. Missed weeks load only the latest
completed week. A checked empty week clears the old drop; source failures do not.
The original SD card and room for old plus new audio are required. Retry preserves
completed preparation and uses a durable transfer journal; catalog publication is
the commit point. Catalog/resume data are internal; audio is SD-only and excluded
from music scans/database. Unrelated drafts, music, artwork and scores are preserved.

Player: Home Podcasts is below playlists, then alphabetic Channels → oldest-first
Episodes. New/Saved-time/Played labels, independent resume and single-track stop.
Right/Left taps start scanning at 5 seconds/second, cycling 10/15/5; direction
changes reset to 5. Middle commits/resumes; leaving WPS commits/saves while paused.
Held seeking and ordinary music controls remain. See the Manager guide and
`docs/podcasts-install.md` for complete use, recovery, limits and controls.

Validation: **88/88 Manager regressions** on Windows; Linux Manager checks;
production native Podcasts, nine Home, two music-library and three metadata
tests; Windows simulator and homelab ARM firmware build (two jobs). The published
Windows Manager completed a live 19-second public-video download/transcode and
isolated verified transfer, with a zero-write repeat. ffprobe confirmed MP3,
two channels, 128000 bit/s and channel/title tags. No browser/UI automation used.

Evidence/backups: `C:/RockboxDev/logs/podcasts-20261109/`, including
`installation/device-before/.rockbox`, `installation/verification.json`, build
logs and `manager-smoke.json`. Source/artifacts are also staged at
`/home/evanh/rockbox-podcasts-20261109` on homelab.

**SD warning resolved November 9:** Ran elevated `chkdsk E: /F /X` on the
verified card (serial 11F13253). Both the initial and post-repair filesystem
scans found no file/folder problems; repair cleared the dirty flag. Windows now
reports **Healthy / OK**, and `fsutil dirty query E:` reports **NOT Dirty**.
All 2,089 inventoried files are preserved: all 785 audio files retain their sizes
and timestamps, and all 1,304 non-audio files retain their SHA-256 hashes.
Pre-repair inventory, a 14,777,344-byte FAT32 boot/allocation-table backup,
CHKDSK logs and `verify-result.json` are under
`C:/RockboxDev/logs/sd-repair-20261109/`. The repair did not alter firmware or
initialize a podcast drop. The earlier SD warning no longer blocks podcast sync.
Safely eject **both volumes**, disconnect USB and restart to load the firmware.
Physical display, buttons, scanning feel and audio acceptance remain user checks.
No music database rebuild is required.

## Manager listening-score discovery — November 9, 2026

Added Discover to the Windows Manager/loader. It reads the player's cumulative
listening snapshots, applies score decay, ranks genres, and requests up to four
MusicBrainz album/song suggestions outside the connected library. Music tables
also have a sortable Score column. Reload scores preserves pending playlist edits.
Compound Hip-Hop/Rap and R&B/Soul tags survive the metadata reader's slash splitting.
See `utils/rockbox-manager/README.md` for use, ranking and the `--suggest` JSON mode.

Validation: all 60 console regression tests pass, and the portable Windows build
publishes to `C:/RockboxDev/rockbox/build-rockbox-manager/RockboxManager.exe`.
A packaged headless live-catalog check scanned a 792-song local fixture, matched
102 songs from the November 8 saved score snapshot and returned two albums plus
two songs. Fixture sizes/timestamps and score SHA-256 stayed unchanged. Evidence
is under `C:/RockboxDev/logs/discovery-20261109`, including `live-suggestions.json`
and `verification.json`. These are saved player data, not a newly connected player.

Windows Application Control blocked a later repeat launch of the published EXE.
The final source passes the build/tests; final packaged launch and visual UI
acceptance remain unverified. Do not change security policy to run it. The user
requested no computer use; continue with command-line checks only. This update
requires the Windows app, not a firmware or homelab service deployment.

## Previous Clip+ firmware and artwork installation

Updated November 8, 2026 (America/New_York). Both requested changes are installed
on the original Clip+: the prior QOL firmware commit cfc4853983 and artwork/Manager
change 5120f62538. The user clarified that deployment meant the Clip+, not homelab.

## Done

- Verified internal volume serial 506F1958 and SD serial 11F13253, the Clip+ target,
  all 785 expected songs and the existing 506 covers before writing.
- Backed up current firmware/settings and playlists and recorded a fresh inventory.
- Installed cfc4853983-261108 firmware: all 341 files passed SHA-256 read-back.
  This includes album filtering, listening-score search order and playback layout.
- Installed 279 new covers: 47 album covers and 232 exact-source YouTube thumbnails.
  All 785 player songs now have verified artwork: 553 album covers, 232 thumbnails.
- Preserved all 506 existing cover hashes. Thumbnail crops are centered squares
  with zero manual offsets; output is 61x61, 1-bit, 550-byte BMP.
- Repeated the actual-player artwork backfill: zero writes, all 785 Installed.
- Verified all 785 music files kept their sizes and modification times. Hashes of
  623 other protected files remain unchanged, including settings, database,
  playlists, likes and scores. Audio/tags were never opened for writing.
- Updated portable Manager remains at
  C:/RockboxDev/rockbox/build-rockbox-manager/RockboxManager.exe. Its 50/50 tests
  passed on Windows and Linux; its real-device send is now verified.
- The separate pending TV Girl album import was not transferred.

## Left for the user

1. Safely eject both player volumes through Windows.
2. Disconnect USB and restart the Clip+ to load the new firmware.
3. Check artwork, Albums, score-ranked search, long-title layout, buttons and audio.

No database rebuild is required. Physical playback/display acceptance remains
unverified while the player is connected over USB.

## Backups and verification

C:/RockboxDev/logs/combined-deployment-20261108/ contains:
- device-before/.rockbox and per-volume playlist backups.
- device-before.json and preflight.json: fresh fingerprints and device identity.
- firmware-installed.json: hashes of every installed firmware file.
- artwork-installed.json and artwork-repeat.json: verified sends and zero-write repeat.
- artwork-operation.json: exact artwork rollback/recovery mapping.
- verification.json: final installation result.

The reusable combined ZIP remains at
C:/RockboxDev/logs/artwork-fallback-20261108/next-deployment.zip
SHA-256: f5dc05c221783340994c6f27e5501ca11175a303f6075d83c02da7c881be842e
Its build-time manifest says prepared; the combined-deployment verification above
records the completed physical installation. Original preparation reports and
centered-preview sheets remain in the artwork-fallback artifact directory.

The same verified bundle and source snapshot are also staged on homelab at
/home/evanh/rockbox-artwork-deployment-20261108; no services were changed there.
Use Terra for advanced work and Luna at MAX reasoning for repetitive work. Push
repository changes to user-owned origin/main. Access homelab through SSH as
evanh@homelab; no browser automation or Sites workflow.
