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

libjpeg-turbo, libopus and lz4 are not in the repository: `native/fetch.sh` downloads pinned releases (checked by
SHA-256) into `native/downloads/`, and their license files come from there. Only lz4's `lib/lz4.c` is built.

## In the release package

`tools/package.ps1` writes one `LICENSE.txt` into the zip: SpiceViewer's license, a list of every component built
into the package (version, file it is in, license, upstream), then each full license text. It also ships every
committed file under `native/` as `source\native`, the complete source and build scripts of the LGPL
`spicecodec.dll`.

| Component | In | License | Text from |
|---|---|---|---|
| SpiceViewer | exe, codec bridge in spicecodec.dll | MIT | `LICENSE` |
| .NET runtime (Native AOT) | exe | MIT and its third-party notices | `dotnet-runtime-MIT.txt`; the ILCompiler package's `THIRD-PARTY-NOTICES.TXT` from NuGet |
| CsWin32 (generated interop code) | exe | MIT | `cswin32-MIT.txt` |
| SpiceSee codec bridge and shims | spicecodec.dll | MIT | `native/LICENSE.SpiceSee` |
| spice-common, spice-gtk decoders | spicecodec.dll | LGPL-2.1-or-later | `native/LICENSE.LGPL-2.1` |
| FastLZ | spicecodec.dll | MIT | `fastlz-MIT.txt` (from `lz.c`'s header) |
| spice-protocol headers | spicecodec.dll | BSD-3-Clause | `spice-protocol-BSD-3-Clause.txt` |
| libjpeg-turbo | turbojpeg.dll | IJG, BSD-3-Clause, zlib | `LICENSE.md` and `README.ijg` from the pinned download |
| libopus | opus.dll | BSD-3-Clause, patent licenses | `COPYING` from the pinned download |
| lz4 (block decoder, `lib/lz4.c`) | spicecodec.dll | BSD-2-Clause | `lib/LICENSE` from the pinned download |
| MinGW-w64 runtime (static) | all three DLLs | ZPL-2.1 and others | `mingw-w64-runtime.txt` |
| LLVM compiler-rt builtins (static) | all three DLLs | Apache-2.0 WITH LLVM-exception | `llvm-compiler-rt.txt` |

The toolchain texts were copied from MSYS2 `clang64/share/licenses` (crt 14.0.0.r426, compiler-rt 22.1.8). Refresh
them when the toolchain changes what it links statically. Check with `llvm-objdump -p` that the DLLs import only
`KERNEL32` and `api-ms-win-crt-*` (UCRT is part of Windows), so nothing else needs a notice.
