/*
   Copyright (C) 2010 Red Hat, Inc.

   This library is free software; you can redistribute it and/or
   modify it under the terms of the GNU Lesser General Public
   License as published by the Free Software Foundation; either
   version 2.1 of the License, or (at your option) any later version.

   This library is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
   Lesser General Public License for more details.

   You should have received a copy of the GNU Lesser General Public
   License along with this library; if not, see <http://www.gnu.org/licenses/>.
*/

/* MODIFIED FILE (LGPL-2.1 section 2b). Changed from upstream spice-gtk src/decode-glz.c (v0.42, commit f04479c1):
   SpiceSee, 2026: images are flat BGRA buffers instead of pixman surfaces; the window records the
     last decoded image, read through glz_decoder_window_last() and sc_glz_image_* accessors.
   SpiceSharp, 2026-10-29: checked compressed-byte reads, cleanup of allocations skipped by longjmp,
     allocation-failure checks, and bounds on references, image IDs and window growth.
   SpiceSharp, 2026-10-30: a 128 MiB budget on the images the window holds; clearing the window also
     resets its oldest ID; failed slot-array, window and decoder allocations are handled; retired images
     and their pixel buffers are reused instead of freed; the budget counts each buffer's capacity.
   Each change is marked SPICESEE/SPICESHARP MODIFICATION below or listed in native/VENDORED.md. */
#include "config.h"

#include <stdio.h>
#include <stdbool.h>
#include <inttypes.h>

#include <glib.h>

#include "gio-coroutine.h"
#include "spice-util.h"
#include "decode.h"

#include "common/canvas_utils.h"


/* SPICESHARP MODIFICATION: bound every compressed byte read by bridge's input length. */
extern _Thread_local const uint8_t *sc_glz_input_end;
static uint8_t sc_glz_read(uint8_t **p) {
    if (*p >= sc_glz_input_end) sc_fatal("truncated GLZ");
    return *(*p)++;
}

struct glz_image_hdr {
    uint64_t                id;
    LzImageType             type;
    uint32_t                width;
    uint32_t                height;
    uint32_t                gross_pixels;
    bool                    top_down;
    uint32_t                win_head_dist;
};

/* SPICESEE MODIFICATION: upstream backs each image with a pixman surface from
   alloc_lz_image_surface(). We have no pixman, and the decoder only ever needs a flat BGRA buffer,
   so the surface is replaced with a plain allocation. Upstream then walks img->data back to the
   allocation start for bottom-up images; ours starts there already. `stride` is recorded so the
   bridge can hand it to callers. See VENDORED.md. */
struct glz_image {
    struct glz_image_hdr    hdr;
    int                     stride;
    uint8_t                 *data;
    size_t                  cap;     /* SPICESHARP MODIFICATION: bytes allocated at data, for reuse */
};

static _Thread_local struct glz_image *sc_pending_image;

/* SPICESHARP MODIFICATION: the window keeps a few retired images for reuse (struct and pixel buffer), so
   warmed-up decoding of similar-sized images does not allocate. A reused buffer is not zeroed: a successful
   decode writes every pixel, and a failed one discards the image. */
static struct glz_image *sc_glz_pool_take(SpiceGlzDecoderWindow *w, size_t bytes);

static struct glz_image *glz_image_new(struct glz_image_hdr *hdr,
                                       int type, void *opaque)
{
    struct glz_image *img;
    size_t bytes = (size_t)hdr->gross_pixels * 4;

    g_return_val_if_fail(type == LZ_IMAGE_TYPE_RGB32 || type == LZ_IMAGE_TYPE_RGBA, NULL);

    img = sc_glz_pool_take(opaque, bytes);   /* SPICESHARP MODIFICATION: opaque is the window */
    if (!img) {
        img = g_new0(struct glz_image, 1);
        if (!img) sc_fatal("GLZ allocation failed");
        sc_pending_image=img;
        img->data = g_malloc0(bytes ? bytes : 1);
        if (!img->data) sc_fatal("GLZ allocation failed");
        img->cap = bytes;
    }
    sc_pending_image=img;
    img->hdr = *hdr;
    /* gross_pixels covers the padded rows PLT sources decode into, so it bounds the output. */
    img->stride = img->hdr.height ? (int)(img->hdr.gross_pixels / img->hdr.height) * 4
                                  : (int)img->hdr.width * 4;
    return img;
}

