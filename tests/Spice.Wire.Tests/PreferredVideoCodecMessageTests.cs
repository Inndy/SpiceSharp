namespace Spice.Wire.Tests;

// SPICE_MSGC_DISPLAY_PREFERRED_VIDEO_CODEC_TYPE (spice-protocol enums.h; spice.proto "message { uint8 num_of_codecs;
// video_codec_type codecs[num_of_codecs] @end; } @declare preferred_video_codec_type") and the codec capabilities
// (protocol.h SPICE_DISPLAY_CAP_*), docs/decisions/0037.
public sealed class PreferredVideoCodecMessageTests
{
    [Fact]
    public void NumbersMatchSpiceProtocol()
    {
        // enum { SPICE_MSGC_DISPLAY_INIT = 101, STREAM_REPORT, PREFERRED_COMPRESSION, GL_DRAW_DONE, PREFERRED_VIDEO_CODEC_TYPE }
        Assert.Equal((ushort)105, DisplayMsg.CPreferredVideoCodecType);
        // SpiceVideoCodecType: MJPEG = 1, VP8, H264, VP9, H265
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 },
            new[] { VideoCodecType.Mjpeg, VideoCodecType.Vp8, VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.H265 }.Select(c => (byte)c));
        // ... MULTI_CODEC = 8, CODEC_MJPEG, CODEC_VP8, CODEC_H264, PREF_VIDEO_CODEC_TYPE, CODEC_VP9, CODEC_H265
        Assert.Equal(9, ClientMsg.CodecCap(VideoCodecType.Mjpeg));
        Assert.Equal(10, ClientMsg.CodecCap(VideoCodecType.Vp8));
        Assert.Equal(11, ClientMsg.CodecCap(VideoCodecType.H264));
        Assert.Equal(12, DisplayCap.PrefVideoCodecType);
        Assert.Equal(13, ClientMsg.CodecCap(VideoCodecType.Vp9));
        Assert.Equal(14, ClientMsg.CodecCap(VideoCodecType.H265));
        Assert.Equal(-1, ClientMsg.CodecCap((VideoCodecType)6));
        Assert.Equal(-1, ClientMsg.CodecCap(0));
    }

    [Fact]
    public void BodyIsACountThenOneBytePerCodec()
    {
        Span<byte> b = stackalloc byte[8];
        b.Fill(0xAA);
        int n = ClientMsg.PreferredVideoCodecType(b, [VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.Mjpeg]);
        Assert.Equal(4, n);
        Assert.Equal(new byte[] { 3, 3, 4, 1, 0xAA, 0xAA, 0xAA, 0xAA }, b.ToArray());
    }

    [Fact]
    public void AnEmptyListIsRefused()
    {
        var b = new byte[4];
        Assert.Throws<ArgumentOutOfRangeException>(() => ClientMsg.PreferredVideoCodecType(b, []));
    }
}
