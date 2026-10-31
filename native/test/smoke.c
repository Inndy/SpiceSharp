#include "spice_codec.h"
#include "lz4.h"
#include <stdio.h>
#include <string.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr,"failed line %d: %s\n",__LINE__,#x); return 1; } } while(0)

static void put_be32(uint8_t *p, uint32_t v) { p[0]=(uint8_t)(v>>24); p[1]=(uint8_t)(v>>16); p[2]=(uint8_t)(v>>8); p[3]=(uint8_t)v; }
static uint32_t get_be32(const uint8_t *p) { return (uint32_t)p[0]<<24|(uint32_t)p[1]<<16|(uint32_t)p[2]<<8|p[3]; }

/* SPICE LZ4 image data after the 2-byte header: [u32 BE size][block] records of one LZ4 stream. */
static int lz4_tests(void) {
    enum { W=61, H=42, BPP=4, ROWS=14, BLOCKS=H/ROWS, BLOCK=W*BPP*ROWS, SIZE=W*BPP*H };
    static uint8_t pixels[SIZE], decoded[SIZE+16], framed[SIZE*2], bad[SIZE*2];
    /* Every block repeats the first one's pseudo-random rows, so blocks 2 and 3 compress only through
       references into earlier blocks: decoding must keep one stream across records. */
    uint32_t seed=12345;
    for (int i=0;i<BLOCK;i++) { seed=seed*1103515245u+12345u; pixels[i]=(uint8_t)(seed>>16); }
    for (int b=1;b<BLOCKS;b++) memcpy(pixels+b*BLOCK,pixels,BLOCK);
    LZ4_stream_t stream; LZ4_initStream(&stream,sizeof stream);
    size_t n=0, second=0;
    for (int b=0;b<BLOCKS;b++) {
        int c=LZ4_compress_fast_continue(&stream,(const char*)pixels+b*BLOCK,(char*)framed+n+4,BLOCK,(int)(sizeof framed-n-4),1);
        CHECK(c>0); put_be32(framed+n,(uint32_t)c);
        if (b==1) { second=n; CHECK(c<BLOCK/8); }
        n+=4+(size_t)c;
    }
    /* Block 2 on its own must not decode: it only refers back into block 1. */
    CHECK(LZ4_decompress_safe((const char*)framed+second+4,(char*)decoded,(int)get_be32(framed+second),BLOCK)!=BLOCK
          || memcmp(decoded,pixels+BLOCK,BLOCK)!=0);

    memset(decoded,0,sizeof decoded);
    CHECK(sc_lz4_decode(framed,n,decoded,sizeof decoded)==SIZE);
    CHECK(memcmp(decoded,pixels,SIZE)==0);
    CHECK(sc_lz4_decode(framed,n,decoded,SIZE)==SIZE);

    /* Truncated: empty, partial size field, size beyond src, partial later record. */
    CHECK(sc_lz4_decode(framed,0,decoded,SIZE)==-1);
    CHECK(sc_lz4_decode(framed,3,decoded,SIZE)==-1);
    CHECK(sc_lz4_decode(framed,n-1,decoded,SIZE)==-1);
    CHECK(sc_lz4_decode(framed,second+2,decoded,SIZE)==-1);
    CHECK(sc_lz4_decode(framed,second+4+1,decoded,SIZE)==-1);
    /* Output overflow. */
    CHECK(sc_lz4_decode(framed,n,decoded,SIZE-1)==-1);
    CHECK(sc_lz4_decode(framed,n,decoded,0)==-1);
    CHECK(sc_lz4_decode(NULL,n,decoded,SIZE)==-1);
    CHECK(sc_lz4_decode(framed,n,NULL,SIZE)==-1);
    /* Corrupt: zero-length and oversized records. */
    memcpy(bad,framed,n); put_be32(bad+second,0);
    CHECK(sc_lz4_decode(bad,n,decoded,SIZE)==-1);
    memcpy(bad,framed,n); put_be32(bad+second,0xFFFFFFFFu);
    CHECK(sc_lz4_decode(bad,n,decoded,SIZE)==-1);
    /* Corrupt: a match reaching before the start of the stream, alone and after a valid block. */
    static const uint8_t far_match[]={0,0,0,3, 0x00,0xFF,0xFF};
    CHECK(sc_lz4_decode(far_match,sizeof far_match,decoded,SIZE)==-1);
    memcpy(bad,framed,second); memcpy(bad+second,far_match,sizeof far_match);
    CHECK(sc_lz4_decode(bad,second+sizeof far_match,decoded,SIZE)==-1);
    /* Corrupt: garbage after a valid block (a literal run longer than the record). */
    memcpy(bad,framed,second); put_be32(bad+second,2); bad[second+4]=0xF0; bad[second+5]=0xFF;
    CHECK(sc_lz4_decode(bad,second+6,decoded,SIZE)==-1);
    puts("LZ4 multi-block stream and malformed-input checks passed");
    return 0;
}

int main(void) {
    enum { W=73,H=41 };
    uint8_t pixels[W*H*4], decoded[W*H*4], compressed[W*H*16];
    for (int i=0;i<W*H;i++) { pixels[i*4]=(uint8_t)i; pixels[i*4+1]=(uint8_t)(i/3); pixels[i*4+2]=(uint8_t)(i/7); pixels[i*4+3]=0; }
    for (int codec=0;codec<2;codec++) {
        int w=0,h=0,top=0; sc_image_type type=SC_IMAGE_INVALID;
        int n=codec ? sc_lz_encode_rgb32(pixels,W,H,W*4,compressed,sizeof compressed) : sc_quic_encode_rgb32(pixels,W,H,W*4,compressed,sizeof compressed);
        CHECK(n>0); memset(decoded,0,sizeof decoded);
        if (codec) { sc_lz *c=sc_lz_create(); CHECK(c); CHECK(sc_lz_begin(c,compressed,n,NULL,0,&w,&h,&type,&top)==0); CHECK(w==W && h==H && type==SC_IMAGE_RGB32); CHECK(sc_lz_decode(c,decoded,sizeof decoded)==0); sc_lz_destroy(c); }
        else { sc_quic *c=sc_quic_create(); CHECK(c); CHECK(sc_quic_begin(c,compressed,n,&w,&h,&type)==0); CHECK(w==W && h==H && type==SC_IMAGE_RGB32); CHECK(sc_quic_decode(c,decoded,W*4)==0); sc_quic_destroy(c); }
        for(int i=0;i<W*H;i++) CHECK(memcmp(pixels+i*4,decoded+i*4,3)==0);
    }
    puts("QUIC and LZ RGB32 round trips passed");
    if (lz4_tests()) return 1;
    /* libavcodec is linked and refuses junk; pictures are checked by Spice.Native.Tests against tests/fixtures/h264. */
    sc_h264 *v=sc_h264_open(2); CHECK(v);
    sc_h264_picture info; uint8_t junk[64]; for (int i=0;i<64;i++) junk[i]=(uint8_t)(i*37+11);
    int r=sc_h264_decode(v,junk,sizeof junk,&info); CHECK(r==SC_H264_INVALID || r==SC_H264_NONE);
    CHECK(sc_h264_decode(v,junk,0,&info)==SC_H264_INVALID);
    CHECK(sc_h264_convert(v,decoded,sizeof decoded,W*4,0)==-1);
    sc_h264_close(v);
    puts("H.264 open, junk and close checks passed");
    return 0;
}
