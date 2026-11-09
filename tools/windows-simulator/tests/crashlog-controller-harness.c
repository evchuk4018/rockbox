#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>
typedef uint32_t sector_t;
#define SD_BLOCK_SIZE 512
static unsigned long regs[128];
/* REGISTERS */
#define SD_CRASHLOG_TEST
#define SANSA_CLIPPLUS
#define INTERNAL_AS3525 0
#define AMS_OF_SIZE 0xf000
#define CGU_MCI_CLOCK_ENABLE 1
static unsigned long CGU_PERI, CGU_SDSLOT, GPIOB_PIN5;
#define GPIOB_PIN(pin) GPIOB_PIN5
static int amsv2_variant;
static bool sd_transfer_active;
static struct { int initialized; sector_t numblocks; unsigned long ocr, rca; } card_info[2];
static unsigned ticks, words, commands, writes;
static unsigned long address;
static bool stuck, command_error, data_error;
static uint32_t expected[128];
static void crash_test_clear(unsigned long bits) { MCI_RAW_STATUS &= ~bits; }
static void crash_test_fifo(uint32_t value) { assert(words < 128 && value == expected[words]); words++; }
static void crash_test_poll(void)
{
    ticks++;
    if (stuck) return;
    MCI_CTRL &= ~FIFO_RESET;
    if (MCI_COMMAND & CMD_DONE_BIT)
    {
        unsigned int command = MCI_COMMAND & 63;
        commands++;
        MCI_COMMAND &= ~CMD_DONE_BIT;
        MCI_RAW_STATUS |= command_error ? MCI_INT_RTO | MCI_INT_CD : MCI_INT_CD;
        MCI_RESP0 = (SD_TRAN << 9) | SD_R1_READY_FOR_DATA;
        if (command == SD_WRITE_BLOCK)
        {
            address = MCI_ARGUMENT;
            assert(words == 128 && MCI_BYTCNT == 512 && MCI_BLKSIZ == 512);
            assert(!(MCI_CTRL & (INT_ENABLE | DMA_ENABLE)));
            writes++;
            MCI_RAW_STATUS |= data_error ? MCI_INT_DCRC : MCI_INT_DTO;
        }
    }
}
#define CRASH_POLL() crash_test_poll()
#define CRASH_CLEAR(bits) crash_test_clear(bits)
#define CRASH_FIFO(value) crash_test_fifo(value)
/* PRODUCTION */

static void reset(void)
{
    memset(regs, 0, sizeof(regs));
    card_info[0].initialized = 1; card_info[0].numblocks = 1000000;
    card_info[0].ocr = 1u << 30; card_info[0].rca = 0x12340000;
    ticks = words = commands = writes = 0;
    sd_transfer_active = stuck = command_error = data_error = false;
    amsv2_variant = 0;
}
int main(void)
{
    for (unsigned i = 0; i < 128; i++) expected[i] = i * 12345;
    reset(); assert(sd_crashlog_write(100, expected));
    assert(address == 100 + AMS_OF_SIZE && writes == 1 && commands == 4);
    reset(); card_info[0].ocr = 0; amsv2_variant = 1;
    assert(sd_crashlog_write(100, expected));
    assert(address == (100 + AMS_OF_SIZE) * 512 && GPIOB_PIN5 == (1u << 5));
    reset(); sd_transfer_active = true;
    assert(!sd_crashlog_write(100, expected) && commands == 0 && words == 0);
    reset(); assert(!sd_crashlog_write(1000000, expected) && commands == 0);
    reset(); card_info[0].initialized = 0;
    assert(!sd_crashlog_write(100, expected) && commands == 0);
    reset(); MCI_STATUS = DATA_BUSY;
    assert(!sd_crashlog_write(100, expected) && commands == 0);
    reset(); stuck = true;
    assert(!sd_crashlog_write(100, expected) && ticks <= CRASH_POLL_LIMIT + 1);
    reset(); command_error = true;
    assert(!sd_crashlog_write(100, expected) && writes == 0);
    reset(); data_error = true;
    assert(!sd_crashlog_write(100, expected) && writes == 1);
    return 0;
}
