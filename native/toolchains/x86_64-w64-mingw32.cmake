set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR AMD64)
set(CMAKE_C_COMPILER x86_64-w64-mingw32-clang)
set(CMAKE_CXX_COMPILER x86_64-w64-mingw32-clang++)
include(${CMAKE_CURRENT_LIST_DIR}/hardening.cmake)
# Archives hold LTO bitcode (native/README.md): use LLVM's archiver, which indexes bitcode symbols.
# Both MSYS2 clang64 and llvm-mingw ship these under the plain names.
find_program(CMAKE_AR NAMES llvm-ar REQUIRED)
find_program(CMAKE_RANLIB NAMES llvm-ranlib REQUIRED)
