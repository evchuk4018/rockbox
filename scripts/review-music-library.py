#!/usr/bin/env python3
"""Inventory, research and safely apply evidence-backed Rockbox tag corrections.

Research is read-only. Apply requires an explicit manifest and backs up each
changed file once. Audio is never transcoded and filenames are never changed.
"""
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from mutagen import File

FIELDS = ("title", "artist", "albumartist", "album", "discnumber", "tracknumber")
ROOTS = [Path("C:/RockboxDev") / name for name in
         ("MusicLibrary", "RockboxMusicFresh", "RockboxCardReady/M", "RockboxInternalReady/M")]
VARIANTS = re.compile(r"\b(remix|live|slowed|sped|nightcore|instrumental|cover|mashup|edit|hoodtrap)\b", re.I)
UA = "RockboxLibraryReview/1.0 (https://github.com/evchuk4018/rockbox)"


def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(path)


def norm(value):
    return re.sub(r"[^\w]+", " ", unicodedata.normalize("NFKC", value).casefold()).strip()


def clean_title(title, artist):
    title = re.sub(r"\s*[\[(](?:official (?:music )?(?:video|audio)|lyrics?|audio|visuali[sz]er)[^\])]*[\])]", "", title, flags=re.I)
    if title.casefold().startswith(artist.casefold() + " - "):
        title = title[len(artist) + 3:]
    return title.strip()


def primary(credit):
    # Reviewed aliases only. Commas in real artist names are significant.
    aliases = {"Violent Vira": "VIOLENT VIRA", "TREFUEGO - Topic": "TREFUEGO",
               "KENTENSHI, KENTENSHI": "KENTENSHI"}
    return aliases.get(credit.strip(), credit.strip())


def inventory(roots):
    rows = []
    for root in roots:
        for path in sorted(root.rglob("*")):
            if path.suffix.lower() not in {".mp3", ".opus", ".flac", ".m4a", ".ogg", ".wav"}:
                continue
            try:
                audio = File(path, easy=True)
                if audio is None:
                    raise ValueError("Unrecognized audio")
                tags = {k: list(audio.tags.get(k, [])) if audio.tags else [] for k in FIELDS}
                artist = primary(next(iter(tags["albumartist"] or tags["artist"]), ""))
                title = clean_title(next(iter(tags["title"]), path.stem), artist)
                rows.append({"path": str(path), "root": str(root), "tags": tags,
                             "title": title, "artist": artist,
                             "duration_ms": round(audio.info.length * 1000),
                             "size": path.stat().st_size})
            except Exception as exc:
                rows.append({"path": str(path), "error": str(exc)})
    return rows


class MusicBrainz:
    def __init__(self, cache):
        self.cache = cache
        self.last = 0

    def get(self, endpoint, **params):
        url = "https://musicbrainz.org/ws/2/" + endpoint + "?" + urllib.parse.urlencode({"fmt": "json", **params})
        path = self.cache / (hashlib.sha256(url.encode()).hexdigest() + ".json")
        if path.exists():
            return json.loads(path.read_text(encoding="utf-8")), url
        for attempt in range(4):
            time.sleep(max(0, 1.1 - (time.monotonic() - self.last)))
            self.last = time.monotonic()
            try:
                request = urllib.request.Request(url, headers={"User-Agent": UA})
                with urllib.request.urlopen(request, timeout=30) as response:
                    data = json.load(response)
                save(path, data)
                return data, url
            except (OSError, urllib.error.HTTPError):
                if attempt == 3:
                    raise
                time.sleep(2 ** attempt)


def credits(items):
    return "".join(item.get("name", item["artist"]["name"]) + item.get("joinphrase", "") for item in items)


