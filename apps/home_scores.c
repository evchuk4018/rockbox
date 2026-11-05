/* Small append-only listening store; latest row per path wins.
 * SPDX-License-Identifier: GPL-2.0-or-later */
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "config.h"
#include "home_autoplay.h"
#include "core_alloc.h"
#include "file.h"
#include "fixedpoint.h"
#include "kernel.h"
#include "misc.h"
#include "timefuncs.h"

/* Paths stay on disk. Hash collisions are resolved by comparing the path. */
struct score_entry {
    uint32_t hash, score, updated, full, late, early;
    off_t offset;
};
static int score_handle, score_count, score_capacity, score_rows;
static off_t incomplete_offset = -1;
static bool loaded, save_failed, index_complete;
static struct mutex score_mutex;
#define SCORE_LINE (MAX_PATH * 3 + 100)

static uint32_t hash_path(const char *path, uint32_t seed)
{
    uint32_t h = 2166136261u ^ seed;
    while (*path)
        h = (h ^ (unsigned char)*path++) * 16777619u;
    h ^= h >> 16;
    h *= 0x7feb352du;
    h ^= h >> 15;
    h *= 0x846ca68bu;
    return h ^ (h >> 16);
}

uint32_t home_score_time(void)
{
    struct tm *tm = get_time();
    time_t now = valid_time(tm) ? mktime(tm) : 0;
    return now > 0 ? (uint32_t)now : 0;
}

uint32_t home_score_decay(uint32_t score, uint32_t then, uint32_t now)
{
    if (!then || now <= then)
        return score;
    uint32_t age = now - then, months = age / (30u * 86400u);
    if (months >= 32)
        return 0;
    score >>= months;
    /* Q31 per-second factor: 2^(-1 / 2592000). At most 22 squaring steps. */
    uint32_t factor = 2147483074u, scale = 0x80000000u;
    for (age %= 30u * 86400u; age; age >>= 1)
    {
        if (age & 1)
            scale = (uint64_t)scale * factor >> 31;
        factor = (uint64_t)factor * factor >> 31;
    }
    return (uint64_t)score * scale >> 31;
}

uint64_t home_weighted_rank(const char *path, uint32_t seed, uint32_t score)
{
    /* Exponential-race sampling: -ln(U)/(1+score)^2. Fixed-point only.
     * Q8 weights retain fractional preferences without overflowing uint64_t. */
    uint32_t u = 1u + (hash_path(path, seed) >> 16);
    uint64_t base = 256u + (score >> 8);
    uint64_t log = 1u + (uint32_t)-fp16_log(u);
    return 1u + (log << 40) / (base * base);
}

int home_listen_points(uint32_t elapsed, uint32_t length, bool completed)
{
    return completed ? 2 : length && elapsed >= length / 2 + length % 2;
}

/* Escape only TSV delimiters and '%'; paths otherwise remain readable UTF-8. */
static void escape_path(char *out, const char *path)
{
    const char hex[] = "0123456789ABCDEF";
    while (*path)
    {
        unsigned char c = *path++;
        if (c == '%' || c == '\t' || c == '\r' || c == '\n')
        {
            *out++ = '%'; *out++ = hex[c >> 4]; *out++ = hex[c & 15];
        }
        else
            *out++ = c;
    }
    *out = 0;
}

static bool parse_row(char *line, struct score_entry *entry, char *path)
{
    unsigned long score, updated, full, late, early;
    int n = 0;
    if (sscanf(line, "%lu\t%lu\t%lu\t%lu\t%lu\t%n",
               &score, &updated, &full, &late, &early, &n) != 5 || !n)
        return false;
    char *src = line + n, *out = path;
    while (*src && out < path + MAX_PATH - 1)
    {
        if (*src == '%')
        {
            if (!strncmp(src, "%25", 3)) *out++ = '%';
            else if (!strncmp(src, "%09", 3)) *out++ = '\t';
            else if (!strncmp(src, "%0A", 3)) *out++ = '\n';
            else if (!strncmp(src, "%0D", 3)) *out++ = '\r';
            else return false;
            src += 3;
        }
        else
            *out++ = *src++;
    }
    *out = 0;
    if (*src || path[0] != '/')
        return false;
    *entry = (struct score_entry){hash_path(path, 0), score, updated,
                                  full, late, early, 0};
    return true;
}

