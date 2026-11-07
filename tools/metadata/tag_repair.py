import argparse
import hashlib
import json
import os
import re
import shutil
import struct
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

import mutagen
from mutagen.id3 import TALB, TCON, TDRC, TIT2, TPE1, TPE2, TPOS, TRCK, Encoding, ID3
from mutagen.oggopus import OggOpus

AUDIO_EXTENSIONS = {".mp3", ".opus", ".flac", ".m4a", ".ogg", ".wav"}
TAG_FIELDS = ("artist", "albumartist", "album", "title", "genre", "tracknumber", "discnumber", "date")

ALLOWED_GENRES = (
    "Pop-Dance",
    "Pop-Indie",
    "Pop-Rock",
    "Pop-R&B",
    "Pop-Acoustic",
    "Pop-Soul",
    "Hip-Hop/Rap-Trap",
    "Hip-Hop/Rap-Pop Rap",
    "Hip-Hop/Rap-Conscious",
    "Hip-Hop/Rap-Emo",
    "Hip-Hop/Rap-Drill",
    "R&B/Soul-Alternative",
    "R&B/Soul-Contemporary",
    "Rock-Alternative",
    "Rock-Indie",
    "Rock-Pop",
    "Electronic-House",
    "Electronic-Hardstyle",
    "Electronic-Chill",
    "Country-Pop",
    "Jazz-Standard",
    "Folk-Indie",
    "Other-Uncategorized",
)

TOP_LEVELS = {g.split("-", 1)[0] for g in ALLOWED_GENRES if not g.startswith("Hip-Hop/Rap-") and not g.startswith("R&B/Soul-")}
TOP_LEVELS.update(("Hip-Hop/Rap", "R&B/Soul"))

GENERIC_DEFAULTS = {
    "pop": "Pop-Dance",
    "hip hop": "Hip-Hop/Rap-Trap",
    "hip-hop": "Hip-Hop/Rap-Trap",
    "hip-hop/rap": "Hip-Hop/Rap-Trap",
    "rap": "Hip-Hop/Rap-Trap",
    "rock": "Rock-Alternative",
    "electronic": "Electronic-House",
    "r&b": "R&B/Soul-Contemporary",
    "r&b/soul": "R&B/Soul-Contemporary",
    "country": "Country-Pop",
    "jazz": "Jazz-Standard",
    "folk": "Folk-Indie",
    "other": "Other-Uncategorized",
}

SPECIFIC_TERMS = {
    "dance-pop": "Pop-Dance",
    "dance pop": "Pop-Dance",
    "electropop": "Pop-Dance",
    "electro pop": "Pop-Dance",
    "synth-pop": "Pop-Dance",
    "synthpop": "Pop-Dance",
    "teen pop": "Pop-Dance",
    "bubblegum pop": "Pop-Dance",
    "dance": "Pop-Dance",
    "edm": "Electronic-House",
    "electro": "Pop-Dance",
    "electro house": "Electronic-House",
    "house": "Electronic-House",
    "deep house": "Electronic-House",
    "tech house": "Electronic-House",
    "progressive house": "Electronic-House",
    "future bass": "Electronic-House",
    "disco": "Pop-Soul",
    "nu-disco": "Pop-Soul",
    "funk": "Pop-Soul",
    "soul": "Pop-Soul",
    "motown": "Pop-Soul",
    "classic soul": "Pop-Soul",
    "northern soul": "Pop-Soul",
    "blue-eyed soul": "Pop-Soul",
    "pop soul": "Pop-Soul",
    "indie pop": "Pop-Indie",
    "bedroom pop": "Pop-Indie",
    "dream pop": "Pop-Indie",
    "chillwave": "Pop-Indie",
    "lo-fi": "Pop-Indie",
    "lo fi": "Pop-Indie",
    "jangle pop": "Pop-Indie",
    "twee pop": "Pop-Indie",
    "alternative pop": "Pop-Indie",
    "chamber pop": "Pop-Acoustic",
    "pop rock": "Pop-Rock",
    "pop punk": "Pop-Rock",
    "pop-punk": "Pop-Rock",
    "power pop": "Pop-Rock",
    "pop r&b": "Pop-R&B",
    "pop rap": "Hip-Hop/Rap-Pop Rap",
    "hip pop": "Hip-Hop/Rap-Pop Rap",
    "dirty rap": "Hip-Hop/Rap-Pop Rap",
    "snap": "Hip-Hop/Rap-Pop Rap",
    "crunk": "Hip-Hop/Rap-Pop Rap",
    "bounce": "Hip-Hop/Rap-Pop Rap",
    "trap": "Hip-Hop/Rap-Trap",
    "trap rap": "Hip-Hop/Rap-Trap",
    "southern hip hop": "Hip-Hop/Rap-Trap",
    "southern rap": "Hip-Hop/Rap-Trap",
    "phonk": "Hip-Hop/Rap-Trap",
    "drift phonk": "Hip-Hop/Rap-Trap",
    "rage": "Hip-Hop/Rap-Trap",
    "plugg": "Hip-Hop/Rap-Trap",
    "cloud rap": "Hip-Hop/Rap-Trap",
    "conscious hip hop": "Hip-Hop/Rap-Conscious",
    "conscious hip-hop": "Hip-Hop/Rap-Conscious",
    "conscious rap": "Hip-Hop/Rap-Conscious",
    "political hip hop": "Hip-Hop/Rap-Conscious",
    "boom bap": "Hip-Hop/Rap-Conscious",
    "east coast hip hop": "Hip-Hop/Rap-Conscious",
    "west coast hip-hop": "Hip-Hop/Rap-Conscious",
    "west coast hip hop": "Hip-Hop/Rap-Conscious",
    "lyrical rap": "Hip-Hop/Rap-Conscious",
    "jazz rap": "Hip-Hop/Rap-Conscious",
    "alternative hip hop": "Hip-Hop/Rap-Conscious",
    "emo rap": "Hip-Hop/Rap-Emo",
    "sad rap": "Hip-Hop/Rap-Emo",
    "melodic rap": "Hip-Hop/Rap-Emo",
    "lo-fi hip hop": "Hip-Hop/Rap-Emo",
    "drill": "Hip-Hop/Rap-Drill",
    "uk drill": "Hip-Hop/Rap-Drill",
    "chicago drill": "Hip-Hop/Rap-Drill",
    "ny drill": "Hip-Hop/Rap-Drill",
    "brooklyn drill": "Hip-Hop/Rap-Drill",
    "alternative r&b": "R&B/Soul-Alternative",
    "alt r&b": "R&B/Soul-Alternative",
    "neo-soul": "R&B/Soul-Alternative",
    "neo soul": "R&B/Soul-Alternative",
    "experimental r&b": "R&B/Soul-Alternative",
    "indie r&b": "R&B/Soul-Alternative",
    "pbr&b": "R&B/Soul-Alternative",
    "contemporary r&b": "R&B/Soul-Contemporary",
    "quiet storm": "R&B/Soul-Contemporary",
    "new jack swing": "R&B/Soul-Contemporary",
    "hip hop soul": "R&B/Soul-Contemporary",
    "trap&b": "R&B/Soul-Contemporary",
    "alternative rock": "Rock-Alternative",
    "art rock": "Rock-Alternative",
    "shoegaze": "Rock-Alternative",
    "grunge": "Rock-Alternative",
    "post-punk": "Rock-Alternative",
    "new wave": "Rock-Alternative",
    "post-hardcore": "Rock-Alternative",
    "math rock": "Rock-Alternative",
    "noise rock": "Rock-Alternative",
    "space rock": "Rock-Alternative",
    "psychedelic rock": "Rock-Alternative",
    "indie rock": "Rock-Indie",
    "garage rock": "Rock-Indie",
    "surf rock": "Rock-Indie",
    "lo-fi rock": "Rock-Indie",
    "punk rock": "Rock-Pop",
    "skate punk": "Rock-Pop",
    "hardstyle": "Electronic-Hardstyle",
    "hardstyle remix": "Electronic-Hardstyle",
    "hardcore": "Electronic-Hardstyle",
    "gabber": "Electronic-Hardstyle",
    "rawstyle": "Electronic-Hardstyle",
    "frenchcore": "Electronic-Hardstyle",
    "speedcore": "Electronic-Hardstyle",
    "ambient": "Electronic-Chill",
    "downtempo": "Electronic-Chill",
    "chillout": "Electronic-Chill",
    "trip hop": "Electronic-Chill",
    "idm": "Electronic-Chill",
    "glitch": "Electronic-Chill",
    "vaporwave": "Electronic-Chill",
    "synthwave": "Electronic-Chill",
    "new age": "Electronic-Chill",
    "country pop": "Country-Pop",
    "contemporary country": "Country-Pop",
    "jazz": "Jazz-Standard",
    "vocal jazz": "Jazz-Standard",
    "swing": "Jazz-Standard",
    "big band": "Jazz-Standard",
    "bebop": "Jazz-Standard",
    "cool jazz": "Jazz-Standard",
    "bossa nova": "Jazz-Standard",
    "smooth jazz": "Jazz-Standard",
    "indie folk": "Folk-Indie",
    "folk": "Folk-Indie",
    "folk rock": "Folk-Indie",
    "americana": "Folk-Indie",
    "singer-songwriter": "Folk-Indie",
    "acoustic": "Pop-Acoustic",
    "acoustic pop": "Pop-Acoustic",
    "folk pop": "Pop-Acoustic",
    "ballad": "Pop-Acoustic",
    "pop ballad": "Pop-Acoustic",
    "uncategorized": "Other-Uncategorized",
    "<untagged>": "Other-Uncategorized",
    "dancehall": "Pop-Dance",
    "uk garage": "Pop-Dance",
    "latin pop": "Pop-Dance",
    "breakcore": "Electronic-Hardstyle",
    "hyperpop": "Pop-Dance",
    "chill rap": "Hip-Hop/Rap-Emo",
    "mylancore": "Pop-Dance",
    "hardstyle / mylancore": "Pop-Dance",
    "hoodtrap": "Hip-Hop/Rap-Trap",
    "hoodtrap / mylancore": "Hip-Hop/Rap-Trap",
    "post-hardcore": "Rock-Alternative",
    "pop punk": "Pop-Rock",
    "pop rock": "Pop-Rock",
    "dance-pop": "Pop-Dance",
}

