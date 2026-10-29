#!/usr/bin/env bash
# Builds and runs the codec benchmark (native/README.md, "Benchmarking codec changes").
#
#   bash native/bench/run.sh [A.dll [B.dll]] [bench options: --reps N --warmup N --cpu K --only NAME]
#
# Defaults: A is the reference DLL that native/difftest/run.sh last built (the vendored sources at its --ref),
# B is native/out/win-x64/spicecodec.dll from build.sh. Pass two paths to compare any two builds.
# For a clean-machine run, copy native/build/bench/bench.exe, both DLLs (in separate folders) and
# tests/fixtures/ there, and run bench.exe directly with the same arguments.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
native=$(cd "$here/.." && pwd)
repo=$(git -C "$native" rev-parse --show-toplevel)
a="$native/build/difftest/release/ref/spicecodec.dll"
b="$native/out/win-x64/spicecodec.dll"
if (($#)) && [[ $1 != --* ]]; then a=$1; shift; fi
if (($#)) && [[ $1 != --* ]]; then b=$1; shift; fi
for f in "$a" "$b"; do [[ -f $f ]] || { echo "missing $f (run native/difftest/run.sh and native/build.sh first)" >&2; exit 2; }; done

cc=x86_64-w64-mingw32-clang
command -v "$cc" >/dev/null || { echo "$cc not on PATH (MSYS2 clang64/bin)" >&2; exit 2; }
out="$native/build/bench"
mkdir -p "$out/tmp"
tmp=$out/tmp
if command -v cygpath >/dev/null; then tmp=$(cygpath -m "$tmp"); fi
export TMPDIR="$tmp" TMP="$tmp" TEMP="$tmp"   # MSYS2's bash drops TMP/TEMP; native clang needs one
"$cc" -std=gnu11 -O2 -march=x86-64-v3 -I"$native/spicecodec/include" -fuse-ld=lld -static-libgcc -o "$out/bench.exe" \
  "$here/bench.c" "$here/../difftest/spicerec.c" "$here/../difftest/glzenc.c"

fixtures="$repo/tests/fixtures"
if command -v cygpath >/dev/null; then fixtures=$(cygpath -w "$fixtures"); a=$(cygpath -w "$a"); b=$(cygpath -w "$b"); fi
"$out/bench.exe" "$a" "$b" --fixtures "$fixtures" "$@"
