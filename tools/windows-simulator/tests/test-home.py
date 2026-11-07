"""Exercise production Home data code and playlist progression on the native host."""
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
WINDOWS_GCC = Path("C:/msys64/ucrt64/bin/gcc.exe")
GCC = str(WINDOWS_GCC) if WINDOWS_GCC.is_file() else shutil.which("gcc")
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
    def test_database_commit_during_playback_start(self):
        database = (ROOT / "apps/tagcache.c").read_text(encoding="utf-8")
        playback = (ROOT / "apps/playback.c").read_text(encoding="utf-8")
        code = (Path(__file__).parent / "tagcache-harness.c").read_text(encoding="utf-8")
        code = code.replace("/* PRODUCTION_STRUCTS */", "\n".join(
            function(database, signature) + ";" for signature in (
                "struct index_entry {", "struct tagcache_header {", "struct master_header {")))
        code = code.replace("/* PRODUCTION_ALLOCATION */", "\n".join((
            function(database, "static void allocate_tempbuf(void)"),
            function(database, "static void free_tempbuf(void)"),
            function(playback, "static void audio_reset_buffer(void)"))))
        code = code.replace("/* PRODUCTION_COMMIT */", function(database, "static bool commit(void)"))
        with tempfile.TemporaryDirectory(prefix="rockbox-db-playback-") as directory:
            source = Path(directory) / "test.c"
            source.write_text(code, encoding="utf-8")
            executable = Path(directory) / "test.exe"
            for target in ("SANSA_CLIP", "SANSA_CLIPPLUS"):
                subprocess.run([GCC, "-std=gnu11", "-Wall", "-Wextra", "-Werror",
                                f"-D{target}", str(source), "-o", str(executable)], check=True)
                subprocess.run([str(executable)], check=True)

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
            subprocess.run([GCC, "-std=gnu11", "-D__PCTOOL__",
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
            if any(token in viewport[4] for token in ("%xd(B", "%xd(D", "%xd(E", "%xd(F", "%xd(H", "%?hG")):
                self.assertGreaterEqual(viewport[1], progress[1] + progress[3])
        footer = sorted((v for v in viewports if v[1] >= progress[1] + progress[3]), key=lambda v: v[0])
        for left, right in zip(footer, footer[1:]):
            self.assertLessEqual(left[0] + left[2], right[0])
        self.assertEqual(len(re.findall(r"%\?it<", skin)), 1)
        self.assertIn("%?if(%id, !=, %it)", skin)
        visualizer = next(v for v in viewports if "%?pL<" in v[4])
        album = next(v for v in viewports if "%?id" in v[4])
        self.assertGreaterEqual(visualizer[1], album[1] + album[3])
        for token in ("%pc", "%pt"):
            timer = next(v for v in viewports if token in v[4])
            self.assertGreaterEqual(timer[1], visualizer[1] + visualizer[3])
            self.assertLessEqual(timer[1] + timer[3], progress[1])
        right_channel = next(v for v in viewports if "%?pR<" in v[4])
        self.assertEqual(visualizer[3], 10)
        self.assertEqual(right_channel[3], 10)
        self.assertEqual(visualizer[0] + visualizer[2], right_channel[0])
        self.assertIn("bars-right-clip.bmp", skin)
        self.assertNotIn("%hV", skin)
        self.assertNotIn("%pm", skin)
        self.assertNotIn("%pr", skin)
        self.assertNotIn("%Sx(Next:)", skin)
        self.assertNotIn("%?It", skin)
        self.assertIn("%xl(E,add-playlist-clip.bmp,0,0)", skin)
        self.assertIn("%xd(E)", skin)
        self.assertNotIn("%?mm", skin)
        import struct
        icon = (ROOT / "wps/cabbiev2/add-playlist-clip.bmp").read_bytes()
        self.assertEqual(icon[:2], b"BM")
        self.assertEqual(struct.unpack_from("<iiHH", icon, 18), (12, 8, 1, 1))

    def test_clip_button_events(self):
        import re
        keymap = (ROOT / "apps/keymaps/keymap-clip.c").read_text(encoding="utf-8")
        arrays = []
        for name in ("standard", "wps", "home"):
            start = keymap.index(f"static const struct button_mapping button_context_{name}[]")
            end = keymap.index("};", start) + 2
            mapping = keymap[start:end]
            if name == "home":
                mapping = "#if defined(SANSA_CLIP) || defined(SANSA_CLIPPLUS)\n" + mapping + "\n#endif"
            arrays.append(mapping)
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
#define LAST_ITEM_IN_LIST__NEXTLIST(context) LAST_ITEM_IN_LIST
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
#if defined(SANSA_CLIP) || defined(SANSA_CLIPPLUS)
    assert(resolve(wps, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT) == ACTION_WPS_PLAY);
    assert(resolve(wps, BUTTON_UP | BUTTON_REL, BUTTON_UP) == ACTION_WPS_TOGGLE_LIKE);
    assert(resolve(wps, BUTTON_SELECT | BUTTON_UP, BUTTON_SELECT) == ACTION_WPS_ADD_TO_PLAYLIST);
    assert(resolve(wps, BUTTON_SELECT | BUTTON_UP | BUTTON_REPEAT, BUTTON_SELECT | BUTTON_UP) != ACTION_WPS_ADD_TO_PLAYLIST);
    assert(resolve(wps, BUTTON_UP | BUTTON_REL, BUTTON_SELECT | BUTTON_UP) != ACTION_WPS_TOGGLE_LIKE);
    assert(resolve(wps, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT | BUTTON_UP) != ACTION_WPS_PLAY);
    assert(resolve(button_context_home, BUTTON_RIGHT, BUTTON_NONE) == ACTION_STD_OK);
    assert(resolve(button_context_home, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT) == ACTION_WPS_PLAY);
    assert(resolve(button_context_home, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT | BUTTON_REPEAT) != ACTION_WPS_PLAY);
#else
    assert(resolve(wps, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT) == ACTION_WPS_BROWSE);
    assert(resolve(wps, BUTTON_UP | BUTTON_REL, BUTTON_UP) == ACTION_WPS_PLAY);
    assert(resolve(wps, BUTTON_SELECT | BUTTON_UP, BUTTON_SELECT) != ACTION_WPS_ADD_TO_PLAYLIST);
#endif
    assert(resolve(wps, BUTTON_UP, BUTTON_NONE) != ACTION_WPS_TOGGLE_LIKE);
    assert(resolve(wps, BUTTON_UP | BUTTON_REPEAT, BUTTON_UP) == ACTION_WPS_PITCHSCREEN);
    assert(resolve(wps, BUTTON_UP | BUTTON_REL, BUTTON_UP | BUTTON_REPEAT) != ACTION_WPS_TOGGLE_LIKE);
    assert(resolve(wps, BUTTON_SELECT | BUTTON_REPEAT, BUTTON_SELECT) == ACTION_WPS_CONTEXT);
    assert(resolve(wps, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT | BUTTON_REPEAT) != ACTION_WPS_PLAY);
    assert(resolve(std, BUTTON_UP, BUTTON_NONE) == ACTION_STD_PREV);
    assert(resolve(std, BUTTON_UP | BUTTON_REPEAT, BUTTON_UP) == ACTION_STD_PREVREPEAT);
    assert(resolve(std, BUTTON_SELECT | BUTTON_REL, BUTTON_SELECT) == ACTION_STD_OK);
    assert(resolve(std, BUTTON_SELECT | BUTTON_UP, BUTTON_SELECT) != ACTION_WPS_ADD_TO_PLAYLIST);
    return 0;
}
"""
        gcc = GCC
        with tempfile.TemporaryDirectory(prefix="rockbox-buttons-") as directory:
            source = Path(directory) / "buttons.c"
            source.write_text(code, encoding="utf-8")
            executable = Path(directory) / "buttons.exe"
            for target in ("SANSA_CLIP", "SANSA_CLIPPLUS", "SANSA_CLIPV2"):
                subprocess.run([gcc, "-std=c11", "-Wall", "-Wextra", "-Werror",
                                f"-D{target}", str(source), "-o", str(executable)], check=True)
                subprocess.run([str(executable)], check=True)

    def test_native_open_preserves_errno(self):
        native = (ROOT / "firmware/common/file.c").read_text(encoding="utf-8")
        internal = (ROOT / "firmware/include/file_internal.h").read_text(encoding="utf-8")
        error_macros = internal[internal.index("#define ERRNO 0"):internal.index("/** Misc. stuff **/")]
        code = r"""#include <assert.h>
#include <errno.h>
#include <fcntl.h>
#define MAX_OPEN_FILES 1
#define DEBUGF(...) ((void)0)
/* FLAGS_AND_ERRORS */
struct filestr_desc { struct { unsigned int flags; } stream; };
static struct filestr_desc open_streams[MAX_OPEN_FILES];
static int open_error;
static void file_internal_lock_WRITER(void) {}
static void file_internal_unlock_WRITER(void) {}
static int close_internal(struct filestr_desc *file)
{ file->stream.flags = 0; return 0; }
static int open_internal_inner2(const char *path, struct filestr_desc *file,
                                unsigned int callflags, int oflag)
{
    (void)path; (void)callflags; (void)oflag;
    /* Failed native opens already destroy the stream before returning. */
    file->stream.flags = open_error ? 0 : FDO_BUSY;
    if (open_error) { errno = open_error; return -7; }
    return 0;
}
/* NATIVE_FUNCTIONS */
int main(void)
{
    const int errors[] = {ENOENT, EIO, EACCES, ENOTDIR};
    for (unsigned int i = 0; i < sizeof(errors) / sizeof(*errors); i++)
    {
        open_error = errors[i]; errno = 0;
        assert(open_internal_inner1("/.rockbox/home-scores.tsv", O_RDONLY, 0) == -73);
        assert(errno == errors[i]);
        assert(!open_streams[0].stream.flags);
    }
    errno = 0;
    assert(open_internal_inner1("/invalid", O_RDONLY | O_TRUNC, 0) == -2);
    assert(errno == EINVAL);
    open_streams[0].stream.flags = FDO_BUSY;
    assert(open_internal_inner1("/busy", O_RDONLY, 0) == -1);
    assert(errno == EMFILE);
    open_streams[0].stream.flags = 0; open_error = 0;
    assert(open_internal_inner1("/created", O_RDWR | O_CREAT, 0) == 0);
    assert(open_streams[0].stream.flags == FDO_BUSY);
    assert(rbfs_close(0) == 0);
    assert(rbfs_close(0) == -2 && errno == EBADF);
    return 0;
}
"""
        code = code.replace("/* FLAGS_AND_ERRORS */",
                            function(internal, "enum fildes_and_obj_flags") + ";\n" + error_macros)
        code = code.replace("/* NATIVE_FUNCTIONS */", "\n\n".join(
            function(native, signature) for signature in (
                "static int alloc_filestr(", "int rbfs_close(", "static int open_internal_inner1(")))
        with tempfile.TemporaryDirectory(prefix="rockbox-native-open-") as directory:
            work = Path(directory)
            source, executable = work / "open.c", work / "open.exe"
            source.write_text(code, encoding="utf-8")
            subprocess.run([GCC, "-std=gnu11", "-Wall", "-Wextra",
                            "-Werror", str(source), "-o", str(executable)], check=True)
            subprocess.run([str(executable)], check=True)

    def test_native_home_and_playback(self):
        gcc = GCC
        self.assertIsNotNone(gcc, "A host GCC compiler is required")
        with tempfile.TemporaryDirectory(prefix="rockbox-home-") as directory:
            work = Path(directory)
            headers = {
                "config.h": "#pragma once\n#define SANSA_CLIP\n",
                "adapter.h": "#define __ACTION_H__\n#define MISC_H\n#define __PLAYLIST_H__\n#define __SETTINGS_H__\n#define _TAGCACHE_H\n#define _APPEVENTS_H\n#include <action.h>\n#include <misc.h>\n#include <playlist.h>\n#include <settings.h>\n#include <tagcache.h>\n#include <appevents.h>\n",
                "rbpaths.h": '#pragma once\n#define ROCKBOX_DIR "."\n',
                "kernel.h": "#pragma once\n#define HZ 100\n#define MIN(a,b) ((a)<(b)?(a):(b))\nstruct mutex { int unused; };\nvoid mutex_init(struct mutex *);\nvoid mutex_lock(struct mutex *);\nvoid mutex_unlock(struct mutex *);\nvoid yield(void);\n",
                "action.h": "#pragma once\nenum { ACTION_NONE, ACTION_STD_PREV, ACTION_STD_PREVREPEAT, ACTION_STD_MENU, ACTION_WPS_MENU, ACTION_WPS_ADD_TO_PLAYLIST };\n",
                "misc.h": "#pragma once\nint read_line(int, char *, int);\nint open_utf8(const char *, int);\n",
                "dir.h": "#pragma once\nint test_mkdir(const char *);\n#define mkdir test_mkdir\n",
                "string-extra.h": "#pragma once\n#include <stddef.h>\n#include <strcasecmp.h>\nsize_t strlcpy(char *, const char *, size_t);\n",
                "strcasecmp.h": (ROOT / "firmware/include/strcasecmp.h").read_text(encoding="utf-8"),
                "core_alloc.h": "#pragma once\n#include <stddef.h>\nint core_alloc(size_t);\nint core_free(int);\nvoid *core_get_data(int);\nvoid *core_get_data_pinned(int);\nvoid core_put_data_pinned(void *);\n",
                "fixedpoint.h": "#pragma once\nlong fp16_log(int);\n",
                "timefuncs.h": "#pragma once\n#include <time.h>\n#include <stdbool.h>\nstruct tm *get_time(void);\nbool valid_time(const struct tm *);\n",
                "metadata.h": "#pragma once\n#include <stdbool.h>\n#include <file.h>\nstruct mp3entry { char path[MAX_PATH]; char *title, *artist, *genre_string; unsigned long elapsed, length; };\nbool get_metadata(struct mp3entry *, int, const char *);\n",
                "audio.h": "#pragma once\n#include <metadata.h>\nenum { TEF_NONE, TEF_CURRENT=1, TEF_AUTO_SKIP=2, TEF_REWIND=4 };\nstruct track_event { unsigned int flags; struct mp3entry *id3; };\nvoid audio_stop(void);\n#define AUDIO_STATUS_PLAY 1\nint audio_status(void);\nvoid audio_flush_and_reload_tracks(void);\nstruct mp3entry *audio_current_track(void);\n",
                "appevents.h": "#pragma once\n#include <stdbool.h>\nenum { PLAYBACK_EVENT_TRACK_CHANGE, PLAYBACK_EVENT_TRACK_FINISH };\nbool add_event(unsigned short, void (*)(unsigned short, void *));\n",
                "lang.h": "#pragma once\n#include <stdint.h>\nenum { LANG_HOME_DATABASE, LANG_TAGCACHE_BUSY, LANG_FAILED, LANG_HOME_NO_OTHER_GENRE, LANG_PLAYLIST_ADDED, LANG_PLAYLIST_ALREADY_ADDED, LANG_PLAYLIST_ADD_FAILED, LANG_CATALOG_NO_PLAYLISTS };\n#define ID2P(x) ((const char *)(intptr_t)(x))\n",
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
int test_read(int, void *, size_t);
int test_open(const char *, int, ...);
int test_close(int);
int test_sync(int);
int test_rename(const char *, const char *);
int test_truncate(int, off_t);
bool file_exists(const char *);
#define write test_write
#define read test_read
#define open test_open
#define close test_close
#define fsync test_sync
#define rename test_rename
#define ftruncate test_truncate
""",
                "logdiskf.h": """#pragma once
#include <stdarg.h>
void _logdiskf(const char *file, const char level, const char *format, ...);
#define ERRORF(...) _logdiskf(__func__, 'E', __VA_ARGS__)
#define WARNF(...) _logdiskf(__func__, 'W', __VA_ARGS__)
#define NOTEF(...) _logdiskf(__func__, 'N', __VA_ARGS__)
""",
            }
            for name, text in headers.items():
                (work / name).write_text(text, encoding="utf-8")
            playlist = (ROOT / "apps/playlist.c").read_text(encoding="utf-8")
            misc = (ROOT / "apps/misc.c").read_text(encoding="utf-8")
            abrepeat = (ROOT / "apps/abrepeat.h").read_text(encoding="utf-8")
            extracted = ["#define read test_read\n" + function(misc, "int read_line(") + "\n#undef read",
                         "#define open test_open\n#define read test_read\n#define write test_write\n#define close test_close\n"
                         + function(misc, "int open_utf8(") + "\n#undef open\n#undef read\n#undef write\n#undef close",
                         *(function(playlist, signature) for signature in (
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
            catalog = (ROOT / "apps/playlist_catalog.c").read_text(encoding="utf-8")
            io_names = {"open": "test_open", "read": "test_read", "write": "test_write",
                        "close": "test_close", "fsync": "test_sync", "rename": "test_rename",
                        "remove": "test_remove"}
            extracted.append("\n".join(f"#define {name} {adapter}" for name, adapter in io_names.items())
                             + "\n" + "\n\n".join(function(catalog, signature) for signature in (
                                 "static int catalog_read_line(", "static int catalog_append_unique(",
                                 "bool catalog_add_track_to_a_playlist("))
                             + "\n" + "\n".join(f"#undef {name}" for name in io_names))
            wps = (ROOT / "apps/gui/wps.c").read_text(encoding="utf-8")
            harness = (Path(__file__).parent / "home-harness.c").read_text(encoding="utf-8")
            harness = harness.replace("/* WPS_ADD_ACTION */", function(wps, "case ACTION_WPS_ADD_TO_PLAYLIST:"))
            (work / "test.c").write_text(harness.replace("/* PRODUCTION_FUNCTIONS */", "\n\n".join(extracted)), encoding="utf-8")
            env = os.environ.copy()
            env["PATH"] = str(Path(gcc).parent) + os.pathsep + env["PATH"]
            sources = [str(ROOT / "apps/home_data.c"), str(ROOT / "apps/home_scores.c"),
                       str(ROOT / "apps/home_autoplay.c"), str(ROOT / "apps/home_liked.c"),
                       str(work / "test.c")]
            for name, scanner in (("host", []),
                                  ("firmware-scanner", [str(ROOT / "firmware/libc/sscanf.c")])):
                run_dir = work / name
                run_dir.mkdir()
                executable = run_dir / "home-test.exe"
                subprocess.run([gcc, "-std=gnu11", "-fwrapv", "-Wall", "-Wextra", "-Werror",
                                "-include", str(work / "adapter.h"), "-I", str(work),
                                "-I", str(ROOT / "apps"), *sources, *scanner,
                                "-o", str(executable)], check=True, env=env)
                subprocess.run([str(executable)], cwd=run_dir, check=True, env=env)


if __name__ == "__main__":
    unittest.main(verbosity=2)