FORCE_ARTIST_GENRES = {
    "the foundations": "Pop-Soul",
    "the ronettes": "Pop-Soul",
    "frankie valli": "Pop-Soul",
    "gloria gaynor": "Pop-Soul",
    "elton john & kiki dee": "Pop-Soul",
    "rupert holmes": "Pop-Soul",
    "boney m.": "Pop-Soul",
    "boney m": "Pop-Soul",
    "tv girl": "Pop-Indie",
    "g h ö s t": "Hip-Hop/Rap-Trap",
    "g h o s t": "Hip-Hop/Rap-Trap",
}

ARTIST_GENRES = {
    "taylor swift": "Pop-Dance",
    "katy perry": "Pop-Dance",
    "dua lipa": "Pop-Dance",
    "kesha": "Pop-Dance",
    "lady gaga": "Pop-Dance",
    "britney spears": "Pop-Dance",
    "sabrina carpenter": "Pop-Dance",
    "chappell roan": "Pop-Dance",
    "ava max": "Pop-Dance",
    "tove lo": "Pop-Dance",
    "kim petras": "Pop-Dance",
    "carly rae jepsen": "Pop-Dance",
    "selena gomez": "Pop-Dance",
    "ariana grande": "Pop-R&B",
    "the weeknd": "R&B/Soul-Alternative",
    "sza": "R&B/Soul-Contemporary",
    "doja cat": "Hip-Hop/Rap-Pop Rap",
    "nicki minaj": "Hip-Hop/Rap-Pop Rap",
    "cardi b": "Hip-Hop/Rap-Pop Rap",
    "megan thee stallion": "Hip-Hop/Rap-Pop Rap",
    "drake": "Hip-Hop/Rap-Pop Rap",
    "post malone": "Hip-Hop/Rap-Pop Rap",
    "kanye west": "Hip-Hop/Rap-Pop Rap",
    "jay-z": "Hip-Hop/Rap-Pop Rap",
    "jaÿ-z": "Hip-Hop/Rap-Pop Rap",
    "future": "Hip-Hop/Rap-Trap",
    "metro boomin": "Hip-Hop/Rap-Trap",
    "21 savage": "Hip-Hop/Rap-Trap",
    "travis scott": "Hip-Hop/Rap-Trap",
    "playboi carti": "Hip-Hop/Rap-Trap",
    "ken carson": "Hip-Hop/Rap-Trap",
    "lil baby": "Hip-Hop/Rap-Trap",
    "dababy": "Hip-Hop/Rap-Trap",
    "kendrick lamar": "Hip-Hop/Rap-Conscious",
    "j. cole": "Hip-Hop/Rap-Conscious",
    "benny the butcher": "Hip-Hop/Rap-Conscious",
    "tyler, the creator": "Hip-Hop/Rap-Conscious",
    "lil durk": "Hip-Hop/Rap-Drill",
    "central cee": "Hip-Hop/Rap-Drill",
    "chief keef": "Hip-Hop/Rap-Drill",
    "juice wrld": "Hip-Hop/Rap-Emo",
    "trippie redd": "Hip-Hop/Rap-Emo",
    "olivia rodrigo": "Pop-Rock",
    "paramore": "Rock-Pop",
    "p!nk": "Rock-Pop",
    "kelly clarkson": "Rock-Pop",
    "pinkshift": "Rock-Pop",
    "fried by fluoride": "Rock-Pop",
    "radiohead": "Rock-Alternative",
    "warpaint": "Rock-Alternative",
    "my bloody valentine": "Rock-Alternative",
    "whirr": "Rock-Alternative",
    "mazzy star": "Rock-Alternative",
    "deftones": "Rock-Alternative",
    "system of a down": "Rock-Alternative",
    "flyleaf": "Rock-Alternative",
    "darkest hour": "Rock-Alternative",
    "candlebox": "Rock-Alternative",
    "pixies": "Rock-Alternative",
    "blur": "Rock-Alternative",
    "the white stripes": "Rock-Alternative",
    "metric": "Rock-Alternative",
    "softcult": "Rock-Alternative",
    "glare": "Rock-Alternative",
    "trauma ray": "Rock-Alternative",
    "fleshwater": "Rock-Alternative",
    "have a nice life": "Rock-Alternative",
    "the neighbourhood": "Rock-Alternative",
    "alvvays": "Pop-Indie",
    "billie eilish": "Pop-Indie",
    "halsey": "Pop-Indie",
    "beabadoobee": "Pop-Indie",
    "clairo": "Pop-Indie",
    "sidney gish": "Pop-Indie",
    "the 1975": "Pop-Indie",
    "two door cinema club": "Pop-Indie",
    "dayglow": "Pop-Indie",
    "still woozy": "Pop-Indie",
    "gus dapperton": "Pop-Indie",
    "beach bunny": "Rock-Indie",
    "wallows": "Rock-Indie",
    "snail mail": "Rock-Indie",
    "soccer mommy": "Rock-Indie",
    "mitski": "Rock-Indie",
    "hop along": "Rock-Indie",
    "surf curse": "Rock-Indie",
    "calvin harris": "Electronic-House",
    "avicii": "Electronic-House",
    "zedd": "Electronic-House",
    "the chainsmokers": "Electronic-House",
    "daft punk": "Electronic-House",
    "lcd soundsystem": "Electronic-House",
    "caribou": "Electronic-House",
    "jeff mills": "Electronic-House",
    "holy fuck": "Electronic-Chill",
    "zizzle": "Electronic-Chill",
    "the books": "Electronic-Chill",
    "pocahaunted": "Electronic-Chill",
    "team sleep": "Electronic-Chill",
    "muller": "Electronic-Hardstyle",
    "baki, zyzz & gym hardstyle": "Electronic-Hardstyle",
    "laufey": "Jazz-Standard",
    "jabberloop": "Jazz-Standard",
    "baila nova": "Jazz-Standard",
    "boygenius": "Folk-Indie",
    "phoebe bridgers": "Folk-Indie",
    "noah kahan": "Folk-Indie",
    "gracie abrams": "Pop-Acoustic",
    "shaboozey": "Country-Pop",
    "hozier": "Pop-Soul",
    "faye webster": "Folk-Indie",
    "rainbow kitten surprise": "Folk-Indie",
}

