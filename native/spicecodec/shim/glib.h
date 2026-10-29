/* Stands in for glib so the vendored GLZ decoder needs no edits for it. Not upstream code. */
#ifndef SC_GLIB_H
#define SC_GLIB_H
#include "spice_common.h"
#include <stdbool.h>
#include <inttypes.h>

typedef int gboolean;
typedef void *gpointer;
#ifndef TRUE
#define TRUE 1
#define FALSE 0
#endif

#define G_BEGIN_DECLS
#define G_END_DECLS
#define G_GUINT64_FORMAT PRIu64
#define G_GINT64_FORMAT  PRId64

#define g_clear_pointer(pp, destroy) \
    do { if (*(pp)) { destroy(*(pp)); *(pp) = NULL; } } while (0)

#endif
