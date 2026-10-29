using System.Buffers.Binary;
using Spice.Native;

namespace Spice.Native.Tests;

public sealed unsafe class NativeReviewRegressionTests
{
    [Fact]
    public void GlzWindowClearAllowsImageIdsToRestartAfterRetirement()
    {
        using var window = new GlzWindow();
        using var decoder = new GlzDecoder(window);
        byte[] output = new byte[4];

        Assert.True(decoder.TryDecode(GlzLiteral(0), 1, 1, output, 4, out _));
        // Image 1 has win_head_dist=0: image 0 is retired and the oldest-ID floor advances.
        Assert.True(decoder.TryDecode(GlzLiteral(1), 1, 1, output, 4, out _));
        window.Clear();

        Assert.True(decoder.TryDecode(GlzLiteral(0), 1, 1, output, 4, out _),
            "A cleared dictionary must accept image ID zero again, including after older images were retired.");
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, output);
    }

    [Fact]
    public void LzCAbiPreservesBytesBeyondAdvertisedVisibleOutput()
    {
        // The C header promises width*height*4 output bytes. This PLT1 row has
        // 9 visible pixels but 16 stored pixels. Reserve the full padded storage
        // and extra guards so detecting a contract violation cannot overrun the allocation.
        byte[] data = new byte[31];
        uint[] fields = [0x20205A4C, 0x10001, 1, 9, 1, 2, 1];
        for (int i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i * 4), fields[i]);
        data[28] = 1; data[29] = 0x55; data[30] = 0x01;
        uint[] palette = [0x00112233, 0x00445566];
        byte[] output = Enumerable.Repeat((byte)0xA5, 128).ToArray();
        nint decoder = CodecApi.sc_lz_create();
        Assert.NotEqual(0, decoder);
        try
        {
            fixed (byte* source = data)
            fixed (uint* colors = palette)
            fixed (byte* destination = output)
            {
                Assert.Equal(0, CodecApi.sc_lz_begin(decoder, source, (nuint)data.Length,
                    colors, palette.Length, out int width, out int height, out var type, out int topDown));
                Assert.Equal(9, width); Assert.Equal(1, height);
                Assert.Equal(ScImageType.Plt1Le, type); Assert.Equal(1, topDown);
                Assert.Equal(0, CodecApi.sc_lz_decode(decoder, destination + 32, (nuint)(width * height * 4)));
                for (int x = 0; x < width; x++)
                {
                    // RGB32's unused fourth byte is normalized by the managed wrapper,
                    // not by this C entry point; only the color is relevant here.
                    uint color = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(32 + x * 4)) & 0xFFFFFF;
                    Assert.Equal(palette[x % 2 == 0 ? 1 : 0], color);
                }
                Assert.All(output[..32], b => Assert.Equal(0xA5, b));
                Assert.All(output[96..], b => Assert.Equal(0xA5, b));
                Assert.All(output[(32 + width * height * 4)..], b => Assert.Equal(0xA5, b));
            }
        }
        finally { CodecApi.sc_lz_destroy(decoder); }
    }

    static byte[] GlzLiteral(ulong id)
    {
        byte[] data = new byte[37];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x20205A4C);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x10001);
        data[8] = 0x18; // top-down RGB32
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(9), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(17), 4);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(21), id);
        // win_head_dist and the single-literal control byte remain zero.
        data[34] = 10; data[35] = 20; data[36] = 30;
        return data;
    }
}
