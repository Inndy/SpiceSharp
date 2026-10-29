/* Decode benchmark comparing two spicecodec.dll builds (native/README.md, "Benchmarking codec changes").
   Not upstream code (MIT).

   usage: bench <A.dll> <B.dll> [--fixtures DIR] [--reps N] [--warmup N] [--cpu K] [--only NAME]

   Both DLLs are loaded into one process and driven with the same inputs. Each repetition runs every workload
   once on A and once on B, alternating which goes first, so slow drift (thermal, turbo, background load) hits
   both alike. The thread is pinned to one logical CPU at high priority, and opted out of power throttling.
   Each batch is timed with QueryThreadCycleTime (cycles this thread ran) and QueryPerformanceCounter (wall).
   Reported: per build the median batch time with a 95% confidence interval of the median, and the median of the
   per-repetition B/A ratios with its 95% interval. A ratio interval that excludes 1.00 is a real difference on
   this machine. Each workload's output checksum must match between the builds. */
#include "../difftest/codecs.h"
#include "../difftest/glzenc.h"
#include "../difftest/spicerec.h"
#include <intrin.h>
#include <math.h>
#include <string.h>

typedef struct input { uint8_t *data; size_t len; } input;
typedef struct workload {
    const char *name;
    int codec;          /* 0 QUIC, 1 LZ, 2 GLZ (a sequence decoded in one window, cleared first) */
    input *in;
    int n, repeat;      /* the inputs, decoded `repeat` times per batch */
    uint64_t pixels;    /* per batch, for throughput */
} workload;

static dt_codec C[2];
static sc_quic *quic[2];
static sc_lz *lz[2];
static sc_glz_window *glzw[2];
static sc_glz *glz[2];
static uint8_t *out;
#define OUT_BYTES ((size_t)8192 * 8192 * 4)

static uint64_t fnv(uint64_t h, const uint8_t *p, size_t n) { for (size_t i = 0; i < n; i++) { h ^= p[i]; h *= 1099511628211ull; } return h; }

/* GLZ batches start from a new window, created outside the timed region. (Clearing would do, but a build
   without the N2 fix rejects a dictionary that restarts at ID 0 after images were released.) */
static void fresh_window(int v)
{
    if (glz[v]) { C[v].glz_destroy(glz[v]); C[v].glz_window_destroy(glzw[v]); }
    glzw[v] = C[v].glz_window_create();
    glz[v] = C[v].glz_create(glzw[v]);
}

/* One batch; returns its output checksum when asked (outside the timed runs), or 0 if a decode failed. */
static uint64_t run(const workload *w, int v, int checksum)
{
    const dt_codec *c = &C[v];
    uint64_t h = 1469598103934665603ull;
    int width, height, top, stride;
    sc_image_type type;
    for (int r = 0; r < w->repeat; r++) for (int i = 0; i < w->n; i++) {
        const input *in = &w->in[i];
        if (w->codec == 0) {
            if (c->quic_begin(quic[v], in->data, in->len, &width, &height, &type) || c->quic_decode(quic[v], out, width * 4)) return 0;
            if (checksum) h = fnv(h, out, (size_t)width * height * 4);
        } else if (w->codec == 1) {
            if (c->lz_begin(lz[v], in->data, in->len, NULL, 0, &width, &height, &type, &top) || dt_lz_decode(c, lz[v], out, OUT_BYTES)) return 0;
            if (checksum) h = fnv(h, out, (size_t)width * height * 4);
        } else {
            const uint8_t *o;
            if (c->glz_decode(glz[v], in->data, in->len, &o, &width, &height, &stride, &top)) return 0;
            if (checksum) h = fnv(h, o, (size_t)stride * height);
        }
    }
    return h;
}

static int cmpd(const void *a, const void *b) { double x = *(const double *)a, y = *(const double *)b; return x < y ? -1 : x > y; }

/* Median and a distribution-free 95% interval of the median (order statistics, normal approximation). */
static void median_ci(double *v, int n, double *med, double *lo, double *hi)
{
    qsort(v, (size_t)n, sizeof *v, cmpd);
    *med = n % 2 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
    int k = (int)floor(n / 2.0 - 0.98 * sqrt((double)n));
    int j = (int)ceil(n / 2.0 + 0.98 * sqrt((double)n));
    if (k < 0) k = 0;
    if (j > n - 1) j = n - 1;
    *lo = v[k]; *hi = v[j];
}

