/* Sansa Clip song-first Home UI. SPDX-License-Identifier: GPL-2.0-or-later */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "home_screen.h"
#include "music_library.h"
#include "home_autoplay.h"
#include "action.h"
#include "abrepeat.h"
#include "audio.h"
#include "filetypes.h"
#include "kernel.h"
#include "lang.h"
#include "language.h"
#include "list.h"
#include "metadata.h"
#include "misc.h"
#include "onplay.h"
#include "playlist.h"
#include "playlist_viewer.h"
#include "root_menu.h"
#include "settings.h"
#include "splash.h"
#include "statusbar-skinned.h"
#include "string-extra.h"
#include "talk.h"
#include "viewport.h"
#include "wps.h"

static struct home_picks picks;
static int playlist_count;
static unsigned int liked_revision;

static bool sample_track(const char *path, void *context)
{
    (void)context;
    static struct mp3entry id3;
    if (get_metadata(&id3, -1, path))
        home_sample_add(&picks, path, id3.title, id3.artist, id3.genre_string);
    return true;
}

static void refresh_songs(void)
{
    home_scores_compact();
    home_sample_begin(&picks, ((uint32_t)rand() << 16) ^ rand());
    if (file_exists(HOME_LIKED_PLAYLIST))
        home_liked_foreach(sample_track, NULL);
    liked_revision = home_liked_revision();
    picks.refreshed_at = (uint32_t)current_tick;
    picks.initialized = true;
}

int home_play_suggestion(const char *path)
{
    if (!file_exists(path))
    {
        splash(HZ * 2, ID2P(LANG_FILE_NOT_FOUND));
        return GO_TO_ROOT;
    }
    if (!warn_on_pl_erase())
        return GO_TO_ROOT;
    const char *queue = home_autoplay_prepare(path);
    if (!queue)
        return GO_TO_ROOT;
    ab_reset_markers();
    if (playlist_create(ROCKBOX_DIR, strrchr(queue, '/') + 1) < 0 ||
        playlist_set_home_autoplay() < 0)
    {
        splash(HZ * 2, ID2P(LANG_FAILED));
        return GO_TO_ROOT;
    }
    playlist_start(0, 0, 0);
    return GO_TO_WPS;
}

static int song_rows(void)
{
    return picks.count ? picks.count : 1;
}

static const char *item_name(int item, void *data, char *buf, size_t size)
{
    (void)data;
    if (item-- == 0) return str(LANG_SEARCH);
    if (item < song_rows())
        return picks.count ? picks.songs[item].label :
                             (const char *)str(LANG_HOME_NO_LIKED);
    item -= song_rows();
    if (item < playlist_count) return music_catalog_label(item, buf, size);
    item -= playlist_count;
    return str(item == 0 ? LANG_ARTISTS : item == 1 ? LANG_ALBUMS : LANG_SETTINGS);
}

static enum themable_icons item_icon(int item, void *data)
{
    (void)data;
    if (item-- == 0) return Icon_Submenu_Entered;
    if (item < song_rows())
        return Icon_Audio;
    return item < song_rows() + playlist_count ? Icon_Playlist : Icon_Submenu_Entered;
}

static int speak_item(int item, void *data)
{
    char buf[MAX_PATH * 2];
    return talk_spell(item_name(item, data, buf, sizeof(buf)), false);
}

