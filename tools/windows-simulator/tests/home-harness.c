/* Host adapters; the runner inserts the actual production playlist functions. */
#include <assert.h>
#include <errno.h>
#include <limits.h>
#include <ctype.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <stdint.h>
#include <stdlib.h>
#ifdef _WIN32
#include <direct.h>
#include <io.h>
#include <windows.h>
#else
#include <sys/stat.h>
#include <unistd.h>
#define _mkdir(path) mkdir(path, 0777)
#define _rmdir rmdir
#define _open open
#define _read read
#define _close close
#define _commit fsync
#define _chsize ftruncate
#define _access access
#define O_BINARY 0
#define MoveFileExA(src, dst, flags) (rename(src, dst) == 0)
#define _set_abort_behavior(...) ((void)0)
#define _set_error_mode(...) ((void)0)
#endif
#undef MAX_PATH
#define MAX_PATH 260
#include "home_screen.h"
#include "home_autoplay.h"
#include "audio.h"
#include "core_alloc.h"
#include "timefuncs.h"
#include "kernel.h"
#include "action.h"
#include "pathfuncs.h"
#include "lang.h"
#define DEBUGF(...) ((void)0)
#define logf(...) ((void)0)
#define BOM_UTF_8_SIZE 3
#define BOM_UTF_8 "\xef\xbb\xbf"
#undef write
#undef open
#undef read
#undef close
#undef fsync
#undef rename
#undef ftruncate

#define FAILURE_LOG_SIZE 4096
static char failure_log[FAILURE_LOG_SIZE];
static size_t failure_log_size;
static void clear_failure_log(void) { failure_log[0] = 0; failure_log_size = 0; }
static bool failure_log_contains(const char *text) { return strstr(failure_log, text) != NULL; }
void _logdiskf(const char *file, const char level, const char *format, ...)
{
    (void)file;
    (void)level;
    if (failure_log_size >= sizeof(failure_log) - 1)
        return;
    va_list ap;
    va_start(ap, format);
    int n = vsnprintf(failure_log + failure_log_size,
                      sizeof(failure_log) - failure_log_size, format, ap);
    va_end(ap);
    if (n > 0)
    {
        size_t used = (size_t)n;
        size_t room = sizeof(failure_log) - failure_log_size - 1;
        failure_log_size += used < room ? used : room;
        failure_log[failure_log_size] = 0;
    }
}

static const char *disk_path(const char *path)
{ return !strncmp(path, "/Playlists", 10) ? path + 1 : path; }
int test_mkdir(const char *path) { return _mkdir(disk_path(path)); }
static int fail_write, fail_sync, fail_rename, rename_failure_countdown;
static int fail_open, fail_read, fail_close, fail_alloc;
static bool short_write, short_read;
int test_open(const char *path, int flags, ...)
{
    if (fail_open) { errno = EACCES; return -1; }
    return _open(disk_path(path), flags | O_BINARY, 0666);
}
int test_read(int fd, void *data, size_t length)
{
    if (fail_read) { errno = EIO; return -1; }
    if (short_read && length > 5) length = 5;
    return _read(fd, data, (unsigned int)length);
}
int test_write(int fd, const void *data, size_t length)
{
    if (fail_write) { errno = ENOSPC; return -1; }
    if (short_write && length > 1) return write(fd, data, length / 2);
    return write(fd, data, length);
}
int test_close(int fd)
{
    int result = _close(fd);
    if (fail_close) { errno = EIO; return -1; }
    return result;
}
int test_sync(int fd)
{
    if (fail_sync) { errno = EIO; return -1; }
    return _commit(fd);
}
int test_rename(const char *old, const char *new)
{
    if (rename_failure_countdown > 0 && --rename_failure_countdown == 0) return -1;
    return !fail_rename && MoveFileExA(disk_path(old), disk_path(new), MOVEFILE_REPLACE_EXISTING) ? 0 : -1;
}
static int test_remove(const char *path) { return remove(disk_path(path)); }
int test_truncate(int fd, off_t length) { return _chsize(fd, length); }
size_t strlcpy(char *dest, const char *src, size_t size)
{
    size_t length = strlen(src);
    if (size) { size_t n = length < size - 1 ? length : size - 1; memcpy(dest, src, n); dest[n] = 0; }
    return length;
}

enum { REPEAT_OFF, REPEAT_ALL, REPEAT_ONE, REPEAT_SHUFFLE, REPEAT_AB };
#define AB_REPEAT_ENABLE
#define PLAYLIST_QUEUED 0x100u
#define PLAYLIST_SKIPPED 0x200u
#define PLAYLIST_COMMAND_RESET 6
#define PLAYLIST_COMMAND_FLAGS 7
static unsigned long indices[1024];
static struct playlist_info current_playlist;
struct test_settings global_settings = {.max_files_in_playlist = 16};
static int manual_skip;
static int calculate_step_count(const struct playlist_info *p, int steps) { (void)p; return steps; }
static int rotate_index(const struct playlist_info *p, int index) { return (index + p->amount - p->first_index) % p->amount; }
static bool playlist_modified(const struct playlist_info *p) { return p->flags & PLAYLIST_FLAG_MODIFIED; }
static void dc_thread_stop(struct playlist_info *p) { (void)p; }
static void dc_thread_start(struct playlist_info *p, bool b) { (void)p; (void)b; }
static void playlist_write_lock(struct playlist_info *p) { (void)p; }
static void playlist_write_unlock(struct playlist_info *p) { (void)p; }
static int is_manual_skip(void) { return manual_skip; }
static void remove_track_unlocked(struct playlist_info *p, int i, bool b) { (void)p; (void)i; (void)b; assert(0); }
static void sort_playlist_unlocked(struct playlist_info *p, bool a, bool b) { (void)p; (void)a; (void)b; }
static void randomise_playlist_unlocked(struct playlist_info *p, int seed, bool a, bool b) { (void)p; (void)seed; (void)a; (void)b; }
static void iap_on_shuffle_state(bool b) { (void)b; }
static int create_and_play_dir(int steps, bool b) { (void)steps; (void)b; assert(0); return -1; }
static int update_control_unlocked(struct playlist_info *p, int cmd, int a, int b, const char *c, const char *d, const char *e)
{ (void)p; (void)cmd; (void)a; (void)b; (void)c; (void)d; (void)e; return 0; }
static void sync_control_unlocked(struct playlist_info *p) { (void)p; }
int playlist_get_repeat_mode(void);
bool playlist_allow_dirplay(const struct playlist_info *p);

/* Only platform services are adapted; queue generation and score I/O below
 * are the production sources, compiled as separate translation units. */
static void *allocations[64];
static size_t allocation_sizes[64], live_bytes, peak_bytes, largest_request;
int core_alloc(size_t length)
{
    if (length > largest_request) largest_request = length;
    if (fail_alloc) { errno = ENOMEM; return -1; }
    for (int i = 1; i < 64; i++)
        if (!allocations[i])
        {
            allocations[i] = malloc(length);
            if (!allocations[i]) return -1;
            allocation_sizes[i] = length;
            live_bytes += length;
            if (live_bytes > peak_bytes) peak_bytes = live_bytes;
            return i;
        }
    return -1;
}
int core_free(int handle)
{
    live_bytes -= allocation_sizes[handle];
    free(allocations[handle]); allocations[handle] = NULL;
    return 0;
}
void *core_get_data(int handle) { return allocations[handle]; }
void *core_get_data_pinned(int handle) { return core_get_data(handle); }
void core_put_data_pinned(void *data) { (void)data; }
void mutex_init(struct mutex *m) { (void)m; }
void mutex_lock(struct mutex *m) { (void)m; }
void mutex_unlock(struct mutex *m) { (void)m; }
void yield(void) {}
static bool clock_valid = true;
struct tm *get_time(void)
{
    static struct tm clock = {.tm_year = 126, .tm_mon = 10, .tm_mday = 5};
    return &clock;
}
bool valid_time(const struct tm *clock) { (void)clock; return clock_valid; }

