/* See glzenc.h. Not upstream code (MIT). */
#include "glzenc.h"
#include <stdlib.h>
#include <string.h>

typedef struct writer { uint8_t *p, *end; int overflow; } writer;
static void put8(writer *w, uint32_t v) { if (w->p < w->end) *w->p++ = (uint8_t)v; else w->overflow = 1; }
static void put32(writer *w, uint32_t v) { put8(w, v >> 24); put8(w, v >> 16); put8(w, v >> 8); put8(w, v); }

static uint32_t next(uint32_t *s) { *s ^= *s << 13; *s ^= *s >> 17; *s ^= *s << 5; return *s; }

/* Pass kinds: what one decoder template instance reads. */
enum { PASS_RGB32, PASS_RGB16, PASS_ALPHA };
static const uint32_t BIAS[] = { 0, 1, 2 };   /* match length bias of each template instance */

typedef struct enc {
    writer w;
    int pass;
    uint32_t n, width;
    uint64_t id;
    const glz_window_image *win;
    int win_count;
    uint32_t *cur;        /* this image's decoded pixels so far */
    uint32_t *lit;        /* what a literal at each position decodes to */
    uint32_t *tmp;        /* overlapping-copy evaluation */
    const uint8_t *alpha; /* PASS_ALPHA literal bytes */
    const uint8_t *raw;   /* PASS_RGB32: b, g, r bytes; PASS_RGB16: hi, lo */
    uint32_t seed;
    int style;
} enc;

static int same(const enc *e, uint32_t a, uint32_t b)
{
    return e->pass == PASS_ALPHA ? (a >> 24) == (b >> 24) : ((a ^ b) & 0xFFFFFF) == 0;
}

/* Length of a same-image match at distance d (d <= pos), evaluating overlapping copies as the decoder does. */
static uint32_t match_intra(enc *e, uint32_t pos, uint32_t d, uint32_t max)
{
    uint32_t k = 0;
    for (; k < max; k++) {
        uint32_t src = pos - d + k;
        uint32_t v = src < pos ? e->cur[src] : e->tmp[src - pos];
        if (!same(e, v, e->lit[pos + k])) break;
        e->tmp[k] = v;
    }
    return k;
}

static uint32_t match_cross(const enc *e, uint32_t pos, const glz_window_image *img, uint32_t ofs, uint32_t max)
{
    if (ofs >= img->pixels) return 0;
    if (max > img->pixels - ofs) max = img->pixels - ofs;
    uint32_t k = 0;
    while (k < max && same(e, img->px[ofs + k], e->lit[pos + k])) k++;
    return k;
}

static void emit_literals(enc *e, uint32_t pos, uint32_t count)
{
    while (count) {
        uint32_t c = count > 32 ? 32 : count;
        put8(&e->w, c - 1);
        for (uint32_t i = 0; i < c; i++, pos++) {
            if (e->pass == PASS_ALPHA) put8(&e->w, e->alpha[pos]);
            else if (e->pass == PASS_RGB16) { put8(&e->w, e->raw[pos * 2]); put8(&e->w, e->raw[pos * 2 + 1]); }
            else { put8(&e->w, e->raw[pos * 3]); put8(&e->w, e->raw[pos * 3 + 1]); put8(&e->w, e->raw[pos * 3 + 2]); }
            e->cur[pos] = e->pass == PASS_ALPHA ? (e->cur[pos] & 0xFFFFFF) | (e->lit[pos] & 0xFF000000) : e->lit[pos];
        }
        count -= c;
    }
}

