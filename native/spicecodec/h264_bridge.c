/* SpiceSharp: software H.264 decoding with FFmpeg's libavcodec (docs/decisions/0037, 2026-10-31 amendment).
   MIT, like the rest of the wrapper; libavcodec and libavutil are LGPL-2.1-or-later and linked statically into this
   (replaceable) DLL. The decoder takes one access unit per call, as spice-server's x264enc sends them, keeps the newest
   picture, and converts it to opaque BGRA on request. Nothing here allocates per frame; libavcodec pools its picture
   buffers and copies each packet into a padded buffer of its own. */
#include "spice_codec.h"

#include <stdlib.h>
#include <string.h>

#include <libavcodec/avcodec.h>
#include <libavutil/frame.h>
#include <libavutil/log.h>
#include <libavutil/pixdesc.h>
#include <libavutil/pixfmt.h>

struct sc_h264 {
    AVCodecContext *ctx;
    AVPacket *pkt;
    AVFrame *frame;   /* the newest picture, valid while has_picture */
    AVFrame *spare;   /* receives the next one */
    int has_picture;
};

SC_API sc_h264 *SC_CALL sc_h264_open(int threads)
{
    const AVCodec *codec = avcodec_find_decoder(AV_CODEC_ID_H264);
    if (!codec) return NULL;
    /* libavcodec logs to stderr by default; the viewer has none, and corrupt streams are reported by return codes. */
    av_log_set_level(AV_LOG_QUIET);
    sc_h264 *h = calloc(1, sizeof *h);
    if (!h) return NULL;
    h->ctx = avcodec_alloc_context3(codec);
    h->pkt = av_packet_alloc();
    h->frame = av_frame_alloc();
    h->spare = av_frame_alloc();
    if (!h->ctx || !h->pkt || !h->frame || !h->spare) goto fail;
    if (threads < 1) threads = 1;
    if (threads > 16) threads = 16;
    /* Slice threads only: frame threads hold each picture back by threads - 1 frames, so the last frame of an update
       would wait for the next one. spice-server's x264enc uses sliced threads, so its frames have several slices. Idle
       workers wait on a condition variable. */
    h->ctx->thread_count = threads;
    h->ctx->thread_type = FF_THREAD_SLICE;
    /* Output each picture as soon as it is decoded (x264's zerolatency streams have no reordering). */
    h->ctx->flags |= AV_CODEC_FLAG_LOW_DELAY;
    if (avcodec_open2(h->ctx, codec, NULL) < 0) goto fail;
    return h;
fail:
    sc_h264_close(h);
    return NULL;
}

SC_API void SC_CALL sc_h264_close(sc_h264 *h)
{
    if (!h) return;
    avcodec_free_context(&h->ctx);
    av_packet_free(&h->pkt);
    av_frame_free(&h->frame);
    av_frame_free(&h->spare);
    free(h);
}

static int supported_format(int f)
{
    switch (f) {
    case AV_PIX_FMT_YUV420P: case AV_PIX_FMT_YUVJ420P:
    case AV_PIX_FMT_YUV422P: case AV_PIX_FMT_YUVJ422P:
    case AV_PIX_FMT_YUV444P: case AV_PIX_FMT_YUVJ444P:
    case AV_PIX_FMT_GBRP: case AV_PIX_FMT_GRAY8:
        return 1;
    default:
        return 0;
    }
}

/* chroma_format_idc of any planar format, deeper ones included (for the caller's log): 0 gray, 1 4:2:0, 2 4:2:2, 3 4:4:4. */
static int chroma_of(int f)
{
    const AVPixFmtDescriptor *d = av_pix_fmt_desc_get(f);
    if (!d || d->nb_components < 3) return 0;
    if (d->log2_chroma_w == 1) return d->log2_chroma_h == 1 ? 1 : 2;
    return 3;
}

