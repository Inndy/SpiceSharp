/* Differential test of two spicecodec.dll builds: a reference (normally the vendored sources at git HEAD) and the
   build under test. Every input goes through both; they must both fail, or both succeed with byte-identical
   output (return codes, dimensions, type, stride, every output byte including guard bytes around the buffer).
   The only allowed differences are the intentional fixes listed in INTENTIONAL[] below; each is also checked on
   its own, against the expected new behaviour. Not upstream code (MIT).

   usage: difftest <ref.dll> <new.dll> [--fixtures DIR] [--runs N] [--seed S] [--quick] [--verbose]

   Inputs: QUIC/LZ/GLZ images extracted from the replay fixtures (GLZ in stream order with a shared window);
   synthetic images of every type the vendored encoders support, in many sizes and content kinds; synthetic GLZ
   sequences from glzenc.c; and seeded mutations of all of them (--runs per input). Same seed, same inputs. */
#include "codecs.h"
#include "encoders.h"
#include "glzenc.h"
#include "spicerec.h"
#include <stdarg.h>
#include <string.h>

/* ---- intentional differences (docs/reviews/2026-10-30-native-codecs.md) ---- */

enum { DIFF_N1, DIFF_N2, DIFF_N4, DIFF_COUNT };
static const struct { const char *id, *what; } INTENTIONAL[DIFF_COUNT] = {
    { "N1", "GLZ: a window holding more than 128 MiB of decoded images rejects the next image; the reference "
            "kept every image" },
    { "N2", "GLZ: clearing the window also resets the oldest-ID floor, so a restarted dictionary may reuse ID 0; "
            "the reference rejected it" },
    { "N4", "LZ: palette images are written as width*height*4 bytes in rows of width*4; the reference wrote "
            "its padded rows (stride*pixels-per-byte pixels each)" },
};
/* Must match SC_GLZ_WINDOW_BUDGET in vendor/decode-glz.c. */
#define GLZ_BUDGET ((uint64_t)128 << 20)

/* ---- harness state ---- */

static dt_codec R, N;
static int verbose, quick, failures, printed;
static long intentional_seen[DIFF_COUNT];

enum { C_QUIC, C_LZ, C_GLZ, C_COUNT };
static const char *CODEC_NAME[C_COUNT] = { "QUIC", "LZ", "GLZ" };
typedef struct counts { long inputs, ok, fail, mismatch; } counts;
static counts base_real[C_COUNT], base_synth[C_COUNT], mutated[C_COUNT];

static void fail(const char *fmt, ...)
{
    failures++;
    if (printed++ >= 40) return;
    va_list ap; va_start(ap, fmt);
    fputs("MISMATCH: ", stdout); vprintf(fmt, ap); fputc('\n', stdout);
    va_end(ap);
}

typedef struct rng { uint64_t s; } rng;
static uint64_t rnd(rng *r)
{
    uint64_t z = (r->s += 0x9E3779B97F4A7C15ull);
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull; z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return z ^ (z >> 31);
}
static uint32_t below(rng *r, uint32_t n) { return n ? (uint32_t)(rnd(r) % n) : 0; }
static uint64_t seed_base = 1;
static rng rng_for(uint64_t a, uint64_t b) { rng r = { seed_base * 0x100000001B3ull ^ a * 0x9E3779B97F4A7C15ull ^ b }; rnd(&r); return r; }

/* Output buffers: one per build, refilled with the same sentinel before every decode. */
#define GUARD 64
#define SENTINEL 0xA5
static uint8_t *out_r, *out_n;
static size_t out_cap;
static void out_reserve(size_t n)
{
    if (n <= out_cap) return;
    free(out_r); free(out_n);
    out_cap = n + n / 4;
    out_r = malloc(out_cap); out_n = malloc(out_cap);
    if (!out_r || !out_n) { fprintf(stderr, "out of memory (%zu bytes)\n", out_cap); exit(2); }
}
static int untouched(const uint8_t *p, size_t n) { for (size_t i = 0; i < n; i++) if (p[i] != SENTINEL) return 0; return 1; }

/* ---- QUIC ---- */

static sc_quic *q_r, *q_n;
static sc_lz *l_r, *l_n;

/* Returns 1 if both succeeded, 0 if both failed, -1 on mismatch. stride_mode: 0 tight, 1 padded, 2 bottom-up. */
static int cmp_quic(const uint8_t *data, size_t len, int stride_mode, const char *label)
{
    int rw = 0, rh = 0, nw = 0, nh = 0;
    sc_image_type rt = 0, nt = 0;
    int rr = R.quic_begin(q_r, data, len, &rw, &rh, &rt);
    int nr = N.quic_begin(q_n, data, len, &nw, &nh, &nt);
    if ((rr == 0) != (nr == 0)) { fail("%s: quic_begin ref=%d new=%d", label, rr, nr); return -1; }
    if (rr) return 0;
    if (rw != nw || rh != nh || rt != nt) { fail("%s: quic_begin %dx%d/%d vs %dx%d/%d", label, rw, rh, rt, nw, nh, nt); return -1; }
    size_t row = (size_t)rw * 4, pitch = row + (stride_mode == 1 ? 12 : 0);
    size_t span = (size_t)(rh - 1) * pitch + row, total = span + 2 * GUARD;
    out_reserve(total);
    memset(out_r, SENTINEL, total); memset(out_n, SENTINEL, total);
    int stride = stride_mode == 2 ? -(int)pitch : (int)pitch;
    size_t start = GUARD + (stride_mode == 2 ? (size_t)(rh - 1) * pitch : 0);
    rr = R.quic_decode(q_r, out_r + start, stride);
    nr = N.quic_decode(q_n, out_n + start, stride);
    if (!untouched(out_n, GUARD) || !untouched(out_n + GUARD + span, GUARD)) { fail("%s: new QUIC wrote outside its output", label); return -1; }
    if ((rr == 0) != (nr == 0)) { fail("%s: quic_decode ref=%d new=%d", label, rr, nr); return -1; }
    if (rr) return 0;
    if (memcmp(out_r, out_n, total)) { fail("%s: QUIC output differs", label); return -1; }
    return 1;
}

/* ---- LZ ---- */

static int ppb_of(int sc_type) { return sc_type == 6 || sc_type == 7 ? 8 : sc_type == 8 || sc_type == 9 ? 2 : 1; }