static struct { const char *path, *genre; bool exists; } library[] = {
    {"/Music/seed.mp3", "Pop", true},
    {"/Elsewhere/not-liked.mp3", "POP", true},
    {"/Music/rock.mp3", "Rock", true},
    {"/Music/missing.mp3", "Pop", false},
    {"/Music/歌曲 troisième.mp3", "Pop", true},
};
static bool database_usable = true;
static int large_library_count, missing_large_track = -1, database_searches;
static bool large_library_missing;
bool file_exists(const char *path)
{
    if (!strncmp(path, "/Large/", 7))
    {
        int index = -1;
        return !large_library_missing && sscanf(path, "/Large/Song %d.mp3", &index) == 1 &&
               index >= 0 && index < large_library_count && index != missing_large_track;
    }
    for (unsigned i = 0; i < sizeof(library) / sizeof(*library); i++)
        if (!strcmp(path, library[i].path)) return library[i].exists;
    return _access(disk_path(path), 0) == 0;
}
bool get_metadata(struct mp3entry *id3, int fd, const char *path)
{
    (void)fd;
    if (!strncmp(path, "/Large/", 7) && file_exists(path))
    {
        memset(id3, 0, sizeof(*id3));
        strlcpy(id3->path, path, sizeof(id3->path));
        id3->genre_string = "Large";
        id3->length = 200000;
        return true;
    }
    for (unsigned i = 0; i < sizeof(library) / sizeof(*library); i++)
        if (!strcmp(path, library[i].path) && library[i].exists)
        {
            memset(id3, 0, sizeof(*id3));
            strlcpy(id3->path, path, sizeof(id3->path));
            id3->genre_string = (char *)library[i].genre;
            id3->length = 200000;
            return true;
        }
    return false;
}
bool tagcache_is_usable(void) { return database_usable; }
bool tagcache_search(struct tagcache_search *s, int tag)
{ (void)tag; database_searches++; *s = (struct tagcache_search){0}; return database_usable; }
bool tagcache_search_add_clause(struct tagcache_search *s, struct tagcache_search_clause *c)
{ assert(c->tag == tag_genre && c->type == clause_is); s->genre = c->str; return true; }
bool tagcache_get_next(struct tagcache_search *s, char *path, long size)
{
    if (!strcmp(s->genre, "Large"))
    {
        if (s->index >= large_library_count) return false;
        snprintf(path, size, "/Large/Song %d.mp3", s->index++);
        return true;
    }
    while (s->index < (int)(sizeof(library) / sizeof(*library)))
    {
        int i = s->index++;
        if (!strcasecmp(s->genre, library[i].genre))
        { strlcpy(path, library[i].path, size); return true; }
    }
    return false;
}
void tagcache_search_finish(struct tagcache_search *s) { (void)s; }
static int splash_count;
static const char *last_splash;
void splash(int ticks, const char *message) { (void)ticks; last_splash = message; splash_count++; }
static void (*callbacks[2])(unsigned short, void *);
bool add_event(unsigned short event, void (*callback)(unsigned short, void *))
{ callbacks[event] = callback; return true; }
static void emit(unsigned short event, struct mp3entry *id3, unsigned flags)
{
    struct track_event te = {.flags = flags, .id3 = id3};
    callbacks[event](event, &te);
}
static int audio_stop_count;
static bool playback_running;
int audio_status(void) { return playback_running ? AUDIO_STATUS_PLAY : 0; }
void audio_stop(void) { audio_stop_count++; playback_running = false; home_autoplay_cancel_genre(); }
static int flush_count;
void audio_flush_and_reload_tracks(void) { flush_count++; }
static struct mp3entry playing_id3;
struct mp3entry *audio_current_track(void) { return &playing_id3; }
static char queue_paths[1024][MAX_PATH];
struct playlist_info *playlist_get_current(void) { return &current_playlist; }
char *playlist_get_name(const struct playlist_info *p, char *out, int size)
{ strlcpy(out, (p ? p : &current_playlist)->filename, size); return out; }
int playlist_get_track_info(struct playlist_info *p, int index, struct playlist_track_info *info)
{
    (void)p;
    if (index < 0 || index >= current_playlist.amount) return -1;
    strlcpy(info->filename, queue_paths[index], sizeof(info->filename)); return 0;
}
int playlist_create(const char *dir, const char *name)
{
    home_autoplay_cancel_genre();
    current_playlist = (struct playlist_info){.indices = indices, .control_fd = -1};
    snprintf(current_playlist.filename, sizeof(current_playlist.filename), "%s/%s", dir, name);
    int fd = test_open(current_playlist.filename, O_RDONLY);
    if (fd < 0) return -1;
    while (current_playlist.amount < 1024 &&
           read_line(fd, queue_paths[current_playlist.amount], MAX_PATH) > 0)
        if (queue_paths[current_playlist.amount][0] == '/') current_playlist.amount++;
    close(fd);
    return 0;
}

static int get_track_filename(struct playlist_info *p, int index, char *out, int size)
{
    (void)p;
    if (index < 0 || index >= current_playlist.amount) return -1;
    strlcpy(out, queue_paths[index], size);
    return 0;
}

/* Catalog UI and the default Latin-1 codepage are platform adapters. The
 * picker deliberately changes playing metadata to test track capture. */
enum catbrowse_status_flags { CATBROWSE_NOTHING, CATBROWSE_CATVIEW, CATBROWSE_PLAYLIST };
static int browser_status, picker_result, catalog_init_result, picker_calls, catalog_reloads;
static const char *picker_playlist = "/Playlists/Selected.m3u8";
static bool picker_changes_track;
static int initialize_catalog(void) { return catalog_init_result; }
static int display_playlists(char *playlist, enum catbrowse_status_flags status)
{
    assert(status == CATBROWSE_PLAYLIST);
    picker_calls++;
    if (picker_changes_track) strlcpy(playing_id3.path, library[2].path, MAX_PATH);
    if (picker_result == -2) splash(HZ * 2, ID2P(LANG_CATALOG_NO_PLAYLISTS));
    if (picker_result < 0) return -1;
    strlcpy(playlist, picker_playlist, MAX_PATH);
    return 0;
}
static void reload_directory(void) { catalog_reloads++; }
static unsigned char *iso_decode(const unsigned char *input, unsigned char *out, int cp, int count)
{
    assert(cp == -1);
    while (count--)
    {
        unsigned char c = *input++;
        if (c >= 128) { *out++ = 0xc0 | (c >> 6); c = 0x80 | (c & 63); }
        *out++ = c;
    }
    return out;
}

/* PRODUCTION_FUNCTIONS */

enum { BUTTON_SELECT = 1, BUTTON_UP = 2, CONTEXT_WPS = 3, SYS_USB_CONNECTED = 4,
       GO_TO_ROOT = 5 };
static int held_buttons, release_step, release_order, wait_calls, wps_leaves;
static bool shortcut_usb;
static int button_status(void) { return held_buttons; }
static int get_action(int context, int timeout)
{
    assert(context == CONTEXT_WPS && timeout == HZ / 10);
    release_step++;
    if (release_step == 1) return ACTION_NONE; /* held chord repeats */
    held_buttons = release_step == 2 ? release_order : 0;
    strlcpy(playing_id3.path, library[2].path, MAX_PATH);
    return shortcut_usb ? SYS_USB_CONNECTED : ACTION_NONE;
}
static long default_event_handler(long action) { return action; }
static void action_wait_for_release(void) { wait_calls++; }
static void gwps_leave_wps(bool theme) { assert(theme); wps_leaves++; }
static int run_wps_add_action(void)
{
    struct { struct mp3entry *id3; } wps = {.id3 = &playing_id3}, *state = &wps;
    bool restore = false;
    switch (ACTION_WPS_ADD_TO_PLAYLIST)
    {
        /* WPS_ADD_ACTION */
    }
    return restore;
}

static void start_track(int index)
{
    struct mp3entry id3;
    current_playlist.index = index;
    assert(get_metadata(&id3, -1, queue_paths[index]));
    playback_running = true;
    playing_id3 = id3;
    emit(PLAYBACK_EVENT_TRACK_CHANGE, &id3, TEF_CURRENT);
}

static void play_batch(void)
{
    for (int i = 0; i < current_playlist.amount; i++) start_track(i);
}

static void prepare_large(int count)
{
    large_library_count = count;
    large_library_missing = false;
    missing_large_track = -1;
    const char *queue = home_autoplay_prepare("/Large/Song 0.mp3");
    assert(queue);
    assert(!playlist_create(".", strrchr(queue, '/') + 1));
    assert(!playlist_set_home_autoplay());
    assert(!strcmp(queue_paths[0], "/Large/Song 0.mp3"));
}

