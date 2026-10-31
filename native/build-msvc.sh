#!/usr/bin/env bash
# Run in MSYS2 bash inheriting an x64 or ARM64 Visual Studio developer environment.
set -euo pipefail
cd "$(dirname "$0")"
# Use MSYS2's POSIX utilities, with the developer tools ahead of its unrelated link.exe.
export PATH="$(dirname "$(command -v cl.exe)"):/usr/bin:$PATH"
export VSLANG=1033
# CMake/Ninja must read localized /showIncludes output in the same encoding.
cmd.exe //c 'chcp 65001 >nul'
case "${1:-}" in x64) ff_arch=x86_64; processor=AMD64;; arm64) ff_arch=aarch64; processor=aarch64;; *) echo 'Usage: build-msvc.sh <x64|arm64>' >&2; exit 2;; esac
if [[ ${VSCMD_ARG_TGT_ARCH:-} != "$1" ]]; then
  echo "Use a Visual Studio developer environment targeting $1 (VSCMD_ARG_TGT_ARCH)." >&2
  exit 2
fi
for tool in cl.exe link.exe lib.exe cmake ninja make nasm; do command -v "$tool" >/dev/null; done
root=$(cygpath -m "$PWD")
b="build/msvc-$1"; out="out/win-$1"
bash fetch.sh
# /MT avoids a VC redistributable dependency. All LGPL objects stay in spicecodec.dll.
release='/O2 /DNDEBUG /GL /Gy /Gw /guard:cf /utf-8'
opus_simd=()
if [[ $1 == x64 ]]; then
  release+=' /arch:AVX2'
  opus_simd=(-DOPUS_X86_PRESUME_SSE=ON -DOPUS_X86_PRESUME_SSE2=ON -DOPUS_X86_PRESUME_SSE4_1=ON -DOPUS_X86_PRESUME_AVX2=ON)
fi
common=(-G Ninja -DCMAKE_C_COMPILER=cl.exe -DCMAKE_BUILD_TYPE=Release
        -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR="$processor"
        -DCMAKE_C_FLAGS_RELEASE="$release" -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
        -DCMAKE_INSTALL_PREFIX="$root/$b/install"
        '-DCMAKE_SHARED_LINKER_FLAGS=/LTCG /OPT:REF /OPT:ICF /GUARD:CF /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /INCREMENTAL:NO')
cmake -S downloads/libjpeg-turbo-3.1.2 -B "$b/jpeg" "${common[@]}" -DENABLE_SHARED=OFF -DENABLE_STATIC=ON -DWITH_CRT_DLL=OFF -DWITH_TURBOJPEG=ON -DWITH_TOOLS=OFF -DWITH_TESTS=OFF -DWITH_SIMD=ON -DREQUIRE_SIMD=ON
cmake --build "$b/jpeg" --target turbojpeg-static
cmake -S downloads/opus-1.5.2 -B "$b/opus" "${common[@]}" -DBUILD_SHARED_LIBS=OFF -DOPUS_STATIC_RUNTIME=ON -DOPUS_BUILD_TESTING=OFF -DOPUS_BUILD_PROGRAMS=OFF "${opus_simd[@]}"
cmake --build "$b/opus"
ff_src="$PWD/downloads/ffmpeg-9.0.2"
# cl/link accept '-' as well as '/'; avoid MSYS2 converting standalone /O2 etc. to paths.
ff_release=${release//\//-}
ff_args=(--toolchain=msvc --target-os=win32 --arch="$ff_arch" --enable-static --disable-shared --enable-lto
         --disable-everything --enable-decoder=h264 --enable-parser=h264
         --disable-programs --disable-doc --disable-network --disable-autodetect --disable-avdevice --disable-avformat
         --disable-swresample --disable-swscale --disable-avfilter --disable-debug --enable-w32threads
         --extra-cflags="$ff_release -MT" --extra-ldflags='-LTCG -GUARD:CF -DYNAMICBASE -HIGHENTROPYVA -NXCOMPAT')
if [[ $1 == arm64 ]]; then
  # FFmpeg ships GNU-style ARM assembly. Clang assembles it to MSVC COFF; C still uses cl.exe.
  command -v clang >/dev/null
  ff_args+=(--enable-cross-compile '--as=clang --driver-mode=cl --target=arm64-pc-windows-msvc')
fi
mkdir -p "$b/ffmpeg"
if [[ ! -f $b/ffmpeg/ffbuild/config.mak || $(cat "$b/ffmpeg/args" 2>/dev/null) != "${ff_args[*]}" ]]; then
  (cd "$b/ffmpeg" && CFLAGS= "$ff_src/configure" "${ff_args[@]}") > "$b/ffmpeg-configure.log"
  printf '%s' "${ff_args[*]}" > "$b/ffmpeg/args"
fi
# MSVC LTCG resolves h264dec.c's debug helper even behind CONFIG_MPEGVIDEODEC=0.
make -C "$b/ffmpeg" -j"$(nproc)" libavcodec/avcodec.lib libavutil/avutil.lib libavcodec/mpegutils.o > "$b/ffmpeg-make.log" 2>&1
cmake -S . -B "$b/spice" "${common[@]}" -DSC_TURBOJPEG_LIB="$root/$b/jpeg/turbojpeg-static.lib" -DSC_OPUS_LIB="$root/$b/opus/opus.lib" \
  -DSC_FFMPEG_SRC="$root/downloads/ffmpeg-9.0.2" -DSC_FFMPEG_BUILD="$root/$b/ffmpeg"
cmake --build "$b/spice"
mkdir -p "$out"
for f in spicecodec.dll turbojpeg.dll opus.dll codec-smoke.exe; do cp "$b/spice/$f" "$out/"; done
cp downloads/opus-1.5.2/COPYING "$out/LICENSE.opus"
cp downloads/libjpeg-turbo-3.1.2/LICENSE.md "$out/LICENSE.libjpeg-turbo.md"
cp downloads/lz4-1.10.0/lib/LICENSE "$out/LICENSE.lz4"
cp downloads/ffmpeg-9.0.2/LICENSE.md "$out/LICENSE.ffmpeg.md"
printf 'Toolchain: MSVC (cl.exe), static CRT\nFFmpeg configure:' > "$out/FFmpeg-build.txt"
printf ' %s' "${ff_args[@]}" >> "$out/FFmpeg-build.txt"
printf '\nExtra object: libavcodec/mpegutils.o (MSVC LTCG debug-helper resolution).\nPatches: none.\n' >> "$out/FFmpeg-build.txt"
if [[ $1 == x64 ]]; then "$out/codec-smoke.exe"; fi