static int cmp_lz(const uint8_t *data, size_t len, const uint32_t *pal, int npal, const char *label)
{
    int rw = 0, rh = 0, rtop = 0, nw = 0, nh = 0, ntop = 0;
    sc_image_type rt = 0, nt = 0;
    int rr = R.lz_begin(l_r, data, len, pal, npal, &rw, &rh, &rt, &rtop);
    int nr = N.lz_begin(l_n, data, len, pal, npal, &nw, &nh, &nt, &ntop);
    if ((rr == 0) != (nr == 0)) { fail("%s: lz_begin ref=%d new=%d", label, rr, nr); return -1; }
    if (rr) return 0;
    if (rw != nw || rh != nh || rt != nt || rtop != ntop) { fail("%s: lz_begin %dx%d/%d/%d vs %dx%d/%d/%d", label, rw, rh, rt, rtop, nw, nh, nt, ntop); return -1; }
    int plt = rt >= 6 && rt <= 10, ppb = ppb_of(rt);
    size_t row_px = plt ? ((size_t)rw + ppb - 1) / ppb * ppb : (size_t)rw;
    size_t tight = (size_t)rw * rh * 4, padded = row_px * rh * 4, region = padded > tight ? padded : tight;
    size_t total = region + 2 * GUARD;
    out_reserve(total);
    memset(out_r, SENTINEL, total); memset(out_n, SENTINEL, total);
    /* Tight capacity, so a version-2 reference doesn't decode in place (that leaves undefined bytes past the image);
       a version-1 reference ignores it and may write its padded rows, which out_r has room for. */
    rr = dt_lz_decode(&R, l_r, out_r + GUARD, tight);
    nr = dt_lz_decode(&N, l_n, out_n + GUARD, tight);
    /* Given width*height*4 bytes, the patched build writes no more (N4), whatever happens. */
    if (!untouched(out_n, GUARD) || !untouched(out_n + GUARD + tight, total - GUARD - tight)) { fail("%s: new LZ wrote outside width*height*4", label); return -1; }
    if ((rr == 0) != (nr == 0)) { fail("%s: lz_decode ref=%d new=%d", label, rr, nr); return -1; }
    if (rr) return 0;
    if (padded != tight) {
        /* Given room for the padded rows, it decodes in place: same visible pixels, nothing written past them. */
        uint8_t *in_place = malloc(total);
        if (!in_place) { fprintf(stderr, "out of memory\n"); exit(2); }
        memset(in_place, SENTINEL, total);
        int ir = N.lz_begin(l_n, data, len, pal, npal, &nw, &nh, &nt, &ntop) || dt_lz_decode(&N, l_n, in_place + GUARD, padded);
        int same = !ir && untouched(in_place, GUARD) && untouched(in_place + GUARD + padded, total - GUARD - padded) &&
                   !memcmp(in_place + GUARD, out_n + GUARD, tight);
        free(in_place);
        if (!same) { fail("%s: in-place LZ palette decode differs", label); return -1; }
    }
    if (padded != tight && !untouched(out_r + GUARD + tight, padded - tight)) {
        /* N4: the reference wrote padded rows. Compare the visible pixels row by row. */
        for (int y = 0; y < rh; y++)
            if (memcmp(out_r + GUARD + (size_t)y * row_px * 4, out_n + GUARD + (size_t)y * rw * 4, (size_t)rw * 4)) {
                fail("%s: LZ palette row %d differs", label, y); return -1;
            }
        intentional_seen[DIFF_N4]++;
        return 1;
    }
    if (memcmp(out_r, out_n, total)) { fail("%s: LZ output differs", label); return -1; }
    return 1;
}

/* ---- GLZ ---- */

typedef struct glz_pair { sc_glz_window *wr, *wn; sc_glz *dr, *dn; } glz_pair;
static void glz_open(glz_pair *g)
{
    g->wr = R.glz_window_create(); g->wn = N.glz_window_create();
    g->dr = R.glz_create(g->wr); g->dn = N.glz_create(g->wn);
    if (!g->wr || !g->wn || !g->dr || !g->dn) { fprintf(stderr, "GLZ create failed\n"); exit(2); }
}
static void glz_close(glz_pair *g)
{
    R.glz_destroy(g->dr); N.glz_destroy(g->dn);
    R.glz_window_destroy(g->wr); N.glz_window_destroy(g->wn);
}
static void glz_clear(glz_pair *g) { R.glz_window_clear(g->wr); N.glz_window_clear(g->wn); }

/* expected: if not NULL, what a correct decoder outputs (synthetic inputs); checked against the reference. */
static int cmp_glz(glz_pair *g, const uint8_t *data, size_t len, const uint32_t *expected, const char *label)
{
    const uint8_t *ro = NULL, *no = NULL;
    int rw = 0, rh = 0, rs = 0, rtop = 0, nw = 0, nh = 0, ns = 0, ntop = 0;
    int rr = R.glz_decode(g->dr, data, len, &ro, &rw, &rh, &rs, &rtop);
    int nr = N.glz_decode(g->dn, data, len, &no, &nw, &nh, &ns, &ntop);
    if ((rr == 0) != (nr == 0)) { fail("%s: glz_decode ref=%d new=%d", label, rr, nr); return -1; }
    if (rr) return 0;
    if (rw != nw || rh != nh || rs != ns || rtop != ntop) { fail("%s: glz_decode %dx%d/%d/%d vs %dx%d/%d/%d", label, rw, rh, rs, rtop, nw, nh, ns, ntop); return -1; }
    if (memcmp(ro, no, (size_t)rs * rh)) { fail("%s: GLZ output differs", label); return -1; }
    if (expected && memcmp(ro, expected, (size_t)rw * rh * 4)) { fail("%s: reference output is not what glzenc expected (test encoder bug)", label); return -1; }
    return 1;
}

/* ---- mutation ---- */

typedef struct field { int ofs, size, be; } field;
static const field QUIC_FIELDS[] = { { 8, 4, 0 }, { 12, 4, 0 }, { 16, 4, 0 }, { 0, 4, 0 }, { 4, 4, 0 } };
static const field LZ_FIELDS[] = { { 8, 4, 1 }, { 12, 4, 1 }, { 16, 4, 1 }, { 20, 4, 1 }, { 24, 4, 1 }, { 0, 4, 1 }, { 4, 4, 1 } };
static const field GLZ_FIELDS[] = { { 8, 1, 1 }, { 9, 4, 1 }, { 13, 4, 1 }, { 17, 4, 1 }, { 21, 4, 1 }, { 25, 4, 1 }, { 29, 4, 1 }, { 0, 4, 1 } };
static const int HEADER_LEN[C_COUNT] = { 20, 28, 33 };

