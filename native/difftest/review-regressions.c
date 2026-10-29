/* Native allocation regressions confirmed in the 2026-10-30 re-review. Not upstream code (MIT).
   Linked with alloc_hook.h, like faultinject.c; run.sh runs it. They failed until the fixes landed.
   Counts requested allocation capacities, excluding allocator overhead and the test's own buffers. */
#include "spice_codec.h"
#include <stdio.h>
#include <stdint.h>
#include <stddef.h>
#include <string.h>

#undef malloc
#undef calloc
#undef realloc
#undef free

typedef union allocation {
    max_align_t alignment;
    struct { size_t bytes; } info;
} allocation;
static size_t live_bytes, live_blocks, calls;
static int failures;

void *dt_malloc(size_t n)
{
    calls++;
    if (n > SIZE_MAX - sizeof(allocation)) return NULL;
    allocation *a = malloc(sizeof(*a) + n);
    if (!a) return NULL;
    a->info.bytes = n;
    live_bytes += n; live_blocks++;
    return a + 1;
}
void *dt_calloc(size_t n, size_t s)
{
    if (s && n > SIZE_MAX / s) return NULL;
    void *p = dt_malloc(n * s);
    if (p) memset(p, 0, n * s);
    return p;
}
void dt_free(void *p)
{
    if (!p) return;
    allocation *a = (allocation *)p - 1;
    live_bytes -= a->info.bytes; live_blocks--;
    free(a);
}
void *dt_realloc(void *p, size_t n)
{
    if (!p) return dt_malloc(n);
    if (!n) { dt_free(p); return NULL; }
    calls++;
    if (n > SIZE_MAX - sizeof(allocation)) return NULL;
    allocation *a = (allocation *)p - 1;
    size_t old = a->info.bytes;
    allocation *b = realloc(a, sizeof(*b) + n);
    if (!b) return NULL;
    b->info.bytes = n;
    live_bytes = live_bytes - old + n;
    return b + 1;
}

static void check(int ok, const char *what)
{
    printf("  %s %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) failures++;
}
static void be32(uint8_t *p, uint32_t v)
{
    for (int i = 0; i < 4; i++) p[i] = (uint8_t)(v >> (24 - i * 8));
}

/* Test both the reviewed commit's ABI and the capacity-taking ABI being developed concurrently.
   Select a correctly typed call at compile time; the output capacity is always the visible image size. */
static int lz_legacy(int (*decode)(sc_lz *, uint8_t *), sc_lz *z, uint8_t *out, size_t cap)
{
    (void)cap;
    return decode(z, out);
}
static int lz_sized(int (*decode)(sc_lz *, uint8_t *, size_t), sc_lz *z, uint8_t *out, size_t cap)
{
    return decode(z, out, cap);
}
#define decode_lz(z, out, cap) _Generic(&sc_lz_decode, \
    int (*)(sc_lz *, uint8_t *): lz_legacy, \
    int (*)(sc_lz *, uint8_t *, size_t): lz_sized)(&sc_lz_decode, z, out, cap)

/* One RGB32 literal, then an intra-image distance-one run. No encoder heap allocations. */
static int glz_flat(sc_glz *g, uint32_t width, uint32_t height, uint64_t id, uint32_t whd)
{
    uint8_t data[40000] = {0};
    be32(data, 0x20205A4C); be32(data + 4, 0x10001); data[8] = 0x18;
    be32(data + 9, width); be32(data + 13, height); be32(data + 17, width * 4);
    be32(data + 21, (uint32_t)(id >> 32)); be32(data + 25, (uint32_t)id); be32(data + 29, whd);
    size_t n = 33;
    data[n++] = 0; data[n++] = 10; data[n++] = 20; data[n++] = 30;
    uint32_t len = width * height - 1;
    if (len >= 7) {
        data[n++] = 0xE0;
        uint32_t ext = len - 7;
        while (ext >= 255) { data[n++] = 255; ext -= 255; }
        data[n++] = (uint8_t)ext;
    } else data[n++] = (uint8_t)(len << 5);
    data[n++] = 0; data[n++] = 0;
    const uint8_t *out = NULL; int w, h, stride, top;
    int r = sc_glz_decode(g, data, n, &out, &w, &h, &stride, &top);
    if (r) return r;
    size_t last = ((size_t)width * height - 1) * 4;
    return w == (int)width && h == (int)height && stride == (int)width * 4 && top == 1 &&
           out[0] == 10 && out[1] == 20 && out[2] == 30 && out[3] == 0 &&
           out[last] == 10 && out[last + 1] == 20 && out[last + 2] == 30 && out[last + 3] == 0 ? 0 : -2;
}

