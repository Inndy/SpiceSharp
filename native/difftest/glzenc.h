/* A small GLZ encoder for tests. No GLZ encoder is vendored (spice-server's is not used here), so the
   difference test and the benchmark write their own synthetic GLZ streams with this. It is written from the
   decoder's format (vendor/decode-glz-tmpl.c), not derived from spice-server. Not upstream code (MIT).

   It emits literals, same-image references (runs, short and long pixel offsets, overlapping copies) and references
   into earlier images of the window, so every branch of the decoder's reference parsing is reachable. It also
   computes what a correct decoder outputs (`expected`), which lets the harness check that its synthetic inputs
   decode as intended. */
#ifndef DT_GLZENC_H
#define DT_GLZENC_H
#include <stdint.h>
#include <stddef.h>

enum { GLZ_RGB16 = 6, GLZ_RGB24 = 7, GLZ_RGB32 = 8, GLZ_RGBA = 9 };

/* An earlier image the encoder may refer to: its id and decoded pixels (b, g, r, pad bytes as the decoder
   leaves them). The referenced image must still be in the decoder's window when this image is decoded. */
typedef struct glz_window_image {
    uint64_t id;
    uint32_t pixels;
    const uint32_t *px;
} glz_window_image;

enum {
    GLZ_STYLE_NORMAL = 0,   /* longest match among the candidates, literals otherwise */
    GLZ_STYLE_LITERALS,     /* literals only */
    GLZ_STYLE_RANDOM_LEN,   /* matches cut to random lengths: many short references */
    GLZ_STYLE_FAR,          /* prefers far same-image and cross-image references */
    GLZ_STYLE_COUNT
};

typedef struct glzenc_params {
    int type;                       /* GLZ_RGB16/RGB24/RGB32/RGBA */
    int top_down;
    uint32_t width, height;
    uint64_t id;
    uint32_t win_head_dist;
    /* RGB16: width*height uint16_t (x1r5g5b5, as the guest sends); otherwise width*height*4 bytes b, g, r, a. */
    const void *src;
    const glz_window_image *window; /* may be NULL */
    int window_count;
    uint32_t seed;
    int style;
} glzenc_params;

/* Writes header and body to out. Returns the byte count, or 0 if cap is too small. If expected is not NULL it
   receives width*height decoded pixels. */
size_t glzenc_encode(const glzenc_params *p, uint8_t *out, size_t cap, uint32_t *expected);
#endif
