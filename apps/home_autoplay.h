/* Clip genre autoplay and listening scores. SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef HOME_AUTOPLAY_H
#define HOME_AUTOPLAY_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "rbpaths.h"

#define HOME_SCORE_ONE 65536u
#define HOME_SCORES_FILE ROCKBOX_DIR "/home-scores.tsv"
#define HOME_AUTOPLAY_A ROCKBOX_DIR "/home-autoplay-a.m3u8"
#define HOME_AUTOPLAY_B ROCKBOX_DIR "/home-autoplay-b.m3u8"

uint32_t home_score_decay(uint32_t score, uint32_t then, uint32_t now);
uint64_t home_weighted_rank(const char *path, uint32_t seed, uint32_t score);
int home_listen_points(uint32_t elapsed, uint32_t length, bool completed);
uint32_t home_score_get(const char *path, uint32_t now);
bool home_score_record(const char *path, uint32_t elapsed, uint32_t length,
                       bool completed, uint32_t now);
void home_scores_reload(void);
bool home_scores_compact(void);
bool home_scores_save_failed(void);
uint32_t home_score_time(void);

void home_autoplay_init(void);
/* Validate the database, stop previous audio, then prepare the first batch. */
const char *home_autoplay_prepare(const char *path);
/* Called at the existing end-of-playlist transition, with the playlist locked. */
int home_autoplay_next_round(void);
/* Request runs with the current playlist locked; it never stops audio. */
bool home_autoplay_active(void);
bool home_autoplay_request_genre(void);
const char *home_autoplay_pending_genre(void);
void home_autoplay_cancel_genre(void);
const char *home_autoplay_peek_genre(int steps, char *buf, size_t size);
/* Commit a pending queue on forward progression, with the playlist locked. */
int home_autoplay_commit_genre(int steps);

#endif
