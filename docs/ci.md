# GitLab CI setup

The repository is hosted on GitHub. To run [.gitlab-ci.yml](../.gitlab-ci.yml), make the source available to a
GitLab project (for example through a repository mirror) and enable its pipelines. No GitLab instance, mirror or
runner is provisioned by this change.

## Runner requirements

Use a dedicated Windows x64 runner with the **PowerShell 7 (`pwsh`) shell executor**, tagged `windows`, `x64`,
and `spice-build`. Its CPU must meet the x86-64-v3 floor. Preinstall:

- The .NET 10 SDK compatible with `global.json`.
- Visual Studio Build Tools with the C++ x64 tools and Windows SDK for Native AOT linking and WinUI compilation.
- PowerShell 7, Python 3 and Git on PATH.
- MSYS2 CLANG64 with Clang/LLD and LLVM utilities, CMake 3.24+, Ninja, NASM, GNU make, curl, tar with xz and
  sha256sum. See [native/README.md](../native/README.md) for the full native build contract.

Set the runner environment variable `SPICE_MSYS2_ROOT` to its MSYS2 installation directory. The job resolves
`usr/bin/bash.exe` under that root and prepends `clang64/bin` and `usr/bin` to PATH. It uses the existing
`native/build.sh` and `native/verify.py` entry points. MSVC support for codec compilation is being developed
separately; this pipeline uses the currently integrated Clang build. MSVC is already required for AOT linking.

Allow outbound access to the configured NuGet feeds and the source URLs in `native/fetch.sh`. The build checks
pinned native archive hashes, including cached downloads. It does not install tools or download an unpinned
compiler. No Proxmox credentials, `.vv` files or real guests are required.

Use an isolated runner account and disposable workspaces without host credentials, private data or access to
the test VMs. Public merge requests execute repository-controlled scripts. A shell executor is not a sandbox;
do not attach an unrestricted personal desktop or the private build box to untrusted fork pipelines. See
[GitLab's runner security guidance](https://docs.gitlab.com/runner/security/).

## Pipeline behavior

Merge requests, branch pushes and tags run the x64 job. Push pipelines are suppressed for branches with an open
merge request, so one change does not run both a branch and merge-request pipeline. The rules follow
[GitLab's workflow guidance](https://docs.gitlab.com/ci/yaml/workflow/).

One job keeps the codec build, managed build, sequential tests and package on the same clean workspace:

1. Build x64 codecs with the existing hash-verified fetch/build scripts.
2. Verify DLL exports, imports and hardening flags.
3. Build `SpiceViewer.sln` in Release, including compiling the viewer test project without running it.
4. Run all seven window-free test projects one at a time. Any failure stops the job and prevents packaging.
5. Run `tools/package.ps1 -Rid win-x64` to publish and package the viewer, launcher and terminal bridge with
   license notices and native rebuild sources/scripts.

TRX test results and any produced zip/checksum files are retained as downloadable artifacts for 14 days, even
when the job fails. TRX is an artifact only; it is not declared as a GitLab JUnit report. Only the listed paths
are uploaded, never `data/`, `.vv` files, logs or the whole workspace.

The job does not run `Spice.Viewer.Tests`, open a viewer, contact Proxmox, publish a release or deploy anything.
Hardware video tests may skip device-dependent checks when the runner lacks the required decoder. GUI behavior,
actual energy use, real-guest compatibility and ARM64 execution require separate verification. Runner setup and
an actual successful pipeline are needed before describing CI as passing.
