/* Managed podcast catalog and Clip playback support. SPDX-License-Identifier: GPL-2.0-or-later */
#include "podcasts.h"

#ifdef PODCAST_TEST
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>

extern int podcast_test_open(const char *path, int flags);
extern int podcast_test_read(int fd, void *data, size_t size);
extern int podcast_test_write(int fd, const void *data, size_t size);
extern int podcast_test_close(int fd);
extern int podcast_test_sync(int fd);
extern int podcast_test_rename(const char *old_path, const char *new_path);
extern int podcast_test_remove(const char *path);
extern int podcast_test_mkdir(const char *path);
extern long podcast_test_now(void);
extern void podcast_test_audio_pause(void);
extern void podcast_test_audio_resume(void);
extern void podcast_test_audio_stop(void);
extern int podcast_test_audio_status(void);
extern long podcast_test_audio_elapsed(void);
extern void podcast_test_audio_seek(long elapsed);
extern int podcast_test_playlist_create(void);
extern int podcast_test_playlist_insert(const char *path);
extern void podcast_test_playlist_sync(void);
extern int podcast_test_playlist_set_single_track(void);
extern void podcast_test_playlist_start(long elapsed);
extern int podcast_test_file_exists(const char *path);

#define PODCAST_OPEN podcast_test_open
#define PODCAST_READ podcast_test_read
#define PODCAST_WRITE podcast_test_write
#define PODCAST_CLOSE podcast_test_close
#define PODCAST_SYNC podcast_test_sync
#define PODCAST_RENAME podcast_test_rename
#define PODCAST_REMOVE podcast_test_remove
#define PODCAST_MKDIR podcast_test_mkdir
#define PODCAST_NOW_TICK podcast_test_now()
#define PODCAST_AUDIO_PAUSE podcast_test_audio_pause()
#define PODCAST_AUDIO_RESUME podcast_test_audio_resume()
#define PODCAST_AUDIO_STOP podcast_test_audio_stop()
#define PODCAST_AUDIO_STATUS podcast_test_audio_status()
#define PODCAST_AUDIO_ELAPSED podcast_test_audio_elapsed()
#define PODCAST_AUDIO_SEEK(position) podcast_test_audio_seek((long)(position))
#define PODCAST_FILE_EXISTS podcast_test_file_exists
#define PODCAST_PLAYLIST_CREATE podcast_test_playlist_create()
#define PODCAST_PLAYLIST_INSERT(path) podcast_test_playlist_insert(path)
#define PODCAST_PLAYLIST_SYNC podcast_test_playlist_sync()
#define PODCAST_PLAYLIST_SINGLE podcast_test_playlist_set_single_track()
#define PODCAST_PLAYLIST_START(index, elapsed) podcast_test_playlist_start(elapsed)
#define PODCAST_NOW_MS ((int64_t)PODCAST_NOW_TICK)
#define PODCAST_HZ 1000
#define PLAYBACK_EVENT_TRACK_FINISH PODCAST_TEST_TRACK_FINISH
#define PLAYBACK_EVENT_TRACK_CHANGE PODCAST_TEST_TRACK_CHANGE
#define TEF_CURRENT PODCAST_TEST_TEF_CURRENT
#define TEF_AUTO_SKIP PODCAST_TEST_TEF_AUTO_SKIP
#define TEF_REWIND PODCAST_TEST_TEF_REWIND
#define add_event podcast_test_add_event

#else
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include "action.h"
#include "appevents.h"
#include "audio.h"
#include "dir.h"
#include "file.h"
#include "gui/list.h"
#include "kernel.h"
#include "lang.h"
#include "language.h"
#include "list.h"
#include "metadata.h"
#include "misc.h"
#include "playlist.h"
#include "root_menu.h"
#include "settings.h"
#include "splash.h"
#include "status.h"
#include "statusbar-skinned.h"
#include "string-extra.h"
#include "viewport.h"
#include "wps.h"

#define PODCAST_OPEN open_utf8
#define PODCAST_READ read
#define PODCAST_WRITE write
#define PODCAST_CLOSE close
#define PODCAST_SYNC fsync
#define PODCAST_RENAME rename
#define PODCAST_REMOVE remove
#define PODCAST_MKDIR mkdir
#define PODCAST_NOW_TICK current_tick
#define PODCAST_AUDIO_PAUSE audio_pause()
#define PODCAST_AUDIO_RESUME audio_resume()
#define PODCAST_AUDIO_STOP audio_stop()
#define PODCAST_AUDIO_STATUS audio_status()
#define PODCAST_AUDIO_ELAPSED (audio_current_track() ? (long)audio_current_track()->elapsed : 0)
#define PODCAST_AUDIO_SEEK(position) do { \
    audio_pre_ff_rewind(); \
    struct mp3entry *seek_track = audio_current_track(); \
    if (seek_track) seek_track->elapsed = (unsigned long)(position); \
    audio_ff_rewind((long)(position)); \
} while (0)
#define PODCAST_FILE_EXISTS file_exists
#define PODCAST_PLAYLIST_CREATE playlist_create(NULL, NULL)
#define PODCAST_PLAYLIST_INSERT(path) playlist_insert_track(NULL, path, PLAYLIST_INSERT_LAST, false, false)
#define PODCAST_PLAYLIST_SYNC playlist_sync(NULL)
#define PODCAST_PLAYLIST_SINGLE playlist_set_single_track()
#define PODCAST_PLAYLIST_START(index, elapsed) playlist_start(index, elapsed, 0)
#define PODCAST_NOW_MS ((int64_t)PODCAST_NOW_TICK * 1000 / HZ)
#define PODCAST_HZ HZ
#define PODCAST_ACTION_SKIPPREV ACTION_WPS_SKIPPREV
#define PODCAST_ACTION_SKIPNEXT ACTION_WPS_SKIPNEXT
#define PODCAST_ACTION_SEEKBACK ACTION_WPS_SEEKBACK
#define PODCAST_ACTION_SEEKFWD ACTION_WPS_SEEKFWD
#define PODCAST_ACTION_STOPSEEK ACTION_WPS_STOPSEEK
#define PODCAST_ACTION_PLAY ACTION_WPS_PLAY

