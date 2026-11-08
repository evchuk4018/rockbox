/* Exercise production skin wrapping and line drawing with measured glyphs. */
#include <assert.h>
#include <stdbool.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#define MIN(a,b) ((a)<(b)?(a):(b))
#define MAX(a,b) ((a)>(b)?(a):(b))
#define MASK 0xC0
#define COMP 0x80
#define DRMODE_SOLID 1
#define DRMODE_INVERSEVID 2
/* PRODUCTION_UTF8 */
struct align_pos { char *left, *center, *right; };
struct line_desc { bool scroll; };
struct screen {
    int (*getcharheight)(void), (*getwidth)(void), (*getheight)(void);
    int (*getstringsize)(const unsigned char *, int *, int *);
    void (*set_drawmode)(int), (*fillrect)(int,int,int,int);
    bool (*puts_scroll)(int,int,const unsigned char *);
    void (*put_line)(int,int,struct line_desc *,const char *,...);
};
static int width=60, height=32;
static char drawn[4][1024];
static bool scrolling[4];
static int charheight(void) { return 8; }
static int vpwidth(void) { return width; }
static int vpheight(void) { return height; }
static int measure(const unsigned char *text,int *w,int *h) {
    int pixels=0;
    while (*text) {
        pixels += *text>=128 ? 12 : *text=='W' ? 9 : 6;
        text += utf8seek(text,1);
    }
    if(w)*w=pixels;
    if(h)*h=8;
    return pixels;
}
static void mode(int m) { (void)m; }
static void clear(int x,int y,int w,int h) {
    assert(x==0&&y>=0&&y+h<=height&&w==width&&h==8);
}
static bool stop(int x,int row,const unsigned char *text) {
    assert(x==0&&row>=0&&row<4&&!*text);scrolling[row]=false;return false;
}
static void draw(int x,int y,struct line_desc *desc,const char *fmt,...) {
    assert(x==0&&y>=0&&y+8<=height);
    int row=y/8;
    scrolling[row]=desc->scroll;
    va_list args;va_start(args,fmt);
    const char *left=va_arg(args,const char *), *center="", *right="";
    if(strcmp(fmt,"$t")) {
        assert(!strcmp(fmt,"$t$*s$t$*s$t"));
        (void)va_arg(args,int);center=va_arg(args,const char *);
        (void)va_arg(args,int);right=va_arg(args,const char *);
    }
    snprintf(drawn[row],sizeof(drawn[row]),"%s%s%s",left,center,right);
    va_end(args);
}
/* PRODUCTION_TEXT */
enum { SKIN_TOKEN_SUBLINE_WRAP, CALLBACK_ERROR=-1 };
#define get_param(element,index) (&(element)->params[index])
static int parse_rows(int rows) {
    struct {struct {int number;} data;} param={.data.number=rows};
    struct {typeof(param) *params;} parsed={&param}, *element=&parsed;
    struct {union {long l;} value;} parsed_token={0}, *token=&parsed_token;
    switch(SKIN_TOKEN_SUBLINE_WRAP) {
        /* PRODUCTION_WRAP_TAG */
    }
    return token->value.l;
}
static struct screen display={charheight,vpwidth,vpheight,measure,mode,clear,stop,draw};
static void render(const char *text,int rows) {
    char buffer[1024];strcpy(buffer,text);
    struct align_pos align={.left=buffer,.center=buffer,.right=NULL};
    /* The renderer places an empty left field before a centered field. */
    char empty[]="";align.left=empty;
    struct line_desc desc={false};
    assert(write_wrapped_line(&display,&align,0,rows,&desc)==MIN(rows,height/8));
    assert(!strcmp(buffer,text));
}
int main(void) {
    assert(parse_rows(0)==CALLBACK_ERROR&&parse_rows(-1)==CALLBACK_ERROR);
    assert(parse_rows(1)==1&&parse_rows(4)==4&&parse_rows(100000)==100000);
    render("Alpha beta gamma delta",4);
    assert(!strcmp(drawn[0],"Alpha beta")&&!strcmp(drawn[1],"gamma"));
    assert(!strcmp(drawn[2],"delta")&&!strcmp(drawn[3],""));
    for(int i=0;i<4;i++)assert(!scrolling[i]);
    render("Alpha beta gamma delta epsilon",2);
    assert(!strcmp(drawn[0],"Alpha beta")&&!strcmp(drawn[1],"gamma delta epsilon"));
    assert(!scrolling[0]&&scrolling[1]);
    render("Short",2);assert(!strcmp(drawn[0],"Short")&&!*drawn[1]);
    assert(!scrolling[0]&&!scrolling[1]);
    render("abcdefghijklmnopqrstuvw",4);
    assert(!strcmp(drawn[0],"abcdefghij")&&!strcmp(drawn[1],"klmnopqrst"));
    assert(!strcmp(drawn[2],"uvw")&&!scrolling[2]);
    render("歌曲歌曲歌曲",2);
    assert(!strcmp(drawn[0],"歌曲歌曲歌")&&!strcmp(drawn[1],"曲"));
    render("WWWWWWW",2);assert(!strcmp(drawn[0],"WWWWWW")&&!strcmp(drawn[1],"W"));
    render("Alpha beta",1);assert(!scrolling[0]);
    render("Alpha beta gamma",1);assert(scrolling[0]);
    render("",4);for(int i=0;i<4;i++)assert(!*drawn[i]&&!scrolling[i]);
    render("  Alpha\t beta gamma",3);
    assert(!strcmp(drawn[0],"Alpha")&&!strcmp(drawn[1],"beta gamma"));
    height=16;render("Alpha beta gamma delta epsilon",4);assert(scrolling[1]);
    puts("Skin wrapping: measured words, UTF-8, overflow, row limits and stale scrollers passed");
}
