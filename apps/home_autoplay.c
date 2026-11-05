/* Genre queues built from Rockbox's existing database and ordinary M3U8 files.
 * SPDX-License-Identifier: GPL-2.0-or-later */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include "home_autoplay.h"
#include "home_screen.h"
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
#define BATCH_SIZE 20
#define POOL_A ROCKBOX_DIR "/home-autoplay-pool-a"
#define POOL_B ROCKBOX_DIR "/home-autoplay-pool-b"
#define POOL_COMMENT "#HOME-POOL:"
#define OFFSET_COMMENT "#HOME-OFFSET:"
static char listening_path[MAX_PATH];
static bool learning, finished, progress_failed;
static const char *pending_queue;
static char pending_genre[MAX_PATH];

static bool mark_played(const char *path);

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
        if (learning && !mark_played(listening_path))
            progress_failed = true;
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
    home_autoplay_cancel_genre();
    progress_failed = false;
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

static const char *pool_name(int slot)
{
    return slot == 'a' ? POOL_A : POOL_B;
}

/* Zero means a legacy queue. Only our two fixed pool paths are accepted. */
static int queue_pool(int fd)
{
    char line[MAX_PATH];
    if (lseek(fd, 0, SEEK_SET) < 0 || read_line(fd, line, sizeof(line)) <= 0)
        return -1;
    size_t prefix = strlen(POOL_COMMENT);
    if (strncmp(line, POOL_COMMENT, prefix))
        return 0;
    return (line[prefix] == 'a' || line[prefix] == 'b') && !line[prefix + 1] ?
           line[prefix] : -1;
}

static int inactive_pool(void)
{
    if (!(playlist_get_current()->flags & PLAYLIST_FLAG_HOME_AUTOPLAY))
        return 'a';
    char queue[MAX_PATH];
    playlist_get_name(NULL, queue, sizeof(queue));
    int fd = open(queue, O_RDONLY);
    if (fd < 0)
        return -1;
    int slot = queue_pool(fd);
    close(fd);
    return slot < 0 ? -1 : slot == 'a' ? 'b' : 'a';
}

static bool write_line_checked(int fd, const char *line)
{
    size_t length = strlen(line);
    return write(fd, line, length) == (ssize_t)length && write(fd, "\n", 1) == 1;
}

/* Pool header: current played marker and newline. Each row is a marker followed
 * by an absolute path. Flip the header after exhaustion instead of rewriting
 * every row. Paths and their byte offsets never change during a session. */
static int pool_marker(int fd)
{
    char header[2];
    return lseek(fd, 0, SEEK_SET) == 0 && read(fd, header, 2) == 2 &&
           (header[0] == '0' || header[0] == '1') && header[1] == '\n' ? header[0] : -1;
}

static int pool_read(int fd, char *row, size_t size)
{
    int n = read_line(fd, row, size);
    if (n > 0 && ((row[0] != '0' && row[0] != '1') || row[1] != '/' ||
                  n != (int)strlen(row) + 1))
        return -1; /* Reject truncated rows, including an unfinished last line. */
    return n;
}

static bool mark_played(const char *path)
{
    char line[MAX_PATH + 1];
    playlist_get_name(NULL, line, sizeof(line));
    int queue = open(line, O_RDONLY);
    if (queue < 0)
        return false;
    int slot = queue_pool(queue), pool = -1;
    bool ok = slot == 0; /* Legacy queues migrate at the next boundary. */
    off_t offset = -1;
    while (slot > 0 && read_line(queue, line, sizeof(line)) > 0)
    {
        if (!strncmp(line, OFFSET_COMMENT, strlen(OFFSET_COMMENT)))
        {
            char *end;
            long value = strtol(line + strlen(OFFSET_COMMENT), &end, 10);
            offset = !*end && value >= 2 && (off_t)value == value ? value : -1;
        }
        else if (line[0] == '/')
        {
            if (!strcmp(line, path) && offset >= 2)
            {
                pool = open(pool_name(slot), O_RDWR);
                int marker = pool >= 0 ? pool_marker(pool) : -1;
                ok = marker >= 0 && lseek(pool, offset, SEEK_SET) == offset &&
                     pool_read(pool, line, sizeof(line)) > 0 && !strcmp(line + 1, path);
                if (ok && line[0] != marker)
                {
                    char played = marker;
                    ok = lseek(pool, offset, SEEK_SET) == offset &&
                         write(pool, &played, 1) == 1 && fsync(pool) == 0;
                }
                break;
            }
            offset = -1;
        }
    }
    if (pool >= 0 && close(pool) < 0) ok = false;
    if (close(queue) < 0) ok = false;
    return ok;
}

