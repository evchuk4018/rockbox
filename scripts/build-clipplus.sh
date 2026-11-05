#!/usr/bin/env bash
set -euo pipefail

# Run in a Linux Rockbox toolchain environment; this does not flash a bootloader.
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
jobs="${1:-2}"
[[ "$jobs" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid job count.' >&2; exit 2; }
command -v arm-elf-eabi-gcc >/dev/null
mkdir -p "$root/build-sansaclipplus"
cd "$root/build-sansaclipplus"
"$root/tools/configure" --target=sansaclipplus --type=n --no-ccache
make -j"$jobs"
make zip
grep -qx 'Target: sansaclipplus' rockbox-info.txt
test -s rockbox.sansa
test -s rockbox.zip