static int large_index(const char *path)
{
    int index = -1;
    assert(sscanf(path, "/Large/Song %d.mp3", &index) == 1);
    assert(index >= 0 && index < large_library_count);
    return index;
}

static void test_sampling_and_refresh(void)
{
    struct home_picks picks = {0};
    assert(home_refresh_due(&picks, 0));
    picks.initialized = true;
    picks.refreshed_at = UINT32_MAX - 50;
    assert(!home_refresh_due(&picks, 49));
    assert(!home_refresh_due(&picks, picks.refreshed_at + HOME_REFRESH_TICKS - 1));
    assert(home_refresh_due(&picks, picks.refreshed_at + HOME_REFRESH_TICKS));
    assert(home_manual_refresh(ACTION_STD_PREV, 0));
    assert(!home_manual_refresh(ACTION_STD_PREVREPEAT, 0));
    assert(!home_manual_refresh(ACTION_STD_PREV, 1));
    home_in_settings = true;
    home_selection = 5;
    home_handle_action(ACTION_STD_PREV);
    assert(home_in_settings);
    home_handle_action(ACTION_STD_MENU);
    assert(!home_in_settings);
    assert(home_selection == 0);
    home_in_settings = true;
    home_handle_action(ACTION_WPS_MENU);
    assert(!home_in_settings);

    unsigned int seen_genres = 0;
    for (unsigned int seed = 0; seed < 100; seed++)
    {
        home_sample_begin(&picks, seed);
        for (int i = 0; i < 12; i++)
        {
            char path[60], genre[20];
            snprintf(path, sizeof(path), "/Music/歌曲 %d.mp3", i);
            snprintf(genre, sizeof(genre), "genre %d", i % 4);
            home_sample_add(&picks, path, "Title", "Artist", genre);
            home_sample_add(&picks, path, "Title", "Artist", genre);
        }
        assert(picks.count == 3);
        for (int i = 0; i < picks.count; i++)
        {
            seen_genres |= 1u << (picks.songs[i].genre[6] - '0');
            assert(strstr(picks.songs[i].label, "Title — Artist"));
            for (int j = 0; j < i; j++)
            {
                assert(strcmp(picks.songs[i].path, picks.songs[j].path));
                assert(strcasecmp(picks.songs[i].genre, picks.songs[j].genre));
            }
        }
    }
    assert(seen_genres == 15);
    home_sample_begin(&picks, 12);
    home_sample_add(&picks, "/a.mp3", NULL, NULL, "Pop");
    home_sample_add(&picks, "/b.mp3", NULL, NULL, "POP");
    home_sample_add(&picks, "/c.mp3", NULL, NULL, NULL);
    assert(picks.count == 1);
}


static void test_playback(void)
{
    for (int mode = REPEAT_OFF; mode <= REPEAT_AB; mode++)
    {
        current_playlist = (struct playlist_info){.amount = 1, .indices = indices,
            .flags = PLAYLIST_FLAG_DIRPLAY, .last_insert_pos = -1};
        assert(!playlist_set_single_track());
        assert(current_playlist.flags & PLAYLIST_FLAG_SINGLE_TRACK);
        assert(!(current_playlist.flags & PLAYLIST_FLAG_DIRPLAY));
        global_settings.repeat_mode = mode;
        global_settings.next_folder = 1;
        assert(playlist_get_repeat_mode() == REPEAT_OFF);
        assert(!ab_repeat_mode_enabled());
        assert(!playlist_allow_dirplay(NULL));
        assert(playlist_check(0));
        assert(!playlist_check(1));
        assert(get_next_index(&current_playlist, 1, REPEAT_ALL) == -1);
        for (manual_skip = 0; manual_skip <= 1; manual_skip++)
            assert(playlist_next(1) == -1);
        assert(global_settings.repeat_mode == mode);
        current_playlist.flags = 0;
        global_settings.next_folder = 0;
        manual_skip = 0;
        assert(playlist_get_repeat_mode() == mode);
        assert(ab_repeat_mode_enabled() == (mode == REPEAT_AB));
        assert(playlist_next(1) == (mode == REPEAT_OFF ? -1 : 0));
    }
    current_playlist.amount = 3;
    current_playlist.index = 0;
    global_settings.repeat_mode = REPEAT_OFF;
    assert(playlist_next(1) == 1);
}

static void test_scores(void)
{
    const uint32_t now = 10000000u, month = 30u * 86400u;
    const char *path = "/Music/Beyoncé 歌曲%\t.mp3";
    remove(HOME_SCORES_FILE);
    home_scores_reload();
    assert(home_listen_points(499, 1000, false) == 0);
    assert(home_listen_points(500, 1000, false) == 1);
    assert(home_listen_points(500, 1001, false) == 0);
    assert(home_listen_points(501, 1001, false) == 1);
    assert(home_listen_points(0, 0, false) == 0);
    assert(home_listen_points(1000, 1000, true) == 2);
    assert(home_score_decay(8 * HOME_SCORE_ONE, now, now + month) == 4 * HOME_SCORE_ONE);
    assert(home_score_decay(HOME_SCORE_ONE, now, now - 1) == HOME_SCORE_ONE);
    assert(home_score_decay(HOME_SCORE_ONE, 0, now) == HOME_SCORE_ONE);
    assert(home_score_decay(HOME_SCORE_ONE, now, now + month * 32) == 0);
    uint32_t half = home_score_decay(HOME_SCORE_ONE, now, now + month / 2);
    assert(half > 46000 && half < 46500);
    assert(home_score_record(path, 100, 1000, false, now));
    assert(home_score_record(path, 500, 1000, false, now));
    assert(home_score_record(path, 1000, 1000, true, now));
    assert(home_score_get(path, now) == 3 * HOME_SCORE_ONE);
    home_scores_reload();
    assert(home_score_get(path, now + month) == 3 * HOME_SCORE_ONE / 2);
    fail_write = 1;
    assert(!home_score_record(path, 1000, 1000, true, now));
    fail_write = 0;
    short_write = true;
    assert(!home_score_record(path, 1000, 1000, true, now));
    short_write = false;
    fail_sync = 1;
    assert(!home_score_record(path, 1000, 1000, true, now));
    fail_sync = 0;
    assert(home_scores_save_failed());
    assert(!home_scores_save_failed());
    home_scores_reload();
    assert(home_score_get(path, now) == 3 * HOME_SCORE_ONE);
    for (int i = 0; i < 70; i++) assert(home_score_record(path, 1000, 1000, true, now));
    int fd = test_open(HOME_SCORES_FILE, O_WRONLY | O_APPEND);
    const char *partial = "123\t10000000\t0\t0\t0\t/Partial";
    assert(write(fd, partial, strlen(partial)) == (int)strlen(partial)); close(fd);
    home_scores_reload();
    fail_rename = 1;
    assert(!home_scores_compact());
    fail_rename = 0;
    assert(home_score_get(path, now) == 143 * HOME_SCORE_ONE);
    assert(home_scores_compact());
    assert(home_score_get(path, now) == 143 * HOME_SCORE_ONE);
    assert(home_score_record(path, 0, 1000, false, now));
    fd = test_open(HOME_SCORES_FILE, O_RDONLY);
    assert(lseek(fd, 0, SEEK_END) < 1024); close(fd);
    home_scores_reload();
    assert(home_score_get(path, now) == 143 * HOME_SCORE_ONE);
    fd = test_open(HOME_SCORES_FILE, O_WRONLY | O_APPEND);
    assert(write(fd, partial, strlen(partial)) == (int)strlen(partial)); close(fd);
    home_scores_reload();
    assert(home_score_get(path, now) == 143 * HOME_SCORE_ONE);
    assert(home_score_record(path, 500, 1000, false, now - 100));
    assert(home_score_get(path, now) == 144 * HOME_SCORE_ONE);
    home_scores_reload();
    assert(home_score_get(path, now) == 144 * HOME_SCORE_ONE);
    assert(home_score_get("/Partial", now) == 0);
    clock_valid = false; assert(home_score_time() == 0); clock_valid = true;

    int strong_first = 0;
    for (uint32_t seed = 0; seed < 10000; seed++)
        strong_first += home_weighted_rank("/strong", seed, 2 * HOME_SCORE_ONE) <
                        home_weighted_rank("/new", seed, 0);
    /* Weight 9 versus 1 gives about 90%, with measurable exploration. */
    assert(strong_first > 8700 && strong_first < 9300);
    remove(HOME_SCORES_FILE); home_scores_reload();
    assert(home_score_record("/Home/favorite.mp3", 1000, 1000, true, home_score_time()));
    int home_favorite = 0;
    for (uint32_t seed = 0; seed < 1000; seed++)
    {
        struct home_picks picks = {0};
        home_sample_begin(&picks, seed);
        home_sample_add(&picks, "/Home/favorite.mp3", "Favorite", NULL, "Pop");
        home_sample_add(&picks, "/Home/rock.mp3", "Rock", NULL, "Rock");
        home_sample_add(&picks, "/Home/jazz.mp3", "Jazz", NULL, "Jazz");
        home_sample_add(&picks, "/Home/folk.mp3", "Folk", NULL, "Folk");
        for (int i = 0; i < picks.count; i++)
            home_favorite += !strcmp(picks.songs[i].path, "/Home/favorite.mp3");
    }
    assert(home_favorite > 970 && home_favorite < 1000);
    remove(HOME_SCORES_FILE); home_scores_reload();
}