static uint32_t get_field(const uint8_t *p, field f)
{
    uint32_t v = 0;
    for (int i = 0; i < f.size; i++) v = f.be ? v << 8 | p[f.ofs + i] : v | (uint32_t)p[f.ofs + i] << (8 * i);
    return v;
}
static void set_field(uint8_t *p, field f, uint32_t v)
{
    for (int i = 0; i < f.size; i++) p[f.ofs + i] = (uint8_t)(f.be ? v >> (8 * (f.size - 1 - i)) : v >> (8 * i));
}

/* dst must hold len + 1024 bytes. Returns the mutated length; desc names the operation. */
static size_t mutate1(rng *r, int codec, const uint8_t *src, size_t len, const uint8_t *other, size_t olen,
                      uint8_t *dst, char *desc, size_t desc_cap)
{
    memcpy(dst, src, len);
    size_t n = len;
    int op = (int)below(r, 9);
    switch (op) {
    case 0: { /* bit flips, half of them near the start */
        int k = 1 + (int)below(r, 8);
        for (int i = 0; i < k && n; i++) {
            size_t at = below(r, 2) ? below(r, (uint32_t)(n < 96 ? n : 96)) : below(r, (uint32_t)n);
            dst[at] ^= (uint8_t)(1u << below(r, 8));
        }
        snprintf(desc, desc_cap, "flip %d bits", k);
        break;
    }
    case 1: { /* random bytes */
        int k = 1 + (int)below(r, 4);
        for (int i = 0; i < k && n; i++) dst[below(r, (uint32_t)n)] = (uint8_t)rnd(r);
        snprintf(desc, desc_cap, "overwrite %d bytes", k);
        break;
    }
    case 2: /* truncation */
        n = below(r, 4) == 0 ? below(r, (uint32_t)(len < 40 ? len : 40)) : below(r, (uint32_t)len);
        snprintf(desc, desc_cap, "truncate to %zu of %zu", n, len);
        break;
    case 3: { /* trailing garbage */
        size_t k = 1 + below(r, 256);
        for (size_t i = 0; i < k; i++) dst[n + i] = (uint8_t)rnd(r);
        n += k;
        snprintf(desc, desc_cap, "append %zu bytes", k);
        break;
    }
    case 4: { /* a header field: inflated, deflated or arbitrary */
        const field *fs = codec == C_QUIC ? QUIC_FIELDS : codec == C_LZ ? LZ_FIELDS : GLZ_FIELDS;
        int nf = codec == C_QUIC ? 5 : codec == C_LZ ? 7 : 8;
        field f = fs[below(r, (uint32_t)nf)];
        if ((size_t)(f.ofs + f.size) > n) { snprintf(desc, desc_cap, "no-op"); break; }
        uint32_t v = get_field(dst, f), nv;
        switch (below(r, 11)) {
        case 0: nv = v + 1; break;
        case 1: nv = v - 1; break;
        case 2: nv = v * 2; break;
        case 3: nv = v / 2; break;
        case 4: nv = 0; break;
        case 5: nv = 0xFFFFFFFFu; break;
        case 6: nv = 0x7FFFFFFFu; break;
        case 7: nv = 0x80000000u; break;
        case 8: nv = (uint32_t)rnd(r); break;
        case 9: nv = below(r, 300); break;
        default: nv = v ^ (1u << below(r, 32)); break;
        }
        set_field(dst, f, nv);
        snprintf(desc, desc_cap, "header field @%d: %u -> %u", f.ofs, v, get_field(dst, f));
        break;
    }
    case 5: { /* another input's header on this body */
        size_t h = (size_t)HEADER_LEN[codec];
        if (other && olen >= h && n >= h) memcpy(dst, other, h);
        snprintf(desc, desc_cap, "swap header");
        break;
    }
    case 6: { /* delete a chunk */
        if (!n) break;
        size_t at = below(r, (uint32_t)n), k = 1 + below(r, 64);
        if (k > n - at) k = n - at;
        memmove(dst + at, dst + at + k, n - at - k);
        n -= k;
        snprintf(desc, desc_cap, "delete %zu at %zu", k, at);
        break;
    }
    case 7: { /* insert a chunk */
        size_t at = below(r, (uint32_t)n + 1), k = 1 + below(r, 64);
        int kind = (int)below(r, 3);
        memmove(dst + at + k, dst + at, n - at);
        for (size_t i = 0; i < k; i++) dst[at + i] = kind == 0 ? 0 : kind == 1 ? 0xFF : (uint8_t)rnd(r);
        n += k;
        snprintf(desc, desc_cap, "insert %zu at %zu", k, at);
        break;
    }
    default: { /* boundary byte values */
        static const uint8_t vals[] = { 0, 1, 0x7F, 0x80, 0xFE, 0xFF };
        if (n) dst[below(r, (uint32_t)n)] = vals[below(r, 6)];
        snprintf(desc, desc_cap, "boundary byte");
        break;
    }
    }
    return n;
}

static size_t mutate(rng *r, int codec, const uint8_t *src, size_t len, const uint8_t *other, size_t olen,
                     uint8_t *dst, char *desc, size_t desc_cap)
{
    size_t n = mutate1(r, codec, src, len, other, olen, dst, desc, desc_cap);
    if (below(r, 5) == 0) { /* stack a second one */
        uint8_t *tmp = malloc(n + 1024);
        char d2[96];
        memcpy(tmp, dst, n);
        n = mutate1(r, codec, tmp, n, other, olen, dst, d2, sizeof d2);
        size_t used = strlen(desc);
        snprintf(desc + used, desc_cap - used, " + %s", d2);
        free(tmp);
    }
    return n;
}

static void tally(counts *c, int result) { c->inputs++; if (result > 0) c->ok++; else if (result == 0) c->fail++; else c->mismatch++; }

static int runs = 16;
static uint8_t *last_input[C_COUNT];
static size_t last_len[C_COUNT];
static void remember(int codec, const uint8_t *data, size_t len)
{
    free(last_input[codec]);
    last_input[codec] = malloc(len ? len : 1);
    memcpy(last_input[codec], data, len);
    last_len[codec] = len;
}

