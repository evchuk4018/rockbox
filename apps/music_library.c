/* Database-backed Clip library and shared song actions.
 * SPDX-License-Identifier: GPL-2.0-or-later */
#include "config.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#include "music_library.h"
#include "string-extra.h"
#include "core_alloc.h"
#include "dir.h"
#include "file.h"
#include "filetypes.h"
#include "playlist.h"
#include "playlist_catalog.h"
#include "home_screen.h"
#include "home_autoplay.h"
#include "tagcache.h"
#include "metadata.h"
#include "audio.h"
#include "action.h"
#include "list.h"
#include "menu.h"
#include "onplay.h"
#include "root_menu.h"
#include "lang.h"
#include "language.h"
#include "splash.h"
#include "misc.h"
#include "talk.h"
#include "kernel.h"
#include "viewport.h"
#include "keyboard.h"
#include "font.h"

static bool pending;
static enum music_view requested;
static char requested_path[MAX_PATH];

void music_library_request(enum music_view view, const char *path)
{
    requested = view;
    strlcpy(requested_path, path ? path : "", sizeof(requested_path));
    pending = true;
}
bool music_library_pending(void) { return pending; }

static int catalog_handle;
static int catalog_count;
static char catalog_dir[MAX_PATH];
static char *catalog_base;

static int catalog_walk(char *path, int *count, size_t *bytes, char *data,
                        size_t capacity, int limit)
{
    DIR *dir = opendir(path);
    if (!dir) return dir_exists(path) ? -1 : 0;
    size_t prefix = strlen(path);
    struct dirent *entry;
    int result = 0;
    while ((entry = readdir(dir)))
    {
        if (!strcmp(entry->d_name, ".") || !strcmp(entry->d_name, "..")) continue;
        if (prefix + strlen(entry->d_name) + 2 > MAX_PATH) { result = -1; break; }
        path[prefix] = '/';
        strlcpy(path + prefix + 1, entry->d_name, MAX_PATH - prefix - 1);
        struct dirinfo info = dir_get_info(dir, entry);
        if (info.attribute & ATTR_DIRECTORY)
            result = catalog_walk(path, count, bytes, data, capacity, limit);
        else
        {
            const char *ext = strrchr(path, '.');
            if (ext && (!strcasecmp(ext, ".m3u") || !strcasecmp(ext, ".m3u8")))
            {
                size_t length = strlen(path) + 1;
                if (data)
                {
                    if (*count >= limit || length > capacity - *bytes) { result = -1; break; }
                    ((int *)data)[*count] = *bytes;
                    memcpy(data + *bytes, path, length);
                }
                (*count)++;
                *bytes += length;
            }
        }
        path[prefix] = 0;
        if (result < 0) break;
        yield();
    }
    path[prefix] = 0;
    closedir(dir);
    return result;
}
static int catalog_stem_compare(const char *left, const char *right)
{
    size_t a = strrchr(left, '.') - left, b = strrchr(right, '.') - right;
    int cmp = strncasecmp(left, right, a < b ? a : b);
    return cmp ? cmp : (a > b) - (a < b);
}
static int catalog_compare(const void *a, const void *b)
{
    const char *left = catalog_base + *(const int *)a;
    const char *right = catalog_base + *(const int *)b;
    int cmp = catalog_stem_compare(strrchr(left, '/') + 1, strrchr(right, '/') + 1);
    return cmp ? cmp : strcmp(left, right);
}
void music_catalog_close(void)
{
    if (catalog_handle > 0) { core_unpin(catalog_handle); core_free(catalog_handle); }
    catalog_handle = catalog_count = 0;
    catalog_base = NULL;
}
int music_catalog_open(void)
{
    music_catalog_close();
    catalog_get_directory(catalog_dir, sizeof(catalog_dir));
    if (!catalog_dir[0]) return -1;
    size_t dirlen = strlen(catalog_dir);
    while (dirlen > 1 && catalog_dir[dirlen - 1] == '/') catalog_dir[--dirlen] = 0;
    char path[MAX_PATH];
    strlcpy(path, catalog_dir, sizeof(path));
    size_t bytes = 0;
    int count = 0;
    if (catalog_walk(path, &count, &bytes, NULL, 0, 0) < 0) return -1;
    if (!count) return 0;
    if (bytes > INT_MAX - count * sizeof(int)) return -1;
    size_t size = bytes + count * sizeof(int);
    catalog_handle = core_alloc(size);
    if (catalog_handle <= 0) { catalog_handle = 0; return -1; }
    core_pin(catalog_handle);
    catalog_base = core_get_data(catalog_handle);
    bytes = count * sizeof(int);
    int actual = 0;
    if (catalog_walk(path, &actual, &bytes, catalog_base, size, count) < 0)
    { music_catalog_close(); return -1; }
    catalog_count = actual;
    qsort(catalog_base, actual, sizeof(int), catalog_compare);
    return actual;
}
const char *music_catalog_path(int index)
{
    return index >= 0 && index < catalog_count ?
        catalog_base + ((int *)catalog_base)[index] : NULL;
}
const char *music_catalog_label(int index, char *buf, size_t size)
{
    const char *path = music_catalog_path(index);
    if (!path) return "";
    const char *name = strrchr(path, '/') + 1;
    bool duplicate = false, same_folder = false;
    for (int i = 0; i < catalog_count; i++)
        if (i != index && !catalog_stem_compare(name, strrchr(music_catalog_path(i), '/') + 1))
        {
            duplicate = true;
            const char *other = music_catalog_path(i);
            size_t parent = name - path;
            if ((size_t)(strrchr(other, '/') + 1 - other) == parent && !strncmp(path, other, parent))
                same_folder = true;
        }
    strlcpy(buf, duplicate ? path + strlen(catalog_dir) + 1 : name, size);
    char *ext = strrchr(buf, '.');
    if (ext && !same_folder) *ext = 0;
    return buf;
}

