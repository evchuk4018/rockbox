/* Small append-only listening store; latest row per path wins.
 * SPDX-License-Identifier: GPL-2.0-or-later */
#include <errno.h>
#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "config.h"
#include "home_autoplay.h"
#include "core_alloc.h"
#include "file.h"
#include "fixedpoint.h"
#include "kernel.h"
#include "logdiskf.h"
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

/* The disk logger flushes to HOME_DIR/rockbox_log.txt. Keep the syscall
 * result and errno before close/rollback/cleanup can overwrite them. */
static void score_log(const char *operation, long result, int error)
{
    ERRORF("%s result=%ld errno=%d", operation, result, error);
}

static void score_log_alloc(const char *operation, long result, int error,
                            size_t bytes)
{
    ERRORF("%s result=%ld errno=%d bytes=%lu", operation, result, error,
           (unsigned long)bytes);
}

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

/* The firmware C library has no %u/%lu conversion and its %n count differs
 * from the host library. Parse the fixed-width decimal fields explicitly. */
static bool parse_decimal(char **cursor, uint32_t *value)
{
    char *start = *cursor;
    if (*start < '0' || *start > '9')
        return false;
    errno = 0;
    char *end;
    unsigned long parsed = strtoul(start, &end, 10);
    int error = errno;
    if (end == start || error == ERANGE)
        return false;
#if ULONG_MAX > UINT32_MAX
    if (parsed > UINT32_MAX)
        return false;
#endif
    *cursor = end;
    *value = parsed;
    return true;
}

static bool parse_row(char *line, struct score_entry *entry, char *path)
{
    uint32_t fields[5];
    char *src = line;
    for (int i = 0; i < 5; i++)
    {
        if (!parse_decimal(&src, &fields[i]) || *src != '\t')
            return false;
        src++;
    }
    char *out = path;
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
    *entry = (struct score_entry){hash_path(path, 0), fields[0], fields[1],
                                  fields[2], fields[3], fields[4], 0};
    return true;
}

