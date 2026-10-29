/* Allocation checks for the working tree's codec bridge, linked statically with alloc_hook.h force-included:
   - every heap allocation the QUIC, LZ and GLZ entry points make is failed in turn (N3): the process must not
     crash, the API must report the failure, and nothing may leak;
   - warmed-up decoding of same-sized images must not allocate (N5 for GLZ; LZ palette scratch, QUIC rows);
   - a cleared GLZ window gives its memory back.
   Not upstream code (MIT). */
#include "spice_codec.h"
#include "glzenc.h"
#include <stdio.h>
#include <string.h>

#undef malloc
#undef calloc
#undef realloc
#undef free

static long live, calls, fail_at;
static int armed, injected;

static int should_fail(void)
{
    if (!armed) return 0;
    if (++calls == fail_at) { injected = 1; return 1; }
    return 0;
}
void *dt_malloc(size_t n) { if (should_fail()) return NULL; void *p = malloc(n); if (p) live++; return p; }
void *dt_calloc(size_t n, size_t s) { if (should_fail()) return NULL; void *p = calloc(n, s); if (p) live++; return p; }
void *dt_realloc(void *p, size_t n)
{
    if (should_fail()) return NULL;
    void *q = realloc(p, n);
    if (q && !p) live++;
    return q;
}
void dt_free(void *p) { if (p) live--; free(p); }

static int failures;
static void check(int ok, const char *what) { printf("  %s %s\n", ok ? "ok  " : "FAIL", what); if (!ok) failures++; }

/* ---- inputs, built before any measurement ---- */

typedef struct blob { uint8_t *p; size_t n; } blob;
static blob quic_small, quic_wide, lz_rgb, lz_plt, glz_seq[24];
static uint8_t *out;
#define OUT_BYTES ((size_t)256 * 64 * 4 * 2)

static void make_image(uint8_t *px, int w, int h, int seed)
{
    for (int i = 0; i < w * h; i++) {
        px[i * 4] = (uint8_t)(i * 7 + seed); px[i * 4 + 1] = (uint8_t)(i / w * 3); px[i * 4 + 2] = (uint8_t)((i % w) ^ seed); px[i * 4 + 3] = 0xFF;
    }
}

static blob keep(const uint8_t *p, size_t n) { blob b = { malloc(n), n }; memcpy(b.p, p, n); return b; }

static void build_inputs(void)
{
    static uint8_t px[256 * 64 * 4], buf[256 * 64 * 16];
    int n;
    make_image(px, 64, 16, 1);
    n = sc_quic_encode_rgb32(px, 64, 16, 64 * 4, buf, sizeof buf); quic_small = keep(buf, (size_t)n);
    make_image(px, 256, 8, 2);
    n = sc_quic_encode_rgb32(px, 256, 8, 256 * 4, buf, sizeof buf); quic_wide = keep(buf, (size_t)n);
    make_image(px, 61, 13, 3);
    n = sc_lz_encode_rgb32(px, 61, 13, 61 * 4, buf, sizeof buf); lz_rgb = keep(buf, (size_t)n);
    /* PLT1_LE, 9 x 5, stride 2: 16 stored pixels per row, so the bridge needs its padding scratch. */
    static const uint32_t hdr[7] = { 0x20205A4C, 0x10001, 1, 9, 5, 2, 1 };
    for (int i = 0; i < 7; i++) { buf[i * 4] = (uint8_t)(hdr[i] >> 24); buf[i * 4 + 1] = (uint8_t)(hdr[i] >> 16); buf[i * 4 + 2] = (uint8_t)(hdr[i] >> 8); buf[i * 4 + 3] = (uint8_t)hdr[i]; }
    buf[28] = 9;  /* ten literal bytes */
    for (int i = 0; i < 10; i++) buf[29 + i] = (uint8_t)(0x55 + i);
    lz_plt = keep(buf, 39);
    /* GLZ: 24 images in one window. IDs 0-19 are kept (whd = id) so the slot array grows past 16; then a
       restart after a clear. Sizes vary so pooled storage is both reused and skipped. */
    for (int i = 0; i < 24; i++) {
        int w = 16 + (i % 3) * 8, h = 9;
        uint64_t id = i < 20 ? (uint64_t)i : (uint64_t)(i - 20);
        make_image(px, w, h, i);
        glzenc_params p = { i % 4 == 3 ? GLZ_RGBA : GLZ_RGB32, 1, (uint32_t)w, (uint32_t)h, id, (uint32_t)(i < 20 ? id : 0), px, NULL, 0, (uint32_t)i, GLZ_STYLE_NORMAL };
        size_t m = glzenc_encode(&p, buf, sizeof buf, NULL);
        glz_seq[i] = keep(buf, m);
    }
    out = malloc(OUT_BYTES);
}

