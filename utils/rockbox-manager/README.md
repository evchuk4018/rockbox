# Rockbox Manager

Portable Windows 11 x64 music and playlist manager for the customized Sansa Clip+ in this
repository. The executable contains its .NET runtime and runs without admin rights.

## Run and use

Open `C:\RockboxDev\rockbox\build-rockbox-manager\RockboxManager.exe`.
Connect the Clip+ over USB with its SD card installed. Detection uses Rockbox's
identification files, USB device association, and volume serials, not drive letters
or volume labels. Multiple players require choosing one. Other Rockbox targets
and cards connected separately through a card reader are outside this version.

- **All music:** search title, artist, album artist, album, genre, or filename;
  filter storage and the exact `Genre-Subgenre` tag. Select songs (Ctrl/Shift for multiple), choose a destination, and Add selected.
- **Artists / Albums:** browse by album artist (falling back to track artist).
  Albums with the same title and different artists stay separate. Album tracks
  use disc number, track number, then title. The table also displays these tags.
- **Artwork:** covers prepare automatically in the background. Select a song for
  its 44×44 player preview and status. Ready covers join the draft; Send to Player
  writes them. Retry artwork retries failed lookups without changing music tags.
- **Playlists:** create, rename, duplicate, delete, add songs, remove entries, or
  reorder with Up/Down or drag and drop. Clear filters and sorting before reordering.
  Removing entries or deleting playlists keeps the audio files.
- **Liked Songs:** the special internal playlist used for hearts on the player.
  Use Like/Unlike or edit its entries. Its filename is protected. Liked Music is
  a separate ordinary playlist. An SD file named Liked Songs is left untouched;
  the firmware's likes live on internal storage.
- **Send to Player:** apply the pending changes. Ordinary playlist edits require
  both original storage volumes so internal and SD copies remain synchronized.
  Likes can be sent without an SD card. Artwork requires the original storage
  containing its source songs; covers are not mirrored between volumes.
- **Discard changes:** reset the current draft. Drafts are held in memory until
  sending, and closing asks before discarding them. Disconnection retains drafts
  for the same player. Refresh reloads the library when no changes are pending.

Matching playlist copies appear once. If their songs/order differ, choose
**Use Internal Version** or **Use SD Version** before editing. New/edited ordinary
playlists have an internal catalog copy and an SD mirror, using paths appropriate
to each volume. Existing repeated/missing entries are preserved. Missing tracks
appear as Unavailable. Audio metadata is read through a read-only stream; failed
tag reads fall back to the filename. Legacy M3U uses the configured Rockbox
codepage; edited files become UTF-8 M3U8 and retain their entries.
The Rockbox path buffer permits at most 259 UTF-8 bytes, including volume prefixes.

After success, safely eject through Windows and disconnect. Open the playlist
on the player and check its order and hearts. Playlist edits do not
require rebuilding the music database. The app does not import/delete audio,
edit tags, update firmware or scores, play audio, or eject the player.

## Album artwork

The Clip/Clip+ firmware in this repository supports PC-prepared monochrome BMPs.
In **Settings → General Settings → Display → Now Playing layout**, choose
**Stacked** (default: album / cover / song) or **Side by side** (cover left,
album and song right). Both retain a thin progress bar and like heart. Long
names scroll, and changing the layout keeps playback running. Existing pause,
like and Add to Playlist button shortcuts keep working.

Manager first checks embedded front artwork, track-specific local images, and
album-specific local images. `cover`, `folder`, or `front` images apply only to
folders containing one album. It then uses its PC cache, an existing generated
player cover, or exact MusicBrainz / Cover Art Archive matches. Different versions
and ambiguous artist/album matches keep the built-in placeholder. Network failures
remain retryable. Requests identify the app, respect MusicBrainz's one-request-per-
second limit, and use bounded retries. Missing online matches are cached for 24 hours.

Sources and provenance are cached in `%LOCALAPPDATA%\RockboxManager\Artwork`.
Conversion uses the supplied Sansa Cover Studio defaults: centered crop, 100% zoom,
125% contrast, brightness 0, cutoff 128, edge detail 40%, auto-levels, Floyd–Steinberg,
no inversion, and a two-pixel inset. Image resampling uses centered area sampling.
There are no conversion controls in this version.

