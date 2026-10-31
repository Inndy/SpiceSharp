/* Software H.264 against MJPEG, CPU per frame (docs/verification/2026-10-31-h264-444-software.md).
 *
 *   h264bench.exe <dir with spicecodec.dll and turbojpeg.dll> <clip.h264> [--reps N] [--quality Q]
 *
 * The clip is an Annex B stream with access unit delimiters (one access unit per frame, as spice-server's x264enc
 * sends). Each frame is decoded and converted to BGRA once to make the MJPEG input: libjpeg-turbo encodes it at
 * --quality (default 80) with 4:2:0 subsampling (libjpeg's defaults, which spice-server's MJPEG encoder keeps). Then,
 * for --reps rounds (default 5), the whole clip is decoded with each configuration: sc_h264 with 1 and 4 slice
 * threads (decode only, and decode plus BGRA conversion), and tj3Decompress8 to BGRA. Process CPU time (all threads,
 * GetProcessTimes) and wall time are summed over the rounds and divided by the frames decoded. A bounded run over a
 * fixed clip: no busy loops, nothing else runs. MIT. */
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "spice_codec.h"

typedef sc_h264 *(SC_CALL *open_fn)(int);
typedef void (SC_CALL *close_fn)(sc_h264 *);
typedef int (SC_CALL *decode_fn)(sc_h264 *, const uint8_t *, size_t, sc_h264_picture *);
typedef int (SC_CALL *convert_fn)(sc_h264 *, uint8_t *, size_t, int, int);
typedef void *(*tj_init_fn)(int);
typedef void (*tj_destroy_fn)(void *);
typedef int (*tj_set_fn)(void *, int, int);
typedef int (*tj_compress_fn)(void *, const uint8_t *, int, int, int, int, unsigned char **, size_t *);
typedef int (*tj_header_fn)(void *, const unsigned char *, size_t);
typedef int (*tj_decompress_fn)(void *, const unsigned char *, size_t, uint8_t *, int, int);
typedef void (*tj_free_fn)(void *);

static open_fn h_open; static close_fn h_close; static decode_fn h_decode; static convert_fn h_convert;
static tj_init_fn tj_init; static tj_destroy_fn tj_destroy; static tj_set_fn tj_set; static tj_compress_fn tj_compress;
static tj_header_fn tj_header; static tj_decompress_fn tj_decompress; static tj_free_fn tj_free;

enum { TJINIT_COMPRESS = 0, TJINIT_DECOMPRESS = 1, TJPARAM_QUALITY = 3, TJPARAM_SUBSAMP = 4, TJSAMP_420 = 2, TJPF_BGRX = 3, TJPF_BGRA = 8 };

static void *must(HMODULE m, const char *name)
{
    void *f = (void *)GetProcAddress(m, name);
    if (!f) { fprintf(stderr, "missing export %s\n", name); exit(2); }
    return f;
}

static double now_s(void)
{
    LARGE_INTEGER c, f;
    QueryPerformanceCounter(&c); QueryPerformanceFrequency(&f);
    return (double)c.QuadPart / (double)f.QuadPart;
}

static double cpu_s(void)
{
    FILETIME a, b, k, u;
    GetProcessTimes(GetCurrentProcess(), &a, &b, &k, &u);
    return ((double)(((uint64_t)k.dwHighDateTime << 32) | k.dwLowDateTime) +
            (double)(((uint64_t)u.dwHighDateTime << 32) | u.dwLowDateTime)) / 1e7;
}

typedef struct { const uint8_t *p; size_t n; } unit;

