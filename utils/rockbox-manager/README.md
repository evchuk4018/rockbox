# Rockbox Manager

Portable Windows 11 x64 music and playlist manager for the customized Sansa Clip+ in this
repository. The executable contains its .NET runtime and runs without admin rights.

## Run and use

Open `C:\RockboxDev\rockbox\build-rockbox-manager\RockboxManager.exe`.

The local build is unsigned. Windows 11 Smart App Control may block it and
offers no per-app exception. A build signed with a Windows-trusted code-signing
certificate can run with Smart App Control enabled. See the
[Smart App Control FAQ](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions).

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
  its 61×61 player preview and status. Ready covers join the draft; Update
  writes them. Retry artwork retries failed lookups without changing music tags.
- **Playlists:** create, rename, duplicate, delete, add songs, remove entries, or
  reorder with Up/Down or drag and drop. Clear filters and sorting before reordering.
  Removing entries or deleting playlists keeps the audio files.
- **Liked Songs:** the special internal playlist used for hearts on the player.
  Use Like/Unlike or edit its entries. Its filename is protected. Liked Music is
  a separate ordinary playlist. An SD file named Liked Songs is left untouched;
  the firmware's likes live on internal storage.
- **Update:** the only action besides playlist editing. It locks the window behind a
  progress bar until it finishes, then reloads the player. In order it:
  1. sends pending playlist, like and artwork edits (or retries an interrupted send;
     a send that fails twice is abandoned and its backups kept);
  2. prepares and transfers the latest completed America/New_York Monday-Sunday
     podcast week for the existing subscriptions;
  3. retires low-scoring Discovery songs and installs new ones (see
     [Discovery](#listening-scores-and-discovery));
  4. updates the **Discovery** playlist and removes retired songs from every playlist.
  Update needs the original player and SD card. Problems from any step are shown
  together in one message after it finishes; the remaining steps still run.

  Podcast subscriptions are kept in `%LOCALAPPDATA%\RockboxManager\Podcasts` and are
  no longer edited in the window. Portable tools are loaded only from `tools` beside
  `RockboxManager.exe`: `yt-dlp.exe`, `deno.exe`, `ffmpeg.exe`, and `ffprobe.exe`.
  Manager passes explicit argument lists, ignores yt-dlp configuration, and uses no
  YouTube account or browser cookies. Podcast card audio is owned only under
  `Podcasts/RockboxManager` and is excluded with `database.ignore`.

Drafts are held in memory until Update; closing asks before discarding them.
Disconnection retains drafts for the same player.

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
require rebuilding the music database. Managed podcast audio is downloaded,
tagged, transferred and retired by Podcasts. The only music audio Manager writes or
deletes is under `MusicLibrary/Discovery/`; scores are read-only.
Firmware installation is a separate release step. Eject through Windows.

## Weekly podcasts and Clip+ controls

Subscriptions start empty. Open **Podcasts**, paste a public YouTube channel
URL (`/@handle`, `/channel/UC…`, `/c/name`, or `/user/name`), and choose **Add**.
Equivalent channel URLs resolve to the same stable channel ID. Remove changes
the next synchronization. Manager must remain open for preparation and automatic
USB transfer; there is no Windows background service or server downloader.

The drop is the **previous Monday–Sunday in America/New_York**, including DST
changes. For example, on November 9 the requested dates are November 2–8.
Public regular Videos-tab uploads are included; Shorts, live/archived streams,
and upcoming releases are excluded. Channel listings are pre-filtered with
approximate upload dates so large channels only fetch full metadata for likely
in-week videos. Full publication timestamps determine inclusion; feeds with a
fixed episode count are not used. A checked preparation is reused through the
week, including after restarting Manager. Failed episodes can be retried without
downloading completed MP3s again.

**A successful replacement removes the entire old drop, including unfinished
episodes and their positions.** Reconnecting in the same week preserves the
installed drop and positions. Subscription changes prepare a replacement for
that week. After several missed weeks, only the latest completed week is loaded.
A successfully checked empty week clears the old drop; a lookup error never
counts as an empty week. Removing every subscription prepares an empty drop.

The original SD card is required: audio has no internal-storage fallback.
Keep enough free space for **both** the old drop and the whole replacement.
Audio goes only to SD `/Podcasts/RockboxManager/<video-id>.mp3`; titles/channel
names live in tags and the catalog, not filenames. The internal catalog is
`/.rockbox/podcasts/catalog.tsv`; internal resume records are under
`/.rockbox/podcasts/resume/`. Manager's music scan excludes this audio, and
`database.ignore` keeps it out of Rockbox music/database discovery.

Copies are streamed, flushed and hash-checked. The catalog is published last.
Cancellation, failed downloads, insufficient space, wrong/missing cards, and
disconnection before publication retain the old catalog/audio. **Retry** resumes
the PC transfer journal or finishes post-publication cleanup; reconnect the same
player/card first. Don't delete `%LOCALAPPDATA%\RockboxManager\Podcasts` during
recovery. Successful cleanup removes obsolete managed files and temporary retry
backups. Unrelated music, playlist drafts, artwork and scores are preserved.
Unexpected filename conflicts are retained and reported, rather than overwritten.

On the Clip+, **Podcasts is directly below the Home playlist list**. Open
**Channels → Episodes**: channels are alphabetical, episodes oldest first.
Rows show New, Saved plus elapsed time, or Played. Empty channels explain that
there were no releases. Each episode resumes independently and stops at its
end; selecting Played starts from zero. Music shuffle/repeat preferences are
preserved. Resume saves use flushed temporary replacements; failed saves keep
the previous record and show a warning. Now Playing checkpoints every 30 seconds.

| Button in a managed episode | Action |
| --- | --- |
| Right tap | Begin forward scan at 5 seconds/second; repeat taps cycle 10, 15, 5 |
| Left tap | Backward scan with the same rates; direction changes reset to 5 |
| Middle during scanning | Commit the shown position and resume |
| Middle otherwise | Existing pause/resume |
| Leave Now Playing while scanning | Commit and save the target, remaining paused |
| Hold Left/Right outside scanning | Existing held-button seek |

Scanning pauses audio, shows direction/rate/time in the artist area, and clamps
at episode boundaries. Holds during scanning do not cycle rates. Ordinary music
keeps its existing controls. Current bounded catalog capacity is 32 channels and
96 weekly episodes; oversized catalogs or unsupported metadata are reported and
retain the previous drop rather than silently omitting episodes.

Keep the entire portable Manager folder together. Tool versions and archive
SHA-256 pins are in `tools/podcast-tools.lock.json`; executable hashes are in
`tools/manifest.json` and checked before use. Licenses are in `tools/licenses`.
The build script bundles the tools by default; developers can use
`-SkipPodcastTools` for code-only builds. These tools do not self-update.

## Listening scores and discovery

Manager reads `.rockbox/home-scores.tsv` from internal storage when loading the
library. The **Score** column in music and playlist views shows current points and
supports sorting.

These are the player's Home genre-autoplay listening scores: a completed play
earns two points; an exit at or beyond halfway earns one. Points halve every
30 days. Listening in ordinary playlists does not earn these points. Invalid
player timestamps retain the stored balance; future timestamps never increase
it. Set the player's clock correctly for meaningful decay.

Each Update installs up to two **pop** songs (Katy Perry-style mainstream pop) and
two **rap** songs as stereo 192-kbps MP3s in `MusicLibrary/Discovery/` on the SD
card, tagged with title, artist, album `Discovery` and genre `Pop` or `Hip-Hop/Rap`.
MusicBrainz supplies the picks: for each group, searches alternate between your two
highest-scoring artists in that group and the catalog tags `dance-pop`/`pop` or
`hip hop`/`trap`. Pop means library genres such as `Pop`, `Pop-Dance` and
`Dance-pop`, excluding indie, rock, alternative, folk and R&B blends; rap is any
hip-hop, rap or trap genre. Live, remix, edit, sped-up and similar versions are
skipped, as are songs already in the library, and each pick has a different artist.
yt-dlp downloads the first YouTube search result for "artist - title official audio"
(under 15 minutes). A failed download is skipped and may be offered again next time.
`%LOCALAPPDATA%\RockboxManager\discovery-history.json` remembers every installed
pick so a retired song is never suggested again.

New songs are appended to the **Discovery** playlist, which accumulates across
updates and can be edited like any other playlist. A Discovery song is deleted (with
its generated cover) only when all of these hold: it has been played (any score
row with listening), it is not liked, and its current score is below **1** point.
Unplayed songs keep their place until you hear them. Deleted songs are removed from
every ordinary playlist. Music outside `MusicLibrary/Discovery/` is never deleted.
New songs appear in Home genre autoplay only after Rockbox's database includes them.

Catalog queries send artist names and genre tags to MusicBrainz; the score file,
paths and listening counts stay local. Artwork and discovery share MusicBrainz's
one-request-per-second budget. The score reader uses the last complete valid
snapshot per exact, case-sensitive path, decodes escaped path characters once,
ignores an interrupted final row, and never edits the score file.

For a read-only JSON preview of what Update would delete and suggest:

```powershell
Start-Process C:\RockboxDev\rockbox\build-rockbox-manager\RockboxManager.exe -WindowStyle Hidden -Wait -ArgumentList '--suggest C:\RockboxDev\logs\suggestions.json'
```

This requires exactly one detected player, reads its music, likes and scores,
queries the catalog, and writes only the requested PC report; nothing is
downloaded or written to the player. It also accepts `--fixture`.

## Album artwork

The Clip/Clip+ firmware in this repository supports PC-prepared monochrome BMPs.
Now Playing has one layout: a borderless **61×61 cover** left, **song title and
artist wrapping** into up to four and two lines on the right, and a three-pixel
progress bar across the bottom. Only the final text row scrolls if it overflows.
Battery, shuffle, playback state, Add to Playlist, and the like heart share the
row above the bar beside the cover. There is no layout selection. Existing
pause, like and Add to Playlist button shortcuts keep working.

Manager first checks embedded front artwork, track-specific local images, and
album-specific local images. `cover`, `folder`, or `front` images apply only to
folders containing one album. It then uses its PC cache, an existing generated
player cover, or verified MusicBrainz / Cover Art Archive matches. Album searches
also check release titles and recording identity, preserving version qualifiers
and checking duration before accepting an unambiguous release.

If a verified album cover is unavailable, Manager reads the song's original
YouTube URL from its ID3 WOAS tag and tries that exact video's largest usable
thumbnail. Thumbnails are cached by video rather than album, so unrelated songs
under a shared album label keep their own images. The selected-song status and
reports identify the source. Missing album matches do not block this fallback.
Retry artwork bypasses cached misses and retries album lookups for thumbnail-backed
songs, allowing genuine release artwork to replace a fallback. Requests identify
the app, respect MusicBrainz's one-request-per-second limit, and use bounded retries.

Sources and provenance are cached in `%LOCALAPPDATA%\RockboxManager\Artwork`.
Conversion uses the supplied Sansa Cover Studio defaults: centered crop, 100% zoom,
125% contrast, brightness 0, cutoff 128, edge detail 40%, auto-levels, Floyd–Steinberg,
no inversion. The 61×61 output has no inset to maximize artwork size. Image resampling uses centered area sampling.
Thumbnails have clear paired black bars removed before square cropping. Original
album artwork retains its existing crop. Review metadata can hold an explicit
square crop (`X`, `Y`, `Size` in original-image pixels); it is tied to the original
image hash and bypasses automatic cropping. There are no conversion controls in
the main window.

Generated files are `<track-stem>.61x61.bmp` beside each audio file. A folder can
therefore hold different albums without sharing a wrong generic cover. If the
filename exceeds Rockbox's path limit, Manager uses the existing internal
`.rockbox/albumart/<artist>-<album>.61x61.bmp` lookup when it fits without a naming
collision. As a final fallback, a folder containing exactly one song can use
`cover.61x61.bmp`; Manager checks that condition again immediately before sending.
If a second song appears, that send is refused. Otherwise the report identifies
the path limitation. Audio files and
embedded tags stay untouched. Covers need no database rebuild. Original cached images are regenerated at full
size; if only an older `.44x44.bmp` remains, Manager upgrades it offline and
removes its old two-pixel padding. Old cover files are kept, and interrupted
older cover sends can still recover. Use this repository's
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

To prepare the PC cache and review BMPs from a local staging library while the
player is disconnected:

```powershell
Start-Process C:\RockboxDev\rockbox\build-rockbox-manager\RockboxManager.exe -WindowStyle Hidden -Wait -ArgumentList '--prepare-artwork C:\RockboxDev\logs\artwork-prepared.json --source-library C:\RockboxDev\RockboxCardReady'
```

This writes only the PC cache, requested report/progress file and a sibling
`.previews` directory. It never runs the send engine or writes music-side artwork.
Add `--source-storage sd` when preparing an SD-card staging tree so artwork paths
are checked with Rockbox's longer SD-volume prefix.
Both preparation commands accept `--retry-artwork` to bypass cached misses and
`--release-review <path>` for compatible verified release hints. After visual
review, connect the Clip+ and use the normal backfill or Send to Player action.

## Interrupted sends and recovery

Before writing, the app checks all affected files against their loaded hashes.
It backs up affected originals on the PC, flushes each temporary replacement,
and verifies its contents after replacement. Writes are per-file; a complete
multi-volume send is not atomic. Rename destinations precede
removal of the old names. The interface reports verified progress if a send fails.

**Update** continues the saved operation, including after restarting the app.
Connect the original player/card. Edits remain locked until it succeeds; if the
retry fails too, Update abandons it and reads the actual current files; already
applied changes remain. If another program/player changed an
affected file, reload and resolve the current versions instead of overwriting it.

Backups are kept in `%LOCALAPPDATA%\RockboxManager\Backups`. Each operation
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
reorder/rename/delete; likes; unsent-close prompt;
disconnect/reconnect; mirrored-copy conflict choices; Update and on-player playback.
Update acceptance: locked window and progress; close blocked while running; offline
catalog and missing tools reported after it finishes; Discovery playlist growth;
retirement of a played, unliked, low-scoring Discovery song only.

The player now displays all catalog playlists on Home automatically. There is
no pin limit or Home-order editor. Legacy pin files are left untouched. Persisted
older sends can still finish through the existing recovery mechanism.