static void glz_capacity_budget(void)
{
    puts("N1: count reused buffer capacity in the live-window budget");
    sc_glz_window *w = sc_glz_window_create(); sc_glz *g = sc_glz_create(w);
    if (!w || !g) { check(0, "create GLZ window/decoder"); sc_glz_destroy(g); sc_glz_window_destroy(w); return; }
    size_t base = live_bytes;
    int ok = 1;
    /* Pool eight 4 MiB buffers, then reuse them as 2 MiB images while keeping all new IDs alive. */
    for (uint32_t id = 0; id < 8; id++) if (glz_flat(g, 1024, 1024, id, id)) ok = 0;
    if (glz_flat(g, 1024, 512, 8, 0)) ok = 0;
    for (uint32_t id = 9; id < 72; id++) {
        if (glz_flat(g, 1024, 512, id, id - 8)) {
            /* A capacity-aware implementation may reject earlier. */
            break;
        }
    }
    check(ok, "seed images decode correctly");
    printf("  requested live allocation bytes above empty window: %zu\n", live_bytes - base);
    /* Pool is empty here on the reviewed implementation. Allow 64 KiB for slot arrays/image structs. */
    check(live_bytes - base <= ((size_t)128 << 20) + 65536,
          "the live window stays within 128 MiB plus metadata when larger pooled buffers are reused");
    sc_glz_destroy(g); sc_glz_window_destroy(w);
    check(live_blocks == 0, "capacity-budget scenario frees all storage");
}

static void lz_error_scratch(void)
{
    puts("N4: release oversized palette scratch on decode failure");
    sc_lz *z = sc_lz_create();
    if (!z) { check(0, "create LZ decoder"); return; }
    size_t base = live_bytes;
    /* Valid PLT1_LE header, padded 2056-pixel rows, no body: begin succeeds, decode fails. */
    uint8_t data[28] = {0};
    uint32_t fields[] = { 0x20205A4C, 0x10001, 1, 2049, 1024, 257, 1 };
    for (int i = 0; i < 7; i++) be32(data + i * 4, fields[i]);
    uint32_t pal[] = { 0x112233, 0x445566 };
    uint8_t *out = malloc((size_t)2056 * 1024 * 4);
    if (!out) { check(0, "allocate test output"); sc_lz_destroy(z); return; }
    int w, h, top; sc_image_type t;
    int r = sc_lz_begin(z, data, sizeof data, pal, 2, &w, &h, &t, &top);
    check(r == 0, "valid palette header is accepted");
    if (!r) check(decode_lz(z, out, (size_t)2049 * 1024 * 4) == -1, "missing palette body returns a codec error");
    printf("  scratch retained after corrupt decode: %zu bytes\n", live_bytes - base);
    check(live_bytes - base <= ((size_t)4 << 20), "error path retains at most the 4 MiB scratch allowance");
    uint8_t rgb[32] = {0};
    uint32_t rgb_fields[] = { 0x20205A4C, 0x10001, 8, 1, 1, 4, 1 };
    for (int i = 0; i < 7; i++) be32(rgb + i * 4, rgb_fields[i]);
    rgb[29] = 10; rgb[30] = 20; rgb[31] = 30;
    r = sc_lz_begin(z, rgb, sizeof rgb, NULL, 0, &w, &h, &t, &top);
    check(r == 0 && decode_lz(z, out, 4) == 0, "decoder recovers on a small RGB image");
    check(live_bytes - base <= ((size_t)4 << 20), "oversized scratch is not pinned by later RGB images");
    free(out); sc_lz_destroy(z);
    check(live_blocks == 0, "scratch-error scenario frees all storage on destroy");
}

static void glz_rotating_sizes(void)
{
    puts("N5: ten recurring GLZ update sizes, each smaller than 6 MiB, whd=0");
    sc_glz_window *w = sc_glz_window_create(); sc_glz *g = sc_glz_create(w);
    if (!w || !g) { check(0, "create GLZ window/decoder"); sc_glz_destroy(g); sc_glz_window_destroy(w); return; }
    int ok = 1;
    size_t start = 0;
    for (uint32_t id = 0; id < 40; id++) {
        /* Heights 1, 4, 10, ..., 1534: adjacent capacities differ by more than the pool's fit tolerance.
           All ten buffers together need less than 12 MiB; the eight-slot limit alone causes eviction. */
        uint32_t h = 3 * (1u << (id % 10)) - 2;
        if (id == 20) start = calls;
        if (glz_flat(g, 1024, h, id, 0)) ok = 0;
    }
    size_t n = calls - start;
    check(ok, "all rotating-size frames decode correctly");
    printf("  allocations in 20 decodes after two full warm-up cycles: %zu\n", n);
    check(n == 0, "recurring display updates allocate nothing after warm-up");
    sc_glz_destroy(g); sc_glz_window_destroy(w);
    check(live_blocks == 0, "rotating-size scenario frees all storage");
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    glz_capacity_budget();
    lz_error_scratch();
    glz_rotating_sizes();
    printf("%s: %d failure(s)\n", failures ? "FAILED" : "PASSED", failures);
    return failures ? 1 : 0;
}