ALBUM_GENRES = (
    ("taylor swift", "fearless", "Country-Pop"),
    ("taylor swift", "red", "Country-Pop"),
    ("taylor swift", "folklore", "Folk-Indie"),
    ("taylor swift", "evermore", "Folk-Indie"),
    ("taylor swift", "1989", "Pop-Dance"),
    ("taylor swift", "lover", "Pop-Dance"),
    ("taylor swift", "reputation", "Pop-Dance"),
    ("taylor swift", "midnights", "Pop-Dance"),
    ("taylor swift", "tortured poets", "Pop-Dance"),
    ("katy perry", "one of the boys", "Pop-Rock"),
    ("paramore", "all we know is falling", "Rock-Pop"),
    ("paramore", "brand new eyes", "Rock-Pop"),
    ("paramore", "paramore", "Rock-Pop"),
)

TITLE_GENRES = {
    ("the foundations", "build me up buttercup (mono)"): "Pop-Soul",
    ("g h ö s t", "i said advance goddamnit"): "Hip-Hop/Rap-Trap",
    ("g h o s t", "i said advance goddamnit"): "Hip-Hop/Rap-Trap",
}

TITLE_ALBUMS = {
    ("calvin harris", "outside"): "Motion",
    ("kesha", "blah blah blah"): "Animal",
    ("kelly clarkson", "stronger"): "Stronger",
}

ALBUMARTIST_ALIASES = {
    "ケイティ・ペリー": "Katy Perry",
    "奧莉維亞": "Olivia Rodrigo",
    "ビリー・アイリッシュ": "Billie Eilish",
    "デミ・ロヴァート": "Demi Lovato",
    "ジェニー・アイコ": "Jhené Aiko",
    "カーディ・B": "Cardi B",
    "アリアナ・グランデ": "Ariana Grande",
    "ザ・ウィークエンド": "The Weeknd",
    "ブルーノ・マーズ": "Bruno Mars",
    "マイリー・サイラス": "Miley Cyrus",
    "テイラー・スウィフト": "Taylor Swift",
}

PLAYLIST_ALBUMS = {
    "study (one-time)",
    "liked music",
    "night drive",
    "white girl party music",
    "viral tiktok songs",
    "more viral/trending",
    "pop",
    "hip-hop/rap",
    "indie/alternative",
    "sad",
    "study",
    "drive",
}

MONTH_ALBUM = re.compile(r"^(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s+\d{1,2},\s+\d{4}$", re.IGNORECASE)

VIDEO_SUFFIXES = (
    re.compile(r"\s*\((?:official\s+)?(?:music\s+)?video\)\s*$", re.IGNORECASE),
    re.compile(r"\s*\[(?:official\s+)?(?:music\s+)?video\]\s*$", re.IGNORECASE),
    re.compile(r"\s*\((?:official\s+)?audio(?:\s+only)?\)\s*$", re.IGNORECASE),
    re.compile(r"\s*\[(?:official\s+)?audio(?:\s+only)?\]\s*$", re.IGNORECASE),
    re.compile(r"\s*\((?:official\s+)?lyrics?(?:\s+video)?\)\s*$", re.IGNORECASE),
    re.compile(r"\s*\[(?:official\s+)?lyrics?(?:\s+video)?\]\s*$", re.IGNORECASE),
    re.compile(r"\s*\(audio\s+only\)\s*$", re.IGNORECASE),
    re.compile(r"\s*\[audio\s+only\]\s*$", re.IGNORECASE),
)

REVIEW_VERIFIED = None
REVIEW_PATH_MAP = None


def normalize_space(value):
    return re.sub(r"\s+", " ", (value or "").strip())


def genre_tokens(value):
    if not value:
        return []
    if isinstance(value, (list, tuple)):
        value = "; ".join(str(v) for v in value)
    text = str(value).casefold()
    tokens = [t.strip() for t in re.split(r"[;,]", text) if t.strip()]
    return tokens or [text.strip()]


IGNORED_TERMS = {
    "music",
    "people & blogs",
    "gaming",
    "entertainment",
    "film & animation",
    "comedy",
    "howto & style",
    "sports",
    "news & politics",
    "education",
    "science & technology",
    "autos & vehicles",
    "travel & events",
    "pets & animals",
    "nonprofits & activism",
}


def map_term(term):
    key = normalize_space(term).casefold()
    if not key or key in IGNORED_TERMS:
        return None
    if key in SPECIFIC_TERMS:
        genre = SPECIFIC_TERMS[key]
        return _genre_top(genre), genre
    if key in GENERIC_DEFAULTS:
        genre = GENERIC_DEFAULTS[key]
        return _genre_top(genre), genre
    return None


def term_score(term):
    key = normalize_space(term).casefold()
    if key in SPECIFIC_TERMS:
        return 1
    if key in GENERIC_DEFAULTS:
        return 0
    return -1


