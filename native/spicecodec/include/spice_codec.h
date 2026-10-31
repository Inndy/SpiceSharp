/* The only header Swift sees. Wraps the vendored QUIC, LZ and GLZ decoders so that a corrupt
   stream returns an error rather than aborting: every entry point installs a jmp_buf that the
   vendored fatal paths unwind to. */
#ifndef SPICE_CODEC_H
#define SPICE_CODEC_H

#include <stdint.h>
#include <stddef.h>

#if defined(_WIN32)
#define SC_API __declspec(dllexport)
#define SC_CALL __cdecl
#else
#define SC_API
#define SC_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    SC_IMAGE_INVALID = 0, SC_IMAGE_GRAY, SC_IMAGE_RGB16, SC_IMAGE_RGB24, SC_IMAGE_RGB32, SC_IMAGE_RGBA,
    SC_IMAGE_PLT1_LE, SC_IMAGE_PLT1_BE, SC_IMAGE_PLT4_LE, SC_IMAGE_PLT4_BE, SC_IMAGE_PLT8, SC_IMAGE_XXXA
} sc_image_type;

/* Interface version, raised whenever a function's contract changes: 2 since sc_lz_decode takes out_cap and writes
   tight rows. A caller checks it before anything else; builds without this function are version 1. */
#define SC_ABI_VERSION 2
SC_API int      SC_CALL sc_abi_version(void);

/* ---- QUIC ---- */
typedef struct sc_quic sc_quic;
SC_API sc_quic *SC_CALL sc_quic_create(void);
SC_API void     SC_CALL sc_quic_destroy(sc_quic *);
/* 0 on success, <0 on corrupt input. Fills width/height/type. */
SC_API int      SC_CALL sc_quic_begin(sc_quic *, const uint8_t *data, size_t len, int *width, int *height, sc_image_type *type);
/* Writes BGRA (RGB32) or BGRA-with-alpha (RGBA) rows into out; stride may be negative for bottom-up. */
SC_API int      SC_CALL sc_quic_decode(sc_quic *, uint8_t *out, int stride);
/* Test helper: encodes 32bpp BGRA rows. Returns bytes written or <0. */
SC_API int      SC_CALL sc_quic_encode_rgb32(const uint8_t *pixels, int width, int height, int stride, uint8_t *out, size_t out_cap);

/* ---- LZ ---- */
typedef struct sc_lz sc_lz;
SC_API sc_lz   *SC_CALL sc_lz_create(void);
SC_API void     SC_CALL sc_lz_destroy(sc_lz *);
/* palette: up to 256 BGRx entries for PLT types, may be NULL otherwise. */
SC_API int      SC_CALL sc_lz_begin(sc_lz *, const uint8_t *data, size_t len, const uint32_t *palette, int palette_count,
                     int *width, int *height, sc_image_type *type, int *top_down);
/* Writes rows of width*4 bytes, palette types included: RGB32, or RGBA when type is RGBA; XXXA writes only byte 3
   of each pixel. out_cap must be at least width*height*4, or it fails with nothing written. Palette rows decode
   padded to whole bytes (up to 7 more pixels per row): when out_cap holds round_up(width, 8)*height*4 bytes the
   decode uses out as its working space and allocates nothing, and bytes past width*height*4 are then undefined;
   otherwise nothing past width*height*4 is written. */
SC_API int      SC_CALL sc_lz_decode(sc_lz *, uint8_t *out, size_t out_cap);
SC_API int      SC_CALL sc_lz_encode_rgb32(const uint8_t *pixels, int width, int height, int stride, uint8_t *out, size_t out_cap);
/* Test helper: encodes a 32bpp buffer as an XXXA (alpha-only) plane, byte 3 of each pixel. */
SC_API int      SC_CALL sc_lz_encode_xxxa(const uint8_t *pixels, int width, int height, int stride, uint8_t *out, size_t out_cap);

/* ---- GLZ ---- */
typedef struct sc_glz_window sc_glz_window;
typedef struct sc_glz sc_glz;
SC_API sc_glz_window *SC_CALL sc_glz_window_create(void);
SC_API void           SC_CALL sc_glz_window_clear(sc_glz_window *);
/* Frees the retired images the window keeps for reuse (at most 32 MiB) and returns their bytes. The images
   the window holds for later references stay. Optional: a version-2 DLL may lack it. */
SC_API size_t         SC_CALL sc_glz_window_trim(sc_glz_window *);
SC_API void           SC_CALL sc_glz_window_destroy(sc_glz_window *);
SC_API sc_glz  *SC_CALL sc_glz_create(sc_glz_window *);
SC_API void     SC_CALL sc_glz_destroy(sc_glz *);
/* On success *out points at BGRA pixels owned by the window; copy before the next decode.
   top_down is 0 when the GLZ header marks the rows bottom-up, as Windows guests routinely send. */
SC_API int      SC_CALL sc_glz_decode(sc_glz *, const uint8_t *data, size_t len, const uint8_t **out, int *width, int *height, int *stride, int *top_down);

/* ---- LZ4 ---- */
/* src is SPICE LZ4 image data after its 2-byte header (top_down, SPICE bitmap format): one or more
   [u32 big-endian size][LZ4 block] records, decoded as one stream into dst (later blocks may refer to
   earlier output). The output is tightly packed rows, width * bytes per pixel each. Returns the number
   of bytes written, or -1 on any error (truncated record, size beyond src, bad block, dst overflow).
   Allocates nothing. */
SC_API int      SC_CALL sc_lz4_decode(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_cap);

/* ---- H.264 (libavcodec, h264_bridge.c) ---- */
/* Software H.264 decoding for the streams hardware decoders refuse (High 4:4:4 Predictive and the rest of the 8-bit
   High profiles). Optional: the interface version stays 2, and a DLL without these functions only has no software
   video path. One handle per stream, used from one thread at a time. */
typedef struct sc_h264 sc_h264;
typedef struct {
    int width, height;  /* the cropped picture size */
    int chroma;         /* 0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4 */
    int supported;      /* sc_h264_convert takes the picture: 8-bit YUV, GBR or gray */
    int full_range;
    int matrix;         /* H.264 matrix_coefficients (AVColorSpace): 0 GBR, 1 BT.709, 5/6 BT.601, 2 unspecified... */
} sc_h264_picture;
#define SC_H264_PICTURE 1   /* a picture was decoded; *info describes it */
#define SC_H264_NONE 0      /* the data was taken, no picture yet */
#define SC_H264_INVALID (-1) /* the data is damaged; the decoder can go on */
#define SC_H264_FATAL (-2)   /* out of memory or an internal error; close the handle */
#define SC_H264_MIRROR 1    /* sc_h264_convert flag: write the rows bottom-up */
/* threads: slice threads (1-16). NULL when libavcodec can't be set up. */
SC_API sc_h264 *SC_CALL sc_h264_open(int threads);
SC_API void     SC_CALL sc_h264_close(sc_h264 *);
/* Decodes one access unit (Annex B). The newest picture is kept for sc_h264_convert until the next picture. */
SC_API int      SC_CALL sc_h264_decode(sc_h264 *, const uint8_t *data, size_t len, sc_h264_picture *info);
/* Converts the newest picture to opaque BGRA rows (stride bytes apart, at least width * 4) at its own size. Returns 0,
   or -1 without a picture, with an unsupported format or when dst_cap is too small. */
SC_API int      SC_CALL sc_h264_convert(sc_h264 *, uint8_t *dst, size_t dst_cap, int stride, int flags);

#ifdef __cplusplus
}
#endif
#endif