static void glz_image_destroy(struct glz_image *img)
{
    if (img == NULL)
        return;

    g_free(img->data);
    g_free(img);
}

/* ------------------------------------------------------------------ */

#define INIT_IMAGES_CAPACITY 100
#define WIN_OVERFLOW_FACTOR 1.5
#define WIN_REALLOC_FACTOR 1.5

/* SPICESHARP MODIFICATION: release allocations skipped by longjmp. */
void sc_glz_abort_pending(void) {
    if (sc_pending_image) { glz_image_destroy(sc_pending_image); sc_pending_image=NULL; }
}

/* SPICESHARP MODIFICATION: bounds for the window's memory (see VENDORED.md).
   SC_GLZ_WINDOW_BUDGET caps the decoded images the window holds. The client advertises an 8M-pixel
   dictionary (32 MiB, src/Spice.Session/SpiceSession.cs); the server keeps its window within that, and
   the client holds at most the previous window plus the image being decoded, 64 MiB. The budget allows
   twice that, counting each buffer's capacity. SC_GLZ_POOL_* bound the retired images kept for reuse:
   enough slots that the byte limit, not the count, decides what recurring update sizes stay warm. */
#define SC_GLZ_WINDOW_BUDGET ((uint64_t)128 << 20)
#define SC_GLZ_POOL_SLOTS 32
#define SC_GLZ_POOL_BYTES ((size_t)32 << 20)

struct SpiceGlzDecoderWindow {
    struct glz_image        **images;
    uint32_t                nimages;
    uint64_t                oldest;
    uint64_t                tail_gap;
    struct glz_image        *last;   /* SPICESEE MODIFICATION: see glz_decoder_window_last() */
    uint64_t                retained;                       /* SPICESHARP MODIFICATION: capacity of images[] */
    struct glz_image        *pool[SC_GLZ_POOL_SLOTS];      /* SPICESHARP MODIFICATION: retired, oldest first */
    int                     npool;
    size_t                  pool_bytes;
};

/* SPICESHARP MODIFICATION: image reuse and accounting. */
static uint64_t sc_glz_bytes(const struct glz_image *img) { return img->cap; }

static struct glz_image *sc_glz_pool_take(SpiceGlzDecoderWindow *w, size_t bytes)
{
    int best = -1;
    for (int i = 0; i < w->npool; i++) {
        size_t cap = w->pool[i]->cap;
        /* Smallest buffer that fits, wastes at most about half of itself and fits in the window budget. */
        if (cap >= bytes && cap - bytes <= bytes + 4096 && cap <= SC_GLZ_WINDOW_BUDGET - w->retained &&
            (best < 0 || cap < w->pool[best]->cap))
            best = i;
    }
    if (best < 0) return NULL;
    struct glz_image *img = w->pool[best];
    w->pool_bytes -= img->cap;
    memmove(&w->pool[best], &w->pool[best + 1], sizeof w->pool[0] * (size_t)(w->npool - best - 1));
    w->npool--;
    return img;
}

