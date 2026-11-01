# Clang-default GitHub CI and optional MSVC integration

2026-11-01. The human requested Clang as default, integration with the GitHub CI
branch, and push to main, preferring rebase/fast-forward. Rebased the optional
MSVC/VS work onto the GitHub CI commit, retaining both decision records as 0047
(CI) and 0048 (MSVC tuning/solution). A tree comparison confirmed the rebase
did not change native, application or test source.

## Combined-branch validation

- Built x64 native codecs with the default `native/build.sh` and the existing
  MSYS2 Clang toolchain. Pinned dependency hashes and QUIC/LZ/LZ4/H.264 smoke
  checks passed. `FFmpeg-build.txt` identifies MinGW Clang.
- `native/verify.py x64` passed exact exports (24/10/5), machine, system imports,
  CFG, ASLR, high-entropy VA and NX.
- Full Any CPU Release solution build: zero warnings/errors. This path uses
  prebuilt Clang DLLs; the optional MSVC native project is not selected.
- All eight test projects passed on the combined branch. The human explicitly
  approved the full viewer suite, including its temporary nonactivating WARP
  windows. No guest or Proxmox connection was made.

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

The five allocation-sensitive Spice suites used serialized xUnit collections;
API, terminal and viewer used normal runner settings. No allocation assertion
or expected output changed. Test-only Credential Manager prefixes and offline
loopback fixtures were used, never real credentials.

## Package and workflow checks

`tools/package.ps1 -Rid win-x64` produced the full Native AOT portable package,
including viewer, launcher, terminal, licenses and corresponding native source.
The three packaged DLLs match the Clang outputs byte-for-byte. The new vcxproj
is present in the native source bundle.

ZIP SHA-256: `4e67b048b850f835ac50bdcec50c9794e3ab563ef7c251b3c909d306f555a98e`.

Parsed the combined workflow YAML/matrix and all six PowerShell blocks, then
executed its exact archive/checksum validation block against the local package
under PowerShell 7: passed. Both CI jobs invoke `native/build.sh` using the
hash-pinned llvm-mingw 20261020 UCRT Windows cross-toolchain. MSVC setup follows
codec compilation and is retained for Native AOT linking. GitLab's existing
Clang build remains unchanged; optional VS/MSVC support is retained.

At integration, hosted execution is still pending the main push. Local syntax,
MSYS2 Clang builds and x64 packaging do not prove the Windows llvm-mingw hosted
matrix, ARM64 packaging or downloadable Actions artifacts. Inspect both jobs
of the first hosted run before claiming those checks passed. ARM64 execution,
live-guest behavior and energy measurements are outside this integration check.
