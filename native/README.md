# Windows native codecs

Run from an MSYS2 shell with `clang64/bin` and `usr/bin` on PATH:

```sh
bash native/build.sh x64
python native/verify.py x64
```

Dependencies: CMake 3.24+, Ninja, Clang targeting MinGW, LLD, llvm-ar, llvm-nm, curl,
tar with xz, sha256sum, NASM for x64 JPEG and H.264 SIMD, and GNU make with a POSIX shell
for FFmpeg's own configure and make. MSYS2 packages include
`mingw-w64-clang-x86_64-{clang,cmake,ninja,nasm}` and `make` (MSYS2 doesn't install `make`
by default; without it build.sh stops at FFmpeg, so build on Linux as below). This machine's root is
`~/scoop/apps/msys2/current`. Do not use clang-cl or MSVC. The MSYS2 clang64
toolchain only targets x64; ARM64 DLLs are cross-built on Linux (below).

Outputs: `native/out/win-x64/{spicecodec,turbojpeg,opus}.dll`, or `win-arm64`.
`codec-smoke.exe` sits alongside them and runs automatically for x64 under MSYS2.
Generated sources, build trees, DLLs and logs are ignored by native/.gitignore.
`fetch.sh` pins libjpeg-turbo tag 3.1.2, Opus tag v1.5.2, lz4 tag v1.10.0 and FFmpeg
9.0.2 by release tarball SHA-256; checksum verification occurs even for cached downloads.
Sources are extracted into native/downloads, never vendored into version control.
Only `lib/lz4.c` of lz4 is compiled, statically into spicecodec.dll. FFmpeg's tarball
(`ffmpeg-9.0.2.tar.xz` from ffmpeg.org) was checked against its `.asc` signature with the
FFmpeg release signing key (fingerprint `FCF9 86EA 15E6 E293 A564 4F10 B432 2F04 D676 58D8`)
before its hash was pinned. The release zip doesn't ship the tarball: its LICENSE.txt names
the release, URL, SHA-256, configure arguments and patches (none).

Build order: `build.sh` builds libjpeg-turbo (`turbojpeg-static`) and opus as
static libraries, then FFmpeg's `libavcodec.a` and `libavutil.a` with FFmpeg's own
configure and make (out of tree, in `build/win-<arch>/ffmpeg`; configure runs again only
when its arguments change), then `CMakeLists.txt` links spicecodec.dll (with libavcodec
and libavutil), turbojpeg.dll (from `turbojpeg.def`) and opus.dll (from `opus.def`) and
the smoke test.

FFmpeg is configured LGPL-only, with nothing but the H.264 decoder and parser:

```sh
configure --enable-cross-compile --target-os=mingw32 --arch=<x86_64|aarch64> --cc=<triple>-clang \
  --ar=llvm-ar --ranlib=llvm-ranlib --nm=llvm-nm --strip=llvm-strip --enable-static --disable-shared --enable-lto \
  --disable-everything --enable-decoder=h264 --enable-parser=h264 \
  --disable-programs --disable-doc --disable-network --disable-autodetect --disable-avdevice --disable-avformat \
  --disable-swresample --disable-swscale --disable-avfilter --disable-debug --enable-w32threads \
  --extra-cflags="-O2 -mguard=cf [-march=x86-64-v3] -ffunction-sections -fdata-sections"
```

No `--enable-gpl` or `--enable-nonfree`. `--disable-postproc` from older FFmpeg guides is
gone (libpostproc was removed in FFmpeg 8). FFmpeg's x86 assembly is built with NASM
(`HAVE_X86ASM`, AVX2 included) and its AArch64 NEON assembly with clang; it still picks
the SIMD functions at run time. `--enable-w32threads` is needed because
`--disable-autodetect` also turns off the thread library. FFmpeg's own `-O3` follows the
`-O2` above. libavutil's random seed (`BCryptGenRandom`) is referenced from code the H.264
decoder links, so spicecodec.dll imports `bcrypt.dll` (part of Windows since Vista;
verify.py allows it for spicecodec.dll only). The decoder is otherwise unmodified.

## Cross-building on Linux

Both architectures cross-compile on a Linux host with llvm-mingw (checked
2026-10-30 with release 20261020: LLVM 23.1.4, UCRT, unpacked to
`~/opt/llvm-mingw`; no root needed). The host needs CMake, Ninja, NASM, curl,
tar, sha256sum and python3. From the repository root:

```sh
export PATH=~/opt/llvm-mingw/bin:$PATH
bash native/build.sh x64 && python3 native/verify.py x64
bash native/build.sh arm64 && python3 native/verify.py arm64
```

