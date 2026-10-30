/* Wraps the vendored QUIC, LZ and GLZ decoders and the pinned lz4 block decoder behind spice_codec.h.
   Not upstream code.

   The vendored codecs report corruption by calling usr->error(), which upstream expects never to
   return. Every entry point here installs a jmp_buf first, so those paths unwind into an error
   return instead of aborting: a hostile server must not be able to kill the process. */
#include "spice_codec.h"
#include "spice_common.h"
#include "common/quic.h"
#include "common/lz.h"
#include "decode.h"
#include "lz4.h"
#include <limits.h>
#include <stdarg.h>

_Thread_local jmp_buf *sc_fatal_env;
_Thread_local const uint8_t *sc_glz_input_end;
extern void sc_glz_abort_pending(void);
extern size_t sc_glz_window_trim_pool(SpiceGlzDecoderWindow *w);
static uint32_t sc_be32(const uint8_t *p) { return ((uint32_t)p[0]<<24)|((uint32_t)p[1]<<16)|((uint32_t)p[2]<<8)|p[3]; }
static uint32_t sc_le32(const uint8_t *p) { return ((uint32_t)p[3]<<24)|((uint32_t)p[2]<<16)|((uint32_t)p[1]<<8)|p[0]; }
static int sc_sizes(uint32_t w, uint32_t h) { return w && h && (uint64_t)w*h <= 67108864 && w <= 16777216; }


void sc_fatal(const char *fmt, ...)
{
    (void)fmt;
    if (sc_fatal_env) {
        longjmp(*sc_fatal_env, 1);
    }
    abort();
}

#define SC_GUARD(env) jmp_buf env; jmp_buf *sc_prev = sc_fatal_env; sc_fatal_env = &env; \
    if (setjmp(env)) { sc_fatal_env = sc_prev; return -1; }
#define SC_UNGUARD() sc_fatal_env = sc_prev

/* ---- QUIC ---- */

struct sc_quic { QuicUsrContext usr; QuicContext *ctx; QuicImageType type; int width, height; };

SPICE_GNUC_NORETURN static void q_error(QuicUsrContext *u, const char *fmt, ...) { (void)u; sc_fatal(fmt); abort(); }
static void q_warn(QuicUsrContext *u, const char *fmt, ...) { (void)u; (void)fmt; }
static void q_info(QuicUsrContext *u, const char *fmt, ...) { (void)u; (void)fmt; }
static void *q_malloc(QuicUsrContext *u, int n) { (void)u; return malloc((size_t)n); }
static void q_free(QuicUsrContext *u, void *p) { (void)u; free(p); }
static int q_more_space(QuicUsrContext *u, uint32_t **io, int rows_completed) { (void)u; (void)io; (void)rows_completed; return 0; }
static int q_more_lines(QuicUsrContext *u, uint8_t **lines) { (void)u; (void)lines; return 0; }

sc_quic *sc_quic_create(void)
{
    sc_quic *q = calloc(1, sizeof *q);
    if (!q) return NULL;
    q->usr.error = q_error; q->usr.warn = q_warn; q->usr.info = q_info;
    q->usr.malloc = q_malloc; q->usr.free = q_free;
    q->usr.more_space = q_more_space; q->usr.more_lines = q_more_lines;
    q->ctx = quic_create(&q->usr);
    if (!q->ctx) { free(q); return NULL; }
    return q;
}

void sc_quic_destroy(sc_quic *q) { if (q) { quic_destroy(q->ctx); free(q); } }

static sc_image_type from_quic(QuicImageType t)
{
    switch (t) {
    case QUIC_IMAGE_TYPE_GRAY:  return SC_IMAGE_GRAY;
    case QUIC_IMAGE_TYPE_RGB16: return SC_IMAGE_RGB16;
    case QUIC_IMAGE_TYPE_RGB24: return SC_IMAGE_RGB24;
    case QUIC_IMAGE_TYPE_RGB32: return SC_IMAGE_RGB32;
    case QUIC_IMAGE_TYPE_RGBA:  return SC_IMAGE_RGBA;
    default:                    return SC_IMAGE_INVALID;
    }
}

int sc_quic_begin(sc_quic *q, const uint8_t *data, size_t len, int *w, int *h, sc_image_type *type)
{
    if (!q || !data || len < 24 || !sc_sizes(sc_le32(data+12), sc_le32(data+16)) || sc_le32(data+8)<1 || sc_le32(data+8)>5) return -1;
    SC_GUARD(env);
    int r = quic_decode_begin(q->ctx, (uint32_t *)(void *)(uintptr_t)data, (unsigned)(len / 4),
                              &q->type, &q->width, &q->height);
    SC_UNGUARD();
    if (r != QUIC_OK) return -1;
    *w = q->width; *h = q->height; *type = from_quic(q->type);
    return *type == SC_IMAGE_INVALID ? -1 : 0;
}

