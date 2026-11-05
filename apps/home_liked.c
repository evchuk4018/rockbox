/* Playlist-backed Clip likes. SPDX-License-Identifier: GPL-2.0-or-later */
#include <errno.h>
#include <string.h>
#include "home_screen.h"
#include "dir.h"
#include "misc.h"
#include "playlist.h"
#include "string-extra.h"

static char cached_path[MAX_PATH];
static int cached_state = -1;
static bool invalidated = true;
static unsigned int revision;

void home_liked_invalidate(void)
{
    invalidated = true;
}

unsigned int home_liked_revision(void) { return revision; }

/* Silent, bounded streaming reader. Reject truncation instead of rewriting a
 * damaged prefix. open_utf8 accepts an optional BOM; read_line accepts CRLF. */
static int liked_entry(int fd, char *path)
{
    char line[MAX_PATH + 4];
    int n;
    while ((n = read_line(fd, line, sizeof(line))) > 0)
    {
        if (strlen(line) == sizeof(line) - 1)
            return -1;
        if (!line[0] || line[0] == '#')
            continue;
        if (playlist_format_track_path(path, line, MAX_PATH,
                                       "/Playlists", 10) < 0)
            return -1;
        return 1;
    }
    return n;
}

bool home_liked_foreach(bool (*visit)(const char *, void *), void *context)
{
    int fd = open_utf8(HOME_LIKED_PLAYLIST, O_RDONLY);
    if (fd < 0) return false;
    char path[MAX_PATH];
    int n;
    while ((n = liked_entry(fd, path)) > 0)
        if (!visit(path, context)) break;
    bool ok = n >= 0;
    if (close(fd) < 0) ok = false;
    return ok;
}

static bool normalize_path(const char *path, char *out)
{
    if (!path || path[0] != '/' || strlen(path) >= MAX_PATH ||
        strchr(path, '\n') || strchr(path, '\r')) return false;
    char input[MAX_PATH];
    strlcpy(input, path, sizeof(input));
    return playlist_format_track_path(out, input, MAX_PATH, "", 0) >= 0;
}

int home_liked_state(const char *path)
{
    char normalized[MAX_PATH];
    if (!normalize_path(path, normalized)) return -1;
    path = normalized;
    if (!invalidated && !strcmp(cached_path, path))
        return cached_state;
    invalidated = false;
    strlcpy(cached_path, path, sizeof(cached_path));
    cached_state = -1;
    int fd = open_utf8(HOME_LIKED_PLAYLIST, O_RDONLY);
    if (fd < 0)
        return cached_state = errno == ENOENT ? 0 : -1;
    char entry[MAX_PATH];
    int n;
    bool found = false;
    while ((n = liked_entry(fd, entry)) > 0)
        if (!strcmp(entry, path)) found = true;
    if (close(fd) == 0 && n == 0)
        cached_state = found;
    return cached_state;
}

static bool write_entry(int fd, const char *path)
{
    size_t size = strlen(path);
    return write(fd, path, size) == (ssize_t)size && write(fd, "\n", 1) == 1;
}

int home_toggle_like(const char *path)
{
    char normalized[MAX_PATH];
    if (!normalize_path(path, normalized)) return -1;
    path = normalized;
    int src = open_utf8(HOME_LIKED_PLAYLIST, O_RDONLY);
    if (src < 0 && errno != ENOENT)
        return -1;
    if (src < 0 && mkdir("/Playlists") < 0 && errno != EEXIST)
        return -1;
    const char *temp = HOME_LIKED_PLAYLIST ".tmp";
    int dst = open(temp, O_WRONLY | O_CREAT | O_TRUNC, 0666);
    bool ok = dst >= 0 && write_entry(dst, "#EXTM3U");
    bool found = false;
    char entry[MAX_PATH];
    int n = 0;
    while (ok && src >= 0 && (n = liked_entry(src, entry)) > 0)
    {
        if (!strcmp(entry, path)) found = true;
        else ok = write_entry(dst, entry);
    }
    if (n < 0) ok = false;
    if (src >= 0 && close(src) < 0) ok = false;
    if (ok && !found) ok = write_entry(dst, path);
    if (dst >= 0)
    {
        if (fsync(dst) < 0) ok = false;
        if (close(dst) < 0) ok = false;
    }
    if (ok && rename(temp, HOME_LIKED_PLAYLIST) == 0)
    {
        strlcpy(cached_path, path, sizeof(cached_path));
        cached_state = !found;
        revision++;
        return cached_state;
    }
    remove(temp);
    return -1;
}
