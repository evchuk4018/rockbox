# Workspace cleanup — November 13, 2026

Removed **16,348 verified redundant files**, totaling **29,592,235,445 bytes
(27.56 GiB)**, from `C:/RockboxDev`. The final scan measured **29.74 GiB**,
down from approximately **57.3 GiB**. Sizes count regular files once and exclude
junction targets; the final total includes the local verification records.

## Removed and retained

- Pruned redundant files from `logs/music-library-20261107/originals`,
  `logs/music-tags-20261107/originals`, the discovery fixture, and
  `logs/music-tags-20261107/removed-duplicates`.
- Removed 11,452 files from the old `db-source` snapshot whose exact contents
  are reachable Git objects, plus one file identical to a retained file.
  The snapshot is now incomplete; use the Git repository for source/builds.
- Kept **111 candidate files (216.43 MiB)**: 41 audio backups without a retained
  encoded-audio match, 68 unique source/build files, and both source archives.
  The archives contain members not matched to Git or retained files.
- Preserved the main library, all three preparation/staging trees, current
  Manager and firmware builds, simulator storage and its music junction.
- Preserved the full `music-tags-20261107/player-before` audio snapshot, the
  repair disk image, firmware/settings recovery files, and diagnostic reports.

The historical `originals/` and `removed-duplicates/` paths are no longer full
rollback sets. Their remaining files must not be removed based on this cleanup's
duplicate claims. Historical tag/path journals and reports remain available.

## Verification

1,646 deletions matched retained full-file SHA-256 hashes; 3,250 matched encoded
audio using FFmpeg stream copy and SHA-256; 11,452 matched reachable Git objects.
All deletion paths were constrained to the approved local folders and checked
for reparse points. Native PowerShell removed individually inventoried files,
then empty directories. Six audio-check timeouts passed on retry; no audio
verification errors remained.

All **28,585 retained files** matched their baseline sizes and SHA-256 hashes
after removal. Every deletion candidate was absent. The simulator junction still
resolved to `C:/RockboxDev/MusicLibrary`. Documentation was updated afterward.

An earlier deduplication had removed tracks 3, 5 and 8 from each pending TV Girl
album folder while leaving their playlist entries. Restored those six files from
the saved staging originals and verified exact SHA-256 matches before cleanup.
Both playlists now resolve ten unique existing tracks; no player transfer occurred.
Use the current hashes below for a future transfer: the November 6 whole-file
hashes predate subsequent tag edits.

Local evidence: `C:/RockboxDev/logs/cleanup-20261113/`, including
`delete-manifest.json` (paths, hashes and retained counterparts/Git objects),
`preserved.json`, `verification.json`, `disk-usage.json`,
`restored-pending-tracks.json`, and `pending-transfer-hashes.json`.
The one-time verification/removal scripts are stored there too.

For future testing, prefer small music fixtures and Git commits over full music
and source snapshots. Keep the retained recovery set while the reported player
boot problem remains unresolved. This cleanup changed no application behavior
and required no homelab deployment.
