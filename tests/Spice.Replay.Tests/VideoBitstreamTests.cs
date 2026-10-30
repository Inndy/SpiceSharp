using Spice.Session;
using Spice.Wire;

namespace Spice.Replay.Tests;

// The declared format of a stream's first frame (docs/decisions/0037): spice-server's x264enc encodes 4:4:4 by
// default, which hardware decoders refuse, so the session reads the SPS before decoding.
public sealed class VideoBitstreamTests
{
    static VideoFormat? Read(VideoCodecType codec, params byte[] frame)
        => VideoBitstream.TryReadFormat(codec, frame, out var f) ? f : null;

    [Fact]
    public void H264HighFourFourFourIsRead()
    {
        // AUD, then SPS: profile 244, constraints 0, level 31, ue(0) sps id, ue(3) chroma, flag 0, ue(0) luma depth.
        Assert.Equal(new VideoFormat(VideoCodecType.H264, 244, 3, 8),
            Read(VideoCodecType.H264, 0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x67, 244, 0, 31, 0x91, 0x80));
        Assert.Equal("H.264 High 4:4:4 Predictive (profile 244), 4:4:4, 8-bit", Read(VideoCodecType.H264, 0, 0, 1, 0x67, 244, 0, 31, 0x91, 0x80).ToString());
    }

    [Fact]
    public void ProfilesWithoutChromaFieldsAreFourTwoZeroEightBit()
    {
        Assert.Equal(new VideoFormat(VideoCodecType.H264, 66, 1, 8), Read(VideoCodecType.H264, 0, 0, 1, 0x67, 66, 0xC0, 30, 0x80));
        Assert.Equal(new VideoFormat(VideoCodecType.H264, 77, 1, 8), Read(VideoCodecType.H264, 0, 0, 1, 0x27, 77, 0x40, 31, 0x80));
    }

    [Fact]
    public void TheLargerBitDepthIsTheFormatsDepth()
    {
        // High 10: chroma 1, luma and chroma bit_depth_minus8 2 (ue 011 each). Bits: 1 010 011 011 -> A6 C0.
        Assert.Equal(new VideoFormat(VideoCodecType.H264, 110, 1, 10), Read(VideoCodecType.H264, 0, 0, 1, 0x67, 110, 0, 40, 0xA6, 0xC0));
        // Luma 8-bit, chroma 10-bit: 1 010 1 011 -> AB.
        Assert.Equal(new VideoFormat(VideoCodecType.H264, 110, 1, 10), Read(VideoCodecType.H264, 0, 0, 1, 0x67, 110, 0, 40, 0xAB));
        // (A positive emulation-prevention case can't be built inside this prefix: a byte after 00 00 03 is at most 3,
        // which would begin seq_parameter_set_id with six zeros, past 31. The malformed escapes are in
        // VideoBitstreamFollowupReviewTests.)
    }

    [Fact]
    public void FramesWithoutAnSpsOrDamagedOnesDeclareNothing()
    {
        Assert.Null(Read(VideoCodecType.H264));
        Assert.Null(Read(VideoCodecType.H264, 1, 0xAA, 0xBB));
        Assert.Null(Read(VideoCodecType.H264, 0, 0, 1, 0x65, 0x88, 0x84)); // an IDR slice
        Assert.Null(Read(VideoCodecType.H264, 0, 0, 1, 0x67, 100, 0)); // cut short
        Assert.Null(Read(VideoCodecType.H264, 0, 0, 1)); // a start code at the very end
        Assert.Null(Read(VideoCodecType.H264, 0, 0, 1, 0x67, 100, 0, 31, 0, 0, 0, 0)); // an Exp-Golomb run past 16 bits
        Assert.Null(Read(VideoCodecType.H265, 0, 0, 1, 0x42, 1, 1)); // HEVC is not read
    }

    [Theory]
    [InlineData(0x82, 0, 1, 8)]
    [InlineData(0xA2, 1, 3, 8)]
    [InlineData(0x92, 2, 1, 10)]
    [InlineData(0xB2, 3, 3, 10)]
    public void Vp9ProfilesAreRead(byte first, int profile, int chroma, int depth)
    {
        Assert.Equal(new VideoFormat(VideoCodecType.Vp9, profile, chroma, depth), Read(VideoCodecType.Vp9, first, 0x49, 0x83));
        Assert.Null(Read(VideoCodecType.Vp9, 0x42)); // no frame marker
    }

    [Fact]
    public void ReadingAllocatesNothing()
    {
        byte[] frame = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x67, 244, 0, 31, 0x91, 0x80];
        VideoBitstream.TryReadFormat(VideoCodecType.H264, frame, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) VideoBitstream.TryReadFormat(VideoCodecType.H264, frame, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
