cmake_minimum_required(VERSION 3.24)
project(SpiceCodec C)
set(CMAKE_C_STANDARD 11)

# build.sh builds libjpeg-turbo and opus as static libraries first and passes them here; this project links all
# three DLLs, so the same export trimming and dead-code removal applies to each.
set(SC_TURBOJPEG_LIB "" CACHE FILEPATH "static libturbojpeg.a")
set(SC_OPUS_LIB "" CACHE FILEPATH "static libopus.a")
set(SC_LZ4_DIR "${CMAKE_CURRENT_SOURCE_DIR}/downloads/lz4-1.10.0/lib" CACHE PATH "lz4 lib/ from the pinned download")
foreach(v SC_TURBOJPEG_LIB SC_OPUS_LIB)
  if(NOT EXISTS "${${v}}")
    message(FATAL_ERROR "${v} is not set or missing; run bash native/build.sh")
  endif()
endforeach()
if(NOT EXISTS "${SC_LZ4_DIR}/lz4.c")
  message(FATAL_ERROR "${SC_LZ4_DIR}/lz4.c is missing; run bash native/fetch.sh")
endif()

# Only the listed functions are exported (spicecodec: SC_API in spice_codec.h; the others: their .def file).
# lld's MinGW driver defaults to -opt:noref, so --gc-sections is what drops unreferenced code.
set(SC_DLL_LINK_OPTIONS -Wl,--exclude-all-symbols -Wl,--gc-sections)

add_library(spicecodec SHARED spicecodec/codec_bridge.c spicecodec/vendor/common/quic.c spicecodec/vendor/common/lz.c spicecodec/vendor/common/mem.c spicecodec/vendor/decode-glz.c ${SC_LZ4_DIR}/lz4.c)
target_include_directories(spicecodec PRIVATE spicecodec/include spicecodec/shim spicecodec/vendor spicecodec/vendor/common ${SC_LZ4_DIR})
set_target_properties(spicecodec PROPERTIES PREFIX "")
target_link_options(spicecodec PRIVATE ${SC_DLL_LINK_OPTIONS})

# The .def file is the only source: its EXPORTS pull the needed members out of the static archive.
add_library(turbojpeg SHARED turbojpeg.def)
add_library(opus SHARED opus.def)
target_link_libraries(turbojpeg PRIVATE ${SC_TURBOJPEG_LIB})
target_link_libraries(opus PRIVATE ${SC_OPUS_LIB})
foreach(t turbojpeg opus)
  set_target_properties(${t} PROPERTIES PREFIX "" LINKER_LANGUAGE C)
  target_link_options(${t} PRIVATE ${SC_DLL_LINK_OPTIONS})
endforeach()

# The smoke test compresses its LZ4 fixtures with its own copy of lz4.c; the DLL exports only sc_*.
add_executable(codec-smoke test/smoke.c ${SC_LZ4_DIR}/lz4.c)
target_include_directories(codec-smoke PRIVATE spicecodec/include ${SC_LZ4_DIR})
target_link_libraries(codec-smoke PRIVATE spicecodec)
enable_testing()
add_test(NAME roundtrip COMMAND codec-smoke)