def is_allowed(genre):
    return genre in ALLOWED_GENRES


def _genre_top(genre):
    if genre.startswith("Hip-Hop/Rap-"):
        return "Hip-Hop/Rap"
    if genre.startswith("R&B/Soul-"):
        return "R&B/Soul"
    return genre.split("-", 1)[0]


def profile_from_terms(terms):
    counts = {}
    for term in terms:
        key = normalize_space(term).casefold()
        if key in IGNORED_TERMS:
            continue
        genre = SPECIFIC_TERMS.get(key) or GENERIC_DEFAULTS.get(key)
        if genre:
            counts[genre] = counts.get(genre, 0) + 1
    return counts


def _profile_counts(profile):
    if not profile:
        return {}
    if isinstance(profile, dict):
        if all(isinstance(v, int) for v in profile.values()):
            return dict(profile)
        result = {}
        for term, count in profile.items():
            genre = SPECIFIC_TERMS.get(str(term).casefold()) or GENERIC_DEFAULTS.get(str(term).casefold())
            if genre:
                result[genre] = result.get(genre, 0) + int(count)
        return result
    return profile_from_terms(profile)


def _pick_profile_genre(counts, top=None):
    if not counts:
        return None
    if top:
        candidates = {g: c for g, c in counts.items() if _genre_top(g) == top}
        if candidates:
            return max(sorted(candidates), key=lambda g: candidates[g])
    return max(sorted(counts), key=lambda g: counts[g])


def _override_get(overrides, section, key):
    if not overrides:
        return None
    data = overrides.get(section) or {}
    return data.get(key) or data.get(key.casefold())


def classify_genre(artist, title, album, current_genres, profile, overrides=None):
    artist_key = normalize_space(artist).casefold()
    title_key = normalize_space(title).casefold()
    album_key = normalize_space(album).casefold()
    override_value = _override_get(overrides, "titles", f"{artist_key}||{title_key}")
    if override_value:
        return override_value
    override_value = _override_get(overrides, "artists", artist_key)
    if override_value and not current_genres:
        return override_value
    for forced_artist, forced_genre in FORCE_ARTIST_GENRES.items():
        if artist_key == forced_artist:
            return forced_genre
    for forced_artist, forced_album, forced_genre in ALBUM_GENRES:
        if artist_key == forced_artist and forced_album in album_key:
            return forced_genre
    if (artist_key, title_key) in TITLE_GENRES:
        return TITLE_GENRES[(artist_key, title_key)]
    counts = _profile_counts(profile)
    if override_value is None:
        override_value = ARTIST_GENRES.get(artist_key)
    candidates = []
    for index, token in enumerate(genre_tokens(current_genres)):
        genre = SPECIFIC_TERMS.get(normalize_space(token).casefold())
        if genre is None:
            genre = GENERIC_DEFAULTS.get(normalize_space(token).casefold())
        if genre:
            candidates.append((term_score(token), genre, index))
    if candidates:
        best_score = max(c[0] for c in candidates)
        best = [c for c in candidates if c[0] == best_score]
        if best_score == 1:
            if len(best) > 1:
                best.sort(key=lambda c: (-counts.get(c[1], 0), c[2]))
            return best[0][1]
        if override_value:
            return override_value
        generic_tops = {_genre_top(c[1]) for c in best}
        for top in sorted(generic_tops):
            profile_genre = _pick_profile_genre(counts, top)
            if profile_genre:
                return profile_genre
        return best[0][1]
    if override_value:
        return override_value
    profile_genre = _pick_profile_genre(counts)
    if profile_genre:
        return profile_genre
    return "Other-Uncategorized"


def clean_title(title, artist):
    value = normalize_space(title)
    if not value:
        return value
    artist = normalize_space(artist)
    if artist:
        prefix = re.compile(r"^[\s\-–—:]*" + re.escape(artist) + r"\s*[-–—:]\s*", re.IGNORECASE)
        value = prefix.sub("", value)
        if value.casefold().startswith(artist.casefold()):
            rest = value[len(artist):]
            extra = re.match(
                r"^\s*(?:&|,|x|×|feat\.?|ft\.?|with)\s+.{1,80}?[-–—:]\s*",
                rest,
                re.IGNORECASE,
            )
            if extra:
                value = rest[extra.end():]
    changed = True
    while changed and value:
        changed = False
        for pattern in VIDEO_SUFFIXES:
            new = pattern.sub("", value).strip()
            if new and new != value:
                value = new
                changed = True
                break
    return value.strip(" -–—:") or normalize_space(title)


def canonical_albumartist(albumartist, artist, album):
    value = normalize_space(albumartist)
    if not value:
        value = normalize_space(artist)
    value = ALBUMARTIST_ALIASES.get(value, value)
    value = re.sub(r"\s+feat(?:uring)?\.?\s+.*$", "", value, flags=re.IGNORECASE).strip()
    return value or normalize_space(artist)


def canonical_album(album, artist, title):
    value = normalize_space(album)
    if not value or value.casefold() == "<untagged>":
        return None
    if value.casefold() in PLAYLIST_ALBUMS:
        return None
    if MONTH_ALBUM.match(value):
        return None
    if value.startswith(("http://", "https://")):
        return None
    return value


def clean_number(value):
    text = normalize_space(str(value or ""))
    if not text or text in {"0", "0/0", "00/00"}:
        return ""
    first = text.split("/", 1)[0].strip()
    if first.isdigit():
        number = int(first)
        return str(number) if number > 0 else ""
    return text


def recording_key(track):
    artist = normalize_space(track.get("artist", "")).casefold()
    title = clean_title(track.get("title", ""), track.get("artist", "")).casefold()
    title = re.sub(r"^\d+\s*[-.]\s*", "", title)
    duration = float(track.get("duration") or 0)
    return artist, title, int(round(duration / 3.0))


def choose_keeper(records):
    def key(record):
        return (
            int(record.get("bitrate") or 0),
            int(record.get("size") or 0),
            1 if record.get("date") else 0,
            -len(str(record.get("path", ""))),
        )
    return max(records, key=key)


MERGE_TARGETS = {
    "Pop-Acoustic": "Pop-Indie",
    "Pop-R&B": "R&B/Soul-Contemporary",
    "Hip-Hop/Rap-Emo": "Hip-Hop/Rap-Trap",
    "Hip-Hop/Rap-Drill": "Hip-Hop/Rap-Trap",
    "Hip-Hop/Rap-Conscious": "Hip-Hop/Rap-Pop Rap",
    "Rock-Indie": "Rock-Alternative",
    "Rock-Pop": "Rock-Alternative",
    "Electronic-Hardstyle": "Electronic-House",
    "Electronic-Chill": "Electronic-House",
    "Folk-Indie": "Pop-Acoustic",
    "Jazz-Standard": "Pop-Soul",
    "Other-Uncategorized": "Pop-Dance",
}

KEEP_SMALL_POOLS = {"Country-Pop", "Jazz-Standard", "Folk-Indie"}


