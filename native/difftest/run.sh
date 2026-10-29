#!/usr/bin/env bash
# Differential verification of the spicecodec codecs (native/README.md, "Verifying codec patches").
#
#   bash native/difftest/run.sh [--ref REV] [--runs N] [--seed S] [--quick] [--asan|--coverage] [--verbose]
#
# 1. Extracts native/spicecodec at REV (default HEAD) with git archive and builds it as the reference DLL.
# 2. Builds the working tree's native/spicecodec as the DLL under test, with the same flags as build.sh.
# 3. Runs difftest.exe on both (fixtures, synthetic images, seeded mutations), and faultinject.exe and
#    review-regressions.exe on the working tree (allocation failures, leaks, warm-path allocations, memory bounds).
# --asan builds both DLLs and both harnesses with -fsanitize=address,undefined instead (no LTO, no CFG).
# --coverage instruments the working tree's DLL and reports which decoder lines the difftest ran.
# Outputs and logs go to native/build/difftest/ (ignored). Exits non-zero on any failure.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
native=$(cd "$here/.." && pwd)
repo=$(git -C "$native" rev-parse --show-toplevel)
ref=HEAD; runs=16; seed=1; asan=0; cov=0; args=()
while (($#)); do
  case "$1" in
    --ref) ref=$2; shift;;
    --runs) runs=$2; shift;;
    --seed) seed=$2; shift;;
    --asan) asan=1;;
    --coverage) cov=1;;
    --quick|--verbose) args+=("$1");;
    *) echo "unknown argument $1" >&2; exit 2;;
  esac
  shift
done

cc=x86_64-w64-mingw32-clang
command -v "$cc" >/dev/null || { echo "$cc not on PATH (MSYS2 clang64/bin)" >&2; exit 2; }
# Compiling several sources into one output needs a temp folder. MSYS2's bash drops TMP/TEMP, and native
# clang can't use an MSYS-style TMPDIR (/c/...), so give it a Windows path of our own.
tmp="$native/build/difftest/tmp"; mkdir -p "$tmp"
if command -v cygpath >/dev/null; then tmp=$(cygpath -m "$tmp"); fi
export TMPDIR="$tmp" TMP="$tmp" TEMP="$tmp"
bash "$native/fetch.sh" >/dev/null
lz4="$native/downloads/lz4-1.10.0/lib"

newflags=()
if ((asan)); then
  flavour=asan
  cflags=(-std=gnu11 -O1 -g -march=x86-64-v3 -fno-omit-frame-pointer -fsanitize=address,undefined)
  dllflags=(-shared -fsanitize=address,undefined -fuse-ld=lld -Wl,--exclude-all-symbols)
  exeflags=(-fsanitize=address,undefined -fuse-ld=lld)
elif ((cov)); then
  flavour=coverage
  cflags=(-std=gnu11 -O1 -g -march=x86-64-v3)
  dllflags=(-shared -fuse-ld=lld -Wl,--exclude-all-symbols)
  exeflags=(-fuse-ld=lld)
  newflags=(-fprofile-instr-generate -fcoverage-mapping)
else
  flavour=release
  # build.sh's flags (docs/decisions/0026, 0027), so the DLL under test is the one that ships.
  cflags=(-std=gnu11 -O2 -DNDEBUG -march=x86-64-v3 -mguard=cf -ffunction-sections -fdata-sections -flto)
  dllflags=(-shared -static-libgcc -fuse-ld=lld -flto -Wl,--dynamicbase,--high-entropy-va,--nxcompat,--guard-cf
            -Wl,--exclude-all-symbols -Wl,--gc-sections)
  exeflags=(-static-libgcc -fuse-ld=lld -flto)
fi
out="$native/build/difftest/$flavour"
rm -rf "$out"; mkdir -p "$out/ref-src" "$out/ref" "$out/new"

git -C "$repo" archive --format=tar "$ref:native" spicecodec | tar -x -C "$out/ref-src"
ref_commit=$(git -C "$repo" rev-parse --short "$ref")

incs() { echo "-I$1/include -I$1/shim -I$1/vendor -I$1/vendor/common -I$lz4"; }
build_dll() { # <spicecodec dir> <output dll> [extra flags]
  local s=$1 o=$2; shift 2
  # shellcheck disable=SC2046
  "$cc" "${cflags[@]}" "$@" $(incs "$s") "${dllflags[@]}" -o "$o" \
    "$s/codec_bridge.c" "$s/vendor/common/quic.c" "$s/vendor/common/lz.c" "$s/vendor/common/mem.c" \
    "$s/vendor/decode-glz.c" "$lz4/lz4.c"
}
echo "building reference ($ref = $ref_commit) and working tree ($flavour)"
build_dll "$out/ref-src/spicecodec" "$out/ref/spicecodec.dll"
build_dll "$native/spicecodec" "$out/new/spicecodec.dll" "${newflags[@]}"