/* Removes an image from the window's accounting and keeps it for reuse, evicting the oldest pooled ones. */
static void sc_glz_retire(SpiceGlzDecoderWindow *w, struct glz_image *img)
{
    if (w->last == img) w->last = NULL;
    w->retained -= sc_glz_bytes(img);
    if (img->cap > SC_GLZ_POOL_BYTES) { glz_image_destroy(img); return; }
    while (w->npool && (w->npool == SC_GLZ_POOL_SLOTS || w->pool_bytes + img->cap > SC_GLZ_POOL_BYTES)) {
        w->pool_bytes -= w->pool[0]->cap;
        glz_image_destroy(w->pool[0]);
        memmove(&w->pool[0], &w->pool[1], sizeof w->pool[0] * (size_t)(w->npool - 1));
        w->npool--;
    }
    w->pool[w->npool++] = img;
    w->pool_bytes += img->cap;
}

/* SPICESEE MODIFICATION: upstream hands the decoded image back through the pixman surface it was
   given in usr_data. We pass no surface, so the most recently decoded image is recorded here and
   read by codec_bridge.c. See VENDORED.md. */
struct glz_image *glz_decoder_window_last(SpiceGlzDecoderWindow *w)
{
    return w ? w->last : NULL;
}

/* SPICESEE MODIFICATION: struct glz_image stays private to this file; the bridge reads it through
   these. See VENDORED.md. */
const uint8_t *sc_glz_image_data(const struct glz_image *img) { return img ? img->data : NULL; }
int sc_glz_image_width(const struct glz_image *img) { return img ? (int)img->hdr.width : 0; }
int sc_glz_image_height(const struct glz_image *img) { return img ? (int)img->hdr.height : 0; }
int sc_glz_image_stride(const struct glz_image *img) { return img ? img->stride : 0; }
int sc_glz_image_top_down(const struct glz_image *img) { return img ? (int)img->hdr.top_down : 1; }

static void glz_decoder_window_resize(SpiceGlzDecoderWindow *w)
{
    struct glz_image  **new_images;
    int i, new_slot;

    SPICE_DEBUG("%s: array resize %u -> %u", __FUNCTION__,
                w->nimages, w->nimages * 2);
    new_images = g_new0(struct glz_image*, w->nimages * 2);
    if (!new_images) sc_fatal("GLZ allocation failed");   /* SPICESHARP MODIFICATION */
    for (i = 0; i < w->nimages; i++) {
        if (w->images[i] == NULL) {
            /*
             * We can have empty slots when images come in out of order, this
             * can happen when a vm has multiple displays, since each display
             * uses its own socket there is no guarantee that images
             * originating from different displays are received in id order.
             */
            continue;
        }
        new_slot = w->images[i]->hdr.id % (w->nimages * 2);
        new_images[new_slot] = w->images[i];
    }
    g_free(w->images);
    w->images = new_images;
    w->nimages *= 2;
}

static void glz_decoder_window_add(SpiceGlzDecoderWindow *w,
                                   struct glz_image *img)
{
    int slot = img->hdr.id % w->nimages;

    /* SPICESHARP MODIFICATION: reject duplicates, bound hostile window growth. */
    if (img->hdr.id < w->oldest) sc_fatal("expired GLZ image id");
    while (w->images[slot]) {
        if (w->images[slot]->hdr.id==img->hdr.id || w->nimages>=65536) sc_fatal("invalid GLZ window id");
        /* need more space */
        glz_decoder_window_resize(w);
        slot = img->hdr.id % w->nimages;
    }

    w->images[slot] = img;
    w->last = img;   /* SPICESEE MODIFICATION */
    w->retained += sc_glz_bytes(img);   /* SPICESHARP MODIFICATION */

    /* close the gap */
    while (w->tail_gap <= img->hdr.id && w->images[w->tail_gap % w->nimages] != NULL &&
           w->images[w->tail_gap % w->nimages]->hdr.id == w->tail_gap)
        w->tail_gap++;
}

struct wait_for_image_data {
    SpiceGlzDecoderWindow     *window;
    uint64_t                   id;
};

static gboolean wait_for_image(gpointer data)
{
    struct wait_for_image_data *wait = data;
    int slot = wait->id % wait->window->nimages;
    struct glz_image *image = wait->window->images[slot];
    gboolean ready = image && image->hdr.id == wait->id;

    return ready;
}