Generated files are `<track-stem>.44x44.bmp` beside each audio file. A folder can
therefore hold different albums without sharing a wrong generic cover. If the
filename exceeds Rockbox's path limit, Manager uses the existing internal
`.rockbox/albumart/<artist>-<album>.44x44.bmp` lookup when it fits without a naming
collision. Otherwise the report identifies the path limitation. Audio files and
embedded tags stay untouched. Covers need no database rebuild. Use this repository's
matching firmware; stock Clip+ firmware does not enable album-art support.

For an unattended backfill, close Manager and run:

```powershell
Start-Process C:\RockboxDev\rockbox\build-rockbox-manager\RockboxManager.exe -WindowStyle Hidden -Wait -ArgumentList '--backfill-artwork C:\RockboxDev\logs\artwork-report.json'
```

This requires exactly one detected player, prepares all current tracks, sends
only changed covers through the normal verified send engine, and writes a JSON
report plus a `.progress.txt` file. The optional `--release-review <path>` imports
compatible verified results from `scripts/review-music-library.py`; the current
album artist, artist, album, title and duration must agree before reuse. It does
not retag or import audio. Fixtures also support this command. `--inspect` stays
read-only and never prepares or sends artwork.

## Interrupted sends and recovery

Before writing, the app checks all affected files against their loaded hashes.
It backs up affected originals on the PC, flushes each temporary replacement,
and verifies its contents after replacement. Writes are per-file; a complete
multi-volume send is not atomic. Rename destinations precede
removal of the old names. The interface reports verified progress if a send fails.

**Retry Send** continues the saved operation, including after restarting the app.
Connect the original player/card. Edits remain locked until retry succeeds or
**Reload player** is selected. Reload abandons retry and reads the actual current
files; already applied changes remain. If another program/player changed an
affected file, reload and resolve the current versions instead of overwriting it.

The **Backups** button opens `%LOCALAPPDATA%\RockboxManager\Backups`. Each operation
has `operation.json` (or `abandoned-operation.json`) and numbered `.original`
files. The manifest maps each numbered file to storage and relative pathname,
records its original hash, and records whether a destination originally existed.
For manual restoration, close the app, identify the correct player volumes,
copy the corresponding originals to their recorded locations, and remove only
files the manifest says were newly created. Verify the restored pairs.
Do not restore a manifest onto a different player/card. Keep one latest completed
backup per device; incomplete and explicitly abandoned operations are retained.
Unexpected errors are recorded in `%LOCALAPPDATA%\RockboxManager\errors.log`.

## Build and checks

Install the .NET 10 SDK (the script also finds `%USERPROFILE%\.dotnet\dotnet.exe`), then:

```powershell
& C:\RockboxDev\rockbox\utils\rockbox-manager\build.ps1
```

This runs the dependency-free console regression harness and publishes a
self-contained single-file x64 build in `build-rockbox-manager`. Source is split
between the WPF App, a UI-independent Core library, and Tests. Build output is
ignored. Runtime third-party notices accompany the executable.

For a read-only hardware diagnostic (writes only the requested PC report):

```powershell
Start-Process C:\RockboxDev\rockbox\build-rockbox-manager\RockboxManager.exe -ArgumentList '--inspect C:\RockboxDev\rockbox\build-rockbox-manager\device-report.json' -Wait
```

For isolated manual testing, `--fixture C:\path\to\fixture` uses `Internal` and
`SD` child folders as a clearly labeled test player. It never enumerates real
players in this mode. Removing/recreating those folders simulates disconnection;
do not point fixture folders at real devices or junctions. Regression tests use
temporary folders and injected storage faults, never mounted hardware.

Manual acceptance: automatic cover preparation and selected-song preview;
Artists/Albums and exact genre filtering; connection gate; search and storage filters; create/add/remove/
reorder/rename/delete; likes; Discard; unsent-close prompt;
disconnect/reconnect; mirrored-copy conflict choices; Send and on-player playback.

The player now displays all catalog playlists on Home automatically. There is
no pin limit or Home-order editor. Legacy pin files are left untouched. Persisted
older sends can still finish through the existing recovery mechanism.
