# MSVC speed flags and VS 2026 solution builds

2026-11-01, isolated `build/msvc-speed` worktree. Automated build, benchmark and
window-free checks only; no guest or desktop use. VS 2026 MSVC 19.51.36256,
v145, .NET SDK 10.0.400, MSYS2 CMake/Ninja/make/NASM/Clang; pinned source archives
verified by the existing fetch script. No vendored source changes.

## Flag measurements

Fresh MSVC baseline from the same source and toolchain: `/O2`, `/GL` + `/LTCG`,
`/arch:AVX2`, static CRT and the existing PE protections. The benchmark loads
both DLLs in one process, pins logical CPU 2, alternates A/B order, checks decoded
checksums, and reports medians and paired-ratio confidence intervals. Local
development-machine results are provisional, not a claim for all CPUs.

Initial whole-codec sweep, 21 repetitions after two warmups:

| Candidate | Result versus original MSVC |
|---|---|
| `/Ob3` | QUIC about 2-4% faster; small LZ/GLZ regressions elsewhere |
| `/Ob3 /favor:AMD64` | No better QUIC gain; LZ desktop about 4% slower |
| `/Ob3 /Oi-` | No balanced gain; small GLZ scrolling/periodic LZ regressions |
| `/Ob3 /GL-` | QUIC/LZ/GLZ regressions, up to about 19% |
| `/Ob3 /Os` | Regressions throughout, up to about 83% |
| `/Ob1` | QUIC and GLZ substantially slower |
| `/Ob3 /Qvec-` | No better balanced result than normal vectorization |

Also tested QUIC `/Ob3` with LZ-only `/favor:INTEL64` and `/Oi-`; neither gave
a clear balanced gain. Selected QUIC-only `/Ob3`, leaving the other codecs and
ARM64 unchanged. This increases inline expansion beyond `/O2`'s default `/Ob2`:
[Microsoft documentation](https://learn.microsoft.com/en-us/cpp/build/reference/ob-inline-function-expansion).

Final VS-built DLL, 101 repetitions after three warmups. Ratio is new/old elapsed
time; below 1 is faster. Every workload checksum matched.

| Workload | Old MSVC ms | Tuned MSVC ms | Paired ratio (95% CI) |
|---|---:|---:|---:|
| QUIC fixture | 14.499 | 14.003 | 0.965 (0.962-0.967) |
| LZ fixture | 0.602 | 0.601 | 0.999 (0.994-1.006) |
| GLZ fixture | 6.742 | 6.828 | 1.009 (0.984-1.031) |
| QUIC desktop | 10.166 | 10.150 | 0.998 (0.994-1.001) |
| LZ desktop | 3.072 | 3.076 | 1.001 (0.998-1.005) |
| LZ periodic | 0.133 | 0.133 | 0.996 (0.991-1.001) |
| GLZ scrolling | 9.576 | 9.504 | 0.994 (0.984-1.000) |
| GLZ periodic | 3.745 | 3.612 | 0.965 (0.958-0.971) |

spicecodec.dll: 2,015,232 -> 2,019,328 bytes (+4,096, 0.2%). An earlier long run
found QUIC fixture/desktop gains of 3.7%/1.2% and a small LZ periodic cost
(0.7%, under 1 microsecond/batch). Final QUIC fixture and GLZ periodic gains
persisted; the desktop gain and LZ cost did not. This variability is why the
record does not claim a universal gain or an improvement on every workload.
See decision 0048. A same-method final comparison with the existing MinGW baseline
still found MSVC QUIC about 6-9% slower and LZ fixture/desktop about 4-6% slower.
GLZ was similar or faster except periodic GLZ (about 9% slower). MinGW remains
the default CI path; these changes do not claim MSVC parity.

## Build and correctness checks

- VS 2026 `MSBuild.exe SpiceViewer.sln /restore /m /p:Configuration=Release
  /p:Platform=x64` and the ARM64 configuration built the complete solution.
- Native project Clean removed the selected architecture's generated outputs
  and cleaned its CMake/FFmpeg products, preserving downloads and the other
  architecture. Subsequent solution builds rebuilt native libraries and smoke.
- An additional x64 build began with native DLLs/metadata removed from both
  native and consumer output directories. All 24 files across six consumers
  matched generated MSVC output byte-for-byte afterward; all 24 ARM64 files
  matched too. This checks build ordering and first-build content collection.
- Native x64 smoke passed QUIC/LZ round trips, multi-block/malformed LZ4 and
  H.264 open/junk/close. The differential harness compared the original MSVC
  baseline with the tuned DLL on fixture/synthetic/mutated inputs: zero failures.
- `native/verify.py x64` and `arm64` passed: correct machine type, 24 spicecodec,
  10 turbojpeg and 5 opus exports, system-only imports, CFG table, ASLR,
  high-entropy VA and NX. ARM64 application hosts were inspected as ARM64 PE.
- Any CPU `dotnet build SpiceViewer.sln -c Release --no-restore` remains the
  managed-only path. `dotnet restore` can evaluate the native project without
  Visual C++ target imports; explicit native builds require VS `MSBuild.exe`.

Sandboxed NuGet restore and LLVM inspection encountered access restrictions;
normal-environment executions passed. The API suite needed the normal user
environment for test-only Credential Manager entries and WindowsApps access.
No real credential values were read or logged.

Final Release solution builds passed for x64 and ARM64 after the build-graph
fixes, using the standard Visual C++ Makefile targets. The Any CPU Release build
also passed with zero warnings/errors. The final VS-built x64 DLL was copied
unchanged to the benchmark input used for the table above.

| Window-free x64 test project | Passed | Skipped |
|---|---:|---:|
| Spice.Native.Tests | 38 | 0 |
| Spice.Wire.Tests | 27 | 0 |
| Spice.Replay.Tests | 445 | 0 |
| Spice.Video.Tests | 65 | 2 |
| Spice.Diagnostics.Tests | 36 | 0 |
| Pve.Api.Tests | 372 | 0 |
| Pve.Term.Tests | 206 | 0 |
| Total | 1,189 | 2 |

The five Spice suites serialized xUnit collections, matching the existing
native validation approach; API and terminal used their normal settings.
The two skipped hardware-decoder creation tests were VP9 and HEVC. Terminal
tests initially failed because fixed-depth paths selected `tests/tools` under
the new platform output layout. Their shared root lookup now walks to the
solution file; assertions remain unchanged, and all 206 passed after the fix.

ARM64 execution, viewer/window tests, live guests and portable packaging are
outside this verification; no runtime claim is made for them.
