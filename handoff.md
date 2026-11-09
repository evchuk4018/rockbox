# Rockbox Manager and Clip+ handoff

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

**Before podcast transfers:** Windows reports SD volume E: dirty / Full Repair
Needed. The firmware update wrote internal storage only and preserved the card.
Resolve that existing filesystem warning before using podcast sync.
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
