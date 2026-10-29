/* See spicerec.h. Not upstream code (MIT). */
#include "spicerec.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static uint32_t le32(const uint8_t *p) { return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24; }

static uint8_t *slurp(const char *path, size_t *n)
{
    FILE *f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t *b = size > 0 ? malloc((size_t)size) : NULL;
    if (b && fread(b, 1, (size_t)size, f) != (size_t)size) { free(b); b = NULL; }
    fclose(f);
    *n = b ? (size_t)size : 0;
    return b;
}

static int by_offset(const void *a, const void *b)
{
    const rec_image *x = a, *y = b;
    return x->data < y->data ? -1 : x->data > y->data;
}

int rec_load(const char *path, rec_file *out)
{
    memset(out, 0, sizeof *out);
    size_t n;
    uint8_t *raw = slurp(path, &n);
    if (!raw || n < 7 || memcmp(raw, "SPREC1\n", 7) != 0) { free(raw); return -1; }
    out->stream = malloc(n);
    size_t pos = 7, len = 0;
    while (pos + 13 <= n) {
        uint32_t size = le32(raw + pos + 9);
        if (size > n - pos - 13) break;
        if (raw[pos] == 0) { memcpy(out->stream + len, raw + pos + 13, size); len += size; }
        pos += 13 + (size_t)size;
    }
    free(raw);
    out->stream_len = len;

    int cap = 1024;
    out->images = malloc(sizeof(rec_image) * (size_t)cap);
    static const struct { const char *magic; int types[2]; } kinds[] = {
        { "QUIC", { REC_QUIC, REC_QUIC } },
        { "  ZL", { REC_LZ_RGB, REC_GLZ_RGB } },
    };
    const uint8_t *s = out->stream;
    for (size_t k = 0; k < 2; k++) {
        for (size_t i = 22; i + 4 <= len; i++) {
            if (memcmp(s + i, kinds[k].magic, 4) != 0) continue;
            const uint8_t *d = s + i - 22;
            int type = d[8];
            uint32_t w = le32(d + 10), h = le32(d + 14), size = le32(d + 18);
            if (type != kinds[k].types[0] && type != kinds[k].types[1]) continue;
            if (w == 0 || h == 0 || w > 8192 || h > 8192 || size <= 16 || size > len - i) continue;
            if (out->count == cap) { cap *= 2; out->images = realloc(out->images, sizeof(rec_image) * (size_t)cap); }
            out->images[out->count++] = (rec_image){ type, w, h, s + i, size };
        }
    }
    qsort(out->images, (size_t)out->count, sizeof(rec_image), by_offset);
    return 0;
}

void rec_free(rec_file *f)
{
    free(f->stream);
    free(f->images);
    memset(f, 0, sizeof *f);
}