/* A max-heap keeps only the best batch, regardless of the genre's size. */
static void keep_track(struct ranked_track *tracks, int *count, int limit,
                       struct ranked_track track)
{
    int i;
    if (*count < limit)
    {
        i = (*count)++;
        while (i > 0 && compare_rank(&track, &tracks[(i - 1) / 2]) > 0)
        {
            tracks[i] = tracks[(i - 1) / 2];
            i = (i - 1) / 2;
        }
    }
    else
    {
        if (compare_rank(&track, tracks) >= 0)
            return;
        i = 0;
        while (2 * i + 1 < *count)
        {
            int child = 2 * i + 1;
            if (child + 1 < *count && compare_rank(&tracks[child + 1], &tracks[child]) > 0)
                child++;
            if (compare_rank(&track, &tracks[child]) >= 0)
                break;
            tracks[i] = tracks[child];
            i = child;
        }
    }
    tracks[i] = track;
}

static const char *write_order(int slot, const char *first, const char *avoid)
{
    int limit = MIN(BATCH_SIZE, global_settings.max_files_in_playlist);
    if (limit <= 0) return NULL;
    int src = open(pool_name(slot), O_RDWR);
    int marker = src >= 0 ? pool_marker(src) : -1;
    int handle = marker >= 0 ? core_alloc(limit * sizeof(struct ranked_track)) : -1;
    if (handle <= 0)
    {
        if (src >= 0) close(src);
        return NULL;
    }
    struct ranked_track *tracks = core_get_data_pinned(handle);
    uint32_t seed = ((uint32_t)rand() << 16) ^ rand(), now = home_score_time();
    char row[MAX_PATH + 1];
    int n = 0;
    bool ok = true;
    for (int pass = 0; pass < 2; pass++)
    {
        int length, playable = 0;
        off_t offset = 2;
        if (lseek(src, offset, SEEK_SET) < 0) { ok = false; break; }
        while ((length = pool_read(src, row, sizeof(row))) > 0)
        {
            if (file_exists(row + 1))
            {
                playable++;
                if (row[0] != marker)
                {
                    uint64_t rank = first && !strcmp(first, row + 1) ? 0 :
                        avoid && !strcmp(avoid, row + 1) ? UINT64_MAX :
                        home_weighted_rank(row + 1, seed, home_score_get(row + 1, now));
                    keep_track(tracks, &n, limit, (struct ranked_track){rank, offset});
                }
            }
            offset += length;
            yield();
        }
        if (length < 0) ok = false;
        if (!ok || n || !playable || pass) break;
        char next_marker = marker == '0' ? '1' : '0';
        ok = lseek(src, 0, SEEK_SET) == 0 && write(src, &next_marker, 1) == 1 && fsync(src) == 0;
        if (!ok) { progress_failed = true; break; }
        marker = next_marker;
    }
    qsort(tracks, n, sizeof(*tracks), compare_rank);
    const char *target = inactive_queue();
    char temp[MAX_PATH];
    snprintf(temp, sizeof(temp), "%s.tmp", target);
    int dst = ok && n ? open(temp, O_WRONLY | O_CREAT | O_TRUNC, 0666) : -1;
    snprintf(row, sizeof(row), POOL_COMMENT "%c", slot);
    ok = dst >= 0 && write_line_checked(dst, row);
    for (int i = 0; ok && i < n; i++)
    {
        snprintf(row, sizeof(row), OFFSET_COMMENT "%ld", (long)tracks[i].offset);
        ok = write_line_checked(dst, row) &&
             lseek(src, tracks[i].offset, SEEK_SET) == tracks[i].offset &&
             pool_read(src, row, sizeof(row)) > 0 && write_line_checked(dst, row + 1);
    }
    if (dst >= 0)
    {
        if (fsync(dst) < 0) ok = false;
        if (close(dst) < 0) ok = false;
    }
    core_put_data_pinned(tracks);
    core_free(handle);
    if (close(src) < 0) ok = false;
    if (ok && rename(temp, target) == 0)
        return target;
    remove(temp);
    return NULL;
}

static bool pool_add(int fd, const char *path)
{
    return write(fd, "1", 1) == 1 && write_line_checked(fd, path);
}

static bool publish_pool(int fd, const char *temp, int slot, bool ok)
{
    if (fd >= 0)
    {
        if (fsync(fd) < 0) ok = false;
        if (close(fd) < 0) ok = false;
    }
    if (ok && rename(temp, pool_name(slot)) == 0)
        return true;
    remove(temp);
    return false;
}

static const char *prepare_queue(const char *path, bool stop)
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
    if (stop) audio_stop();
    int slot = inactive_pool();
    const char *temp = ROCKBOX_DIR "/home-autoplay-source.tmp";
    int fd = slot > 0 ? open(temp, O_WRONLY | O_CREAT | O_TRUNC, 0666) : -1;
    bool ok = fd >= 0 && write_line_checked(fd, "0") && pool_add(fd, path);
    char candidate[MAX_PATH];
    while (ok && tagcache_get_next(&search, candidate, sizeof(candidate)))
    {
        if (!strcmp(path, candidate) || !file_exists(candidate))
            continue;
        ok = pool_add(fd, candidate);
        yield();
    }
    tagcache_search_finish(&search);
    const char *result = publish_pool(fd, temp, slot, ok) ? write_order(slot, path, NULL) : NULL;
    if (result && stop) progress_failed = false;
    if (!result) splash(HZ * 2, ID2P(LANG_FAILED));
    return result;
}