#endif

#define PODCAST_AUDIO_PREFIX "/<microSD1>/Podcasts/RockboxManager/"

static struct podcast_channel channels[PODCAST_MAX_CHANNELS];
static struct podcast_episode episodes[PODCAST_MAX_EPISODES];
static int episode_channels[PODCAST_MAX_EPISODES];
static uint32_t episode_elapsed[PODCAST_MAX_EPISODES];
static bool episode_played[PODCAST_MAX_EPISODES];
static int channel_count;
static int episode_count;
static bool catalog_loaded;
static bool save_failed;
static bool playback_active;
static int active_episode = -1;
static bool events_registered;
static char active_episode_id[PODCAST_ID_MAX];
static uint32_t active_elapsed;
static long checkpoint_tick;
static bool checkpoint_started;
static struct podcast_scan_state active_scan;
#ifndef PODCAST_TEST
static char scan_title[32];
#endif

static void register_events(void);
static int find_episode_id(const char *id);
static int active_index(void);
static bool save_active(void);
static void podcast_playback_event(unsigned short id, void *param);

static bool valid_utf8(const unsigned char *text, size_t length)
{
    size_t i = 0;
    while (i < length)
    {
        unsigned char c = text[i++];
        if (c <= 0x7f)
            continue;
        if (c >= 0xc2 && c <= 0xdf)
        {
            if (i >= length || (text[i++] & 0xc0) != 0x80) return false;
            continue;
        }
        if (c >= 0xe0 && c <= 0xef)
        {
            if (i + 1 >= length || (text[i] & 0xc0) != 0x80 ||
                (text[i + 1] & 0xc0) != 0x80) return false;
            if ((c == 0xe0 && text[i] < 0xa0) ||
                (c == 0xed && text[i] >= 0xa0)) return false;
            i += 2;
            continue;
        }
        if (c >= 0xf0 && c <= 0xf4)
        {
            if (i + 2 >= length || (text[i] & 0xc0) != 0x80 ||
                (text[i + 1] & 0xc0) != 0x80 ||
                (text[i + 2] & 0xc0) != 0x80) return false;
            if ((c == 0xf0 && text[i] < 0x90) ||
                (c == 0xf4 && text[i] >= 0x90)) return false;
            i += 3;
            continue;
        }
        return false;
    }
    return true;
}

static int hex_value(unsigned char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
}

static bool decode_text(const char *source, char *dest, size_t size)
{
    size_t used = 0;
    size_t length = strlen(source);
    for (size_t i = 0; i < length; i++)
    {
        unsigned char c = (unsigned char)source[i];
        if (c == '%')
        {
            if (i + 2 >= length) return false;
            int high = hex_value((unsigned char)source[++i]);
            int low = hex_value((unsigned char)source[++i]);
            if (high < 0 || low < 0) return false;
            c = (unsigned char)((high << 4) | low);
            if (c != 0x25 && c != 0x09 && c != 0x0d && c != 0x0a) return false;
        }
        if (!c || used + 1 >= size) return false;
        dest[used++] = (char)c;
    }
    dest[used] = 0;
    return valid_utf8((const unsigned char *)dest, used);
}

static bool valid_id(const char *id)
{
    size_t length = strlen(id);
    if (!length || length >= PODCAST_ID_MAX) return false;
    for (size_t i = 0; i < length; i++)
        if (!((id[i] >= 'a' && id[i] <= 'z') ||
              (id[i] >= 'A' && id[i] <= 'Z') ||
              (id[i] >= '0' && id[i] <= '9') ||
              id[i] == '_' || id[i] == '-')) return false;
    return true;
}