static void append_score_text(const char *text)
{
    int fd = test_open(HOME_SCORES_FILE, O_WRONLY | O_CREAT | O_APPEND);
    assert(fd >= 0);
    assert(write(fd, text, strlen(text)) == (int)strlen(text));
    assert(!close(fd));
}

static void test_score_malformed(void)
{
    const uint32_t now = 10000000u;
    const char *path = "/Music/Valid 歌曲%\t.mp3";
    remove(HOME_SCORES_FILE);
    home_scores_reload();
    assert(home_score_record(path, 1000, 1000, true, now));
    append_score_text("1\t2\t3\t4\t5 /Bad/space.mp3\n"
                      "1\t2\t3\t4\t4294967296\t/Bad/overflow.mp3\n"
                      "-1\t2\t3\t4\t5\t/Bad/negative.mp3\n"
                      "1\t2\t3\t4\t5\tRelative/path.mp3\n"
                      "1\t2\t3\t4\t5\t/Bad/%FFescape.mp3\n"
                      "1\t2\t3\t4\t5\t\n");
    home_scores_reload();
    assert(home_score_get(path, now) == 2 * HOME_SCORE_ONE);
    assert(home_score_get("/Bad/space.mp3", now) == 0);
    assert(home_score_get("/Bad/overflow.mp3", now) == 0);
    assert(home_score_get("/Bad/negative.mp3", now) == 0);
    assert(home_score_get("/Bad/%FFescape.mp3", now) == 0);

    const char *escaped = "/Music/line\nbreak.mp3";
    assert(home_score_record(escaped, 500, 1000, false, now));
    home_scores_reload();
    assert(home_score_get(path, now) == 2 * HOME_SCORE_ONE);
    assert(home_score_get(escaped, now) == HOME_SCORE_ONE);
    remove(HOME_SCORES_FILE);
    home_scores_reload();
}

static void seed_failure_store(const char *path, uint32_t now)
{
    remove(HOME_SCORES_FILE);
    home_scores_reload();
    assert(home_score_record(path, 1000, 1000, true, now));
    assert(home_score_get(path, now) == 2 * HOME_SCORE_ONE);
    home_scores_save_failed();
}

static void test_score_failures(void)
{
    const uint32_t now = 10000000u;
    const char *path = "/Music/Failure 歌曲.mp3";

    seed_failure_store(path, now);
    clear_failure_log();
    fail_open = 1;
    assert(!home_score_record(path, 1000, 1000, true, now));
    fail_open = 0;
    assert(failure_log_contains("record-open"));

    seed_failure_store(path, now);
    clear_failure_log();
    fail_write = 1;
    assert(!home_score_record(path, 1000, 1000, true, now));
    fail_write = 0;
    assert(failure_log_contains("record-write"));

    seed_failure_store(path, now);
    clear_failure_log();
    short_write = true;
    assert(!home_score_record(path, 1000, 1000, true, now));
    short_write = false;
    assert(failure_log_contains("record-write"));

    seed_failure_store(path, now);
    clear_failure_log();
    fail_sync = 1;
    assert(!home_score_record(path, 1000, 1000, true, now));
    fail_sync = 0;
    assert(failure_log_contains("record-sync"));

    seed_failure_store(path, now);
    clear_failure_log();
    fail_close = 1;
    assert(!home_score_record(path, 1000, 1000, true, now));
    fail_close = 0;
    assert(failure_log_contains("record-close"));
    home_scores_reload();
    assert(home_score_get(path, now) >= 2 * HOME_SCORE_ONE);

    seed_failure_store(path, now);
    for (int i = 0; i < 31; i++)
    {
        char extra[60];
        snprintf(extra, sizeof(extra), "/Music/Failure fill %d.mp3", i);
        assert(home_score_record(extra, 1000, 1000, true, now));
    }
    clear_failure_log();
    fail_alloc = 1;
    assert(!home_score_record("/Music/Alloc failure.mp3", 1000, 1000, true, now));
    fail_alloc = 0;
    assert(failure_log_contains("record-alloc"));
    assert(failure_log_contains("bytes="));
    home_scores_reload();
    assert(home_score_get(path, now) == 2 * HOME_SCORE_ONE);

    seed_failure_store(path, now);
    clear_failure_log();
    fail_open = 1;
    home_scores_reload();
    fail_open = 0;
    assert(failure_log_contains("reload-open"));
    char original_log[FAILURE_LOG_SIZE];
    strcpy(original_log, failure_log);
    assert(!home_score_record(path, 1000, 1000, true, now));
    assert(!home_score_record(path, 1000, 1000, true, now));
    assert(!strcmp(original_log, failure_log));
    assert(home_scores_save_failed());
    home_scores_reload();
    assert(home_score_get(path, now) == 2 * HOME_SCORE_ONE);

    seed_failure_store(path, now);
    clear_failure_log();
    fail_read = 1;
    home_scores_reload();
    fail_read = 0;
    assert(failure_log_contains("reload-read"));
    strcpy(original_log, failure_log);
    assert(!home_score_record(path, 1000, 1000, true, now));
    assert(!strcmp(original_log, failure_log));
    assert(home_scores_save_failed());
    home_scores_reload();
    assert(home_score_get(path, now) == 2 * HOME_SCORE_ONE);

    seed_failure_store(path, now);
    clear_failure_log();
    fail_alloc = 1;
    home_scores_reload();
    fail_alloc = 0;
    assert(failure_log_contains("reload-alloc"));
    assert(failure_log_contains("bytes="));
    strcpy(original_log, failure_log);
    assert(!home_score_record(path, 1000, 1000, true, now));
    assert(!strcmp(original_log, failure_log));
    assert(home_scores_save_failed());
    home_scores_reload();
    assert(home_score_get(path, now) == 2 * HOME_SCORE_ONE);

    seed_failure_store(path, now);
    for (int i = 0; i < 70; i++)
        assert(home_score_record(path, 1000, 1000, true, now));
    assert(home_score_get(path, now) == 142 * HOME_SCORE_ONE);

    clear_failure_log();
    fail_open = 1;
    assert(!home_scores_compact());
    fail_open = 0;
    assert(failure_log_contains("compact-open-src"));
    assert(failure_log_contains("compact-open-dst"));
    home_scores_reload();
    assert(home_score_get(path, now) == 142 * HOME_SCORE_ONE);

    clear_failure_log();
    fail_read = 1;
    assert(!home_scores_compact());
    fail_read = 0;
    assert(failure_log_contains("compact-read"));
    home_scores_reload();
    assert(home_score_get(path, now) == 142 * HOME_SCORE_ONE);

    clear_failure_log();
    fail_write = 1;
    assert(!home_scores_compact());
    fail_write = 0;
    assert(failure_log_contains("compact-write"));
    home_scores_reload();
    assert(home_score_get(path, now) == 142 * HOME_SCORE_ONE);

    clear_failure_log();
    fail_sync = 1;
    assert(!home_scores_compact());
    fail_sync = 0;
    assert(failure_log_contains("compact-sync"));
    home_scores_reload();
    assert(home_score_get(path, now) == 142 * HOME_SCORE_ONE);

    clear_failure_log();
    fail_close = 1;
    assert(!home_scores_compact());
    fail_close = 0;
    assert(failure_log_contains("compact-close-dst"));
    home_scores_reload();
    assert(home_score_get(path, now) == 142 * HOME_SCORE_ONE);

    clear_failure_log();
    fail_rename = 1;
    assert(!home_scores_compact());
    fail_rename = 0;
    assert(failure_log_contains("compact-rename"));
    home_scores_reload();
    assert(home_score_get(path, now) == 142 * HOME_SCORE_ONE);

    clear_failure_log();
    assert(home_scores_compact());
    assert(!failure_log[0]);
    home_scores_reload();
    assert(home_score_get(path, now) == 142 * HOME_SCORE_ONE);
    remove(HOME_SCORES_FILE);
    home_scores_reload();
}