def candidates(data, row):
    result = []
    for recording in data.get("recordings", []):
        if recording.get("video") or int(recording.get("score", 0)) < 95:
            continue
        if norm(recording["title"]) != norm(row["title"]):
            continue
        description = recording["title"] + " " + recording.get("disambiguation", "")
        if bool(VARIANTS.search(row["title"])) != bool(VARIANTS.search(description)):
            continue
        if abs(recording.get("length", 0) - row["duration_ms"]) > 3500:
            continue
        artist_names = [norm(c["artist"]["name"]) for c in recording.get("artist-credit", [])]
        if norm(row["artist"]) not in artist_names and norm(row["artist"]) != norm(credits(recording.get("artist-credit", []))):
            continue
        for release in recording.get("releases", []):
            group = release.get("release-group", {})
            if release.get("status") != "Official" or group.get("secondary-types"):
                continue
            kind = group.get("primary-type")
            if kind not in {"Album", "EP", "Single"}:
                continue
            # Search results omit redundant release credits for many albums.
            # The complete release is fetched and checked before any write.
            release_artists = release.get("artist-credit") or recording.get("artist-credit", [])
            if not release_artists or any(c["artist"]["name"] == "Various Artists" for c in release_artists):
                continue
            date = release.get("date", "9999")
            rank = (kind != "Album", kind == "Single", date,
                    bool(re.search(r"deluxe|expanded|anniversary|remaster", release["title"], re.I)), release["id"])
            result.append((rank, recording, release))
    return sorted(result, key=lambda item: item[0])


def research(rows, directory, retry=False):
    client = MusicBrainz(directory / "cache")
    output = directory / "review.json"
    reviews = json.loads(output.read_text(encoding="utf-8")) if output.exists() else {}
    grouped = {}
    for row in rows:
        if "error" not in row:
            key = norm(row["artist"]) + "|" + norm(row["title"])
            grouped.setdefault(key, []).append(row)
    for i, (key, copies) in enumerate(grouped.items()):
        if key in reviews and (not retry or reviews[key].get("status") == "verified"):
            continue
        row = copies[0]
        review = {"artist": row["artist"], "title": row["title"], "status": "unresolved",
                  "paths": [r["path"] for r in copies], "reason": "No unambiguous recording/release match"}
        try:
            escaped = lambda value: value.replace("\\", "\\\\").replace('"', '\\"')
            query = f'recording:"{escaped(row["title"])}" AND artist:"{escaped(row["artist"])}" AND video:false'
            data, source = client.get("recording/", query=query, limit=30)
            matches = candidates(data, row)
            if matches:
                _, recording, release = matches[0]
                # Full release data supplies verified disc and track positions.
                full, release_source = client.get("release/" + release["id"], inc="recordings+artist-credits+release-groups")
                if full.get("release-group", {}).get("secondary-types") or any(
                        item["artist"]["name"] == "Various Artists" for item in full.get("artist-credit", [])):
                    reviews[key] = review
                    save(output, reviews)
                    continue
                positions = [(m, t) for m in full.get("media", []) for t in m.get("tracks", [])
                             if t.get("recording", {}).get("id") == recording["id"]]
                if len(positions) == 1:
                    media, track = positions[0]
                    review.update(status="verified", recording_id=recording["id"], release_id=release["id"],
                        tags={"artist": [credits(recording["artist-credit"])],
                              "albumartist": [credits(full["artist-credit"])], "album": [full["title"]],
                              "discnumber": [str(media["position"])], "tracknumber": [str(track["position"])]},
                        sources=[source, release_source], duration_ms=recording["length"])
                    review.pop("reason", None)
            reviews[key] = review
        except Exception as exc:
            review["reason"] = str(exc)
            reviews[key] = review
        save(output, reviews)
        if i % 20 == 0:
            print(json.dumps({"reviewed": i + 1, "total": len(grouped), "verified": sum(r["status"] == "verified" for r in reviews.values())}), flush=True)
    print(json.dumps({"reviewed": len(reviews), "verified": sum(r["status"] == "verified" for r in reviews.values())}), flush=True)