static void *glz_decoder_window_bits(SpiceGlzDecoderWindow *w, uint64_t id,
                                     uint32_t dist, uint32_t offset, uint32_t length)
{
    struct wait_for_image_data data = {
        .window = w,
        .id = id - dist,
    };

    if (!g_coroutine_condition_wait(g_coroutine_self(), wait_for_image, &data))
        SPICE_DEBUG("wait for image cancelled");

    int slot = (id - dist) % w->nimages;

    g_return_val_if_fail(w->images[slot] != NULL, NULL);
    g_return_val_if_fail(w->images[slot]->hdr.id == id - dist, NULL);
    g_return_val_if_fail(offset <= w->images[slot]->hdr.gross_pixels && length <= w->images[slot]->hdr.gross_pixels-offset, NULL);

    return w->images[slot]->data + offset * 4;
}

static void glz_decoder_window_release(SpiceGlzDecoderWindow *w,
                                       uint64_t oldest)
{
    int slot;

    /* SPICESHARP: each slot only needs clearing once, even for hostile ID gaps. Retired images are kept for reuse. */
    if (oldest > w->oldest && oldest-w->oldest > (uint64_t)w->nimages) {
        for (uint32_t i=0;i<w->nimages;i++) { if (w->images[i] && w->images[i]->hdr.id<oldest) { if(w->last==w->images[i]) w->last=NULL; sc_glz_retire(w,w->images[i]); w->images[i]=NULL; } }
        w->oldest=oldest;
    }
    while (w->oldest < oldest) {
        slot = w->oldest % w->nimages;
        if (w->last == w->images[slot]) {
            w->last = NULL;   /* SPICESEE MODIFICATION: do not leave `last` dangling */
        }
        if (w->images[slot]) {   /* SPICESHARP MODIFICATION: kept for reuse, not freed */
            sc_glz_retire(w, w->images[slot]);
            w->images[slot] = NULL;
        }
        w->oldest++;
    }
}

/* ------------------------------------------------------------------ */

typedef struct GlibGlzDecoder {
    SpiceGlzDecoder         base;
    uint8_t                 *in_start;
    uint8_t                 *in_now;
    SpiceGlzDecoderWindow   *window;
    struct glz_image_hdr    image;
} GlibGlzDecoder;

/*
 * Give hints to the compiler for branch prediction optimization.
 */
#if defined(__GNUC__) && (__GNUC__ > 2)
#define LZ_EXPECT_CONDITIONAL(c) (__builtin_expect((c), 1))
#define LZ_UNEXPECT_CONDITIONAL(c) (__builtin_expect((c), 0))
#else
#define LZ_EXPECT_CONDITIONAL(c) (c)
#define LZ_UNEXPECT_CONDITIONAL(c) (c)
#endif


/*
 * the palette images will be treated as one byte pixels. Their width
 * should be transformed accordingly.
 */
typedef struct one_byte_pixel_t {
    uint8_t a;
} one_byte_pixel_t;

typedef struct rgb32_pixel_t {
    uint8_t b;
    uint8_t g;
    uint8_t r;
    uint8_t pad;
} rgb32_pixel_t;

typedef struct rgb24_pixel_t {
    uint8_t b;
    uint8_t g;
    uint8_t r;
} rgb24_pixel_t;

typedef uint16_t rgb16_pixel_t;


#define LZ_PLT
#define PLT8
#define TO_RGB32
#include "decode-glz-tmpl.c"

#define LZ_PLT
#define PLT4_BE
#define TO_RGB32
#include "decode-glz-tmpl.c"

#define LZ_PLT
#define PLT4_LE
#define TO_RGB32
#include "decode-glz-tmpl.c"

#define LZ_PLT
#define PLT1_BE
#define TO_RGB32
#include "decode-glz-tmpl.c"