int home_screen(void)
{
    if (liked_revision != home_liked_revision() ||
        home_refresh_due(&picks, (uint32_t)current_tick))
        refresh_songs();
    if (home_scores_save_failed())
        splash(HZ * 2, ID2P(LANG_HOME_SCORE_SAVE_FAILED));

    int result = GO_TO_ROOT;
    struct gui_synclist list;
    FOR_NB_SCREENS(i)
    {
        sb_set_persistent_title(str(LANG_ROCKBOX_TITLE), Icon_Rockbox, i);
        viewportmanager_theme_enable(i, true, NULL);
    }
    gui_synclist_init(&list, item_name, NULL, false, 1, NULL);
    gui_synclist_set_title(&list, str(LANG_ROCKBOX_TITLE), Icon_Rockbox);
    gui_synclist_set_icon_callback(&list, item_icon);
    gui_synclist_set_voice_callback(&list, speak_item);

    while (true)
    {
        playlist_count = music_catalog_open();
        if (playlist_count < 0)
        {
            playlist_count = 0;
            splash(HZ * 2, ID2P(LANG_FAILED));
        }
        gui_synclist_set_nb_items(&list, song_rows() + playlist_count + 4);
        gui_synclist_select_item(&list, MIN(home_selection, list.nb_items - 1));
        FOR_NB_SCREENS(i)
            sb_set_persistent_title(str(LANG_ROCKBOX_TITLE), Icon_Rockbox, i);
        gui_synclist_draw(&list);
        gui_synclist_speak_item(&list);

        bool reload = false;
        while (!reload)
        {
            keyclick_set_callback(gui_synclist_keyclick_callback, &list);
            int action = get_action(CONTEXT_CUSTOM | CONTEXT_MAINMENU | ALLOW_SOFTLOCK,
                                    list_do_action_timeout(&list, HZ));
            int selected = gui_synclist_get_sel_pos(&list);
            if (home_manual_refresh(action, selected))
            {
                refresh_songs();
                home_selection = 0;
                break;
            }
            if (gui_synclist_do_button(&list, &action))
                continue;
            home_selection = gui_synclist_get_sel_pos(&list);
            int song = home_selection - 1;
            int pin = song - song_rows();
            switch (action)
            {
                case ACTION_STD_OK:
                    if (home_selection == 0)
                    {
                        music_library_request(MUSIC_SEARCH, NULL);
                        goto done;
                    }
                    if (song >= 0 && song < picks.count)
                    {
                        result = home_play_suggestion(picks.songs[song].path);
                        goto done;
                    }
                    if (pin >= 0 && pin < playlist_count)
                    {
                        if (!file_exists(music_catalog_path(pin)))
                            splash(HZ * 2, ID2P(LANG_FILE_NOT_FOUND));
                        else
                        {
                            enum playlist_viewer_result ret =
                                playlist_viewer_ex(music_catalog_path(pin), NULL);
                            if (ret == PLAYLIST_VIEWER_OK)
                            {
                                result = GO_TO_WPS;
                                goto done;
                            }
                            if (ret == PLAYLIST_VIEWER_USB ||
                                ret == PLAYLIST_VIEWER_MAINMENU)
                            {
                                home_selection = 0;
                                goto done;
                            }
                            if (home_refresh_due(&picks, (uint32_t)current_tick))
                                refresh_songs();
                        }
                        reload = true;
                    }
                    else if (pin >= playlist_count)
                    {
                        if (pin < playlist_count + 2)
                            music_library_request(pin == playlist_count ? MUSIC_ARTISTS : MUSIC_ALBUMS, NULL);
                        else home_in_settings = true;
                        goto done;
                    }
                    break;
                case ACTION_STD_CONTEXT:
                    if (song >= 0 && song < picks.count)
                    {
                        int ret = onplay(picks.songs[song].path, FILE_ATTR_AUDIO,
                                         CONTEXT_TREE, false, ONPLAY_NO_CUSTOMACTION);
                        if (ret == ONPLAY_MAINMENU) goto done;
                        if (ret == ONPLAY_START_PLAY) { result = GO_TO_WPS; goto done; }
                        if (ret == ONPLAY_REVEAL_FILE || ret == ONPLAY_PLUGIN)
                        { result = ret == ONPLAY_PLUGIN ? GO_TO_PLUGIN : GO_TO_FILEBROWSER; goto done; }
                        reload = true;
                    }
                    else if (pin >= 0 && pin < playlist_count)
                    {
                        char playlist[MAX_PATH];
                        strlcpy(playlist, music_catalog_path(pin), sizeof(playlist));
                        int ret = onplay(playlist, FILE_ATTR_M3U, CONTEXT_TREE,
                                         false, ONPLAY_NO_CUSTOMACTION);
                        if (ret == ONPLAY_START_PLAY)
                        {
                            result = GO_TO_WPS;
                            goto done;
                        }
                        if (ret == ONPLAY_MAINMENU)
                            goto done;
                        if (ret == ONPLAY_REVEAL_FILE || ret == ONPLAY_PLUGIN)
                        {
                            result = ret == ONPLAY_PLUGIN ? GO_TO_PLUGIN :
                                                            GO_TO_FILEBROWSER;
                            goto done;
                        }
                        reload = true;
                    }
                    break;
                case ACTION_STD_MENU:
                case ACTION_STD_CANCEL:
                    home_selection = 0;
                    reload = true;
                    break;
                case ACTION_TREE_WPS:
                    if (audio_status() & AUDIO_STATUS_PLAY)
                    { result = GO_TO_WPS; goto done; }
                    break;
                case ACTION_TREE_STOP:
                    list_stop_handler();
                    reload = true;
                    break;
                default:
                    if (default_event_handler(action) == SYS_USB_CONNECTED)
                        goto done;
                    break;
            }
        }
    }
done:
    music_catalog_close();
    keyclick_set_callback(NULL, NULL);
    talk_shutup();
    gui_synclist_scroll_stop(&list);
    FOR_NB_SCREENS(i)
        viewportmanager_theme_undo(i, false);
    return result;
}
