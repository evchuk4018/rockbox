# Managed podcasts interchange (version 1)

The Manager and Clip firmware share this UTF-8, LF-terminated TSV format.
Catalog: internal `/.rockbox/podcasts/catalog.tsv`. Resume records: internal
`/.rockbox/podcasts/resume/<video-id>.tsv`. Audio: SD card only, at
`/Podcasts/RockboxManager/<video-id>.mp3` (Rockbox absolute path prefix
`/<microSD1>`). Metadata is never encoded in audio filenames.

Catalog rows:

```text
ROCKBOX-PODCASTS\t1\tYYYY-MM-DD\tYYYY-MM-DD
C\tchannel-id\tchannel-name
E\tchannel-id\tvideo-id\ttitle\tpublication-unix-seconds\tduration-ms\t/<microSD1>/Podcasts/RockboxManager/video-id.mp3
```

The header dates are the inclusive Monday and Sunday of the latest completed
America/New_York week. A catalog with no E rows is a valid empty drop. Every E
row references a C row. Sort channels by name and episodes by publication time
(then ID). IDs contain only ASCII letters, digits, underscore and hyphen.
Strings escape percent, tab, CR and LF as `%25`, `%09`, `%0D`, `%0A`, decoded
exactly once. Paths must match the managed SD prefix and video-ID basename;
reject traversal, alternate volumes and embedded separators in IDs.

Resume record (one newline-terminated row):

```text
ROCKBOX-PODCAST-RESUME\t1\tvideo-id\telapsed-ms\t0-or-1-played
```

Malformed/unsupported records are ignored. Clamp elapsed to catalog duration;
Played selection restarts at zero. Flush temporary writes before replacement;
failed saves retain the previous record. Manager deletes only obsolete resume
records for episode IDs it previously installed, after catalog publication.

The catalog replacement is the transfer commit point. Before publication all
old audio and resume records remain usable. A durable Manager journal records
identity, owned paths, staged files, desired catalog and cleanup, allowing copy
retry or post-publication cleanup without treating a source error as an empty
week. An SD-root `database.ignore` in `Podcasts/RockboxManager` excludes audio
from Rockbox's music database; Manager also excludes this subtree from scans.