/* dist 0: same image, d = pixel distance; otherwise ofs is the pixel offset in image id - dist. */
static void emit_ref(enc *e, uint32_t len, uint32_t dist, uint32_t ofs)
{
    uint32_t len_enc = len - BIAS[e->pass];
    uint32_t ctrl = (len_enc > 7 ? 7 : len_enc) << 5;
    int far = ofs >= 4096;
    put8(&e->w, ctrl | (far ? 0x10 : 0) | (ofs & 0xF));
    if (len_enc >= 7) {
        uint32_t rest = len_enc - 7;
        while (rest >= 255) { put8(&e->w, 255); rest -= 255; }
        put8(&e->w, rest);
    }
    put8(&e->w, (ofs >> 4) & 0xFF);
    if (!far) {
        int flag = dist < (1u << 6) ? 0 : dist < (1u << 14) ? 1 : dist < (1u << 22) ? 2 : 3;
        put8(&e->w, (uint32_t)flag << 6 | (dist & 0x3F));
        for (int i = 0; i < flag; i++) put8(&e->w, dist >> (6 + 8 * i));
    } else {
        int flag = dist == 0 ? 0 : dist < (1u << 8) ? 1 : dist < (1u << 16) ? 2 : 3;
        int very_far = ofs >= (1u << 17);
        put8(&e->w, (uint32_t)flag << 6 | (uint32_t)very_far << 5 | ((ofs >> 12) & 0x1F));
        for (int i = 0; i < flag; i++) put8(&e->w, dist >> (8 * i));
        if (very_far) put8(&e->w, ofs >> 17);
    }
}

static void encode_pass(enc *e)
{
    const uint32_t min = e->pass == PASS_ALPHA ? 3 : e->pass == PASS_RGB16 ? 2 : (e->style == GLZ_STYLE_RANDOM_LEN ? 1 : 2);
    uint32_t pos = 0, pending = 0;
    while (pos < e->n) {
        uint32_t best = 0, best_dist = 0, best_ofs = 0, max = e->n - pos;
        if (e->style != GLZ_STYLE_LITERALS) {
            uint32_t w = e->width;
            uint32_t intra[12] = { 1, 2, 3, 4, 7, w, w + 1, w > 1 ? w - 1 : 1, 2 * w, 4097, 131073, 0 };
            int ni = 11;
            if (e->style == GLZ_STYLE_FAR && pos > 1) intra[ni++] = 1 + next(&e->seed) % (pos < (1u << 25) ? pos : (1u << 25));
            for (int i = 0; i < ni; i++) {
                uint32_t d = intra[i];
                if (d == 0 || d > pos || d > (1u << 25)) continue;
                uint32_t len = match_intra(e, pos, d, max);
                /* Prefer far offsets in GLZ_STYLE_FAR so the long offset forms are covered. */
                if (len >= min && (len > best || (e->style == GLZ_STYLE_FAR && len == best && d > 4096))) {
                    best = len; best_dist = 0; best_ofs = d - 1;
                }
            }
            for (int k = 0; k < e->win_count; k++) {
                const glz_window_image *img = &e->win[e->win_count - 1 - k];
                if (k >= 4 || img->id >= e->id) break;
                uint64_t dist = e->id - img->id;
                uint32_t cands[5] = { pos, pos - 1, pos + 1, pos - w, pos + w };
                int nc = 5;
                if (e->style == GLZ_STYLE_FAR) cands[nc - 1] = next(&e->seed) % img->pixels;
                for (int c = 0; c < nc; c++) {
                    uint32_t ofs = cands[c];
                    if (ofs >= img->pixels || ofs >= (1u << 25)) continue;
                    if (dist >= (1u << 30) || (ofs >= 4096 && dist >= (1u << 24))) continue;
                    uint32_t len = match_cross(e, pos, img, ofs, max);
                    if (len >= min && (len > best || (e->style == GLZ_STYLE_FAR && len == best))) {
                        best = len; best_dist = (uint32_t)dist; best_ofs = ofs;
                    }
                }
            }
            if (best && e->style == GLZ_STYLE_RANDOM_LEN) best = min + next(&e->seed) % (best - min + 1);
        }
        if (!best) {
            /* A literal: later matches may already copy it, before emit_literals writes it out. */
            e->cur[pos] = e->pass == PASS_ALPHA ? (e->cur[pos] & 0xFFFFFF) | (e->lit[pos] & 0xFF000000) : e->lit[pos];
            pending++; pos++;
            continue;
        }
        if (pending) { emit_literals(e, pos - pending, pending); pending = 0; }
        emit_ref(e, best, best_dist, best_ofs);
        /* Apply the copy to cur exactly as the decoder will. */
        if (best_dist == 0) {
            uint32_t d = best_ofs + 1;
            for (uint32_t k = 0; k < best; k++) {
                uint32_t v = e->cur[pos + k - d];
                e->cur[pos + k] = e->pass == PASS_ALPHA ? (e->cur[pos + k] & 0xFFFFFF) | (v & 0xFF000000) : v;
            }
        } else {
            const glz_window_image *img = NULL;
            for (int k = 0; k < e->win_count; k++) if (e->win[k].id == e->id - best_dist) img = &e->win[k];
            for (uint32_t k = 0; k < best; k++) {
                uint32_t v = img->px[best_ofs + k];
                e->cur[pos + k] = e->pass == PASS_ALPHA ? (e->cur[pos + k] & 0xFFFFFF) | (v & 0xFF000000) : v;
            }
        }
        pos += best;
    }
    if (pending) emit_literals(e, pos - pending, pending);
}

