/* Pulls QUIC, LZ_RGB and GLZ_RGB image payloads out of a display-channel recording
   (tests/fixtures/<name>/display-0.spicerec, written by tools/spicerec). Not upstream code (MIT).

   A recording is "SPREC1\n" then records of [u8 direction][u64 timestamp][u32 length][bytes]; direction 0 is
   server to client. The server-to-client bytes are joined and scanned for the codec magic; a hit counts only if
   the 22 bytes before it are a plausible SpiceImageDescriptor (u64 id, u8 type, u8 flags, u32 width, u32 height)
   followed by the u32 data_size of SpiceQUICData/SpiceLZRGBData. Images come back in stream order, which GLZ needs:
   each GLZ image may refer to earlier ones through the shared window. */
#ifndef DT_SPICEREC_H
#define DT_SPICEREC_H
#include <stdint.h>
#include <stddef.h>

enum { REC_QUIC = 1, REC_LZ_RGB = 101, REC_GLZ_RGB = 102 };

typedef struct rec_image {
    int type;               /* REC_* */
    uint32_t width, height; /* from the descriptor */
    const uint8_t *data;    /* points into rec_file.stream */
    uint32_t len;
} rec_image;

typedef struct rec_file {
    uint8_t *stream;        /* joined server-to-client bytes */
    size_t stream_len;
    rec_image *images;
    int count;
} rec_file;

/* Returns 0 on success; the file must exist and start with the SPREC1 header. */
int rec_load(const char *path, rec_file *out);
void rec_free(rec_file *f);
#endif