int music_song_menu(const char *path)
{
    char song[MAX_PATH];
    if (!path || path[0] != '/' || strlcpy(song, path, sizeof(song)) >= sizeof(song))
        return ONPLAY_OK;
    /* Consume the initiating hold/release before the next menu reads input. */
    action_wait_for_release();
    int liked = home_liked_state(song);
    const char *items[] = { str(LANG_PLAY_NEXT), str(LANG_MUSIC_ADD_QUEUE),
        str(LANG_ADD_TO_PL), str(liked > 0 ? LANG_MUSIC_UNLIKE : LANG_MUSIC_LIKE),
        str(LANG_MUSIC_GO_ARTIST), str(LANG_MUSIC_GO_ALBUM), str(LANG_MUSIC_MORE) };
    struct menu_item_ex menu = { MT_RETURN_ID | MENU_HAS_DESC |
        MENU_ITEM_COUNT(ARRAYLEN(items)), { .strings = items },
        { .callback_and_desc = NULL } };
    struct menu_callback_with_desc desc = { NULL, ID2P(LANG_ONPLAY_MENU_TITLE), Icon_Audio };
    menu.callback_and_desc = &desc;
    int choice = do_menu(&menu, NULL, NULL, false);
    if (choice == MENU_ATTACHED_USB) return ONPLAY_MAINMENU;
    if (choice < 0) return ONPLAY_OK;
    if (choice == 6) return MUSIC_MENU_MORE;
    if (!file_exists(song)) { splash(HZ * 2, ID2P(LANG_FILE_NOT_FOUND)); return ONPLAY_OK; }
    switch (choice)
    {
        case 0:
        case 1:
            if (playlist_insert_track(NULL, song, choice == 0 ? PLAYLIST_INSERT_FIRST :
                                      PLAYLIST_INSERT_LAST, true, true) < 0)
                splash(HZ * 2, ID2P(LANG_FAILED));
            else
            {
                home_autoplay_cancel_genre();
                playlist_set_modified(NULL, true);
                splash(HZ, ID2P(LANG_PLAYLIST_ADDED));
            }
            break;
        case 2: catalog_add_track_to_a_playlist(song); break;
        case 3:
            if (home_toggle_like(song) < 0) splash(HZ * 2, ID2P(LANG_HOME_LIKE_FAILED));
            break;
        case 4:
        case 5:
            music_library_request(choice == 4 ? MUSIC_ARTIST : MUSIC_ALBUM, song);
            return ONPLAY_MAINMENU;
    }
    return ONPLAY_OK;
}

