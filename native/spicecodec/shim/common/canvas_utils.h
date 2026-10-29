/* decode-glz.c includes this only for alloc_lz_image_surface, which is pixman-based. Our edited
   glz_image_new allocates a plain BGRA buffer instead, so nothing is needed here.
   Not upstream code. */
#ifndef SC_CANVAS_UTILS_H
#define SC_CANVAS_UTILS_H
#include "../spice_common.h"
#endif
