using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Spice.Native;
using Spice.Media;
using Spice.Wire;
using Spice.Tests;

namespace Spice.Native.Tests;

public sealed unsafe class CodecTests
{
    private const int W = 73, H = 41;
    private static byte[] Image()
    {
        var result = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++) { result[i * 4] = (byte)i; result[i * 4 + 1] = (byte)(i / 3); result[i * 4 + 2] = (byte)(i / 7); result[i * 4 + 3] = 255; }
        return result;
    }
    private static byte[] Encode(bool lz, byte[] pixels)
    {
        byte[] compressed = new byte[pixels.Length * 8];
        int n;
        fixed (byte* src = pixels) fixed (byte* dst = compressed)
            n = lz ? CodecApi.sc_lz_encode_rgb32(src, W, H, W * 4, dst, (nuint)compressed.Length) : CodecApi.sc_quic_encode_rgb32(src, W, H, W * 4, dst, (nuint)compressed.Length);
        Assert.True(n > 0); return compressed[..n];
    }
    [Fact] public void LoadsAllDlls() => Assert.True(NativeCodecs.TryLoad(out string? error), error);

    [Theory] [InlineData(false)] [InlineData(true)]
    public void RoundTripAndBounds(bool lz)
    {
        byte[] image = Image(), encoded = Encode(lz, image);
        using var q = new QuicDecoder(); using var z = new LzDecoder();
        const int stride = W * 4 + 13;
        byte[] output = Enumerable.Repeat((byte)0xA5, stride * H + 64).ToArray();
        var dst = output.AsSpan(32, (H - 1) * stride + W * 4);
        bool Decode(ReadOnlySpan<byte> d, Span<byte> target, int width, int height, int pitch) => lz ? z.TryDecode(d, [], width, height, target, pitch, out _) : q.TryDecode(d, width, height, target, pitch, out _);
        Assert.True(Decode(encoded, dst, W, H, stride));
        for (int y = 0; y < H; y++) Assert.Equal(image.AsSpan(y * W * 4, W * 4).ToArray(), dst.Slice(y * stride, W * 4).ToArray());
        Assert.All(output[..32], b => Assert.Equal(0xA5, b));
        Assert.All(output[(32 + dst.Length)..], b => Assert.Equal(0xA5, b));
        Assert.False(Decode(encoded, dst[..10], W, H, stride));
        Assert.False(Decode(encoded, dst, W + 1, H, stride));
        Assert.False(Decode(encoded, dst, int.MaxValue, int.MaxValue, int.MaxValue));
        Assert.False(Decode(encoded, dst, W, H, -1));
        for (int y = 0; y < H - 1; y++) Assert.All(output.AsSpan(32 + y * stride + W * 4, 13).ToArray(), b => Assert.Equal(0xA5, b));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void CorruptAndMutatedStreamsStayBounded(bool lz)
    {
        byte[] encoded = Encode(lz, Image());
        using var q = new QuicDecoder(); using var z = new LzDecoder();
        byte[] output = new byte[W * H * 4 + 64]; var random = new Random(873);
        for (int i = 0; i < 400; i++)
        {
            var data = (byte[])encoded.Clone();
            if (i < 100) data[0] ^= (byte)random.Next(1, 256);
            else if (i < 200) data = data[..random.Next(0, Math.Min(data.Length, 28))];
            else for (int j = 0; j < 5; j++) data[random.Next(data.Length)] ^= (byte)random.Next(1, 256);
            Array.Fill(output, (byte)0xBD);
            bool ok = lz ? z.TryDecode(data, [], W, H, output.AsSpan(32, W * H * 4), W * 4, out _) : q.TryDecode(data, W, H, output.AsSpan(32, W * H * 4), W * 4, out _);
            if (i < 200) Assert.False(ok);
            Assert.All(output[..32], b => Assert.Equal(0xBD, b)); Assert.All(output[^32..], b => Assert.Equal(0xBD, b));
        }
    }

    [Fact] public void LzBottomUpAndAlphaOnly()
    {
        byte[] image = Image(), data = Encode(true, image), dst = new byte[image.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 0);
        using var z = new LzDecoder();
        Assert.True(z.TryDecode(data, [], W, H, dst, W * 4, out _));
        Assert.Equal(image[..(W * 4)], dst[^ (W * 4)..]);
        byte[] alpha = new byte[image.Length * 8]; int n;
        for (int i = 3; i < image.Length; i += 4) image[i] = (byte)i;
        fixed (byte* src = image) fixed (byte* output = alpha) n = CodecApi.sc_lz_encode_xxxa(src, W, H, W * 4, output, (nuint)alpha.Length);
        Assert.True(n > 0); Array.Fill(dst, (byte)77);
        Assert.True(z.TryDecode(alpha.AsSpan(0, n), [], W, H, dst, W * 4, out var type)); Assert.Equal(ScImageType.Xxxa, type);
        for (int i = 0; i < dst.Length; i++) Assert.Equal(i % 4 == 3 ? image[i] : (byte)77, dst[i]);
    }

    [Fact] public void PalettePaddingAndLifetime()
    {
        // Hand-authored one-row PLT1 literal: 9 visible pixels, 16 stored pixels.
        byte[] data = new byte[31];
        uint[] fields = [0x20205A4C, 0x10001, 1, 9, 1, 2, 1];
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i * 4), fields[i]);
        data[28] = 1; data[29] = 0x55; data[30] = 0x01;
        using var z = new LzDecoder(); byte[] dst = new byte[36];
        for (int repeat = 0; repeat < 100; repeat++)
        {
            Assert.True(z.TryDecode(data, [0x00112233, 0x00445566], 9, 1, dst, 36, out var type));
            Assert.Equal(ScImageType.Plt1Le, type);
            for (int x = 0; x < 9; x++) { Assert.Equal(x % 2 == 0 ? 0x66 : 0x33, dst[x * 4]); Assert.Equal(255, dst[x * 4 + 3]); }
        }
        Assert.False(z.TryDecode(data, [], 9, 1, dst, 36, out _));
        // A short PLT8 palette is valid only when every index is in range.
        byte[] plt8 = new byte[30];
        uint[] fields8 = [0x20205A4C, 0x10001, 5, 1, 1, 1, 1];
        for (int i = 0; i < fields8.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(plt8.AsSpan(i * 4), fields8[i]);
        plt8[28] = 0; plt8[29] = 1;
        Assert.True(z.TryDecode(plt8, [0x00112233, 0x00445566], 1, 1, dst, 4, out _));
        plt8[29] = 2;
        Assert.False(z.TryDecode(plt8, [0x00112233, 0x00445566], 1, 1, dst, 4, out _));
    }

    [Fact] public void JpegEncodeDecodeAndMjpeg()
    {
        byte[] image = Image(); nint compressor = CodecApi.tj3Init(0), jpeg = 0; nuint size = 0;
        Assert.NotEqual(0, compressor);
        try
        {
            Assert.Equal(0, CodecApi.tj3Set(compressor, 3, 95)); Assert.Equal(0, CodecApi.tj3Set(compressor, 4, 0));
            fixed (byte* src = image) Assert.Equal(0, CodecApi.tj3Compress8(compressor, src, W, W * 4, H, 3, ref jpeg, ref size));
            var bytes = new ReadOnlySpan<byte>((void*)jpeg, (int)size);
            using var decoder = new JpegDecoder();
            Assert.True(decoder.TryReadHeader(bytes, out int w, out int h)); Assert.Equal(W, w); Assert.Equal(H, h);
            byte[] dst = new byte[image.Length]; Assert.True(decoder.TryDecode(bytes, dst, W, H, W * 4));
            long error = 0; for (int i = 0; i < dst.Length; i++) if (i % 4 != 3) error += Math.Abs(dst[i] - image[i]);
            Assert.True(error / (double)(W * H * 3) < 12);
            Assert.False(decoder.TryDecode(bytes, dst[..4], W, H, W * 4));
            Assert.False(decoder.TryDecode(bytes, dst, W - 1, H, W * 4));
            Assert.False(decoder.TryReadHeader([1, 2, 3], out _, out _));
            using var stream = new MjpegStreamDecoder(); Assert.True(stream.TryDecodeFrame(bytes, dst, W, H, W * 4));
        }
        finally { if (jpeg != 0) CodecApi.tj3Free(jpeg); CodecApi.tj3Destroy(compressor); }
    }

    [Fact] public void GlzLiteralAndTruncation()
    {
        // RGB32, 2x1, first image ID zero, one literal run.
        byte[] data = new byte[40];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x20205A4C); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x10001);
        data[8] = 0x18; BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(9), 2); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(13), 1); BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(17), 8);
        data[33] = 1; data[34] = 10; data[35] = 20; data[36] = 30; data[37] = 40; data[38] = 50; data[39] = 60;
        using var window = new GlzWindow(); using var decoder = new GlzDecoder(window); byte[] dst = new byte[8];
        for (int n = 0; n < data.Length; n++) Assert.False(decoder.TryDecode(data.AsSpan(0, n), 2, 1, dst, 8, out _));
        Assert.True(decoder.TryDecode(data, 2, 1, dst, 8, out var type)); Assert.Equal(ScImageType.Rgb32, type);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, dst);
        var random = new Random(944);
        for (int i = 0; i < 300; i++)
        {
            window.Clear(); byte[] mutant = (byte[])data.Clone();
            for (int j = 0; j < 3; j++) mutant[random.Next(mutant.Length)] ^= (byte)random.Next(1, 256);
            decoder.TryDecode(mutant, 2, 1, dst, 8, out _);
        }
        window.Clear(); window.Dispose(); Assert.False(decoder.TryDecode(data, 2, 1, dst, 8, out _));
    }

    [Fact] public void RawAudioAndOpusBounds()
    {
        using var audio = new AudioDecoder(AudioDataMode.Raw, 2, 48000); short[] pcm = new short[4];
        Assert.Equal(2, audio.Decode([0xFF, 0x7F, 0, 0x80, 1, 0, 0xFF, 0xFF], pcm)); Assert.Equal(new short[] { 32767, -32768, 1, -1 }, pcm);
        Assert.Equal(-1, audio.Decode([1, 2, 3], pcm)); Assert.Equal(-1, audio.Decode(new byte[12], pcm));
        using var opus = new OpusDecoderHandle(48000, 2); Assert.Equal(-1, opus.Decode([1], pcm, int.MaxValue)); Assert.Equal(-1, opus.Decode([], pcm, 2));
    }

    [Fact] public void WarmImageAndAudioCallsDoNotAllocate()
    {
        byte[] data = Encode(true, Image()), dst = new byte[W * H * 4];
        using var lz = new LzDecoder();
        using var raw = new AudioDecoder(AudioDataMode.Raw, 2, 48000);
        byte[] audio = new byte[1920]; short[] pcm = new short[960];
        for (int i = 0; i < 20; i++) { lz.TryDecode(data, [], W, H, dst, W * 4, out _); raw.Decode(audio, pcm); }
        AllocationWindow.Measure(nameof(WarmImageAndAudioCallsDoNotAllocate),
            () => { for (int i = 0; i < 100; i++) { lz.TryDecode(data, [], W, H, dst, W * 4, out _); raw.Decode(audio, pcm); } },
            prepare: () => { for (int i = 0; i < 20; i++) { lz.TryDecode(data, [], W, H, dst, W * 4, out _); raw.Decode(audio, pcm); } });
    }
}