int sc_quic_decode(sc_quic *q, uint8_t *out, int stride)
{
    if (!q || !out) return -1;
    SC_GUARD(env);
    QuicImageType to = q->type == QUIC_IMAGE_TYPE_RGBA ? QUIC_IMAGE_TYPE_RGBA : QUIC_IMAGE_TYPE_RGB32;
    int r = quic_decode(q->ctx, to, out, stride);
    SC_UNGUARD();
    return r == QUIC_OK ? 0 : -1;
}

int sc_quic_encode_rgb32(const uint8_t *px, int w, int h, int stride, uint8_t *out, size_t cap)
{
    if (!px || !out || w <= 0 || h <= 0) return -1;
    sc_quic *q = sc_quic_create();
    if (!q) return -1;
    int words;
    {
        SC_GUARD(env);
        words = quic_encode(q->ctx, QUIC_IMAGE_TYPE_RGB32, w, h,
                            (uint8_t *)(uintptr_t)px, (unsigned)h, stride,
                            (uint32_t *)(void *)out, (unsigned)(cap / 4));
        SC_UNGUARD();
    }
    sc_quic_destroy(q);
    return words > 0 ? words * 4 : -1;
}

int sc_abi_version(void) { return SC_ABI_VERSION; }

/* ---- LZ ---- */

/* Palette images with padded rows are rare and mostly small (cursors, masks). The scratch is only needed when the
   caller's buffer can't hold the padded rows; a larger one is freed after use. */
#define SC_LZ_SCRATCH_KEEP ((size_t)4 << 20)

struct sc_lz {
    LzUsrContext usr;
    LzContext *ctx;
    LzImageType type;
    int width, height, n_pixels, top_down;
    struct { uint64_t unique; uint16_t num_ents; uint32_t ents[256]; } palette_storage;
    /* Palette rows decode padded to whole bytes; they are decoded here and copied out tightly, so the caller's
       buffer needs only width*height*4 bytes (review N4). Kept between calls up to SC_LZ_SCRATCH_KEEP bytes. */
    uint8_t *scratch;
    size_t scratch_cap;
};

SPICE_GNUC_NORETURN static void l_error(LzUsrContext *u, const char *fmt, ...) { (void)u; sc_fatal(fmt); abort(); }
static void l_warn(LzUsrContext *u, const char *fmt, ...) { (void)u; (void)fmt; }
static void l_info(LzUsrContext *u, const char *fmt, ...) { (void)u; (void)fmt; }
static void *l_malloc(LzUsrContext *u, int n) { (void)u; return malloc((size_t)n); }
static void l_free(LzUsrContext *u, void *p) { (void)u; free(p); }
static int l_more_space(LzUsrContext *u, uint8_t **io) { (void)u; (void)io; return 0; }
static int l_more_lines(LzUsrContext *u, uint8_t **lines) { (void)u; (void)lines; return 0; }

sc_lz *sc_lz_create(void)
{
    sc_lz *z = calloc(1, sizeof *z);
    if (!z) return NULL;
    z->usr.error = l_error; z->usr.warn = l_warn; z->usr.info = l_info;
    z->usr.malloc = l_malloc; z->usr.free = l_free;
    z->usr.more_space = l_more_space; z->usr.more_lines = l_more_lines;
    z->ctx = lz_create(&z->usr);
    if (!z->ctx) { free(z); return NULL; }
    return z;
}

void sc_lz_destroy(sc_lz *z) { if (z) { lz_destroy(z->ctx); free(z->scratch); free(z); } }

/* Frees a scratch above SC_LZ_SCRATCH_KEEP, after a decode that succeeded or failed. */
static void sc_lz_trim(sc_lz *z)
{
    if (z->scratch_cap > SC_LZ_SCRATCH_KEEP) { free(z->scratch); z->scratch = NULL; z->scratch_cap = 0; }
}

static sc_image_type from_lz(LzImageType t)
{
    switch (t) {
    case LZ_IMAGE_TYPE_PLT1_LE: return SC_IMAGE_PLT1_LE;
    case LZ_IMAGE_TYPE_PLT1_BE: return SC_IMAGE_PLT1_BE;
    case LZ_IMAGE_TYPE_PLT4_LE: return SC_IMAGE_PLT4_LE;
    case LZ_IMAGE_TYPE_PLT4_BE: return SC_IMAGE_PLT4_BE;
    case LZ_IMAGE_TYPE_PLT8:    return SC_IMAGE_PLT8;
    case LZ_IMAGE_TYPE_RGB16:   return SC_IMAGE_RGB16;
    case LZ_IMAGE_TYPE_RGB24:   return SC_IMAGE_RGB24;
    case LZ_IMAGE_TYPE_RGB32:   return SC_IMAGE_RGB32;
    case LZ_IMAGE_TYPE_RGBA:    return SC_IMAGE_RGBA;
    case LZ_IMAGE_TYPE_XXXA:    return SC_IMAGE_XXXA;
    default:                    return SC_IMAGE_INVALID;
    }
}

