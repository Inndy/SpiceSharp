/* Vendored QUIC/LZ encoders for the difference test; see encoders.c. Not upstream code (MIT). */
#ifndef DT_ENCODERS_H
#define DT_ENCODERS_H
#include <stdint.h>
#include <stddef.h>

/* QuicImageType values (vendor/common/quic.h) and LzImageType values (vendor/common/lz_common.h). */
enum { Q_GRAY = 1, Q_RGB16, Q_RGB24, Q_RGB32, Q_RGBA };
enum { L_PLT1_LE = 1, L_PLT1_BE, L_PLT4_LE, L_PLT4_BE, L_PLT8, L_RGB16, L_RGB24, L_RGB32, L_RGBA, L_XXXA, L_A8 };

/* Both return the encoded byte count, or -1 if the encoder rejected the image or ran out of room. */
int enc_quic(int type, int width, int height, const uint8_t *lines, int stride, uint8_t *out, size_t cap);
int enc_lz(int type, int width, int height, int top_down, const uint8_t *lines, int stride, uint8_t *out, size_t cap);
#endif
