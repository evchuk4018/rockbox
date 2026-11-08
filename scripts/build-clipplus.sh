#!/usr/bin/env bash
set -euo pipefail

# Run in a Linux Rockbox toolchain environment; this does not flash a bootloader.
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
jobs="${1:-2}"
[[ "$jobs" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid job count.' >&2; exit 2; }
command -v "${ROCKBOX_COMPILER_PREFIX:-arm-elf-eabi-}gcc" >/dev/null
options=(--target=sansaclipplus --type=n --no-ccache)
if [[ -n "${ROCKBOX_COMPILER_PREFIX:-}" ]]; then
    options+=(--compiler-prefix="$ROCKBOX_COMPILER_PREFIX")
fi
mkdir -p "$root/build-sansaclipplus"
cd "$root/build-sansaclipplus"
"$root/tools/configure" "${options[@]}"
make -j"$jobs"
make -j"$jobs" zip
grep -qx 'Target: sansaclipplus' rockbox-info.txt
test -s rockbox.sansa
test -s rockbox.zip

# Windows filesystem transfers must reject malformed paths before any writes.
python3 - "$PWD/rockbox.zip" <<'PY'
import sys, zipfile
from pathlib import PurePosixPath
with zipfile.ZipFile(sys.argv[1]) as archive:
    names = archive.namelist()
    assert len(names) == len({name.lower() for name in names}), "Duplicate archive paths"
    for name in names:
        assert name.startswith(".rockbox/") and ".." not in PurePosixPath(name).parts, repr(name)
        assert "\\" not in name and not any(ord(c) < 32 for c in name), repr(name)
    assert "Target: sansaclipplus\n" in archive.read(".rockbox/rockbox-info.txt").decode()
PY
python3 - "$PWD/rockbox.zip" <<'PY'
import struct, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as package:
    font = package.read('.rockbox/fonts/12-ProFont.fnt')
    assert font[:4] == b'RB12' and struct.unpack_from('<H', font, 6)[0] == 12
    assert package.read('.rockbox/docs/profontdoc.txt')
PY