static bool valid_date(const char *date)
{
    if (strlen(date) != 10 || date[4] != '-' || date[7] != '-') return false;
    for (int i = 0; i < 10; i++)
        if (i != 4 && i != 7 && (date[i] < '0' || date[i] > '9')) return false;
    int year = (date[0] - '0') * 1000 + (date[1] - '0') * 100 +
               (date[2] - '0') * 10 + date[3] - '0';
    int month = (date[5] - '0') * 10 + date[6] - '0';
    int day = (date[8] - '0') * 10 + date[9] - '0';
    static const unsigned char month_days[] =
        {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    int maximum = month >= 1 && month <= 12 ? month_days[month - 1] : 0;
    if (month == 2 && (year % 4 == 0 && (year % 100 != 0 || year % 400 == 0))) maximum++;
    return day >= 1 && day <= maximum;
}

static bool parse_unsigned(const char *text, uint64_t *value)
{
    if (!text[0]) return false;
    uint64_t result = 0;
    for (const unsigned char *p = (const unsigned char *)text; *p; p++)
    {
        if (*p < '0' || *p > '9' || result > (UINT64_MAX - (*p - '0')) / 10)
            return false;
        result = result * 10 + (*p - '0');
    }
    *value = result;
    return true;
}

static bool parse_publication(const char *text, int64_t *value)
{
    uint64_t parsed;
    if (!parse_unsigned(text, &parsed) || parsed > INT64_MAX) return false;
    *value = (int64_t)parsed;
    return true;
}

static int split_fields(char *line, char **fields, int maximum)
{
    int count = 0;
    fields[count++] = line;
    for (char *p = line; *p; p++)
        if (*p == '\t')
        {
            *p = 0;
            if (count == maximum) return -1;
            fields[count++] = p + 1;
        }
    return count;
}

/* Unlike read_line(), this preserves CR and rejects a non-LF-terminated row. */
static int podcast_read_line(int fd, char *line, size_t size)
{
    size_t used = 0;
    bool bad = false;
    while (true)
    {
        unsigned char c;
        int result = PODCAST_READ(fd, &c, 1);
        if (result < 0) return -2;
        if (!result) return used ? -1 : 0;
        if (c == '\n') break;
        if (c == '\r' || c == 0) bad = true;
        if (used + 1 < size) line[used++] = (char)c;
        else bad = true;
    }
    line[used] = 0;
    if (!used) return -3; /* A blank row is malformed, not end of file. */
    return bad ? -1 : (int)used;
}

static int find_channel(const char *id)
{
    for (int i = 0; i < channel_count; i++)
        if (!strcmp(channels[i].id, id)) return i;
    return -1;
}

static int find_episode_id(const char *id)
{
    for (int i = 0; i < episode_count; i++)
        if (!strcmp(episodes[i].id, id)) return i;
    return -1;
}

static bool path_for_id(const char *id, char *path, size_t size)
{
    int length = snprintf(path, size, "%s%s.mp3", PODCAST_AUDIO_PREFIX, id);
    return length >= 0 && (size_t)length < size;
}

static bool parse_header(char *line)
{
    char *fields[4];
    if (split_fields(line, fields, 4) != 4 || strcmp(fields[0], "ROCKBOX-PODCASTS") ||
        strcmp(fields[1], "1") || !valid_date(fields[2]) || !valid_date(fields[3]))
        return false;
    return true;
}

static void parse_channel(char **fields)
{
    if (channel_count >= PODCAST_MAX_CHANNELS) return;
    char id[PODCAST_ID_MAX], name[PODCAST_NAME_MAX];
    if (!decode_text(fields[1], id, sizeof(id)) || !valid_id(id) ||
        !decode_text(fields[2], name, sizeof(name)) ||
        find_channel(id) >= 0) return;
    struct podcast_channel *channel = &channels[channel_count++];
    strcpy(channel->id, id);
    strcpy(channel->name, name);
}

static void parse_episode(char **fields)
{
    if (episode_count >= PODCAST_MAX_EPISODES) return;
    char channel_id[PODCAST_ID_MAX], id[PODCAST_ID_MAX];
    char title[PODCAST_TITLE_MAX], path[PODCAST_PATH_MAX];
    int channel;
    int64_t publication;
    uint64_t duration;
    if (!decode_text(fields[1], channel_id, sizeof(channel_id)) ||
        !valid_id(channel_id) || (channel = find_channel(channel_id)) < 0 ||
        !decode_text(fields[2], id, sizeof(id)) || !valid_id(id) ||
        !decode_text(fields[3], title, sizeof(title)) ||
        !parse_publication(fields[4], &publication) ||
        !parse_unsigned(fields[5], &duration) ||
        !decode_text(fields[6], path, sizeof(path))) return;
    char expected[PODCAST_PATH_MAX];
    if (!path_for_id(id, expected, sizeof(expected)) || strcmp(path, expected)) return;
    for (int i = 0; i < episode_count; i++)
        if (!strcmp(episodes[i].id, id)) return;
    struct podcast_episode *episode = &episodes[episode_count];
    strcpy(episode->id, id);
    strcpy(episode->title, title);
    episode->publication = publication;
    episode->duration_ms = duration > UINT32_MAX ? UINT32_MAX : (uint32_t)duration;
    episode_channels[episode_count++] = channel;
}

static void sort_catalog(void)
{
    for (int i = 1; i < channel_count; i++)
        for (int j = i; j > 0; j--)
        {
            int left = j - 1, right = j;
            int order = strcasecmp(channels[left].name, channels[right].name);
            if (order > 0 || (order == 0 && strcmp(channels[left].id, channels[right].id) > 0))
            {
                struct podcast_channel swap = channels[left];
                channels[left] = channels[right];
                channels[right] = swap;
                for (int e = 0; e < episode_count; e++)
                {
                    if (episode_channels[e] == left) episode_channels[e] = right;
                    else if (episode_channels[e] == right) episode_channels[e] = left;
                }
            }
            else break;
        }

    for (int i = 1; i < episode_count; i++)
        for (int j = i; j > 0; j--)
        {
            int left = j - 1, right = j;
            bool after = episode_channels[left] > episode_channels[right] ||
                (episode_channels[left] == episode_channels[right] &&
                 (episodes[left].publication > episodes[right].publication ||
                  (episodes[left].publication == episodes[right].publication &&
                   strcmp(episodes[left].id, episodes[right].id) > 0)));
            if (!after) break;
            struct podcast_episode episode_swap = episodes[left];
            episodes[left] = episodes[right];
            episodes[right] = episode_swap;
            uint32_t elapsed_swap = episode_elapsed[left];
            episode_elapsed[left] = episode_elapsed[right];
            episode_elapsed[right] = elapsed_swap;
            bool played_swap = episode_played[left];
            episode_played[left] = episode_played[right];
            episode_played[right] = played_swap;
            int channel_swap = episode_channels[left];
            episode_channels[left] = episode_channels[right];
            episode_channels[right] = channel_swap;
        }
}

static bool load_resume(int index)
{
    char path[PODCAST_PATH_MAX], line[PODCAST_MAX_CATALOG_LINE];
    int length = snprintf(path, sizeof(path), "%s/%s.tsv", PODCAST_RESUME_DIR,
                          episodes[index].id);
    if (length < 0 || (size_t)length >= sizeof(path)) return false;
    int fd = PODCAST_OPEN(path, O_RDONLY);
    if (fd < 0) return false;
    int result = podcast_read_line(fd, line, sizeof(line));
    bool valid = false;
    char *fields[5];
    uint64_t elapsed;
    if (result > 0 && split_fields(line, fields, 5) == 5 &&
        !strcmp(fields[0], "ROCKBOX-PODCAST-RESUME") && !strcmp(fields[1], "1") &&
        !strcmp(fields[2], episodes[index].id) && parse_unsigned(fields[3], &elapsed) &&
        (strcmp(fields[4], "0") == 0 || strcmp(fields[4], "1") == 0))
    {
        bool played = fields[4][0] == '1';
        if (podcast_read_line(fd, line, sizeof(line)) == 0)
        {
            episode_played[index] = played;
            episode_elapsed[index] = played ? 0 :
                elapsed > episodes[index].duration_ms ? episodes[index].duration_ms : (uint32_t)elapsed;
            valid = true;
        }
    }
    PODCAST_CLOSE(fd);
    return valid;
}

bool podcast_catalog_load(void)
{
    char line[PODCAST_MAX_CATALOG_LINE];
    int fd = PODCAST_OPEN(PODCAST_CATALOG_PATH, O_RDONLY);
    if (fd < 0) return false;
    if (podcast_read_line(fd, line, sizeof(line)) <= 0 || !parse_header(line))
    {
        PODCAST_CLOSE(fd);
        return false;
    }

    char retained_id[PODCAST_ID_MAX] = {0};
    bool retain_active = playback_active && active_episode_id[0];
    if (retain_active)
    {
        snprintf(retained_id, sizeof(retained_id), "%s", active_episode_id);
        save_active();
    }

    channel_count = episode_count = 0;
    memset(episode_elapsed, 0, sizeof(episode_elapsed));
    memset(episode_played, 0, sizeof(episode_played));
    catalog_loaded = true;
    while (true)
    {
        int result = podcast_read_line(fd, line, sizeof(line));
        if (result == 0) break;
        if (result == -2) break;
        if (result < 0) continue;
        char *fields[7];
        int count = split_fields(line, fields, 7);
        if (count == 3 && !strcmp(fields[0], "C")) parse_channel(fields);
        else if (count == 7 && !strcmp(fields[0], "E")) parse_episode(fields);
    }
    PODCAST_CLOSE(fd);
    sort_catalog();
    for (int i = 0; i < episode_count; i++) load_resume(i);
    if (retain_active)
    {
        active_episode = find_episode_id(retained_id);
        if (active_episode >= 0)
        {
            active_elapsed = episode_elapsed[active_episode];
            if (active_scan.duration_ms != episodes[active_episode].duration_ms)
            {
                active_scan.duration_ms = episodes[active_episode].duration_ms;
                if (active_scan.target_ms > active_scan.duration_ms)
                    active_scan.target_ms = active_scan.duration_ms;
            }
        }
        else
        {
            playback_active = false;
            active_episode = -1;
            active_episode_id[0] = 0;
            active_scan.active = false;
        }
    }
    else
        active_episode = -1;
    return true;
}

bool podcast_init(void)
{
    register_events();
    bool loaded = podcast_catalog_load();
#ifndef PODCAST_TEST
    if (loaded)
    {
        struct mp3entry *track = audio_current_track();
        if (track)
        {
            struct track_event event = {.flags = TEF_CURRENT, .id3 = track};
            podcast_playback_event(PLAYBACK_EVENT_TRACK_CHANGE, &event);
        }
    }
#endif
    return loaded;
}

void podcast_catalog_reset(void)
{
    channel_count = episode_count = 0;
    catalog_loaded = false;
    playback_active = false;
    active_episode = -1;
    active_episode_id[0] = 0;
    active_elapsed = 0;
    active_scan.active = false;
    checkpoint_started = false;
}

int podcast_channel_count(void) { return channel_count; }

const struct podcast_channel *podcast_channel_at(int channel)
{
    return channel >= 0 && channel < channel_count ? &channels[channel] : NULL;
}

int podcast_episode_count(int channel)
{
    int count = 0;
    if (channel < 0 || channel >= channel_count) return 0;
    for (int i = 0; i < episode_count; i++) if (episode_channels[i] == channel) count++;
    return count;
}

static int episode_index(int channel, int episode)
{
    if (channel < 0 || channel >= channel_count || episode < 0) return -1;
    for (int i = 0; i < episode_count; i++)
        if (episode_channels[i] == channel && episode-- == 0) return i;
    return -1;
}

const struct podcast_episode *podcast_episode_at(int channel, int episode)
{
    int index = episode_index(channel, episode);
    return index >= 0 ? &episodes[index] : NULL;
}

enum podcast_episode_status podcast_episode_status(int channel, int episode)
{
    int index = episode_index(channel, episode);
    if (index < 0) return PODCAST_EPISODE_NEW;
    if (episode_played[index]) return PODCAST_EPISODE_PLAYED;
    return episode_elapsed[index] ? PODCAST_EPISODE_SAVED : PODCAST_EPISODE_NEW;
}

uint32_t podcast_episode_resume(int channel, int episode)
{
    int index = episode_index(channel, episode);
    return index >= 0 ? episode_elapsed[index] : 0;
}

const char *podcast_episode_label(int channel, int episode, char *buf, size_t size)
{
    if (!podcast_episode_count(channel))
    {
#ifdef PODCAST_TEST
        snprintf(buf, size, "No episodes");
#else
        snprintf(buf, size, "%s", str(LANG_PODCAST_NO_EPISODES));
#endif
        return buf;
    }

    int index = episode_index(channel, episode);
    const struct podcast_episode *item = index >= 0 ? &episodes[index] : NULL;
    if (!item)
    {
        if (size) buf[0] = 0;
        return buf;
    }

    enum podcast_episode_status status = podcast_episode_status(channel, episode);
#ifdef PODCAST_TEST
    const char *label = status == PODCAST_EPISODE_PLAYED ? "Played" :
        status == PODCAST_EPISODE_SAVED ? "Saved" : "New";
#else
    const char *label = status == PODCAST_EPISODE_PLAYED ? str(LANG_PODCAST_PLAYED) :
        status == PODCAST_EPISODE_SAVED ? str(LANG_PODCAST_SAVED) : str(LANG_PODCAST_NEW);
#endif
    if (status == PODCAST_EPISODE_SAVED)
    {
        char elapsed[24];
#ifdef PODCAST_TEST
        unsigned long seconds = episode_elapsed[index] / 1000;
        snprintf(elapsed, sizeof(elapsed), "%lu:%02lu", seconds / 60, seconds % 60);
#else
        format_time(elapsed, sizeof(elapsed), episode_elapsed[index]);
#endif
        snprintf(buf, size, "%s %s: %s", label, elapsed, item->title);
    }
    else
        snprintf(buf, size, "%s: %s", label, item->title);
    return buf;
}

static bool save_episode_index(int index, uint32_t elapsed_ms, bool played)
{
    char path[PODCAST_PATH_MAX], temp[PODCAST_PATH_MAX];
    int length = snprintf(path, sizeof(path), "%s/%s.tsv", PODCAST_RESUME_DIR,
                          episodes[index].id);
    int temp_length = snprintf(temp, sizeof(temp), "%s.tmp", path);
    if (length < 0 || temp_length < 0 || (size_t)length >= sizeof(path) ||
        (size_t)temp_length >= sizeof(temp)) goto failed;
    if (PODCAST_MKDIR(ROCKBOX_DIR "/podcasts") < 0 && errno != EEXIST) goto failed;
    if (PODCAST_MKDIR(PODCAST_RESUME_DIR) < 0 && errno != EEXIST) goto failed;
    if (played) elapsed_ms = 0;
    if (elapsed_ms > episodes[index].duration_ms) elapsed_ms = episodes[index].duration_ms;
    char line[PODCAST_MAX_CATALOG_LINE];
    int line_length = snprintf(line, sizeof(line), "ROCKBOX-PODCAST-RESUME\t1\t%s\t%lu\t%d\n",
                               episodes[index].id, (unsigned long)elapsed_ms, played ? 1 : 0);
    if (line_length < 0 || (size_t)line_length >= sizeof(line)) goto failed;
    int fd = PODCAST_OPEN(temp, O_WRONLY | O_CREAT | O_TRUNC);
    if (fd < 0) goto failed;
    ssize_t written = PODCAST_WRITE(fd, line, (size_t)line_length);
    bool ok = written == line_length && PODCAST_SYNC(fd) == 0 && PODCAST_CLOSE(fd) == 0;
    if (!ok)
    {
        PODCAST_CLOSE(fd);
        PODCAST_REMOVE(temp);
        goto failed;
    }
    if (PODCAST_RENAME(temp, path) < 0)
    {
        PODCAST_REMOVE(temp);
        goto failed;
    }
    episode_elapsed[index] = elapsed_ms;
    episode_played[index] = played;
    return true;

failed:
    save_failed = true;
    return false;
}

bool podcast_episode_save(int channel, int episode, uint32_t elapsed_ms, bool played)
{
    int index = episode_index(channel, episode);
    return index >= 0 && save_episode_index(index, elapsed_ms, played);
}

bool podcast_save_failed(void)
{
    bool failed = save_failed;
    save_failed = false;
    return failed;
}

static void report_save_failure(void)
{
#ifndef PODCAST_TEST
    if (podcast_save_failed())
        splash(HZ * 2, ID2P(LANG_PODCAST_SAVE_FAILED));
#endif
}

static int find_episode_path(const char *path)
{
    char expected[PODCAST_PATH_MAX];
    for (int i = 0; i < episode_count; i++)
        if (path_for_id(episodes[i].id, expected, sizeof(expected)) && !strcmp(path, expected))
            return i;
    return -1;
}

static int active_index(void)
{
    if (!playback_active || !active_episode_id[0]) return -1;
    active_episode = find_episode_id(active_episode_id);
    return active_episode;
}

static uint32_t current_active_elapsed(void)
{
#ifndef PODCAST_TEST
    char active_path[PODCAST_PATH_MAX];
    struct mp3entry *track = audio_current_track();
    if (!track || !path_for_id(active_episode_id, active_path, sizeof(active_path)) ||
        strcmp(track->path, active_path))
        return active_elapsed;
#endif
    long elapsed = PODCAST_AUDIO_ELAPSED;
    if (elapsed < 0) return 0;
    if ((uint64_t)elapsed > UINT32_MAX) return UINT32_MAX;
    return (uint32_t)elapsed;
}

static bool save_active_elapsed(uint32_t elapsed)
{
    int index = active_index();
    if (index < 0) return true;
    bool ok = save_episode_index(index, elapsed, episode_played[index]);
    if (ok) active_elapsed = episode_elapsed[index];
    return ok;
}

static bool save_active(void)
{
    if (active_index() < 0) return true;
    uint32_t elapsed = active_scan.active ? (uint32_t)active_scan.target_ms :
                                           current_active_elapsed();
    return save_active_elapsed(elapsed);
}

static void podcast_playback_event(unsigned short id, void *param)
{
    struct track_event *event = (struct track_event *)param;
    if (id == PLAYBACK_EVENT_TRACK_CHANGE)
    {
        if (!event || !event->id3 || !(event->flags & TEF_CURRENT)) return;
        if (playback_active && active_episode_id[0])
        {
            char active_path[PODCAST_PATH_MAX];
            if (!path_for_id(active_episode_id, active_path, sizeof(active_path)) ||
                strcmp(active_path, event->id3->path))
                save_active_elapsed(current_active_elapsed());
        }
        int index = event && event->id3 ? find_episode_path(event->id3->path) : -1;
        playback_active = index >= 0;
        active_episode = index;
        active_scan.active = false;
        if (index >= 0)
        {
            snprintf(active_episode_id, sizeof(active_episode_id), "%s", episodes[index].id);
            active_elapsed = episode_elapsed[index];
            podcast_scan_init(&active_scan, episodes[index].duration_ms, active_elapsed);
            checkpoint_tick = PODCAST_NOW_TICK;
            checkpoint_started = true;
        }
        else
            active_episode_id[0] = 0;
    }
    else if (id == PLAYBACK_EVENT_TRACK_FINISH && playback_active && event &&
             event->id3 && (event->flags & TEF_CURRENT))
    {
        int index = active_index();
        char active_path[PODCAST_PATH_MAX];
        if (index < 0 || !path_for_id(active_episode_id, active_path, sizeof(active_path)) ||
            strcmp(active_path, event->id3->path)) return;

        uint32_t elapsed = event->id3->elapsed > episodes[index].duration_ms ?
            episodes[index].duration_ms : (uint32_t)event->id3->elapsed;
        if (event->flags & TEF_REWIND)
        {
            save_active_elapsed(elapsed);
            active_scan.active = false;
            return;
        }

        unsigned long tolerance = event->id3->length < 1000 ? event->id3->length : 1000;
        bool complete = (event->flags & TEF_AUTO_SKIP) && event->id3->elapsed > 0 &&
            (!event->id3->length || event->id3->elapsed >= event->id3->length - tolerance);
        save_episode_index(index, complete ? 0 : elapsed,
                           episode_played[index] || complete);
        active_scan.active = false;
        playback_active = false;
        active_episode = -1;
        active_episode_id[0] = 0;
    }
}

static void register_events(void)
{
    if (!events_registered)
    {
        events_registered = true;
        add_event(PLAYBACK_EVENT_TRACK_CHANGE, podcast_playback_event);
        add_event(PLAYBACK_EVENT_TRACK_FINISH, podcast_playback_event);
    }
}

bool podcast_start_episode(int channel, int episode)
{
    int index = episode_index(channel, episode);
    char path[PODCAST_PATH_MAX];
    if (!catalog_loaded || index < 0 || !path_for_id(episodes[index].id, path, sizeof(path)) ||
        !PODCAST_FILE_EXISTS(path)) return false;
    save_active();
    playback_active = false;
    active_episode = -1;
    active_episode_id[0] = 0;
    active_scan.active = false;
    PODCAST_AUDIO_STOP;
    if (PODCAST_PLAYLIST_CREATE < 0 || PODCAST_PLAYLIST_INSERT(path) < 0)
        return false;
    PODCAST_PLAYLIST_SYNC;
    if (PODCAST_PLAYLIST_SINGLE < 0) return false;
    register_events();
    playback_active = true;
    active_episode = index;
    snprintf(active_episode_id, sizeof(active_episode_id), "%s", episodes[index].id);
    active_elapsed = episode_elapsed[index];
    podcast_scan_init(&active_scan, episodes[index].duration_ms, episode_elapsed[index]);
    checkpoint_tick = PODCAST_NOW_TICK;
    checkpoint_started = true;
    PODCAST_PLAYLIST_START(0, episode_elapsed[index]);
    return true;
}

void podcast_scan_init(struct podcast_scan_state *scan, int64_t duration_ms,
                       int64_t current_ms)
{
    scan->active = false;
    scan->direction = 0;
    scan->speed = 0;
    scan->duration_ms = duration_ms < 0 ? 0 : duration_ms;
    scan->target_ms = current_ms < 0 ? 0 : current_ms > scan->duration_ms ?
        scan->duration_ms : current_ms;
    scan->last_ms = 0;
}

bool podcast_scan_tap(struct podcast_scan_state *scan, int direction, int64_t now_ms)
{
    if (direction != -1 && direction != 1) return false;
    if (scan->active) podcast_scan_tick(scan, now_ms);
    if (!scan->active)
    {
        scan->active = true;
        scan->direction = direction;
        scan->speed = 5;
    }
    else if (scan->direction != direction)
    {
        scan->direction = direction;
        scan->speed = 5;
    }
    else
        scan->speed = scan->speed == 5 ? 10 : scan->speed == 10 ? 15 : 5;
    scan->last_ms = now_ms;
    return true;
}

void podcast_scan_tick(struct podcast_scan_state *scan, int64_t now_ms)
{
    if (!scan->active || now_ms <= scan->last_ms) return;
    int64_t delta = now_ms - scan->last_ms;
    /* speed is podcast seconds advanced per real second, so both delta and
     * the target are milliseconds and the scale cancels out. */
    int64_t step = delta * scan->speed;
    if (step)
    {
        scan->target_ms += scan->direction * step;
        if (scan->target_ms < 0) scan->target_ms = 0;
        if (scan->target_ms > scan->duration_ms) scan->target_ms = scan->duration_ms;
    }
    scan->last_ms = now_ms;
}

void podcast_scan_commit(struct podcast_scan_state *scan, bool resume)
{
    (void)resume;
    scan->active = false;
    scan->direction = 0;
    scan->speed = 0;
    scan->last_ms = 0;
#ifndef PODCAST_TEST
    struct wps_state *state = get_wps_state();
    state->ff_rewind_count = 0;
    status_set_ffmode(0);
    FOR_NB_SCREENS(i) sb_set_title_text(NULL, Icon_NOICON, i);
    skin_request_full_update(WPS);
#endif
}

static void show_scan(const struct podcast_scan_state *scan)
{
#ifndef PODCAST_TEST
    struct wps_state *state = get_wps_state();
    struct mp3entry *id3 = audio_current_track();
    long offset = id3 ? (long)(scan->target_ms - id3->elapsed) : 0;
    state->ff_rewind_count = offset > INT_MAX ? INT_MAX :
        offset < INT_MIN ? INT_MIN : (int)offset;
    status_set_ffmode(scan->direction > 0 ? STATUS_FASTFORWARD : STATUS_FASTBACKWARD);
    snprintf(scan_title, sizeof(scan_title), "Podcast %c%dx",
             scan->direction > 0 ? '+' : '-',
             scan->speed);
    FOR_NB_SCREENS(i) sb_set_title_text(scan_title, Icon_Audio, i);
    skin_request_full_update(WPS);
#else
    (void)scan;
#endif
}

const char *podcast_scan_feedback(char *buf, size_t size)
{
    if (!playback_active || !active_scan.active) return NULL;
    unsigned long seconds = (unsigned long)(active_scan.target_ms / 1000);
    snprintf(buf, size, "%c%dx %lu:%02lu", active_scan.direction > 0 ? '>' : '<',
             active_scan.speed, seconds / 60, seconds % 60);
    return buf;
}

bool podcast_wps_action(int action)
{
    if (!playback_active || active_episode < 0) return false;
    if (action == PODCAST_ACTION_SKIPNEXT || action == PODCAST_ACTION_SKIPPREV)
    {
        if (!active_scan.active)
        {
            podcast_scan_init(&active_scan, episodes[active_episode].duration_ms,
                              PODCAST_AUDIO_ELAPSED);
            PODCAST_AUDIO_PAUSE;
        }
        podcast_scan_tap(&active_scan, action == PODCAST_ACTION_SKIPNEXT ? 1 : -1,
                         PODCAST_NOW_MS);
        show_scan(&active_scan);
        return true;
    }
    if (active_scan.active && (action == PODCAST_ACTION_SEEKFWD ||
                               action == PODCAST_ACTION_SEEKBACK ||
                               action == PODCAST_ACTION_STOPSEEK)) return true;
    if (active_scan.active && action == PODCAST_ACTION_PLAY)
    {
        podcast_scan_tick(&active_scan, PODCAST_NOW_MS);
        uint32_t target = (uint32_t)active_scan.target_ms;
        PODCAST_AUDIO_SEEK(target);
        podcast_scan_commit(&active_scan, true);
        PODCAST_AUDIO_RESUME;
        save_active_elapsed(target);
        report_save_failure();
        return true;
    }
    return false;
}

void podcast_wps_checkpoint(void)
{
    if (!playback_active) return;
    if (!active_scan.active) active_elapsed = current_active_elapsed();
    if (active_scan.active)
    {
        podcast_scan_tick(&active_scan, PODCAST_NOW_MS);
        show_scan(&active_scan);
    }
    long now = PODCAST_NOW_TICK;
    if (checkpoint_started && now - checkpoint_tick >= 30 * PODCAST_HZ)
    {
        save_active();
        report_save_failure();
        checkpoint_tick = now;
    }
}

void podcast_wps_pause_save(void)
{
    if (active_scan.active)
    {
        podcast_wps_exit();
        return;
    }
    if (playback_active)
    {
        save_active();
        report_save_failure();
    }
}

void podcast_wps_exit(void)
{
    if (!playback_active) return;
    if (active_scan.active)
    {
        podcast_scan_tick(&active_scan, PODCAST_NOW_MS);
        uint32_t target = (uint32_t)active_scan.target_ms;
        PODCAST_AUDIO_SEEK(target);
        PODCAST_AUDIO_PAUSE;
        podcast_scan_commit(&active_scan, false);
        save_active_elapsed(target);
        report_save_failure();
    }
    else
    {
        save_active();
        report_save_failure();
    }
}

#ifndef PODCAST_TEST
static const char *channel_item_name(int item, void *data, char *buf, size_t size)
{
    (void)data;
    (void)buf;
    (void)size;
    if (podcast_channel_count() == 0) return str(LANG_PODCAST_NO_EPISODES);
    return podcast_channel_at(item)->name;
}

static int episode_item_channel;

static const char *episode_item_name(int item, void *data, char *buf, size_t size)
{
    (void)data;
    return podcast_episode_label(episode_item_channel, item, buf, size);
}

static int browse_episodes(int channel)
{
    struct gui_synclist list;
    int result = GO_TO_ROOT;
    episode_item_channel = channel;
    gui_synclist_init(&list, episode_item_name, NULL, false, 1, NULL);
    gui_synclist_set_title(&list, podcast_channel_at(channel)->name, Icon_Audio);
    gui_synclist_set_nb_items(&list, podcast_episode_count(channel) ?: 1);
    while (true)
    {
        gui_synclist_draw(&list);
        int action;
        if (list_do_action(CONTEXT_LIST, HZ, &list, &action)) continue;
        int selected = gui_synclist_get_sel_pos(&list);
        if (action == ACTION_STD_CANCEL || action == ACTION_STD_MENU) break;
        if (action == ACTION_STD_OK)
        {
            if (!podcast_episode_count(channel))
                splash(HZ * 2, ID2P(LANG_PODCAST_NO_EPISODES));
            else if (podcast_start_episode(channel, selected))
            {
                result = GO_TO_WPS;
                break;
            }
            else
                splash(HZ * 2, ID2P(LANG_FAILED));
        }
        else if (action == ACTION_TREE_WPS && audio_status())
        {
            result = GO_TO_WPS;
            break;
        }
        else if (default_event_handler(action) == SYS_USB_CONNECTED)
            break;
    }
    gui_synclist_scroll_stop(&list);
    return result;
}

int podcast_screen(void)
{
    if (!podcast_init())
    {
        splash(HZ * 2, ID2P(LANG_FAILED));
        return GO_TO_ROOT;
    }
    struct gui_synclist list;
    int result = GO_TO_ROOT;
    FOR_NB_SCREENS(i) viewportmanager_theme_enable(i, true, NULL);
    gui_synclist_init(&list, channel_item_name, NULL, false, 1, NULL);
    gui_synclist_set_title(&list, str(LANG_PODCASTS), Icon_Audio);
    gui_synclist_set_nb_items(&list, podcast_channel_count() ?: 1);
    while (true)
    {
        gui_synclist_draw(&list);
        int action;
        if (list_do_action(CONTEXT_LIST, HZ, &list, &action)) continue;
        int selected = gui_synclist_get_sel_pos(&list);
        if (action == ACTION_STD_CANCEL || action == ACTION_STD_MENU) break;
        if (action == ACTION_STD_OK)
        {
            if (!podcast_channel_count())
                splash(HZ * 2, ID2P(LANG_PODCAST_NO_EPISODES));
            else
            {
                result = browse_episodes(selected);
                if (result == GO_TO_WPS) break;
            }
        }
        else if (action == ACTION_TREE_WPS && audio_status())
        {
            result = GO_TO_WPS;
            break;
        }
        else if (default_event_handler(action) == SYS_USB_CONNECTED)
            break;
    }
    gui_synclist_scroll_stop(&list);
    FOR_NB_SCREENS(i) viewportmanager_theme_undo(i, false);
    return result;
}
#endif
