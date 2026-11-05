/* Genre queues built from Rockbox's existing database and ordinary M3U8 files.
 * SPDX-License-Identifier: GPL-2.0-or-later */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "home_autoplay.h"
#include "appevents.h"
#include "audio.h"
#include "core_alloc.h"
#include "file.h"
#include "kernel.h"
#include "lang.h"
#include "metadata.h"
#include "misc.h"
#include "playlist.h"
#include "settings.h"
#include "splash.h"
#include "string-extra.h"
#include "tagcache.h"

struct ranked_track { uint64_t rank; off_t offset; };
static char listening_path[MAX_PATH];
static bool learning, finished;

static void listening_event(unsigned short event, void *data)
{
    struct track_event *te = data;
    if (!(te->flags & TEF_CURRENT))
        return;
    if (event == PLAYBACK_EVENT_TRACK_CHANGE)
    {
        learning = !!(playlist_get_current()->flags & PLAYLIST_FLAG_HOME_AUTOPLAY);
        finished = false;
        strlcpy(listening_path, te->id3->path, sizeof(listening_path));
    }
    else if (learning && !finished && !(te->flags & TEF_REWIND) &&
             !strcmp(listening_path, te->id3->path))
    {
        finished = true;
        /* Decoder errors also advance automatically; don't call an early error
         * a full listen. Normal codec rounding is allowed one second. */
        bool complete = (te->flags & TEF_AUTO_SKIP) && te->id3->elapsed > 0 &&
            (!te->id3->length || te->id3->elapsed >= te->id3->length - MIN(te->id3->length, 1000ul));
        home_score_record(listening_path, te->id3->elapsed, te->id3->length,
                          complete, home_score_time());
    }
}

void home_autoplay_init(void)
{
    home_scores_reload();
    add_event(PLAYBACK_EVENT_TRACK_CHANGE, listening_event);
    add_event(PLAYBACK_EVENT_TRACK_FINISH, listening_event);
}

static int compare_rank(const void *a, const void *b)
{
    const struct ranked_track *x = a, *y = b;
    if (x->rank != y->rank)
        return x->rank < y->rank ? -1 : 1;
    return (x->offset > y->offset) - (x->offset < y->offset);
}

static const char *inactive_queue(void)
{
    char active[MAX_PATH];
    playlist_get_name(NULL, active, sizeof(active));
    return !strcmp(active, HOME_AUTOPLAY_A) ? HOME_AUTOPLAY_B : HOME_AUTOPLAY_A;
}

/* Two passes over paths, with only 16 bytes per matching song held temporarily.
 * The active playlist is never rewritten. Its exact order remains resumable. */
static const char *write_order(int src, const char *first, const char *avoid)
{
    char path[MAX_PATH];
    int count = 0;
    lseek(src, 0, SEEK_SET);
    while (read_line(src, path, sizeof(path)) > 0)
        if (path[0] == '/' && file_exists(path)) count++;
    if (!count || count > global_settings.max_files_in_playlist)
        return NULL;
    int handle = core_alloc(count * sizeof(struct ranked_track));
    if (handle <= 0)
        return NULL;
    struct ranked_track *tracks = core_get_data_pinned(handle);
    uint32_t seed = ((uint32_t)rand() << 16) ^ rand(), now = home_score_time();
    lseek(src, 0, SEEK_SET);
    int n = 0;
    off_t offset = 0;
    while (read_line(src, path, sizeof(path)) > 0)
    {
        off_t next = lseek(src, 0, SEEK_CUR);
        if (path[0] == '/' && file_exists(path) && n < count)
        {
            tracks[n++] = (struct ranked_track){
                first && !strcmp(first, path) ? 0 :
                home_weighted_rank(path, seed, home_score_get(path, now)), offset};
        }
        offset = next;
    }
    qsort(tracks, n, sizeof(*tracks), compare_rank);
    if (avoid && n > 1)
    {
        lseek(src, tracks[0].offset, SEEK_SET);
        read_line(src, path, sizeof(path));
        if (!strcmp(path, avoid))
        {
            struct ranked_track swap = tracks[0];
            tracks[0] = tracks[1]; tracks[1] = swap;
        }
    }
    const char *target = inactive_queue();
    char temp[MAX_PATH];
    snprintf(temp, sizeof(temp), "%s.tmp", target);
    int dst = open(temp, O_WRONLY | O_CREAT | O_TRUNC, 0666);
    bool ok = dst >= 0 && n > 0;
    for (int i = 0; ok && i < n; i++)
    {
        lseek(src, tracks[i].offset, SEEK_SET);
        ok = read_line(src, path, sizeof(path)) > 0;
        size_t length = strlen(path);
        if (ok) ok = write(dst, path, length) == (ssize_t)length && write(dst, "\n", 1) == 1;
    }
    if (dst >= 0)
    {
        if (fsync(dst) < 0) ok = false;
        if (close(dst) < 0) ok = false;
    }
    core_put_data_pinned(tracks);
    core_free(handle);
    if (ok && rename(temp, target) == 0)
        return target;
    remove(temp);
    return NULL;
}