/* ---- synthetic inputs ---- */

static uint32_t rng = 12345;
static uint32_t rnd(void) { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }

/* Desktop-like: gradient background, flat windows with text-like strokes, a noisy icon strip. */
static void desktop(uint8_t *px, int W, int H, int scroll)
{
    rng = 12345;
    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
        uint8_t *p = px + ((size_t)y * W + x) * 4; p[0] = (uint8_t)(80 + y / 6); p[1] = (uint8_t)(60 + x / 10); p[2] = 40; p[3] = 0xFF;
    }
    for (int win = 0; win < 4; win++) {
        int x0 = 60 + win * 250, y0 = 40 + win * 120, ww = 640, hh = 400;
        for (int y = y0; y < y0 + hh && y < H; y++) for (int x = x0; x < x0 + ww && x < W; x++) {
            uint8_t *p = px + ((size_t)y * W + x) * 4;
            int title = y < y0 + 28, sy = y + scroll;
            p[0] = title ? 200 : 255; p[1] = title ? 120 : 255; p[2] = title ? 40 : 255;
            int ty = (sy - y0 - 40) % 18, tx = (x - x0 - 10);
            if (!title && y > y0 + 40 && ty < 12 && tx > 0 && tx < ww - 20 && ((tx / 7 + (sy - y0) / 18) % 9) != 0) {
                uint32_t r = rnd(); if ((r & 3) == 0) { p[0] = p[1] = p[2] = (uint8_t)(r >> 8 & 0x3f); }
            }
        }
    }
    for (int y = H - 48; y < H; y++) for (int x = 0; x < W; x++) {
        uint8_t *p = px + ((size_t)y * W + x) * 4; uint32_t r = rnd();
        if (x % 64 < 40) { p[0] = (uint8_t)r; p[1] = (uint8_t)(r >> 8); p[2] = (uint8_t)(r >> 16); } else { p[0] = p[1] = p[2] = 30; }
    }
}

/* Short repeating patterns: back-references that overlap their own output (copy distance < length). */
static void periodic(uint8_t *px, int W, int H)
{
    static const uint32_t pal[] = { 0xFF000000u, 0xFFFFFFFFu, 0xFF2050C0u, 0xFF808080u, 0xFFF0F0F0u, 0xFF102030u, 0xFFC0C0C0u };
    for (int y = 0; y < H; y++) {
        int period = 1 + (y / 16) % 7;
        for (int x = 0; x < W; x++) memcpy(px + ((size_t)y * W + x) * 4, &pal[(x / (1 + y % 3)) % period], 4);
    }
}

static input keep(const uint8_t *p, size_t n) { input in = { malloc(n), n }; memcpy(in.data, p, n); return in; }