int sc_lz_begin(sc_lz *z, const uint8_t *data, size_t len, const uint32_t *palette, int palette_count,
                int *w, int *h, sc_image_type *type, int *top_down)
{
    if (!z || !data || len < 28) return -1;
    uint32_t t=sc_be32(data+8), w0=sc_be32(data+12), h0=sc_be32(data+16), stride0=sc_be32(data+20);
    if (t<1 || t>10 || !sc_sizes(w0,h0)) return -1;
    if (t<=5) {
        unsigned ppb = t<=2 ? 8 : t<=4 ? 2 : 1;
        if (stride0 != (w0+ppb-1)/ppb || (uint64_t)stride0*ppb*h0 > 67108864) return -1;
        int minimum=t<=2 ? 2 : 1;
        if (!palette || palette_count<minimum || palette_count>256) return -1;
    }
    SpicePalette *plt = NULL;
    if (palette && palette_count>0 && palette_count<=256) {
        plt=(SpicePalette *)&z->palette_storage;
        plt->num_ents=(uint16_t)palette_count;
        memcpy(plt->ents,palette,(size_t)palette_count*4);
    }

    jmp_buf env; jmp_buf *sc_prev = sc_fatal_env; sc_fatal_env = &env;
    if (setjmp(env)) { sc_fatal_env = sc_prev; return -1; }
    lz_decode_begin(z->ctx, (uint8_t *)(uintptr_t)data, (unsigned)len,
                    &z->type, &z->width, &z->height, &z->n_pixels, &z->top_down, plt);
    sc_fatal_env = sc_prev;


    *w = z->width; *h = z->height; *top_down = z->top_down;
    *type = from_lz(z->type);
    return *type == SC_IMAGE_INVALID ? -1 : 0;
}

int sc_lz_decode(sc_lz *z, uint8_t *out, size_t out_cap)
{
    if (!z || !out) return -1;
    /* Palette rows hold whole bytes, so a row decodes to n_pixels/height pixels, up to 7 more than width. */
    size_t row_px = z->height > 0 ? (size_t)z->n_pixels / (size_t)z->height : 0, row = (size_t)z->width * 4;
    if (out_cap < row * (size_t)z->height) return -1;
    int padded = z->type >= LZ_IMAGE_TYPE_PLT1_LE && z->type <= LZ_IMAGE_TYPE_PLT8 && z->height > 0 && row_px != (size_t)z->width;
    /* A caller buffer that holds the padded rows is used as the working space, so nothing is allocated. */
    int in_place = padded && out_cap >= (size_t)z->n_pixels * 4;
    uint8_t *dst = out;
    if (padded && !in_place) {
        size_t need = (size_t)z->n_pixels * 4;
        if (need > z->scratch_cap) {
            uint8_t *p = realloc(z->scratch, need);
            if (!p) return -1;
            z->scratch = p;
            z->scratch_cap = need;
        }
        dst = z->scratch;
    }
    jmp_buf env; jmp_buf *sc_prev = sc_fatal_env; sc_fatal_env = &env;
    if (setjmp(env)) { sc_fatal_env = sc_prev; sc_lz_trim(z); return -1; }
    /* RGBA and XXXA only round-trip to themselves (lz_decode errors on any other requested
       type for them); everything else upconverts to RGB32. */
    LzImageType to = z->type == LZ_IMAGE_TYPE_RGBA ? LZ_IMAGE_TYPE_RGBA
                    : z->type == LZ_IMAGE_TYPE_XXXA ? LZ_IMAGE_TYPE_XXXA
                    : LZ_IMAGE_TYPE_RGB32;
    lz_decode(z->ctx, to, dst);
    SC_UNGUARD();
    if (in_place) {
        /* Compact the rows: each moves down (row <= row_px * 4), so ascending order never overwrites unread rows. */
        for (int y = 1; y < z->height; y++) memmove(out + (size_t)y * row, out + (size_t)y * row_px * 4, row);
    } else if (padded) {
        for (int y = 0; y < z->height; y++) memcpy(out + (size_t)y * row, z->scratch + (size_t)y * row_px * 4, row);
        sc_lz_trim(z);
    }
    return 0;
}

