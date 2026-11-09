"""Native regression checks for persistent Clip+ crash recording."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
GCC = shutil.which("gcc") or shutil.which("clang")


def function(source, signature):
    start = source.index(signature)
    brace = source.index("{", start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


class CrashLogTests(unittest.TestCase):
    def test_emergency_controller_is_polled_bounded_and_internal_only(self):
        source = (ROOT / "firmware/target/arm/as3525/sd-as3525v2.c").read_text(encoding="utf-8")
        start = source.index("/* Persistent crash-log polling writer")
        end = source.index("/* End persistent crash-log polling writer */", start)
        production = source[start:end]
        registers = source[source.index("#define SD_BASE"):source.index("#define UNALIGNED_NUM_SECTORS")]
        registers = registers.replace("#define SD_REG(x)       (*(volatile unsigned long *) (SD_BASE+x))", "#define SD_REG(x) (regs[(x) / 4])")
        defines = (ROOT / "firmware/export/sd.h").read_text(encoding="utf-8")
        defines = defines[defines.index("/* SD States */"):defines.rindex("#endif")]
        harness = Path(__file__).with_name("crashlog-controller-harness.c").read_text(encoding="utf-8")
        harness = harness.replace("/* REGISTERS */", registers + defines).replace("/* PRODUCTION */", production)
        with tempfile.TemporaryDirectory(prefix="rockbox-crash-pio-") as directory:
            path = Path(directory) / "pio.c"
            path.write_text(harness, encoding="utf-8")
            executable = Path(directory) / "pio.exe"
            subprocess.run([GCC, "-std=gnu11", "-Wall", "-Wextra", "-Werror", str(path), "-o", str(executable)], check=True)
            subprocess.run([str(executable)], check=True)

    def test_fat_mapping_respects_internal_file_allocation(self):
        source = (ROOT / "firmware/common/fat.c").read_text(encoding="utf-8")
        production = function(source, "bool fat_crashlog_sectors(")
        harness = r'''
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
typedef uint32_t sector_t;
struct fat_file { int volume; long firstcluster; };
struct fat_filestr { struct fat_file *file; sector_t lastsector; };
struct bpb { bool mounted; int drive; unsigned long firstdatasector, totalsectors; sector_t startsector; };
static struct bpb bpb = {true, 0, 100, 10000, 63};
#define FAT_BPB(volume) ((volume) == 0 ? &bpb : NULL)
#define LOG_SECTOR_SIZE(bpb) 512
static bool fail_seek;
static void fat_filestr_init(struct fat_filestr *s, struct fat_file *f) { s->file = f; }
static int fat_seek(struct fat_filestr *s, unsigned long n) {
    s->lastsector = n == 1 ? 200 : 400; return fail_seek ? -1 : 0;
}
/* PRODUCTION */
int main(void) {
    struct fat_file file = {0, 2}; sector_t sectors[2];
    assert(fat_crashlog_sectors(&file, sectors));
    assert(sectors[0] == 263 && sectors[1] == 463);
    bpb.drive = 1; assert(!fat_crashlog_sectors(&file, sectors)); bpb.drive = 0;
    bpb.mounted = false; assert(!fat_crashlog_sectors(&file, sectors)); bpb.mounted = true;
    file.firstcluster = -1; assert(!fat_crashlog_sectors(&file, sectors)); file.firstcluster = 2;
    fail_seek = true; assert(!fat_crashlog_sectors(&file, sectors)); fail_seek = false;
    bpb.firstdatasector = 300; assert(!fat_crashlog_sectors(&file, sectors));
    return 0;
}
'''.replace("/* PRODUCTION */", production)
        with tempfile.TemporaryDirectory(prefix="rockbox-crashmap-") as directory:
            path = Path(directory) / "mapping.c"
            path.write_text(harness, encoding="utf-8")
            executable = Path(directory) / "mapping.exe"
            subprocess.run([GCC, "-std=gnu11", "-Wall", "-Wextra", "-Werror", "-include", "stddef.h", str(path), "-o", str(executable)], check=True)
            subprocess.run([str(executable)], check=True)

    def test_persistent_records_and_storage_failures(self):
        self.assertIsNotNone(GCC)
        with tempfile.TemporaryDirectory(prefix="rockbox-crashlog-") as directory:
            executable = Path(directory) / "crashlog-test.exe"
            subprocess.run([GCC, "-std=gnu11", "-Wall", "-Wextra", "-Werror",
                            "-DCRASHLOG_TEST", "-I", str(ROOT / "firmware/export"),
                            str(Path(__file__).with_name("crashlog-harness.c")),
                            "-o", str(executable)], check=True)
            subprocess.run([str(executable)], check=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
