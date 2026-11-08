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
    def test_large_keyboard_fits_clip_display(self):
        keyboard = (home.ROOT / "apps/recorder/keyboard.c").read_text(encoding="utf-8")
        # Skip the forward declaration when extracting the production body.
        production = home.function(keyboard[keyboard.index("int kbd_input_with_font("):],
                                   "static void kbd_calc_vp_params(")
        source = r'''
#include <assert.h>
#include <stddef.h>
#define FONT_SYSFIXED -1
#define DEFAULT_MARGIN 6
typedef unsigned int ucschar_t;
struct viewport {unsigned width,height;};
enum {eKBD_VP_PICKER};
struct screen {int screen_type;};
struct edit_state {int unused;};
struct keyboard_parameters {
    struct viewport *kbd_viewports;ucschar_t *kbd_buf_ptr;
    unsigned font_w,font_h,max_line_len,text_w;
    int max_chars,max_chars_text,lines,default_lines,keyboard_margin,pages,page,x,y,main_y;
};
static unsigned get_icon_width(int screen) {(void)screen;return 6;}
'''
        source += production + r'''
int main(void) {
    struct viewport vp={128,48};struct screen screen={0};struct edit_state state={0};
    ucschar_t keys[]={13,'A','B','C','D','E','F','G','H','I','J','K','L','M',
                     13,'N','O','P','Q','R','S','T','U','V','W','X','Y','Z',
                     11,'0','1','2','3','4','5','6','7','8','9',0x2423,0xFEFF};
    struct keyboard_parameters pm={.kbd_viewports=&vp,.kbd_buf_ptr=keys,
        .font_w=6,.font_h=12,.max_line_len=16,.text_w=6,.default_lines=4};
    kbd_calc_vp_params(&pm,&screen,&state,3);
    assert(pm.pages==1&&pm.lines==3&&pm.max_chars>=13);
    assert(pm.main_y>=36&&pm.main_y+16<=64);
    pm.font_w=6;pm.font_h=8;
    kbd_calc_vp_params(&pm,&screen,&state,FONT_SYSFIXED-1);
    assert(pm.pages==1&&pm.lines==3&&pm.main_y+16<=64);
}
'''
        with tempfile.TemporaryDirectory(prefix="rockbox-search-keyboard-") as directory:
            path = Path(directory) / "keyboard.c"
            path.write_text(source, encoding="utf-8")
            executable = path.with_suffix(".exe")
            subprocess.run([home.GCC, "-std=gnu11", "-Wall", "-Wextra", "-Werror",
                            str(path), "-o", str(executable)], check=True)
            subprocess.run([str(executable)], check=True)

    def test_production_catalog_groups_and_song_actions(self):
        production = (home.ROOT / "apps/music_library.c").read_text(encoding="utf-8")
        signatures = ["void music_library_request(", "bool music_library_pending(",
                      "static int catalog_walk(", "static int catalog_stem_compare(", "static int catalog_compare(",
                      "void music_catalog_close(", "int music_catalog_open(",
                      "const char *music_catalog_path(", "const char *music_catalog_label(",
                      "int music_song_menu(", "static const char *field(",
                      "static int library_compare(", "static bool same(",
                      "static bool library_searchable(", "static int library_header_rows(",
                      "static bool library_matches(", "static bool library_edit_query(",
                      "static void library_rows(", "static const char *library_name(",
                      "int music_library_screen("]
        functions = "\n".join(home.function(production, name) for name in signatures)
        home_source = (home.ROOT / "apps/home_screen.c").read_text(encoding="utf-8")
        home_case = home_source.split("                case ACTION_TREE_WPS:", 1)[1].split("                case ACTION_TREE_STOP:", 1)[0]
        functions += "\nstatic int audio_state; static int audio_status(void) { return audio_state; }\n"
        functions += "static int return_home(int status) { audio_state=status; int result=GO_TO_ROOT; switch(ACTION_TREE_WPS) { case ACTION_TREE_WPS:" + home_case + "} done: return result; }\n"
        functions += "\n".join(home.function(home_source, name) for name in (
            "static int song_rows(", "static const char *item_name(", "static enum themable_icons item_icon("))

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
#define strlcpy library_test_strlcpy
static size_t strlcpy(char *d,const char *s,size_t n) { size_t l=strlen(s); if(n) {size_t c=l<n-1?l:n-1;memcpy(d,s,c);d[c]=0;}return l; }
char *strcasestr(const char *,const char *);
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
enum music_view { MUSIC_ARTISTS,MUSIC_ALBUMS,MUSIC_ARTIST,MUSIC_ALBUM,MUSIC_SEARCH };
static bool pending;
static enum music_view requested;
static char requested_path[MAX_PATH];
static int catalog_handle,catalog_count;
static char catalog_dir[MAX_PATH],*catalog_base;
const char *music_catalog_path(int);
enum {LANG_PLAY_NEXT,LANG_MUSIC_ADD_QUEUE,LANG_ADD_TO_PL,LANG_MUSIC_UNLIKE,LANG_MUSIC_LIKE,LANG_MUSIC_GO_ARTIST,LANG_MUSIC_GO_ALBUM,LANG_MUSIC_MORE,LANG_ONPLAY_MENU_TITLE,LANG_FILE_NOT_FOUND,LANG_FAILED,LANG_PLAYLIST_ADDED,LANG_HOME_LIKE_FAILED,LANG_MUSIC_ALL_SONGS,LANG_SEARCH,LANG_MUSIC_NO_MATCHES,LANG_HOME_DATABASE,LANG_WAIT,LANG_ARTISTS,LANG_ALBUMS};
static const char *labels[]={"Play next","Add to queue","Add to playlist","Unlike","Like","Go to artist","Go to album","More...","Song","Missing","Failed","Added","Like failed","All songs","Search","No matches","Database required","Wait","Artists","Albums","No liked songs","Settings"};
#define LANG_HOME_NO_LIKED 20
#define LANG_SETTINGS 21
enum themable_icons {Icon_Audio=1,Icon_Submenu_Entered,Icon_Playlist};
static struct {int count;struct {char label[MAX_PATH*2];} songs[3];} picks;
static int playlist_count;
#define str(x) labels[x]
#define ID2P(x) labels[x]
enum { ONPLAY_MAINMENU=-1,ONPLAY_OK=0,ONPLAY_START_PLAY=1,ONPLAY_PLUGIN=2,ONPLAY_REVEAL_FILE=3,ONPLAY_NO_CUSTOMACTION=0,MUSIC_MENU_MORE=-100 };
enum {GO_TO_ROOT=-1,GO_TO_WPS=2,ACTION_TREE_WPS=50,AUDIO_STATUS_PLAY=1,AUDIO_STATUS_PAUSE=2};
enum { PLAYLIST_INSERT_FIRST=-1,PLAYLIST_INSERT_LAST=-2 };
enum {MT_RETURN_ID=5,MENU_HAS_DESC=16,MENU_ATTACHED_USB=-10};
#define MENU_ITEM_COUNT(n) ((n)<<12)
struct menu_callback_with_desc { void *callback;const char *desc;int icon; };
struct menu_item_ex { unsigned flags;union {const char **strings;};union {const struct menu_callback_with_desc *callback_and_desc;};};
static int selection,liked,position,changed,cancelled,release_count;
static char affected[MAX_PATH],mutating[MAX_PATH];
static int do_menu(const struct menu_item_ex *m,void *a,void *b,bool c) { (void)a;(void)b;(void)c;assert((m->flags>>12)==7);assert(!strcmp(m->strings[0],"Play next"));strcpy(mutating,"/new.mp3");return selection; }
static void action_wait_for_release(void) {release_count++;}
static int home_liked_state(const char *p) {(void)p;return liked;}
static void splash(int t,const char *p) {(void)t;(void)p;}
static int inserted_count;
static char queued[12][MAX_PATH];
static int playlist_insert_track(void *p,const char *s,int where,bool queue,bool sync) {(void)p;assert(queue==sync);strlcpy(affected,s,sizeof(affected));position=where;if(queue)return 0;strlcpy(queued[inserted_count],s,MAX_PATH);return inserted_count++;}
static void playlist_set_modified(void *p,bool b) {(void)p;assert(b);changed++;}
static void home_autoplay_cancel_genre(void) {cancelled++;}
static bool catalog_add_track_to_a_playlist(const char *p) {strlcpy(affected,p,sizeof(affected));return true;}
static int home_toggle_like(const char *p) {strlcpy(affected,p,sizeof(affected));return 1;}
/* LIBRARY_TRACK */
static uint32_t score_time=1000,second_score,disc_score;
static int score_reads;
static uint32_t home_score_time(void) {return score_time;}
static uint32_t home_score_get(const char *p,uint32_t now) {assert(now==score_time);score_reads++;return !strcmp(p,"/Second.mp3")?second_score:!strcmp(p,"/Second disc.mp3")?disc_score:0;}
static struct library_track *tracks;
static int *visible,track_count,visible_count;
static char *strings;
static bool album_sort,song_sort;
enum library_page {PAGE_ARTISTS,PAGE_ALBUMS,PAGE_ARTIST_ALBUMS,PAGE_TRACKS,PAGE_ARTIST_TRACKS,PAGE_SEARCH};
static enum library_page page;
static char artist[TAG_MAXLEN],album[TAG_MAXLEN],query[MAX_PATH];
static int keyboard_index,keyboard_returns[8];
static const char *keyboard_text[8];
typedef uint32_t ucschar_t;
#define FONT_DIR "/.rockbox/fonts"
static int loaded_fonts;
static int font_load(const char *p) {assert(!strcmp(p,FONT_DIR "/12-ProFont.fnt"));loaded_fonts++;return 3;}
static void font_unload(int id) {assert(id==3);loaded_fonts--;}
static int kbd_input_with_font(char *text,int size,ucschar_t *kbd,int font) {
    const char *expected="ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    assert(font==3&&loaded_fonts==1);
    int keys=0,rows=0;
    assert(kbd);
    for(ucschar_t *row=kbd;*row!=0xFEFF;row+=*row+1) {
        rows++;assert(*row<=13);
        for(unsigned i=1;i<=*row;i++,keys++)
            assert(row[i]==(keys<36?(ucschar_t)expected[keys]:0x2423));
    }
    assert(rows==3&&keys==37);
    assert(keyboard_index<8);strlcpy(text,keyboard_text[keyboard_index],size);
    return keyboard_returns[keyboard_index++];
}
enum {ACTION_STD_CANCEL=10,ACTION_STD_MENU,ACTION_STD_OK,ACTION_STD_CONTEXT,SYS_USB_CONNECTED,CONTEXT_LIST,CONTEXT_TREE,FILE_ATTR_AUDIO,GO_TO_PLUGIN,GO_TO_FILEBROWSER};
#define FOR_NB_SCREENS(i) for(int i=0;i<1;i++)
static bool usable=true;
static int audio_status(void);
static int themes,started=-1,stopped,opened,context_count;
static bool tagcache_is_usable(void) {return usable;}
static int library_open(void) {opened++;int h=core_alloc(1);if(h>0)core_pin(h);return h;}
static void viewportmanager_theme_enable(int i,bool b,void *p) {(void)i;(void)b;(void)p;themes++;}
static void viewportmanager_theme_undo(int i,bool b) {(void)i;(void)b;themes--;}
static void talk_shutup(void) {}
static bool warn_on_pl_erase(void) {return true;}
static void audio_stop(void) {stopped++;}
static int playlist_create(void *a,void *b) {(void)a;(void)b;inserted_count=0;return 0;}
static void playlist_sync(void *p) {(void)p;}
static void playlist_start(int i,int a,int b) {(void)a;(void)b;started=i;}
static int onplay(const char *p,int a,int b,bool c,int d) {(void)a;(void)b;(void)c;(void)d;context_count++;strlcpy(affected,p,sizeof(affected));return ONPLAY_OK;}
static int default_event_handler(int a) {return a;}
struct gui_synclist {int count,selected;const char *(*name)(int,void *,char *,size_t);};
static void gui_synclist_init(struct gui_synclist *l,const char *(*name)(int,void *,char *,size_t),void *p,bool b,int n,void *v) {(void)p;(void)b;(void)n;(void)v;l->name=name;}
static void gui_synclist_set_title(struct gui_synclist *l,const char *t,int i) {(void)l;(void)t;(void)i;}
static void gui_synclist_set_nb_items(struct gui_synclist *l,int n) {assert(n>0);l->count=n;}
static void gui_synclist_select_item(struct gui_synclist *l,int n) {assert(n>=0&&n<l->count);l->selected=n;}
static int gui_synclist_get_sel_pos(struct gui_synclist *l) {return l->selected;}
static void gui_synclist_scroll_stop(struct gui_synclist *l) {(void)l;}
static void gui_synclist_draw(struct gui_synclist *l) {char b[1024];for(int i=0;i<l->count;i++)assert(l->name(i,NULL,b,sizeof(b)));}
struct event {int action,row;const char *label;int restore;};
static struct event events[20];static int event_index;
static bool list_do_action(int c,int t,struct gui_synclist *l,int *a) {
    (void)c;(void)t;assert(event_index<20);struct event *e=&events[event_index++];
    assert(e->row>=0&&e->row<l->count);
    if(e->restore>=0)assert(l->selected==e->restore);
    char b[1024];if(e->label)assert(!strcmp(l->name(e->row,NULL,b,sizeof(b)),e->label));
    l->selected=e->row;*a=e->action;return false;
}
'''
        checks = r'''
