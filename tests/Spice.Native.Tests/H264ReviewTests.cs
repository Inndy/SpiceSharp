using System.Runtime.InteropServices;
using Spice.Native;
using Spice.Tests;

namespace Spice.Native.Tests;

// Security boundary checks for the 2026-10-31 review; no window or external fixture generator.
public sealed unsafe partial class H264ReviewTests
{
    [Fact]
    public void InputMayEndImmediatelyBeforeAnUnreadablePage()
    {
        byte[] unit = H264Fixtures.AccessUnits("clip-444.h264")[0];
        int page = Environment.SystemPageSize;
        Assert.True(unit.Length <= page);
        nint memory = VirtualAlloc(0, (nuint)(2 * page), 0x3000, 4);
        Assert.NotEqual(0, memory);
        try
        {
            Assert.True(VirtualProtect(memory + page, (nuint)page, 1, out _));
            var input = new Span<byte>((void*)(memory + page - unit.Length), unit.Length);
            unit.CopyTo(input);
            using var decoder = H264Decoder.TryCreate(1);
            Assert.NotNull(decoder);
            // No readable padding belongs to the caller: libavcodec must create its own.
            Assert.Equal(H264DecodeStatus.Picture, decoder.Decode(input, out var picture));
            Assert.Equal((320, 240, 1), (picture.Width, picture.Height, picture.Supported));
        }
        finally { Assert.True(VirtualFree(memory, 0, 0x8000)); }
    }

    [Fact]
    public void SizeAndPixelFormatChangesCannotWriteBeyondTheDestination()
    {
        using var decoder = H264Decoder.TryCreate(1);
        Assert.NotNull(decoder);
        byte[] large = H264Fixtures.AccessUnits("clip-444.h264")[0];
        byte[] smallDeep = H264Fixtures.AccessUnits("clip-444-10bit.h264")[0];
        Assert.Equal(H264DecodeStatus.Picture, decoder.Decode(large, out _));
        Assert.Equal(H264DecodeStatus.Picture, decoder.Decode(smallDeep, out var small));
        Assert.Equal((64, 48, 0), (small.Width, small.Height, small.Supported));
        byte[] storage = new byte[320 * 240 * 4 + 32];
        storage.AsSpan().Fill(0xA5);
        Assert.False(decoder.TryConvert(storage.AsSpan(16, 64 * 48 * 4), 64 * 4, false));
        Assert.All(storage, b => Assert.Equal((byte)0xA5, b));

        Assert.Equal(H264DecodeStatus.Picture, decoder.Decode(large, out var big));
        Assert.Equal((320, 240, 1), (big.Width, big.Height, big.Supported));
        Assert.False(decoder.TryConvert(storage.AsSpan(16, 64 * 48 * 4), big.Width * 4, false));
        Assert.False(decoder.TryConvert(storage.AsSpan(16, big.Width * big.Height * 4), -1, false));
        Assert.All(storage, b => Assert.Equal((byte)0xA5, b));
        Assert.True(decoder.TryConvert(storage.AsSpan(16, big.Width * big.Height * 4), big.Width * 4, true));
        Assert.All(storage[..16], b => Assert.Equal((byte)0xA5, b));
        Assert.All(storage[^16..], b => Assert.Equal((byte)0xA5, b));
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(nint address, nuint size, uint freeType);
}
