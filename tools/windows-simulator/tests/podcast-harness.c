#include <assert.h>
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#ifdef _WIN32
#include <io.h>
#include <direct.h>
#include <windows.h>
#define podcast_mkdir _mkdir
#else
#define podcast_mkdir mkdir
#endif

#include "podcasts.h"

int podcast_test_fail_write;
int podcast_test_fail_sync;
int podcast_test_fail_rename;
long podcast_test_tick;
int podcast_test_single_track_calls;
int podcast_test_audio_pause_calls;
int podcast_test_audio_resume_calls;
int podcast_test_started_elapsed;
int podcast_test_audio_seek_calls;
long podcast_test_audio_elapsed_value = 4000;
int podcast_test_settings_repeat = 2;
int podcast_test_settings_shuffle = 1;

static podcast_test_event_handler event_handlers[3];

static const char *local_path(const char *path)
{
    static char local[512];
    assert(path[0] == '/');
    snprintf(local, sizeof(local), ".%s", path);
    return local;
}

int podcast_test_open(const char *path, int flags)
{
    return open(local_path(path), flags, 0666);
}

int podcast_test_read(int fd, void *data, size_t size)
{
    return (int)read(fd, data, size);
}

int podcast_test_write(int fd, const void *data, size_t size)
{
    if (podcast_test_fail_write)
    {
        errno = ENOSPC;
        return -1;
    }
    return (int)write(fd, data, size);
}

int podcast_test_close(int fd) { return close(fd); }

int podcast_test_sync(int fd)
{
    if (podcast_test_fail_sync)
    {
        errno = EIO;
        return -1;
    }
#ifdef _WIN32
    return _commit(fd);
#else
    return fsync(fd);
#endif
}

int podcast_test_rename(const char *old_path, const char *new_path)
{
    if (podcast_test_fail_rename)
    {
        errno = EIO;
        return -1;
    }
    char from[512], to[512];
    snprintf(from, sizeof(from), "%s", local_path(old_path));
    snprintf(to, sizeof(to), "%s", local_path(new_path));
#ifdef _WIN32
    return MoveFileExA(from, to, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) ? 0 : -1;
#else
    return rename(from, to);
#endif
}

int podcast_test_remove(const char *path) { return remove(local_path(path)); }

int podcast_test_mkdir(const char *path)
{
    return podcast_mkdir(local_path(path));
}

long podcast_test_now(void) { return podcast_test_tick; }

void podcast_test_audio_pause(void) { podcast_test_audio_pause_calls++; }
void podcast_test_audio_resume(void) { podcast_test_audio_resume_calls++; }
void podcast_test_audio_stop(void) {}
int podcast_test_audio_status(void) { return 1; }
long podcast_test_audio_elapsed(void) { return podcast_test_audio_elapsed_value; }
void podcast_test_audio_seek(long elapsed)
{
    podcast_test_audio_seek_calls++;
    podcast_test_started_elapsed = (int)elapsed;
}
int podcast_test_playlist_create(void) { return 0; }
int podcast_test_playlist_insert(const char *path) { (void)path; return 0; }
void podcast_test_playlist_sync(void) {}
int podcast_test_playlist_set_single_track(void)
{
    podcast_test_single_track_calls++;
    return 0;
}
void podcast_test_playlist_start(long elapsed) { podcast_test_started_elapsed = (int)elapsed; }
int podcast_test_file_exists(const char *path) { (void)path; return 1; }

void podcast_test_add_event(unsigned short id, podcast_test_event_handler handler)
{
    assert(id < sizeof(event_handlers) / sizeof(event_handlers[0]));
    event_handlers[id] = handler;
}

static void emit_track_event(unsigned short id, const char *path,
                             unsigned int flags, unsigned long elapsed,
                             unsigned long length)
{
    struct mp3entry id3 = {0};
    struct track_event event = {.flags = flags, .id3 = &id3};
    snprintf(id3.path, sizeof(id3.path), "%s", path);
    id3.elapsed = elapsed;
    id3.length = length;
    assert(event_handlers[id]);
    event_handlers[id](id, &event);
}

static void make_parent_dirs(void)
{
    assert(!podcast_mkdir(".rockbox") || errno == EEXIST);
    assert(!podcast_mkdir(".rockbox/podcasts") || errno == EEXIST);
    assert(!podcast_mkdir(".rockbox/podcasts/resume") || errno == EEXIST);
}

static void write_file(const char *path, const void *data, size_t size)
{
    FILE *file = fopen(local_path(path), "wb");
    assert(file);
    assert(fwrite(data, 1, size, file) == size);
    assert(!fclose(file));
}

