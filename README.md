# SpiceSharp

English | [繁體中文](README.zh-TW.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![100% vibe-coded](https://img.shields.io/badge/100%25-vibe--coded-8b5cf6)

**Your Proxmox console, at home on Windows.**

SpiceSharp is a native Windows SPICE client. Its viewer, **SpiceViewer**, opens Proxmox `.vv` connection files
and brings the guest desktop into a regular Windows window. It uses C# on .NET 10, Native AOT, a Win32 UI and
Direct3D 11 presentation.

## Why this exists

A VM console is something you open to get work done. It should connect quickly, fit the desktop you already use,
send shortcuts to the machine you are controlling, and stop spending resources when the picture stops changing.

SpiceSharp explores what a SPICE client can be when those needs drive the design specifically for Windows:

- **Native desktop behavior.** Per-monitor DPI, dark title bars, snap layouts, full screen, and keyboard capture
  for shortcuts such as Alt+Tab and the Windows key.
- **Performance and energy first.** Present changed content rather than redraw continuously; reuse buffers on
  hot paths and use event-driven audio. Low memory use comes next, then small binaries.
- **A viewer that stands on its own.** Open a `.vv` directly, or use the separate Proxmox launcher. Each SPICE
  console gets its own viewer process; the launcher stays outside the rendering path.
- **Choices you can inspect.** Protocol, transport, codecs and UI have separate layers. Design decisions,
  review findings and verification records live alongside the code. LGPL codecs stay in a replaceable DLL.

These are engineering priorities, not universal performance claims. The [principles](docs/design/principles.md)
and [verification records](docs/verification/README.md) describe the trade-offs and actual measurements.

## What it does today

This is an early **0.1.0** project, packaged as a portable Windows zip. Expect rough edges and incomplete protocol
coverage. The [design spec](docs/design/spec.md) includes future work as well as current requirements.

| Area | Support |
| --- | --- |
| Connection | SPICE `.vv` files, Proxmox spiceproxy tunneling, CA and certificate-subject validation |
| Desktop | Display, cursor, keyboard and mouse, full screen, fit / 1:1 scaling, remembered window state |
| Guest integration | Clipboard and resize-to-window with a compatible SPICE guest agent |
| Media | Opus / PCM playback, MJPEG, software H.264; hardware H.264 / VP9 / HEVC when the device and Windows codecs support them |
| Proxmox tools | Separate WinUI 3 launcher, terminal bridge, VNC bridge to your own VNC viewer |
| Diagnostics | Logs, optional status overlay, recorded-session replay tests and allocation regression tests |

USB redirection, smartcards, microphone input, file transfer and shared folders are outside the current scope.
The viewer is Windows-only. [SpiceSee](https://github.com/aaronpollock/SpiceSee), a major architectural and codec
reference for this project, serves macOS.

## Get started

Requirements: **Windows 11**, on **x64 with x86-64-v3 support** (including AVX2, FMA and BMI1/2), or **ARM64**.
Windows 10 22H2 is best effort. WARP software rendering supports environments without a usable hardware GPU,
including RDP sessions; the x64 CPU requirement still applies. Packaged builds need no separately installed
.NET runtime.

1. Extract a matching `SpiceViewer-<version>-win-x64.zip` or `-win-arm64.zip` from the
   [releases page](https://github.com/Inndy/SpiceSharp/releases), when available, or build from source below.
2. In Proxmox, use a VM configured for a SPICE display and choose **Console → SPICE** to download a `.vv` file.
3. Open it promptly; its connection ticket is short-lived:

   ```powershell
   .\SpiceViewer.exe .\console.vv
   ```

Optionally run `SpiceViewer.exe --register` to associate `.vv` files with that copy for your Windows account;
`--unregister` removes the association. The zip also includes `PveLauncher\PveLauncher.exe` and `PveTerm.exe`.
See the [terminal documentation](src/PveTerm/README.md) and [bundled user guide](tools/package/README.txt).
Settings and logs normally live in `data\` beside the executable; keep the extracted folder writable.

| Default shortcut | Action |
| --- | --- |
| Ctrl+Alt+R | Release keyboard and mouse to Windows |
| Shift+F11 | Toggle full screen |
| Ctrl+Alt+Shift+F12 | Toggle the status overlay |

A `.vv` file can override the release and full-screen hotkeys. Right-click the title bar for the window menu,
including **Send Ctrl+Alt+Del**, scaling and capture settings.

Treat `.vv` files as credentials: they can contain a live ticket. Do not attach them to issues or commit them.
Refresh expired connection files from Proxmox. Certificate mismatches are refused, with no bypass button.

## 100% vibe-coded, openly

**This project is 100% vibe-coded.** Its project code is AI-written under human direction, primarily with Claude
implementing and Codex reviewing and writing regression tests. Vendored third-party code retains its own authorship
and licenses. The human sets the goals and makes the product and architectural decisions.

We still need evidence that the result works: upstream protocol references, scrubbed traffic replays, approved
golden images, allocation tests, independent review and live guest checks. The [agent guide](AGENTS.md) documents
this workflow, and the [reviews](docs/reviews/README.md) retain findings and implementer responses. AI review and
passing tests do not prove every protocol edge case or hostile input is safe. Bug reports and human review are welcome.

## Build and test

Use the .NET 10 SDK selected by `global.json`. Windows Native AOT publishing also requires Visual Studio Build
Tools with C++ tools and the Windows SDK, plus PowerShell 7. The current codec build uses MSYS2 CLANG64 with
Clang/LLD, CMake, Ninja, NASM, GNU make and fetch utilities. See [native/README.md](native/README.md) for the
complete toolchain and Linux cross-build instructions. Dependencies are pinned by version or source archive hash.

Build the codec DLLs from the repository root in an MSYS2 CLANG64 shell:

```sh
bash native/build.sh x64
python native/verify.py x64
```

Then, on Windows:

```powershell
dotnet build SpiceViewer.sln -c Release
dotnet test tests/Spice.Wire.Tests -c Release
pwsh tools/package.ps1
```

The zip and SHA-256 file go to `artifacts/`. The same runnable files go to `out/win-x64/`, preserving its `data\`
folder. For ARM64, cross-build the native DLLs as described in the native guide, then use
`pwsh tools/package.ps1 -Rid win-arm64` with the matching MSVC ARM64 build tools installed.

Run window-free test projects one at a time: `Spice.Native.Tests`, `Spice.Wire.Tests`, `Spice.Replay.Tests`,
`Spice.Video.Tests`, `Spice.Diagnostics.Tests`, `Pve.Api.Tests`, and `Pve.Term.Tests`, all under `tests/`.
Native, replay and video tests need the codec DLLs. **`dotnet test SpiceViewer.sln` also runs viewer tests that
open windows**; reserve it for a dedicated desktop session. Hardware decoder coverage depends on the runner.

## GitLab CI

[`.gitlab-ci.yml`](.gitlab-ci.yml) builds x64 codecs, checks native DLLs, builds the solution, runs the seven
window-free test projects sequentially, and packages a Native AOT portable zip. Test results are retained even
on failure; successful builds expose the zip and checksum as job artifacts.

It requires a Windows x64 runner with the documented tools and tags; see [CI setup](docs/ci.md).
Adding this YAML to GitHub alone does not run a GitLab pipeline. GUI tests, real guests and ARM64 runtime checks
remain separate verification steps.

## Contributing

Issues and pull requests are welcome. For bugs, include the build version, Windows version, CPU architecture,
guest OS, reproduction steps and a redacted log excerpt. Remove tickets, tokens, addresses, hostnames and account
details. Never post credentials or private guest recordings in a public issue.

Start with the [documentation index](docs/README.md), [spec](docs/design/spec.md),
[principles](docs/design/principles.md) and relevant [decisions](docs/decisions/README.md). Keep changes focused;
include a regression test for a behavioral fix. New replay fixtures must be scrubbed with
`tools/spicerec/scrub.py`; golden-image changes require maintainer approval.

| Path | Purpose |
| --- | --- |
| `src/VvFile`, `src/Spice.Wire`, `src/Spice.Transport` | Connection-file parsing, protocol and transport |
| `src/Spice.Canvas`, `src/Spice.Native`, `src/Spice.Media`, `src/Spice.Session` | Drawing, codecs, media and sessions |
| `src/SpiceViewer` | Win32 viewer, presentation and input |
| `src/Pve.Api`, `src/PveLauncher`, `src/Pve.Term`, `src/PveTerm` | Proxmox API, launcher and console bridges |
| `native/`, `tests/`, `tools/`, `docs/` | Native builds, regression coverage, utilities and project records |

## License and acknowledgments

SpiceSharp's own code is [MIT-licensed](LICENSE). SPICE QUIC/LZ/GLZ decoders and FFmpeg's LGPL-only H.264 decoder
live in the replaceable `spicecodec.dll`; other bundled components have their own licenses. The optional WinUI
launcher also distributes Microsoft runtime components under their respective terms.
[licenses/README.md](licenses/README.md) maps the components, and [native/VENDORED.md](native/VENDORED.md)
records upstream sources and modifications. Release packages include license notices and native rebuild sources
and scripts; pinned external sources are fetched by hash.

Thanks to [SpiceSee](https://github.com/aaronpollock/SpiceSee), the SPICE and QEMU projects, libjpeg-turbo, Opus,
LZ4 and FFmpeg. Their implementations and reference sources make this client possible.