/* One exact-size allocation holds the database snapshot, indexes and strings.
 * This deliberately never takes all available RAM away from audio playback. */
struct library_track {
    int title, artist, album, path, disc, number, performer;
    uint32_t score;
};
static struct library_track *tracks;
static int *visible, track_count, visible_count;
static char *strings;
static bool album_sort, song_sort;
static char fields[5][TAG_MAXLEN];
static struct tagcache_search search;
static const char *field(int offset) { return strings + offset; }

static size_t read_fields(void)
{
    const int tags[] = { tag_title, tag_albumartist, tag_album };
    for (int i = 0; i < 3; i++)
    {
        fields[i][0] = 0;
        tagcache_retrieve(&search, search.idx_id, tags[i], fields[i], sizeof(fields[i]));
    }
    fields[4][0] = 0;
    tagcache_retrieve(&search, search.idx_id, tag_artist, fields[4], sizeof(fields[4]));
    if (!fields[1][0] || !strcmp(fields[1], UNTAGGED))
        strlcpy(fields[1], fields[4], sizeof(fields[1]));
    if (!fields[4][0] || !strcmp(fields[4], UNTAGGED))
        strlcpy(fields[4], fields[1], sizeof(fields[4]));
    if (!fields[0][0] || !strcmp(fields[0], UNTAGGED))
        strlcpy(fields[0], strrchr(fields[3], '/') + 1, sizeof(fields[0]));
    size_t bytes = 0;
    for (int i = 0; i < 5; i++) bytes += strlen(fields[i]) + 1;
    return bytes;
}
static int library_compare(const void *a, const void *b)
{
    const struct library_track *x = a, *y = b;
    if (song_sort)
    {
        if (x->score != y->score) return x->score < y->score ? 1 : -1;
        int cmp = strcasecmp(field(x->title), field(y->title));
        if (!cmp) cmp = strcasecmp(field(x->performer), field(y->performer));
        if (!cmp) cmp = strcasecmp(field(x->album), field(y->album));
        return cmp ? cmp : strcmp(field(x->path), field(y->path));
    }
    int cmp = strcasecmp(field(album_sort ? x->album : x->artist),
                         field(album_sort ? y->album : y->artist));
    if (!cmp) cmp = strcasecmp(field(album_sort ? x->artist : x->album),
                               field(album_sort ? y->artist : y->album));
    if (!cmp) cmp = (x->disc > y->disc) - (x->disc < y->disc);
    if (!cmp) cmp = (x->number > y->number) - (x->number < y->number);
    if (!cmp) cmp = strcasecmp(field(x->title), field(y->title));
    return cmp ? cmp : strcmp(field(x->path), field(y->path));
}
static int library_open(void)
{
    size_t bytes = 0;
    int count = 0;
    if (!tagcache_search(&search, tag_filename)) return -1;
    while (tagcache_get_next(&search, fields[3], sizeof(fields[3])))
    {
        bytes += read_fields();
        count++;
        yield();
    }
    tagcache_search_finish(&search);
    if (!count || bytes > INT_MAX - count * (sizeof(*tracks) + sizeof(int))) return -1;
    size_t size = bytes + count * (sizeof(*tracks) + sizeof(int));
    int handle = core_alloc(size);
    if (handle <= 0) return -1;
    core_pin(handle);
    tracks = core_get_data(handle);
    visible = (int *)(tracks + count);
    strings = (char *)(visible + count);
    size_t used = 0;
    track_count = 0;
    if (!tagcache_search(&search, tag_filename)) goto fail;
    while (tagcache_get_next(&search, fields[3], sizeof(fields[3])))
    {
        size_t length = read_fields();
        if (track_count >= count || length > bytes - used)
        { tagcache_search_finish(&search); goto fail; }
        struct library_track *track = &tracks[track_count++];
        int *offsets[] = { &track->title, &track->artist, &track->album,
                          &track->path, &track->performer };
        for (int i = 0; i < 5; i++)
        {
            *offsets[i] = used;
            length = strlen(fields[i]) + 1;
            memcpy(strings + used, fields[i], length);
            used += length;
        }
        track->disc = tagcache_get_numeric(&search, tag_discnumber);
        track->number = tagcache_get_numeric(&search, tag_tracknumber);
        yield();
    }
    tagcache_search_finish(&search);
    return handle;
fail:
    core_unpin(handle);
    core_free(handle);
    return -1;
}

