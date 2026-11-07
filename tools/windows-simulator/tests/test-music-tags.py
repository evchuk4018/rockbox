"""Verify real MP3/Opus retagging preserves encoded audio and unrelated tags."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

import imageio_ffmpeg
from mutagen import File

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location("review", ROOT / "scripts/review-music-library.py")
review = importlib.util.module_from_spec(spec)
spec.loader.exec_module(review)


class TagTests(unittest.TestCase):
    def test_mp3_and_opus_audio_and_unrelated_tags_survive(self):
        with tempfile.TemporaryDirectory(prefix="rockbox-tag-test-") as directory:
            root = Path(directory)
            for extension, codec in (("mp3", "libmp3lame"), ("opus", "libopus")):
                path = root / ("sample." + extension)
                subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-nostdin", "-v", "error", "-f", "lavfi",
                                "-i", "sine=frequency=220:duration=0.3", "-c:a", codec, str(path)], check=True)
                tags = File(path, easy=True)
                tags["title"] = ["Sample"]
                tags["artist"] = ["Artist"]
                tags["genre"] = ["Keep this genre"]
                tags.save()
            rows = review.inventory([root])
            digest = {r["path"]: review.audio_hash(r["path"]) for r in rows}
            desired = {"artist": ["Artist feat. Guest"], "albumartist": ["Artist"], "album": ["Original Album"],
                       "discnumber": ["1"], "tracknumber": ["2"]}
            corrections = {"artist|sample": {"status": "verified", "duration_ms": rows[0]["duration_ms"],
                                             "tags": desired, "sources": ["test fixture"]}}
            review.apply(rows, corrections, root / "work")
            journal = json.loads((root / "work/applied.json").read_text(encoding="utf-8"))
            self.assertEqual(len(journal), 2)
            for row in rows:
                self.assertEqual(review.audio_hash(row["path"]), digest[row["path"]])
                tags = File(row["path"], easy=True)
                self.assertEqual(tags["genre"], ["Keep this genre"])
                self.assertEqual(tags["title"], ["Sample"])
                self.assertEqual(tags["album"], ["Original Album"])
            # A repeated apply is a no-op, with no additional backups.
            review.apply(rows, corrections, root / "work")
            self.assertEqual(len(list((root / "work/originals").iterdir())), 2)

    def test_recording_version_and_duration_are_required(self):
        row = {"title": "Song", "artist": "Artist", "duration_ms": 200000}
        recording = {"id": "r", "title": "Song", "score": 100, "length": 200000,
                     "artist-credit": [{"artist": {"name": "Artist"}}],
                     "releases": [{"id": "a", "title": "Album", "date": "2000", "status": "Official",
                                   "artist-credit": [{"artist": {"name": "Artist"}}],
                                   "release-group": {"primary-type": "Album"}}]}
        self.assertEqual(len(review.candidates({"recordings": [recording]}, row)), 1)
        recording["disambiguation"] = "live"
        self.assertEqual(review.candidates({"recordings": [recording]}, row), [])
        recording.pop("disambiguation")
        recording["length"] = 220000
        self.assertEqual(review.candidates({"recordings": [recording]}, row), [])
        self.assertEqual(review.primary("Tyler, The Creator"), "Tyler, The Creator")


if __name__ == "__main__":
    unittest.main(verbosity=2)
