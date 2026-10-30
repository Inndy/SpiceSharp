using Spice.Session;
using Spice.Wire;

namespace Spice.Replay.Tests;

// Review of 9c81fd6. Synthetic syntax only; no recording or external decoder.
public sealed class VideoBitstreamFollowupReviewTests
{
    public static TheoryData<string, byte[]> InvalidSpsPrefixes => new()
    {
        { "forbidden_zero_bit", [0, 0, 1, 0xE7, 100, 0, 31, 0xAC] },
        { "reserved_zero_2bits", [0, 0, 1, 0x67, 100, 3, 31, 0xAC] },
        // ue(32) = 00000100001; SPS IDs are restricted to 0..31 (7.4.2.1.1).
        { "sps_id_32", [0, 0, 1, 0x67, 100, 0, 31, 0x04, 0x2B, 0x80] },
        // 7.3.1: the byte following emulation_prevention_three_byte must be <= 3.
        // The original EmulationPreventionBytesAreSkipped test uses this malformed escape too.
        { "illegal_emulation_prevention_follower", [0, 0, 1, 0x67, 100, 0, 0, 3, 0xAC] },
        // SPS NAL ends immediately after profile_idc. The next NAL's prefix/header must not supply SPS fields.
        { "next_nal_is_not_sps_payload", [0, 0, 1, 0x67, 244, 0, 0, 1, 0x68, 0xFF] },
        // sps_id=0, chroma=1, luma_minus8=0, followed by an unterminated chroma_minus8 ue(v).
        { "truncated_chroma_bit_depth", [0, 0, 1, 0x67, 100, 0, 31, 0xA8] },
        // Same prefix with chroma_minus8=7, outside 0..6.
        { "invalid_chroma_bit_depth", [0, 0, 1, 0x67, 100, 0, 31, 0xA8, 0x80] },
    };

    [Theory]
    [MemberData(nameof(InvalidSpsPrefixes))]
    public void MalformedFieldsUsedForTheFormatDoNotDeclareAFormat(string reason, byte[] frame)
        => Assert.False(VideoBitstream.TryReadFormat(VideoCodecType.H264, frame, out _), reason);

    // Prefix reads intentionally stop before scaling lists (7.3.2.1.1). A real list, including the
    // nextScale=0/default-matrix case, must not move or change the preceding format fields.
    [Theory]
    [InlineData(100, 1, false)]
    [InlineData(100, 1, true)]
    [InlineData(244, 3, false)]
    [InlineData(244, 3, true)]
    public void ScalingListsAfterTheDepthFieldsDoNotChangeTheFormat(int profile, int chroma, bool scaling)
    {
        string bits = "1" + Ue(chroma) + (chroma == 3 ? "0" : "") + "11" + "0";
        bits += scaling ? "1" + "1" + Ue(16) + new string('0', chroma == 3 ? 11 : 7) : "0";
        // delta_scale=-8 maps to se(v) codeNum 16. nextScale=0 needs no further deltas.
        bits += "1111" + "0" + "11" + "11" + "00"; // frame_num, POC, refs, gaps, dimensions, frame/direct/crop/VUI
        byte[] frame = Sps(profile, bits);
        Assert.True(VideoBitstream.TryReadFormat(VideoCodecType.H264, frame, out var actual));
        Assert.Equal(new VideoFormat(VideoCodecType.H264, profile, chroma, 8), actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vp9ProfileBitsHaveTheirSpecifiedOrder(int profile)
    {
        byte header = (byte)(0x80 | ((profile & 1) << 5) | ((profile >> 1) << 4));
        Assert.True(VideoBitstream.TryReadFormat(VideoCodecType.Vp9, [header], out var actual));
        Assert.Equal(profile, actual.Profile);
    }

    [Fact]
    public void BoundedGarbageAndTruncationDoNotThrowOrProduceOutOfRangeH264Fields()
    {
        var random = new Random(0x264);
        for (int length = 0; length <= 96; length++)
        {
            byte[] frame = new byte[length];
            for (int pass = 0; pass < 64; pass++)
            {
                random.NextBytes(frame);
                if (length >= 4 && pass % 2 == 0) { frame[0] = 0; frame[1] = 0; frame[2] = 1; frame[3] = 0x67; }
                if (VideoBitstream.TryReadFormat(VideoCodecType.H264, frame, out var f))
                {
                    Assert.InRange(f.ChromaFormat, 0, 3);
                    Assert.InRange(f.BitDepth, 8, 14);
                }
                VideoBitstream.TryReadFormat(VideoCodecType.Vp9, frame, out _);
            }
        }
        // ue(v) runs of 17+ zeros, all-zeros, and a final escape byte: no overflow or unbounded loop.
        foreach (int length in new[] { 0, 1, 2, 3, 16, 256 })
        {
            byte[] frame = [0, 0, 1, 0x67, 100, 0, 31, .. new byte[length]];
            Assert.False(VideoBitstream.TryReadFormat(VideoCodecType.H264, frame, out _));
        }
        Assert.False(VideoBitstream.TryReadFormat(VideoCodecType.H264, [0, 0, 1, 0x67, 100, 0, 0, 3], out _));
    }

    static string Ue(int value)
    {
        string binary = Convert.ToString(value + 1, 2);
        return new string('0', binary.Length - 1) + binary;
    }

    static byte[] Sps(int profile, string bits)
    {
        bits += "1"; // rbsp_stop_one_bit
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        var payload = new List<byte> { (byte)profile, 0, 31 };
        for (int i = 0; i < bits.Length; i += 8) payload.Add(Convert.ToByte(bits.Substring(i, 8), 2));
        var result = new List<byte> { 0, 0, 1, 0x67 };
        int zeros = 0;
        foreach (byte b in payload)
        {
            if (zeros == 2 && b <= 3) { result.Add(3); zeros = 0; }
            result.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();
    }
}