size_t glzenc_encode(const glzenc_params *p, uint8_t *out, size_t cap, uint32_t *expected)
{
    uint32_t n = p->width * p->height;
    enc e = { .w = { out, out + cap, 0 }, .n = n, .width = p->width, .id = p->id, .win = p->window,
              .win_count = p->window ? p->window_count : 0, .seed = p->seed | 1, .style = p->style };
    e.cur = calloc(n, 4); e.lit = malloc((size_t)n * 4); e.tmp = malloc((size_t)n * 4);
    uint8_t *raw = malloc((size_t)n * 3), *alpha = malloc(n);
    e.raw = raw; e.alpha = alpha;

    put32(&e.w, 0x20205A4C); put32(&e.w, 0x00010001);
    put8(&e.w, (uint32_t)p->type | (p->top_down ? 0x10 : 0));
    put32(&e.w, p->width); put32(&e.w, p->height);
    put32(&e.w, p->width * (p->type == GLZ_RGB16 ? 2 : p->type == GLZ_RGB24 ? 3 : 4));
    put32(&e.w, (uint32_t)(p->id >> 32)); put32(&e.w, (uint32_t)p->id);
    put32(&e.w, p->win_head_dist);

    if (p->type == GLZ_RGB16) {
        const uint16_t *s = p->src;
        for (uint32_t i = 0; i < n; i++) {
            uint8_t hi = (uint8_t)(s[i] >> 8), lo = (uint8_t)s[i];
            raw[i * 2] = hi; raw[i * 2 + 1] = lo;
            /* decode-glz-tmpl.c COPY_COMP_PIXEL for LZ_RGB16 + TO_RGB32, in the same uint8_t arithmetic */
            uint8_t g = (uint8_t)(((hi << 6) | (lo >> 2)) & ~0x07);
            g |= (uint8_t)(g >> 5);
            uint8_t r = (uint8_t)(((hi << 1) & ~0x07) | ((hi >> 4) & 0x07));
            uint8_t b = (uint8_t)((lo << 3) | ((lo >> 2) & 0x07));
            e.lit[i] = b | (uint32_t)g << 8 | (uint32_t)r << 16;
        }
        e.pass = PASS_RGB16;
        encode_pass(&e);
    } else {
        const uint8_t *s = p->src;
        for (uint32_t i = 0; i < n; i++) {
            raw[i * 3] = s[i * 4]; raw[i * 3 + 1] = s[i * 4 + 1]; raw[i * 3 + 2] = s[i * 4 + 2];
            alpha[i] = s[i * 4 + 3];
            e.lit[i] = s[i * 4] | (uint32_t)s[i * 4 + 1] << 8 | (uint32_t)s[i * 4 + 2] << 16;
        }
        e.pass = PASS_RGB32;
        encode_pass(&e);
        if (p->type == GLZ_RGBA) {
            for (uint32_t i = 0; i < n; i++) e.lit[i] = (e.cur[i] & 0xFFFFFF) | (uint32_t)alpha[i] << 24;
            e.pass = PASS_ALPHA;
            encode_pass(&e);
        }
    }
    if (expected) memcpy(expected, e.cur, (size_t)n * 4);
    size_t written = e.w.overflow ? 0 : (size_t)(e.w.p - out);
    free(e.cur); free(e.lit); free(e.tmp); free(raw); free(alpha);
    return written;
}