static int put(const char *s,int *used) { int n=*used;strcpy(strings+n,s);*used+=strlen(s)+1;return n; }
static void add(int i,const char *t,const char *a,const char *l,int d,int n,int *used) {char p[MAX_PATH];snprintf(p,sizeof(p),"/%s.mp3",t);tracks[i]=(struct library_track){put(t,used),put(a,used),put(l,used),put(p,used),d,n,put(a,used),0};}
static void reset_ui(enum music_view view) {
    music_library_request(view,NULL);keyboard_index=event_index=0;started=-1;
    memset(keyboard_returns,0,sizeof(keyboard_returns));memset(events,0,sizeof(events));
}
int main(void) {
    assert(return_home(0)==GO_TO_ROOT);assert(return_home(AUDIO_STATUS_PLAY)==GO_TO_WPS);assert(return_home(AUDIO_STATUS_PLAY|AUDIO_STATUS_PAUSE)==GO_TO_WPS);
    assert(music_catalog_open()==27);assert(pins==1);
    char name[MAX_PATH];
    assert(!strcmp(item_name(0,NULL,name,sizeof(name)),"Search"));
    assert(!strcmp(item_name(1,NULL,name,sizeof(name)),"No liked songs"));
    picks.count=3;strcpy(picks.songs[0].label,"Suggestion");
    assert(!strcmp(item_name(1,NULL,name,sizeof(name)),"Suggestion"));
    assert(!strcmp(item_name(4,NULL,name,sizeof(name)),"Artists"));
    assert(!strcmp(item_name(5,NULL,name,sizeof(name)),"Albums"));
    assert(!strcmp(item_name(6,NULL,name,sizeof(name)),"Settings"));
    playlist_count=catalog_count;
    assert(item_icon(0,NULL)==Icon_Submenu_Entered&&item_icon(1,NULL)==Icon_Audio);
    assert(item_icon(4,NULL)==Icon_Playlist);
    assert(!strcmp(item_name(4,NULL,name,sizeof(name)),"Alpha"));
    assert(!strcmp(music_catalog_label(0,name,sizeof(name)),"Alpha"));
    bool nested=false,top=false;
    for(int i=0;i<catalog_count;i++) {
        const char *label=music_catalog_label(i,name,sizeof(name));
        nested |= !strcmp(label,"nested/Duplicate");top |= !strcmp(label,"Duplicate");
    }
    assert(nested&&top);music_catalog_close();assert(!allocation&&!pins);
    fail_alloc=1;assert(music_catalog_open()==-1);assert(!allocation);fail_alloc=0;
    struct library_track data[12];int indexes[12];char storage[4096];int used=0;
    tracks=data;visible=indexes;strings=storage;track_count=6;
    add(0,"Second","A","Same",1,2,&used);add(1,"First","A","Same",1,1,&used);
    add(2,"Different artist","B","Same",1,1,&used);add(3,"A comma","Tyler, The Creator","Other",1,1,&used);
    add(4,"Other album","A","Other",1,1,&used);add(5,"Second disc","A","Same",2,1,&used);
    page=PAGE_ARTISTS;library_rows();assert(visible_count==3);
    page=PAGE_ALBUMS;library_rows();assert(visible_count==1);
    assert(!strcmp(library_name(1,NULL,name,sizeof(name)),"Same - A"));
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
    /* Category isolation, full drill-down, Unicode and track-artist matching. */
    page=PAGE_ARTISTS;strcpy(query,"tYlEr");library_rows();assert(visible_count==1);
    assert(!strcmp(library_name(0,NULL,name,sizeof(name)),"Search"));
    strcpy(query,"Second");library_rows();assert(visible_count==0);
    assert(!strcmp(library_name(1,NULL,name,sizeof(name)),"No matches"));
    page=PAGE_ALBUMS;strcpy(query,"sAM");library_rows();assert(visible_count==1);
    strcpy(query,"other");library_rows();assert(visible_count==0);
    assert(!strcmp(library_name(0,NULL,name,sizeof(name)),"Search"));
    assert(!strcmp(library_name(1,NULL,name,sizeof(name)),"No matches"));
    page=PAGE_TRACKS;strcpy(artist,"A");strcpy(album,"Same");library_rows();assert(visible_count==3);
    page=PAGE_SEARCH;strcpy(query,"second");library_rows();assert(visible_count==2);
    assert(!strcmp(library_name(1,NULL,name,sizeof(name)),"Second — A"));
    strcpy(query,"creator");library_rows();assert(visible_count==1);
    strcpy(query,"other");library_rows();assert(visible_count==2);
    add(6,"歌曲","Beyoncé","Unicode",1,1,&used);
    add(7,"Feature","Various Artists","Compilation",1,1,&used);
    tracks[7].performer=put("Guest performer",&used);
    add(8,"Taking What's Not Yours","TV Girl","Who Really Cares",1,1,&used);
    add(9,"Build Me Up Buttercup (Mono)","The Foundations","From the Foundations",1,28,&used);
    track_count=10;
    strcpy(query,"歌曲");library_rows();assert(visible_count==1);
    strcpy(query,"Beyoncé");library_rows();assert(visible_count==1);
    strcpy(query,"guest");library_rows();assert(visible_count==1);
    strcpy(query,"taking");library_rows();assert(visible_count==1);
    assert(!strcmp(field(tracks[visible[0]].title),"Taking What's Not Yours"));
    strcpy(query,"butter");library_rows();assert(visible_count==1);
    assert(!strcmp(field(tracks[visible[0]].title),"Build Me Up Buttercup (Mono)"));
    /* Scores are captured once per rebuild, with zero/ties in title order. */
    strcpy(query,"second");second_score=10;disc_score=UINT32_MAX;
    int reads=score_reads;library_rows();assert(score_reads==reads+track_count);
    assert(!strcmp(field(tracks[visible[0]].title),"Second disc"));
    second_score=UINT32_MAX;disc_score=10;score_time++;library_rows();
    assert(!strcmp(field(tracks[visible[0]].title),"Second"));
    second_score=disc_score=10;library_rows();
    assert(!strcmp(field(tracks[visible[0]].title),"Second"));
    second_score=disc_score=0;library_rows();
    assert(!strcmp(field(tracks[visible[0]].title),"Second"));
    /* Production screen: keyboard cancellation, empty input and unavailable resources. */
    reset_ui(MUSIC_SEARCH);keyboard_text[0]="changed";keyboard_returns[0]=-1;
    int before=opened;assert(music_library_screen(NULL)==GO_TO_ROOT&&opened==before);
    reset_ui(MUSIC_SEARCH);keyboard_text[0]="";
    assert(music_library_screen(NULL)==GO_TO_ROOT&&opened==before);
    usable=false;reset_ui(MUSIC_SEARCH);assert(music_library_screen(NULL)==GO_TO_ROOT&&keyboard_index==0);usable=true;
    fail_alloc=1;reset_ui(MUSIC_SEARCH);keyboard_text[0]="second";
    assert(music_library_screen(NULL)==GO_TO_ROOT&&!allocation&&!pins);fail_alloc=0;
    /* A header is not a song; playback and context actions use score order. */
    second_score=50;disc_score=200;
    reset_ui(MUSIC_SEARCH);keyboard_text[0]="second";
    events[0]=(struct event){ACTION_STD_CONTEXT,0,"Search",0};
    events[1]=(struct event){ACTION_STD_CONTEXT,2,"Second — A",0};
    events[2]=(struct event){ACTION_STD_OK,2,"Second — A",2};
    before=context_count;assert(music_library_screen(NULL)==GO_TO_WPS);
    assert(started==1&&inserted_count==2&&context_count==before+1&&!strcmp(affected,"/Second.mp3"));
    assert(!strcmp(queued[0],"/Second disc.mp3")&&!strcmp(queued[1],"/Second.mp3"));
    second_score=disc_score=0;
    reset_ui(MUSIC_SEARCH);keyboard_text[0]="second";keyboard_text[1]="destroyed";keyboard_returns[1]=-1;keyboard_text[2]="";
    events[0]=(struct event){ACTION_STD_OK,0,"Search",0};
    events[1]=(struct event){ACTION_STD_CONTEXT,1,"Second — A",0};
    events[2]=(struct event){ACTION_STD_OK,0,"Search",1};
    assert(music_library_screen(NULL)==GO_TO_ROOT&&keyboard_index==3);
    /* Returning from artist/album drill-down restores the filtered parent's row. */
    reset_ui(MUSIC_ARTISTS);keyboard_text[0]="tyler";keyboard_text[1]="";
    events[0]=(struct event){ACTION_STD_OK,0,"Search",0};
    events[1]=(struct event){ACTION_STD_OK,1,"Tyler, The Creator",0};
    events[2]=(struct event){ACTION_STD_OK,1,"Other",0};
    events[3]=(struct event){ACTION_STD_CANCEL,0,"1.01 A comma",0};
    events[4]=(struct event){ACTION_STD_CANCEL,1,"Other",1};
    events[5]=(struct event){ACTION_STD_OK,0,"Search",1};
    events[6]=(struct event){ACTION_STD_CANCEL,1,"A",0};
    assert(music_library_screen(NULL)==GO_TO_ROOT);
    reset_ui(MUSIC_ALBUMS);keyboard_text[0]="same";
    events[0]=(struct event){ACTION_STD_OK,0,"Search",0};
    events[1]=(struct event){ACTION_STD_OK,1,"Same - A",0};
    events[2]=(struct event){ACTION_STD_CANCEL,0,"1.01 First",0};
    events[3]=(struct event){ACTION_STD_CANCEL,1,"Same - A",1};
    assert(music_library_screen(NULL)==GO_TO_ROOT);
    /* Hidden single-song albums remain reachable through Go to album. */
    reset_ui(MUSIC_ALBUM);music_library_request(MUSIC_ALBUM,"/Different artist.mp3");
    events[0]=(struct event){ACTION_STD_CANCEL,0,"1.01 Different artist",0};
    events[1]=(struct event){ACTION_STD_CANCEL,0,"Search",0};
    assert(music_library_screen(NULL)==GO_TO_ROOT);
    /* Two complete album groups with the same title stay separate. */
    add(10,"B side","B","Same",1,2,&used);track_count=11;
    page=PAGE_ALBUMS;strcpy(query,"same");library_rows();assert(visible_count==2);
    assert(!strcmp(library_name(1,NULL,name,sizeof(name)),"Same - A"));
    assert(!strcmp(library_name(2,NULL,name,sizeof(name)),"Same - B"));
    reset_ui(MUSIC_SEARCH);keyboard_text[0]="absent";keyboard_text[1]="second";
    events[0]=(struct event){ACTION_STD_OK,1,"No matches",0};
    events[1]=(struct event){ACTION_STD_OK,0,"Search",1};
    events[2]=(struct event){SYS_USB_CONNECTED,1,"Second — A",0};
    assert(music_library_screen(NULL)==GO_TO_ROOT&&!allocation&&!pins&&!themes&&!loaded_fonts);
    assert(stopped==1); /* Browsing and editing leave audio alone. */
    puts("Catalog, multi-track albums, score-ranked search/queue, disc order and frozen song actions passed");
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
            prefix = prefix.replace("/* LIBRARY_TRACK */", home.function(production, "struct library_track {") + ";")
            source.write_text(prefix + functions + checks, encoding="utf-8")
            executable = root / "test.exe"
            subprocess.run([home.GCC, "-std=gnu11", "-Wall", "-Wextra", "-Werror", str(source),
                            str(home.ROOT / "firmware/common/strcasestr.c"), "-o", str(executable)], check=True)
            subprocess.run([str(executable)], cwd=root, check=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
