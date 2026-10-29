/* spice-gtk waits on a coroutine when a GLZ back-reference names an image that has not arrived yet,
   which happens when several displays share one decoder window. We drive one window from one actor
   and feed it in order, so the wait collapses to a single evaluation of the predicate: true if the
   image is already there, false otherwise. decode-glz.c's g_return_val_if_fail checks then reject
   the miss instead of blocking. Not upstream code. */
#ifndef SC_GIO_COROUTINE_H
#define SC_GIO_COROUTINE_H
#include "glib.h"

struct coroutine;
static inline struct coroutine *g_coroutine_self(void) { return NULL; }

static inline gboolean g_coroutine_condition_wait(struct coroutine *self,
                                                  gboolean (*condition)(gpointer),
                                                  gpointer data)
{
    (void)self;
    return condition(data);
}

#endif