/* One stand-alone (QUIC or LZ) input: compare it, then its mutations. */
static void run_single(int codec, const uint8_t *data, size_t len, const uint32_t *pal, int npal, int real,
                       uint64_t key, const char *label)
{
    char lab[256], desc[200];
    int res = codec == C_QUIC ? cmp_quic(data, len, 0, label) : cmp_lz(data, len, pal, npal, label);
    tally(real ? &base_real[codec] : &base_synth[codec], res);
    if (verbose && res == 0) printf("note: %s fails in both builds\n", label);
    uint8_t *m = malloc(len + 1024 + 4096);
    for (int k = 0; k < runs; k++) {
        rng r = rng_for(key, (uint64_t)k);
        size_t n = mutate(&r, codec, data, len, last_input[codec], last_len[codec], m, desc, sizeof desc);
        snprintf(lab, sizeof lab, "%s mutation %d (%s)", label, k, desc);
        int mr = codec == C_QUIC ? cmp_quic(m, n, (int)below(&r, 3), lab)
                                 : cmp_lz(m, n, pal, below(&r, 8) == 0 ? (int)below(&r, 257) : npal, lab);
        tally(&mutated[codec], mr);
    }
    free(m);
    remember(codec, data, len);
}

/* ---- GLZ sequences ---- */

typedef struct glz_step { uint8_t *data; size_t len; uint32_t *expected; int clear_before; } glz_step;
typedef struct glz_seq { glz_step *steps; int n, cap; char name[96]; int real; } glz_seq;

static void seq_add(glz_seq *s, const uint8_t *data, size_t len, uint32_t *expected, int clear_before)
{
    if (s->n == s->cap) { s->cap = s->cap ? s->cap * 2 : 16; s->steps = realloc(s->steps, sizeof(glz_step) * (size_t)s->cap); }
    glz_step *st = &s->steps[s->n++];
    st->data = malloc(len); memcpy(st->data, data, len); st->len = len;
    st->expected = expected; st->clear_before = clear_before;
}
static void seq_free(glz_seq *s)
{
    for (int i = 0; i < s->n; i++) { free(s->steps[i].data); free(s->steps[i].expected); }
    free(s->steps);
    memset(s, 0, sizeof *s);
}

/* The unmutated sequence, then `runs` passes in which some images are replaced by mutations. Every step of
   every pass is compared: a mutation that one build accepts and the other rejects also shows up as a
   difference in the images after it. */
static void run_glz_seq(glz_seq *s, uint64_t key)
{
    char lab[256], desc[200];
    glz_pair g;
    glz_open(&g);
    for (int i = 0; i < s->n; i++) {
        if (s->steps[i].clear_before) glz_clear(&g);
        snprintf(lab, sizeof lab, "%s image %d", s->name, i);
        tally(s->real ? &base_real[C_GLZ] : &base_synth[C_GLZ], cmp_glz(&g, s->steps[i].data, s->steps[i].len, s->steps[i].expected, lab));
    }
    glz_close(&g);
    size_t maxlen = 0;
    for (int i = 0; i < s->n; i++) if (s->steps[i].len > maxlen) maxlen = s->steps[i].len;
    uint8_t *m = malloc(maxlen + 1024 + 4096);
    for (int k = 0; k < runs; k++) {
        rng r = rng_for(key, (uint64_t)k);
        /* Long real sequences: about one image in 40; short synthetic ones: one image per pass. */
        int one = s->n < 40 ? (int)below(&r, (uint32_t)s->n) : -1;
        glz_open(&g);
        for (int i = 0; i < s->n; i++) {
            glz_step *st = &s->steps[i];
            if (st->clear_before) glz_clear(&g);
            int mutate_this = one >= 0 ? i == one : below(&r, 40) == 0;
            if (!mutate_this) {
                snprintf(lab, sizeof lab, "%s pass %d image %d", s->name, k, i);
                int res = cmp_glz(&g, st->data, st->len, NULL, lab);
                if (res < 0) mutated[C_GLZ].mismatch++;
                continue;
            }
            const glz_step *other = &s->steps[below(&r, (uint32_t)s->n)];
            size_t n = mutate(&r, C_GLZ, st->data, st->len, other->data, other->len, m, desc, sizeof desc);
            snprintf(lab, sizeof lab, "%s pass %d image %d mutated (%s)", s->name, k, i, desc);
            tally(&mutated[C_GLZ], cmp_glz(&g, m, n, NULL, lab));
        }
        glz_close(&g);
    }
    free(m);
}

/* ---- synthetic content ---- */

enum { K_NOISE, K_GRADIENT, K_FLAT, K_UI, K_STRIPES, K_SPARSE, K_DITHER, K_ROWREPEAT, K_COUNT };
static const char *KIND_NAME[K_COUNT] = { "noise", "gradient", "flat", "ui", "stripes", "sparse", "dither", "rowrepeat" };

/* BGRA pixels; variant shifts the content so GLZ sequences have related but different images. */
static void make_content(uint8_t *px, int w, int h, int kind, uint32_t seed, int variant)
{
    rng r = rng_for(0xC0FFEEu ^ (uint64_t)kind, seed);
    uint32_t period = 2 + seed % 8;
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
        uint8_t *p = px + ((size_t)y * w + x) * 4;
        int sx = x + variant * 3, sy = y + variant;
        uint32_t v;
        switch (kind) {
        case K_NOISE: v = (uint32_t)rnd(&r); break;
        case K_GRADIENT: v = (uint32_t)(sx * 255 / (w > 1 ? w : 1)) | (uint32_t)(sy * 255 / (h > 1 ? h : 1)) << 8 | (uint32_t)((sx + sy) & 255) << 16 | (uint32_t)(255 - (sx & 255)) << 24; break;
        case K_FLAT: v = 0xFF336699u ^ (uint32_t)variant; break;
        case K_UI: {
            int title = (sy % 120) < 22, border = (sx % 200) < 2;
            int text = !title && (sy % 18) < 11 && (sx % 7) < 5 && ((sx / 7 + sy / 18) % 5) != 0;
            v = border ? 0xFF404040u : title ? 0xFF2050C0u : text ? ((rnd(&r) & 3) ? 0xFF000000u : 0xFF303030u) : 0xFFF0F0F0u;
            break;
        }
        case K_STRIPES: { static const uint32_t pal[] = { 0xFF000000u, 0xFFFFFFFFu, 0x80FF0000u, 0xFF00FF00u, 0x400000FFu, 0xFF808080u, 0xFF123456u, 0x00654321u, 0xFFABCDEFu };
            v = pal[(uint32_t)(sx + (sy / 3)) % period]; break; }
        case K_SPARSE: v = (rnd(&r) % 97) == 0 ? (uint32_t)rnd(&r) : 0xFFEEEEEEu; break;
        case K_DITHER: v = ((sx ^ sy) & 1) ? 0xFF102030u : 0xFFF0E0D0u; break;
        default: { rng rr = rng_for(seed, (uint64_t)(sy % 5) * 100003u + (uint64_t)sx); v = (uint32_t)rnd(&rr); break; }
        }
        p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24);
    }
}