static void test_autoplay(void)
{
    home_autoplay_init();
    global_settings.max_files_in_playlist = 16;
    home_autoplay_cancel_genre();
    current_playlist = (struct playlist_info){.indices = indices};
    strcpy(current_playlist.filename, "/Playlists/Previous.m3u8");
    database_usable = false;
    audio_stop_count = 0;
    assert(!home_autoplay_prepare(library[0].path));
    assert(!strcmp(current_playlist.filename, "/Playlists/Previous.m3u8"));
    assert(audio_stop_count == 0);
    assert(splash_count > 0); database_usable = true;
    const char *queue = home_autoplay_prepare(library[0].path);
    assert(queue);
    assert(audio_stop_count == 1);
    assert(!playlist_create(".", strrchr(queue, '/') + 1));
    assert(!playlist_set_home_autoplay());
    assert(current_playlist.amount == 3);
    assert(!strcmp(queue_paths[0], library[0].path));
    bool outside = false;
    for (int i = 0; i < 3; i++)
    {
        outside |= !strcmp(queue_paths[i], library[1].path);
        for (int j = 0; j < i; j++) assert(strcmp(queue_paths[i], queue_paths[j]));
    }
    assert(outside);
    for (int mode = REPEAT_OFF; mode <= REPEAT_AB; mode++)
    {
        global_settings.repeat_mode = mode;
        assert(playlist_get_repeat_mode() == REPEAT_SHUFFLE);
        assert(!playlist_allow_dirplay(NULL));
        assert(!ab_repeat_mode_enabled());
        play_batch();
        char last[MAX_PATH]; strcpy(last, queue_paths[current_playlist.index]);
        assert(playlist_check(1));
        assert(playlist_next(1) == 0);
        assert(current_playlist.flags & PLAYLIST_FLAG_HOME_AUTOPLAY);
        assert(strcmp(last, queue_paths[0]));
        assert(global_settings.repeat_mode == mode);
        global_settings.playlist_shuffle = false;
        assert(playlist_next(-1) == -1);
        assert(!global_settings.playlist_shuffle);
    }
    struct mp3entry id3;
    assert(get_metadata(&id3, -1, library[0].path));
    uint32_t now = home_score_time();
    emit(PLAYBACK_EVENT_TRACK_CHANGE, &id3, TEF_CURRENT);
    id3.elapsed = id3.length;
    emit(PLAYBACK_EVENT_TRACK_FINISH, &id3, TEF_CURRENT | TEF_AUTO_SKIP);
    emit(PLAYBACK_EVENT_TRACK_FINISH, &id3, TEF_CURRENT | TEF_AUTO_SKIP);
    assert(home_score_get(id3.path, now) == 2 * HOME_SCORE_ONE);
    emit(PLAYBACK_EVENT_TRACK_CHANGE, &id3, TEF_CURRENT);
    id3.elapsed = id3.length / 2;
    emit(PLAYBACK_EVENT_TRACK_FINISH, &id3, TEF_CURRENT | TEF_REWIND);
    assert(home_score_get(id3.path, now) == 2 * HOME_SCORE_ONE);
    current_playlist.flags = 0; /* Replacement must still credit the old track. */
    emit(PLAYBACK_EVENT_TRACK_FINISH, &id3, TEF_CURRENT);
    assert(home_score_get(id3.path, now) == 3 * HOME_SCORE_ONE);
    emit(PLAYBACK_EVENT_TRACK_CHANGE, &id3, TEF_CURRENT);
    id3.elapsed = 0;
    emit(PLAYBACK_EVENT_TRACK_FINISH, &id3, TEF_CURRENT | TEF_AUTO_SKIP);
    assert(home_score_get(id3.path, now) == 3 * HOME_SCORE_ONE);
    emit(PLAYBACK_EVENT_TRACK_CHANGE, &id3, TEF_CURRENT);
    id3.elapsed = id3.length;
    emit(PLAYBACK_EVENT_TRACK_FINISH, &id3, TEF_CURRENT | TEF_AUTO_SKIP);
    assert(home_score_get(id3.path, now) == 3 * HOME_SCORE_ONE);
    /* A persisted flag restored before TRACK_CHANGE resumes learning. */
    current_playlist.flags = PLAYLIST_FLAG_HOME_AUTOPLAY;
    emit(PLAYBACK_EVENT_TRACK_CHANGE, &id3, TEF_CURRENT);
    id3.elapsed = 1;
    emit(PLAYBACK_EVENT_TRACK_FINISH, &id3, TEF_CURRENT);
    assert(home_score_get(id3.path, now) == 3 * HOME_SCORE_ONE);

    library[1].exists = library[4].exists = false;
    queue = home_autoplay_prepare(library[0].path); assert(queue);
    assert(!playlist_create(".", strrchr(queue, '/') + 1));
    assert(!playlist_set_home_autoplay());
    assert(current_playlist.amount == 1);
    assert(playlist_check(1));
    assert(playlist_next(1) == 0);
    assert(current_playlist.amount == 1);
    library[0].exists = false;
    assert(playlist_next(1) == -1);
    library[0].exists = library[1].exists = library[4].exists = true;
    queue = home_autoplay_prepare(library[4].path); assert(queue);
    assert(!playlist_create(".", strrchr(queue, '/') + 1));
    assert(!playlist_set_home_autoplay());
    assert(!strcmp(queue_paths[0], library[4].path));
    play_batch();
    assert(playlist_next(1) == 0);
    printf("Score table and queue peak host allocation: %zu bytes.\n", peak_bytes);
}

static void test_batches_and_resume(void)
{
    global_settings.max_files_in_playlist = 1024;
    prepare_large(45);
    bool seen[45] = {false};
    int searches = database_searches;
    for (int batch = 0; batch < 9; batch++)
    {
        if (batch % 3 == 0) memset(seen, 0, sizeof(seen));
        assert(current_playlist.amount == (batch % 3 == 2 ? 5 : 20));
        for (int i = 0; i < current_playlist.amount; i++)
        {
            int index = large_index(queue_paths[i]);
            assert(!seen[index]); seen[index] = true;
            start_track(i); /* An immediate skip still counts as used. */
            if (batch == 0 && i == 7)
            {
                char saved[20][MAX_PATH], queue[MAX_PATH];
                memcpy(saved, queue_paths, sizeof(saved));
                strcpy(queue, current_playlist.filename);
                home_autoplay_init(); /* Discard volatile session state. */
                assert(!playlist_create(".", strrchr(queue, '/') + 1));
                assert(!playlist_set_home_autoplay());
                assert(current_playlist.amount == 20);
                assert(!memcmp(saved, queue_paths, sizeof(saved)));
                start_track(i); /* Resume the same song; marking is idempotent. */
            }
        }
        char last[MAX_PATH]; strcpy(last, queue_paths[current_playlist.index]);
        assert(playlist_next(1) == 0);
        assert(strcmp(last, queue_paths[0]));
        if (batch % 3 == 2)
            for (int i = 0; i < 45; i++) assert(seen[i]);
    }
    assert(database_searches == searches); /* No database query between batches. */
    assert(current_playlist.amount == 20);

    /* Only the seed started. The other 44, including queued/unreached songs,
     * must all appear before the seed can return. */
    prepare_large(45);
    start_track(0);
    memset(seen, 0, sizeof(seen)); seen[0] = true;
    current_playlist.index = current_playlist.amount - 1;
    for (int batch = 0; batch < 3; batch++)
    {
        assert(playlist_next(1) == 0);
        assert(current_playlist.amount == (batch == 2 ? 4 : 20));
        for (int i = 0; i < current_playlist.amount; i++)
        {
            int index = large_index(queue_paths[i]);
            assert(!seen[index]); seen[index] = true;
            start_track(i);
        }
    }
    for (int i = 0; i < 45; i++) assert(seen[i]);

    /* Starting another suggestion resets history even for the same genre. */
    prepare_large(45);
    play_batch();
    prepare_large(45);
    assert(!strcmp(queue_paths[0], "/Large/Song 0.mp3"));

    global_settings.max_files_in_playlist = 7;
    prepare_large(45);
    assert(current_playlist.amount == 7);
    global_settings.max_files_in_playlist = 1;
    prepare_large(2);
    assert(current_playlist.amount == 1);
    start_track(0);
    assert(playlist_next(1) == 0);
    assert(!strcmp(queue_paths[0], "/Large/Song 1.mp3"));
    start_track(0);
    assert(playlist_next(1) == 0);
    assert(!strcmp(queue_paths[0], "/Large/Song 0.mp3"));
    global_settings.max_files_in_playlist = 1024;
}

