#!/usr/bin/env bash
set -euo pipefail

# Called by build.ps1 with MSYSTEM=UCRT64; keep build tools isolated from other installations.
export PATH='/ucrt64/bin:/usr/bin:/c/Program Files/Git/cmd'
export LC_ALL=C
jobs="${1:-4}"
reconfigure="${2:-0}"
[[ "$jobs" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid parallel job count.' >&2; exit 2; }
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
source_dir="$root/rockbox"
build_dir="$root/build-sansaclip-sim"

for tool in gcc ld make perl sdl2-config zip git diff; do
    command -v "$tool" >/dev/null || { echo "Missing dependency: $tool. See README.md." >&2; exit 2; }
done
[[ "$(gcc -dumpmachine)" == 'x86_64-w64-mingw32' ]] || {
    echo 'Expected the UCRT64 x86_64-w64-mingw32 GCC compiler.' >&2; exit 2;
}
[[ "$(command -v gcc)" == /ucrt64/bin/* ]] || { echo 'GCC must come from /ucrt64/bin.' >&2; exit 2; }
[[ -f "$source_dir/tools/configure" ]] || { echo 'Missing Rockbox source/tools/configure.' >&2; exit 2; }
# Rockbox makefiles contain unquoted prerequisite paths; use the requested space-free root.
[[ "$root" != *[[:space:]]* ]] || { echo 'Rockbox upstream makefiles require a development path without whitespace (use C:\RockboxDev).' >&2; exit 2; }

mkdir -p -- "$build_dir"
cd -- "$build_dir"
if [[ ! -f Makefile || "$reconfigure" == '1' ]]; then
    "$source_dir/tools/configure" --target=sansaclip --type=s --no-ccache
    # configure does not invalidate its dependency database; refresh it for new headers/options.
    rm -f -- make.dep
fi
grep -Eq '^export MODELNAME[[:space:]]*=[[:space:]]*sansaclip[[:space:]]*$' Makefile || {
    echo 'Unexpected build target. Use build.ps1 -Reconfigure for the original Sansa Clip.' >&2; exit 2;
}
grep -Eq '^export APP_TYPE[[:space:]]*=[[:space:]]*sdl-sim[[:space:]]*$' Makefile || {
    echo 'This is not an SDL simulator configuration. Use build.ps1 -Reconfigure.' >&2; exit 2;
}
make -j"$jobs"
# Upstream packaging can ignore failed resource-copy subprocesses.
# Preserve a log and fail the wrapper for those known error diagnostics.
make install 2>&1 | tee install.log
if grep -Eq '^(cp:|ERROR:|install:)' install.log; then
    echo 'Simulator resource installation reported an error; inspect install.log.' >&2
    exit 2
fi
for resource in simdisk/.rockbox/codecs/wav.codec simdisk/.rockbox/langs/english.lng simdisk/.rockbox/themes/rockbox_failsafe.cfg; do
    [[ -s "$resource" ]] || { echo "Missing installed simulator resource: $resource" >&2; exit 2; }
done
[[ -f rockboxui.exe && -d simdisk/.rockbox ]] || {
    echo 'Build/install finished without expected simulator executable or installed resources.' >&2; exit 2;
}