static void write_resume(const char *id, const char *row)
{
    char path[256];
    snprintf(path, sizeof(path), "/.rockbox/podcasts/resume/%s.tsv", id);
    write_file(path, row, strlen(row));
}

static void test_catalog(void)
{
    const char catalog[] =
        "ROCKBOX-PODCASTS\t1\t2026-11-02\t2026-11-08\n"
        "C\tz\tZulu\n"
        "C\ta\tAlpha\n"
        "C\te\tEmpty\n"
        "\n"
        "E\tz\tz2\tSecond\t20\t90000\t/<microSD1>/Podcasts/RockboxManager/z2.mp3\n"
        "E\ta\ta2\tLater%09episode\t10\t10000\t/<microSD1>/Podcasts/RockboxManager/a2.mp3\n"
        "E\ta\ta1\tFirst\t5\t10000\t/<microSD1>/Podcasts/RockboxManager/a1.mp3\n"
        "E\te\te1\tNo file\t1\t1000\t/<microSD1>/Podcasts/RockboxManager/not-e1.mp3\n"
        "E\tmissing\tx\tUnknown channel\t1\t1\t/<microSD1>/Podcasts/RockboxManager/x.mp3\n"
        "E\ta\tbad/id\tBad ID\t1\t1\t/<microSD1>/Podcasts/RockboxManager/bad/id.mp3\n"
        "E\ta\tbadpath\tBad path\t1\t1\t/<microSD1>/Podcasts/RockboxManager/other.mp3\n"
        "E\ta\ta3\tBad%2Fescape\t2\t1\t/<microSD1>/Podcasts/RockboxManager/a3.mp3\n"
        "E\ta\ta4\tBad\xc0\xaftitle\t3\t1\t/<microSD1>/Podcasts/RockboxManager/a4.mp3\n"
        "E\ta\ta1\tDuplicate\t1\t1\t/<microSD1>/Podcasts/RockboxManager/a1.mp3\n";
    write_file("/.rockbox/podcasts/catalog.tsv", catalog, sizeof(catalog) - 1);
    write_resume("a1", "ROCKBOX-PODCAST-RESUME\t1\ta1\t99999\t0\n");
    write_resume("a2", "ROCKBOX-PODCAST-RESUME\t1\ta2\t5000\t1\n");

    assert(podcast_catalog_load());
    assert(podcast_channel_count() == 3);
    assert(!strcmp(podcast_channel_at(0)->name, "Alpha"));
    assert(!strcmp(podcast_channel_at(1)->name, "Empty"));
    assert(!strcmp(podcast_channel_at(2)->name, "Zulu"));
    assert(podcast_episode_count(0) == 2);
    assert(!strcmp(podcast_episode_at(0, 0)->id, "a1"));
    assert(!strcmp(podcast_episode_at(0, 1)->id, "a2"));
    assert(podcast_episode_count(1) == 0);
    assert(podcast_episode_status(0, 0) == PODCAST_EPISODE_SAVED);
    assert(podcast_episode_resume(0, 0) == 10000);
    assert(podcast_episode_status(0, 1) == PODCAST_EPISODE_PLAYED);
    assert(podcast_episode_resume(0, 1) == 0);
    assert(podcast_episode_status(1, 0) == PODCAST_EPISODE_NEW);
    char label[128];
    assert(!strcmp(podcast_episode_label(0, 0, label, sizeof(label)),
                   "Saved 0:10: First"));
    assert(!strcmp(podcast_episode_label(1, 0, label, sizeof(label)), "No episodes"));
}

