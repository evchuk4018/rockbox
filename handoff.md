# Artwork repair handoff

Updated November 8, 2026 (America/New_York). Preparation is complete. The user
chose installation later and asked that this repair join the prior firmware
update in the next deployment.

## Done

- Accounted for all 785 existing player songs: 553 album covers and 232 exact
  source YouTube thumbnails. Zero missing covers remain in the prepared payload.
- Filled the 279 previous gaps with 47 newly found album covers and 232 thumbnails.
  Preserved the hashes of all 506 previously installed covers.
- Used centered square thumbnail crops, as requested. Zero manual offsets or
  crop overrides were applied. All output is 61x61, 1-bit, 550-byte BMP.
- Updated Manager's source-tag extraction, release/recording searches, independent
  per-video caches, retry upgrades, path conflict checks and long-filename fallback.
- Added PC-only cache/preview preparation. No artwork sidecars were written into
  the source staging tree. No audio or tags were edited.
- Passed 50/50 Manager tests on Windows and Linux and published the final portable
  Windows Manager at C:/RockboxDev/rockbox/build-rockbox-manager/RockboxManager.exe.
- Verified all 792 staging audio files (5,126,560,122 bytes) retain their SHA-256
  hashes, sizes and modification times. Repeated preparation produced the same
  785 player cover hashes.
- Verified a three-song isolated send, including the long-title generic cover;
  repeating the send made zero writes and preserved all audio hashes/timestamps.
- Prepared one next-deployment ZIP with the prior cfc4853983-261108 QOL firmware
  (341 files), 279 new covers and the updated Windows Manager. The seven extra
  staging tracks and separate pending album import are excluded.
- Updated the repository README, Manager guide and C:/RockboxDev/NEXT-TRANSFER.md.

## Ready for next deployment

Bundle: C:/RockboxDev/logs/artwork-fallback-20261108/next-deployment.zip

ZIP SHA-256: f5dc05c221783340994c6f27e5501ca11175a303f6075d83c02da7c881be842e

The ZIP has 625 files; every payload file passed hash read-back. deployment.json
maps all 785 songs, their source audio hashes, cover sources and new destinations.
Internal/ and SD/ mean identified player volumes, never fixed drive letters.
PC/Manager/ belongs on Windows. The original PC cache remains in
%LOCALAPPDATA%/RockboxManager/Artwork.

## Left to do when the Clip+ reconnects

1. Verify the original Clip+ and SD card; rescan the live library for changes.
2. Back up affected firmware/cover files and fingerprint personal data and audio.
3. Install the pending QOL firmware and artwork together using INSTALL.md in the
   ZIP. Use Manager's verified/resumable artwork send with a fresh device-bound
   operation. Recheck the generic-cover folder still has only its source song.
4. Read back installed hashes, verify personal data/audio unchanged and confirm
   another artwork backfill makes zero writes.
5. Safely eject both volumes, disconnect, restart and check artwork, the prior
   QOL behavior, physical buttons and playback. No database rebuild is required.

No part of this combined update has been installed on the physical player yet.
Use Terra for advanced work and Luna at MAX reasoning for repetitive work. Push
repository changes to the user-owned origin/main. Homelab access is SSH through
evanh@homelab; no browser automation or Sites workflow.

## Evidence

All new artifacts are in C:/RockboxDev/logs/artwork-fallback-20261108/:
prepared-final.json, preparation-verification.json, deployment.json,
deployment-verification.json, fixture-verification.json, Windows/Linux test
logs, original thumbnails and source/centered-preview contact sheets.
