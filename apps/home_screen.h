/* Song-first Sansa Clip home screen. SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef HOME_SCREEN_H
#define HOME_SCREEN_H

#include <stdbool.h>
#include <stdint.h>
#include "config.h"
#include "file.h"
#include "rbpaths.h"

#define HOME_SONG_COUNT 3
#define HOME_MAX_PINS 16
#define HOME_LIKED_PLAYLIST "/Playlists/Liked Songs.m3u8"
#define HOME_PINS_FILE ROCKBOX_DIR "/home-pins.txt"
#define HOME_REFRESH_TICKS (30u * 60u * HZ)

struct home_song {
    char path[MAX_PATH];
    char genre[MAX_PATH];
    char label[MAX_PATH * 2];
    uint64_t song_rank;
};

struct home_picks {
    struct home_song songs[HOME_SONG_COUNT];
    int count;
    uint32_t seed;
    uint32_t score_time;
    uint32_t refreshed_at;
    bool initialized;
};

void home_sample_begin(struct home_picks *picks, uint32_t seed);
void home_sample_add(struct home_picks *picks, const char *path,
                     const char *title, const char *artist, const char *genre);
bool home_refresh_due(const struct home_picks *picks, uint32_t now);
bool home_manual_refresh(int action, int selected);
int home_pins_load(char pins[HOME_MAX_PINS][MAX_PATH]);
bool home_playlist_is_pinned(const char *path);
int home_toggle_pin(const char *path);
/* -1 on storage failure, otherwise 0 (unliked) or 1 (liked). */
int home_liked_state(const char *path);
int home_toggle_like(const char *path);
void home_liked_invalidate(void);
unsigned int home_liked_revision(void);
bool home_liked_foreach(bool (*visit)(const char *, void *), void *context);

/* Back keeps the legacy menu as the parent; an explicit Home action clears it. */
extern bool home_in_settings;
extern int home_selection;
void home_handle_action(int action);
int home_screen(void);
int home_play_suggestion(const char *path);

#endif
