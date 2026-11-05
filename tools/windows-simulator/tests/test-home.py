"""Exercise production Home data code and playlist progression on the native host."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("seed_home", ROOT / "scripts/seed-home-playlists.py")
SEED = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SEED)


def function(source, signature):
    """Compile exact production functions, with host adapters for Rockbox services."""
    start = source.index(signature)
    brace = source.index("{", start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


class HomeTests(unittest.TestCase):
    def test_seed_preserves_existing_files_and_unicode(self):
        with tempfile.TemporaryDirectory() as directory:
            disk = Path(directory)
            library = disk / "MusicLibrary" / "Beyoncé"
            library.mkdir(parents=True)
            for i in range(13):
                (library / f"歌曲 {i}.mp3").write_bytes(b"unchanged audio")
            created = SEED.seed_playlists(disk)
            self.assertEqual(created["Liked Songs"], 13)
            liked = (disk / "Playlists/Liked Songs.m3u8").read_text(encoding="utf-8").splitlines()[1:]
            self.assertEqual(len(set(liked)), 13)
            self.assertIn("/MusicLibrary/Beyoncé/歌曲 0.mp3", liked)
            mixes = []
            for i in range(1, 6):
                mixes.extend((disk / f"Playlists/Test Mix {i}.m3u8").read_text(encoding="utf-8").splitlines()[1:])
            self.assertCountEqual(mixes, liked)
            pins = disk / ".rockbox/home-pins.txt"
            pins.write_text("/Playlists/My own.m3u8\n", encoding="utf-8")
            before = {p: p.read_bytes() for p in disk.rglob("*") if p.is_file()}
            self.assertEqual(SEED.seed_playlists(disk), {})
            self.assertEqual(before, {p: p.read_bytes() for p in before})

    def test_native_home_and_playback(self):
        bundled_gcc = Path("C:/msys64/ucrt64/bin/gcc.exe")
        gcc = str(bundled_gcc) if bundled_gcc.is_file() else shutil.which("gcc")
        self.assertIsNotNone(gcc, "A native Windows GCC compiler is required")
        with tempfile.TemporaryDirectory(prefix="rockbox-home-") as directory:
            work = Path(directory)
            headers = {
                "config.h": "#pragma once\n#define SANSA_CLIP\n",
                "adapter.h": "#define __ACTION_H__\n#define MISC_H\n#include <action.h>\n#include <misc.h>\n",
                "rbpaths.h": '#pragma once\n#define ROCKBOX_DIR "."\n',
                "kernel.h": "#pragma once\n#define HZ 100\n",
                "action.h": "#pragma once\nenum { ACTION_NONE, ACTION_STD_PREV, ACTION_STD_PREVREPEAT, ACTION_STD_MENU, ACTION_WPS_MENU };\n",
                "misc.h": "#pragma once\nint read_line(int, char *, int);\n",
                "string-extra.h": "#pragma once\n#include <stddef.h>\nsize_t strlcpy(char *, const char *, size_t);\n",
                "file.h": """#pragma once
#include <fcntl.h>
#include <unistd.h>
#include <stdio.h>
#define MAX_PATH 260
int test_write(int, const void *, size_t);
int test_open(const char *, int, ...);
int test_sync(int);
int test_rename(const char *, const char *);
#define write test_write
#define open test_open
#define fsync test_sync
#define rename test_rename
""",
            }
            for name, text in headers.items():
                (work / name).write_text(text, encoding="utf-8")
            playlist = (ROOT / "apps/playlist.c").read_text(encoding="utf-8")
            misc = (ROOT / "apps/misc.c").read_text(encoding="utf-8")
            abrepeat = (ROOT / "apps/abrepeat.h").read_text(encoding="utf-8")
            extracted = [function(misc, "int read_line("), *(function(playlist, signature) for signature in (
                "static int get_next_index(", "bool playlist_check(", "int playlist_next(",
                "int playlist_get_repeat_mode(", "bool playlist_allow_dirplay(",
                "int update_playlist_flags_unlocked(", "int playlist_set_single_track(",
            ))]
            extracted.append(function(abrepeat[abrepeat.index('#else /*def AB_REPEAT_ENABLE*/'):],
                                      "static inline bool ab_repeat_mode_enabled("))
            harness = (Path(__file__).parent / "home-harness.c").read_text(encoding="utf-8")
            (work / "test.c").write_text(harness.replace("/* PRODUCTION_FUNCTIONS */", "\n\n".join(extracted)), encoding="utf-8")
            executable = work / "home-test.exe"
            env = os.environ.copy()
            env["PATH"] = str(Path(gcc).parent) + os.pathsep + env["PATH"]
            subprocess.run([gcc, "-std=c11", "-Wall", "-Wextra", "-Werror", "-include", str(work / "adapter.h"), "-I", str(work), "-I", str(ROOT / "apps"),
                            str(ROOT / "apps/home_data.c"), str(work / "test.c"), "-o", str(executable)], check=True, env=env)
            subprocess.run([str(executable)], cwd=work, check=True, env=env)


if __name__ == "__main__":
    unittest.main(verbosity=2)
