# 0026. x64 builds target x86-64-v3

- Status: Accepted
- Date: 2026-10-30
- Decided by: the human

- **Context:** the native codecs were built for generic x86-64 and chose their SIMD paths at run time, and Native AOT
  compiled the exe for its default baseline. The human decided the first release opts in aggressively to new ISA and APIs, with
  x86-64-v3 as the floor ([../design/principles.md](../design/principles.md)).
- **Decision:**
  - `native/build.sh` adds `-march=x86-64-v3` to every x64 C build (spicecodec, libjpeg-turbo, opus).
  - libjpeg-turbo is configured with `WITH_SIMD=ON REQUIRE_SIMD=ON`, so a build without NASM fails instead of quietly
    dropping its SIMD code.
  - opus is configured with `OPUS_X86_PRESUME_SSE/SSE2/SSE4_1/AVX2=ON`: its SSE4.1 and AVX2 paths are used directly,
    without run-time CPU detection.
  - `SpiceViewer.csproj` sets `IlcInstructionSet` to `x86-64-v3` for `win-x64`, so `Vector256` and the other
    intrinsics paths compile to AVX2 without run-time checks.
  - ARM64 is unchanged until it is built (deferred in [0020](0020-v0-ships-as-a-portable-zip.md)).
- **Reason and cost:**
  - Faster code without run-time dispatch.
  - CPUs without AVX2 can't run the viewer. That includes Intel before Haswell (2013), AMD before Excavator (2015),
    and some later low-end Pentium, Celeron and Atom parts.
  - Native AOT exits at startup on such a CPU, and the DLLs would fault on their first AVX2 instruction. The user
    README states the requirement.
  - The first build grew turbojpeg.dll from 990,208 to 1,117,696 bytes and opus.dll from 524,800 to 591,872 bytes;
    spicecodec.dll is unchanged in size.
  - Follow-up in [0027](0027-native-dlls-trimmed-with-gc-sections-and-full-lto-lz4-decoder-in-spicecodec.md):
    - Sizes are exact. Trimmed exports, `--gc-sections` and full LTO took turbojpeg.dll to 940,544 bytes and opus.dll
      to 235,520 bytes, keeping the v3 SIMD paths.
    - Speed is provisional: it was measured on a busy development machine. Against generic x86-64, v3 was within ±3%
      on most workloads, Opus was about 4% faster and LZ about 5% slower. It is to be re-measured in a clean
      environment.
