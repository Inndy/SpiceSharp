/* Replaces glib and spice-common's logging/allocation layer for the vendored codecs.
   Nothing here is upstream code. */
#ifndef SC_SHIM_H
#define SC_SHIM_H
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <setjmp.h>
#include <spice/macros.h>

/* Fatal-path trampoline: codec_bridge.c installs a jmp_buf before every decode, so a corrupt
   stream unwinds to an error return instead of aborting the process. */
extern _Thread_local jmp_buf *sc_fatal_env;
void sc_fatal(const char *fmt, ...);

#define spice_error(...)    sc_fatal(__VA_ARGS__)
#define spice_critical(...) sc_fatal(__VA_ARGS__)
#define spice_warning(...)  ((void)0)
#define spice_debug(...)    ((void)0)
#define spice_info(...)     ((void)0)
#define spice_printerr(...) ((void)0)
#define spice_assert(x)     do { if (!(x)) sc_fatal("assert: " #x); } while (0)
#define spice_extra_assert(x) spice_assert(x)
/* Upstream logs and returns here, which leaves the caller to continue with unvalidated state — in
   decode_header() a bad magic returns early and decode() then indexes DECODE_TO_RGB32[0], a NULL
   function pointer. We are fed these bytes by a possibly hostile server, so a failed precondition
   unwinds to an error return instead. Every entry point in codec_bridge.c installs the guard. */
#define spice_return_if_fail(x) do { if (!(x)) sc_fatal("precondition: " #x); } while (0)
#define spice_return_val_if_fail(x, v) do { if (!(x)) sc_fatal("precondition: " #x); } while (0)
#define spice_warn_if_fail(x) ((void)0)
#define spice_warn_if_reached() ((void)0)

/* The codecs only ever run little-endian here (arm64 + x86_64). */
#define GUINT32_TO_LE(x)   (x)
#define GUINT32_FROM_LE(x) (x)
#define GUINT16_TO_LE(x)   (x)
#define GUINT16_FROM_LE(x) (x)

/* spice_malloc/spice_new/... are the real upstream ones from vendor/common/mem.c — not redefined
   here, or the declarations in mem.h would not parse. Only the glib spellings need mapping. */
#define g_malloc(n)          malloc(n)
#define g_malloc0(n)         calloc(1, (n))
#define g_free(p)            free(p)
#define g_new(t, n)          ((t *)malloc(sizeof(t) * (size_t)(n)))
#define g_new0(t, n)         ((t *)calloc((size_t)(n), sizeof(t)))
#define g_warning(...)       ((void)0)
#define g_debug(...)         ((void)0)
#define g_return_if_fail(x)  spice_return_if_fail(x)
#define g_return_val_if_fail(x, v) spice_return_val_if_fail(x, v)

#define G_UNLIKELY(x)        __builtin_expect(!!(x), 0)
#define G_LIKELY(x)          __builtin_expect(!!(x), 1)
#define G_GNUC_UNUSED        __attribute__((unused))
#ifndef SPICE_GNUC_UNUSED    /* spice/macros.h defines this one */
#define SPICE_GNUC_UNUSED    __attribute__((unused))
#endif

#ifndef MIN
#define MIN(a, b) ((a) < (b) ? (a) : (b))
#define MAX(a, b) ((a) > (b) ? (a) : (b))
#endif
#endif