def merge_small_pools(planned, minimum=5):
    counts = {}
    for item in planned:
        genre = item["genre"] if "genre" in item else item["after"]["genre"]
        counts[genre] = counts.get(genre, 0) + 1
    mapping = {}
    changed = True
    while changed:
        changed = False
        for genre, count in list(counts.items()):
            if count >= minimum or genre in mapping or genre in KEEP_SMALL_POOLS:
                continue
            target = MERGE_TARGETS.get(genre)
            if not target or target == genre:
                same_top = [g for g in counts if _genre_top(g) == _genre_top(genre) and g != genre]
                target = max(same_top, key=lambda g: counts[g]) if same_top else "Other-Uncategorized"
            while target in mapping:
                target = mapping[target]
            mapping[genre] = target
            counts[target] = counts.get(target, 0) + count
            del counts[genre]
            changed = True
    return mapping


def read_tags(path):
    audio = mutagen.File(str(path), easy=True)
    tags = audio.tags or {}
    result = {}
    for field in TAG_FIELDS:
        value = tags.get(field)
        if value is None:
            result[field] = ""
        elif isinstance(value, (list, tuple)):
            result[field] = "; ".join(str(v) for v in value)
        else:
            result[field] = str(value)
    return result


def mp3_audio_sha256(path):
    data = Path(path).read_bytes()
    start = 0
    if data[:3] == b"ID3" and len(data) >= 10:
        size = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F)
        start = 10 + size
    end = len(data)
    if end - start >= 128 and data[-128:-125] == b"TAG":
        end -= 128
    return hashlib.sha256(data[start:end]).hexdigest()


def track_record(path, root, deploy=None):
    path = Path(path)
    audio = mutagen.File(str(path), easy=True)
    if audio is None:
        return None
    tags = read_tags(path)
    info = audio.info
    duration = float(getattr(info, "length", 0) or 0)
    bitrate = int(getattr(info, "bitrate", 0) or 0)
    if not bitrate and duration:
        bitrate = int(path.stat().st_size * 8 / duration)
    record = {
        "root": root,
        "path": str(path),
        "deploy": str(deploy or path),
        "relative": str(path.relative_to(Path(root))),
        "duration": round(duration, 3),
        "bitrate": bitrate,
        "size": path.stat().st_size,
        "format": path.suffix.lower(),
        "audio_sha256": mp3_audio_sha256(path) if path.suffix.lower() == ".mp3" else "",
    }
    record.update(tags)
    return record


def inventory(config):
    roots = config["roots"]
    records = []
    for label, root_config in roots.items():
        if isinstance(root_config, str):
            root_path = Path(root_config)
            deploy_root = root_config
        else:
            root_path = Path(root_config["path"])
            deploy_root = root_config.get("deploy", root_config["path"])
        if not root_path.exists():
            continue
        for path in root_path.rglob("*"):
            if path.suffix.lower() not in AUDIO_EXTENSIONS:
                continue
            relative = path.relative_to(root_path)
            deploy = Path(deploy_root) / relative
            record = track_record(path, str(root_path), deploy)
            if record:
                record["root_label"] = label
                records.append(record)
    return records


def fetch_profiles(records, cache_path, limit=None):
    cache = {}
    if Path(cache_path).exists():
        cache = json.loads(Path(cache_path).read_text(encoding="utf-8"))
    artists = sorted({normalize_space(r["artist"]) for r in records if r.get("artist")})
    if limit:
        artists = artists[:limit]
    headers = {"User-Agent": "RockboxTagRepair/1.0 (https://github.com/evchuk4018/rockbox)"}
    for index, artist in enumerate(artists, 1):
        if artist in cache:
            continue
        query = urllib.parse.quote(f'artist:"{artist}"')
        url = f"https://musicbrainz.org/ws/2/artist/?query={query}&fmt=json&limit=5&inc=genres+tags"
        terms = []
        mbid = None
        try:
            request = urllib.request.Request(url, headers=headers)
            with urllib.request.urlopen(request, timeout=30) as response:
                data = json.load(response)
            candidates = data.get("artists") or []
            best = None
            for candidate in candidates:
                name = normalize_space(candidate.get("name", "")).casefold()
                score = int(candidate.get("score", 0))
                if name == artist.casefold() or score >= 95:
                    best = candidate
                    break
            if best is None and candidates:
                best = candidates[0]
            if best:
                mbid = best.get("id")
                terms.extend(g.get("name", "") for g in best.get("genres") or [])
                terms.extend(t.get("name", "") for t in best.get("tags") or [])
                if mbid and not best.get("genres"):
                    detail_url = f"https://musicbrainz.org/ws/2/artist/{mbid}?fmt=json&inc=genres+tags"
                    request = urllib.request.Request(detail_url, headers=headers)
                    with urllib.request.urlopen(request, timeout=30) as response:
                        detail = json.load(response)
                    terms.extend(g.get("name", "") for g in detail.get("genres") or [])
                    terms.extend(t.get("name", "") for t in detail.get("tags") or [])
        except Exception as exc:
            sys.stderr.write(f"profile {artist}: {exc}\n")
        cache[artist] = {"mbid": mbid, "terms": sorted(set(t for t in terms if t))}
        if index < len(artists):
            time.sleep(1.1)
        if index % 25 == 0:
            Path(cache_path).write_text(json.dumps(cache, indent=1, ensure_ascii=False), encoding="utf-8")
            sys.stderr.write(f"profiles {index}/{len(artists)}\n")
    Path(cache_path).write_text(json.dumps(cache, indent=1, ensure_ascii=False), encoding="utf-8")
    return cache


def load_verified_maps(review_path):
    result = {}
    if not review_path or not Path(review_path).exists():
        return result
    data = json.loads(Path(review_path).read_text(encoding="utf-8"))
    for key, value in data.items():
        if value.get("status") != "verified":
            continue
        for path in value.get("paths") or []:
            result[str(Path(path)).casefold()] = value
    return result


def local_profiles(records):
    collected = {}
    for record in records:
        artist = normalize_space(record.get("artist", "")).casefold()
        if not artist:
            continue
        collected.setdefault(artist, []).extend(genre_tokens(record.get("genre", "")))
    return {
        artist: {"source": "local", "terms": sorted(set(terms))}
        for artist, terms in collected.items()
    }


def tag_value(tags, key):
    value = (tags or {}).get(key, [])
    if isinstance(value, (list, tuple)):
        return normalize_space(value[0]) if value else ""
    return normalize_space(value)


def unify_albumartists(groups_out):
    albums = {}
    for group in groups_out:
        album = group["after"]["album"].casefold()
        if album:
            albums.setdefault(album, []).append(group)
    chosen = {}
    for album, groups in albums.items():
        artists = sorted({g["after"]["albumartist"] for g in groups if g["after"]["albumartist"]}, key=len)
        if len(artists) < 2:
            continue
        for candidate in artists:
            if all(other.casefold().startswith(candidate.casefold()) for other in artists):
                chosen[album] = candidate
                for group in groups:
                    group["after"]["albumartist"] = candidate
                break
    return chosen