static void test_manually_queued_autoplay(void)
{
    prepare_large(45);
    start_track(0);
    struct mp3entry id3;
    assert(get_metadata(&id3, -1, library[1].path));
    emit(PLAYBACK_EVENT_TRACK_CHANGE, &id3, TEF_CURRENT);
    current_playlist.index = current_playlist.amount - 1;
    assert(playlist_next(1) == 0);
    prepare_large(45);
    start_track(0);
    char manual[MAX_PATH];
    for (int n = 1; n < 45; n++)
    {
        snprintf(manual, sizeof(manual), "/Large/Song %d.mp3", n);
        bool present = false;
        for (int i = 0; i < current_playlist.amount; i++)
            if (!strcmp(queue_paths[i], manual)) present = true;
        if (!present) break;
    }
    assert(get_metadata(&id3, -1, manual));
    emit(PLAYBACK_EVENT_TRACK_CHANGE, &id3, TEF_CURRENT);
    current_playlist.index = current_playlist.amount - 1;
    assert(playlist_next(1) == 0);
    for (int i = 0; i < current_playlist.amount; i++) assert(strcmp(queue_paths[i], manual));
}

static void test_batch_weights(void)
{
    remove(HOME_SCORES_FILE); home_scores_reload();
    assert(home_score_record("/Large/Song 1.mp3", 1000, 1000, true, home_score_time()));
    int favorite = 0, lower = 0, favorite_early = 0, lower_early = 0;
    for (unsigned int seed = 0; seed < 100; seed++)
    {
        srand(seed);
        prepare_large(45);
        for (int i = 1; i < current_playlist.amount; i++)
        {
            int index = large_index(queue_paths[i]);
            favorite += index == 1;
            lower += index == 2;
            favorite_early += index == 1 && i < 5;
            lower_early += index == 2 && i < 5;
        }
    }
    assert(favorite > lower && lower > 0);
    assert(favorite_early > lower_early && lower_early > 0);
    remove(HOME_SCORES_FILE); home_scores_reload();
}

static void active_pool(char *name)
{
    char header[MAX_PATH];
    int fd = test_open(current_playlist.filename, O_RDONLY);
    assert(fd >= 0 && read_line(fd, header, sizeof(header)) > 0);
    close(fd);
    const char *marker = strstr(header, "#HOME-POOL:");
    assert(marker && marker[11] && marker[12] == 0);
    char slot = marker[11];
    snprintf(name, MAX_PATH, "./home-autoplay-pool-%c", slot);
}

static void test_batch_failures_and_missing(void)
{
    prepare_large(45);
    char pool[MAX_PATH], original[4096], after[4096], queue[MAX_PATH];
    active_pool(pool);
    strcpy(queue, current_playlist.filename);
    int fd = test_open(pool, O_RDONLY);
    assert(fd >= 0);
    int size = read(fd, original, sizeof(original)); close(fd);
    assert(size > 0 && size < (int)sizeof(original));
    for (int failure = 0; failure < 5; failure++)
    {
        fail_write = failure == 0; fail_sync = failure == 1;
        fail_rename = failure == 2; short_write = failure == 3;
        rename_failure_countdown = failure == 4 ? 2 : 0; /* Pool published, queue fails. */
        assert(!home_autoplay_prepare("/Large/Song 0.mp3"));
        fail_write = fail_sync = fail_rename = 0; short_write = false;
        rename_failure_countdown = 0;
        assert(!strcmp(queue, current_playlist.filename));
        fd = test_open(pool, O_RDONLY);
        assert(fd >= 0 && read(fd, after, sizeof(after)) == size); close(fd);
        assert(!memcmp(original, after, size));
    }
    play_batch();
    for (int failure = 0; failure < 4; failure++)
    {
        fail_write = failure == 0; fail_sync = failure == 1;
        fail_rename = failure == 2; short_write = failure == 3;
        assert(playlist_next(1) == -1);
        fail_write = fail_sync = fail_rename = 0; short_write = false;
        assert(!strcmp(queue, current_playlist.filename));
        assert(current_playlist.amount == 20);
    }
    assert(playlist_next(1) == 0);

    /* A failed progress write latches until a new session is prepared. */
    fail_write = 1; start_track(0); fail_write = 0;
    current_playlist.index = current_playlist.amount - 1;
    assert(playlist_next(1) == -1);
    prepare_large(45);
    fail_sync = 1; start_track(0); fail_sync = 0;
    current_playlist.index = current_playlist.amount - 1;
    assert(playlist_next(1) == -1);
    prepare_large(1); start_track(0);
    fail_sync = 1; assert(playlist_next(1) == -1); fail_sync = 0;
    assert(playlist_next(1) == -1); /* Failed cycle reset also latches. */
    prepare_large(45);
    play_batch();
    missing_large_track = 0; /* Already used; deleting it must not reset history. */
    assert(playlist_next(1) == 0);
    assert(current_playlist.amount == 20);
    play_batch();
    missing_large_track = -1;
    /* Delete one of the last five remaining tracks. */
    bool remaining[45]; memset(remaining, 1, sizeof(remaining));
    active_pool(pool); fd = test_open(pool, O_RDONLY); assert(fd >= 0);
    char row[MAX_PATH + 1]; assert(read_line(fd, row, sizeof(row)) > 0);
    char marker = row[0];
    while (read_line(fd, row, sizeof(row)) > 0)
        remaining[large_index(row + 1)] = row[0] != marker;
    close(fd);
    for (int i = 0; i < 45; i++) if (remaining[i]) { missing_large_track = i; break; }
    assert(playlist_next(1) == 0);
    assert(current_playlist.amount == 4);
    large_library_missing = true;
    current_playlist.index = current_playlist.amount - 1;
    assert(playlist_next(1) == -1);
    large_library_missing = false; missing_large_track = -1;

    /* Missing/truncated progress is an error, never a fresh cycle. */
    prepare_large(45); play_batch(); active_pool(pool);
    fd = test_open(pool, O_RDWR); assert(fd >= 0);
    assert(!test_truncate(fd, 17)); close(fd);
    assert(playlist_next(1) == -1);
    prepare_large(45); play_batch(); active_pool(pool);
    assert(!remove(pool));
    assert(playlist_next(1) == -1);
}

