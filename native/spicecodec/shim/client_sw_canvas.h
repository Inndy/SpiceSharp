/* Just the decoder handles decode.h and decode-glz.c reference, lifted from spice-common's
   canvas_base.h so we do not have to vendor the whole pixman-dependent canvas. Not upstream code. */
#ifndef SC_CLIENT_SW_CANVAS_H
#define SC_CLIENT_SW_CANVAS_H

#include "spice_common.h"
#include "draw.h"
#include "lz_common.h"

typedef struct _SpiceGlzDecoder SpiceGlzDecoder;
typedef struct _SpiceJpegDecoder SpiceJpegDecoder;
typedef struct _SpiceZlibDecoder SpiceZlibDecoder;

typedef struct {
    void (*decode)(SpiceGlzDecoder *decoder, uint8_t *data, SpicePalette *plt, void *usr_data);
} SpiceGlzDecoderOps;

struct _SpiceGlzDecoder {
    SpiceGlzDecoderOps *ops;
};

#endif