def build_plan(records, profiles, overrides, verified_paths):
    profile_map = {normalize_space(key).casefold(): value for key, value in profiles.items()}
    groups = {}
    for record in records:
        groups.setdefault(recording_key(record), []).append(record)
    planned = []
    groups_out = []
    for key, members in groups.items():
        player_members = [m for m in members if m.get("root_label") == "player"]
        verified = None
        for member in members:
            candidate = verified_paths.get(str(Path(member["path"])).casefold())
            if candidate:
                verified = candidate
                break
        source = None
        if verified:
            source = {
                "artist": verified.get("artist", ""),
                "title": verified.get("title", ""),
                "album": tag_value(verified.get("tags"), "album"),
                "albumartist": tag_value(verified.get("tags"), "albumartist"),
                "tracknumber": tag_value(verified.get("tags"), "tracknumber"),
                "discnumber": tag_value(verified.get("tags"), "discnumber"),
                "date": "",
            }
        elif player_members:
            source = choose_keeper(player_members)
        else:
            source = choose_keeper(members)
        current_genres = sorted({g for m in members for g in genre_tokens(m.get("genre"))})
        profile_entry = profile_map.get(normalize_space(source.get("artist", "")).casefold(), {})
        profile = profile_entry.get("terms", []) if isinstance(profile_entry, dict) else profile_entry
        genre = classify_genre(
            source.get("artist", ""),
            source.get("title", ""),
            source.get("album", ""),
            current_genres,
            profile,
            overrides,
        )
        artist = normalize_space(source.get("artist", ""))
        title = clean_title(source.get("title", ""), artist)
        albumartist = canonical_albumartist(source.get("albumartist", ""), artist, source.get("album", ""))
        album = canonical_album(source.get("album", ""), artist, title)
        if not album:
            for member in members:
                candidate = canonical_album(member.get("album", ""), member.get("artist", ""), member.get("title", ""))
                if candidate:
                    album = candidate
                    break
        if not album:
            for (override_artist, fragment), override_album in TITLE_ALBUMS.items():
                if artist.casefold() == override_artist and fragment in title.casefold():
                    album = override_album
                    break
        track = clean_number(source.get("tracknumber", ""))
        disc = clean_number(source.get("discnumber", ""))
        date = normalize_space(source.get("date", ""))
        after = {
            "artist": artist,
            "title": title,
            "albumartist": albumartist,
            "album": album or "",
            "genre": genre,
            "tracknumber": track,
            "discnumber": disc,
            "date": date,
        }
        group = {
            "key": list(key),
            "paths": [m["path"] for m in members],
            "player_paths": [m["path"] for m in player_members],
            "source": source.get("path"),
            "after": after,
        }
        groups_out.append(group)
        for member in members:
            item_after = dict(after)
            if not item_after["date"]:
                item_after["date"] = normalize_space(member.get("date", ""))
            planned.append({
                "path": member["path"],
                "deploy": member["deploy"],
                "root": member["root"],
                "root_label": member["root_label"],
                "before": {field: member.get(field, "") for field in TAG_FIELDS},
                "after": item_after,
                "duration": member.get("duration", 0),
                "bitrate": member.get("bitrate", 0),
                "size": member.get("size", 0),
                "format": member.get("format", ""),
                "audio_sha256": member.get("audio_sha256", ""),
            })
    chosen_albumartists = unify_albumartists(groups_out)
    for item in planned:
        album_key = item["after"]["album"].casefold()
        if album_key in chosen_albumartists:
            item["after"]["albumartist"] = chosen_albumartists[album_key]
    return planned, groups_out


def merge_plan_genres(planned, minimum=5):
    player = [p for p in planned if p["root_label"] == "player"]
    mapping = merge_small_pools(player, minimum)
    if not mapping:
        return {}
    for item in planned:
        item["genre_before_merge"] = item["after"]["genre"]
        item["after"]["genre"] = mapping.get(item["after"]["genre"], item["after"]["genre"])
    return mapping


def tag_signature(tags):
    return tuple(normalize_space(str(tags.get(field, ""))) for field in TAG_FIELDS)


def plan_changes(planned):
    changes = []
    for item in planned:
        if tag_signature(item["before"]) != tag_signature(item["after"]):
            changes.append(item)
    return changes


def write_tags(path, after):
    path = Path(path)
    audio = mutagen.File(str(path))
    if path.suffix.lower() == ".mp3":
        tags = ID3(str(path))
        version = tags.version[1] if tags.version else 3
        def set_text(frame, value):
            if value:
                tags.setall(frame, [globals()[frame](encoding=Encoding.UTF8, text=[value])])
            elif frame in tags:
                tags.delall(frame)
        set_text("TIT2", after.get("title"))
        set_text("TPE1", after.get("artist"))
        set_text("TPE2", after.get("albumartist"))
        set_text("TALB", after.get("album"))
        set_text("TCON", after.get("genre"))
        set_text("TRCK", after.get("tracknumber"))
        set_text("TPOS", after.get("discnumber"))
        if after.get("date"):
            set_text("TDRC", after["date"])
        tags.save(str(path), v2_version=version)
        return
    if path.suffix.lower() == ".opus" and isinstance(audio, OggOpus):
        for key, field in (
            ("title", "title"),
            ("artist", "artist"),
            ("albumartist", "albumartist"),
            ("album", "album"),
            ("genre", "genre"),
            ("tracknumber", "tracknumber"),
            ("discnumber", "discnumber"),
        ):
            value = after.get(field)
            if value:
                audio[key] = [value]
            elif key in audio:
                del audio[key]
        if after.get("date"):
            audio["date"] = [after["date"]]
        audio.save()
        return
    easy = mutagen.File(str(path), easy=True)
    for field in TAG_FIELDS:
        value = after.get(field)
        if value:
            easy[field] = [value]
        elif field in easy:
            del easy[field]
    easy.save()


def backup_file(path, backup_dir):
    path = Path(path)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    target = Path(backup_dir) / f"{digest}{path.suffix.lower()}"
    if not target.exists():
        shutil.copy2(path, target)
    return target


def apply_plan(plan_path, backup_dir, dry_run=False, only_roots=None):
    plan = json.loads(Path(plan_path).read_text(encoding="utf-8"))
    planned = plan["planned"]
    applied = []
    skipped = []
    for item in planned:
        if only_roots and item["root_label"] not in only_roots:
            continue
        path = Path(item["deploy"])
        if not path.exists():
            skipped.append(item["deploy"])
            continue
        current = read_tags(path)
        if tag_signature(current) == tag_signature(item["after"]):
            continue
        if tag_signature(current) != tag_signature(item["before"]):
            raise RuntimeError(f"tags changed since plan: {path}")
        before_audio = mp3_audio_sha256(path) if path.suffix.lower() == ".mp3" else ""
        if dry_run:
            applied.append({"path": str(path), "before": current, "after": item["after"], "dry_run": True})
            continue
        if item["root_label"] != "player":
            backup_file(path, backup_dir)
        write_tags(path, item["after"])
        after_read = read_tags(path)
        if tag_signature(after_read) != tag_signature(item["after"]):
            raise RuntimeError(f"tag write verification failed: {path}")
        after_audio = mp3_audio_sha256(path) if path.suffix.lower() == ".mp3" else ""
        if before_audio and before_audio != after_audio:
            raise RuntimeError(f"audio changed: {path}")
        applied.append({"path": str(path), "before": current, "after": after_read})
    report = {"applied": applied, "count": len(applied), "skipped": skipped, "dry_run": dry_run}
    return report


