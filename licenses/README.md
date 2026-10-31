# Licenses

SpiceSharp is free software. Code written for this project is under the MIT License ([../LICENSE](../LICENSE)).
Third-party code keeps its own license; this table maps the repository and the release package to them.

## In this repository

| Path | License | Notes |
|---|---|---|
| everything not listed below | MIT | [../LICENSE](../LICENSE) |
| `native/spicecodec/vendor/common/` (QUIC, LZ, mem, macros) | LGPL-2.1-or-later | spice-common; `lz.h` is MIT (FastLZ), `draw.h` BSD-3-Clause |
| `native/spicecodec/vendor/decode*.{c,h}` | LGPL-2.1-or-later | spice-gtk GLZ decoder |
| `native/spicecodec/vendor/spice/` | BSD-3-Clause | spice-protocol; `macros.h` is LGPL-2.1-or-later |
| `native/spicecodec/codec_bridge.c`, `include/`, `shim/` | MIT | from SpiceSee, [../native/LICENSE.SpiceSee](../native/LICENSE.SpiceSee) |

Every vendored file carries its upstream license header. Modified LGPL files also carry a dated `MODIFIED FILE`
notice (LGPL-2.1 section 2b). [../native/VENDORED.md](../native/VENDORED.md) records each file's origin, revision
and changes. The LGPL text is [../native/LICENSE.LGPL-2.1](../native/LICENSE.LGPL-2.1).

libjpeg-turbo, libopus, lz4 and FFmpeg are not in the repository: `native/fetch.sh` downloads pinned releases (checked
by SHA-256) into `native/downloads/`, and their license files come from there. Only lz4's `lib/lz4.c` is built. Of
FFmpeg, only libavcodec's H.264 decoder and the libavutil it needs are built, configured without `--enable-gpl` or
`--enable-nonfree` (LGPL-2.1-or-later), and linked statically into `spicecodec.dll`, so every LGPL part of the
package stays in that one replaceable DLL.

## In the release package

`tools/package.ps1` writes one `LICENSE.txt` into the zip: SpiceViewer's license, a list of every component built
into the package (version, file it is in, license, upstream), then each full license text. It also ships every
committed file under `native/` as `source\native`, the complete source and build scripts of the LGPL
`spicecodec.dll`.

| Component | In | License | Text from |
|---|---|---|---|
| SpiceViewer, PveTerm, PveLauncher | the exes, codec bridge in spicecodec.dll | MIT | `LICENSE` |
| .NET runtime (Native AOT) | the exes | MIT and its third-party notices | `dotnet-runtime-MIT.txt`; the ILCompiler package's `THIRD-PARTY-NOTICES.TXT` from NuGet |
| CsWin32 (generated interop code) | the exes | MIT | `cswin32-MIT.txt` |
| SpiceSee codec bridge and shims | spicecodec.dll | MIT | `native/LICENSE.SpiceSee` |
| spice-common, spice-gtk decoders | spicecodec.dll | LGPL-2.1-or-later | `native/LICENSE.LGPL-2.1` |
| FastLZ | spicecodec.dll | MIT | `fastlz-MIT.txt` (from `lz.c`'s header) |
| spice-protocol headers | spicecodec.dll | BSD-3-Clause | `spice-protocol-BSD-3-Clause.txt` |
| libjpeg-turbo | turbojpeg.dll | IJG, BSD-3-Clause, zlib | `LICENSE.md` and `README.ijg` from the pinned download |
| libopus | opus.dll | BSD-3-Clause, patent licenses | `COPYING` from the pinned download |
| lz4 (block decoder, `lib/lz4.c`) | spicecodec.dll | BSD-2-Clause | `lib/LICENSE` from the pinned download |
| FFmpeg 9.0.2 (libavcodec H.264 decoder, libavutil; unmodified, LGPL configuration) | spicecodec.dll | LGPL-2.1-or-later | `LICENSE.md` and `COPYING.LGPLv2.1` from the pinned download |
| MinGW-w64 runtime (static) | all three DLLs | ZPL-2.1 and others | `mingw-w64-runtime.txt` |
| LLVM compiler-rt builtins (static) | all three DLLs | Apache-2.0 WITH LLVM-exception | `llvm-compiler-rt.txt` |
| Windows App SDK 2.5.1 components (WinUI 2.3.9, Foundation 2.3.12, InteractiveExperiences 2.1.9, Base 2.0.4; self-contained) | `PveLauncher\` except the exe and WebView2 files | Microsoft Software License Terms (distributable code) and third-party notices | `license.txt` and `NOTICE.txt` of `Microsoft.WindowsAppSDK.WinUI`, `NOTICE.txt` of `Microsoft.WindowsAppSDK.Base`, from NuGet |
| WebView2 SDK (brought in by WinUI; unused) | `PveLauncher\WebView2Loader.dll`, `Microsoft.Web.WebView2.Core.dll` | BSD-3-Clause (Microsoft) | `LICENSE.txt` and `NOTICE.txt` of `Microsoft.Web.WebView2` from NuGet |
| C#/WinRT runtime (`WinRT.Runtime`) | `PveLauncher.exe` | MIT | `cswinrt-MIT.txt` |
| Windows SDK .NET projection (`Microsoft.Windows.SDK.NET.Ref`) | `PveLauncher.exe` | Microsoft Windows SDK license terms | the package has only a link (https://aka.ms/WinSDKLicenseURL); the text is `sdk_license.txt` of `Microsoft.Windows.SDK.Win32Metadata` (restored for CsWin32), from NuGet |

The last four come only with the v1 launcher ([decisions/0038](../docs/decisions/0038-v1-pve-launcher-winui3-unpackaged-self-contained-aot.md));
`tools/package.ps1 -NoLauncher` leaves them, `PveTerm.exe` and `PveLauncher\` out. The Windows App SDK is not free
software: its terms allow redistribution with an app and ask that end users accept terms that protect it at least
as well, which `LICENSE.txt` states before the component list. Its versions are read from the launcher's
`project.assets.json`, so the texts match what was published.

The toolchain texts were copied from MSYS2 `clang64/share/licenses` (crt 14.0.0.r426, compiler-rt 22.1.8). Refresh
them when the toolchain changes what it links statically. Check with `llvm-objdump -p` that the DLLs import only
`KERNEL32` and `api-ms-win-crt-*` (UCRT is part of Windows), so nothing else needs a notice.
