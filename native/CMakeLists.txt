cmake_minimum_required(VERSION 3.24)
project(SpiceCodec C)
set(CMAKE_C_STANDARD 11)

if(MSVC)
  # Static CRT: replacement DLLs need no separately installed VC runtime.
  set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded)
  add_compile_options(/O2 /GL /Gy /Gw /guard:cf /utf-8)
  add_compile_definitions(_CRT_SECURE_NO_WARNINGS)
  if(MSVC_C_ARCHITECTURE_ID STREQUAL "x64")
    add_compile_options(/arch:AVX2)
  endif()
  add_link_options(/LTCG /OPT:REF /OPT:ICF /GUARD:CF /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /INCREMENTAL:NO)
  add_link_options(/MACHINE:${MSVC_C_ARCHITECTURE_ID})
endif()

# build.sh builds libjpeg-turbo and opus as static libraries first and passes them here; this project links all
# three DLLs, so the same export trimming and dead-code removal applies to each.
set(SC_TURBOJPEG_LIB "" CACHE FILEPATH "static libturbojpeg.a or turbojpeg-static.lib")
set(SC_OPUS_LIB "" CACHE FILEPATH "static libopus.a or opus.lib")
set(SC_LZ4_DIR "${CMAKE_CURRENT_SOURCE_DIR}/downloads/lz4-1.10.0/lib" CACHE PATH "lz4 lib/ from the pinned download")
set(SC_FFMPEG_SRC "" CACHE PATH "the pinned FFmpeg source tree")
set(SC_FFMPEG_BUILD "" CACHE PATH "FFmpeg's build tree (static libraries and generated headers)")
foreach(v SC_TURBOJPEG_LIB SC_OPUS_LIB)
  if(NOT EXISTS "${${v}}")
    message(FATAL_ERROR "${v} is not set or missing; run bash native/build.sh")
  endif()
endforeach()
if(MSVC)
  set(SC_AVCODEC libavcodec/avcodec.lib)
  set(SC_AVUTIL libavutil/avutil.lib)
  set(SC_FFMPEG_HELPER libavcodec/mpegutils.o)
else()
  set(SC_AVCODEC libavcodec/libavcodec.a)
  set(SC_AVUTIL libavutil/libavutil.a)
endif()
foreach(f ${SC_AVCODEC} ${SC_AVUTIL} ${SC_FFMPEG_HELPER})
  if(NOT EXISTS "${SC_FFMPEG_BUILD}/${f}")
    message(FATAL_ERROR "${SC_FFMPEG_BUILD}/${f} is missing; run bash native/build.sh")
  endif()
endforeach()
if(NOT EXISTS "${SC_LZ4_DIR}/lz4.c")
  message(FATAL_ERROR "${SC_LZ4_DIR}/lz4.c is missing; run bash native/fetch.sh")
endif()

# Only the listed functions are exported (spicecodec: SC_API in spice_codec.h; the others: their .def file).
# lld's MinGW driver defaults to -opt:noref, so --gc-sections is what drops unreferenced code.
if(MSVC)
  set(SC_DLL_LINK_OPTIONS)
else()
  set(SC_DLL_LINK_OPTIONS -Wl,--exclude-all-symbols -Wl,--gc-sections)
endif()

add_library(spicecodec SHARED spicecodec/codec_bridge.c spicecodec/vendor/common/quic.c spicecodec/vendor/common/lz.c spicecodec/vendor/common/mem.c spicecodec/vendor/decode-glz.c ${SC_LZ4_DIR}/lz4.c)
if(MSVC AND MSVC_C_ARCHITECTURE_ID STREQUAL "x64")
  # More aggressive inlining measurably speeds up QUIC. Keep the other codecs'
  # inlining budget unchanged: whole-target /Ob3 regressed some LZ/GLZ workloads.
  set_property(SOURCE spicecodec/vendor/common/quic.c APPEND PROPERTY COMPILE_OPTIONS /Ob3)
endif()
target_include_directories(spicecodec PRIVATE spicecodec/include spicecodec/shim spicecodec/vendor spicecodec/vendor/common ${SC_LZ4_DIR})
if(MSVC)
  # gnulib verify.h deliberately declares an anonymous struct inside sizeof.
  # MSVC accepts the compile-time check but warns about this GNU-compatible idiom.
  target_compile_options(spicecodec PRIVATE /wd4116)
endif()
set_target_properties(spicecodec PROPERTIES PREFIX "")
target_link_options(spicecodec PRIVATE ${SC_DLL_LINK_OPTIONS})
# Software H.264 (libavcodec, LGPL): compiled apart from the vendored decoders, which see the SpiceSee shims instead of
# FFmpeg's headers. Linked statically, so all LGPL code stays in this one replaceable DLL.
add_library(sc_h264 OBJECT spicecodec/h264_bridge.c)
target_include_directories(sc_h264 PRIVATE spicecodec/include ${SC_FFMPEG_BUILD} ${SC_FFMPEG_SRC})
target_sources(spicecodec PRIVATE $<TARGET_OBJECTS:sc_h264>)
target_link_libraries(spicecodec PRIVATE ${SC_FFMPEG_BUILD}/${SC_AVCODEC} ${SC_FFMPEG_BUILD}/${SC_AVUTIL} bcrypt)
if(MSVC)
  # MSVC LTCG resolves even the debug helper referenced behind CONFIG_MPEGVIDEODEC=0
  # in h264dec.c. Supply upstream's object; /OPT:REF removes its unused functions.
  target_link_libraries(spicecodec PRIVATE ${SC_FFMPEG_BUILD}/libavcodec/mpegutils.o)
endif()

# The .def file is the only source: its EXPORTS pull the needed members out of the static archive.
add_library(turbojpeg SHARED turbojpeg.def)
add_library(opus SHARED opus.def)
if(MSVC)
  # Give .def-only DLLs a COFF input so link.exe does not warn about missing objects.
  target_sources(turbojpeg PRIVATE dll_stub.c)
  target_sources(opus PRIVATE dll_stub.c)
endif()
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