# The harness encodes its synthetic inputs with the reference tree's QUIC and LZ encoders.
r="$out/ref-src/spicecodec"
# shellcheck disable=SC2046
"$cc" "${cflags[@]}" $(incs "$r") "${exeflags[@]}" -o "$out/difftest.exe" \
  "$here/difftest.c" "$here/spicerec.c" "$here/glzenc.c" "$here/encoders.c" "$r/vendor/common/quic.c" "$r/vendor/common/lz.c"
# Fault injection links the working tree statically, with malloc/calloc/realloc/free counted and failable.
w="$native/spicecodec"
# shellcheck disable=SC2046
"$cc" "${cflags[@]}" $(incs "$w") -include "$here/alloc_hook.h" "${exeflags[@]}" -o "$out/faultinject.exe" \
  "$here/faultinject.c" "$here/glzenc.c" "$w/codec_bridge.c" "$w/vendor/common/quic.c" "$w/vendor/common/lz.c" \
  "$w/vendor/common/mem.c" "$w/vendor/decode-glz.c" "$lz4/lz4.c"
# The re-review's allocation regressions (budget by capacity, scratch on errors, recurring GLZ sizes), same hooks.
# shellcheck disable=SC2046
"$cc" "${cflags[@]}" $(incs "$w") -include "$here/alloc_hook.h" "${exeflags[@]}" -o "$out/review-regressions.exe" \
  "$here/review-regressions.c" "$w/codec_bridge.c" "$w/vendor/common/quic.c" "$w/vendor/common/lz.c" \
  "$w/vendor/common/mem.c" "$w/vendor/decode-glz.c" "$lz4/lz4.c"

fixtures="$repo/tests/fixtures"
if command -v cygpath >/dev/null; then fixtures=$(cygpath -w "$fixtures"); fi
status=0
if ((cov)); then
  prof="$out/new.profraw"
  if command -v cygpath >/dev/null; then prof=$(cygpath -m "$prof"); fi
  export LLVM_PROFILE_FILE="$prof"
fi
if ((asan)); then
  supp="$here/ubsan.supp"
  if command -v cygpath >/dev/null; then supp=$(cygpath -m "$supp"); fi
  export UBSAN_OPTIONS="suppressions='$supp'"   # quoted: ':' separates options
fi
"$out/difftest.exe" "$out/ref/spicecodec.dll" "$out/new/spicecodec.dll" --fixtures "$fixtures" \
  --runs "$runs" --seed "$seed" "${args[@]}" 2>&1 | tee "$out/difftest.log" || status=1
"$out/faultinject.exe" 2>&1 | tee "$out/faultinject.log" || status=1
"$out/review-regressions.exe" 2>&1 | tee "$out/review-regressions.log" || status=1
if ((cov)); then
  llvm-profdata merge -sparse "$out/new.profraw" -o "$out/new.profdata"
  # Decoder sources only; quic.c and lz.c also hold the encoders, which the viewer doesn't use.
  d="$native/spicecodec"
  llvm-cov report "$out/new/spicecodec.dll" -instr-profile="$out/new.profdata"     "$d/vendor/common/quic.c" "$d/vendor/common/quic_tmpl.c" "$d/vendor/common/quic_family_tmpl.c"     "$d/vendor/common/lz.c" "$d/vendor/common/lz_decompress_tmpl.c" "$d/vendor/decode-glz.c"     "$d/vendor/decode-glz-tmpl.c" "$d/codec_bridge.c" 2>&1 | tee "$out/coverage.txt"
  llvm-cov show "$out/new/spicecodec.dll" -instr-profile="$out/new.profdata" -format=html     -output-dir="$out/coverage-html" "$d/vendor" "$d/codec_bridge.c" >/dev/null
  echo "line-by-line: $out/coverage-html/index.html"
fi
if ((asan)); then
  # UBSan reports and continues; any report fails the run.
  if grep -h "runtime error:" "$out/difftest.log" "$out/faultinject.log" "$out/review-regressions.log"; then echo "UBSan reported the lines above"; status=1; fi
fi
exit $status
