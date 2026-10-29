namespace Spice.Wire.Tests;

// SPICE_MSGC_DISPLAY_PREFERRED_COMPRESSION (spice-protocol enums.h; spice.proto "message { image_compression
// image_compression; } @declare preferred_compression", image_compression being an enum8).
public sealed class PreferredCompressionMessageTests
{
    [Fact]
    public void MessageNumberAndEnumMatchSpiceProtocol()
    {
        // enum { SPICE_MSGC_DISPLAY_INIT = 101, STREAM_REPORT, PREFERRED_COMPRESSION, ... }
        Assert.Equal((ushort)103, DisplayMsg.CPreferredCompression);
        // SpiceImageCompression: INVALID, OFF, AUTO_GLZ, AUTO_LZ, QUIC, GLZ, LZ, LZ4
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 },
            new[] { ImageCompression.Invalid, ImageCompression.Off, ImageCompression.AutoGlz, ImageCompression.AutoLz,
                ImageCompression.Quic, ImageCompression.Glz, ImageCompression.Lz, ImageCompression.Lz4 }.Select(c => (byte)c));
        // SPICE_DISPLAY_CAP_LZ4_COMPRESSION = 5, SPICE_DISPLAY_CAP_PREF_COMPRESSION = 6
        Assert.Equal(5, DisplayCap.Lz4Compression);
        Assert.Equal(6, DisplayCap.PrefCompression);
    }

    [Fact]
    public void BodyIsOneImageCompressionByte()
    {
        Span<byte> b = stackalloc byte[4];
        b.Fill(0xAA);
        int n = ClientMsg.PreferredCompression(b, ImageCompression.Lz4);
        Assert.Equal(1, n);
        Assert.Equal(new byte[] { 7, 0xAA, 0xAA, 0xAA }, b.ToArray());
    }
}