int main(int argc, char **argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: bench <A.dll> <B.dll> [--fixtures DIR] [--reps N] [--warmup N] [--cpu K] [--only NAME]\n");
        return 2;
    }
    const char *fixtures = NULL, *only = NULL;
    int reps = 31, warmup = 3, cpu = 2;
    for (int i = 3; i < argc; i++) {
        if (!strcmp(argv[i], "--fixtures") && i + 1 < argc) fixtures = argv[++i];
        else if (!strcmp(argv[i], "--reps") && i + 1 < argc) reps = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--warmup") && i + 1 < argc) warmup = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--cpu") && i + 1 < argc) cpu = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--only") && i + 1 < argc) only = argv[++i];
        else { fprintf(stderr, "unknown argument %s\n", argv[i]); return 2; }
    }
    if (reps < 5) reps = 5;
    setvbuf(stdout, NULL, _IONBF, 0);
    for (int v = 0; v < 2; v++) {
        dt_codec_load(&C[v], argv[1 + v]);
        quic[v] = C[v].quic_create(); lz[v] = C[v].lz_create();
        fresh_window(v);
    }
    out = malloc(OUT_BYTES);

    /* ---- workloads ---- */
    workload wl[16];
    int nwl = 0;
    if (fixtures) {
        input *q = malloc(sizeof(input) * 4096), *l = malloc(sizeof(input) * 4096), *g = malloc(sizeof(input) * 8192);
        int nq = 0, nl = 0, ng = 0;
        uint64_t pq = 0, pl = 0, pg = 0;
        static const char *names[] = { "win7-rich", "win81-rich", "win7-desktop", "win81-desktop" };
        for (int f = 0; f < 4; f++) {
            char path[1024];
            snprintf(path, sizeof path, "%s\\%s\\display-0.spicerec", fixtures, names[f]);
            rec_file rf;
            if (rec_load(path, &rf)) { fprintf(stderr, "cannot read %s\n", path); return 2; }
            for (int i = 0; i < rf.count; i++) {
                rec_image *im = &rf.images[i];
                uint64_t px = (uint64_t)im->width * im->height;
                if (im->type == REC_QUIC) { q[nq++] = keep(im->data, im->len); pq += px; }
                else if (im->type == REC_LZ_RGB) { l[nl++] = keep(im->data, im->len); pl += px; }
                else if (f == 0) { g[ng++] = keep(im->data, im->len); pg += px; }   /* one GLZ dictionary */
            }
            rec_free(&rf);
        }
        wl[nwl++] = (workload){ "quic_fixture", 0, q, nq, 4, pq * 4 };
        wl[nwl++] = (workload){ "lz_fixture", 1, l, nl, 4, pl * 4 };
        wl[nwl++] = (workload){ "glz_fixture", 2, g, ng, 1, pg };
    }
    enum { W = 1280, H = 720 };
    uint8_t *px = malloc((size_t)W * H * 4), *buf = malloc((size_t)W * H * 8);
    desktop(px, W, H, 0);
    int n = C[0].quic_encode_rgb32(px, W, H, W * 4, buf, (size_t)W * H * 8);
    input *qd = malloc(sizeof *qd); *qd = keep(buf, (size_t)n);
    wl[nwl++] = (workload){ "quic_desktop720p", 0, qd, 1, 2, (uint64_t)W * H * 2 };
    n = C[0].lz_encode_rgb32(px, W, H, W * 4, buf, (size_t)W * H * 8);
    input *ld = malloc(sizeof *ld); *ld = keep(buf, (size_t)n);
    wl[nwl++] = (workload){ "lz_desktop720p", 1, ld, 1, 4, (uint64_t)W * H * 4 };
    periodic(px, W, H);
    n = C[0].lz_encode_rgb32(px, W, H, W * 4, buf, (size_t)W * H * 8);
    input *lp = malloc(sizeof *lp); *lp = keep(buf, (size_t)n);
    wl[nwl++] = (workload){ "lz_periodic720p", 1, lp, 1, 4, (uint64_t)W * H * 4 };
    {
        /* A scrolling desktop: 12 frames, each referring to the three before it. */
        input *gs = malloc(sizeof(input) * 12);
        uint32_t *dec[12];
        glz_window_image win[12];
        for (int i = 0; i < 12; i++) {
            desktop(px, W, H, i * 18);
            dec[i] = malloc((size_t)W * H * 4);
            int first = i > 3 ? i - 3 : 0;
            glzenc_params p = { GLZ_RGB32, 1, W, H, (uint64_t)i, (uint32_t)(i - first), px, win + first, i - first, 7, GLZ_STYLE_NORMAL };
            size_t m = glzenc_encode(&p, buf, (size_t)W * H * 8, dec[i]);
            win[i] = (glz_window_image){ (uint64_t)i, W * H, dec[i] };
            gs[i] = keep(buf, m);
        }
        wl[nwl++] = (workload){ "glz_scroll720p", 2, gs, 12, 1, (uint64_t)W * H * 12 };
        /* Four frames of short repeating patterns, each image on its own (whd = 0). */
        periodic(px, W, H);
        input *gp = malloc(sizeof(input) * 4);
        for (int i = 0; i < 4; i++) {
            glzenc_params p = { GLZ_RGB32, 1, W, H, (uint64_t)i, 0, px, NULL, 0, 7, GLZ_STYLE_NORMAL };
            gp[i] = keep(buf, glzenc_encode(&p, buf, (size_t)W * H * 8, NULL));
        }
        wl[nwl++] = (workload){ "glz_periodic720p", 2, gp, 4, 1, (uint64_t)W * H * 4 };
    }

    /* ---- environment ---- */
    int regs[4]; char brand[49] = { 0 };
    for (int i = 0; i < 3; i++) { __cpuid(regs, (int)(0x80000002u + (unsigned)i)); memcpy(brand + i * 16, regs, 16); }
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    PROCESS_POWER_THROTTLING_STATE pt = { PROCESS_POWER_THROTTLING_CURRENT_VERSION, PROCESS_POWER_THROTTLING_EXECUTION_SPEED, 0 };
    SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &pt, sizeof pt);
    if (!SetThreadAffinityMask(GetCurrentThread(), (DWORD_PTR)1 << cpu)) { fprintf(stderr, "cannot pin to CPU %d\n", cpu); return 2; }
    LARGE_INTEGER qpf; QueryPerformanceFrequency(&qpf);
    printf("cpu: %s\npinned to logical CPU %d, high priority, %d repetitions after %d warm-up\nA: %s\nB: %s\n\n",
           brand, cpu, reps, warmup, argv[1], argv[2]);

    /* ---- measure ---- */
    double *t[2][16], *c[2][16];
    for (int v = 0; v < 2; v++) for (int k = 0; k < nwl; k++) { t[v][k] = malloc(sizeof(double) * (size_t)reps); c[v][k] = malloc(sizeof(double) * (size_t)reps); }
    int fails = 0;
    for (int r = -warmup; r < reps; r++) {
        for (int k = 0; k < nwl; k++) {
            if (only && strcmp(only, wl[k].name)) continue;
            for (int s = 0; s < 2; s++) {
                int v = (r + k + s) % 2 == 0 ? 0 : 1;   /* alternate which build goes first */
                ULONG64 c0, c1; LARGE_INTEGER q0, q1;
                if (wl[k].codec == 2) fresh_window(v);
                QueryThreadCycleTime(GetCurrentThread(), &c0); QueryPerformanceCounter(&q0);
                uint64_t ok = run(&wl[k], v, 0);
                QueryPerformanceCounter(&q1); QueryThreadCycleTime(GetCurrentThread(), &c1);
                if (!ok) fails++;
                if (r >= 0) { t[v][k][r] = (double)(q1.QuadPart - q0.QuadPart) * 1000.0 / (double)qpf.QuadPart; c[v][k][r] = (double)(c1 - c0); }
            }
        }
    }

    printf("%-18s %24s %24s %22s %22s  %s\n", "workload", "A ms (95% CI)", "B ms (95% CI)", "B/A wall (95% CI)", "B/A cycles (95% CI)", "output");
    for (int k = 0; k < nwl; k++) {
        if (only && strcmp(only, wl[k].name)) continue;
        double m[2], lo[2], hi[2], rt[512], rc[512], rm, rl, rh, cm, cl, ch;
        int nr = reps < 512 ? reps : 512;
        for (int i = 0; i < nr; i++) { rt[i] = t[1][k][i] / t[0][k][i]; rc[i] = c[1][k][i] / c[0][k][i]; }
        for (int v = 0; v < 2; v++) median_ci(t[v][k], reps, &m[v], &lo[v], &hi[v]);
        median_ci(rt, nr, &rm, &rl, &rh);
        median_ci(rc, nr, &cm, &cl, &ch);
        fresh_window(0); fresh_window(1);
        uint64_t ha = run(&wl[k], 0, 1), hb = run(&wl[k], 1, 1);
        printf("%-18s %8.3f (%6.3f-%6.3f) %8.3f (%6.3f-%6.3f) %6.3f (%5.3f-%5.3f) %6.3f (%5.3f-%5.3f)  %s\n", wl[k].name,
               m[0], lo[0], hi[0], m[1], lo[1], hi[1], rm, rl, rh, cm, cl, ch, ha == hb ? "same" : "DIFFERS");
        if (ha != hb) fails++;
    }
    if (fails) printf("\n%d decode failure(s) or output differences\n", fails);
    return fails ? 3 : 0;
}
