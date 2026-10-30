using System.Buffers.Binary;
using Spice.Wire;

namespace Spice.Wire.Tests;

/// <summary>DISPLAY_MONITORS_CONFIG parsing (docs/decisions/0035): the first non-empty head on surface 0.</summary>
public sealed class MonitorsConfigParseTests
{
    /// <summary>{u16 count, u16 max_allowed, heads[] {id, surface_id, width, height, x, y, flags}}, as spice.proto lays it out.</summary>
    static byte[] Config(ushort count, params (uint Id, uint Surface, uint W, uint H, uint X, uint Y)[] heads)
    {
        var b = new byte[4 + heads.Length * DisplayParse.MonitorHeadSize];
        BinaryPrimitives.WriteUInt16LittleEndian(b, count);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 4);
        for (int i = 0; i < heads.Length; i++)
        {
            var s = b.AsSpan(4 + i * DisplayParse.MonitorHeadSize);
            var (id, surface, w, h, x, y) = heads[i];
            uint[] fields = [id, surface, w, h, x, y, 0];
            for (int f = 0; f < fields.Length; f++) BinaryPrimitives.WriteUInt32LittleEndian(s[(f * 4)..], fields[f]);
        }
        return b;
    }

    [Fact]
    public void OneHeadIsReadInWireOrder()
    {
        Assert.True(DisplayParse.TryPrimaryHead(Config(1, (0, 0, 640, 480, 0, 0)), out var head, out int count));
        Assert.Equal(new MonitorHead(0, 0, 640, 480, 0, 0, 0), head);
        Assert.Equal(1, count);
    }

    [Fact]
    public void TheUbuntuFixtureMessageNamesTheMonitorNotTheSurface()
    {
        // The bytes of tests/fixtures/ubuntu-rich's MONITORS_CONFIG: one 640x480 head on the 640x512 surface 0.
        byte[] recorded =
        [
            0x01, 0x00, 0x04, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x02, 0x00, 0x00, 0xE0, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        Assert.True(DisplayParse.TryPrimaryHead(recorded, out var head, out int count));
        Assert.Equal((640u, 480u, 0u, 0u), (head.Width, head.Height, head.X, head.Y));
        Assert.Equal(1, count);
    }

    [Fact]
    public void ZeroHeadsMeansNoMonitor()
    {
        Assert.False(DisplayParse.TryPrimaryHead(Config(0), out var head, out int count));
        Assert.Equal(default, head);
        Assert.Equal(0, count);
    }

    [Fact]
    public void HeadsOnOtherSurfacesAndEmptyHeadsAreSkipped()
    {
        var m = Config(3, (0, 1, 800, 600, 0, 0), (1, 0, 0, 0, 0, 0), (2, 0, 1024, 768, 800, 0));
        Assert.True(DisplayParse.TryPrimaryHead(m, out var head, out int count));
        Assert.Equal(2u, head.Id);
        Assert.Equal((1024u, 768u, 800u, 0u), (head.Width, head.Height, head.X, head.Y));
        Assert.Equal(3, count);
    }

    [Fact]
    public void OnlyTheFirstHeadOnSurfaceZeroIsUsed()
    {
        var m = Config(2, (0, 0, 1024, 768, 0, 0), (1, 0, 1280, 1024, 1024, 0));
        Assert.True(DisplayParse.TryPrimaryHead(m, out var head, out int count));
        Assert.Equal(0u, head.Id);
        Assert.Equal(2, count);
    }

    [Fact]
    public void NoHeadOnSurfaceZero()
        => Assert.False(DisplayParse.TryPrimaryHead(Config(1, (0, 3, 640, 480, 0, 0)), out _, out _));

    [Fact]
    public void ACountLargerThanThePayloadIsRejectedEvenWhenTheFirstHeadFits()
    {
        // Two heads announced, one present: the message is malformed as a whole.
        var m = Config(2, (0, 0, 640, 480, 0, 0));
        Assert.Throws<WireException>(() => DisplayParse.TryPrimaryHead(m, out _, out _));
    }

    [Fact]
    public void AnImplausibleCountIsRejected()
    {
        var m = Config(DisplayParse.MaxMonitorHeads + 1);
        Assert.Throws<WireException>(() => DisplayParse.TryPrimaryHead(m, out _, out _));
    }

    [Fact]
    public void AShortHeaderIsRejected()
    {
        Assert.Throws<WireException>(() => DisplayParse.TryPrimaryHead(new byte[3], out _, out _));
        Assert.Throws<WireException>(() => DisplayParse.TryPrimaryHead(Array.Empty<byte>(), out _, out _));
    }

    [Fact]
    public void OversizedRectanglesAreReturnedAsSent()
    {
        // Clamping to the surface is the canvas's job; the parser keeps the full u32 values.
        Assert.True(DisplayParse.TryPrimaryHead(Config(1, (0, 0, uint.MaxValue, uint.MaxValue, uint.MaxValue, 7)), out var head, out _));
        Assert.Equal((uint.MaxValue, uint.MaxValue, uint.MaxValue, 7u), (head.Width, head.Height, head.X, head.Y));
    }

    [Fact]
    public void ParsingAllocatesNothing()
    {
        ReadOnlyMemory<byte> m = Config(2, (0, 1, 800, 600, 0, 0), (1, 0, 640, 480, 0, 0));
        DisplayParse.TryPrimaryHead(m, out _, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        uint sum = 0;
        for (int i = 0; i < 10_000; i++)
            if (DisplayParse.TryPrimaryHead(m, out var head, out _)) sum += head.Height;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(4_800_000u, sum);
        Assert.Equal(0, allocated);
    }
}