typedef struct size2 { int w, h; } size2;
static const size2 SIZES[] = {
    { 1, 1 }, { 1, 2 }, { 2, 1 }, { 3, 3 }, { 7, 5 }, { 8, 8 }, { 9, 1 }, { 13, 7 }, { 16, 16 }, { 17, 3 },
    { 31, 9 }, { 33, 33 }, { 63, 2 }, { 64, 64 }, { 65, 17 }, { 127, 1 }, { 1, 127 }, { 100, 75 }, { 255, 3 },
    { 257, 5 }, { 320, 200 }, { 4100, 3 },
};
static const size2 BIG[] = { { 1920, 1080 }, { 1366, 769 } };
#define NSIZES ((int)(sizeof SIZES / sizeof SIZES[0]))

static uint8_t *enc_buf;
static size_t enc_cap;
static void enc_reserve(size_t n) { if (n > enc_cap) { free(enc_buf); enc_cap = n; enc_buf = malloc(n); } }

static void synth_quic(void)
{
    static const int types[] = { Q_GRAY, Q_RGB16, Q_RGB24, Q_RGB32, Q_RGBA };
    static const char *tname[] = { "GRAY", "RGB16", "RGB24", "RGB32", "RGBA" };
    char label[160];
    int nsizes = quick ? 12 : NSIZES;
    for (int t = 0; t < 5; t++) for (int si = 0; si < nsizes + 2; si++) for (int k = 0; k < K_COUNT; k++) {
        size2 sz = si < nsizes ? SIZES[si] : BIG[si - nsizes];
        if (si >= nsizes && (quick || (k != K_UI && k != K_NOISE) || (types[t] != Q_RGB32 && types[t] != Q_RGBA))) continue;
        size_t npx = (size_t)sz.w * sz.h;
        uint8_t *px = malloc(npx * 4), *lines = malloc(npx * 4);
        make_content(px, sz.w, sz.h, k, (uint32_t)(t * 1000 + si * 10 + k), 0);
        int bpp = types[t] == Q_GRAY ? 1 : types[t] == Q_RGB16 ? 2 : types[t] == Q_RGB24 ? 3 : 4;
        for (size_t i = 0; i < npx; i++) {
            uint8_t *s = px + i * 4;
            if (bpp == 1) lines[i] = s[1];
            else if (bpp == 2) { uint16_t v = (uint16_t)((s[2] >> 3) << 10 | (s[1] >> 3) << 5 | s[0] >> 3); memcpy(lines + i * 2, &v, 2); }
            else if (bpp == 3) memcpy(lines + i * 3, s, 3);
            else memcpy(lines + i * 4, s, 4);
        }
        enc_reserve(npx * 8 + 4096);
        int n = enc_quic(types[t], sz.w, sz.h, lines, sz.w * bpp, enc_buf, enc_cap);
        snprintf(label, sizeof label, "synthetic QUIC %s %dx%d %s", tname[t], sz.w, sz.h, KIND_NAME[k]);
        if (n > 0) run_single(C_QUIC, enc_buf, (size_t)n, NULL, 0, 0, (uint64_t)C_QUIC << 56 | (uint64_t)t << 40 | (uint64_t)si << 20 | (uint64_t)k, label);
        else if (verbose) printf("note: encoder rejected %s\n", label);
        free(px); free(lines);
    }
}

static void synth_lz(void)
{
    static const char *tname[] = { "", "PLT1_LE", "PLT1_BE", "PLT4_LE", "PLT4_BE", "PLT8", "RGB16", "RGB24", "RGB32", "RGBA", "XXXA", "A8" };
    char label[160];
    int nsizes = quick ? 12 : NSIZES;
    for (int t = L_PLT1_LE; t <= L_A8; t++) for (int si = 0; si < nsizes + 2; si++) for (int k = 0; k < K_COUNT; k++) {
        size2 sz = si < nsizes ? SIZES[si] : BIG[si - nsizes];
        if (si >= nsizes && (quick || (k != K_UI && k != K_STRIPES) || (t != L_RGB32 && t != L_RGBA && t != L_PLT8))) continue;
        size_t npx = (size_t)sz.w * sz.h;
        uint8_t *px = malloc(npx * 4);
        make_content(px, sz.w, sz.h, k, (uint32_t)(t * 1000 + si * 10 + k), 0);
        int ppb = t <= L_PLT1_BE ? 8 : t <= L_PLT4_BE ? 2 : 1;
        int plt = t <= L_PLT8;
        int ncolors = t <= L_PLT1_BE ? 2 : t <= L_PLT4_BE ? 16 : 256;
        int stride = plt ? (sz.w + ppb - 1) / ppb : sz.w * (t == L_RGB16 ? 2 : t == L_RGB24 ? 3 : t == L_A8 ? 1 : 4);
        uint8_t *lines = calloc((size_t)stride * sz.h + 16, 1);
        rng pr = rng_for(0x9A1u, (uint64_t)(t * 7919 + si * 31 + k));
        uint32_t pal[256];
        for (int i = 0; i < 256; i++) pal[i] = (uint32_t)rnd(&pr) & 0xFFFFFF;
        int npal = ncolors;
        /* PLT8 palettes: full, exactly enough, or short (the out-of-range indices must fail in both). */
        if (t == L_PLT8) npal = k % 3 == 0 ? 256 : k % 3 == 1 ? 64 : 200;
        for (int y = 0; y < sz.h; y++) for (int x = 0; x < sz.w; x++) {
            const uint8_t *s = px + ((size_t)y * sz.w + x) * 4;
            uint8_t *row = lines + (size_t)y * stride;
            unsigned idx = (unsigned)(s[0] + s[1] * 3 + s[2] * 7) % (unsigned)(t == L_PLT8 && k % 3 == 1 ? 64 : ncolors);
            switch (t) {
            case L_PLT1_LE: row[x / 8] |= (uint8_t)((idx & 1) << (x % 8)); break;
            case L_PLT1_BE: row[x / 8] |= (uint8_t)((idx & 1) << (7 - x % 8)); break;
            case L_PLT4_LE: row[x / 2] |= (uint8_t)((idx & 15) << (x % 2 ? 4 : 0)); break;
            case L_PLT4_BE: row[x / 2] |= (uint8_t)((idx & 15) << (x % 2 ? 0 : 4)); break;
            case L_PLT8: row[x] = (uint8_t)idx; break;
            case L_RGB16: { uint16_t v = (uint16_t)((s[2] >> 3) << 10 | (s[1] >> 3) << 5 | s[0] >> 3); memcpy(row + x * 2, &v, 2); break; }
            case L_RGB24: memcpy(row + x * 3, s, 3); break;
            case L_A8: row[x] = s[3]; break;
            default: memcpy(row + x * 4, s, 4); break;
            }
        }
        /* Padding bits of the last byte of palette rows are encoded too; set some so they show up. */
        if (plt && (sz.w % ppb) && k % 2) for (int y = 0; y < sz.h; y++) {
            uint8_t *last = lines + (size_t)y * stride + stride - 1;
            if (t == L_PLT1_LE) *last |= (uint8_t)(0xFF << (sz.w % 8));
            else if (t == L_PLT1_BE) *last |= (uint8_t)(0xFF >> (sz.w % 8));
            else if (t == L_PLT4_LE) *last |= 0xF0;
            else if (t == L_PLT4_BE) *last |= 0x0F;
        }
        enc_reserve(npx * 8 + 4096);
        int n = enc_lz(t, sz.w, sz.h, (si + k) % 2, lines, stride, enc_buf, enc_cap);
        snprintf(label, sizeof label, "synthetic LZ %s %dx%d %s", tname[t], sz.w, sz.h, KIND_NAME[k]);
        if (n > 0) run_single(C_LZ, enc_buf, (size_t)n, pal, plt ? npal : (k % 4 == 0 ? 2 : 0), 0, (uint64_t)C_LZ << 56 | (uint64_t)t << 40 | (uint64_t)si << 20 | (uint64_t)k, label);
        else if (verbose) printf("note: encoder rejected %s\n", label);
        free(px); free(lines);
    }
}