int sc_lz_encode_rgb32(const uint8_t *px, int w, int h, int stride, uint8_t *out, size_t cap)
{
    if (!px || !out || w <= 0 || h <= 0) return -1;
    sc_lz *z = sc_lz_create();
    if (!z) return -1;
    int n;
    {
        SC_GUARD(env);
        n = lz_encode(z->ctx, LZ_IMAGE_TYPE_RGB32, w, h, 1 /* top_down */,
                      (uint8_t *)(uintptr_t)px, (unsigned)h, stride, out, (unsigned)cap);
        SC_UNGUARD();
    }
    sc_lz_destroy(z);
    return n > 0 ? n : -1;
}

/* Test-only: encodes the alpha plane of jpeg-alpha images, mirroring sc_lz_encode_rgb32. */
int sc_lz_encode_xxxa(const uint8_t *px, int w, int h, int stride, uint8_t *out, size_t cap)
{
    if (!px || !out || w <= 0 || h <= 0) return -1;
    sc_lz *z = sc_lz_create();
    if (!z) return -1;
    int n;
    {
        SC_GUARD(env);
        n = lz_encode(z->ctx, LZ_IMAGE_TYPE_XXXA, w, h, 1 /* top_down */,
                      (uint8_t *)(uintptr_t)px, (unsigned)h, stride, out, (unsigned)cap);
        SC_UNGUARD();
    }
    sc_lz_destroy(z);
    return n > 0 ? n : -1;
}

/* ---- GLZ ---- */

struct sc_glz_window { SpiceGlzDecoderWindow *w; };
struct sc_glz { SpiceGlzDecoder *d; sc_glz_window *win; };

sc_glz_window *sc_glz_window_create(void)
{
    sc_glz_window *w = calloc(1, sizeof *w);
    if (!w) return NULL;
    w->w = glz_decoder_window_new();
    if (!w->w) { free(w); return NULL; }
    return w;
}

void sc_glz_window_clear(sc_glz_window *w) { if (w) glz_decoder_window_clear(w->w); }
size_t sc_glz_window_trim(sc_glz_window *w) { return w ? sc_glz_window_trim_pool(w->w) : 0; }
void sc_glz_window_destroy(sc_glz_window *w) { if (w) { glz_decoder_window_destroy(w->w); free(w); } }

sc_glz *sc_glz_create(sc_glz_window *w)
{
    if (!w) return NULL;
    sc_glz *g = calloc(1, sizeof *g);
    if (!g) return NULL;
    g->win = w;
    g->d = glz_decoder_new(w->w);
    if (!g->d) { free(g); return NULL; }
    return g;
}

void sc_glz_destroy(sc_glz *g) { if (g) { glz_decoder_destroy(g->d); free(g); } }

int sc_glz_decode(sc_glz *g, const uint8_t *data, size_t len, const uint8_t **out,
                  int *w, int *h, int *stride, int *top_down)
{
    if (!g || !data || len < 4) return -1;
    if (len < 33 || (data[8]&15)<6 || (data[8]&15)>9 ||
        !sc_sizes(sc_be32(data+9), sc_be32(data+13))) return -1;
    sc_glz_input_end=data+len;
    jmp_buf env; jmp_buf *sc_prev=sc_fatal_env; sc_fatal_env=&env;
    if (setjmp(env)) { sc_fatal_env=sc_prev; sc_glz_abort_pending(); return -1; }
    g->d->ops->decode(g->d, (uint8_t *)(uintptr_t)data, NULL, NULL);
    SC_UNGUARD();

    struct glz_image *img = glz_decoder_window_last(g->win->w);
    if (!img) return -1;
    *out = sc_glz_image_data(img);
    *w = sc_glz_image_width(img);
    *h = sc_glz_image_height(img);
    *stride = sc_glz_image_stride(img);
    *top_down = sc_glz_image_top_down(img);
    return 0;
}

/* ---- LZ4 ---- */

/* Mirrors spice-common canvas_get_lz4 (canvas_base.c) after its 2-byte header: the records form one
   LZ4 stream decoded into contiguous output, so later blocks may reference earlier ones. lz4 never
   aborts, so no jmp_buf is needed; the stream state lives on the stack. */
int sc_lz4_decode(const uint8_t *src, size_t src_len, uint8_t *dst, size_t dst_cap)
{
    if (!src || !dst) return -1;
    if (dst_cap > INT_MAX) dst_cap = INT_MAX;
    LZ4_streamDecode_t stream;
    if (!LZ4_setStreamDecode(&stream, NULL, 0)) return -1;
    size_t pos = 0, total = 0;
    do {
        if (src_len - pos < 4) return -1;
        uint32_t n = sc_be32(src + pos);
        pos += 4;
        if (n == 0 || n > INT_MAX || n > src_len - pos) return -1;
        int d = LZ4_decompress_safe_continue(&stream, (const char *)(src + pos), (char *)(dst + total),
                                             (int)n, (int)(dst_cap - total));
        if (d <= 0) return -1;
        total += (size_t)d;
        pos += n;
    } while (pos < src_len);
    return (int)total;
}