Each build takes about 55 s on 8 cores from a clean tree (about 12 s before FFmpeg was
added; FFmpeg's configure and make are most of it), and verify.py passes for both.
`build.sh arm64` uses `aarch64-w64-mingw32-clang` and libjpeg-turbo's NEON
intrinsics instead of NASM. The executables don't run on Linux: copy
`out/win-<arch>` to Windows to run `codec-smoke.exe`.

The x64 DLLs match the MSYS2 clang64 (LLVM 22) build in exports, imported DLLs,
DLL characteristics and CFG function counts, and are up to 2 % larger. On
Windows, the Linux-built x64 smoke test passed, and so did the difference test
with the Linux-built spicecodec.dll as the DLL under test. To repeat that, run
`bash native/difftest/run.sh` once, then from `native/build/difftest/release`:
`./difftest.exe ref/spicecodec.dll <linux-out>/spicecodec.dll --fixtures
../../../../tests/fixtures`. The ARM64 DLLs built
this way ran in the win-arm64 release zip: the human connected it to the win81
guest from a Windows ARM64 machine on 2026-10-30, and everything they tried worked
(see docs/verification/2026-10-30-arm64.md). The ARM64 smoke and difference tests
have not been run on ARM64 yet.

## ABI and binding decisions

The spicecodec header retains all 17 reference functions and enum values,
and adds an 18th, `sc_lz4_decode`, and `sc_glz_window_trim` (2026-10-31). Only `SC_API`
(`__declspec(dllexport)`) and `SC_CALL` (`__cdecl`) were added to the reference declarations.
No GLib is linked; the SpiceSee shim headers satisfy the upstream includes.
The DLL exports only those 20 functions (with `sc_abi_version`), and the four optional `sc_h264_*` functions
below (24 in all). `sc_glz_window_trim` is optional: a
version-2 DLL without it still works, and the viewer then has no GLZ reuse pool to trim (decision 0041). `sc_abi_version` returns `SC_ABI_VERSION`
(2), raised whenever a function's contract changes; the viewer won't decode LZ with
a DLL of another version (one without the function is version 1). Callers own the QUIC/LZ/LZ4 output
buffers; GLZ output belongs to the window. One GLZ window must be shared by a
session.

`sc_lz_decode(z, out, out_cap)` writes rows of `width * 4` bytes, palette types
included, and fails if `out_cap` is below `width * height * 4`. Palette rows hold
whole bytes, so a PLT1/PLT4 row decodes to up to 7 extra pixels (review N4). With
`out_cap` of at least `round_up(width, 8) * height * 4` (what `LzDecoder` passes)
it decodes in `out` and compacts the rows, allocating nothing, and bytes past
`width * height * 4` are undefined; with less it decodes into scratch storage in
the handle (at most 4 MiB of it is kept after a decode, successful or not) and
writes nothing past `width * height * 4`.

A GLZ window holds at most 128 MiB of image buffers, by capacity (twice
what the advertised 32 MiB dictionary can make a well-behaved server keep); an
image that would exceed it is rejected, and so is every later one until images
are released or the window is cleared (review N1). Retired GLZ images are kept
for reuse (at most 32 images, 32 MiB), so warmed-up decoding does not allocate;
`sc_glz_window_trim` frees them (and only them) when the session goes idle.

`sc_h264_open(threads)`, `sc_h264_decode(h, data, len, &info)`, `sc_h264_convert(h, dst, dst_cap, stride, flags)`
and `sc_h264_close(h)` (`h264_bridge.c`, MIT) decode H.264 in software with libavcodec (decision 0037, amended
2026-10-31). They are optional: the interface version stays 2, and a DLL without them only has no software video path
(the viewer checks the exports). One handle per stream, from one thread at a time. `threads` (1-16) are libavcodec
slice threads; frame threads are not used, because they hold each picture back by threads - 1 frames. Each call takes
one access unit, as spice-server's x264enc sends them, and keeps the newest picture; `info` gives its cropped size, its
chroma format and whether `sc_h264_convert` takes it (8-bit YUV 4:2:0, 4:2:2, 4:4:4, GBR or gray). The conversion
writes opaque BGRA at the picture's own size (the viewer's canvas scales it to the destination, as for MJPEG), rows
bottom-up with `SC_H264_MIRROR`, with the picture's matrix (BT.601, BT.709, BT.2020, ...; unspecified is BT.709 from
720 lines, else BT.601) and range, in Q14 fixed point that clang vectorizes (AVX2 on x64). 4:2:0 and 4:2:2 chroma is
taken from the nearest sample, like libjpeg-turbo's fast upsampling. Damaged data returns `SC_H264_INVALID` and the
decoder goes on; `SC_H264_FATAL` means the handle can't. libavcodec's logging is silenced. Decoding allocates no
managed memory; libavcodec copies each packet into a padded buffer of its own and pools its pictures.