static bool reserve_entry(void)
{
    if (score_count < score_capacity)
        return true;
    int capacity = score_capacity ? score_capacity * 2 : 32;
    int handle = core_alloc(capacity * sizeof(struct score_entry));
    if (handle <= 0)
        return false;
    if (score_handle > 0)
    {
        memcpy(core_get_data(handle), core_get_data(score_handle),
               score_count * sizeof(struct score_entry));
        core_free(score_handle);
    }
    score_handle = handle;
    score_capacity = capacity;
    return true;
}

static int find_entry(int fd, const char *path)
{
    bool own_fd = fd < 0;
    uint32_t hash = hash_path(path, 0);
    for (int i = 0; i < score_count; i++)
    {
        struct score_entry entry = ((struct score_entry *)core_get_data(score_handle))[i];
        if (entry.hash != hash)
            continue;
        if (fd < 0) fd = open(HOME_SCORES_FILE, O_RDONLY);
        char line[SCORE_LINE], saved[MAX_PATH];
        if (fd >= 0 && lseek(fd, entry.offset, SEEK_SET) >= 0 &&
            read_line(fd, line, sizeof(line)) > 0 &&
            parse_row(line, &entry, saved) && !strcmp(saved, path))
        {
            if (own_fd) close(fd);
            return i;
        }
    }
    if (own_fd && fd >= 0) close(fd);
    return -1;
}

void home_scores_reload(void)
{
    if (!loaded)
        mutex_init(&score_mutex);
    mutex_lock(&score_mutex);
    loaded = true;
    if (score_handle > 0)
        core_free(score_handle);
    score_handle = score_count = score_capacity = score_rows = 0;
    incomplete_offset = -1;
    index_complete = true;
    int fd = open(HOME_SCORES_FILE, O_RDONLY);
    if (fd < 0 && errno != ENOENT) index_complete = false;
    if (fd >= 0)
    {
        char line[SCORE_LINE], path[MAX_PATH];
        off_t offset = 0;
        int length;
        while ((length = read_line(fd, line, sizeof(line))) > 0)
        {
            off_t next = lseek(fd, 0, SEEK_CUR);
            struct score_entry entry;
            /* Ignore an incomplete final append. */
            char last;
            lseek(fd, next - 1, SEEK_SET);
            bool complete = read(fd, &last, 1) == 1 && last == '\n';
            incomplete_offset = complete ? -1 : offset;
            if (complete && parse_row(line, &entry, path))
            {
                int i = find_entry(fd, path);
                if (i < 0)
                {
                    if (reserve_entry()) i = score_count++;
                    else index_complete = false;
                }
                if (i >= 0)
                {
                    entry.offset = offset;
                    ((struct score_entry *)core_get_data(score_handle))[i] = entry;
                }
                score_rows++;
            }
            offset = next;
            lseek(fd, next, SEEK_SET);
        }
        if (length < 0) index_complete = false;
        close(fd);
    }
    if (!index_complete) save_failed = true;
    mutex_unlock(&score_mutex);
}

uint32_t home_score_get(const char *path, uint32_t now)
{
    if (!loaded)
        home_scores_reload();
    mutex_lock(&score_mutex);
    int i = find_entry(-1, path);
    uint32_t score = 0;
    if (i >= 0)
    {
        struct score_entry entry = ((struct score_entry *)core_get_data(score_handle))[i];
        score = home_score_decay(entry.score, entry.updated, now);
    }
    mutex_unlock(&score_mutex);
    return score;
}