enum library_page { PAGE_ARTISTS, PAGE_ALBUMS, PAGE_ARTIST_ALBUMS, PAGE_TRACKS, PAGE_ARTIST_TRACKS, PAGE_SEARCH };
static enum library_page page;
static char artist[TAG_MAXLEN], album[TAG_MAXLEN], query[MAX_PATH];
static bool same(const char *a, const char *b) { return !strcasecmp(a, b); }
static bool library_searchable(void)
{
    return page == PAGE_ARTISTS || page == PAGE_ALBUMS || page == PAGE_SEARCH;
}
static int library_header_rows(void)
{
    return library_searchable() || page == PAGE_ARTIST_ALBUMS;
}
static bool library_matches(const struct library_track *track)
{
    if (!query[0] || !library_searchable()) return true;
    if (page == PAGE_ARTISTS) return strcasestr(field(track->artist), query) != NULL;
    if (page == PAGE_ALBUMS) return strcasestr(field(track->album), query) != NULL;
    return strcasestr(field(track->title), query) ||
           strcasestr(field(track->performer), query) ||
           strcasestr(field(track->artist), query) ||
           strcasestr(field(track->album), query);
}
/* Work on a copy: the keyboard also modifies its buffer when canceled. */
static bool library_edit_query(void)
{
    /* Search ignores case: large capitals, digits and a visible space key. */
    ucschar_t layout[] = {
        13, 'A','B','C','D','E','F','G','H','I','J','K','L','M',
        13, 'N','O','P','Q','R','S','T','U','V','W','X','Y','Z',
        11, '0','1','2','3','4','5','6','7','8','9',0x2423,
        0xFEFF
    };
    char text[sizeof(query)];
    strlcpy(text, query, sizeof(text));
    action_wait_for_release();
    int font = font_load(FONT_DIR "/12-ProFont.fnt");
    int ret = kbd_input_with_font(text, sizeof(text), layout, font);
    if (font >= 0) font_unload(font);
    if (ret < 0) return false;
    strlcpy(query, text, sizeof(query));
    return true;
}
static void library_rows(void)
{
    album_sort = page == PAGE_ALBUMS;
    song_sort = page == PAGE_SEARCH;
    if (song_sort)
    {
        uint32_t now = home_score_time();
        for (int i = 0; i < track_count; i++)
        {
            tracks[i].score = home_score_get(field(tracks[i].path), now);
            yield();
        }
    }
    qsort(tracks, track_count, sizeof(*tracks), library_compare);
    visible_count = 0;
    for (int i = 0; i < track_count; i++)
    {
        struct library_track *track = &tracks[i];
        if (album_sort)
        {
            int end = i + 1;
            while (end < track_count &&
                   same(field(track->artist), field(tracks[end].artist)) &&
                   same(field(track->album), field(tracks[end].album))) end++;
            if (end - i > 1 && library_matches(track)) visible[visible_count++] = i;
            i = end - 1;
            continue;
        }
        if (!library_matches(track)) continue;
        if (!library_searchable() && !same(field(track->artist), artist)) continue;
        if (page == PAGE_TRACKS && !same(field(track->album), album)) continue;
        if (visible_count && page <= PAGE_ARTIST_ALBUMS)
        {
            struct library_track *previous = &tracks[visible[visible_count - 1]];
            if (same(field(track->artist), field(previous->artist)) &&
                (page == PAGE_ARTISTS || same(field(track->album), field(previous->album))))
                continue;
        }
        visible[visible_count++] = i;
    }
}
static const char *library_name(int item, void *unused, char *buf, size_t size)
{
    (void)unused;
    if (library_searchable() && item-- == 0) return str(LANG_SEARCH);
    if (page == PAGE_ARTIST_ALBUMS && item-- == 0) return str(LANG_MUSIC_ALL_SONGS);
    if (item >= visible_count) return str(LANG_MUSIC_NO_MATCHES);
    struct library_track *track = &tracks[visible[item]];
    if (page == PAGE_ARTISTS) return field(track->artist);
    if (page == PAGE_ARTIST_ALBUMS) return field(track->album);
    if (page == PAGE_SEARCH)
        snprintf(buf, size, "%s — %s", field(track->title), field(track->performer));
    else if (page == PAGE_ALBUMS)
        snprintf(buf, size, "%s - %s", field(track->album), field(track->artist));
    else if (track->number > 0)
        snprintf(buf, size, "%d.%02d %s", track->disc > 0 ? track->disc : 1,
                 track->number, field(track->title));
    else strlcpy(buf, field(track->title), size);
    return buf;
}
int music_library_screen(void *unused)
{
    (void)unused;
    enum music_view view = requested;
    pending = false;
    query[0] = 0;
    if (!tagcache_is_usable())
    { splash(HZ * 3, ID2P(LANG_HOME_DATABASE)); return GO_TO_ROOT; }
    if (view == MUSIC_SEARCH && (!library_edit_query() || !query[0])) return GO_TO_ROOT;
    splash(0, ID2P(LANG_WAIT));
    int handle = library_open();
    if (handle < 0) { splash(HZ * 2, ID2P(LANG_FAILED)); return GO_TO_ROOT; }
    bool root_artists = view == MUSIC_ARTISTS || view == MUSIC_ARTIST;
    page = view == MUSIC_SEARCH ? PAGE_SEARCH : root_artists ? PAGE_ARTISTS : PAGE_ALBUMS;
    artist[0] = album[0] = 0;
    if (view == MUSIC_ARTIST || view == MUSIC_ALBUM)
    {
        bool found = false;
        for (int i = 0; i < track_count; i++)
            if (!strcmp(field(tracks[i].path), requested_path))
            {
                strlcpy(artist, field(tracks[i].artist), sizeof(artist));
                strlcpy(album, field(tracks[i].album), sizeof(album));
                found = true;
                break;
            }
        if (!found)
        { splash(HZ * 3, ID2P(LANG_HOME_DATABASE)); core_unpin(handle); core_free(handle); return GO_TO_ROOT; }
        page = view == MUSIC_ARTIST ? PAGE_ARTIST_ALBUMS : PAGE_TRACKS;
    }
    int result = GO_TO_ROOT;
    struct gui_synclist list;
    FOR_NB_SCREENS(i) viewportmanager_theme_enable(i, true, NULL);
    gui_synclist_init(&list, library_name, NULL, false, 1, NULL);
    bool reload = true;
    int saved_selection[PAGE_SEARCH + 1] = {0};
    int restore_selection = 0;
    while (true)
    {
        if (reload)
        {
            library_rows();
            gui_synclist_set_title(&list, page == PAGE_ARTISTS ? (const char *)str(LANG_ARTISTS) :
                page == PAGE_ALBUMS ? (const char *)str(LANG_ALBUMS) :
                page == PAGE_SEARCH ? query : page == PAGE_TRACKS ? album : artist, Icon_Audio);
            gui_synclist_set_nb_items(&list, (visible_count ? visible_count : 1) + library_header_rows());
            gui_synclist_select_item(&list, restore_selection);
            restore_selection = 0;
            reload = false;
        }
        gui_synclist_draw(&list);
        int action;
        if (list_do_action(CONTEXT_LIST, HZ, &list, &action)) continue;
        int selected = gui_synclist_get_sel_pos(&list);
        if (action == ACTION_STD_CANCEL)
        {
            if (library_searchable()) break;
            page = (page == PAGE_TRACKS || page == PAGE_ARTIST_TRACKS) && root_artists ?
                PAGE_ARTIST_ALBUMS : root_artists ? PAGE_ARTISTS : PAGE_ALBUMS;
            restore_selection = saved_selection[page];
            reload = true;
        }
        else if (action == ACTION_STD_MENU) break;
        else if (action == ACTION_TREE_WPS) { if (audio_status()) result = GO_TO_WPS; break; }
        else if (action == ACTION_STD_OK && library_searchable() && selected == 0)
        {
            if (library_edit_query())
            {
                if (page == PAGE_SEARCH && !query[0]) break;
                reload = true;
            }
        }
        else if (action == ACTION_STD_OK && visible_count)
        {
            saved_selection[page] = selected;
            if (page == PAGE_ARTIST_ALBUMS && selected == 0)
            { page = PAGE_ARTIST_TRACKS; reload = true; continue; }
            int row = selected - library_header_rows();
            struct library_track *track = &tracks[visible[row]];
            if (page <= PAGE_ARTIST_ALBUMS)
            {
                strlcpy(artist, field(track->artist), sizeof(artist));
                strlcpy(album, field(track->album), sizeof(album));
                page = page == PAGE_ARTISTS ? PAGE_ARTIST_ALBUMS : PAGE_TRACKS;
                reload = true;
            }
            else if (warn_on_pl_erase())
            {
                if (!file_exists(field(track->path))) { splash(HZ * 2, ID2P(LANG_FILE_NOT_FOUND)); continue; }
                audio_stop();
                if (playlist_create(NULL, NULL) < 0) { splash(HZ * 2, ID2P(LANG_FAILED)); continue; }
                int start = -1;
                bool ok = true;
                for (int i = 0; i < visible_count; i++)
                {
                    int inserted = playlist_insert_track(NULL, field(tracks[visible[i]].path), PLAYLIST_INSERT_LAST, false, false);
                    if (inserted < 0) { ok = false; break; }
                    if (i == row) start = inserted;
                }
                playlist_sync(NULL);
                if (ok && start >= 0) { playlist_start(start, 0, 0); result = GO_TO_WPS; break; }
                splash(HZ * 2, ID2P(LANG_FAILED));
            }
        }
        else if (action == ACTION_STD_CONTEXT && page >= PAGE_TRACKS && visible_count &&
                 selected >= library_header_rows())
        {
            char path[MAX_PATH];
            strlcpy(path, field(tracks[visible[selected - library_header_rows()]].path), sizeof(path));
            int ret = onplay(path, FILE_ATTR_AUDIO, CONTEXT_TREE, false, ONPLAY_NO_CUSTOMACTION);
            if (pending || ret == ONPLAY_MAINMENU) break;
            if (ret == ONPLAY_START_PLAY) { result = GO_TO_WPS; break; }
            if (ret == ONPLAY_PLUGIN || ret == ONPLAY_REVEAL_FILE)
            { result = ret == ONPLAY_PLUGIN ? GO_TO_PLUGIN : GO_TO_FILEBROWSER; break; }
        }
        else if (default_event_handler(action) == SYS_USB_CONNECTED) break;
    }
    talk_shutup();
    gui_synclist_scroll_stop(&list);
    FOR_NB_SCREENS(i) viewportmanager_theme_undo(i, false);
    core_unpin(handle);
    core_free(handle);
    return result;
}
