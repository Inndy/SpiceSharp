#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
case "${1:-}" in x64) triple=x86_64-w64-mingw32;; arm64) triple=aarch64-w64-mingw32;; *) echo 'Usage: build.sh <x64|arm64>' >&2; exit 2;; esac
root=$(pwd)
# Native Windows CMake accepts mixed paths; Linux CMake uses POSIX paths.
if command -v cygpath >/dev/null; then root=$(cygpath -m "$root"); fi
toolchain="$root/toolchains/$triple.cmake"
export CFLAGS="${CFLAGS:-} -O2 -mguard=cf"
# Full LTO with per-function sections, so the DLL links (--gc-sections, trimmed exports) drop everything the exports
# don't reach (docs/decisions/0027). Release flags only: CMake's configure checks compile without them.
release='-O2 -DNDEBUG -ffunction-sections -fdata-sections -flto'
# x64 targets x86-64-v3 (AVX2, FMA, BMI2; docs/decisions/0026): every codec may use it unconditionally.
jpeg_simd=(-DWITH_SIMD=ON -DREQUIRE_SIMD=ON)
opus_simd=()
if [[ $1 == x64 ]]; then
  CFLAGS+=" -march=x86-64-v3"
  opus_simd=(-DOPUS_X86_PRESUME_SSE=ON -DOPUS_X86_PRESUME_SSE2=ON -DOPUS_X86_PRESUME_SSE4_1=ON -DOPUS_X86_PRESUME_AVX2=ON)
fi
b="build/win-$1"; out="out/win-$1"
command -v "$triple-clang" >/dev/null
bash fetch.sh
# Nothing is installed, but an explicit prefix keeps the Windows-style defaults (c:/libjpeg-turbo-gcc64) out of
# configure, which a Linux CMake rejects as relative paths.
common=(-G Ninja -DCMAKE_TOOLCHAIN_FILE="$toolchain" -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_FLAGS_RELEASE="$release"
        -DCMAKE_INSTALL_PREFIX="$root/$b/install")
# libjpeg-turbo and opus are built as static libraries; native/CMakeLists.txt links them into turbojpeg.dll and
# opus.dll with the exports listed in turbojpeg.def and opus.def.
cmake -S downloads/libjpeg-turbo-3.1.2 -B "$b/jpeg" "${common[@]}" -DENABLE_SHARED=OFF -DENABLE_STATIC=ON -DWITH_TURBOJPEG=ON -DWITH_TOOLS=OFF -DWITH_TESTS=OFF "${jpeg_simd[@]}"
cmake --build "$b/jpeg" --target turbojpeg-static
cmake -S downloads/opus-1.5.2 -B "$b/opus" "${common[@]}" -DBUILD_SHARED_LIBS=OFF -DOPUS_BUILD_TESTING=OFF -DOPUS_BUILD_PROGRAMS=OFF "${opus_simd[@]}"
cmake --build "$b/opus"
# FFmpeg: only the H.264 decoder (and parser), LGPL-only (no --enable-gpl or --enable-nonfree), static, for
# spicecodec.dll's software video path. Its own configure and make, out of tree; configure runs again only when these
# arguments change. Same flags as the rest: full LTO, per-function sections, CFG, x86-64-v3 on x64 (with NASM SIMD).
ff_src=$(pwd)/downloads/ffmpeg-9.0.2
case $1 in x64) ff_arch=x86_64;; arm64) ff_arch=aarch64;; esac
ff_args=(--enable-cross-compile --target-os=mingw32 --arch="$ff_arch" --cc="$triple-clang" --ar=llvm-ar --ranlib=llvm-ranlib
         --nm=llvm-nm --strip=llvm-strip --enable-static --disable-shared --enable-lto
         --disable-everything --enable-decoder=h264 --enable-parser=h264
         --disable-programs --disable-doc --disable-network --disable-autodetect --disable-avdevice --disable-avformat
         --disable-swresample --disable-swscale --disable-avfilter --disable-debug --enable-w32threads
         --extra-cflags="$CFLAGS -ffunction-sections -fdata-sections")
mkdir -p "$b/ffmpeg"
if [[ ! -f $b/ffmpeg/ffbuild/config.mak || $(cat "$b/ffmpeg/args" 2>/dev/null) != "${ff_args[*]}" ]]; then
  (cd "$b/ffmpeg" && CFLAGS= "$ff_src/configure" "${ff_args[@]}") > "$b/ffmpeg-configure.log"
  printf '%s' "${ff_args[*]}" > "$b/ffmpeg/args"
fi
make -C "$b/ffmpeg" -j"$(nproc)" libavcodec/libavcodec.a libavutil/libavutil.a > "$b/ffmpeg-make.log"
cmake -S . -B "$b/spice" "${common[@]}" -DSC_TURBOJPEG_LIB="$root/$b/jpeg/libturbojpeg.a" -DSC_OPUS_LIB="$root/$b/opus/libopus.a" \
  -DSC_FFMPEG_SRC="$root/downloads/ffmpeg-9.0.2" -DSC_FFMPEG_BUILD="$root/$b/ffmpeg"
cmake --build "$b/spice"
mkdir -p "$out"
for f in spicecodec.dll turbojpeg.dll opus.dll codec-smoke.exe; do cp "$b/spice/$f" "$out/"; done
cp downloads/opus-1.5.2/COPYING "$out/LICENSE.opus"
cp downloads/libjpeg-turbo-3.1.2/LICENSE.md "$out/LICENSE.libjpeg-turbo.md"
cp downloads/lz4-1.10.0/lib/LICENSE "$out/LICENSE.lz4"
cp downloads/ffmpeg-9.0.2/LICENSE.md "$out/LICENSE.ffmpeg.md"
if [[ $(uname -s) == MINGW* || $(uname -s) == MSYS* ]]; then
  if [[ $1 == x64 ]]; then "$out/codec-smoke.exe"; fi
fi