/* ---- scenarios: each runs one handle's whole life; any failure must surface as an error return ---- */

static int scen_quic(void)
{
    int ok = 0, w, h; sc_image_type t;
    sc_quic *q = sc_quic_create();
    if (!q) return 0;
    const blob *in[] = { &quic_small, &quic_wide, &quic_small };
    for (int i = 0; i < 3; i++)
        if (sc_quic_begin(q, in[i]->p, in[i]->n, &w, &h, &t) == 0 && sc_quic_decode(q, out, w * 4) == 0) ok++;
    sc_quic_destroy(q);
    return ok;
}

static int scen_lz(void)
{
    int ok = 0, w, h, top; sc_image_type t;
    uint32_t pal[2] = { 0x112233, 0x445566 };
    sc_lz *z = sc_lz_create();
    if (!z) return 0;
    for (int i = 0; i < 4; i++) {
        const blob *b = i % 2 ? &lz_plt : &lz_rgb;
        if (sc_lz_begin(z, b->p, b->n, pal, 2, &w, &h, &t, &top) == 0 && sc_lz_decode(z, out, (size_t)w * h * 4) == 0) ok++; /* tight: the scratch path */
    }
    sc_lz_destroy(z);
    return ok;
}

static int scen_glz(void)
{
    int ok = 0;
    sc_glz_window *win = sc_glz_window_create();
    if (!win) return 0;
    sc_glz *g = sc_glz_create(win);
    if (g) {
        for (int i = 0; i < 24; i++) {
            if (i == 20) sc_glz_window_clear(win);
            const uint8_t *o; int w, h, s, top;
            if (sc_glz_decode(g, glz_seq[i].p, glz_seq[i].n, &o, &w, &h, &s, &top) == 0) ok++;
        }
        sc_glz_destroy(g);
    }
    sc_glz_window_destroy(win);
    return ok;
}

static void sweep(const char *name, int (*scen)(void))
{
    long base = live;
    armed = 1; fail_at = 0; calls = 0;
    int full = scen();
    armed = 0;
    long total = calls;
    int leaks = live != base, runs = 0, degraded = 0;
    for (long f = 1; f <= total; f++) {
        armed = 1; fail_at = f; calls = 0; injected = 0;
        int ok = scen();
        armed = 0;
        runs++;
        if (live != base) { leaks++; printf("  leak: %s, allocation %ld failed: %ld block(s)\n", name, f, live - base); base = live; }
        if (injected && ok < full) degraded++;
    }
    char what[160];
    snprintf(what, sizeof what, "%s: %ld allocations, each failed in turn: no crash, no leak (%d of %d runs reported errors)", name, total, degraded, runs);
    check(!leaks, what);
}

static long allocations_during(void (*fn)(void *), void *arg)
{
    armed = 1; fail_at = 0; calls = 0;
    fn(arg);
    armed = 0;
    return calls;
}

/* ---- warm paths ---- */

