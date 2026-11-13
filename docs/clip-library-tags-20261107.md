# Clip library tag repair — November 7, 2026

Rebuilt the Clip+ metadata and database around canonical two-level genre tags so
Home genre autoplay only mixes tracks in the same subgenre.

## Result

- Player database: **785 tracks** (was 802).
- Removed **17 duplicate player files**; staging trees were deduplicated too.
- Retagged **4,141 files** across the player and local copies; **4,084 remain**
  after duplicate removal. Encoded audio hashes are unchanged.
- Replaced the fragmented genre list with **19 `Genre-Subgenre` tags**.
  No bare `Pop`/`pop` or case variants remain.
- Taylor Swift is `Pop-Dance` or `Country-Pop`; Build Me Up Buttercup is
  `Pop-Soul`. Their autoplay pools are disjoint.
- Rebuilt the database with all **785 paths resolving**, tags matching the plan,
  and runtime statistics preserved.
- Updated 15 playlist files; all **1,935 entries resolve**.

## Genre taxonomy

| Top level | Subgenres |
| --- | --- |
| Pop | Dance, Indie, Rock, R&B, Acoustic, Soul |
| Hip-Hop/Rap | Trap, Pop Rap, Conscious, Emo |
| R&B/Soul | Alternative, Contemporary |
| Rock | Alternative, Indie, Pop |
| Electronic | House |
| Country | Pop |
| Jazz | Standard |
| Folk | Indie |

Player counts: Pop-Dance 165, Hip-Hop/Rap-Trap 144, Pop-Rock 80,
Hip-Hop/Rap-Pop Rap 78, Pop-Indie 69, Rock-Alternative 51, Electronic-House 37,
Rock-Pop 25, R&B/Soul-Contemporary 23, R&B/Soul-Alternative 19, Folk-Indie 17,
Hip-Hop/Rap-Conscious 17, Pop-R&B 15, Pop-Soul 11, Rock-Indie 9, Jazz-Standard 7,
Pop-Acoustic 7, Hip-Hop/Rap-Emo 7, Country-Pop 4.

Small Electronic-Chill, Electronic-Hardstyle, and Hip-Hop/Rap-Drill pools were
merged into Electronic-House or Hip-Hop/Rap-Trap. Country-Pop, Jazz-Standard, and
Folk-Indie were kept as distinct small pools.

## Tag corrections

- Normalized genre strings to one `Genre-Subgenre` value per file.
- Unified album artists for albums that were split by featured-artist or localized
  credits; restored English names for localized album-artist aliases.
- Removed playlist-name and YouTube-date album tags; filled known custom-edit
  albums; kept verified release albums.
- Removed clear artist prefixes and video suffixes from titles; preserved version
  qualifiers such as slowed, sped-up, nightcore, and remix.
- Normalized `0/0` track and disc values.
- Preserved MusicBrainz IDs, source URLs, comments, artwork, and all unedited tags.

## Duplicates

Removed duplicate encodes on the player and both staging trees. Seven same-title
pairs were retained because their durations differ by more than five seconds and
they are different versions, not duplicate encodes:

- Beach Bunny — Dream Boy (single / Honeymoon)
- Dua Lipa — Don’t Start Now (short / album)
- Fried By Fluoride — The Love I Lost (112 s / 132 s)
- Katy Perry — The One That Got Away (album / music video)
- Paramore — Decode (262 s / 250 s)
- PinkPantheress — Stateside + Zara Larsson (album / single)
- Radiohead — Creep (240.7 s / 234.1 s)

## Verification

`verification.json` records the status and final tags of all 4,141 pre-dedupe
files.

- 1,871 file copies correspond to 428 MusicBrainz-verified groups.
- 2,050 file copies correspond to 637 previously unresolved groups; their
  artist/title/album tags were retained rather than guessed.
- 220 local-only file copies were audited locally.
- Every file passed structural validation, tag-write verification, and encoded
  audio hash checks. MusicBrainz lookups were rate-limited/503 during this pass,
  so custom edits were not re-guessed.

## Evidence

Under `C:/RockboxDev/logs/music-tags-20261107`:

**November 13 workspace cleanup:** `originals/` and `removed-duplicates/` below
now contain only unmatched files, rather than complete historical rollback sets.
The full `player-before/` snapshot, state backups and evidence remain. Six pending
TV Girl staging files were restored before pruning. See the
[cleanup record](workspace-cleanup-20261113.md) for verification and current hashes.

- `player-before/` full 802-file player snapshot
- `originals/` original local files
- `removed-duplicates/` removed player and staging files
- `database-before/`, `playlists-before/`, `home-before/`
- `plan.json`, `applied-local.json`, `applied-player.json`, `verification.json`
- `database-transfer.json`

Firmware was not changed. No audio was imported, renamed, or transcoded outside
duplicate removal. The player database was rebuilt with the existing
`database_sansaclipplus` tool.
