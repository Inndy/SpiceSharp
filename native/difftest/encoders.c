/* The vendored QUIC and LZ encoders, linked statically into the difference test so it can produce every image
   type they support (the DLL only exports RGB32 and XXXA test encoders). run.sh compiles this against the
   reference tree, so the inputs don't depend on the code under test. Not upstream code (MIT). */
#include "spice_common.h"
#include "common/quic.h"
#include "common/lz.h"
#include "encoders.h"
#include <stdarg.h>

/* The shim's spice_assert/spice_error land here, as they do in codec_bridge.c. */
_Thread_local jmp_buf *sc_fatal_env;
_Thread_local const uint8_t *sc_glz_input_end;
void sc_fatal(const char *fmt, ...)
{
    (void)fmt;
    if (sc_fatal_env) longjmp(*sc_fatal_env, 1);
    abort();
}

SPICE_GNUC_NORETURN static void q_error(QuicUsrContext *u, const char *fmt, ...) { (void)u; sc_fatal(fmt); abort(); }
static void q_msg(QuicUsrContext *u, const char *fmt, ...) { (void)u; (void)fmt; }
static void *q_malloc(QuicUsrContext *u, int n) { (void)u; return malloc((size_t)n); }
static void q_free(QuicUsrContext *u, void *p) { (void)u; free(p); }
static int q_more_space(QuicUsrContext *u, uint32_t **io, int rows) { (void)u; (void)io; (void)rows; return 0; }
static int q_more_lines(QuicUsrContext *u, uint8_t **lines) { (void)u; (void)lines; return 0; }

SPICE_GNUC_NORETURN static void l_error(LzUsrContext *u, const char *fmt, ...) { (void)u; sc_fatal(fmt); abort(); }
static void l_msg(LzUsrContext *u, const char *fmt, ...) { (void)u; (void)fmt; }
static void *l_malloc(LzUsrContext *u, int n) { (void)u; return malloc((size_t)n); }
static void l_free(LzUsrContext *u, void *p) { (void)u; free(p); }
static int l_more_space(LzUsrContext *u, uint8_t **io) { (void)u; (void)io; return 0; }
static int l_more_lines(LzUsrContext *u, uint8_t **lines) { (void)u; (void)lines; return 0; }

int enc_quic(int type, int width, int height, const uint8_t *lines, int stride, uint8_t *out, size_t cap)
{
    QuicUsrContext usr = { q_error, q_msg, q_msg, q_malloc, q_free, q_more_space, q_more_lines };
    QuicContext *q = quic_create(&usr);
    if (!q) return -1;
    volatile int words = -1;
    jmp_buf env;
    jmp_buf *prev = sc_fatal_env;
    sc_fatal_env = &env;
    if (!setjmp(env)) {
        /* quic_encode takes the first line and walks by stride, so a bottom-up source passes its last row. */
        words = quic_encode(q, (QuicImageType)type, width, height, (uint8_t *)(uintptr_t)lines, (unsigned)height,
                            stride, (uint32_t *)(void *)out, (unsigned)(cap / 4));
    }
    sc_fatal_env = prev;
    quic_destroy(q);
    return words > 0 ? words * 4 : -1;
}

int enc_lz(int type, int width, int height, int top_down, const uint8_t *lines, int stride, uint8_t *out, size_t cap)
{
    LzUsrContext usr = { l_error, l_msg, l_msg, l_malloc, l_free, l_more_space, l_more_lines };
    LzContext *z = lz_create(&usr);
    if (!z) return -1;
    volatile int n = -1;
    jmp_buf env;
    jmp_buf *prev = sc_fatal_env;
    sc_fatal_env = &env;
    if (!setjmp(env)) {
        n = lz_encode(z, (LzImageType)type, width, height, top_down, (uint8_t *)(uintptr_t)lines,
                      (unsigned)height, stride, out, (unsigned)cap);
    }
    sc_fatal_env = prev;
    lz_destroy(z);
    return n > 0 ? n : -1;
}
