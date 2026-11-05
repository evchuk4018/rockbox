/* Song sampling and persistent playlist pins. SPDX-License-Identifier: GPL-2.0-or-later */
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include "home_screen.h"
#include "home_autoplay.h"
#include "action.h"
#include "kernel.h"
#include "misc.h"
#include "string-extra.h"

bool home_in_settings;
int home_selection;

void home_handle_action(int action)
{
    if (action == ACTION_STD_MENU || action == ACTION_WPS_MENU)
    {
        home_in_settings = false;
        home_selection = 0;
    }
}

void home_sample_begin(struct home_picks *picks, uint32_t seed)
{
    picks->count = 0;
    picks->seed = seed;
    picks->score_time = home_score_time();
}

void home_sample_add(struct home_picks *picks, const char *path,
                     const char *title, const char *artist, const char *genre)
{
    if (!genre || !genre[0] || strlen(genre) >= MAX_PATH ||
        !path || !path[0] || strlen(path) >= MAX_PATH)
        return;

    uint64_t song_rank = home_weighted_rank(path, picks->seed,
        home_score_get(path, picks->score_time));
    int slot = -1;
    for (int i = 0; i < picks->count; i++)
    {
        struct home_song *song = &picks->songs[i];
        if (!strcmp(path, song->path))
            return;
        if (!strcasecmp(genre, song->genre))
        {
            if (song_rank >= song->song_rank)
                return;
            slot = i;
            break;
        }
    }
    if (slot < 0)
    {
        if (picks->count < HOME_SONG_COUNT)
            slot = picks->count++;
        else
        {
            slot = 0;
            for (int i = 1; i < picks->count; i++)
                if (picks->songs[i].song_rank > picks->songs[slot].song_rank)
                    slot = i;
            if (song_rank >= picks->songs[slot].song_rank)
                return;
        }
    }

    struct home_song *song = &picks->songs[slot];
    strlcpy(song->path, path, sizeof(song->path));
    strlcpy(song->genre, genre, sizeof(song->genre));
    song->song_rank = song_rank;
    if (!title || !title[0])
    {
        title = strrchr(path, '/');
        title = title ? title + 1 : path;
    }
    snprintf(song->label, sizeof(song->label), "%s%s%s", title,
             artist && artist[0] ? " — " : "", artist ? artist : "");
    /* Only three slots: keep the strongest random priority at the top. */
    for (int i = 1; i < picks->count; i++)
        for (int j = i; j > 0 && picks->songs[j].song_rank < picks->songs[j - 1].song_rank; j--)
        {
            struct home_song swap = picks->songs[j];
            picks->songs[j] = picks->songs[j - 1];
            picks->songs[j - 1] = swap;
        }
}

bool home_refresh_due(const struct home_picks *picks, uint32_t now)
{
    return !picks->initialized ||
           (uint32_t)(now - picks->refreshed_at) >= HOME_REFRESH_TICKS;
}

bool home_manual_refresh(int action, int selected)
{
    return action == ACTION_STD_PREV && selected == 0;
}

int home_pins_load(char pins[HOME_MAX_PINS][MAX_PATH])
{
    int fd = open(HOME_PINS_FILE, O_RDONLY);
    if (fd < 0)
        return errno == ENOENT ? 0 : -1;

    char line[MAX_PATH];
    int count = 0, length;
    while ((length = read_line(fd, line, sizeof(line))) > 0)
    {
        if (line[0] != '/' || strlen(line) >= MAX_PATH - 1)
            continue;
        bool duplicate = false;
        for (int i = 0; i < count; i++)
            if (!strcmp(pins[i], line))
                duplicate = true;
        if (duplicate)
            continue;
        if (count == HOME_MAX_PINS)
        {
            count = -1;
            break;
        }
        strlcpy(pins[count++], line, MAX_PATH);
    }
    if (length < 0)
        count = -1;
    close(fd);
    return count;
}

static char pin_buffer[HOME_MAX_PINS][MAX_PATH];

bool home_playlist_is_pinned(const char *path)
{
    int count = home_pins_load(pin_buffer);
    for (int i = 0; i < count; i++)
        if (!strcmp(pin_buffer[i], path))
            return true;
    return false;
}

int home_toggle_pin(const char *path)
{
    int count = home_pins_load(pin_buffer);
    if (count < 0 || !path || path[0] != '/' || strlen(path) >= MAX_PATH - 1 ||
        strchr(path, '\n') || strchr(path, '\r'))
        return -1;

    int index;
    for (index = 0; index < count; index++)
        if (!strcmp(pin_buffer[index], path))
            break;
    if (index < count)
    {
        memmove(pin_buffer[index], pin_buffer[index + 1],
                (count - index - 1) * sizeof(pin_buffer[0]));
        count--;
    }
    else
    {
        if (count == HOME_MAX_PINS)
            return -1;
        strlcpy(pin_buffer[count++], path, MAX_PATH);
    }

    const char *temp = HOME_PINS_FILE ".tmp";
    int fd = open(temp, O_WRONLY | O_CREAT | O_TRUNC, 0666);
    if (fd < 0)
        return -1;
    bool ok = true;
    for (int i = 0; ok && i < count; i++)
    {
        size_t length = strlen(pin_buffer[i]);
        ok = write(fd, pin_buffer[i], length) == (ssize_t)length &&
             write(fd, "\n", 1) == 1;
    }
    if (fsync(fd) < 0)
        ok = false;
    if (close(fd) < 0)
        ok = false;
    if (ok && rename(temp, HOME_PINS_FILE) == 0)
        return 0;
    remove(temp);
    return -1;
}
