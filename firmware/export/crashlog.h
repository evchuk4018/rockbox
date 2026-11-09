/* Persistent Clip+ crash records. SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef CRASHLOG_H
#define CRASHLOG_H
#include <stdbool.h>
#include <stdint.h>
#if !defined(CRASHLOG_TEST)
#include "config.h"
#endif

#if defined(CRASHLOG_TEST) || (defined(SANSA_CLIPPLUS) && !defined(SIMULATOR) && !defined(BOOTLOADER))
bool crashlog_init(void);
void crashlog_disarm(void);
bool crashlog_capture(const char *kind, const char *message, uint32_t pc,
                     uint32_t sp, uint32_t fsr, uint32_t far);
#else
static inline bool crashlog_init(void) { return false; }
static inline void crashlog_disarm(void) { }
static inline bool crashlog_capture(const char *kind, const char *message,
                                   uint32_t pc, uint32_t sp, uint32_t fsr, uint32_t far)
{
    (void)kind; (void)message; (void)pc; (void)sp; (void)fsr; (void)far;
    return false;
}
#endif
#endif
