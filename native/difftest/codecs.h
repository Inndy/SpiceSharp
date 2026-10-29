/* Loads one spicecodec.dll build through GetProcAddress, so two builds (reference and patched) can be
   driven side by side in one process. Shared by difftest.c and ../bench/bench.c. Not upstream code (MIT). */
#ifndef DT_CODECS_H
#define DT_CODECS_H
#include <windows.h>
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include "spice_codec.h"   /* types only: the functions are resolved from each DLL */

typedef struct dt_codec {
    const char *path;
    HMODULE module;
    sc_quic *(__cdecl *quic_create)(void);
    void (__cdecl *quic_destroy)(sc_quic *);
    int (__cdecl *quic_begin)(sc_quic *, const uint8_t *, size_t, int *, int *, sc_image_type *);
    int (__cdecl *quic_decode)(sc_quic *, uint8_t *, int);
    int (__cdecl *quic_encode_rgb32)(const uint8_t *, int, int, int, uint8_t *, size_t);
    sc_lz *(__cdecl *lz_create)(void);
    void (__cdecl *lz_destroy)(sc_lz *);
    int (__cdecl *lz_begin)(sc_lz *, const uint8_t *, size_t, const uint32_t *, int, int *, int *, sc_image_type *, int *);
    int abi;   /* sc_abi_version(), 1 for builds without it; call sc_lz_decode through dt_lz_decode */
    int (__cdecl *lz_decode_v1)(sc_lz *, uint8_t *);
    int (__cdecl *lz_decode)(sc_lz *, uint8_t *, size_t);
    int (__cdecl *lz_encode_rgb32)(const uint8_t *, int, int, int, uint8_t *, size_t);
    sc_glz_window *(__cdecl *glz_window_create)(void);
    void (__cdecl *glz_window_clear)(sc_glz_window *);
    void (__cdecl *glz_window_destroy)(sc_glz_window *);
    sc_glz *(__cdecl *glz_create)(sc_glz_window *);
    void (__cdecl *glz_destroy)(sc_glz *);
    int (__cdecl *glz_decode)(sc_glz *, const uint8_t *, size_t, const uint8_t **, int *, int *, int *, int *);
} dt_codec;

static FARPROC dt_need(HMODULE m, const char *path, const char *name)
{
    FARPROC p = GetProcAddress(m, name);
    if (!p) { fprintf(stderr, "%s: missing export %s\n", path, name); exit(2); }
    return p;
}

/* Exits on failure: a harness without both builds can't say anything. */
static void dt_codec_load(dt_codec *c, const char *path)
{
    c->path = path;
    /* LOAD_WITH_ALTERED_SEARCH_PATH: two DLLs with the same file name must both load, each from its own folder. */
    c->module = LoadLibraryExA(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!c->module) { fprintf(stderr, "cannot load %s (error %lu)\n", path, GetLastError()); exit(2); }
#define DT_GET(field, name) *(FARPROC *)&c->field = dt_need(c->module, path, name)
    DT_GET(quic_create, "sc_quic_create"); DT_GET(quic_destroy, "sc_quic_destroy");
    DT_GET(quic_begin, "sc_quic_begin"); DT_GET(quic_decode, "sc_quic_decode");
    DT_GET(quic_encode_rgb32, "sc_quic_encode_rgb32");
    DT_GET(lz_create, "sc_lz_create"); DT_GET(lz_destroy, "sc_lz_destroy");
    DT_GET(lz_begin, "sc_lz_begin");
    FARPROC abi = GetProcAddress(c->module, "sc_abi_version");
    c->abi = abi ? ((int (__cdecl *)(void))(void *)abi)() : 1;
    if (c->abi == 1) DT_GET(lz_decode_v1, "sc_lz_decode"); else DT_GET(lz_decode, "sc_lz_decode");
    DT_GET(lz_encode_rgb32, "sc_lz_encode_rgb32");
    DT_GET(glz_window_create, "sc_glz_window_create"); DT_GET(glz_window_clear, "sc_glz_window_clear");
    DT_GET(glz_window_destroy, "sc_glz_window_destroy");
    DT_GET(glz_create, "sc_glz_create"); DT_GET(glz_destroy, "sc_glz_destroy");
    DT_GET(glz_decode, "sc_glz_decode");
#undef DT_GET
}

/* sc_lz_decode with each build's own signature: version 1 had no out_cap (and wrote padded palette rows). */
static int dt_lz_decode(const dt_codec *c, sc_lz *z, uint8_t *out, size_t cap)
{
    return c->abi == 1 ? c->lz_decode_v1(z, out) : c->lz_decode(z, out, cap);
}
#endif
