#!/usr/bin/env bash
# Re-review allocation regressions on their own (run.sh also runs them). They failed until the fixes landed.
# Usage: bash native/difftest/run-review-regressions.sh [--asan] [--source PATH/TO/spicecodec]
# --source permits reviewing an archived commit while unrelated work changes the working tree.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
native=$(cd "$here/.." && pwd)
src="$native/spicecodec"; asan=0
while (($#)); do
  case "$1" in
    --source) src=$2; shift;;
    --asan) asan=1;;
    *) echo "unknown argument $1" >&2; exit 2;;
  esac
  shift
done
cc=x86_64-w64-mingw32-clang
command -v "$cc" >/dev/null || { echo "$cc not on PATH (MSYS2 clang64/bin)" >&2; exit 2; }
lz4="$native/downloads/lz4-1.10.0/lib"
[[ -f "$lz4/lz4.c" ]] || { echo "run bash native/fetch.sh first" >&2; exit 2; }
flags=(-std=gnu11 -O2 -DNDEBUG -march=x86-64-v3 -mguard=cf -flto -static-libgcc -fuse-ld=lld -Wl,--guard-cf)
mode=release
if ((asan)); then
  mode=asan
  flags=(-std=gnu11 -O1 -g -march=x86-64-v3 -fno-omit-frame-pointer -fsanitize=address,undefined -fuse-ld=lld)
  # No encoder inputs are generated here, so no UBSan suppressions are needed. Any report must fail the run.
  export UBSAN_OPTIONS=halt_on_error=1
fi
out="$native/build/review-regressions/$mode"; mkdir -p "$out/tmp"
tmp="$out/tmp"
if command -v cygpath >/dev/null; then tmp=$(cygpath -m "$tmp"); fi
export TMPDIR="$tmp" TMP="$tmp" TEMP="$tmp"
"$cc" "${flags[@]}" -I"$src/include" -I"$src/shim" -I"$src/vendor" -I"$src/vendor/common" -I"$lz4" \
  -include "$here/alloc_hook.h" -o "$out/review-regressions.exe" "$here/review-regressions.c" \
  "$src/codec_bridge.c" "$src/vendor/common/quic.c" "$src/vendor/common/lz.c" "$src/vendor/common/mem.c" \
  "$src/vendor/decode-glz.c" "$lz4/lz4.c"
"$out/review-regressions.exe" 2>&1 | tee "$out/results.log"
