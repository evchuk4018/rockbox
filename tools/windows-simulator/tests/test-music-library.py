"""Compile production catalog, grouping and song actions with host services."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("home_tests", Path(__file__).with_name("test-home.py"))
home = importlib.util.module_from_spec(spec)
spec.loader.exec_module(home)


class MusicTests(unittest.TestCase):
    def test_production_catalog_groups_and_song_actions(self):
        production = (home.ROOT / "apps/music_library.c").read_text(encoding="utf-8")
        signatures = ["void music_library_request(", "bool music_library_pending(",
                      "static int catalog_walk(", "static int catalog_stem_compare(", "static int catalog_compare(",
                      "void music_catalog_close(", "int music_catalog_open(",
                      "const char *music_catalog_path(", "const char *music_catalog_label(",
                      "int music_song_menu(", "static const char *field(",
                      "static int library_compare(", "static bool same(",
                      "static void library_rows(", "static const char *library_name("]
        functions = "\n".join(home.function(production, name) for name in signatures)
        home_source = (home.ROOT / "apps/home_screen.c").read_text(encoding="utf-8")
        home_case = home_source.split("                case ACTION_TREE_WPS:", 1)[1].split("                case ACTION_TREE_STOP:", 1)[0]
        functions += "\nstatic int audio_state; static int audio_status(void) { return audio_state; }\n"
        functions += "static int return_home(int status) { audio_state=status; int result=GO_TO_ROOT; switch(ACTION_TREE_WPS) { case ACTION_TREE_WPS:" + home_case + "} done: return result; }\n"

        prefix = r'''
#include <assert.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#include <dirent.h>
#include <sys/stat.h>
#define MAX_PATH 260
#define TAG_MAXLEN 520
#define ATTR_DIRECTORY 16
#define HZ 100
#define ARRAYLEN(x) (sizeof(x)/sizeof((x)[0]))
static size_t strlcpy(char *d,const char *s,size_t n) { size_t l=strlen(s); if(n) {size_t c=l<n-1?l:n-1;memcpy(d,s,c);d[c]=0;}return l; }
typedef struct { DIR *native; char path[1024]; } test_dir;
struct dirinfo { int attribute; };
static test_dir *host_opendir(const char *p) { DIR *d=opendir(p); if(!d)return NULL;test_dir *r=malloc(sizeof(*r));r->native=d;strlcpy(r->path,p,sizeof(r->path));return r; }
static struct dirent *host_readdir(test_dir *d) { return readdir(d->native); }
static int host_closedir(test_dir *d) { int n=closedir(d->native);free(d);return n; }
static struct dirinfo dir_get_info(test_dir *d,struct dirent *e) { char p[1500];struct stat s;snprintf(p,sizeof(p),"%s/%s",d->path,e->d_name);assert(!stat(p,&s));return (struct dirinfo){S_ISDIR(s.st_mode)?ATTR_DIRECTORY:0}; }
#define DIR test_dir
#define opendir host_opendir
#define readdir host_readdir
#define closedir host_closedir
static bool dir_exists(const char *p) { struct stat s;return !stat(p,&s)&&S_ISDIR(s.st_mode); }
static bool file_exists(const char *p) { return p && p[0]=='/'; }
static void yield(void) {}
static void *allocation;
static int pins, fail_alloc;
static int core_alloc(size_t size) { if(fail_alloc)return -1;assert(!allocation);allocation=malloc(size);return allocation?1:-1; }
static void core_pin(int h) { assert(h==1);pins++; }
static void core_unpin(int h) { assert(h==1);pins--; }
static void *core_get_data(int h) { assert(h==1&&pins);return allocation; }
static void core_free(int h) { assert(h==1&&!pins);free(allocation);allocation=NULL; }
static void catalog_get_directory(char *p,size_t n) { strlcpy(p,"Playlists/",n); }
enum music_view { MUSIC_ARTISTS,MUSIC_ALBUMS,MUSIC_ARTIST,MUSIC_ALBUM };
static bool pending;
static enum music_view requested;
static char requested_path[MAX_PATH];
static int catalog_handle,catalog_count;
static char catalog_dir[MAX_PATH],*catalog_base;
const char *music_catalog_path(int);
enum {LANG_PLAY_NEXT,LANG_MUSIC_ADD_QUEUE,LANG_ADD_TO_PL,LANG_MUSIC_UNLIKE,LANG_MUSIC_LIKE,LANG_MUSIC_GO_ARTIST,LANG_MUSIC_GO_ALBUM,LANG_MUSIC_MORE,LANG_ONPLAY_MENU_TITLE,LANG_FILE_NOT_FOUND,LANG_FAILED,LANG_PLAYLIST_ADDED,LANG_HOME_LIKE_FAILED,LANG_MUSIC_ALL_SONGS};
static const char *labels[]={"Play next","Add to queue","Add to playlist","Unlike","Like","Go to artist","Go to album","More...","Song","Missing","Failed","Added","Like failed","All songs"};
#define str(x) labels[x]
#define ID2P(x) labels[x]
enum { ONPLAY_MAINMENU=-1,ONPLAY_OK=0,MUSIC_MENU_MORE=-100 };
enum {GO_TO_ROOT=-1,GO_TO_WPS=2,ACTION_TREE_WPS=50,AUDIO_STATUS_PLAY=1,AUDIO_STATUS_PAUSE=2};
enum { PLAYLIST_INSERT_FIRST=-1,PLAYLIST_INSERT_LAST=-2 };
enum {MT_RETURN_ID=5,MENU_HAS_DESC=16,Icon_Audio=1,MENU_ATTACHED_USB=-10};
#define MENU_ITEM_COUNT(n) ((n)<<12)
struct menu_callback_with_desc { void *callback;const char *desc;int icon; };
struct menu_item_ex { unsigned flags;union {const char **strings;};union {const struct menu_callback_with_desc *callback_and_desc;};};
static int selection,liked,position,changed,cancelled,release_count;
static char affected[MAX_PATH],mutating[MAX_PATH];
static int do_menu(const struct menu_item_ex *m,void *a,void *b,bool c) { (void)a;(void)b;(void)c;assert((m->flags>>12)==7);assert(!strcmp(m->strings[0],"Play next"));strcpy(mutating,"/new.mp3");return selection; }
static void action_wait_for_release(void) {release_count++;}
static int home_liked_state(const char *p) {(void)p;return liked;}
static void splash(int t,const char *p) {(void)t;(void)p;}
static int playlist_insert_track(void *p,const char *s,int where,bool queue,bool sync) {(void)p;assert(queue&&sync);strlcpy(affected,s,sizeof(affected));position=where;return 0;}
static void playlist_set_modified(void *p,bool b) {(void)p;assert(b);changed++;}
static void home_autoplay_cancel_genre(void) {cancelled++;}
static bool catalog_add_track_to_a_playlist(const char *p) {strlcpy(affected,p,sizeof(affected));return true;}
static int home_toggle_like(const char *p) {strlcpy(affected,p,sizeof(affected));return 1;}
struct library_track {int title,artist,album,path,disc,number;};
static struct library_track *tracks;
static int *visible,track_count,visible_count;
static char *strings;
static bool album_sort;
enum library_page {PAGE_ARTISTS,PAGE_ALBUMS,PAGE_ARTIST_ALBUMS,PAGE_TRACKS,PAGE_ARTIST_TRACKS};
static enum library_page page;
static char artist[TAG_MAXLEN],album[TAG_MAXLEN];
'''
        checks = r'''
static int put(const char *s,int *used) { int n=*used;strcpy(strings+n,s);*used+=strlen(s)+1;return n; }
static void add(int i,const char *t,const char *a,const char *l,int d,int n,int *used) {tracks[i]=(struct library_track){put(t,used),put(a,used),put(l,used),put(t,used),d,n};}
int main(void) {
    assert(return_home(0)==GO_TO_ROOT);assert(return_home(AUDIO_STATUS_PLAY)==GO_TO_WPS);assert(return_home(AUDIO_STATUS_PLAY|AUDIO_STATUS_PAUSE)==GO_TO_WPS);
    assert(music_catalog_open()==27);assert(pins==1);
    char name[MAX_PATH];
    assert(!strcmp(music_catalog_label(0,name,sizeof(name)),"Alpha"));
    bool nested=false,top=false;
    for(int i=0;i<catalog_count;i++) {
        const char *label=music_catalog_label(i,name,sizeof(name));
        nested |= !strcmp(label,"nested/Duplicate");top |= !strcmp(label,"Duplicate");
    }
    assert(nested&&top);music_catalog_close();assert(!allocation&&!pins);
    fail_alloc=1;assert(music_catalog_open()==-1);assert(!allocation);fail_alloc=0;
    struct library_track data[6];int indexes[6];char storage[2048];int used=0;
    tracks=data;visible=indexes;strings=storage;track_count=6;
    add(0,"Second","A","Same",1,2,&used);add(1,"First","A","Same",1,1,&used);
    add(2,"Different artist","B","Same",1,1,&used);add(3,"A comma","Tyler, The Creator","Other",1,1,&used);
    add(4,"Other album","A","Other",1,1,&used);add(5,"Second disc","A","Same",2,1,&used);
    page=PAGE_ARTISTS;library_rows();assert(visible_count==3);
    page=PAGE_ALBUMS;library_rows();assert(visible_count==4);
    strcpy(artist,"A");page=PAGE_ARTIST_ALBUMS;library_rows();assert(visible_count==2);
    assert(!strcmp(library_name(0,NULL,name,sizeof(name)),"All songs"));
    strcpy(album,"Same");page=PAGE_TRACKS;library_rows();assert(visible_count==3);
    assert(!strcmp(field(tracks[visible[0]].title),"First"));
    assert(!strcmp(field(tracks[visible[1]].title),"Second"));
    assert(tracks[visible[2]].disc==2);
    page=PAGE_ARTIST_TRACKS;library_rows();assert(visible_count==4);
    for(selection=0;selection<6;selection++) {
        strcpy(mutating,"/original.mp3");affected[0]=0;pending=false;
        int result=music_song_menu(mutating);
        if(selection<4)assert(!strcmp(affected,"/original.mp3"));
        else {assert(result==ONPLAY_MAINMENU&&music_library_pending());assert(!strcmp(requested_path,"/original.mp3"));assert(requested==(selection==4?MUSIC_ARTIST:MUSIC_ALBUM));}
        if(selection==0)assert(position==PLAYLIST_INSERT_FIRST);
        if(selection==1)assert(position==PLAYLIST_INSERT_LAST);
    }
    selection=6;assert(music_song_menu("/original.mp3")==MUSIC_MENU_MORE);
    selection=-2;affected[0]=0;assert(music_song_menu("/original.mp3")==ONPLAY_OK);assert(!affected[0]);
    assert(changed==2&&cancelled==2&&release_count==8);
    puts("Catalog >16, nested names, allocation failure, artist/album identity, disc order and frozen song actions passed");
}
'''
        with tempfile.TemporaryDirectory(prefix="rockbox-library-") as directory:
            root = Path(directory)
            catalog = root / "Playlists"
            (catalog / "nested").mkdir(parents=True)
            for name in ["Alpha.m3u8", "Duplicate.m3u8", *[f"Mix {i:02}.m3u8" for i in range(24)]]:
                (catalog / name).write_text("#EXTM3U\n", encoding="utf-8")
            (catalog / "nested/Duplicate.m3u").write_text("", encoding="utf-8")
            (catalog / "ignored.txt").write_text("", encoding="utf-8")
            source = root / "test.c"
            source.write_text(prefix + functions + checks, encoding="utf-8")
            executable = root / "test.exe"
            subprocess.run([home.GCC, "-std=gnu11", "-Wall", "-Wextra", "-Werror", str(source), "-o", str(executable)], check=True)
            subprocess.run([str(executable)], cwd=root, check=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
