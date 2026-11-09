/* Managed podcast catalog and Clip playback support. SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef PODCASTS_H
#define PODCASTS_H

#include <stddef.h>
#include <stdbool.h>
#include <stdint.h>

#ifdef PODCAST_TEST
#define ROCKBOX_DIR "/.rockbox"
#define PODCAST_PATH_MAX 260

enum {
    PODCAST_TEST_TRACK_FINISH = 1,
    PODCAST_TEST_TRACK_CHANGE = 2,
};

enum {
    PODCAST_TEST_TEF_CURRENT = 0x1,
    PODCAST_TEST_TEF_AUTO_SKIP = 0x2,
    PODCAST_TEST_TEF_REWIND = 0x4,
};

struct mp3entry {
    char path[PODCAST_PATH_MAX];
    unsigned long elapsed;
    unsigned long length;
};

struct track_event {
    unsigned int flags;
    struct mp3entry *id3;
};

typedef void (*podcast_test_event_handler)(unsigned short id, void *param);
void podcast_test_add_event(unsigned short id, podcast_test_event_handler handler);
#else
#include "config.h"
#include "file.h"
#include "rbpaths.h"
#define PODCAST_PATH_MAX MAX_PATH
#endif

#define PODCAST_ID_MAX 64
#define PODCAST_NAME_MAX 512
#define PODCAST_TITLE_MAX 512
#define PODCAST_MAX_CHANNELS 32
#define PODCAST_MAX_EPISODES 96
#define PODCAST_MAX_CATALOG_LINE 2048

#define PODCAST_CATALOG_PATH ROCKBOX_DIR "/podcasts/catalog.tsv"
#define PODCAST_RESUME_DIR ROCKBOX_DIR "/podcasts/resume"

struct podcast_channel {
    char id[PODCAST_ID_MAX];
    char name[PODCAST_NAME_MAX];
};

struct podcast_episode {
    char id[PODCAST_ID_MAX];
    char title[PODCAST_TITLE_MAX];
    int64_t publication;
    uint32_t duration_ms;
};

enum podcast_episode_status {
    PODCAST_EPISODE_NEW,
    PODCAST_EPISODE_SAVED,
    PODCAST_EPISODE_PLAYED,
};

struct podcast_scan_state {
    bool active;
    int direction;
    int speed;
    int64_t target_ms;
    int64_t duration_ms;
    int64_t last_ms;
};

bool podcast_catalog_load(void);
bool podcast_init(void);
void podcast_catalog_reset(void);
int podcast_channel_count(void);
const struct podcast_channel *podcast_channel_at(int channel);
int podcast_episode_count(int channel);
const struct podcast_episode *podcast_episode_at(int channel, int episode);
enum podcast_episode_status podcast_episode_status(int channel, int episode);
uint32_t podcast_episode_resume(int channel, int episode);
const char *podcast_episode_label(int channel, int episode, char *buf, size_t size);
bool podcast_episode_save(int channel, int episode, uint32_t elapsed_ms, bool played);
bool podcast_save_failed(void);

bool podcast_start_episode(int channel, int episode);
void podcast_wps_pause_save(void);
void podcast_wps_checkpoint(void);
void podcast_wps_exit(void);
bool podcast_wps_action(int action);
const char *podcast_scan_feedback(char *buf, size_t size);

void podcast_scan_init(struct podcast_scan_state *scan, int64_t duration_ms,
                       int64_t current_ms);
bool podcast_scan_tap(struct podcast_scan_state *scan, int direction,
                      int64_t now_ms);
void podcast_scan_tick(struct podcast_scan_state *scan, int64_t now_ms);
void podcast_scan_commit(struct podcast_scan_state *scan, bool resume);

#ifdef PODCAST_TEST
enum {
    PODCAST_ACTION_SKIPPREV = 1,
    PODCAST_ACTION_SKIPNEXT,
    PODCAST_ACTION_SEEKBACK,
    PODCAST_ACTION_SEEKFWD,
    PODCAST_ACTION_STOPSEEK,
    PODCAST_ACTION_PLAY,
};
#else
int podcast_screen(void);
#endif

#endif