bool home_score_record(const char *path, uint32_t elapsed, uint32_t length,
                       bool completed, uint32_t now)
{
    if (!path || path[0] != '/' || strlen(path) >= MAX_PATH)
        return false;
    if (!loaded)
        home_scores_reload();
    mutex_lock(&score_mutex);
    if (!index_complete)
    {
        save_failed = true;
        mutex_unlock(&score_mutex);
        return false;
    }
    int fd = open(HOME_SCORES_FILE, O_RDWR | O_CREAT, 0666);
    bool ok = false;
    if (fd >= 0)
    {
        /* Discard an interrupted final row before appending, so a truncated
         * path cannot become a new, apparently complete record next boot. */
        if (incomplete_offset >= 0)
        {
            if (ftruncate(fd, incomplete_offset) < 0)
            {
                close(fd);
                save_failed = true;
                mutex_unlock(&score_mutex);
                return false;
            }
            incomplete_offset = -1;
        }
        struct score_entry entry = { .hash = hash_path(path, 0) };
        int i = find_entry(fd, path);
        if (i >= 0)
            entry = ((struct score_entry *)core_get_data(score_handle))[i];
        if (i >= 0 || reserve_entry())
        {
            int points = home_listen_points(elapsed, length, completed);
            entry.score = home_score_decay(entry.score, entry.updated, now);
            uint32_t gain = points * HOME_SCORE_ONE;
            entry.score = UINT32_MAX - entry.score < gain ? UINT32_MAX : entry.score + gain;
            if (now > entry.updated) entry.updated = now;
            uint32_t *counter = completed ? &entry.full : points ? &entry.late : &entry.early;
            if (*counter != UINT32_MAX) (*counter)++;
            char escaped[MAX_PATH * 3], line[SCORE_LINE];
            escape_path(escaped, path);
            int n = snprintf(line, sizeof(line), "%lu\t%lu\t%lu\t%lu\t%lu\t%s\n",
                             (unsigned long)entry.score, (unsigned long)entry.updated,
                             (unsigned long)entry.full, (unsigned long)entry.late,
                             (unsigned long)entry.early, escaped);
            off_t end = lseek(fd, 0, SEEK_END);
            ok = end >= 0 && write(fd, line, n) == n && fsync(fd) == 0;
            if (ok)
            {
                entry.offset = end;
                if (i < 0) i = score_count++;
                ((struct score_entry *)core_get_data(score_handle))[i] = entry;
                score_rows++;
            }
            else if (end >= 0)
                ftruncate(fd, end);
        }
        if (close(fd) < 0) ok = false;
    }
    if (!ok) save_failed = true;
    mutex_unlock(&score_mutex);
    return ok;
}

bool home_scores_save_failed(void)
{
    mutex_lock(&score_mutex);
    bool failed = save_failed;
    save_failed = false;
    mutex_unlock(&score_mutex);
    return failed;
}

bool home_scores_compact(void)
{
    if (!loaded)
        home_scores_reload();
    mutex_lock(&score_mutex);
    bool ok = true;
    if (!index_complete)
    {
        mutex_unlock(&score_mutex);
        return false;
    }
    /* Consolidate on a Home visit, only after substantial duplication. */
    if (score_rows > score_count * 4 + 64)
    {
        int src = open(HOME_SCORES_FILE, O_RDONLY);
        int dst = open(HOME_SCORES_FILE ".tmp", O_WRONLY | O_CREAT | O_TRUNC, 0666);
        ok = src >= 0 && dst >= 0;
        off_t offset = 0;
        for (int i = 0; ok && i < score_count; i++)
        {
            struct score_entry entry = ((struct score_entry *)core_get_data(score_handle))[i];
            char line[SCORE_LINE] = "";
            ok = lseek(src, entry.offset, SEEK_SET) >= 0 &&
                 read_line(src, line, sizeof(line)) > 0;
            size_t length = strlen(line);
            if (ok) ok = write(dst, line, length) == (ssize_t)length && write(dst, "\n", 1) == 1;
            if (ok)
            {
                ((struct score_entry *)core_get_data(score_handle))[i].offset = offset;
                offset += length + 1;
            }
        }
        if (dst >= 0)
        {
            if (fsync(dst) < 0) ok = false;
            if (close(dst) < 0) ok = false;
        }
        if (src >= 0) close(src);
        if (ok) ok = rename(HOME_SCORES_FILE ".tmp", HOME_SCORES_FILE) == 0;
        if (!ok)
        {
            remove(HOME_SCORES_FILE ".tmp");
            /* The mutex is recursive. Restore old offsets before releasing it. */
            home_scores_reload();
        }
        else
        {
            score_rows = score_count;
            incomplete_offset = -1;
        }
    }
    else
    {
        mutex_unlock(&score_mutex);
        return true;
    }
    if (!ok) save_failed = true;
    mutex_unlock(&score_mutex);
    return ok;
}
