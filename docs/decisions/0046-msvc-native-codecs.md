# 0046. MSVC may build the native codec DLLs

- Status: Accepted
- Date: 2026-11-01
- Decided by: the human ("do it in new worktree, allow codec DLL built from MSVC toolchain";
  "Yes, implement MSVC support"). Build details by agent.

- **Context:** decision 0001 selected MinGW Clang for cross-compilation; the native README
  explicitly excluded MSVC. The human now permits MSVC as another Windows build toolchain.
- **Decision:** keep the MinGW/Linux path and add `native/build-msvc.sh <x64|arm64>` using
  a matching Visual Studio developer environment and MSYS2's POSIX build utilities.
  CMake uses cl/lib/link, `/O2`, `/GL` + `/LTCG`, `/Gy` + `/Gw`, `/OPT:REF` + `/OPT:ICF`,
  CFG, ASLR, high-entropy VA and NX. All inputs use `/MT`, avoiding an external VC runtime.
  x64 uses `/arch:AVX2`, the existing Opus SIMD assumptions and QUIC's AVX2/LZCNT paths.
  Vendored codec algorithms and the flat C ABI stay the same; a small compiler adaptation
  selects MSVC's leading-zero intrinsics and retains CRT initialization under LTCG.
  LGPL code remains in replaceable spicecodec.dll.
  ARM64 FFmpeg assembly uses Clang's integrated assembler, producing MSVC COFF; all C
  uses cl.exe. An explicit mpegutils.o supplies the debug helper MSVC LTCG resolves
  even behind CONFIG_MPEGVIDEODEC=0. FFmpeg sources are unmodified.
  Both paths use the same pinned dependency sources and output layout. The MSVC build tree
  is separate. Outputs carry actual FFmpeg configure arguments through publish to LICENSE.txt,
  which also selects the matching static runtime license (Microsoft or MinGW/compiler-rt).
- **Reason:** Windows builders can use their installed Visual Studio toolchain while Linux
  cross-compilation remains available. Static CRT adds code to the DLLs but avoids a deployment
  dependency; measured sizes and validation are in the verification record. This follows the
  flexibility and performance priorities in [principles.md](../design/principles.md).

This amends the toolchain restriction in 0001 and the compiler/linker flag details of 0027;
it preserves their export, hardening, cross-build and LGPL replacement requirements.

## Measured trade-off

The local comparison in the verification record found MSVC QUIC about 10% slower
and LZ about 4% slower; GLZ varied by workload. Outputs matched. MSVC is opt-in
for the human's requested toolchain flexibility; it does not replace the default
MinGW path or claim equal performance. The default therefore retains the faster
measured build. The MSVC DLL sizes and exact validation limits are recorded there.
