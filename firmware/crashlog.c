/* Persistent Clip+ crash records. SPDX-License-Identifier: GPL-2.0-or-later */
#include "crashlog.h"
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#ifndef CRASHLOG_TEST
#include <fcntl.h>
#include "file.h"
#include "kernel.h"
#include "rbpaths.h"
#include "rbversion.h"
#include "sd.h"
#include "system.h"
extern bool file_crashlog_sectors(int fd, sector_t sectors[2]);
#define CRASH_OPEN open
#define CRASH_CLOSE close
#define CRASH_SIZE ffilesize
#define CRASH_READ read
#define CRASH_WRITE write
#define CRASH_SYNC fsync
#define CRASH_MAP file_crashlog_sectors
#define CRASH_RAW_WRITE sd_crashlog_write
#endif

#define RECORD_SIZE 512
#define CHECKSUM_OFFSET 480
#define CRASH_PATH ROCKBOX_DIR "/crashlog.txt"

static unsigned char record[RECORD_SIZE] __attribute__((aligned(32)));
static sector_t record_sectors[2];
static int pinned_fd = -1;
static int pinned_guard_fd = -1;
static bool armed;
static bool recording;
static unsigned int next_slot;
static uint32_t next_sequence;

static uint32_t record_checksum(const unsigned char *data)
{
    uint32_t hash = 2166136261u;
    for (unsigned int i = 0; i < CHECKSUM_OFFSET; i++)
        hash = (hash ^ data[i]) * 16777619u;
    return hash;
}

static bool record_valid(const unsigned char *data, uint32_t *sequence)
{
    static const char prefix[] = "ROCKBOX-CRASH 1\nSequence: ";
    if (memcmp(data, prefix, sizeof(prefix) - 1) ||
        memcmp(data + CHECKSUM_OFFSET, "Checksum: ", 10)) return false;
    uint32_t number = 0;
    for (unsigned int i = sizeof(prefix) - 1; i < sizeof(prefix) - 1 + 10; i++)
    {
        if (data[i] < '0' || data[i] > '9' || number > (UINT32_MAX - (data[i] - '0')) / 10)
            return false;
        number = number * 10 + data[i] - '0';
    }
    if (data[sizeof(prefix) - 1 + 10] != '\n') return false;
    uint32_t expected = 0;
    for (unsigned int i = CHECKSUM_OFFSET + 10; i < CHECKSUM_OFFSET + 18; i++)
    {
        unsigned int c = data[i];
        unsigned int value = c >= '0' && c <= '9' ? c - '0' :
                             c >= 'a' && c <= 'f' ? c - 'a' + 10 : 16;
        if (value > 15) return false;
        expected = (expected << 4) | value;
    }
    if (expected != record_checksum(data)) return false;
    *sequence = number;
    return true;
}

void crashlog_disarm(void)
{
    armed = false;
    if (pinned_fd >= 0)
    {
        CRASH_CLOSE(pinned_fd);
        pinned_fd = -1;
    }
    if (pinned_guard_fd >= 0)
    {
        CRASH_CLOSE(pinned_guard_fd);
        pinned_guard_fd = -1;
    }
}

bool crashlog_init(void)
{
    /* Normal thread context only. Never clear records on a clean boot. Pin the
     * file until USB/shutdown so unlink cannot recycle its allocation chain. */
    crashlog_disarm();
    int fd = CRASH_OPEN(CRASH_PATH, O_RDWR | O_CREAT, 0666);
    if (fd < 0) return false;
    long length = CRASH_SIZE(fd);
    bool ok = length == 2 * RECORD_SIZE;
    if (length == 0)
    {
        memset(record, ' ', sizeof(record));
        static const char empty[] = "No crash recorded. Reserved crash-log slot.\n";
        memcpy(record, empty, sizeof(empty) - 1);
        record[RECORD_SIZE - 1] = '\n';
        ok = CRASH_WRITE(fd, record, sizeof(record)) == sizeof(record) &&
             CRASH_WRITE(fd, record, sizeof(record)) == sizeof(record) && CRASH_SYNC(fd) == 0;
    }
    int closed = CRASH_CLOSE(fd);
    if (!ok || closed < 0) return false;
    fd = CRASH_OPEN(CRASH_PATH, O_RDONLY);
    if (fd < 0) return false;
    int guard = CRASH_OPEN(CRASH_PATH, O_RDONLY);
    if (guard < 0)
    {
        CRASH_CLOSE(fd);
        return false;
    }
    uint32_t sequences[2] = {0, 0};
    bool valid[2] = {false, false};
    for (int i = 0; i < 2; i++)
    {
        if (CRASH_READ(fd, record, sizeof(record)) != sizeof(record))
        {
            CRASH_CLOSE(fd);
            CRASH_CLOSE(guard);
            return false;
        }
        valid[i] = record_valid(record, &sequences[i]);
    }
    if (!CRASH_MAP(fd, record_sectors) || record_sectors[0] == record_sectors[1])
    {
        CRASH_CLOSE(fd);
        CRASH_CLOSE(guard);
        return false;
    }
    next_slot = !valid[0] ? 0 : !valid[1] ? 1 :
        (int32_t)(sequences[0] - sequences[1]) > 0 ? 1 : 0;
    uint32_t newest = valid[0] && (!valid[1] || (int32_t)(sequences[0] - sequences[1]) > 0)
        ? sequences[0] : valid[1] ? sequences[1] : 0;
    next_sequence = newest + 1;
    pinned_fd = fd;
    pinned_guard_fd = guard;
    armed = true;
    return true;
}

bool crashlog_capture(const char *kind, const char *message, uint32_t pc,
                     uint32_t sp, uint32_t fsr, uint32_t far)
{
    /* Terminal fault context: no heap, files, locks, scheduler or interrupts.
     * Latch before touching the formatter to suppress nested faults. */
    if (!armed || recording) return false;
    recording = true;
#ifndef CRASHLOG_TEST
    disable_interrupt(IRQ_FIQ_STATUS);
#endif
    memset(record, ' ', sizeof(record));
    int length = snprintf((char *)record, CHECKSUM_OFFSET,
        "ROCKBOX-CRASH 1\nSequence: %010lu\nBuild: %.63s\nKind: %.31s\n"
        "Message: %.127s\nPC: %08lx\nSP: %08lx\nFSR: %08lx\nFAR: %08lx\nTicks: %ld\n",
        (unsigned long)next_sequence, RBVERSION, kind, message,
        (unsigned long)pc, (unsigned long)sp, (unsigned long)fsr,
        (unsigned long)far, (long)current_tick);
    if (length < 0 || length >= CHECKSUM_OFFSET) return false;
    record[length] = ' ';
    record[CHECKSUM_OFFSET - 1] = '\n';
    int tail = snprintf((char *)record + CHECKSUM_OFFSET, RECORD_SIZE - CHECKSUM_OFFSET,
                        "Checksum: %08lx\n", (unsigned long)record_checksum(record));
    if (tail < 0 || tail >= RECORD_SIZE - CHECKSUM_OFFSET) return false;
    record[CHECKSUM_OFFSET + tail] = ' ';
    record[RECORD_SIZE - 1] = '\n';
    return CRASH_RAW_WRITE(record_sectors[next_slot], record);
}
