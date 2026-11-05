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
#include "home_screen.h"
#include "kernel.h"
#include "action.h"
#undef write
#undef open
#undef fsync
#undef rename

static int fail_write, fail_sync, fail_rename;
int test_open(const char *path, int flags, ...)
{
    return _open(path, flags | O_BINARY, 0666);
}
int test_write(int fd, const void *data, size_t length)
{
    if (fail_write) { errno = ENOSPC; return -1; }
    return write(fd, data, length);
}
int test_sync(int fd)
{
    return fail_sync ? -1 : _commit(fd);
}
int test_rename(const char *old, const char *new)
{
    return !fail_rename && MoveFileExA(old, new, MOVEFILE_REPLACE_EXISTING) ? 0 : -1;
}
size_t strlcpy(char *dest, const char *src, size_t size)
{
    size_t length = strlen(src);
    if (size) { size_t n = length < size - 1 ? length : size - 1; memcpy(dest, src, n); dest[n] = 0; }
    return length;
}

enum { REPEAT_OFF, REPEAT_ALL, REPEAT_ONE, REPEAT_SHUFFLE, REPEAT_AB };
#define AB_REPEAT_ENABLE
#define PLAYLIST_FLAG_MODIFIED (1u << 0)
#define PLAYLIST_FLAG_DIRPLAY (1u << 1)
#define PLAYLIST_FLAG_SINGLE_TRACK (1u << 2)
#define PLAYLIST_QUEUED 0x100u
#define PLAYLIST_SKIPPED 0x200u
#define PLAYLIST_COMMAND_RESET 6
#define PLAYLIST_COMMAND_FLAGS 7
struct playlist_info {
    int amount, index, first_index, last_insert_pos, control_fd;
    unsigned int flags;
    unsigned long *indices;
    bool started;
};
static unsigned long indices[4];
static struct playlist_info current_playlist;
static struct { int repeat_mode, next_folder; bool playlist_shuffle; } global_settings;
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

/* PRODUCTION_FUNCTIONS */

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

int main(void)
{
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
    _set_error_mode(_OUT_TO_STDERR);
    test_sampling_and_refresh();
    test_pins();
    test_playback();
    puts("Home sampling, refresh, navigation, pins, and repeat-mode checks passed.");
    return 0;
}