typedef struct glz_warm { sc_glz *g; int sizes, whd, first, count; } glz_warm;
static void glz_frames(void *arg)
{
    glz_warm *a = arg;
    static uint8_t px[320 * 200 * 4], buf[320 * 200 * 16];
    for (int i = a->first; i < a->first + a->count; i++) {
        int w = 320 - (i % a->sizes) * 40, h = 200 - (i % a->sizes) * 30;
        make_image(px, w, h, i);
        glzenc_params p = { GLZ_RGB32, 1, (uint32_t)w, (uint32_t)h, (uint64_t)i, (uint32_t)(i < a->whd ? i : a->whd), px, NULL, 0, 1, GLZ_STYLE_NORMAL };
        armed = 0;  /* the test encoder's own allocations don't count */
        size_t n = glzenc_encode(&p, buf, sizeof buf, NULL);
        armed = 1;
        const uint8_t *o; int ow, oh, os, top;
        if (sc_glz_decode(a->g, buf, n, &o, &ow, &oh, &os, &top) != 0) { printf("  warm GLZ decode %d failed\n", i); failures++; }
    }
}

static void lz_plt_frames(void *arg)
{
    sc_lz *z = arg;
    uint32_t pal[2] = { 1, 2 };
    int w, h, top; sc_image_type t;
    for (int i = 0; i < 50; i++) if (sc_lz_begin(z, lz_plt.p, lz_plt.n, pal, 2, &w, &h, &t, &top) || sc_lz_decode(z, out, OUT_BYTES)) failures++;
}

static void quic_frames(void *arg)
{
    sc_quic *q = arg;
    int w, h; sc_image_type t;
    for (int i = 0; i < 50; i++) if (sc_quic_begin(q, quic_small.p, quic_small.n, &w, &h, &t) || sc_quic_decode(q, out, w * 4)) failures++;
}

static void warm_paths(void)
{
    static const struct { int sizes, whd; const char *what; } cases[] = {
        { 1, 1, "GLZ, same size, whd=1" },
        { 2, 1, "GLZ, two alternating sizes, whd=1" },
        { 3, 2, "GLZ, three rotating sizes, whd=2" },
        { 1, 4, "GLZ, same size, whd=4" },
    };
    for (size_t c = 0; c < sizeof cases / sizeof cases[0]; c++) {
        sc_glz_window *win = sc_glz_window_create();
        glz_warm a = { sc_glz_create(win), cases[c].sizes, cases[c].whd, 0, 20 };
        allocations_during(glz_frames, &a);
        a.first = 20; a.count = 100;
        long n = allocations_during(glz_frames, &a);
        char what[160];
        snprintf(what, sizeof what, "%s: %ld allocations in 100 warmed-up decodes", cases[c].what, n);
        check(n == 0, what);
        long before = live;
        sc_glz_window_clear(win);
        snprintf(what, sizeof what, "%s: clearing the window frees its images and pooled storage (%ld blocks)", cases[c].what, before - live);
        check(live <= before - (cases[c].whd + 1), what);
        sc_glz_destroy(a.g);
        sc_glz_window_destroy(win);
    }
    sc_lz *z = sc_lz_create();
    lz_plt_frames(z);
    long n = allocations_during(lz_plt_frames, z);
    char what[160];
    snprintf(what, sizeof what, "LZ palette with padding: %ld allocations in 50 warmed-up decodes", n);
    check(n == 0, what);
    sc_lz_destroy(z);
    sc_quic *q = sc_quic_create();
    quic_frames(q);
    n = allocations_during(quic_frames, q);
    snprintf(what, sizeof what, "QUIC, same size: %ld allocations in 50 warmed-up decodes", n);
    check(n == 0, what);
    sc_quic_destroy(q);
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    build_inputs();
    printf("allocation failure injection (N3):\n");
    sweep("QUIC create/begin/decode/destroy", scen_quic);
    sweep("LZ create/begin/decode (RGB32, padded PLT1)/destroy", scen_lz);
    sweep("GLZ window/decoder create, 24 decodes with slot growth, clear, destroy", scen_glz);
    printf("warm-path allocations (N5):\n");
    warm_paths();
    printf("%s: %d failure(s)\n", failures ? "FAILED" : "PASSED", failures);
    return failures ? 1 : 0;
}
