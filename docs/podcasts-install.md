# Weekly Podcasts — Manager and Clip+

## Windows Manager

Extract the entire portable ZIP into a PC folder and run `RockboxManager.exe`.
Keep `tools/`, its manifest/licenses, README and notices alongside it. No .NET
installation or administrator permission is needed. Open **Podcasts** while
connected or disconnected, then add a public YouTube channel URL. Subscriptions
start empty. Keep Manager running to prepare audio and synchronize USB connections.

## Clip+ firmware

Use only the matching **sansaclipplus** firmware ZIP with an existing Rockbox
bootloader. This is a firmware/resources update, not a bootloader flash.

1. Identify internal storage by `version.sdk` (`Product: Clip+`) and
   `.rockbox/rockbox-info.txt` (`Target: sansaclipplus`). Identify the original
   SD card by its volume serial; drive letters may change.
2. Back up `.rockbox` and playlists. Verify the download against `SHA256SUMS.txt`.
3. Merge the ZIP's `.rockbox` folder into internal storage. Replace packaged
   firmware/resources, preserving config, database, playlists, artwork, scores
   and personal files. Never format either volume or replace its entire contents.
   `scripts/install-clipplus.py` automates identity checks, backup, flushed writes,
   read-back hashes and protected-file verification on Windows; see `--help`.
4. Safely eject **both volumes**, disconnect USB and restart the player.

No music database rebuild is needed. An SD card is required for podcast audio;
there is no internal fallback. Resolve filesystem repair warnings before podcast
transfers. Physical display, buttons and audio acceptance are user checks.

## Weekly behavior and recovery

The requested drop is the previous Monday–Sunday in America/New_York. Successful
replacement removes the entire old drop, including unfinished episodes. Same-week
reconnects preserve the drop and positions; subscription changes replace it on
sync. Missed weeks load only the latest completed week. A checked empty week
clears the old drop, while source/download failures preserve it.

Keep space for the old and new drop together. Before catalog publication,
cancellation, low space, wrong/missing cards or interrupted transfers leave the
old drop usable. Reconnect the same player/card and choose **Retry**. A persistent
PC journal resumes pending work or finishes cleanup after publication. Do not
delete the Manager Podcasts cache/journal during recovery. Unrelated music,
artwork, playlists and scores are preserved.

## Player controls

Home **Podcasts**, directly below playlists, opens **Channels → Episodes**.
Episodes resume independently; Played episodes restart at zero. Each stops at its
end without changing music repeat/shuffle preferences. Resume saves occur on pause,
exit, stop and finish, with 30-second Now Playing checkpoints.

- **Right tap:** forward scan at 5 seconds/second; further taps cycle 10, 15, 5.
- **Left tap:** backward scan; a direction change resets to 5.
- **Middle while scanning:** commit the displayed target and resume playback.
- **Middle otherwise:** normal pause/resume.
- **Leave Now Playing while scanning:** commit/save and remain paused.
- **Hold Left/Right outside scanning:** existing held seeking. Holds during a scan
  do not repeatedly change its rate. Ordinary music controls are unchanged.

Scanning pauses audio, displays direction/rate/time and clamps at episode bounds.
Channels sort alphabetically; episodes sort oldest first with New/Saved/Played
labels. Full use and recovery details are in the portable Manager README.
