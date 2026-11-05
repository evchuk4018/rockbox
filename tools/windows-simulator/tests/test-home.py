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

    def test_clip_skin_parser(self):
        parser = ROOT / "lib/skin_parser"
        source = r"""#include <stdio.h>
#include <stdlib.h>
#include "skin_parser.h"
#include "skin_debug.h"
int main(int argc, char **argv) {
    if (argc != 2) return 2;
    FILE *f = fopen(argv[1], "rb");
    if (!f) return 2;
    fseek(f, 0, SEEK_END); long size = ftell(f); rewind(f);
    char *text = calloc(size + 1, 1);
    if (!text || fread(text, 1, size, f) != (size_t)size) return 2;
    fclose(f);
    struct skin_element *tree = skin_parse(text);
    if (!tree) { skin_error_format_message(); return 1; }
    skin_free_tree(tree); free(text); return 0;
}
"""
        with tempfile.TemporaryDirectory(prefix="rockbox-skin-") as directory:
            source_file = Path(directory) / "skin.c"
            source_file.write_text(source, encoding="utf-8")
            executable = Path(directory) / "skin.exe"
            subprocess.run(["C:/msys64/ucrt64/bin/gcc.exe", "-std=gnu11", "-D__PCTOOL__",
                            "-I", str(parser), str(source_file),
                            *(str(parser / name) for name in ("skin_parser.c", "skin_scan.c", "skin_debug.c", "skin_buffer.c", "tag_table.c")),
                            "-o", str(executable)], check=True)
            subprocess.run([str(executable), str(ROOT / "wps/cabbiev2.sansaclip.wps")], check=True)

    def test_clip_footer_layout(self):
        import re
        skin = (ROOT / "wps/cabbiev2.sansaclip.wps").read_text(encoding="utf-8")
        viewports = []
        for block in skin.split("%V(")[1:]:
            coordinates, content = block.split(")", 1)
            x, y, width, height, _ = map(int, coordinates.split(","))
            self.assertLessEqual(x + width, 128)
            self.assertLessEqual(y + height, 64)
            viewports.append((x, y, width, height, content))
        progress = next(v for v in viewports if "%pb(" in v[4])
        for viewport in viewports:
            if any(token in viewport[4] for token in ("%pr", "%xd(B", "%xd(D", "%xd(F", "%xd(H", "%?hG")):
                self.assertGreaterEqual(viewport[1], progress[1] + progress[3])
        footer = sorted((v for v in viewports if v[1] >= progress[1] + progress[3]), key=lambda v: v[0])
        for left, right in zip(footer, footer[1:]):
            self.assertLessEqual(left[0] + left[2], right[0])
        self.assertEqual(len(re.findall(r"%it(?![A-Za-z])", skin)), 1)
        self.assertNotIn("%id", skin)  # Singles often have identical album/title tags.
        self.assertNotIn("%Sx(Next:)", skin)
        self.assertNotIn("%?It", skin)

    def test_clip_button_events(self):
        import re
        keymap = (ROOT / "apps/keymaps/keymap-clip.c").read_text(encoding="utf-8")
        arrays = []
        for name in ("standard", "wps"):
            start = keymap.index(f"static const struct button_mapping button_context_{name}[]")
            end = keymap.index("};", start) + 2
            arrays.append(keymap[start:end])
        mappings = "\n".join(arrays)
        actions = sorted(set(re.findall(r"\bACTION_\w+", mappings)) - {"ACTION_NONE"})
        buttons = sorted(set(re.findall(r"\bBUTTON_\w+", mappings)) - {"BUTTON_NONE"})
        definitions = "\n".join(f"#define {name} (1u << {i})" for i, name in enumerate(buttons))
        worker = function((ROOT / "apps/action.c").read_text(encoding="utf-8"),
                          "static inline int action_code_worker(")
        code = """#include <assert.h>
#include <stdbool.h>
#include <stddef.h>
#define BUTTON_NONE 0
#define LAST_ITEM_IN_LIST {0, 0, 0}
#define HAS_BUTTON_HOLD
#define has_flag(value, flag) (((value) & (flag)) == (flag))
struct button_mapping { int action_code, button_code, pre_button_code; };
typedef struct { int button; } action_last_t;
typedef struct { int button; const struct button_mapping *items; bool is_prebutton; } action_cur_t;
""" + definitions + "\nenum { ACTION_NONE, ACTION_UNKNOWN, " + ", ".join(actions) + " };\n" + mappings + "\n" + worker + """
static int resolve(const struct button_mapping *map, int button, int last_button)
{
    action_last_t last = {.button = last_button};
    action_cur_t cur = {.button = button, .items = map};
    int end = 0;
    return action_code_worker(&last, &cur, &end);
}
int main(void)
{
    const struct button_mapping *wps = button_context_wps;
    const struct button_mapping *std = button_context_standard;
#ifdef SANSA_CLIP
    assert(resolve(wps, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT) == ACTION_WPS_PLAY);
    assert(resolve(wps, BUTTON_UP | BUTTON_REL, BUTTON_UP) == ACTION_WPS_TOGGLE_LIKE);
#else
    assert(resolve(wps, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT) == ACTION_WPS_BROWSE);
    assert(resolve(wps, BUTTON_UP | BUTTON_REL, BUTTON_UP) == ACTION_WPS_PLAY);
#endif
    assert(resolve(wps, BUTTON_UP, BUTTON_NONE) != ACTION_WPS_TOGGLE_LIKE);
    assert(resolve(wps, BUTTON_UP | BUTTON_REPEAT, BUTTON_UP) == ACTION_WPS_PITCHSCREEN);
    assert(resolve(wps, BUTTON_UP | BUTTON_REL, BUTTON_UP | BUTTON_REPEAT) != ACTION_WPS_TOGGLE_LIKE);
    assert(resolve(wps, BUTTON_SELECT | BUTTON_REPEAT, BUTTON_SELECT) == ACTION_WPS_CONTEXT);
    assert(resolve(wps, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT | BUTTON_REPEAT) != ACTION_WPS_PLAY);
    assert(resolve(std, BUTTON_UP, BUTTON_NONE) == ACTION_STD_PREV);
    assert(resolve(std, BUTTON_UP | BUTTON_REPEAT, BUTTON_UP) == ACTION_STD_PREVREPEAT);
    assert(resolve(std, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT) == ACTION_STD_OK);
    return 0;
}
"""
        gcc = "C:/msys64/ucrt64/bin/gcc.exe"
        with tempfile.TemporaryDirectory(prefix="rockbox-buttons-") as directory:
            source = Path(directory) / "buttons.c"
            source.write_text(code, encoding="utf-8")
            executable = Path(directory) / "buttons.exe"
            for clip in (True, False):
                subprocess.run([gcc, "-std=c11", "-Wall", "-Wextra", "-Werror",
                                *(["-DSANSA_CLIP"] if clip else []), str(source), "-o", str(executable)], check=True)
                subprocess.run([str(executable)], check=True)

    def test_native_home_and_playback(self):
        bundled_gcc = Path("C:/msys64/ucrt64/bin/gcc.exe")
        gcc = str(bundled_gcc) if bundled_gcc.is_file() else shutil.which("gcc")
        self.assertIsNotNone(gcc, "A native Windows GCC compiler is required")
        with tempfile.TemporaryDirectory(prefix="rockbox-home-") as directory:
            work = Path(directory)
            headers = {
                "config.h": "#pragma once\n#define SANSA_CLIP\n",
                "adapter.h": "#define __ACTION_H__\n#define MISC_H\n#define __PLAYLIST_H__\n#define __SETTINGS_H__\n#define _TAGCACHE_H\n#define _APPEVENTS_H\n#include <action.h>\n#include <misc.h>\n#include <playlist.h>\n#include <settings.h>\n#include <tagcache.h>\n#include <appevents.h>\n",
                "rbpaths.h": '#pragma once\n#define ROCKBOX_DIR "."\n',
                "kernel.h": "#pragma once\n#define HZ 100\n#define MIN(a,b) ((a)<(b)?(a):(b))\nstruct mutex { int unused; };\nvoid mutex_init(struct mutex *);\nvoid mutex_lock(struct mutex *);\nvoid mutex_unlock(struct mutex *);\nvoid yield(void);\n",
                "action.h": "#pragma once\nenum { ACTION_NONE, ACTION_STD_PREV, ACTION_STD_PREVREPEAT, ACTION_STD_MENU, ACTION_WPS_MENU };\n",
                "misc.h": "#pragma once\nint read_line(int, char *, int);\nint open_utf8(const char *, int);\n",
                "dir.h": "#pragma once\nint test_mkdir(const char *);\n#define mkdir test_mkdir\n",
                "string-extra.h": "#pragma once\n#include <stddef.h>\nsize_t strlcpy(char *, const char *, size_t);\n",
                "core_alloc.h": "#pragma once\n#include <stddef.h>\nint core_alloc(size_t);\nint core_free(int);\nvoid *core_get_data(int);\nvoid *core_get_data_pinned(int);\nvoid core_put_data_pinned(void *);\n",
                "fixedpoint.h": "#pragma once\nlong fp16_log(int);\n",
                "timefuncs.h": "#pragma once\n#include <time.h>\n#include <stdbool.h>\nstruct tm *get_time(void);\nbool valid_time(const struct tm *);\n",
                "metadata.h": "#pragma once\n#include <stdbool.h>\n#include <file.h>\nstruct mp3entry { char path[MAX_PATH]; char *title, *artist, *genre_string; unsigned long elapsed, length; };\nbool get_metadata(struct mp3entry *, int, const char *);\n",
                "audio.h": "#pragma once\n#include <metadata.h>\nenum { TEF_NONE, TEF_CURRENT=1, TEF_AUTO_SKIP=2, TEF_REWIND=4 };\nstruct track_event { unsigned int flags; struct mp3entry *id3; };\nvoid audio_stop(void);\n#define AUDIO_STATUS_PLAY 1\nint audio_status(void);\nvoid audio_flush_and_reload_tracks(void);\nstruct mp3entry *audio_current_track(void);\n",
                "appevents.h": "#pragma once\n#include <stdbool.h>\nenum { PLAYBACK_EVENT_TRACK_CHANGE, PLAYBACK_EVENT_TRACK_FINISH };\nbool add_event(unsigned short, void (*)(unsigned short, void *));\n",
                "lang.h": "#pragma once\n#include <stdint.h>\nenum { LANG_HOME_DATABASE, LANG_TAGCACHE_BUSY, LANG_FAILED, LANG_HOME_NO_OTHER_GENRE };\n#define ID2P(x) ((const char *)(intptr_t)(x))\n",
                "splash.h": "#pragma once\nvoid splash(int, const char *);\n",
                "settings.h": "#pragma once\n#include <stdbool.h>\nstruct test_settings { int repeat_mode, next_folder; bool playlist_shuffle; int max_files_in_playlist; };\nextern struct test_settings global_settings;\n",
                "playlist.h": """#pragma once
#include <stdbool.h>
#include <file.h>
#define PLAYLIST_FLAG_MODIFIED (1u << 0)
#define PLAYLIST_FLAG_DIRPLAY (1u << 1)
#define PLAYLIST_FLAG_SINGLE_TRACK (1u << 2)
#define PLAYLIST_FLAG_HOME_AUTOPLAY (1u << 3)
struct playlist_info {
    int amount, index, first_index, last_insert_pos, control_fd;
    unsigned int flags;
    unsigned long *indices;
    bool started;
    char filename[MAX_PATH];
};
struct playlist_track_info { char filename[MAX_PATH]; };
struct playlist_info *playlist_get_current(void);
char *playlist_get_name(const struct playlist_info *, char *, int);
int playlist_get_track_info(struct playlist_info *, int, struct playlist_track_info *);
int playlist_create(const char *, const char *);
int playlist_set_home_autoplay(void);
bool playlist_home_change_genre(void);
const char *playlist_peek(int, char *, size_t);
ssize_t playlist_format_track_path(char *, char *, int, const char *, size_t);
""",
                "tagcache.h": """#pragma once
#include <stdbool.h>
enum { tag_filename, tag_genre, clause_is };
struct tagcache_search_clause { int tag, type; char *str; };
struct tagcache_search { int index; const char *genre; };
bool tagcache_is_usable(void);
bool tagcache_search(struct tagcache_search *, int);
bool tagcache_search_add_clause(struct tagcache_search *, struct tagcache_search_clause *);
bool tagcache_get_next(struct tagcache_search *, char *, long);
void tagcache_search_finish(struct tagcache_search *);
""",
                "file.h": """#pragma once
#include <fcntl.h>
#include <unistd.h>
#include <stdio.h>
#define MAX_PATH 260
int test_write(int, const void *, size_t);
int test_open(const char *, int, ...);
int test_sync(int);
int test_rename(const char *, const char *);
int test_truncate(int, off_t);
bool file_exists(const char *);
#define write test_write
#define open test_open
#define fsync test_sync
#define rename test_rename
#define ftruncate test_truncate
""",
            }
            for name, text in headers.items():
                (work / name).write_text(text, encoding="utf-8")
            playlist = (ROOT / "apps/playlist.c").read_text(encoding="utf-8")
            misc = (ROOT / "apps/misc.c").read_text(encoding="utf-8")
            abrepeat = (ROOT / "apps/abrepeat.h").read_text(encoding="utf-8")
            extracted = [function(misc, "int read_line("), "#define open test_open\n" + function(misc, "int open_utf8(") + "\n#undef open", *(function(playlist, signature) for signature in (
                "static int get_next_index(", "bool playlist_check(", "int playlist_next(",
                "int playlist_get_repeat_mode(", "bool playlist_allow_dirplay(",
                "int update_playlist_flags_unlocked(", "int playlist_set_single_track(",
                "int playlist_set_home_autoplay(", "bool playlist_home_change_genre(",
                "const char* playlist_peek(", "ssize_t playlist_format_track_path(",
            ))]
            paths = (ROOT / "firmware/common/pathfuncs.c").read_text(encoding="utf-8")
            (work / "pathfuncs.h").write_text((ROOT / "firmware/export/pathfuncs.h").read_text(encoding="utf-8"), encoding="utf-8")
            extracted = [*(function(paths, signature) for signature in (
                "int path_strip_drive(", "void path_correct_separators(",
                "void path_remove_dot_segments (", "size_t path_append_ex(",
            )), *extracted]
            fixedpoint = (ROOT / "lib/fixedpoint/fixedpoint.c").read_text(encoding="utf-8")
            extracted.append(function(fixedpoint, "long fp16_log("))
            extracted.append(function(abrepeat[abrepeat.index('#else /*def AB_REPEAT_ENABLE*/'):],
                                      "static inline bool ab_repeat_mode_enabled("))
            harness = (Path(__file__).parent / "home-harness.c").read_text(encoding="utf-8")
            (work / "test.c").write_text(harness.replace("/* PRODUCTION_FUNCTIONS */", "\n\n".join(extracted)), encoding="utf-8")
            executable = work / "home-test.exe"
            env = os.environ.copy()
            env["PATH"] = str(Path(gcc).parent) + os.pathsep + env["PATH"]
            subprocess.run([gcc, "-std=c11", "-fwrapv", "-Wall", "-Wextra", "-Werror", "-include", str(work / "adapter.h"), "-I", str(work), "-I", str(ROOT / "apps"),
                            str(ROOT / "apps/home_data.c"), str(ROOT / "apps/home_scores.c"),
                            str(ROOT / "apps/home_autoplay.c"), str(ROOT / "apps/home_liked.c"), str(work / "test.c"), "-o", str(executable)], check=True, env=env)
            subprocess.run([str(executable)], cwd=work, check=True, env=env)


if __name__ == "__main__":
    unittest.main(verbosity=2)