`sc_lz4_decode(src, src_len, dst, dst_cap)` takes SPICE LZ4 image data after
its 2-byte header (top_down, SPICE bitmap format; the caller parses those):
one or more [u32 big-endian size][LZ4 block] records, decoded as one stream into
`dst` (later blocks may reference earlier output, as in spice-common's
`canvas_get_lz4`). The output is tightly packed rows (width * bytes per pixel);
the caller spreads them to its stride. It returns the bytes written, or -1 on a
truncated record, a size beyond `src`, a bad block or output overflow. The
stream state is on the stack: nothing is allocated.

turbojpeg.dll and opus.dll do not export the full upstream API. They export
exactly the names in `turbojpeg.def` and `opus.def`; everything else is linked
statically and removed by `--gc-sections`. To bind another function, add it to
the `.def` file (`verify.py` fails on any difference).

turbojpeg.dll is libjpeg-turbo 3.1.2 with the tj3 API. It exports `tj3Init`,
`tj3Destroy`, `tj3Get`, `tj3Set`, `tj3DecompressHeader`, `tj3Decompress8`,
`tj3GetErrorStr` and `tj3GetErrorCode` for decoding, plus `tj3Compress8` and
`tj3Free`, which the tests use to build JPEG fixtures. Use the pinned
`downloads/libjpeg-turbo-3.1.2/turbojpeg.h` for enums and exact signatures.
opus.dll (Opus 1.5.2) exports `opus_decoder_create`, `opus_decoder_destroy`,
`opus_decode`, `opus_decoder_ctl` and `opus_get_version_string`.
`opus_decoder_ctl` is variadic: use a binding with the concrete argument layout
for each request. All exported C functions use the Windows C calling convention.
`verify.py` saves complete export lists beside the DLLs.

## Hardening, validation, and limits

All C compilation uses `-O2 -mguard=cf -ffunction-sections -fdata-sections
-flto` (full LTO; x64 adds `-march=x86-64-v3`), LLD, `-static-libgcc`, and
`--dynamicbase --high-entropy-va --nxcompat --guard-cf`, plus `--strip-debug
--build-id` (llvm-mingw's CRT objects carry DWARF that would add about 35 KB to
each image; MSYS2's don't, so its output is unchanged). The DLLs link with
`--exclude-all-symbols --gc-sections`; lld's MinGW default is `-opt:noref`, so
without `--gc-sections` nothing is removed. Static archives hold LTO bitcode,
so the toolchain files use `llvm-ar` and `llvm-ranlib`. The LTO flags are in
`CMAKE_C_FLAGS_RELEASE` only, so configure checks compile without them. Sizes
and build times are in ../docs/decisions/0027. Compiler runtime code is static.
JPEG's hand-written SIMD assembly follows upstream's implementation;
CFG does not instrument assembly instructions. The x64 builds have populated
CFG function tables and CF_INSTRUMENTED flags, ASLR, high-entropy VA and NX.
`llvm-objdump -p` (verify.py) confirms only KERNEL32 and api-ms-win-crt-*
imports for all three, plus bcrypt.dll for spicecodec.dll (FFmpeg, above).
No libwinpthread, GLib, libgcc, or compiler runtime DLL is required.

The x64 C smoke test passed QUIC and LZ round trips for a patterned 73x41 BGRA
image, checking dimensions, format, and every RGB byte. RGB32's unused fourth
byte is excluded. It also compresses a three-block LZ4 stream whose later blocks
only decode through references into earlier ones (with its own copy of lz4.c),
frames it as SPICE does, checks `sc_lz4_decode` reproduces it, and checks -1 for
empty, truncated, oversized, zero-length and corrupt records and output overflow. The smoke test alone does not cover
GLZ, hostile inputs or real SPICE traffic; the difference test ("Verifying codec patches") does, against a
reference build. Neither validates application rendering. Upstream compiler warnings about packed structs and
SIMD alignment casts remain.

See VENDORED.md for per-file origins, changes and reference comparisons, and
../licenses/README.md for how licenses apply here and in the release package.
The LGPL decoders stay in the replaceable spicecodec.dll. A release ships every
committed file in this folder as source\native, so the DLLs can be rebuilt from
it (fetch.sh downloads the pinned libjpeg-turbo, opus and lz4 sources), and one
LICENSE.txt with every component's license text. FFmpeg's tarball is not in the zip (the human,
2026-10-31): fetch.sh downloads it, and LICENSE.txt names its URL, SHA-256, the configure arguments and
the patches applied (none). Modified LGPL files carry a
dated MODIFIED FILE notice (LGPL-2.1 section 2b); keep it current when editing
them. The inherited GLZ malformed-back-reference allocation leak documented by
SpiceSee was fixed by the 2026-10-29 safety patch (see VENDORED.md); this port
still does not claim to harden the decoders against every hostile stream.
Original SpiceSee source revisions are absent from the shallow reference
clones; the manifest distinguishes those recorded origins from verified
current-reference comparisons.

