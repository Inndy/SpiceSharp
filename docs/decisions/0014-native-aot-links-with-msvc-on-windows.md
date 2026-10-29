# 0014. Native AOT links with MSVC on Windows

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** .NET Native AOT for `win-x64`/`win-arm64` links with MSVC `link.exe` (Visual Studio "Desktop development with C++"); it cannot cross-link from Linux/macOS. The ILCompiler targets also call `vswhere.exe` from `PATH`.
- **Decision:** release builds of SpiceViewer.exe are published on Windows (CI: a Windows runner). Put `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer` on `PATH` before `dotnet publish -c Release -r win-x64`. The native DLLs (msys2/llvm-mingw) and all managed projects still build anywhere.
- **Reason:** no supported cross-OS AOT linker; everything else in the repo stays host-independent. If a Linux-only release pipeline is wanted later, the unofficial `PublishAotCross` package (lld-link + Windows SDK libs from `xwin`) is the known route; not adopted for v0.