/* Mirrors the decoder window's bookkeeping (decode-glz.c: tail_gap, release floor) for one synthetic sequence,
   so the encoder refers only to images the decoder still holds. IDs start at 0, as each SPICE dictionary does:
   after a clear the window expects ID 0 first. */
typedef struct sim { uint64_t ids[16]; uint32_t whd[16]; int present[16], n; uint64_t tail, oldest; } sim;
static void sim_add(sim *s, uint64_t id, uint32_t whd)
{
    s->ids[s->n] = id; s->whd[s->n] = whd; s->present[s->n] = 1; s->n++;
    for (;;) {
        int found = 0;
        for (int i = 0; i < s->n; i++) if (s->present[i] && s->ids[i] == s->tail) found = 1;
        if (!found) break;
        s->tail++;
    }
    for (int i = 0; i < s->n; i++)
        if (s->present[i] && s->ids[i] == s->tail - 1 && s->ids[i] - s->whd[i] > s->oldest) s->oldest = s->ids[i] - s->whd[i];
    for (int i = 0; i < s->n; i++) if (s->ids[i] < s->oldest) s->present[i] = 0;
}

/* Builds and runs synthetic GLZ sequences: per type, size and content, six related images in one window. */
static void synth_glz(void)
{
    static const int types[] = { GLZ_RGB16, GLZ_RGB24, GLZ_RGB32, GLZ_RGBA, 0 /* mixed */ };
    static const char *tname[] = { "RGB16", "RGB24", "RGB32", "RGBA", "mixed" };
    static const size2 gsizes[] = { { 1, 1 }, { 3, 2 }, { 9, 7 }, { 17, 1 }, { 64, 64 }, { 65, 33 }, { 257, 9 },
                                    { 4100, 4 }, { 300, 470 }, { 1920, 1080 } };
    int ng = quick ? 7 : (int)(sizeof gsizes / sizeof gsizes[0]);
    static const int heads[] = { 0, 1, 3, -1 /* whd = id: keep everything */ };
    static const int mixed[] = { GLZ_RGB32, GLZ_RGBA, GLZ_RGB16, GLZ_RGB24, GLZ_RGB32, GLZ_RGBA };
    /* Out of order: images 2 and 3 swap places on the wire, as with several displays sharing one window. */
    static const int order_io[] = { 0, 1, 3, 2, 4, 5 };
    enum { LEN = 6 };
    for (int t = 0; t < 5; t++) for (int si = 0; si < ng; si++) for (int k = 0; k < K_COUNT; k++) {
        size2 sz = gsizes[si];
        if (sz.w * sz.h >= 1000000 && k != K_UI && k != K_NOISE) continue;
        int head = heads[(t + si + k) % 4];
        int style = (si + k + t) % GLZ_STYLE_COUNT;
        int out_of_order = (si + k) % 5 == 4;
        glz_seq s = { 0 };
        snprintf(s.name, sizeof s.name, "synthetic GLZ %s %dx%d %s whd=%d style=%d%s", tname[t], sz.w, sz.h,
                 KIND_NAME[k], head, style, out_of_order ? " out-of-order" : "");
        size_t npx = (size_t)sz.w * sz.h, cap = npx * 8 + 4096;
        uint8_t *px = malloc(npx * 4), *buf = malloc(cap);
        uint16_t *px16 = malloc(npx * 2);
        glz_window_image win[LEN];
        sim sm = { 0 };
        for (int j = 0; j < LEN; j++) {
            int idx = out_of_order ? order_io[j] : j;
            int type = types[t] ? types[t] : mixed[idx];
            make_content(px, sz.w, sz.h, k, (uint32_t)(si * 10 + k), idx / 2);
            for (size_t i = 0; i < npx; i++) px16[i] = (uint16_t)((px[i * 4 + 2] >> 3) << 10 | (px[i * 4 + 1] >> 3) << 5 | px[i * 4] >> 3);
            uint64_t id = (uint64_t)idx;
            uint32_t whd = head < 0 ? (uint32_t)idx : (uint32_t)(idx < head ? idx : head);
            glz_window_image usable[LEN];
            int nu = 0;
            for (int i = 0; i < sm.n; i++) if (sm.present[i] && sm.ids[i] < id) usable[nu++] = win[i];
            uint32_t *expected = malloc(npx * 4);
            glzenc_params p = { type, (j + k) % 2, (uint32_t)sz.w, (uint32_t)sz.h, id, whd,
                                type == GLZ_RGB16 ? (const void *)px16 : (const void *)px, usable, nu,
                                (uint32_t)(si * 131 + k * 17 + j), style };
            size_t n = glzenc_encode(&p, buf, cap, expected);
            if (!n) { fprintf(stderr, "glzenc: buffer too small\n"); exit(2); }
            seq_add(&s, buf, n, expected, 0);
            win[sm.n] = (glz_window_image){ id, (uint32_t)npx, expected };
            sim_add(&sm, id, whd);
        }
        run_glz_seq(&s, (uint64_t)C_GLZ << 56 | (uint64_t)t << 40 | (uint64_t)si << 20 | (uint64_t)k);
        seq_free(&s);
        free(px); free(px16); free(buf);
    }

    /* Clears inside one window. Nothing is released before a clear (whd = id), so the restarted dictionary's
       ID 0 is valid in both builds; the clear-after-release case is N2, checked on its own. */
    {
        glz_seq s = { 0 };
        snprintf(s.name, sizeof s.name, "synthetic GLZ with clears");
        size2 sz = { 40, 30 };
        size_t npx = (size_t)sz.w * sz.h, cap = npx * 8 + 4096;
        uint8_t *px = malloc(npx * 4), *buf = malloc(cap);
        glz_window_image win[8];
        int nwin = 0;
        for (int j = 0; j < 8; j++) {
            int clear = j == 3 || j == 6;
            if (clear) nwin = 0;
            make_content(px, sz.w, sz.h, K_UI, 77, j);
            uint64_t id = (uint64_t)nwin;
            uint32_t *expected = malloc(npx * 4);
            glzenc_params p = { j % 2 ? GLZ_RGBA : GLZ_RGB32, 1, (uint32_t)sz.w, (uint32_t)sz.h, id, (uint32_t)id, px,
                                win, nwin, (uint32_t)j, GLZ_STYLE_NORMAL };
            size_t n = glzenc_encode(&p, buf, cap, expected);
            seq_add(&s, buf, n, expected, clear);
            win[nwin++] = (glz_window_image){ id, (uint32_t)npx, expected };
        }
        run_glz_seq(&s, 0xC1EA4ull);
        seq_free(&s);
        free(px); free(buf);
    }
}