SC_API int SC_CALL sc_h264_decode(sc_h264 *h, const uint8_t *data, size_t len, sc_h264_picture *info)
{
    if (!h || !data || len == 0 || len > INT32_MAX) return SC_H264_INVALID;
    /* A packet without a reference-counted buffer: avcodec_send_packet copies it into a padded buffer, as its bitstream
       readers need AV_INPUT_BUFFER_PADDING_SIZE readable bytes after the data. */
    h->pkt->data = (uint8_t *)data;
    h->pkt->size = (int)len;
    int r = avcodec_send_packet(h->ctx, h->pkt);
    h->pkt->data = NULL;
    h->pkt->size = 0;
    if (r == AVERROR(ENOMEM)) return SC_H264_FATAL;
    if (r < 0 && r != AVERROR(EAGAIN)) return r == AVERROR_INVALIDDATA ? SC_H264_INVALID : SC_H264_FATAL;
    int got = 0;
    for (;;) {
        r = avcodec_receive_frame(h->ctx, h->spare);
        if (r == AVERROR(EAGAIN) || r == AVERROR_EOF) break;
        if (r == AVERROR_INVALIDDATA) return got ? SC_H264_PICTURE : SC_H264_INVALID;
        if (r < 0) return SC_H264_FATAL;
        /* Keep the newest picture; the one it replaces goes back to libavcodec's pool. */
        AVFrame *t = h->frame; h->frame = h->spare; h->spare = t;
        av_frame_unref(h->spare);
        got = 1;
    }
    if (!got) return SC_H264_NONE;
    h->has_picture = 1;
    if (info) {
        info->width = h->frame->width;
        info->height = h->frame->height;
        info->chroma = chroma_of(h->frame->format);
        info->supported = supported_format(h->frame->format);
        info->full_range = h->frame->color_range == AVCOL_RANGE_JPEG || h->frame->format == AV_PIX_FMT_YUVJ420P ||
            h->frame->format == AV_PIX_FMT_YUVJ422P || h->frame->format == AV_PIX_FMT_YUVJ444P;
        info->matrix = (int)h->frame->colorspace;
    }
    return SC_H264_PICTURE;
}

/* Fixed-point YCbCr to RGB, Q14. */
typedef struct { int yoff, ymul, rv, gu, gv, bu; } coef;

static coef coefficients(const AVFrame *f)
{
    double kr, kb;
    switch (f->colorspace) {
    case AVCOL_SPC_BT709: kr = 0.2126; kb = 0.0722; break;
    case AVCOL_SPC_BT470BG: case AVCOL_SPC_SMPTE170M: kr = 0.299; kb = 0.114; break;
    case AVCOL_SPC_SMPTE240M: kr = 0.212; kb = 0.087; break;
    case AVCOL_SPC_FCC: kr = 0.30; kb = 0.11; break;
    case AVCOL_SPC_BT2020_NCL: case AVCOL_SPC_BT2020_CL: kr = 0.2627; kb = 0.0593; break;
    default:
        /* Unspecified: BT.709 from 720 lines up, BT.601 below, as the hardware path assumes. */
        if (f->height >= 720) { kr = 0.2126; kb = 0.0722; } else { kr = 0.299; kb = 0.114; }
        break;
    }
    int full = f->color_range == AVCOL_RANGE_JPEG || f->format == AV_PIX_FMT_YUVJ420P ||
        f->format == AV_PIX_FMT_YUVJ422P || f->format == AV_PIX_FMT_YUVJ444P;
    double ys = full ? 1.0 : 255.0 / 219.0, cs = full ? 1.0 : 255.0 / 224.0, kg = 1.0 - kr - kb;
    const double q = 16384.0;
    coef c;
    c.yoff = full ? 0 : 16;
    c.ymul = (int)(ys * q + 0.5);
    c.rv = (int)(2.0 * (1.0 - kr) * cs * q + 0.5);
    c.bu = (int)(2.0 * (1.0 - kb) * cs * q + 0.5);
    c.gu = (int)(2.0 * (1.0 - kb) * kb / kg * cs * q + 0.5);
    c.gv = (int)(2.0 * (1.0 - kr) * kr / kg * cs * q + 0.5);
    return c;
}

static inline uint32_t clamp8(int v)
{
    return v < 0 ? 0u : v > 255 ? 255u : (uint32_t)v;
}

/* Written so the compiler vectorizes them (AVX2 on x64, NEON on ARM64). */
static void row444(const uint8_t *restrict y, const uint8_t *restrict u, const uint8_t *restrict v, uint32_t *restrict out,
                   int w, coef c)
{
    for (int x = 0; x < w; x++) {
        int yy = (y[x] - c.yoff) * c.ymul + 8192, uu = u[x] - 128, vv = v[x] - 128;
        uint32_t r = clamp8((yy + c.rv * vv) >> 14);
        uint32_t g = clamp8((yy - c.gu * uu - c.gv * vv) >> 14);
        uint32_t b = clamp8((yy + c.bu * uu) >> 14);
        out[x] = 0xFF000000u | r << 16 | g << 8 | b;
    }
}