static void test_atomic_resume_and_playback(void)
{
    char before[256], after[256];
    const char *path = "/.rockbox/podcasts/resume/a1.tsv";
    FILE *file = fopen(local_path(path), "rb");
    assert(file);
    size_t before_size = fread(before, 1, sizeof(before), file);
    assert(!fclose(file));

    for (int failure = 0; failure < 3; failure++)
    {
        podcast_test_fail_write = failure == 0;
        podcast_test_fail_sync = failure == 1;
        podcast_test_fail_rename = failure == 2;
        assert(!podcast_episode_save(0, 0, 7000, false));
        podcast_test_fail_write = podcast_test_fail_sync = podcast_test_fail_rename = 0;
        assert(podcast_save_failed());
        file = fopen(local_path(path), "rb");
        assert(file);
        size_t after_size = fread(after, 1, sizeof(after), file);
        assert(!fclose(file));
        assert(before_size == after_size && !memcmp(before, after, before_size));
    }

    assert(podcast_episode_save(0, 0, 4000, false));
    assert(podcast_catalog_load());
    assert(podcast_episode_resume(0, 0) == 4000);
    podcast_test_single_track_calls = 0;
    podcast_test_started_elapsed = 0;
    assert(podcast_start_episode(0, 0));
    assert(podcast_test_single_track_calls == 1);
    assert(podcast_test_started_elapsed == 4000);
    assert(podcast_test_settings_repeat == 2);
    assert(podcast_test_settings_shuffle == 1);

    podcast_test_tick = 0;
    podcast_test_audio_pause_calls = 0;
    podcast_test_audio_resume_calls = 0;
    podcast_test_audio_seek_calls = 0;
    assert(podcast_wps_action(PODCAST_ACTION_SKIPNEXT));
    char feedback[64];
    assert(podcast_scan_feedback(feedback, sizeof(feedback)) && strstr(feedback, ">5x"));
    assert(podcast_test_audio_pause_calls == 1);
    podcast_test_tick = 1000;
    podcast_wps_checkpoint();
    assert(podcast_test_audio_seek_calls == 0);
    assert(podcast_test_started_elapsed == 4000);
    assert(podcast_wps_action(PODCAST_ACTION_SKIPNEXT));
    assert(podcast_wps_action(PODCAST_ACTION_SEEKFWD));
    assert(podcast_wps_action(PODCAST_ACTION_PLAY));
    assert(podcast_test_audio_resume_calls == 1);
    assert(podcast_test_audio_seek_calls == 1);
    assert(podcast_test_started_elapsed == 9000);
    assert(!podcast_scan_feedback(feedback, sizeof(feedback)));
}

static void test_event_hooks(void)
{
    const char saved[] = "ROCKBOX-PODCAST-RESUME\t1\ta1\t1000\t0\n";
    podcast_catalog_reset();
    write_resume("a1", saved);
    assert(podcast_catalog_load());

    assert(podcast_start_episode(0, 0));
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT, 1000, 10000);
    emit_track_event(PODCAST_TEST_TRACK_FINISH,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT | PODCAST_TEST_TEF_AUTO_SKIP,
                     10000, 10000);
    assert(podcast_episode_status(0, 0) == PODCAST_EPISODE_PLAYED);
    assert(podcast_episode_resume(0, 0) == 0);

    write_resume("a1", saved);
    assert(podcast_catalog_load());
    assert(podcast_start_episode(0, 0));
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT, 1000, 10000);
    emit_track_event(PODCAST_TEST_TRACK_FINISH,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT, 4000, 10000);
    assert(podcast_episode_status(0, 0) == PODCAST_EPISODE_SAVED);
    assert(podcast_episode_resume(0, 0) == 4000);

    podcast_catalog_reset();
    write_resume("a1", saved);
    assert(podcast_catalog_load());
    assert(podcast_start_episode(0, 0));
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT, 1000, 10000);
    emit_track_event(PODCAST_TEST_TRACK_FINISH,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT | PODCAST_TEST_TEF_AUTO_SKIP,
                     8500, 10000);
    assert(podcast_episode_status(0, 0) == PODCAST_EPISODE_SAVED);
    assert(podcast_episode_resume(0, 0) == 8500);

    assert(podcast_start_episode(0, 0));
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT, 1000, 10000);
    emit_track_event(PODCAST_TEST_TRACK_FINISH,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT | PODCAST_TEST_TEF_REWIND,
                     5000, 10000);
    assert(podcast_episode_status(0, 0) == PODCAST_EPISODE_SAVED);
    assert(podcast_episode_resume(0, 0) == 5000);

    podcast_catalog_reset();
    write_resume("a1", saved);
    assert(podcast_catalog_load());
    assert(podcast_start_episode(0, 0));
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT, 1000, 10000);
    podcast_test_fail_write = 1;
    emit_track_event(PODCAST_TEST_TRACK_FINISH,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT | PODCAST_TEST_TEF_AUTO_SKIP,
                     10000, 10000);
    podcast_test_fail_write = 0;
    assert(podcast_save_failed());
    assert(podcast_episode_status(0, 0) == PODCAST_EPISODE_SAVED);
    assert(podcast_episode_resume(0, 0) == 1000);

    podcast_catalog_reset();
    assert(podcast_catalog_load());
    assert(podcast_start_episode(0, 0));
    podcast_test_audio_elapsed_value = 6000;
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT, 6000, 10000);
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/z2.mp3",
                     PODCAST_TEST_TEF_CURRENT, 0, 90000);
    assert(podcast_episode_resume(0, 0) == 6000);

    const char reordered[] =
        "ROCKBOX-PODCASTS\t1\t2026-11-02\t2026-11-08\n"
        "C\tz\tAardvark\n"
        "C\ta\tZebra\n"
        "E\tz\tz2\tSecond\t20\t90000\t/<microSD1>/Podcasts/RockboxManager/z2.mp3\n"
        "E\ta\ta1\tFirst\t5\t10000\t/<microSD1>/Podcasts/RockboxManager/a1.mp3\n"
        "E\ta\ta2\tLater\t10\t10000\t/<microSD1>/Podcasts/RockboxManager/a2.mp3\n";
    write_file("/.rockbox/podcasts/catalog.tsv", reordered, sizeof(reordered) - 1);
    assert(podcast_start_episode(0, 0));
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT, 3000, 10000);
    assert(podcast_catalog_load());
    emit_track_event(PODCAST_TEST_TRACK_FINISH,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT | PODCAST_TEST_TEF_AUTO_SKIP,
                     10000, 10000);
    assert(podcast_episode_status(1, 0) == PODCAST_EPISODE_PLAYED);

    podcast_catalog_reset();
    assert(podcast_init());
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT, 0, 10000);
    emit_track_event(PODCAST_TEST_TRACK_FINISH,
                     "/<microSD1>/Podcasts/RockboxManager/a1.mp3",
                     PODCAST_TEST_TEF_CURRENT | PODCAST_TEST_TEF_AUTO_SKIP,
                     10000, 10000);
    assert(podcast_episode_status(1, 0) == PODCAST_EPISODE_PLAYED);

    podcast_catalog_reset();
    assert(podcast_init());
    podcast_test_tick = 1000;
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/z2.mp3",
                     PODCAST_TEST_TEF_CURRENT, 0, 90000);
    podcast_test_audio_elapsed_value = 35000;
    podcast_test_tick = 31000;
    podcast_wps_checkpoint();
    podcast_catalog_reset();
    assert(podcast_catalog_load());
    assert(podcast_episode_resume(0, 0) == 35000);
}