/* ---- intentional differences: the new behaviour, checked directly ---- */

static int check(int ok, const char *what)
{
    printf("  %s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) failures++;
    return ok;
}

/* A width x height RGB32 GLZ image: one literal, then one run to the end. */
static size_t glz_flat(uint8_t *out, uint32_t w, uint32_t h, uint64_t id, uint32_t whd)
{
    uint8_t px[4] = { 10, 20, 30, 0 };
    uint32_t *flat = malloc((size_t)w * h * 4);
    for (size_t i = 0; i < (size_t)w * h; i++) memcpy(&flat[i], px, 4);
    glzenc_params p = { GLZ_RGB32, 1, w, h, id, whd, flat, NULL, 0, 1, GLZ_STYLE_NORMAL };
    size_t n = glzenc_encode(&p, out, 16384, NULL);
    free(flat);
    return n;
}

static int glz_one(sc_glz *dec, const dt_codec *c, const uint8_t *data, size_t len)
{
    const uint8_t *o; int w, h, s, t;
    return c->glz_decode(dec, data, len, &o, &w, &h, &s, &t);
}

static void check_intentional(void)
{
    static uint8_t buf[16384];
    glz_pair g;

    printf("N1: GLZ window budget\n");
    glz_open(&g);
    int accepted_new = 0, accepted_ref = 0;
    const uint32_t W = 1024, H = 1024;  /* 4 MiB each */
    int limit = (int)(GLZ_BUDGET / ((uint64_t)W * H * 4));
    for (int i = 0; i < limit + 4; i++) {
        size_t n = glz_flat(buf, W, H, (uint64_t)i, (uint32_t)i);  /* whd = id: nothing is ever released */
        if (glz_one(g.dr, &R, buf, n) == 0) accepted_ref++;
        if (glz_one(g.dn, &N, buf, n) == 0) accepted_new++;
    }
    check(accepted_new == limit, "new build keeps exactly 128 MiB of images when none is released");
    printf("  reference accepted %d of %d (%s)\n", accepted_ref, limit + 4, accepted_ref == limit + 4 ? "old behaviour" : accepted_ref == limit ? "same as new" : "unexpected");
    if (accepted_ref != limit) intentional_seen[DIFF_N1]++;
    N.glz_window_clear(g.wn);
    size_t n = glz_flat(buf, W, H, 0, 0);
    check(glz_one(g.dn, &N, buf, n) == 0, "after a clear the window accepts images again");
    glz_close(&g);
    glz_open(&g);
    int steady = 1;
    for (int i = 0; i < 200 && steady; i++) {  /* 800 MiB decoded in total, at most two images held */
        n = glz_flat(buf, W, H, (uint64_t)i, i ? 1u : 0u);
        steady = glz_one(g.dn, &N, buf, n) == 0;
    }
    check(steady, "the budget counts only images still held: 200 x 4 MiB with whd=1 all decode");
    glz_close(&g);

    printf("N2: GLZ clear resets the oldest-ID floor\n");
    glz_open(&g);
    n = glz_flat(buf, 1, 1, 0, 0);
    int r0 = glz_one(g.dr, &R, buf, n), n0 = glz_one(g.dn, &N, buf, n);
    n = glz_flat(buf, 1, 1, 1, 0);  /* releases image 0 */
    int r1 = glz_one(g.dr, &R, buf, n), n1 = glz_one(g.dn, &N, buf, n);
    glz_clear(&g);
    n = glz_flat(buf, 1, 1, 0, 0);
    int r2 = glz_one(g.dr, &R, buf, n), n2 = glz_one(g.dn, &N, buf, n);
    check(r0 == 0 && n0 == 0 && r1 == 0 && n1 == 0, "images 0 and 1 decode in both builds");
    check(n2 == 0, "new build accepts image 0 again after the clear");
    printf("  reference: %s\n", r2 == 0 ? "same as new" : "rejects it (old behaviour)");
    if (r2 != 0) intentional_seen[DIFF_N2]++;
    /* Without a clear, a released ID is still rejected. */
    n = glz_flat(buf, 1, 1, 1, 0);  /* releases the new image 0 */
    check(glz_one(g.dn, &N, buf, n) == 0, "the restarted dictionary continues (image 1 releases image 0)");
    n = glz_flat(buf, 1, 1, 0, 0);
    check(glz_one(g.dn, &N, buf, n) != 0, "without a clear, a released ID is still rejected");
    glz_close(&g);

    printf("N4: LZ palette images write width*height*4 bytes\n");
    {
        /* 9x1 PLT1_LE literal row: 9 visible pixels, 16 stored. */
        static const uint8_t lz[31] = { 0x20, 0x20, 0x5A, 0x4C, 0, 1, 0, 1, 0, 0, 0, 1, 0, 0, 0, 9, 0, 0, 0, 1,
                                        0, 0, 0, 2, 0, 0, 0, 1, 1, 0x55, 0x01 };
        uint32_t pal[2] = { 0x112233, 0x445566 };
        int w, h, top;
        sc_image_type t;
        uint8_t out[160];
        memset(out, SENTINEL, sizeof out);
        int ok = N.lz_begin(l_n, lz, sizeof lz, pal, 2, &w, &h, &t, &top) == 0 && dt_lz_decode(&N, l_n, out + 32, 36) == 0;
        check(ok && w == 9 && h == 1, "decodes");
        int colors = 1;
        for (int x = 0; x < 9; x++) colors &= (out[32 + x * 4] | out[33 + x * 4] << 8 | out[34 + x * 4] << 16) == (int)pal[x % 2 ? 0 : 1];
        check(colors, "visible pixels are the palette colours");
        check(untouched(out, 32) && untouched(out + 32 + 36, sizeof out - 68), "nothing written outside the 36 bytes");
        memset(out, SENTINEL, sizeof out);
        ok = N.lz_begin(l_n, lz, sizeof lz, pal, 2, &w, &h, &t, &top) == 0 && dt_lz_decode(&N, l_n, out + 32, 35) != 0;
        check(ok && untouched(out, sizeof out), "out_cap below width*height*4 fails with nothing written");
        memset(out, SENTINEL, sizeof out);
        R.lz_begin(l_r, lz, sizeof lz, pal, 2, &w, &h, &t, &top);
        dt_lz_decode(&R, l_r, out + 32, sizeof out - 32);
        int padded = !untouched(out + 68, 28);
        printf("  reference: %s\n", padded ? "writes 64 bytes (old behaviour)" : "same as new");
    }
}

/* ---- fixtures ---- */

static void run_fixtures(const char *dir)
{
    char pattern[1024], path[1024], label[256];
    snprintf(pattern, sizeof pattern, "%s\\*", dir);
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) { fprintf(stderr, "no fixtures in %s\n", dir); exit(2); }
    int found = 0;
    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || fd.cFileName[0] == '.') continue;
        snprintf(path, sizeof path, "%s\\%s\\display-0.spicerec", dir, fd.cFileName);
        rec_file f;
        if (rec_load(path, &f)) continue;
        found++;
        glz_seq s = { 0 };
        s.real = 1;
        snprintf(s.name, sizeof s.name, "%s GLZ", fd.cFileName);
        int nq = 0, nl = 0;
        for (int i = 0; i < f.count; i++) {
            rec_image *im = &f.images[i];
            uint64_t key = (uint64_t)found << 48 | (uint64_t)i;
            if (im->type == REC_QUIC) {
                snprintf(label, sizeof label, "%s QUIC %d (%ux%u)", fd.cFileName, nq++, im->width, im->height);
                uint8_t *copy = malloc(im->len); memcpy(copy, im->data, im->len);
                run_single(C_QUIC, copy, im->len, NULL, 0, 1, key, label);
                free(copy);
            } else if (im->type == REC_LZ_RGB) {
                snprintf(label, sizeof label, "%s LZ %d (%ux%u)", fd.cFileName, nl++, im->width, im->height);
                uint8_t *copy = malloc(im->len); memcpy(copy, im->data, im->len);
                run_single(C_LZ, copy, im->len, NULL, 0, 1, key, label);
                free(copy);
            } else {
                seq_add(&s, im->data, im->len, NULL, 0);
            }
        }
        printf("fixture %s: %d QUIC, %d LZ, %d GLZ images\n", fd.cFileName, nq, nl, s.n);
        if (s.n) run_glz_seq(&s, (uint64_t)found << 48 | 0xFFFFFFull);
        seq_free(&s);
        rec_free(&f);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    if (!found) { fprintf(stderr, "no display-0.spicerec under %s\n", dir); exit(2); }
}