static void test_library_sized_memory(void)
{
    remove(HOME_SCORES_FILE); home_scores_reload();
    peak_bytes = live_bytes;
    uint32_t now = home_score_time();
    large_library_count = 850;
    int fd = test_open(HOME_AUTOPLAY_A, O_WRONLY | O_CREAT | O_TRUNC);
    assert(fd >= 0);
    for (int i = 0; i < 850; i++)
    {
        char path[MAX_PATH], line[MAX_PATH + 1];
        snprintf(path, sizeof(path), "/Large/Song %d.mp3", i);
        assert(home_score_record(path, 1000, 1000, true, now));
        int n = snprintf(line, sizeof(line), "%s\n", path);
        assert(write(fd, line, n) == n);
    }
    close(fd);
    home_scores_reload();
    global_settings.max_files_in_playlist = 1024;
    assert(!playlist_create(".", "home-autoplay-a.m3u8"));
    assert(!playlist_set_home_autoplay());
    current_playlist.index = 849;
    char last[MAX_PATH]; strcpy(last, queue_paths[849]);
    largest_request = 0;
    size_t retained = live_bytes;
    peak_bytes = retained;
    assert(playlist_next(1) == 0);
    assert(current_playlist.amount == 20); /* Legacy full queue adopted as a pool. */
    assert(strcmp(queue_paths[0], last));
    for (int i = 0; i < 20; i++)
    {
        assert(home_score_get(queue_paths[i], now) == 2 * HOME_SCORE_ONE);
        for (int j = 0; j < i; j++) assert(strcmp(queue_paths[i], queue_paths[j]));
    }
    assert(live_bytes <= 32768);
    assert(largest_request <= 320 && peak_bytes <= retained + 320);
    printf("850 scored tracks: %zu bytes retained, %zu bytes peak including queue construction.\n",
           live_bytes, peak_bytes);
    global_settings.max_files_in_playlist = 100;
    largest_request = 0;
    prepare_large(5000);
    assert(current_playlist.amount == 20);
    assert(largest_request <= 320);
}

static void write_likes(const char *contents)
{
    _mkdir("Playlists");
    FILE *f = fopen("Playlists/Liked Songs.m3u8", "wb");
    assert(f && fwrite(contents, 1, strlen(contents), f) == strlen(contents));
    assert(!fclose(f));
    home_liked_invalidate();
}

static void test_likes(void)
{
    remove("Playlists/Liked Songs.m3u8");
    _rmdir("Playlists");
    home_liked_invalidate();
    assert(home_liked_state(library[0].path) == 0);
    unsigned int before = home_liked_revision();
    assert(home_toggle_like(library[0].path) == 1);
    assert(home_liked_revision() == before + 1);
    assert(home_liked_state(library[0].path) == 1);
    home_liked_invalidate();
    assert(home_liked_state(library[0].path) == 1);
    assert(home_toggle_like("/Music/./seed.mp3") == 0);
    write_likes("\xef\xbb\xbf#EXTM3U\r\n../Music/seed.mp3\r\n/Music/seed.mp3\n../Music/歌曲 troisième.mp3\n/Music/SEED.mp3\n");
    assert(home_liked_state(library[0].path) == 1);
    assert(home_toggle_like(library[0].path) == 0);
    assert(home_liked_state("/Music/SEED.mp3") == 1);
    assert(home_liked_state(library[4].path) == 1);
    home_liked_invalidate();
    assert(home_liked_state(library[0].path) == 0); /* all duplicates removed */
    for (int failure = 0; failure < 4; failure++)
    {
        fail_write = failure == 0; short_write = failure == 1;
        fail_sync = failure == 2; fail_rename = failure == 3;
        assert(home_toggle_like(library[0].path) == -1);
        fail_write = fail_sync = fail_rename = 0; short_write = false;
        home_liked_invalidate();
        assert(home_liked_state(library[0].path) == 0);
        assert(home_liked_state(library[4].path) == 1);
    }
    assert(home_toggle_like(library[0].path) == 1);
    assert(home_liked_state(library[0].path) == 1);
    /* Cache refresh happens on re-entry or track change. */
    write_likes("#EXTM3U\n/Music/rock.mp3\n");
    assert(home_liked_state(library[0].path) == 0);
    char invalid[MAX_PATH + 20]; memset(invalid, 'a', sizeof(invalid));
    invalid[0] = '/'; invalid[sizeof(invalid) - 1] = 0;
    assert(home_toggle_like(invalid) == -1);
    assert(home_toggle_like("/Music/bad\nline.mp3") == -1);
    char overlong[MAX_PATH + 20]; memset(overlong, 'a', sizeof(overlong));
    overlong[sizeof(overlong)-2] = '\n'; overlong[sizeof(overlong)-1] = 0;
    write_likes(overlong);
    assert(home_toggle_like(library[0].path) == -1);
    remove("Playlists/Liked Songs.m3u8");
}

static void put_playlist(const char *path, const char *text)
{
    FILE *f = fopen(disk_path(path), "wb");
    assert(f && fwrite(text, 1, strlen(text), f) == strlen(text));
    assert(!fclose(f));
}

static void expect_playlist(const char *path, const char *expected)
{
    char contents[2048];
    FILE *f = fopen(disk_path(path), "rb");
    assert(f);
    size_t n = fread(contents, 1, sizeof(contents), f);
    assert(!ferror(f) && !fclose(f));
    assert(n == strlen(expected) && !memcmp(contents, expected, n));
    char temp[MAX_PATH + 4];
    snprintf(temp, sizeof(temp), "%s.tmp", path);
    assert(_access(disk_path(temp), 0) != 0);
}

