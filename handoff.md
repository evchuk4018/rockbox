# Clip+ firmware and artwork handoff

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