int main(int argc, char **argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: difftest <ref.dll> <new.dll> [--fixtures DIR] [--runs N] [--seed S] [--quick] [--verbose]\n");
        return 2;
    }
    const char *fixtures = NULL;
    for (int i = 3; i < argc; i++) {
        if (!strcmp(argv[i], "--fixtures") && i + 1 < argc) fixtures = argv[++i];
        else if (!strcmp(argv[i], "--runs") && i + 1 < argc) runs = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--seed") && i + 1 < argc) seed_base = strtoull(argv[++i], NULL, 0);
        else if (!strcmp(argv[i], "--quick")) quick = 1;
        else if (!strcmp(argv[i], "--verbose")) verbose = 1;
        else { fprintf(stderr, "unknown argument %s\n", argv[i]); return 2; }
    }
    setvbuf(stdout, NULL, _IONBF, 0);
    dt_codec_load(&R, argv[1]);
    dt_codec_load(&N, argv[2]);
    q_r = R.quic_create(); q_n = N.quic_create(); l_r = R.lz_create(); l_n = N.lz_create();
    printf("reference: %s\nnew:       %s\nseed %llu, %d mutations per input%s\n", argv[1], argv[2],
           (unsigned long long)seed_base, runs, quick ? ", quick" : "");
    DWORD t0 = GetTickCount();
    if (fixtures) run_fixtures(fixtures);
    printf("synthetic QUIC...\n"); synth_quic();
    printf("synthetic LZ...\n"); synth_lz();
    printf("synthetic GLZ...\n"); synth_glz();
    printf("intentional differences:\n");
    check_intentional();

    printf("\n%-5s %28s %28s %34s\n", "", "real inputs (ok/fail/diff)", "synthetic (ok/fail/diff)", "mutated (decodes: ok/fail/diff)");
    for (int c = 0; c < C_COUNT; c++)
        printf("%-5s %6ld: %6ld/%6ld/%4ld   %6ld: %6ld/%6ld/%4ld   %8ld: %8ld/%8ld/%4ld\n", CODEC_NAME[c],
               base_real[c].inputs, base_real[c].ok, base_real[c].fail, base_real[c].mismatch,
               base_synth[c].inputs, base_synth[c].ok, base_synth[c].fail, base_synth[c].mismatch,
               mutated[c].inputs, mutated[c].ok, mutated[c].fail, mutated[c].mismatch);
    printf("\nintentional differences seen (listed fixes; the new behaviour is checked above):\n");
    for (int i = 0; i < DIFF_COUNT; i++) printf("  %s x%ld: %s\n", INTENTIONAL[i].id, intentional_seen[i], INTENTIONAL[i].what);
    printf("\n%s: %d failure(s), %.1f s\n", failures ? "FAILED" : "PASSED", failures, (GetTickCount() - t0) / 1000.0);
    R.quic_destroy(q_r); N.quic_destroy(q_n); R.lz_destroy(l_r); N.lz_destroy(l_n);
    return failures ? 1 : 0;
}