def audio_hash(path):
    import imageio_ffmpeg
    result = subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-nostdin", "-v", "error", "-i", str(path),
                             "-map", "0:a:0", "-c:a", "copy", "-f", "hash", "-hash", "sha256", "-"],
                            check=True, capture_output=True, text=True)
    return result.stdout.strip()


def refine_cached(rows, directory):
    """Review unmatched songs against complete, already-researched tracklists.

    Search ranking can hide an original album below many compilations. A full
    album also identifies guest credits without splitting punctuation in names.
    """
    reviews = json.loads((directory / "review.json").read_text(encoding="utf-8"))
    overrides_path = directory / "identity-overrides.json"
    overrides = json.loads(overrides_path.read_text(encoding="utf-8")) if overrides_path.exists() else {}
    aliases = {}
    releases = []

    def visit(data):
        if isinstance(data, dict):
            artist = data.get("artist")
            if isinstance(artist, dict) and artist.get("name"):
                canonical = norm(artist["name"])
                aliases[canonical] = canonical
                for alias in artist.get("aliases", []):
                    aliases[norm(alias["name"])] = canonical
            for value in data.values():
                visit(value)
        elif isinstance(data, list):
            for value in data:
                visit(value)

    def title_key(title):
        title = re.sub(r"\s*[\[(](?:feat\.?|ft\.?)\s+[^\])]+[\])]", "", title, flags=re.I)
        title = re.sub(r"\s+(?:feat\.?|ft\.?)\s+.*$", "", title, flags=re.I)
        return norm(title)

    index = {}
    for cache in (directory / "cache").glob("*.json"):
        data = json.loads(cache.read_text(encoding="utf-8"))
        visit(data)
        if "media" in data and "tracks" in (data.get("media") or [{}])[0]:
            releases.append(data)
    for release in releases:
        group = release.get("release-group", {})
        kind = group.get("primary-type")
        if release.get("status") != "Official" or group.get("secondary-types") or kind not in {"Album", "EP", "Single"}:
            continue
        album_artist = credits(release.get("artist-credit", []))
        if not album_artist or album_artist == "Various Artists":
            continue
        rank = (kind != "Album", kind == "Single", release.get("date", "9999"),
                bool(re.search(r"deluxe|expanded|anniversary|remaster", release["title"], re.I)), release["id"])
        for media in release.get("media", []):
            for track in media.get("tracks", []):
                recording = track.get("recording", {})
                length = track.get("length") or recording.get("length")
                if not length or recording.get("video"):
                    continue
                names = track.get("artist-credit") or recording.get("artist-credit") or release["artist-credit"]
                description = track["title"] + " " + recording.get("disambiguation", "")
                candidate = (rank, release, media, track, recording, length, names, description)
                index.setdefault(title_key(track["title"]), []).append(candidate)
    for row in rows:
        if "error" in row:
            continue
        key = norm(row["artist"]) + "|" + norm(row["title"])
        identity = overrides.get(key, {})
        lookup_artist = identity.get("artist", row["artist"])
        lookup_title = identity.get("title", row["title"])
        previous = reviews.get(key, {})
        matches = []
        for candidate in index.get(title_key(lookup_title), []):
            rank, release, media, track, recording, length, names, description = candidate
            if abs(length - row["duration_ms"]) > 3500 or bool(VARIANTS.search(row["title"])) != bool(VARIANTS.search(description)):
                continue
            local = aliases.get(norm(lookup_artist), norm(lookup_artist))
            known = [norm(item["artist"]["name"]) for item in names + release["artist-credit"]]
            # Match entire canonical names, or a known primary credit followed
            # by guest credits. Never split a name such as Tyler, The Creator.
            if local not in known and not any(norm(lookup_artist).startswith(name + " ") for name in known):
                continue
            matches.append(candidate)
        if not matches:
            continue
        rank, release, media, track, recording, length, names, description = min(matches, key=lambda item: item[0])
        # Keep a direct search result when no better album is established.
        if previous.get("status") == "verified" and previous.get("release_id") == release["id"]:
            continue
        reviews[key] = {"artist": row["artist"], "title": row["title"], "status": "verified",
                        "recording_id": recording.get("id"), "release_id": release["id"],
                        "duration_ms": length, "paths": previous.get("paths", [row["path"]]),
                        "tags": {"artist": [credits(names)], "albumartist": [credits(release["artist-credit"])],
                                 "album": [release["title"]], "discnumber": [str(media["position"])],
                                 "tracknumber": [str(track["position"])]},
                        "sources": ["https://musicbrainz.org/release/" + release["id"],
                                    "https://musicbrainz.org/recording/" + recording["id"], *identity.get("sources", [])],
                        "method": "Full release tracklist, artist credit, version and duration reviewed"}
    save(directory / "review-refined.json", reviews)
    print(json.dumps({"reviewed": len(reviews), "verified": sum(r["status"] == "verified" for r in reviews.values())}))


