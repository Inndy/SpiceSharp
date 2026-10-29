set(CMAKE_C_FLAGS_INIT "-O2 -mguard=cf")
set(CMAKE_CXX_FLAGS_INIT "-O2 -mguard=cf")
set(CMAKE_C_FLAGS_RELEASE "-O2 -DNDEBUG" CACHE STRING "Release C flags")
set(CMAKE_CXX_FLAGS_RELEASE "-O2 -DNDEBUG" CACHE STRING "Release C++ flags")
# --strip-debug: llvm-mingw ships its CRT startup objects with DWARF (about 35 KB per image); MSYS2 does not.
# Stripping also drops lld's default build ID (the CodeView record), so ask for it explicitly.
set(CMAKE_SHARED_LINKER_FLAGS_INIT "-static-libgcc -fuse-ld=lld -Wl,--dynamicbase,--high-entropy-va,--nxcompat,--guard-cf,--strip-debug,--build-id")
set(CMAKE_EXE_LINKER_FLAGS_INIT "-static-libgcc -fuse-ld=lld -Wl,--dynamicbase,--high-entropy-va,--nxcompat,--guard-cf,--strip-debug,--build-id")