static void test_scan_and_music_regression(void)
{
    struct podcast_scan_state scan;
    podcast_scan_init(&scan, 200000, 100000);
    assert(podcast_scan_tap(&scan, 1, 0));
    assert(scan.active && scan.direction == 1 && scan.speed == 5);
    podcast_scan_tick(&scan, 1000);
    assert(scan.target_ms == 105000);
    assert(podcast_scan_tap(&scan, 1, 1500) && scan.speed == 10);
    assert(scan.target_ms == 107500);
    podcast_scan_tick(&scan, 2000);
    assert(scan.target_ms == 112500);
    assert(podcast_scan_tap(&scan, 1, 2000) && scan.speed == 15);
    assert(podcast_scan_tap(&scan, 1, 3000) && scan.speed == 5);
    assert(podcast_scan_tap(&scan, -1, 3000));
    assert(scan.direction == -1 && scan.speed == 5);
    podcast_scan_tick(&scan, 10000);
    assert(scan.target_ms == 92500);
    podcast_scan_tick(&scan, 100000);
    assert(scan.target_ms == 0);
    podcast_scan_commit(&scan, false);
    assert(!scan.active && scan.target_ms == 0);

    podcast_catalog_reset();
    podcast_test_audio_pause_calls = 0;
    assert(!podcast_wps_action(PODCAST_ACTION_SKIPNEXT));
    assert(!podcast_wps_action(PODCAST_ACTION_SEEKFWD));
    assert(!podcast_test_audio_pause_calls);
}

int main(void)
{
    make_parent_dirs();
    test_catalog();
    assert(podcast_init());
    podcast_test_tick = 1000;
    emit_track_event(PODCAST_TEST_TRACK_CHANGE,
                     "/<microSD1>/Podcasts/RockboxManager/z2.mp3",
                     PODCAST_TEST_TEF_CURRENT, 0, 90000);
    podcast_test_audio_elapsed_value = 35000;
    podcast_test_tick = 31000;
    podcast_wps_checkpoint();
    podcast_catalog_reset();
    assert(podcast_catalog_load());
    assert(podcast_episode_resume(2, 0) == 35000);
    podcast_test_audio_elapsed_value = 4000;
    test_atomic_resume_and_playback();
    test_event_hooks();
    test_scan_and_music_regression();
    return 0;
}