def duplicate_key(track):
    artist = normalize_space(track.get("artist", "")).casefold()
    title = clean_title(track.get("title", ""), track.get("artist", "")).casefold()
    title = re.sub(r"^\d+\s*[-.]\s*", "", title)
    return artist, title


def virtual_path(path):
    return "/<microSD1>/" + Path(path).relative_to(Path("E:/")).as_posix()


def playlist_target(entry, playlist):
    entry = entry.strip()
    if entry.startswith("/<microSD1>/"):
        return Path("E:/") / entry[len("/<microSD1>/"):]
    target = Path(entry)
    if not target.is_absolute():
        target = playlist.parent / target
    return Path(os.path.normpath(str(target)))


def format_playlist_entry(entry, keeper, playlist):
    if entry.strip().startswith("/<microSD1>/"):
        return virtual_path(keeper)
    relative = os.path.relpath(str(keeper), str(playlist.parent)).replace("\\", "/")
    return relative


def rewrite_playlists(playlist_roots, removed_map):
    changed = []
    missing = []
    for root in playlist_roots:
        root = Path(root)
        if not root.exists():
            continue
        for playlist in root.rglob("*.m3u8"):
            original = playlist.read_text(encoding="utf-8-sig").splitlines()
            output = []
            seen = set()
            modified = False
            for line in original:
                stripped = line.strip()
                if not stripped or stripped.startswith("#"):
                    output.append(line)
                    continue
                target = playlist_target(stripped, playlist)
                key = str(target).casefold()
                if key in removed_map:
                    keeper = removed_map[key]
                    line = format_playlist_entry(stripped, keeper, playlist)
                    target = keeper
                    modified = True
                if not target.exists():
                    missing.append((str(playlist), stripped))
                line_key = str(target).casefold()
                if line_key in seen:
                    modified = True
                    continue
                seen.add(line_key)
                output.append(line)
            if modified:
                playlist.write_text("\n".join(output) + "\n", encoding="utf-8")
                changed.append(str(playlist))
    return changed, missing


def merge_home_scores(home_dir, removed_map):
    path = Path(home_dir) / "home-scores.tsv"
    if not path.exists():
        return 0
    entries = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        parts = line.split("\t")
        if len(parts) < 2:
            continue
        entries.append([parts[:-1], parts[-1]])
    removed_virtual = {}
    for old, new in removed_map.items():
        old_virtual = "/<microSD1>/" + Path(old).relative_to(Path("E:/")).as_posix()
        new_virtual = "/<microSD1>/" + Path(new).relative_to(Path("E:/")).as_posix()
        removed_virtual[old_virtual] = new_virtual
    merged = 0
    for old, new in removed_virtual.items():
        source = next((entry for entry in entries if entry[1].casefold() == old.casefold()), None)
        if not source:
            continue
        target = next((entry for entry in entries if entry[1].casefold() == new.casefold()), None)
        if target is None:
            source[1] = new
        else:
            width = max(len(source[0]), len(target[0]))
            values = []
            for index in range(width):
                a = int(source[0][index]) if index < len(source[0]) and source[0][index].lstrip("-").isdigit() else 0
                b = int(target[0][index]) if index < len(target[0]) and target[0][index].lstrip("-").isdigit() else 0
                values.append(str(max(a, b)))
            target[0] = values
            entries.remove(source)
        merged += 1
    path.write_text("\n".join("\t".join(entry[0] + [entry[1]]) for entry in entries) + "\n", encoding="utf-8")
    return merged


def dedupe_root(root, backup_dir):
    root = Path(root)
    records = []
    for path in root.rglob("*"):
        if path.suffix.lower() not in AUDIO_EXTENSIONS:
            continue
        record = track_record(path, str(root), path)
        if record:
            records.append(record)
    groups = {}
    for record in records:
        groups.setdefault(duplicate_key(record), []).append(record)
    removed = []
    for key, members in groups.items():
        if len(members) < 2:
            continue
        if max(m["duration"] for m in members) - min(m["duration"] for m in members) > 5.0:
            continue
        keeper = choose_keeper(members)
        for member in members:
            if member is keeper:
                continue
            source = Path(member["path"])
            target = Path(backup_dir) / root.parent.name / source.relative_to(root)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            source.unlink()
            removed.append({"path": str(source), "keeper": keeper["path"]})
    return {"root": str(root), "removed": removed}


def apply_duplicates(plan_path, backup_dir, playlist_roots, home_dir, stage_roots):
    plan = json.loads(Path(plan_path).read_text(encoding="utf-8"))
    removed_map = {}
    removed = []
    for group in plan["duplicates"]:
        keeper = Path(group["keeper_deploy"])
        for old in group["remove_deploy"]:
            old_path = Path(old)
            removed_map[str(old_path).casefold()] = keeper
            target = Path(backup_dir) / "player" / old_path.relative_to(Path("E:/"))
            target.parent.mkdir(parents=True, exist_ok=True)
            if old_path.exists():
                shutil.copy2(old_path, target)
                old_path.unlink()
            removed.append({"path": str(old_path), "keeper": str(keeper)})
            relative = old_path.relative_to(Path("E:/M"))
            for stage in stage_roots:
                stage = Path(stage)
                for candidate in (stage / relative, (stage / relative).with_suffix(".opus")):
                    if candidate.exists():
                        stage_target = Path(backup_dir) / stage.parent.name / relative
                        stage_target = stage_target.with_suffix(candidate.suffix)
                        stage_target.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(candidate, stage_target)
                        candidate.unlink()
    changed, missing = rewrite_playlists(playlist_roots, removed_map)
    merged = merge_home_scores(home_dir, removed_map)
    for name in (
        "home-autoplay-pool-a",
        "home-autoplay-pool-b",
        "home-autoplay-a.m3u8",
        "home-autoplay-b.m3u8",
        "home-autoplay-source.tmp",
    ):
        target = Path(home_dir) / name
        if target.exists():
            target.unlink()
    return {
        "removed": removed,
        "playlist_files_changed": changed,
        "playlist_missing_entries": missing,
        "home_scores_merged": merged,
    }