#define LZ_PLT
#define PLT1_LE
#define TO_RGB32
#include "decode-glz-tmpl.c"


#define LZ_RGB16
#define TO_RGB32
#include "decode-glz-tmpl.c"

#define LZ_RGB32
#include "decode-glz-tmpl.c"

#define LZ_RGB_ALPHA
#include "decode-glz-tmpl.c"

#undef LZ_UNEXPECT_CONDITIONAL
#undef LZ_EXPECT_CONDITIONAL

typedef size_t (*decode_function)(SpiceGlzDecoderWindow *window,
                                  uint8_t* in_buf, uint8_t *out_buf, int size,
                                  uint64_t id, SpicePalette *plt);

// ordered according to LZ_IMAGE_TYPE
const decode_function DECODE_TO_RGB32[] = {
    NULL,
    glz_plt1_le_to_rgb32_decode,
    glz_plt1_be_to_rgb32_decode,
    glz_plt4_le_to_rgb32_decode,
    glz_plt4_be_to_rgb32_decode,
    glz_plt8_to_rgb32_decode,
    glz_rgb16_to_rgb32_decode,
    glz_rgb32_decode,
    glz_rgb32_decode,
    glz_rgb32_decode
};

static uint32_t decode_32(GlibGlzDecoder *d)
{
    uint32_t word = 0;
    word |= sc_glz_read(&d->in_now);
    word <<= 8;
    word |= sc_glz_read(&d->in_now);
    word <<= 8;
    word |= sc_glz_read(&d->in_now);
    word <<= 8;
    word |= sc_glz_read(&d->in_now);
    return word;
}

static uint64_t decode_64(GlibGlzDecoder *d)
{
    uint64_t long_word = decode_32(d);
    long_word <<= 32;
    long_word |= decode_32(d);
    return long_word;
}

static void decode_header(GlibGlzDecoder *d)
{
    uint32_t magic;
    uint32_t version;
    uint32_t stride;
    uint8_t tmp;

    magic = decode_32(d);
    g_return_if_fail(magic == LZ_MAGIC);

    version = decode_32(d);
    g_return_if_fail(version == LZ_VERSION);

    tmp = sc_glz_read(&d->in_now);

    d->image.type = (LzImageType)(tmp & LZ_IMAGE_TYPE_MASK);
    d->image.top_down = (tmp >> LZ_IMAGE_TYPE_LOG) ? true : false;
    d->image.width = decode_32(d);
    d->image.height = decode_32(d);
    stride = decode_32(d);

    if (IS_IMAGE_TYPE_PLT[d->image.type]) {
        d->image.gross_pixels = stride * PLT_PIXELS_PER_BYTE[d->image.type]
            * d->image.height;
    } else {
        d->image.gross_pixels = d->image.width * d->image.height;
    }

    d->image.id = decode_64(d);
    d->image.win_head_dist = decode_32(d);
    if (d->image.win_head_dist>d->image.id || d->image.id==UINT64_MAX) sc_fatal("invalid GLZ image id");

    SPICE_DEBUG("%s: %ux%u, id %" G_GUINT64_FORMAT ", ref %" G_GUINT64_FORMAT,
                __FUNCTION__,
                d->image.width, d->image.height, d->image.id,
                d->image.id - d->image.win_head_dist);
}