static bool reserve_entry(const char *operation)
{
    if (score_count < score_capacity)
        return true;
    int capacity = score_capacity ? score_capacity * 2 : 32;
    size_t bytes = (size_t)capacity * sizeof(struct score_entry);
    int handle = core_alloc(bytes);
    if (handle <= 0)
    {
        score_log_alloc(operation, handle, errno, bytes);
        return false;
    }
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

/* Returns an entry index, -1 when absent, or -2 after a storage error. */
static int find_entry(int fd, const char *path)
{
    bool own_fd = fd < 0;
    uint32_t hash = hash_path(path, 0);
    for (int i = 0; i < score_count; i++)
    {
        struct score_entry entry = ((struct score_entry *)core_get_data(score_handle))[i];
        if (entry.hash != hash)
            continue;
        if (fd < 0)
        {
            fd = open(HOME_SCORES_FILE, O_RDONLY);
            if (fd < 0)
            {
                int error = errno;
                if (error != ENOENT)
                    score_log("find-open", fd, error);
                return error == ENOENT ? -1 : -2;
            }
        }
        char line[SCORE_LINE], saved[MAX_PATH];
        if (lseek(fd, entry.offset, SEEK_SET) < 0)
        {
            int error = errno;
            score_log("find-seek", -1, error);
            if (own_fd) close(fd);
            return -2;
        }
        int length = read_line(fd, line, sizeof(line));
        if (length < 0)
        {
            int error = errno;
            score_log("find-read", length, error);
            if (own_fd) close(fd);
            return -2;
        }
        if (length > 0 && parse_row(line, &entry, saved) && !strcmp(saved, path))
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
    int open_error = errno;
    if (fd < 0 && open_error != ENOENT)
    {
        score_log("reload-open", fd, open_error);
        index_complete = false;
    }
    if (fd >= 0)
    {
        char line[SCORE_LINE], path[MAX_PATH];
        off_t offset = 0;
        while (true)
        {
            int length = read_line(fd, line, sizeof(line));
            if (!length)
                break;
            if (length < 0)
            {
                int error = errno;
                score_log("reload-read", length, error);
                index_complete = false;
                break;
            }
            off_t next = lseek(fd, 0, SEEK_CUR);
            if (next < 0)
            {
                int error = errno;
                score_log("reload-seek", (long)next, error);
                index_complete = false;
                break;
            }
            if (lseek(fd, next - 1, SEEK_SET) < 0)
            {
                int error = errno;
                score_log("reload-seek", -1, error);
                index_complete = false;
                break;
            }
            /* Ignore an incomplete final append. */
            char last;
            int got = read(fd, &last, 1);
            int read_error = errno;
            if (got < 0)
            {
                score_log("reload-read", got, read_error);
                index_complete = false;
                break;
            }
            bool complete = got == 1 && last == '\n';
            incomplete_offset = complete ? -1 : offset;
            if (complete)
            {
                struct score_entry entry;
                if (parse_row(line, &entry, path))
                {
                    int i = find_entry(fd, path);
                    if (i == -2)
                    {
                        index_complete = false;
                        break;
                    }
                    if (i < 0)
                    {
                        if (!reserve_entry("reload-alloc"))
                        {
                            index_complete = false;
                            break;
                        }
                        i = score_count++;
                    }
                    entry.offset = offset;
                    ((struct score_entry *)core_get_data(score_handle))[i] = entry;
                    score_rows++;
                }
            }
            offset = next;
            if (lseek(fd, next, SEEK_SET) < 0)
            {
                int error = errno;
                score_log("reload-seek", -1, error);
                index_complete = false;
                break;
            }
        }
        if (close(fd) < 0)
        {
            int error = errno;
            score_log("reload-close", -1, error);
            index_complete = false;
        }
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
    int open_error = errno;
    bool ok = false;
    if (fd < 0)
        score_log("record-open", fd, open_error);
    else
    {
        /* Discard an interrupted final row before appending, so a truncated
         * path cannot become a new, apparently complete record next boot. */
        if (incomplete_offset >= 0)
        {
            if (ftruncate(fd, incomplete_offset) < 0)
            {
                int error = errno;
                score_log("record-truncate", -1, error);
                if (close(fd) < 0)
                    score_log("record-close", -1, errno);
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
        if (i != -2 && (i >= 0 || reserve_entry("record-alloc")))
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
            if (end < 0)
            {
                int error = errno;
                score_log("record-seek", (long)end, error);
            }
            else
            {
                ssize_t wrote = write(fd, line, n);
                int write_error = errno;
                if (wrote != n)
                    score_log("record-write", (long)wrote, write_error);
                else if (fsync(fd) < 0)
                {
                    int error = errno;
                    score_log("record-sync", -1, error);
                }
                else
                    ok = true;
                if (ok)
                {
                    entry.offset = end;
                    if (i < 0) i = score_count++;
                    ((struct score_entry *)core_get_data(score_handle))[i] = entry;
                    score_rows++;
                }
                else if (ftruncate(fd, end) < 0)
                {
                    int error = errno;
                    score_log("record-rollback", -1, error);
                }
            }
        }
        if (close(fd) < 0)
        {
            int error = errno;
            score_log("record-close", -1, error);
            ok = false;
        }
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
        int src_error = errno;
        if (src < 0)
        {
            score_log("compact-open-src", src, src_error);
            ok = false;
        }
        int dst = open(HOME_SCORES_FILE ".tmp", O_WRONLY | O_CREAT | O_TRUNC, 0666);
        int dst_error = errno;
        if (dst < 0)
        {
            score_log("compact-open-dst", dst, dst_error);
            ok = false;
        }
        off_t offset = 0;
        for (int i = 0; ok && i < score_count; i++)
        {
            struct score_entry entry = ((struct score_entry *)core_get_data(score_handle))[i];
            char line[SCORE_LINE] = "";
            if (lseek(src, entry.offset, SEEK_SET) < 0)
            {
                int error = errno;
                score_log("compact-seek", -1, error);
                ok = false;
                break;
            }
            int length = read_line(src, line, sizeof(line));
            if (length <= 0)
            {
                int error = errno;
                score_log("compact-read", length, error);
                ok = false;
                break;
            }
            size_t size = strlen(line);
            ssize_t wrote = write(dst, line, size);
            int write_error = errno;
            if (wrote != (ssize_t)size)
            {
                score_log("compact-write", (long)wrote, write_error);
                ok = false;
                break;
            }
            wrote = write(dst, "\n", 1);
            write_error = errno;
            if (wrote != 1)
            {
                score_log("compact-write", (long)wrote, write_error);
                ok = false;
                break;
            }
            ((struct score_entry *)core_get_data(score_handle))[i].offset = offset;
            offset += size + 1;
        }
        if (dst >= 0)
        {
            if (fsync(dst) < 0)
            {
                int error = errno;
                score_log("compact-sync", -1, error);
                ok = false;
            }
            if (close(dst) < 0)
            {
                int error = errno;
                score_log("compact-close-dst", -1, error);
                ok = false;
            }
        }
        if (src >= 0 && close(src) < 0)
        {
            int error = errno;
            score_log("compact-close-src", -1, error);
        }
        if (ok && rename(HOME_SCORES_FILE ".tmp", HOME_SCORES_FILE) != 0)
        {
            int error = errno;
            score_log("compact-rename", -1, error);
            ok = false;
        }
        if (!ok)
        {
            int removed = remove(HOME_SCORES_FILE ".tmp");
            int remove_error = errno;
            if (removed < 0 && remove_error != ENOENT)
                score_log("compact-remove", removed, remove_error);
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
