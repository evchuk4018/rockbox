#!/usr/bin/env bash
set -euo pipefail

# Installed location: rockbox/tools/windows-simulator/tests/test-genlang.sh.
# Keep all generated test outputs inside the dedicated simulator build tree.
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
source_root="$(cd -- "$script_dir/../../.." && pwd -P)"
development_root="$source_root"
output_dir="$development_root/build-sansaclip-sim/checks/genlang/lang"
english="$source_root/apps/lang/english.lang"

if [[ "${MSYSTEM:-}" != 'UCRT64' ]]; then
    echo 'Run this check from an MSYS2 UCRT64 login shell.' >&2
    exit 2
fi
for tool in perl gcc; do
    command -v "$tool" >/dev/null || {
        echo "Missing dependency: $tool" >&2
        exit 2
    }
done
if [[ "$(command -v gcc)" != /ucrt64/bin/* || "$(gcc -dumpmachine)" != 'x86_64-w64-mingw32' ]]; then
    echo 'Expected native Windows GCC from /ucrt64/bin.' >&2
    exit 2
fi
[[ -f "$source_root/tools/genlang" && -f "$english" ]] || {
    echo 'Could not locate Rockbox tools/genlang and apps/lang/english.lang.' >&2
    exit 2
}

mkdir -p -- "$output_dir"
perl -s "$source_root/tools/genlang" \
    "-e=$english" "-p=$output_dir" -t=sansaclip "$english"

# Native GCC translates command-line MSYS paths but cannot translate an
# absolute /c/... path embedded in a generated C #include directive.
gcc -fsyntax-only "$output_dir/lang_core.c"
gcc -E -x c "$output_dir/lang.h" -o /dev/null

printf 'PASS: generated language C and header resolve with native UCRT64 GCC.\n'
printf 'Generated check files: %s\n' "$output_dir"