static void decode(SpiceGlzDecoder *decoder,
                   uint8_t *data, SpicePalette *palette,
                   void *usr_data)
{
    GlibGlzDecoder *d = SPICE_CONTAINEROF(decoder, GlibGlzDecoder, base);
    LzImageType decoded_type;
    struct glz_image *decoded_image;
    size_t n_in_bytes_decoded;

    d->in_start = data;
    d->in_now = data;

    decode_header(d);

    if (d->image.type == LZ_IMAGE_TYPE_RGBA) {
        decoded_type = LZ_IMAGE_TYPE_RGBA;
    } else {
        decoded_type = LZ_IMAGE_TYPE_RGB32;
    }

    /* SPICESHARP MODIFICATION: bound the memory the window holds (see SC_GLZ_WINDOW_BUDGET). */
    if ((uint64_t)d->image.gross_pixels * 4 > SC_GLZ_WINDOW_BUDGET - d->window->retained)
        sc_fatal("GLZ window budget exceeded");
    (void)usr_data;
    decoded_image = glz_image_new(&d->image, decoded_type, d->window);   /* SPICESHARP: for reuse */

    n_in_bytes_decoded = DECODE_TO_RGB32[d->image.type]
        (d->window, d->in_now, decoded_image->data,
         d->image.gross_pixels, d->image.id, palette);

    d->in_now += n_in_bytes_decoded;

    if (d->image.type == LZ_IMAGE_TYPE_RGBA) {
        glz_rgb_alpha_decode(d->window, d->in_now, decoded_image->data,
                             d->image.gross_pixels, d->image.id, palette);
    }

    glz_decoder_window_add(d->window, decoded_image);
    sc_pending_image=NULL;

    { /* release old images from last tail_gap, only if the gap is closed  */
        uint64_t oldest;
        struct glz_image *image = d->window->images[(d->window->tail_gap - 1) % d->window->nimages];

        g_return_if_fail(image != NULL);

        oldest = image->hdr.id - image->hdr.win_head_dist;
        glz_decoder_window_release(d->window, oldest);
    }
}

/* ------------------------------------------------------------------ */

static SpiceGlzDecoderOps glz_decoder_ops = {
    .decode = decode,
};

void glz_decoder_window_clear(SpiceGlzDecoderWindow *w)
{
    int i;
    struct glz_image **images;

    g_return_if_fail(w->nimages == 0 || w->images != NULL);

    for (i = 0; i < w->nimages; i++) {
        if (w->images[i]) {
            glz_image_destroy(w->images[i]);
        }
    }

    /* SPICESHARP MODIFICATION: pooled images go too, and a reset dictionary starts again at any ID,
       0 included: the oldest-ID floor is reset with the rest. */
    for (i = 0; i < w->npool; i++) {
        glz_image_destroy(w->pool[i]);
    }
    w->npool = 0;
    w->pool_bytes = 0;
    w->retained = 0;
    w->oldest = 0;

    w->last = NULL;   /* SPICESEE MODIFICATION: the images it pointed at are gone */
    /* SPICESHARP MODIFICATION: if the new slot array can't be allocated, keep the old one, emptied. */
    images = g_new0(struct glz_image*, 16);
    if (images) {
        g_free(w->images);
        w->images = images;
        w->nimages = 16;
    } else if (w->images) {
        memset(w->images, 0, sizeof(*w->images) * w->nimages);
    }
    w->tail_gap = 0;
}

SpiceGlzDecoderWindow *glz_decoder_window_new(void)
{
    SpiceGlzDecoderWindow *w = g_new0(SpiceGlzDecoderWindow, 1);
    if (!w) return NULL;   /* SPICESHARP MODIFICATION: allocation failures return NULL */
    glz_decoder_window_clear(w);
    if (!w->images) {
        g_free(w);
        return NULL;
    }
    return w;
}

void glz_decoder_window_destroy(SpiceGlzDecoderWindow *w)
{
    if (w == NULL)
        return;

    glz_decoder_window_clear(w);
    g_free(w->images);
    g_free(w);
}

SpiceGlzDecoder *glz_decoder_new(SpiceGlzDecoderWindow *w)
{
    GlibGlzDecoder *d = g_new0(GlibGlzDecoder, 1);
    if (!d) return NULL;   /* SPICESHARP MODIFICATION */
    d->base.ops = &glz_decoder_ops;
    d->window = w;
    return &d->base;
}

void glz_decoder_destroy(SpiceGlzDecoder *d)
{
    g_free(d);
}