const char *home_autoplay_prepare(const char *path)
{
    static struct mp3entry id3;
    static struct tagcache_search search;
    if (!tagcache_is_usable())
    {
        splash(HZ * 3, ID2P(LANG_HOME_DATABASE));
        return NULL;
    }
    if (!get_metadata(&id3, -1, path) || !id3.genre_string || !*id3.genre_string)
    {
        splash(HZ * 2, ID2P(LANG_FAILED));
        return NULL;
    }
    if (!tagcache_search(&search, tag_filename))
    {
        splash(HZ * 2, ID2P(LANG_TAGCACHE_BUSY));
        return NULL;
    }
    struct tagcache_search_clause genre = {
        .tag = tag_genre, .type = clause_is, .str = id3.genre_string
    };
    tagcache_search_add_clause(&search, &genre);
    /* Stop only after the database and seed are validated. This also finishes
     * the old listening record before ranking, and serializes queue writers
     * with the audio thread's end-of-pass transition. */
    audio_stop();
    const char *temp = ROCKBOX_DIR "/home-autoplay-source.tmp";
    int fd = open(temp, O_RDWR | O_CREAT | O_TRUNC, 0666);
    bool ok = fd >= 0;
    char candidate[MAX_PATH];
    size_t length = strlen(path);
    if (ok) ok = write(fd, path, length) == (ssize_t)length && write(fd, "\n", 1) == 1;
    while (ok && tagcache_get_next(&search, candidate, sizeof(candidate)))
    {
        if (!strcmp(path, candidate) || !file_exists(candidate))
            continue;
        length = strlen(candidate);
        ok = write(fd, candidate, length) == (ssize_t)length && write(fd, "\n", 1) == 1;
        yield();
    }
    tagcache_search_finish(&search);
    const char *result = ok ? write_order(fd, path, NULL) : NULL;
    if (fd >= 0) close(fd);
    remove(temp);
    if (!result) splash(HZ * 2, ID2P(LANG_FAILED));
    return result;
}

int home_autoplay_next_round(void)
{
    char source[MAX_PATH], last[MAX_PATH];
    struct playlist_track_info info;
    playlist_get_name(NULL, source, sizeof(source));
    last[0] = 0;
    if (playlist_get_track_info(NULL, playlist_get_current()->index, &info) == 0)
        strlcpy(last, info.filename, sizeof(last));
    int fd = open(source, O_RDONLY);
    if (fd < 0)
        return -1;
    const char *next = write_order(fd, NULL, last);
    close(fd);
    if (!next || playlist_create(ROCKBOX_DIR, strrchr(next, '/') + 1) < 0 ||
        playlist_set_home_autoplay() < 0)
        return -1;
    playlist_get_current()->started = true;
    return 0;
}