int main(int argc, char **argv)
{
    if (argc < 3) { fprintf(stderr, "usage: h264bench <dll dir> <clip.h264> [--reps N] [--quality Q]\n"); return 2; }
    int reps = 5, quality = 80;
    for (int i = 3; i + 1 < argc; i += 2) {
        if (!strcmp(argv[i], "--reps")) reps = atoi(argv[i + 1]);
        else if (!strcmp(argv[i], "--quality")) quality = atoi(argv[i + 1]);
    }
    char path[MAX_PATH];
    snprintf(path, sizeof path, "%s\\spicecodec.dll", argv[1]);
    HMODULE sc = LoadLibraryA(path);
    snprintf(path, sizeof path, "%s\\turbojpeg.dll", argv[1]);
    HMODULE tj = LoadLibraryA(path);
    if (!sc || !tj) { fprintf(stderr, "cannot load the DLLs from %s\n", argv[1]); return 2; }
    h_open = (open_fn)must(sc, "sc_h264_open"); h_close = (close_fn)must(sc, "sc_h264_close");
    h_decode = (decode_fn)must(sc, "sc_h264_decode"); h_convert = (convert_fn)must(sc, "sc_h264_convert");
    tj_init = (tj_init_fn)must(tj, "tj3Init"); tj_destroy = (tj_destroy_fn)must(tj, "tj3Destroy"); tj_set = (tj_set_fn)must(tj, "tj3Set");
    tj_compress = (tj_compress_fn)must(tj, "tj3Compress8"); tj_header = (tj_header_fn)must(tj, "tj3DecompressHeader");
    tj_decompress = (tj_decompress_fn)must(tj, "tj3Decompress8"); tj_free = (tj_free_fn)must(tj, "tj3Free");

    FILE *f = fopen(argv[2], "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", argv[2]); return 2; }
    fseek(f, 0, SEEK_END); long size = ftell(f); fseek(f, 0, SEEK_SET);
    uint8_t *data = malloc((size_t)size);
    if (!data || fread(data, 1, (size_t)size, f) != (size_t)size) return 2;
    fclose(f);
    /* Access units start at each delimiter (00 00 01 09, with a fourth leading zero when present). */
    size_t cap = 4096, count = 0;
    unit *units = malloc(cap * sizeof *units);
    for (long i = 0; i + 3 < size; i++)
        if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 0x1F) == 9) {
            long s = i > 0 && data[i - 1] == 0 ? i - 1 : i;
            if (count && units[count - 1].n == 0) units[count - 1].n = (size_t)(s - (units[count - 1].p - data));
            if (count == cap) units = realloc(units, (cap *= 2) * sizeof *units);
            units[count].p = data + s; units[count].n = 0; count++;
        }
    if (!count) { fprintf(stderr, "no access unit delimiters\n"); return 2; }
    units[count - 1].n = (size_t)(data + size - units[count - 1].p);

    /* The MJPEG input: every frame, decoded once and encoded with libjpeg-turbo. */
    sc_h264 *h = h_open(4);
    sc_h264_picture pic = {0};
    uint8_t *bgra = NULL;
    unsigned char **jpegs = calloc(count, sizeof *jpegs);
    size_t *jpeg_sizes = calloc(count, sizeof *jpeg_sizes), h264_bytes = 0, jpeg_bytes = 0;
    int w = 0, ht = 0, chroma = 0, frames = 0;
    void *enc = tj_init(TJINIT_COMPRESS);
    tj_set(enc, TJPARAM_QUALITY, quality);
    tj_set(enc, TJPARAM_SUBSAMP, TJSAMP_420);
    for (size_t i = 0; i < count; i++) {
        h264_bytes += units[i].n;
        if (h_decode(h, units[i].p, units[i].n, &pic) != SC_H264_PICTURE) continue;
        if (!bgra) { w = pic.width; ht = pic.height; chroma = pic.chroma; bgra = malloc((size_t)w * ht * 4); }
        if (h_convert(h, bgra, (size_t)w * ht * 4, w * 4, 0) != 0) { fprintf(stderr, "convert failed\n"); return 1; }
        if (tj_compress(enc, bgra, w, w * 4, ht, TJPF_BGRA, &jpegs[frames], &jpeg_sizes[frames]) != 0) { fprintf(stderr, "jpeg failed\n"); return 1; }
        jpeg_bytes += jpeg_sizes[frames++];
    }
    tj_destroy(enc);
    h_close(h);
    printf("clip: %s, %dx%d, chroma_format_idc %d, %zu access units, %d pictures\n", argv[2], w, ht, chroma, count, frames);
    printf("H.264 %.1f KiB/frame; MJPEG (libjpeg-turbo q%d 4:2:0) %.1f KiB/frame\n", h264_bytes / 1024.0 / count, quality,
           jpeg_bytes / 1024.0 / frames);
    printf("%d rounds per configuration; CPU is all threads of the process\n\n", reps);
    printf("%-34s %12s %12s\n", "configuration", "CPU ms/frame", "wall ms/frame");

    struct { const char *name; int threads, convert; } configs[] = {
        { "H.264 software, 1 thread, decode", 1, 0 },
        { "H.264 software, 1 thread, +BGRA", 1, 1 },
        { "H.264 software, 4 threads, decode", 4, 0 },
        { "H.264 software, 4 threads, +BGRA", 4, 1 },
    };
    for (size_t c = 0; c < sizeof configs / sizeof *configs; c++) {
        double cpu = 0, wall = 0, opening = 0;
        long n = 0;
        for (int r = 0; r < reps; r++) {
            double o0 = now_s();
            h = h_open(configs[c].threads); /* thread creation stays outside the timing; reported below */
            opening += now_s() - o0;
            double c0 = cpu_s(), t0 = now_s();
            for (size_t i = 0; i < count; i++)
                if (h_decode(h, units[i].p, units[i].n, &pic) == SC_H264_PICTURE) {
                    n++;
                    if (configs[c].convert) h_convert(h, bgra, (size_t)w * ht * 4, w * 4, 0);
                }
            cpu += cpu_s() - c0; wall += now_s() - t0;
            h_close(h);
        }
        printf("%-34s %12.2f %12.2f   (sc_h264_open %.2f ms)\n", configs[c].name, cpu * 1000 / n, wall * 1000 / n,
               opening * 1000 / reps);
    }
    {
        double cpu = 0, wall = 0;
        long n = 0;
        void *dec = tj_init(TJINIT_DECOMPRESS);
        for (int r = 0; r < reps; r++) {
            double c0 = cpu_s(), t0 = now_s();
            for (int i = 0; i < frames; i++) {
                if (tj_header(dec, jpegs[i], jpeg_sizes[i]) != 0 || tj_decompress(dec, jpegs[i], jpeg_sizes[i], bgra, w * 4, TJPF_BGRX) != 0) {
                    fprintf(stderr, "jpeg decode failed\n"); return 1;
                }
                n++;
            }
            cpu += cpu_s() - c0; wall += now_s() - t0;
        }
        tj_destroy(dec);
        printf("%-34s %12.2f %12.2f\n", "MJPEG libjpeg-turbo, to BGRA", cpu * 1000 / n, wall * 1000 / n);
    }
    for (int i = 0; i < frames; i++) tj_free(jpegs[i]);
    return 0;
}
