# GitHub CI and release build artifacts

[Windows release build](../.github/workflows/build.yml) runs on pushes to `main`, tags matching `v*`, pull
requests and manual **Actions > Windows release build > Run workflow** dispatches. The workflow must first be
pushed to GitHub; manual dispatch becomes available after it is present on the default branch. No secrets or
private runner setup are required. The existing [GitLab pipeline](ci.md) is independent.

## Build and validation

Two disposable `windows-2025` x64 runners build `win-x64` and `win-arm64` independently. Each sets up the SDK
from `global.json`, MSYS2 utilities and the pinned llvm-mingw 20261020 UCRT x64-hosted cross-toolchain
([official release](https://github.com/mstorsjo/llvm-mingw/releases/tag/20261020)). Its Windows ZIP is checked
against SHA-256 `fd5462b7f5d93b2ff981fcd3f9c7e427e47f3203c64ea91c27bcc444eebd74cd` before extraction.
The hosted image supplies CMake, Ninja, Python, MSVC and the Windows SDK. MSYS2 supplies NASM and POSIX tools.
Native source downloads are hash-checked by `fetch.sh`. Actions are pinned to commit hashes; hosted image and
utility package versions can change between runs.
The FFmpeg tarball is prefetched from upstream, with Alpine Linux's source cache as a transport fallback after
observed upstream TLS failures on both hosted targets. The filename and SHA-256 come from `native/fetch.sh`;
either source must match that same hash, then `fetch.sh` checks it again before extraction. A checksum mismatch
fails immediately. This changes download availability, not the source version, source bytes or trust pin.

Both jobs build codecs using the default Clang script (`native/build.sh`), verify their architecture, exports, imports and PE
protections, then run `tools/package.ps1` for the matching RID. The x64 job also runs the native smoke test
(inside the native build script), builds the full solution and executes all seven window-free test projects
sequentially before packaging. Any unresolved build, verification, test or publish failure stops that job's package upload.
Before upload, each job checks the zip's SHA-256 sidecar, required executables, DLLs, notices and native build
scripts, and rejects runtime data, connection files and debug symbols in the archive. TRX reports upload even
after a test failure. ARM64 is cross-built, not executed or tested on ARM hardware.

If a completed test run fails only because every allocation-measurement window
was GC-disturbed, CI performs one plain fresh-process project rerun, as allowed
by the repository's flaky-test guidance. The original result is retained as
`*.gc-disturbed.trx`. Real assertions, clean-window allocation regressions,
aborted/incomplete runs and a failing second attempt still fail the job. No
allocation bound or test expectation is relaxed.

Clang remains the default native compiler locally and in both CI systems. The slower MSVC codec path is
optional (including the explicit x64/ARM64 VS solution builds). GitHub CI sets up MSVC only after the Clang
codec build, for the linker required by .NET Native AOT; it does not rebuild codec DLLs with MSVC.
The managed solution build explicitly selects **Any CPU**, and project tests select `AnyCPU`, overriding
the developer environment's `Platform=x64`. This preserves the Clang DLLs instead of selecting the optional
VS/MSVC native project during `dotnet build`. GitLab uses the same explicit managed-platform selection.

The workflow never runs `Spice.Viewer.Tests`, opens application windows, contacts a guest, accesses Proxmox
credentials, signs executables or publishes a GitHub Release. Hardware-dependent tests may skip on hosted
runners. A successful build does not prove GUI behavior, real-guest compatibility, energy use or ARM64 runtime
behavior. Require **both matrix jobs** to succeed before considering their packages for a release.

## Download and promote

Open a successful workflow run in the repository's Actions tab. The `SpiceViewer-win-x64` and
`SpiceViewer-win-arm64` artifacts each contain the existing `SpiceViewer-<version>-<rid>.zip` and its
`.zip.sha256` sidecar. Download and extract GitHub's artifact wrapper first; the checksum applies to the inner
release zip. Artifacts expire after 14 days. `test-results-win-x64` contains the TRX reports separately.

The portable zip includes SpiceViewer.exe, PveTerm.exe, the self-contained PveLauncher folder, three codec DLLs,
README.txt, LICENSE.txt and source/native rebuild files. The existing packaging script controls this layout and
version. A `v*` tag triggers a build but does not change the project version or create a release. Set project
versions deliberately before tagging, and review both jobs and their matching commit before distribution.

To publish later, download the packages from that reviewed run and attach the **inner release zips and their
checksum sidecars** to a GitHub Release. Release publication and signing remain explicit human steps; CI has
only `contents: read` permission. Do not mistake the outer GitHub artifact archive for the portable release zip.
