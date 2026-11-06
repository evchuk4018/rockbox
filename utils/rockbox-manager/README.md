# Rockbox Manager

Portable Windows 11 x64 playlist manager for the customized Sansa Clip+ in this
repository. The executable contains its .NET runtime and runs without admin rights.

## Run and use

Open `C:\RockboxDev\rockbox\build-rockbox-manager\RockboxManager.exe`.
Connect the Clip+ over USB with its SD card installed. Detection uses Rockbox's
identification files, USB device association, and volume serials, not drive letters
or volume labels. Multiple players require choosing one. Other Rockbox targets
and cards connected separately through a card reader are outside this version.

- **All music:** search title, artist, album, genre, or filename; filter storage.
  Select songs (Ctrl/Shift for multiple), choose a destination, and Add selected.
- **Playlists:** create, rename, duplicate, delete, add songs, remove entries, or
  reorder with Up/Down or drag and drop. Clear filters and sorting before reordering.
  Removing entries or deleting playlists keeps the audio files.
- **Liked Songs:** the special internal playlist used for hearts on the player.
  Use Like/Unlike or edit its entries. Its filename is protected. Liked Music is
  a separate ordinary playlist. An SD file named Liked Songs is left untouched;
  the firmware's likes live on internal storage.
- **Home order:** pin/unpin ordinary playlists and rearrange the player's Home
  list, up to 16 entries. Renaming or deleting a playlist updates its pin.
- **Send to Player:** apply the pending changes. Ordinary playlist edits require
  both original storage volumes so internal and SD copies remain synchronized.
  Likes and internal-only pin edits can be sent without an SD card.
- **Discard changes:** reset the current draft. Drafts are held in memory until
  sending, and closing asks before discarding them. Disconnection retains drafts
  for the same player. Refresh reloads the library when no changes are pending.

Matching playlist copies appear once. If their songs/order differ, choose
**Use Internal Version** or **Use SD Version** before editing. New/edited ordinary
playlists have an internal catalog copy and an SD mirror, using paths appropriate
to each volume. Existing repeated/missing entries are preserved. Missing tracks
appear as Unavailable. Audio metadata is read through a read-only stream; failed
tag reads fall back to the filename. Legacy M3U uses the configured Rockbox
codepage; edited files become UTF-8 M3U8 and pin references follow that change.
The Rockbox path buffer permits at most 259 UTF-8 bytes, including volume prefixes.

After success, safely eject through Windows and disconnect. Open the playlist
on the player and check its order, hearts, and Home pins. Playlist edits do not
require rebuilding the music database. The app does not import/delete audio,
edit tags, update firmware or scores, play audio, or eject the player.

## Interrupted sends and recovery

Before writing, the app checks all affected files against their loaded hashes.
It backs up affected originals on the PC, flushes each temporary replacement,
and verifies its contents after replacement. Writes are per-file; a complete
multi-volume send is not atomic. Rename destinations and pin changes precede
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
files the manifest says were newly created. Verify the restored pairs and pins.
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

Manual acceptance: connection gate; search and storage filters; create/add/remove/
reorder/rename/delete; likes; Home pins/order; Discard; unsent-close prompt;
disconnect/reconnect; mirrored-copy conflict choices; Send and on-player playback.
