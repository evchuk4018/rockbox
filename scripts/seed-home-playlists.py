"""Create non-destructive Home UI test playlists; audio is never copied or edited."""
import argparse
import random
from pathlib import Path


def seed_playlists(simdisk: Path) -> dict[str, int]:
    library = simdisk / "MusicLibrary"
    tracks = sorted(
        "/" + path.relative_to(simdisk).as_posix()
        for path in library.rglob("*")
        if path.is_file() and path.suffix.lower() == ".mp3"
    )
    if not tracks:
        raise ValueError(f"No MP3 files found in {library}")
    playlists = simdisk / "Playlists"
    playlists.mkdir(parents=True, exist_ok=True)
    shuffled = tracks.copy()
    random.Random(42).shuffle(shuffled)
    contents = {"Liked Songs": tracks}
    contents.update({f"Test Mix {i + 1}": shuffled[i::5] for i in range(5)})
    created = {}
    for name, entries in contents.items():
        try:
            with (playlists / f"{name}.m3u8").open("x", encoding="utf-8", newline="\n") as output:
                output.write("#EXTM3U\n" + "\n".join(entries) + "\n")
            created[name] = len(entries)
        except FileExistsError:
            pass
    settings = simdisk / ".rockbox"
    settings.mkdir(parents=True, exist_ok=True)
    try:
        with (settings / "home-pins.txt").open("x", encoding="utf-8", newline="\n") as output:
            output.write("/Playlists/Test Mix 1.m3u8\n/Playlists/Test Mix 2.m3u8\n")
    except FileExistsError:
        pass
    return created


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--simdisk", type=Path, default=Path(__file__).resolve().parents[1] / "build-sansaclip-sim" / "simdisk")
    args = parser.parse_args()
    for name, count in seed_playlists(args.simdisk).items():
        print(f"Created {name}: {count} tracks")
    print("Existing playlists and pins preserved.")
