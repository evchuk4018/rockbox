/* Clip music navigation. SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef MUSIC_LIBRARY_H
#define MUSIC_LIBRARY_H
#include <stdbool.h>
#include <stddef.h>

enum music_view { MUSIC_ARTISTS, MUSIC_ALBUMS, MUSIC_ARTIST, MUSIC_ALBUM, MUSIC_SEARCH };
void music_library_request(enum music_view view, const char *path);
bool music_library_pending(void);
int music_library_screen(void *unused);
/* A stable, compact catalog snapshot. No pointers survive close. */
int music_catalog_open(void);
void music_catalog_close(void);
const char *music_catalog_path(int index);
const char *music_catalog_label(int index, char *buf, size_t size);
enum { MUSIC_MENU_MORE = -100 };
/* Returns MUSIC_MENU_MORE, otherwise an onplay_return_code. */
int music_song_menu(const char *path);
#endif
