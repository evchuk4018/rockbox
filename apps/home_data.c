/* Song sampling and navigation. SPDX-License-Identifier: GPL-2.0-or-later */
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include "home_screen.h"
#include "home_autoplay.h"
#include "action.h"
#include "kernel.h"
#include "misc.h"
#include "string-extra.h"

/* Display comma-separated artist credits once, without changing stored tags.
 * Fall back to the original credit if the complete result would not fit. */
const char *home_artist_label(const char *artist, char *buf, size_t size)
{
    if (!artist || !size) return artist;
    const char *part = artist;
    size_t used = 0;
    while (*part)
    {
        const char *end = strchr(part, ',');
        if (!end) end = part + strlen(part);
        const char *start = part, *trim = end;
        while (start < trim && (*start == ' ' || *start == '\t')) start++;
        while (trim > start && (trim[-1] == ' ' || trim[-1] == '\t')) trim--;
        size_t length = trim - start;
        bool duplicate = false;
        for (const char *previous = artist; previous < part; )
        {
            const char *stop = strchr(previous, ',');
            if (!stop) break;
            const char *last = stop;
            while (previous < last && (*previous == ' ' || *previous == '\t')) previous++;
            while (last > previous && (last[-1] == ' ' || last[-1] == '\t')) last--;
            if ((size_t)(last - previous) == length &&
                !strncasecmp(previous, start, length)) duplicate = true;
            previous = stop + 1;
        }
        if (length && !duplicate)
        {
            if (used + (used ? 2 : 0) + length >= size) return artist;
            if (used) { buf[used++] = ','; buf[used++] = ' '; }
            memcpy(buf + used, start, length);
            used += length;
        }
        part = *end ? end + 1 : end;
    }
    buf[used] = 0;
    return used ? buf : artist;
}

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
