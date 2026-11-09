#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <fcntl.h>
#include <errno.h>

typedef uint32_t sector_t;
#define ROCKBOX_DIR "/.rockbox"
#define RBVERSION "test-build"
#define current_tick 12345L
static unsigned char disk[1024];
static size_t disk_length;
static unsigned position;
static bool fail_open, fail_sync, fail_map, fail_raw, tear_raw;
static int filesystem_calls, raw_calls, raw_target = -1;
static int next_fd = 7;
static void check_fd(int fd) { assert(fd >= 7 && fd <= 9); }
static int file_open(const char *path, int flags, ...)
{
    filesystem_calls++;
    assert(!strcmp(path, "/.rockbox/crashlog.txt"));
    if (fail_open) return -1;
    if (flags & O_TRUNC) disk_length = 0;
    position = 0;
    return next_fd++;
}
static int file_close(int fd) { check_fd(fd); filesystem_calls++; return 0; }
static int file_sync(int fd) { check_fd(fd); filesystem_calls++; return fail_sync ? -1 : 0; }
static long file_size(int fd) { check_fd(fd); filesystem_calls++; return disk_length; }
static long file_read(int fd, void *data, size_t size)
{
    check_fd(fd); filesystem_calls++;
    if (size > disk_length - position) size = disk_length - position;
    memcpy(data, disk + position, size); position += size; return size;
}
static long file_write(int fd, const void *data, size_t size)
{
    check_fd(fd); filesystem_calls++;
    assert(position + size <= sizeof(disk));
    memcpy(disk + position, data, size); position += size;
    if (position > disk_length) disk_length = position;
    return size;
}
static bool file_map(int fd, sector_t sectors[2])
{
    check_fd(fd); filesystem_calls++;
    sectors[0] = 100; sectors[1] = 900; /* fragmented file */
    return !fail_map;
}
static bool raw_write(sector_t sector, const void *data)
{
    assert(sector == 100 || sector == 900);
    raw_target = sector == 100 ? 0 : 1; raw_calls++;
    if (fail_raw) return false;
    memcpy(disk + raw_target * 512, data, tear_raw ? 256 : 512);
    return !tear_raw;
}
#define CRASH_OPEN file_open
#define CRASH_CLOSE file_close
#define CRASH_SYNC file_sync
#define CRASH_SIZE file_size
#define CRASH_READ file_read
#define CRASH_WRITE file_write
#define CRASH_MAP file_map
#define CRASH_RAW_WRITE raw_write

#include "../../../firmware/crashlog.c"

static void boot(void)
{
    crashlog_disarm();
    next_fd = 7;
    recording = false; /* Reset-only BSS latch in the real firmware. */
    fail_open = fail_sync = fail_map = fail_raw = tear_raw = false;
    assert(crashlog_init());
}

int main(void)
{
    assert(!crashlog_capture("Panic", "before mount", 1, 2, 0, 0));
    assert(raw_calls == 0);
    boot();
    assert(disk_length == 1024);
    int calls = filesystem_calls;
    assert(crashlog_capture("Panic", "audio allocation failed", 0x1234, 0x5678, 0, 0));
    assert(filesystem_calls == calls && raw_calls == 1 && raw_target == 0);
    assert(strstr((char *)disk, "Build: test-build\n"));
    assert(strstr((char *)disk, "audio allocation failed"));
    assert(strstr((char *)disk, "PC: 00001234"));
    uint32_t sequence;
    assert(record_valid(disk, &sequence) && sequence == 1);
    assert(!crashlog_capture("Panic", "nested", 3, 4, 0, 0));
    assert(raw_calls == 1);

    unsigned char previous[512]; memcpy(previous, disk, 512);
    boot(); calls = filesystem_calls;
    assert(crashlog_capture("Data abort", "Data abort", 0x55, 0x66, 0xd, 0xdead));
    assert(raw_target == 1 && filesystem_calls == calls);
    assert(!memcmp(previous, disk, 512));
    assert(record_valid(disk + 512, &sequence) && sequence == 2);
    assert(strstr((char *)disk + 512, "FSR: 0000000d"));
    assert(strstr((char *)disk + 512, "FAR: 0000dead"));

    memcpy(previous, disk + 512, 512);
    boot(); tear_raw = true;
    assert(!crashlog_capture("Panic", "interrupted write", 1, 2, 0, 0));
    assert(!memcmp(previous, disk + 512, 512));
    boot();
    assert(crashlog_capture("Panic", "retry after torn record", 1, 2, 0, 0));
    assert(record_valid(disk, &sequence) && sequence == 3);

    boot(); crashlog_disarm(); calls = raw_calls;
    assert(!crashlog_capture("Panic", "USB owns the disk", 1, 2, 0, 0));
    assert(raw_calls == calls);
    for (int failure = 0; failure < 3; failure++)
    {
        crashlog_disarm();
        next_fd = 7;
        fail_open = failure == 0; fail_sync = failure == 1; fail_map = failure == 2;
        if (failure == 1) disk_length = 0;
        assert(!crashlog_init());
        calls = raw_calls;
        assert(!crashlog_capture("Panic", "not armed", 1, 2, 0, 0));
        assert(raw_calls == calls);
        fail_open = fail_sync = fail_map = false;
    }
    boot(); fail_raw = true;
    assert(!crashlog_capture("Panic", "controller unavailable", 1, 2, 0, 0));
    puts("Crash records: panic/abort, restart, fragmentation, torn writes, nested faults, USB and I/O failures passed");
    return 0;
}
