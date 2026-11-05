/* Host adapters; the runner inserts the actual production playlist functions. */
#include <assert.h>
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <stdint.h>
#include <stdlib.h>
#include <windows.h>
#undef MAX_PATH
#define MAX_PATH 260
#include "home_screen.h"
#include "home_autoplay.h"
#include "audio.h"
#include "core_alloc.h"
#include "timefuncs.h"
#include "kernel.h"
#include "action.h"
#undef write
#undef open
#undef fsync
#undef rename
#undef ftruncate

static int fail_write, fail_sync, fail_rename, rename_failure_countdown;
static bool short_write;
int test_open(const char *path, int flags, ...)
{
    return _open(path, flags | O_BINARY, 0666);
}
int test_write(int fd, const void *data, size_t length)
{
    if (fail_write) { errno = ENOSPC; return -1; }
    if (short_write && length > 1) return write(fd, data, length / 2);
    return write(fd, data, length);
}
int test_sync(int fd)
{
    return fail_sync ? -1 : _commit(fd);
}
int test_rename(const char *old, const char *new)
{
    if (rename_failure_countdown > 0 && --rename_failure_countdown == 0) return -1;
    return !fail_rename && MoveFileExA(old, new, MOVEFILE_REPLACE_EXISTING) ? 0 : -1;
}
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
    return _access(path, 0) == 0;
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
void splash(int ticks, const char *message) { (void)ticks; (void)message; splash_count++; }
static void (*callbacks[2])(unsigned short, void *);
bool add_event(unsigned short event, void (*callback)(unsigned short, void *))
{ callbacks[event] = callback; return true; }
static void emit(unsigned short event, struct mp3entry *id3, unsigned flags)
{
    struct track_event te = {.flags = flags, .id3 = id3};
    callbacks[event](event, &te);
}
static int audio_stop_count;
void audio_stop(void) { audio_stop_count++; }
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

/* PRODUCTION_FUNCTIONS */

static void start_track(int index)
{
    struct mp3entry id3;
    current_playlist.index = index;
    assert(get_metadata(&id3, -1, queue_paths[index]));
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

static void test_pins(void)
{
    char pins[HOME_MAX_PINS][MAX_PATH];
    const char *first = "/Playlists/Beyoncé 歌曲.m3u8";
    const char *second = "/Playlists/Second.m3u8";
    assert(home_pins_load(pins) == 0);
    assert(!home_toggle_pin(first));
    assert(!home_toggle_pin(second));
    assert(home_pins_load(pins) == 2);
    assert(!strcmp(pins[0], first) && !strcmp(pins[1], second));
    assert(home_playlist_is_pinned(first));
    fail_write = 1;
    assert(home_toggle_pin(first) < 0);
    fail_write = 0;
    fail_sync = 1;
    assert(home_toggle_pin(first) < 0);
    fail_sync = 0;
    fail_rename = 1;
    assert(home_toggle_pin(first) < 0);
    fail_rename = 0;
    assert(home_pins_load(pins) == 2);
    assert(home_playlist_is_pinned(first));
    assert(!home_toggle_pin(first));
    assert(home_pins_load(pins) == 1 && !strcmp(pins[0], second));
    assert(!home_toggle_pin(first));
    assert(home_pins_load(pins) == 2 && !strcmp(pins[1], first));
    assert(home_toggle_pin("/bad\npath.m3u8") < 0);
    for (int i = 2; i < HOME_MAX_PINS; i++)
    {
        char path[60];
        snprintf(path, sizeof(path), "/Playlists/Mix %d.m3u8", i);
        assert(!home_toggle_pin(path));
    }
    assert(home_toggle_pin("/Playlists/overflow.m3u8") < 0);
    assert(home_pins_load(pins) == HOME_MAX_PINS);
    assert(!home_toggle_pin(first));
    assert(home_pins_load(pins) == HOME_MAX_PINS - 1);
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

static void test_autoplay(void)
{
    home_autoplay_init();
    global_settings.max_files_in_playlist = 16;
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
    char slot;
    assert(sscanf(header, "#HOME-POOL:%c", &slot) == 1);
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

int main(void)
{
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    _set_error_mode(_OUT_TO_STDERR);
    test_sampling_and_refresh();
    test_pins();
    test_playback();
    test_scores();
    test_autoplay();
    test_batches_and_resume();
    test_batch_weights();
    test_batch_failures_and_missing();
    test_library_sized_memory();
    puts("Home sampling, pins, 20-song autoplay batches, scores, persistence, and repeat checks passed.");
    return 0;
}
