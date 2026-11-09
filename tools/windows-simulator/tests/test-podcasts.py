"""Native tests for the Clip managed-podcast firmware module."""
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
GCC = shutil.which("gcc") or shutil.which("clang")


class PodcastTests(unittest.TestCase):
    def test_catalog_resume_playback_scan_and_events(self):
        self.assertIsNotNone(GCC, "A host C compiler is required")
        with tempfile.TemporaryDirectory(prefix="rockbox-podcasts-") as directory:
            work = Path(directory)
            executable = work / "podcast-test.exe"
            subprocess.run([
                GCC, "-std=gnu11", "-Wall", "-Wextra", "-Werror",
                "-DPODCAST_TEST", "-I", str(ROOT / "apps"),
                str(ROOT / "tools/windows-simulator/tests/podcast-harness.c"),
                str(ROOT / "apps/podcasts.c"), "-o", str(executable),
            ], check=True)
            subprocess.run([str(executable)], cwd=work, check=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