def build_duplicates(records):
    groups = {}
    for record in records:
        if record.get("root_label") != "player":
            continue
        groups.setdefault(duplicate_key(record), []).append(record)
    duplicates = []
    for key, members in groups.items():
        if len(members) < 2:
            continue
        durations = [m["duration"] for m in members]
        if max(durations) - min(durations) > 5.0:
            continue
        keeper = choose_keeper(members)
        remove = [m for m in members if m is not keeper]
        duplicates.append({
            "key": list(key),
            "keeper": keeper["path"],
            "keeper_deploy": keeper["deploy"],
            "remove": [m["path"] for m in remove],
            "remove_deploy": [m["deploy"] for m in remove],
            "records": members,
        })
    return duplicates


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    inventory_parser = sub.add_parser("inventory")
    inventory_parser.add_argument("--config", required=True)
    inventory_parser.add_argument("--out", required=True)
    profiles_parser = sub.add_parser("profiles")
    profiles_parser.add_argument("--inventory", required=True)
    profiles_parser.add_argument("--out", required=True)
    profiles_parser.add_argument("--limit", type=int)
    local_parser = sub.add_parser("local-profiles")
    local_parser.add_argument("--inventory", required=True)
    local_parser.add_argument("--out", required=True)
    plan_parser = sub.add_parser("plan")
    plan_parser.add_argument("--inventory", required=True)
    plan_parser.add_argument("--profiles", required=True)
    plan_parser.add_argument("--review", required=True)
    plan_parser.add_argument("--overrides")
    plan_parser.add_argument("--out", required=True)
    plan_parser.add_argument("--minimum", type=int, default=5)
    apply_parser = sub.add_parser("apply")
    apply_parser.add_argument("--plan", required=True)
    apply_parser.add_argument("--backup-dir", required=True)
    apply_parser.add_argument("--only-roots")
    apply_parser.add_argument("--dry-run", action="store_true")
    apply_parser.add_argument("--report")
    dedupe_parser = sub.add_parser("dedupe")
    dedupe_parser.add_argument("--inventory", required=True)
    dedupe_parser.add_argument("--out", required=True)
    dedupe_apply_parser = sub.add_parser("dedupe-apply")
    dedupe_apply_parser.add_argument("--plan", required=True)
    dedupe_apply_parser.add_argument("--backup-dir", required=True)
    dedupe_apply_parser.add_argument("--playlists", required=True)
    dedupe_apply_parser.add_argument("--home", required=True)
    dedupe_apply_parser.add_argument("--stages")
    dedupe_apply_parser.add_argument("--report")
    dedupe_root_parser = sub.add_parser("dedupe-root")
    dedupe_root_parser.add_argument("--root", required=True)
    dedupe_root_parser.add_argument("--backup-dir", required=True)
    dedupe_root_parser.add_argument("--report")
    validate_parser = sub.add_parser("validate")
    validate_parser.add_argument("--plan", required=True)
    validate_parser.add_argument("--duplicates")
    args = parser.parse_args()

    if args.command == "inventory":
        config = json.loads(Path(args.config).read_text(encoding="utf-8"))
        records = inventory(config)
        Path(args.out).write_text(json.dumps(records, indent=1, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({"records": len(records)}, indent=2))
        return
    if args.command == "profiles":
        records = json.loads(Path(args.inventory).read_text(encoding="utf-8"))
        cache = fetch_profiles(records, args.out, args.limit)
        print(json.dumps({"artists": len(cache)}, indent=2))
        return
    if args.command == "local-profiles":
        records = json.loads(Path(args.inventory).read_text(encoding="utf-8"))
        profiles = local_profiles(records)
        Path(args.out).write_text(json.dumps(profiles, indent=1, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({"artists": len(profiles)}, indent=2))
        return
    if args.command == "plan":
        records = json.loads(Path(args.inventory).read_text(encoding="utf-8"))
        profiles = json.loads(Path(args.profiles).read_text(encoding="utf-8"))
        overrides = json.loads(Path(args.overrides).read_text(encoding="utf-8")) if args.overrides else {}
        verified = load_verified_maps(args.review)
        planned, groups = build_plan(records, profiles, overrides, verified)
        mapping = merge_plan_genres(planned, args.minimum)
        duplicates = build_duplicates(records)
        changes = plan_changes(planned)
        document = {
            "planned": planned,
            "groups": groups,
            "duplicates": duplicates,
            "genre_merge": mapping,
            "change_count": len(changes),
            "player_change_count": sum(1 for c in changes if c["root_label"] == "player"),
        }
        Path(args.out).write_text(json.dumps(document, indent=1, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({"groups": len(groups), "changes": len(changes), "duplicates": len(duplicates), "genre_merge": mapping}, indent=2, ensure_ascii=False))
        return
    if args.command == "apply":
        only_roots = set(args.only_roots.split(",")) if args.only_roots else None
        report = apply_plan(args.plan, args.backup_dir, args.dry_run, only_roots)
        if args.report:
            Path(args.report).write_text(json.dumps(report, indent=1, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({"count": report["count"], "skipped": len(report["skipped"]), "dry_run": report["dry_run"]}, indent=2))
        return
    if args.command == "dedupe":
        records = json.loads(Path(args.inventory).read_text(encoding="utf-8"))
        duplicates = build_duplicates(records)
        Path(args.out).write_text(json.dumps(duplicates, indent=1, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({"duplicate_groups": len(duplicates), "remove_count": sum(len(d["remove"]) for d in duplicates)}, indent=2))
        return
    if args.command == "dedupe-apply":
        stage_roots = args.stages.split(",") if args.stages else []
        report = apply_duplicates(
            args.plan,
            args.backup_dir,
            args.playlists.split(","),
            args.home,
            stage_roots,
        )
        if args.report:
            Path(args.report).write_text(json.dumps(report, indent=1, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({
            "removed": len(report["removed"]),
            "playlist_files_changed": len(report["playlist_files_changed"]),
            "playlist_missing_entries": len(report["playlist_missing_entries"]),
            "home_scores_merged": report["home_scores_merged"],
        }, indent=2))
        return
    if args.command == "dedupe-root":
        report = dedupe_root(args.root, args.backup_dir)
        if args.report:
            Path(args.report).write_text(json.dumps(report, indent=1, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({"root": report["root"], "removed": len(report["removed"])}, indent=2))
        return
    if args.command == "validate":
        plan = json.loads(Path(args.plan).read_text(encoding="utf-8"))
        errors = []
        for item in plan["planned"]:
            genre = item["after"]["genre"]
            if not is_allowed(genre):
                errors.append(f"invalid genre {genre} for {item['path']}")
            if item["after"]["genre"] != item.get("genre_before_merge", genre) and not any(
                item["after"]["genre"] == value for value in plan.get("genre_merge", {}).values()
            ):
                errors.append(f"unexpected merge for {item['path']}")
        player = [p for p in plan["planned"] if p["root_label"] == "player"]
        counts = {}
        for item in player:
            counts[item["after"]["genre"]] = counts.get(item["after"]["genre"], 0) + 1
        for genre, count in counts.items():
            if count < 5 and genre not in {"Country-Pop", "Jazz-Standard", "Folk-Indie"}:
                errors.append(f"small pool {genre}: {count}")
        taylor = {p["after"]["genre"] for p in player if p["after"]["artist"] == "Taylor Swift"}
        buttercup = {p["after"]["genre"] for p in player if "Buttercup" in p["after"]["title"]}
        if taylor & buttercup:
            errors.append(f"Taylor Swift shares a genre with Buttercup: {taylor & buttercup}")
        if args.duplicates:
            duplicates = json.loads(Path(args.duplicates).read_text(encoding="utf-8"))
            for group in duplicates:
                if len(group["remove"]) and max(r["duration"] for r in group["records"]) - min(r["duration"] for r in group["records"]) <= 3.0:
                    pass
        print(json.dumps({"errors": errors, "player_genres": counts}, indent=2, ensure_ascii=False))
        if errors:
            raise SystemExit(1)


if __name__ == "__main__":
    main()
