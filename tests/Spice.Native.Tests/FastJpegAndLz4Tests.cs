using Spice.Media;
using Spice.Tests;
using Xunit.Abstractions;

namespace Spice.Native.Tests;

// docs/decisions/0028: the fast JPEG switch on the decoder handle, and the sc_lz4_decode binding.
public sealed unsafe class FastJpegAndLz4Tests(ITestOutputHelper output)
{
    const int TjSamp420 = 2, TjPfBgrx = 3;

    /// <summary>
    /// A 4:2:0 JPEG of smooth colour gradients with a little noise and a disc, roughly like desktop video: both fast IDCT
    /// and fast upsampling change pixels, but only slightly.
    /// </summary>
    static byte[] EncodeJpeg(int w, int h)
    {
        var rng = new Random(1);
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool disc = (x - w / 2) * (x - w / 2) + (y - h / 2) * (y - h / 2) < h * h / 9;
                pixels[i] = (byte)(disc ? 60 : x * 250 / w + rng.Next(4));
                pixels[i + 1] = (byte)(disc ? 90 : y * 250 / h + rng.Next(4));
                pixels[i + 2] = (byte)(disc ? 200 : (x + y) * 120 / (w + h));
                pixels[i + 3] = 255;
            }
        nint compressor = CodecApi.tj3Init(0), jpeg = 0; nuint size = 0;
        Assert.NotEqual(0, compressor);
        try
        {
            Assert.Equal(0, CodecApi.tj3Set(compressor, CodecApi.TJPARAM_QUALITY, 95));
            Assert.Equal(0, CodecApi.tj3Set(compressor, CodecApi.TJPARAM_SUBSAMP, TjSamp420));
            fixed (byte* src = pixels) Assert.Equal(0, CodecApi.tj3Compress8(compressor, src, w, w * 4, h, TjPfBgrx, ref jpeg, ref size));
            return new ReadOnlySpan<byte>((void*)jpeg, (int)size).ToArray();
        }
        finally { if (jpeg != 0) CodecApi.tj3Free(jpeg); CodecApi.tj3Destroy(compressor); }
    }

    [Fact]
    public void FastJpegChangesPixelsSlightlyAndSwitchesBackToTheAccurateDecoder()
    {
        const int w = 256, h = 144;
        byte[] jpeg = EncodeJpeg(w, h);
        using var decoder = new JpegDecoder();
        Assert.False(decoder.Fast);
        byte[] Decode()
        {
            var dst = new byte[w * h * 4];
            Assert.True(decoder.TryDecode(jpeg, dst, w, h, w * 4));
            return dst;
        }
        byte[] accurate = Decode();
        decoder.Fast = true;
        Assert.True(decoder.Fast);
        byte[] fast = Decode();
        byte[] fastAgain = Decode();
        decoder.Fast = false;
        byte[] accurateAgain = Decode();

        Assert.Equal(accurate, accurateAgain);
        Assert.Equal(fast, fastAgain);
        Assert.NotEqual(accurate, fast);
        int maxDiff = 0, changed = 0;
        long sum = 0;
        for (int i = 0; i < fast.Length; i++)
        {
            if (i % 4 == 3) { Assert.Equal(255, fast[i]); continue; }
            int d = Math.Abs(fast[i] - accurate[i]);
            maxDiff = Math.Max(maxDiff, d);
            sum += d;
            if (d != 0) changed++;
        }
        double mean = (double)sum / (w * h * 3);
        output.WriteLine($"fast vs accurate: {changed} of {w * h * 3} channel values differ, mean difference {mean:F2}, max {maxDiff}");
        // Small on average; the largest differences sit on the disc's saturated colour edge, where fast upsampling
        // replicates chroma instead of interpolating it.
        Assert.InRange(mean, 0.01, 2.0);

        using var stream = new MjpegStreamDecoder();
        stream.Fast = true;
        Assert.True(stream.Fast);
        var frame = new byte[w * h * 4];
        Assert.True(stream.TryDecodeFrame(jpeg, frame, w, h, w * 4));
        Assert.Equal(fast, frame);
    }

    /// <summary>Repetitive enough to make matches (including long and overlapping ones), with some noise.</summary>
    static byte[] Sample(int length, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (i / 97 % 3) switch { 0 => 0x5A, 1 => (byte)(i % 7), _ => (byte)rng.Next(4) };
        return data;
    }

    [Lz4NativeTheory]
    [InlineData(1, int.MaxValue, true)]
    [InlineData(12, int.MaxValue, true)]
    [InlineData(13, int.MaxValue, true)]
    [InlineData(1000, 100, false)]
    [InlineData(1000, 100, true)]
    [InlineData(200_000, 4096, true)]
    [InlineData(200_000, int.MaxValue, true)]
    public void NativeDecodeRoundTripsTheTestEncoder(int length, int chunk, bool linked)
    {
        byte[] raw = Sample(length, length);
        byte[] frames = Lz4TestEncoder.Frames(raw, chunk, linked);
        output.WriteLine($"{length} B in chunks of {chunk} ({(linked ? "linked" : "independent")}): {frames.Length} B compressed");
        var dst = new byte[length];
        Assert.Equal(length, Lz4Decoder.Decode(frames, dst));
        Assert.Equal(raw, dst);
        // Room to spare: the decoded size is still reported exactly.
        var roomy = new byte[length + 64];
        Assert.Equal(length, Lz4Decoder.Decode(frames, roomy));
        Assert.Equal(raw, roomy[..length]);
    }

    [Lz4NativeFact]
    public void NativeDecodeRejectsMalformedInput()
    {
        byte[] raw = Sample(5000, 3);
        byte[] frames = Lz4TestEncoder.Frames(raw, 1000);
        var dst = new byte[raw.Length];
        Assert.Equal(-1, Lz4Decoder.Decode([], dst));
        Assert.Equal(-1, Lz4Decoder.Decode(frames, []));
        Assert.Equal(-1, Lz4Decoder.Decode(frames, new byte[raw.Length - 1])); // output doesn't fit
        Assert.Equal(-1, Lz4Decoder.Decode(frames[..^1], dst)); // block cut short
        Assert.Equal(-1, Lz4Decoder.Decode(frames[..3], dst)); // size word cut short
        byte[] oversized = (byte[])frames.Clone();
        oversized[0] = 0x7F; // first block claims more bytes than there are
        Assert.Equal(-1, Lz4Decoder.Decode(oversized, dst));
        byte[] zero = [0, 0, 0, 0, .. frames];
        Assert.Equal(-1, Lz4Decoder.Decode(zero, dst)); // an empty block
        // A match reaching before the start of the output.
        // Token: 1 literal, a 4-byte match at offset 255 (only 1 byte decoded so far), then 3 closing literals.
        byte[] badOffset = [0, 0, 0, 8, 0x10, 0xAA, 0xFF, 0x00, 0x30, 0xBB, 0xBB, 0xBB];
        Assert.Equal(-1, Lz4Decoder.Decode(badOffset, dst));
        var rng = new Random(9);
        for (int i = 0; i < 200; i++)
        {
            var junk = new byte[rng.Next(2, 300)];
            rng.NextBytes(junk);
            junk[0] = 0; junk[1] = 0; // a plausible size word
            int n = Lz4Decoder.Decode(junk, dst);
            Assert.InRange(n, -1, dst.Length);
        }
    }
}
