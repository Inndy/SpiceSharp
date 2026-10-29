# 0027. Native DLLs trimmed with gc-sections and full LTO; LZ4 decoder in spicecodec.dll

- Status: Accepted
- Date: 2026-10-30
- Decided by: the human (2026-10-30: shrink the DLLs, a slower build is acceptable; implement an LZ4 image
  decoder). The build details below are by agent.

- **Context:**
  - [principles.md](../design/principles.md) ranks small binaries third, after speed and memory.
  - lld's MinGW driver defaults to `-opt:noref`, so the DLLs were never garbage-collected.
  - turbojpeg.dll and opus.dll exported the whole upstream API, which kept all of it alive, including the JPEG
    encoder, transforms and the Opus encoder.
  - The node runs liblz4 1.10.0, so the guest can send LZ4 images.
- **Decision:**
  - Layout: the three DLLs stay (spicecodec.dll, turbojpeg.dll, opus.dll). Merging them was not chosen, because
    spicecodec.dll must stay the separately replaceable LGPL library.
  - Static libraries: `native/build.sh` builds libjpeg-turbo (`turbojpeg-static`) and opus as static libraries.
    `native/CMakeLists.txt` links them into turbojpeg.dll and opus.dll.
  - Exports:
    - turbojpeg.dll exports only `native/turbojpeg.def`: the tj3 decode API, the error functions, plus
      `tj3Compress8`/`tj3Free`, which the tests use.
    - opus.dll exports only `native/opus.def`: decoder create/destroy/decode/ctl and the version string.
    - spicecodec.dll still exports only its `SC_API` functions.
    - `native/verify.py` fails unless every DLL exports exactly its list. A new binding needs its name added to the
      `.def` file.
  - Dead-code removal: all three DLLs link with `-Wl,--exclude-all-symbols -Wl,--gc-sections`.
  - Compiler flags:
    - Every C file is compiled with `-ffunction-sections -fdata-sections -flto` (full LTO).
    - These flags are in `CMAKE_C_FLAGS_RELEASE` only, so CMake's configure checks run without them.
    - The toolchain files set `CMAKE_AR`/`CMAKE_RANLIB` to `llvm-ar`/`llvm-ranlib`, which index bitcode archives.
      Both MSYS2 clang64 and llvm-mingw ship them.
  - Settings kept unchanged: `-O2`, `-mguard=cf`, x86-64-v3, `REQUIRE_SIMD` and `OPUS_X86_PRESUME_*`
    ([0026](0026-x64-builds-target-x86-64-v3.md)).
  - LZ4 source: `native/fetch.sh` pins the lz4 v1.10.0 release tarball by SHA-256. Only `lib/lz4.c`
    (BSD-2-Clause) is compiled, statically, into spicecodec.dll. There is no separate DLL.
  - LZ4 entry point: `sc_lz4_decode(src, src_len, dst, dst_cap)` decodes the SPICE LZ4 payload, the part after the
    2-byte top_down/format header:
    - The payload is a series of [u32 big-endian size][LZ4 block] records.
    - All records are decoded as one stream into one contiguous buffer with `LZ4_decompress_safe_continue`, as
      spice-common's `canvas_get_lz4` does, so later blocks may reference earlier output.
    - The output is tightly packed rows.
    - The stream state is on the stack, so the call allocates nothing.
    - It returns the bytes written, or -1 for a truncated record, a size beyond the input, a bad block or an output
      overflow.
    - `native/test/smoke.c` checks a three-block stream that only decodes across blocks, plus truncated, oversized,
      zero-length and corrupt input.
  - This departs from the technology table in [spec.md](../design/spec.md), which lists LZ4 as managed
    (K4os.Compression.LZ4, "no extra native code"). The human chose the native decoder on 2026-10-30 and approved
    updating the spec's table to match (2026-10-30).
  - Out of scope: advertising `SPICE_DISPLAY_CAP_LZ4_COMPRESSION`, which [0004](0004-lz4-is-not-advertised.md)
    declined, belongs to the managed side and is recorded in
    [0028](0028-fast-jpeg-and-preferred-image-compression-are-settings-lz4-is-advertised.md).
- **Reason:**
  - The DLLs shrink by 30% raw and 35% zipped, while keeping CFG, ASLR/NX, the SIMD paths and the import set.
  - Decoded output is bit-identical across the configurations compared below.
  - Full LTO was chosen over ThinLTO because ThinLTO was larger.
  - LZ4 adds 20,480 bytes to spicecodec.dll: an identical build without lz4.c is 124,928 bytes.
- **Cost:**
  - turbojpeg.dll and opus.dll no longer expose the full upstream API. A user can still drop in an upstream build
    of either, because upstream exports a superset.
  - The clean `bash native/build.sh x64` took 36 s before and 44 s after on the development machine (32 threads).
    The LTO link of the three DLLs takes 5.9 s in parallel: turbojpeg 5.3 s, opus 2.6 s and spicecodec 1.5 s when
    each is linked alone.
  - These times were taken on the same busy machine as the speed figures below.

## Measurements

### Sizes (bytes, exact)

Zipped means deflate level 9, each DLL alone; the total row zips the three together.

| DLL | Before | After | Before, zipped | After, zipped |
|---|---:|---:|---:|---:|
| spicecodec.dll | 186,368 | 145,408 (with lz4) | 67,356 | 59,611 |
| turbojpeg.dll | 1,117,696 | 940,544 | 347,411 | 288,161 |
| opus.dll | 591,872 | 235,520 | 332,240 | 135,574 |
| total | 1,895,936 | 1,321,472 (−30.3%) | 746,963 | 483,302 (−35.3%) |

"Before" is the x86-64-v3 build from [0026](0026-x64-builds-target-x86-64-v3.md).

### Prototype variants, before LZ4 (exact)

These builds used the same trimmed exports. Totals are for the three DLLs:

| Variant | Total bytes |
|---|---:|
| trimmed exports only, no `--gc-sections` | 1,556,992 |
| `--gc-sections` | 1,344,000 |
| `--gc-sections` + full LTO | 1,304,064 |
| `--gc-sections` + ThinLTO | 1,349,632 |
| `-O3` + full LTO | 1,401,856 |
| `--gc-sections` + `OPUS_FLOAT_APPROX` | 1,344,512 |

### Speed (provisional)

These figures were measured on the development machine while other builds and agents were running, not in a
quiet environment. Re-measure them in a clean environment before relying on them.

- Method: median of 5 rounds × 21 repetitions per workload, compared with the x86-64-v3 build without LTO.
- Full LTO: LZ about 8% faster, GLZ about 3% faster, Opus about 2% faster. QUIC and JPEG were within ±1%.
- x86-64-v3 compared with generic x86-64 ([0026](0026-x64-builds-target-x86-64-v3.md)): most workloads were within
  ±3%. Opus was about 4% faster on v3, and LZ about 5% slower.
- The decoded-output checksums were identical between the LTO and non-LTO v3 builds for every workload. That
  result is exact.

## Not adopted

- `-O3`: not adopted for now. It costs 7–9% in size, and its speed effect is unproven. Re-measure it in a clean
  environment.
- ThinLTO: larger than full LTO.
- `OPUS_FLOAT_APPROX`: no size gain, and it changes the decoded audio.
- One merged DLL: see the decision above.