static void test_add_to_playlist(void)
{
    char normalized[MAX_PATH];
    path_remove_dot_segments(normalized, "/Playlists/Nested/../../Music/./seed.mp3");
    assert(!strcmp(normalized, "/Music/seed.mp3"));
    path_remove_dot_segments(normalized, "/../../Music/seed.mp3");
    assert(!strcmp(normalized, "/Music/seed.mp3"));
    _mkdir("Playlists");
    const char *path = "/Playlists/Selected.m3u8";
    const char *original = "\xef\xbb\xbf#EXTM3U\r\n# keep this comment\r\n../Music/seed.mp3\r\n";
    picker_playlist = path;
    put_playlist(path, original);
    picker_result = -1; /* cancel */
    int messages = splash_count, selections = picker_calls;
    assert(!catalog_add_track_to_a_playlist(library[4].path));
    assert(picker_calls == selections + 1 && splash_count == messages);
    expect_playlist(path, original);
    picker_result = -2; /* existing picker reports an empty catalog */
    assert(!catalog_add_track_to_a_playlist(library[4].path));
    assert(last_splash == ID2P(LANG_CATALOG_NO_PLAYLISTS));
    expect_playlist(path, original);
    picker_result = 0;
    catalog_init_result = -1;
    selections = picker_calls;
    assert(!catalog_add_track_to_a_playlist(library[4].path));
    assert(picker_calls == selections);
    catalog_init_result = 0;
    browser_status = CATBROWSE_PLAYLIST;
    assert(!catalog_add_track_to_a_playlist(library[4].path));
    browser_status = 0;

    assert(catalog_add_track_to_a_playlist("/Music/./seed.mp3"));
    assert(last_splash == ID2P(LANG_PLAYLIST_ALREADY_ADDED));
    expect_playlist(path, original);
    short_read = true;
    assert(catalog_add_track_to_a_playlist(library[4].path));
    short_read = false;
    assert(last_splash == ID2P(LANG_PLAYLIST_ADDED));
    const char *added = "\xef\xbb\xbf#EXTM3U\r\n# keep this comment\r\n../Music/seed.mp3\r\n/Music/歌曲 troisième.mp3\n";
    expect_playlist(path, added);
    assert(catalog_add_track_to_a_playlist(library[4].path));
    expect_playlist(path, added);
    put_playlist(path, "#EXTM3U\n/Music/SEED.mp3");
    assert(catalog_add_track_to_a_playlist(library[0].path));
    expect_playlist(path, "#EXTM3U\n/Music/SEED.mp3\n/Music/seed.mp3\n");

    for (int size = 0; size < 3; size++)
    {
        const char *small[] = {"", "#", "#x"};
        char expected[128];
        put_playlist(path, small[size]);
        snprintf(expected, sizeof(expected), "%s%s%s\n", small[size], size ? "\n" : "", library[0].path);
        assert(catalog_add_track_to_a_playlist(library[0].path));
        expect_playlist(path, expected);
    }
    _mkdir("Playlists/Nested");
    picker_playlist = "/Playlists/Nested/Mix.m3u8";
    put_playlist(picker_playlist, "../../Music/./seed.mp3\n");
    assert(catalog_add_track_to_a_playlist(library[0].path));
    assert(last_splash == ID2P(LANG_PLAYLIST_ALREADY_ADDED));
    expect_playlist(picker_playlist, "../../Music/./seed.mp3\n");

    picker_playlist = "/Playlists/Legacy.m3u";
    put_playlist(picker_playlist, "#EXTM3U\r\n../Music/Beyonc\xe9.mp3\r\n");
    assert(catalog_add_track_to_a_playlist("/Music/Beyoncé.mp3"));
    assert(last_splash == ID2P(LANG_PLAYLIST_ALREADY_ADDED));
    expect_playlist(picker_playlist, "#EXTM3U\r\n../Music/Beyonc\xe9.mp3\r\n");
    assert(catalog_add_track_to_a_playlist(library[4].path));
    expect_playlist(picker_playlist, "\xef\xbb\xbf#EXTM3U\r\n../Music/Beyoncé.mp3\r\n/Music/歌曲 troisième.mp3\n");
    assert(catalog_add_track_to_a_playlist("/Music/Beyoncé.mp3")); /* BOM overrides .m3u */
    assert(last_splash == ID2P(LANG_PLAYLIST_ALREADY_ADDED));

    picker_playlist = path;
    for (int failure = 0; failure < 7; failure++)
    {
        put_playlist(path, original);
        fail_open = failure == 0; fail_read = failure == 1;
        fail_write = failure == 2; short_write = failure == 3;
        fail_sync = failure == 4; fail_close = failure == 5; fail_rename = failure == 6;
        assert(!catalog_add_track_to_a_playlist(library[4].path));
        assert(last_splash == ID2P(LANG_PLAYLIST_ADD_FAILED));
        fail_open = fail_read = fail_write = fail_sync = fail_close = fail_rename = 0;
        short_write = false;
        expect_playlist(path, original);
    }
    char oversized[MAX_PATH + 20];
    memset(oversized, 'x', sizeof(oversized)); oversized[sizeof(oversized) - 1] = 0;
    put_playlist(path, oversized);
    assert(!catalog_add_track_to_a_playlist(library[0].path));
    expect_playlist(path, oversized);
    assert(!catalog_add_track_to_a_playlist("/Music/bad\nline.mp3"));
    assert(!catalog_add_track_to_a_playlist(NULL));

    write_likes("#EXTM3U\n/Music/rock.mp3\n");
    assert(home_liked_state(library[0].path) == 0);
    unsigned int revision_before = home_liked_revision();
    int stops = audio_stop_count, flushes = flush_count;
    struct playlist_info queue_before = current_playlist;
    uint32_t score_before = home_score_get(library[0].path, home_score_time());
    picker_playlist = HOME_LIKED_PLAYLIST;
    assert(catalog_add_track_to_a_playlist(library[0].path));
    assert(home_liked_state(library[0].path) == 1);
    assert(home_liked_revision() == revision_before + 1);
    assert(catalog_add_track_to_a_playlist(library[0].path));
    assert(home_liked_state(library[0].path) == 1 && home_liked_revision() == revision_before + 1);
    assert(audio_stop_count == stops && flush_count == flushes);
    assert(!memcmp(&queue_before, &current_playlist, sizeof(queue_before)));
    assert(home_score_get(library[0].path, home_score_time()) == score_before);

    picker_playlist = path;
    picker_changes_track = true;
    for (int order = BUTTON_SELECT; order <= BUTTON_UP; order++)
    {
        put_playlist(path, "#EXTM3U\n");
        strlcpy(playing_id3.path, library[0].path, MAX_PATH);
        held_buttons = BUTTON_SELECT | BUTTON_UP;
        release_step = 0; release_order = order;
        int waits = wait_calls, leaves = wps_leaves;
        assert(run_wps_add_action() == 1);
        assert(release_step == 3 && !held_buttons);
        assert(wait_calls == waits + 1 && wps_leaves == leaves + 1);
        expect_playlist(path, "#EXTM3U\n/Music/seed.mp3\n");
        assert(!strcmp(playing_id3.path, library[2].path));
    }
    shortcut_usb = true;
    held_buttons = BUTTON_SELECT | BUTTON_UP; release_step = 0;
    selections = picker_calls;
    int leaves = wps_leaves;
    assert(run_wps_add_action() == GO_TO_ROOT);
    assert(picker_calls == selections && wps_leaves == leaves + 1);
    shortcut_usb = picker_changes_track = false;
    puts("Add to Playlist: safe append, duplicates, encodings, failures, likes and WPS chord checks passed.");
}

static void test_genre_change(void)
{
    home_autoplay_init();
    large_library_count = 0;
    global_settings.max_files_in_playlist = 100;
    const char *queue = home_autoplay_prepare(library[0].path);
    assert(queue && !playlist_create(".", strrchr(queue, '/') + 1));
    assert(!playlist_set_home_autoplay());
    start_track(0);
    write_likes("#EXTM3U\n/Music/seed.mp3\n/Music/rock.mp3\n");
    int stopped = audio_stop_count, flushed = flush_count;
    assert(playlist_home_change_genre());
    assert(audio_stop_count == stopped && flush_count == flushed + 1);
    assert(!strcmp(home_autoplay_pending_genre(), "Rock"));
    char next[MAX_PATH];
    assert(playlist_check(1) && playlist_peek(1, next, sizeof(next)));
    assert(!strcmp(next, library[2].path));
    assert(!strcmp(queue_paths[current_playlist.index], library[0].path));
    assert(playlist_home_change_genre());
    assert(flush_count == flushed + 1); /* held Left cannot keep rerolling/flushing */
    playing_id3.elapsed = playing_id3.length;
    uint32_t now = home_score_time();
    uint32_t score = home_score_get(playing_id3.path, now);
    emit(PLAYBACK_EVENT_TRACK_FINISH, &playing_id3, TEF_CURRENT | TEF_AUTO_SKIP);
    assert(home_score_get(playing_id3.path, now) == score + 2 * HOME_SCORE_ONE);
    assert(playlist_next(1) == 0);
    assert(!home_autoplay_pending_genre());
    assert(!strcmp(queue_paths[0], library[2].path));
    start_track(0);
    playing_id3.elapsed = 1000;
    assert(playlist_home_change_genre());
    assert(!strcmp(home_autoplay_pending_genre(), "Pop"));
    emit(PLAYBACK_EVENT_TRACK_FINISH, &playing_id3, TEF_CURRENT);
    assert(playlist_next(1) == 0); /* explicit Next also switches */
    assert(!strcmp(queue_paths[0], library[0].path));
    start_track(0);
    assert(playlist_home_change_genre());
    assert(playlist_next(-1) == -1 && !home_autoplay_pending_genre());
    assert(playlist_home_change_genre());
    audio_stop();
    assert(!home_autoplay_pending_genre());
    assert(!playlist_home_change_genre());
    start_track(0);
    assert(playlist_home_change_genre());
    home_autoplay_init();
    assert(!home_autoplay_pending_genre());
    write_likes("#EXTM3U\n/Music/seed.mp3\n/Music/missing.mp3\n");
    assert(!playlist_home_change_genre());
    assert(!home_autoplay_pending_genre());
    write_likes("#EXTM3U\n/Music/rock.mp3\n");
    fail_sync = 1;
    assert(!playlist_home_change_genre());
    fail_sync = 0;
    assert(!home_autoplay_pending_genre());
    assert(!strcmp(queue_paths[current_playlist.index], library[0].path));
    current_playlist.flags = 0;
    assert(!playlist_home_change_genre());
}

int main(void)
{
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    _set_error_mode(_OUT_TO_STDERR);
    char artists[128];
    assert(!strcmp(home_artist_label("A5omic, Tom Ryan, Tom Ryan", artists, sizeof(artists)),
                   "A5omic, Tom Ryan"));
    assert(!strcmp(home_artist_label("Beyoncé, A5omic, Beyoncé", artists, sizeof(artists)),
                   "Beyoncé, A5omic"));
    assert(!strcmp(home_artist_label("A, B, a ", artists, sizeof(artists)), "A, B"));
    assert(!strcmp(home_artist_label("Tyler, The Creator", artists, sizeof(artists)), "Tyler, The Creator"));
    const char *long_credit = "First Artist, Second Artist";
    assert(home_artist_label(long_credit, artists, 4) == long_credit);
    assert(home_artist_label(NULL, artists, sizeof(artists)) == NULL);
    test_sampling_and_refresh();
    test_playback();
    test_scores();
    test_score_malformed();
    test_score_failures();
    test_autoplay();
    test_batches_and_resume();
    test_manually_queued_autoplay();
    test_batch_weights();
    test_batch_failures_and_missing();
    test_library_sized_memory();
    test_likes();
    test_add_to_playlist();
    test_genre_change();
    puts("Home sampling, 20-song autoplay batches, scores, persistence, and repeat checks passed.");
    return 0;
}
