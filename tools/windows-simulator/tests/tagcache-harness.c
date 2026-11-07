/* Host service adapters for the production database commit and audio allocator. */
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <inttypes.h>
#include <fcntl.h>

#define read test_read
#define close test_close
#define lseek test_lseek
#define sleep test_sleep

#define HAVE_DIRCACHE
#define TAGCACHE_MAGIC 0x54434810
#define TAGCACHE_FILE_NOCOMMIT "ignore"
#define TAGCACHE_FILE_TEMP "temp"
#define TAG_COUNT 3
#define TAGCACHE_IS_NUMERIC(i) ((i) == 2)
#define USR_CANCEL false
#define TALK_BUFFER_LOOSE 1
#define logf(...) ((void)0)
#define panicf(...) do { fprintf(stderr, __VA_ARGS__); abort(); } while (0)
#ifdef SANSA_CLIPPLUS
#define MEMORYSIZE 8
#define TEST_FREE_MEMORY (5 * 1024 * 1024)
#else
#define MEMORYSIZE 2
#define TEST_FREE_MEMORY (1024 * 1024)
#endif

/* PRODUCTION_STRUCTS */
static struct master_header current_tcmh, disk_header;
static struct { bool ready, commit_delayed; int commit_step; } tc_stat;
static int write_lock, read_lock, tempbuf_handle, audiobuf_handle;
static char *tempbuf;
static size_t tempbuf_size, filebuflen;
struct buflib_callbacks { int unused; };
static struct buflib_callbacks buflib_ops_locked, ops;

static size_t allocations[8];
static size_t available = TEST_FREE_MEMORY;
static char memory[TEST_FREE_MEMORY];
static bool allocation_failure;
static int index_calls, header_writes, removed, suspended, resumed;
static int input_count, input_magic = TAGCACHE_MAGIC;
static int input_bytes = sizeof(struct tagcache_header);

static int core_alloc_ex(size_t size, struct buflib_callbacks *callbacks)
{
    (void)callbacks;
    if (allocation_failure || size > available) return -1;
    for (int i = 1; i < 8; i++)
        if (!allocations[i]) {
            allocations[i] = size; available -= size; return i;
        }
    abort();
}
static int core_alloc_maximum(size_t *size, struct buflib_callbacks *callbacks)
{
    *size = available;
    return *size ? core_alloc_ex(*size, callbacks) : -1;
}
static int core_free(int handle)
{
    assert(handle > 0 && allocations[handle]);
    available += allocations[handle]; allocations[handle] = 0; return 0;
}
static void *core_get_data(int handle) { assert(handle > 0); return memory; }
static size_t core_allocatable(void) { return available; }
static size_t pcmbuf_size_reqd(void)
{ return (MEMORYSIZE > 2 ? 600 : 180) * 1024; }
static void talk_buffer_set_policy(int policy) { (void)policy; }
static void audio_reset_buffer_noalloc(void *buffer)
{
    (void)buffer;
    /* PCM descriptors, scratch memory and file buffering need space too. */
    assert(filebuflen >= pcmbuf_size_reqd() + 128 * 1024);
}

/* PRODUCTION_ALLOCATION */

static int open_db_fd(const char *name, int flags)
{ (void)flags; return !strcmp(name, TAGCACHE_FILE_TEMP) ? 1 : -1; }
static int read(int fd, void *buffer, int size)
{
    assert(fd == 1 && size == (int)sizeof(struct tagcache_header));
    struct tagcache_header h = {input_magic, 0, input_count};
    memcpy(buffer, &h, sizeof(h)); return input_bytes;
}
static int close(int fd) { assert(fd == 1 || fd == 2); return 0; }
static void remove_db_file(const char *name)
{ assert(!strcmp(name, TAGCACHE_FILE_TEMP)); removed++; }
static void sleep(int ticks) { (void)ticks; abort(); }
static bool check_all_headers(void) { return !disk_header.dirty; }
static void dircache_free_buffer(void) { suspended++; }
static void dircache_resume(void) { resumed++; }
static void update_master_header(void)
{ disk_header = current_tcmh; header_writes++; }
static int open_master_fd(struct master_header *h, bool write)
{ assert(write); *h = disk_header; return 2; }
static int lseek(int fd, int pos, int whence)
{ assert(fd == 2 && pos == 0 && whence == SEEK_SET); return 0; }
static void write_master_header(int fd, struct master_header *h)
{ assert(fd == 2); disk_header = *h; header_writes++; }
static void tagcache_commit_finalize(void)
{ current_tcmh = disk_header; tc_stat.ready = check_all_headers(); }
static void do_timed_yield(void) {}
static int build_index(int i, struct tagcache_header *h, int fd)
{
    (void)i; (void)h; assert(fd == 1);
    index_calls++;
    /* Reproduce pressing Play while the commit has marked the DB dirty. */
    assert(disk_header.dirty);
    audio_reset_buffer();
    assert(audiobuf_handle > 0);
    audiobuf_handle = core_free(audiobuf_handle);
    return 1;
}
static bool build_numeric_indices(struct tagcache_header *h, int fd)
{ (void)h; assert(fd == 1); return true; }

/* PRODUCTION_COMMIT */

int main(int argc, char **argv)
{
    (void)argv;
    current_tcmh.tch.entry_count = 802;
    current_tcmh.commitid = 9;
    disk_header = current_tcmh;
    tc_stat.ready = true;

    if (argc > 1) input_count = 1; /* independently reproduce allocation bug */
    assert(commit());
    if (argc == 1) {
        /* Empty auto-updates must neither dirty nor rebuild the database. */
        assert(!index_calls && !header_writes && !suspended && removed == 1);
        assert(tc_stat.ready && !disk_header.dirty && disk_header.commitid == 9);
        input_count = 1;
        assert(commit());
    }
    assert(index_calls == 2 && header_writes == 2);
    assert(disk_header.tch.entry_count == 803 && disk_header.commitid == 10);
    assert(!disk_header.dirty && tc_stat.ready && !read_lock);
    assert(suspended == resumed && tempbuf_size == 0 && tempbuf == NULL);
    assert(available == sizeof(memory));

    /* Allocation failure leaves the old database clean and delays the update. */
    allocation_failure = true;
    assert(!commit());
    assert(tc_stat.commit_delayed && !disk_header.dirty);
    assert(header_writes == 2 && suspended == resumed && !read_lock);
    allocation_failure = false;
    input_magic = 0;
    assert(!commit() && header_writes == 2);
    input_magic = TAGCACHE_MAGIC;
    input_bytes--;
    assert(!commit() && header_writes == 2);
    puts("Database/playback overlap, empty scans and failure recovery passed");
    return 0;
}
