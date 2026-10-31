# 0001. Native toolchain: msys2 clang64 instead of clang-cl

- Status: Accepted
- Date: 2026-10-29
- Decided by: the human

- **Context:** the draft says codecs live in spicecodec.dll built from vendored C; the human asked that the whole product be cross-compilable from a non-Windows host.
- **Decision:** build spicecodec.dll, turbojpeg.dll and opus.dll with CMake + clang (msys2 `clang64` on Windows, llvm-mingw elsewhere). See `native/README.md`.
- **Reason:** one toolchain on every host; no Visual Studio dependency. CFG, ASLR and NX are enabled in the link flags.

Amended by [0046](0046-msvc-native-codecs.md): MSVC is also allowed on Windows; MinGW remains the cross-build path.
