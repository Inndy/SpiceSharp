# SpiceViewer

A native Windows viewer for Proxmox SPICE connection files (`.vv`), written in C# for .NET 10 Native AOT with Win32
and a Direct3D 11 flip-model presenter. It is built to use as little energy as possible: nothing runs while the guest
is idle, frames are presented only when something changed, and the hot paths don't allocate.

Status: v0 for Windows x64, shipped as a portable zip. See [docs/design/spec.md](docs/design/spec.md) for the full
design and [docs/README.md](docs/README.md) for all documentation.

## Build

Requirements: the .NET 10 SDK, and for Native AOT on Windows the MSVC linker (Visual Studio Build Tools). The codec
DLLs are built with clang from MSYS2 (or llvm-mingw on Linux), see [native/README.md](native/README.md).

```sh
bash native/build.sh x64          # spicecodec.dll, turbojpeg.dll, opus.dll -> native/out/win-x64
dotnet build SpiceViewer.sln
dotnet test SpiceViewer.sln
pwsh tools/package.ps1            # Native AOT publish + artifacts/SpiceViewer-<version>-win-x64.zip; out/win-x64/ is the same set, ready to run (keeps its data)
```

## Layout

| Path | Contents |
|---|---|
| `src/VvFile` | `.vv` parser |
| `src/Spice.Wire` | Protocol messages, readers and writers |
| `src/Spice.Transport` | Proxy tunnel, pinned TLS, link handshake, channels |
| `src/Spice.Native` | Bindings to the native codec DLLs |
| `src/Spice.Canvas` | Surfaces, image decoding and drawing commands |
| `src/Spice.Media` | Audio (Opus/raw PCM) and MJPEG stream decoders |
| `src/Spice.Session` | Channels combined into a session: display, cursor, inputs, agent, playback |
| `src/SpiceViewer` | The Windows app: window, presenter, input, clipboard, audio output, settings |
| `native/` | Vendored C codecs and the build for the native DLLs |
| `tests/` | Unit, protocol, replay and viewer tests; `tests/fixtures` holds recorded sessions with approved goldens |
| `tools/` | `spice-cli`, `spicerec` (fixture recorder), `pve-vv.py` (dev helper), `package.ps1` |
| `docs/` | Spec, decision records, verification and review records |

## License

SpiceSharp is free software under the MIT License, see [LICENSE](LICENSE). The SPICE QUIC/LZ/GLZ decoders are
LGPL-2.1-or-later and live in the replaceable `spicecodec.dll`, which also holds the BSD-licensed lz4 block decoder;
libjpeg-turbo and libopus are BSD-licensed.
[licenses/README.md](licenses/README.md) maps every path and every component of the release package to its license,
and [native/VENDORED.md](native/VENDORED.md) records the origin and changes of each vendored file.