def apply(rows, reviews, directory):
    journal_path = directory / "applied.json"
    journal = json.loads(journal_path.read_text(encoding="utf-8")) if journal_path.exists() else {}
    for row in rows:
        if "error" in row:
            continue
        path = Path(row["path"])
        key = norm(row["artist"]) + "|" + norm(row["title"])
        review = reviews.get(key, {})
        if review.get("status") != "verified" or abs(row["duration_ms"] - review["duration_ms"]) > 3500:
            continue
        audio = File(path, easy=True)
        desired = review["tags"]
        if all(list(audio.tags.get(k, [])) == v for k, v in desired.items()):
            continue
        current = {k: list(audio.tags.get(k, [])) for k in FIELDS}
        if current != row["tags"] or abs(round(audio.info.length * 1000) - row["duration_ms"]) > 10:
            raise RuntimeError(f"File changed since inventory; review it again: {path}")
        identity = hashlib.sha256(str(path).encode()).hexdigest()
        backup = directory / "originals" / (identity + path.suffix)
        before = hashlib.sha256(path.read_bytes()).hexdigest()
        if identity in journal:
            raise RuntimeError(f"Previously applied file changed: {path}")
        if backup.exists() and hashlib.sha256(backup.read_bytes()).hexdigest() != before:
            raise RuntimeError(f"Backup conflict: {path}")
        backup.parent.mkdir(parents=True, exist_ok=True)
        if not backup.exists():
            shutil.copy2(path, backup)
        digest = audio_hash(path)
        try:
            for tag, value in desired.items():
                audio[tag] = value
            audio.save()
            assert audio_hash(path) == digest, f"Encoded audio changed: {path}"
            actual = File(path, easy=True)
            assert all(list(actual.tags.get(k, [])) == v for k, v in desired.items()), path
        except Exception:
            shutil.copy2(backup, path)
            raise
        journal[identity] = {"path": str(path), "backup": str(backup), "before_sha256": before,
                             "after_sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "audio_sha256": digest,
                             "before_tags": row["tags"], "after_tags": desired, "sources": review["sources"]}
        save(journal_path, journal)
        if len(journal) % 50 == 0:
            print(f"Verified {len(journal)} retagged files", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["inventory", "research", "retry", "refine", "apply"])
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--root", type=Path, action="append")
    parser.add_argument("--review", type=Path)
    args = parser.parse_args()
    inventory_path = args.work / "inventory.json"
    if args.command == "inventory":
        rows = inventory(args.root or ROOTS)
        save(inventory_path, rows)
        print(json.dumps({"files": len(rows), "errors": sum("error" in r for r in rows)}))
    else:
        rows = json.loads(inventory_path.read_text(encoding="utf-8"))
        if args.command in {"research", "retry"}:
            research(rows, args.work, retry=args.command == "retry")
        elif args.command == "refine":
            refine_cached(rows, args.work)
        else:
            apply(rows, json.loads((args.review or args.work / "review.json").read_text(encoding="utf-8")), args.work)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
