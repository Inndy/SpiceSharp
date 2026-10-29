# 0020. v0 ships as a portable zip

- Status: Accepted
- Date: 2026-10-29
- Decided by: the human

- **Context:** the draft asks for a code-signed installer through GitHub Releases and winget. The human chose a portable zip for v0 and deferred the installer format (MSIX vs classic), code signing and win-arm64 (no ARM64 MSVC tools on the build machine).
- **Decision:** `pwsh tools/package.ps1` publishes Native AOT for `win-x64` and writes `artifacts/SpiceViewer-<version>-win-x64.zip` with a `.sha256`. The zip holds the exe, the three native DLLs, `README.txt`, one `LICENSE.txt` (SpiceViewer's MIT license, a list of every component in the package with version, file, license and upstream, then each full text: .NET runtime and its notices, CsWin32, SpiceSee, spice-common/spice-gtk LGPL-2.1, FastLZ, spice-protocol, libjpeg-turbo, libopus, lz4, MinGW-w64 runtime, LLVM compiler-rt; see [licenses/README.md](../../licenses/README.md)) and `source/native` (every committed file under `native/`: the LGPL library's complete source and build scripts, so users can rebuild and replace `spicecodec.dll`). File association is opt-in per user with `SpiceViewer.exe --register`, or with the opt-in per-user install `--install` (see "Settings: portable by default, opt-in install").
- **Reason:** meets the draft's licensing handling without an installer; unsigned binaries will trigger SmartScreen until signing is set up.