/* 4:2:0 and 4:2:2 rows: each chroma sample covers two luma samples (nearest, as libjpeg-turbo's fast upsampling). */
static void row42x(const uint8_t *restrict y, const uint8_t *restrict u, const uint8_t *restrict v, uint32_t *restrict out,
                   int w, coef c)
{
    int pairs = w >> 1;
    for (int x = 0; x < pairs; x++) {
        int uu = u[x] - 128, vv = v[x] - 128;
        int dr = c.rv * vv, dg = -c.gu * uu - c.gv * vv, db = c.bu * uu;
        int y0 = (y[2 * x] - c.yoff) * c.ymul + 8192, y1 = (y[2 * x + 1] - c.yoff) * c.ymul + 8192;
        out[2 * x] = 0xFF000000u | clamp8((y0 + dr) >> 14) << 16 | clamp8((y0 + dg) >> 14) << 8 | clamp8((y0 + db) >> 14);
        out[2 * x + 1] = 0xFF000000u | clamp8((y1 + dr) >> 14) << 16 | clamp8((y1 + dg) >> 14) << 8 | clamp8((y1 + db) >> 14);
    }
    if (w & 1) {
        int uu = u[pairs] - 128, vv = v[pairs] - 128, y0 = (y[w - 1] - c.yoff) * c.ymul + 8192;
        out[w - 1] = 0xFF000000u | clamp8((y0 + c.rv * vv) >> 14) << 16 | clamp8((y0 - c.gu * uu - c.gv * vv) >> 14) << 8 |
            clamp8((y0 + c.bu * uu) >> 14);
    }
}

static void row_gbr(const uint8_t *restrict g, const uint8_t *restrict b, const uint8_t *restrict r, uint32_t *restrict out, int w)
{
    for (int x = 0; x < w; x++) out[x] = 0xFF000000u | (uint32_t)r[x] << 16 | (uint32_t)g[x] << 8 | b[x];
}

static void row_gray(const uint8_t *restrict y, uint32_t *restrict out, int w, coef c)
{
    for (int x = 0; x < w; x++) {
        uint32_t l = clamp8(((y[x] - c.yoff) * c.ymul + 8192) >> 14);
        out[x] = 0xFF000000u | l * 0x010101u;
    }
}

SC_API int SC_CALL sc_h264_convert(sc_h264 *h, uint8_t *dst, size_t dst_cap, int stride, int flags)
{
    if (!h || !h->has_picture || !dst) return -1;
    const AVFrame *f = h->frame;
    int w = f->width, ht = f->height;
    if (w <= 0 || ht <= 0 || !supported_format(f->format)) return -1;
    if (stride < w * 4 || (uint64_t)stride * (uint64_t)(ht - 1) + (uint64_t)w * 4 > dst_cap) return -1;
    coef c = coefficients(f);
    int vshift = f->format == AV_PIX_FMT_YUV420P || f->format == AV_PIX_FMT_YUVJ420P;
    int sub = chroma_of(f->format) == 1 || chroma_of(f->format) == 2;
    for (int row = 0; row < ht; row++) {
        uint32_t *out = (uint32_t *)(dst + (size_t)stride * (size_t)((flags & SC_H264_MIRROR) ? ht - 1 - row : row));
        const uint8_t *y = f->data[0] + (ptrdiff_t)f->linesize[0] * row;
        if (f->format == AV_PIX_FMT_GRAY8) { row_gray(y, out, w, c); continue; }
        int crow = vshift ? row >> 1 : row;
        const uint8_t *u = f->data[1] + (ptrdiff_t)f->linesize[1] * crow, *v = f->data[2] + (ptrdiff_t)f->linesize[2] * crow;
        if (f->format == AV_PIX_FMT_GBRP) row_gbr(y, u, v, out, w);
        else if (sub) row42x(y, u, v, out, w, c);
        else row444(y, u, v, out, w, c);
    }
    return 0;
}