const char *home_autoplay_prepare(const char *path)
{
    return prepare_queue(path, true);
}

struct genre_pick {
    char exclude[MAX_PATH], path[MAX_PATH], genre[MAX_PATH];
    uint32_t seed, now;
    uint64_t rank;
};

static bool pick_genre(const char *path, void *context)
{
    static struct mp3entry id3;
    struct genre_pick *pick = context;
    if (!get_metadata(&id3, -1, path) || !id3.genre_string ||
        !id3.genre_string[0] || strlen(id3.genre_string) >= MAX_PATH ||
        !strcasecmp(id3.genre_string, pick->exclude))
        return true;
    uint64_t rank = home_weighted_rank(path, pick->seed, home_score_get(path, pick->now));
    if (rank < pick->rank)
    {
        pick->rank = rank;
        strlcpy(pick->path, path, sizeof(pick->path));
        strlcpy(pick->genre, id3.genre_string, sizeof(pick->genre));
    }
    return true;
}

bool home_autoplay_active(void)
{
    return (audio_status() & AUDIO_STATUS_PLAY) &&
        (playlist_get_current()->flags & PLAYLIST_FLAG_HOME_AUTOPLAY);
}

bool home_autoplay_request_genre(void)
{
    if (pending_queue) return true;
    if (!home_autoplay_active()) return false;
    const struct mp3entry *current = audio_current_track();
    struct genre_pick pick = {.seed = ((uint32_t)rand() << 16) ^ rand(),
                              .now = home_score_time(), .rank = UINT64_MAX};
    if (!current || !current->genre_string) return false;
    strlcpy(pick.exclude, current->genre_string, sizeof(pick.exclude));
    if (!home_liked_foreach(pick_genre, &pick) || !pick.path[0])
    {
        splash(HZ * 2, ID2P(LANG_HOME_NO_OTHER_GENRE));
        return false;
    }
    const char *queue = prepare_queue(pick.path, false);
    if (!queue) return false;
    strlcpy(pending_genre, pick.genre, sizeof(pending_genre));
    pending_queue = queue; /* Publish only the complete pool and queue. */
    return true;
}

const char *home_autoplay_pending_genre(void)
{
    return pending_queue ? pending_genre : NULL;
}

void home_autoplay_cancel_genre(void)
{
    pending_queue = NULL;
}

const char *home_autoplay_peek_genre(int steps, char *buf, size_t size)
{
    const char *queue = pending_queue;
    if (!queue || steps <= 0) return NULL;
    int fd = open(queue, O_RDONLY);
    if (fd < 0) return NULL;
    char path[MAX_PATH];
    const char *result = NULL;
    while (read_line(fd, path, sizeof(path)) > 0)
        if (path[0] == '/' && --steps == 0)
        {
            if (buf && size) strlcpy(buf, path, size);
            result = buf && size ? buf : "";
            break;
        }
    close(fd);
    return result;
}

int home_autoplay_commit_genre(int steps)
{
    const char *queue = pending_queue;
    if (!queue || !home_autoplay_peek_genre(steps, NULL, 0)) return -1;
    /* playlist_create cancels the pending request, so keep the filename local. */
    if (playlist_create(ROCKBOX_DIR, strrchr(queue, '/') + 1) < 0 ||
        playlist_set_home_autoplay() < 0)
        return -1;
    home_autoplay_cancel_genre();
    playlist_get_current()->index = steps - 1;
    playlist_get_current()->started = true;
    progress_failed = false;
    return steps - 1;
}

int home_autoplay_next_round(void)
{
    if (progress_failed) return -1;
    char source[MAX_PATH], last[MAX_PATH];
    struct playlist_track_info info;
    playlist_get_name(NULL, source, sizeof(source));
    last[0] = 0;
    if (playlist_get_track_info(NULL, playlist_get_current()->index, &info) == 0)
        strlcpy(last, info.filename, sizeof(last));
    int fd = open(source, O_RDONLY);
    if (fd < 0)
        return -1;
    int slot = queue_pool(fd);
    if (slot == 0)
    {
        /* The old full queue is also the complete genre pool. Adopt it once. */
        slot = 'a';
        const char *temp = ROCKBOX_DIR "/home-autoplay-source.tmp";
        int pool = open(temp, O_WRONLY | O_CREAT | O_TRUNC, 0666);
        bool ok = pool >= 0 && write_line_checked(pool, "0") && lseek(fd, 0, SEEK_SET) == 0;
        int length = 0;
        while (ok && (length = read_line(fd, source, sizeof(source))) > 0)
            if (source[0] == '/' && file_exists(source)) ok = pool_add(pool, source);
        if (!publish_pool(pool, temp, slot, ok && length >= 0)) slot = -1;
    }
    if (close(fd) < 0) slot = -1;
    const char *next = slot > 0 ? write_order(slot, NULL, last) : NULL;
    if (!next || playlist_create(ROCKBOX_DIR, strrchr(next, '/') + 1) < 0 ||
        playlist_set_home_autoplay() < 0)
        return -1;
    playlist_get_current()->started = true;
    return 0;
}