## Verifying codec patches

Patches to the vendored decoders are allowed when this differential test passes
([../docs/decisions/0029](../docs/decisions/0029-vendored-codec-patches-allowed-with-differential-verification.md)).
From an MSYS2 shell with `clang64/bin` and `usr/bin` on PATH:

```sh
bash native/difftest/run.sh                  # 16 mutations per input, about 30 s
bash native/difftest/run.sh --runs 64        # more mutations
bash native/difftest/run.sh --asan           # AddressSanitizer + UBSan builds
bash native/difftest/run.sh --ref <commit>   # another reference revision
bash native/difftest/run.sh --coverage       # line coverage of the decoders
```

`run.sh` extracts `native/spicecodec` at the reference revision (default `HEAD`)
with `git archive`, builds it and the working tree into two DLLs with build.sh's
flags, and loads both into `difftest.exe`. Every input goes through both builds;
they must both fail, or both succeed with the same return codes, dimensions,
type, stride and output bytes, including guard bytes around the buffer. The
inputs are:

- the QUIC, LZ and GLZ images in `tests/fixtures/*/display-0.spicerec`, GLZ in
  stream order through one window (`spicerec.c`);
- synthetic images of every type the vendored encoders produce: QUIC GRAY,
  RGB16, RGB24, RGB32, RGBA; LZ PLT1/PLT4 (both bit orders), PLT8, RGB16, RGB24,
  RGB32, RGBA, XXXA, A8; 22 sizes from 1x1 to 4100x3 plus two large ones; eight
  content kinds (noise, gradient, flat, UI, short periods, sparse, dither,
  repeated rows); LZ and QUIC are encoded with the reference tree's encoders;
- synthetic GLZ sequences from `glzenc.c`, a test encoder written from the
  decoder's format: RGB16, RGB24, RGB32, RGBA and mixed windows, literals, runs,
  overlapping, far and very far same-image references, references into earlier
  images, out-of-order IDs and clears; the reference build's output must also
  equal what the encoder predicts, which checks the encoder itself;
- seeded mutations of each of the above: bit flips, byte overwrites,
  truncation, trailing garbage, header fields set to edge values, another
  input's header, deleted and inserted chunks. GLZ sequences are replayed with
  some images replaced by mutations, and every later image is compared too.

The fixes that change behaviour on purpose are listed in `INTENTIONAL[]` in
`difftest.c`; for each, the harness checks the new behaviour itself and accepts
the reference showing either the old or the new one. `faultinject.exe` links the
working tree statically with every `malloc`/`calloc`/`realloc` routed through
`alloc_hook.h`: it fails each allocation of a QUIC, LZ and GLZ session in turn
and checks for crashes and leaks, and it checks that warmed-up decoding does not
allocate. `review-regressions.exe` (same hooks) checks the memory bounds the
re-review found: the GLZ budget by capacity, LZ scratch after a failed decode,
and ten recurring GLZ sizes staying warm. `--asan` builds the DLLs and harnesses with
`-fsanitize=address,undefined`; `ubsan.supp` suppresses only the vendored LZ
*encoder*'s signed hash overflow, which builds test inputs and is not used by the
viewer. Everything goes to `native/build/difftest/` (ignored).

## Benchmarking codec changes

```sh
bash native/bench/run.sh A.dll B.dll [--reps 31] [--warmup 3] [--cpu 2] [--only NAME]
```

`bench.exe` loads two spicecodec.dll builds (defaults: the reference DLL from the
last difftest run and `out/win-x64/spicecodec.dll`) and decodes the same inputs
with both: the fixtures' QUIC, LZ and GLZ images, and synthetic 1280x720
desktop, short-period and scrolling images. Each repetition runs every workload
on A and on B, alternating which goes first. The thread is pinned to one logical
CPU, at high priority and out of power throttling. Each batch is timed with
`QueryThreadCycleTime` and `QueryPerformanceCounter`. The output gives each
build's median batch time and its 95% confidence interval (order statistics),
and the median per-repetition B/A ratio with its interval: an interval that
excludes 1.000 is a difference on that machine. Outputs must checksum the same.

Numbers from a busy development machine are provisional. For a clean
measurement, build `bench.exe` (run the script once) and copy it, the two DLLs
(in separate folders) and `tests/fixtures/` to a quiet machine. Plug it in, set
the power plan to High performance, close other programs and run
`bench.exe A\spicecodec.dll B\spicecodec.dll --fixtures fixtures --reps 101`.
Use `--cpu` to pick a performance core on hybrid CPUs.
