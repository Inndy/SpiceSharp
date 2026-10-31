# MSVC native codecs, 2026-11-01

Automated build/ABI verification; no live guest connection. All work was isolated
on `build/msvc-codec`. The human authorized the window-opening viewer test suite.

## Native builds

Visual Studio 2026 MSVC 19.51, CMake 4.3.1, Ninja, MSYS2 GNU make and NASM.
`native/build-msvc.sh x64` and `arm64` built the three DLLs and codec-smoke.exe.
ARM64 FFmpeg assembly used Clang's integrated assembler; all C used cl.exe.
Both builds use static CRT, optimization/LTCG and guarded links. The x64 smoke
passed QUIC/LZ round trips, LZ4 malformed/multi-block cases, and H.264 lifecycle/junk.
`native/verify.py` passed for both: correct machine, exactly 24 spicecodec exports,
10 turbojpeg exports, 5 opus exports, system-only imports, CFG function table,
ASLR/high-entropy VA and NX. ARM64 execution remains untested.

| DLL | MSVC x64 bytes | MSVC ARM64 bytes |
|---|---:|---:|
| spicecodec.dll | 2,015,232 | 1,721,856 |
| turbojpeg.dll | 849,408 | 645,120 |
| opus.dll | 289,792 | 276,480 |

The differential harness compared the MSVC x64 codec with the existing MinGW DLL
using five scrubbed fixture sets, synthetic inputs and mutations: no failure or
output difference (30 QUIC, 5 LZ, 2,803 GLZ real inputs; 5,064 synthetic inputs;
52,974 mutated inputs). These checks do not prove live viewer/guest behavior.

## Managed and package checks

Full `dotnet build SpiceViewer.sln`: zero warnings and zero errors. The first
sandboxed restore failed on network access; the authorized restore/build passed.
Native, wire, replay, video and diagnostics tests passed. An initial replay run
before the viewer assembly existed failed its three reflection-dependent tests;
after the full solution build, all 445 replay tests passed.

The H.264 allocation test reported zero-byte samples disturbed by concurrent GC
under default test parallelism. The native, wire, replay, video and diagnostics projects passed with xUnit collections serialized
(`ParallelizeTestCollections=false`, `MaxParallelThreads=1`), without changing tests.
A broadly serialized API run was interrupted and replaced by its standard runner
settings; the normal API suite passed all 372 tests. API, terminal and viewer tests passed with their normal
settings. No allocation threshold was relaxed.

Package syntax and actual-MSVC FFmpeg notice selection were checked, including the
pinned release URL/hash, configure arguments and `mpegutils.o` provenance. The existing MinGW build was rebuilt from this source (only its output directory
was redirected to avoid replacing the MSVC test inputs). Its smoke and PE checks
passed too.

| Test project | Passed |
|---|---:|
| Spice.Native.Tests | 38 |
| Spice.Wire.Tests | 27 |
| Spice.Replay.Tests | 445 |
| Spice.Video.Tests | 67 |
| Spice.Diagnostics.Tests | 36 |
| Pve.Api.Tests | 372 |
| Pve.Term.Tests | 206 |
| Spice.Viewer.Tests | 101 |
| Total | 1,292 |

Final integration/package results are recorded below.

## Provisional speed comparison

The existing benchmark compared the MinGW baseline with the MSVC x64 DLL in one
process, alternating order, 21 repetitions after two warmups. Every workload's
output checksum matched. On this machine, MSVC QUIC was about 10% slower, LZ about
4% slower, and GLZ ranged from about 2% faster to 12% slower depending on the
workload. These are local measurements, not a universal performance claim.
MinGW remains the default build command; MSVC is the human-requested alternative.

## Final integration checks

Rebased onto the current main (8ac1306, public README/CI documentation). The only
conflict was the decision index: retained CI decision 0045 and renumbered MSVC to
0046. A tree comparison confirmed the tested source and tests were unchanged by
rebase. The rebased Release solution build passed with zero warnings/errors.

`tools/package.ps1 -NoLauncher` produced the x64 Native AOT viewer package. Checked
the ZIP checksum, native source bundle, MSVC configure/helper provenance, both
Microsoft license texts, and equality of all three packaged DLLs with the verified
MSVC outputs. The packaged DLLs loaded headlessly and H.264 open/close succeeded.
This does not claim a live viewer/guest check or ARM64 execution.
