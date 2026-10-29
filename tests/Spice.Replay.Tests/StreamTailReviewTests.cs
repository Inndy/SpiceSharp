using Spice.Session;
using Spice.Tests;
using Spice.Wire;

namespace Spice.Replay.Tests;

#pragma warning disable IL2026, IL2075
public sealed partial class StreamPacingTests
{
    static byte[] SizedTailFrame(uint stamp, SpiceRect dest) => Bytes(w =>
    {
        w.Write(7u); w.Write(stamp); w.Write((uint)W); w.Write((uint)H); Rect(w, dest);
        w.Write((uint)Jpeg.Value.Length); w.Write(Jpeg.Value);
    });

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task TailVisibilityUsesTheIntersectionOfSurfaceDestinationAndAnyClipRect(int clipCase, bool visible)
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        var dest = new SpiceRect(9, 7, 12, 9); // Only pixel (9,7) is inside the 10x8 surface.
        var onSurface = new SpiceRect(9, 7, 10, 8);
        var offSurface = new SpiceRect(10, 7, 12, 9);
        h.Send(DisplayMsg.StreamDataSized, SizedTailFrame(Epoch + 100, dest));
        h.Send(DisplayMsg.StreamClip, clipCase switch
        {
            0 => Clip(7, onSurface),
            1 => Clip(7, offSurface), // Intersects dest, but only outside the surface.
            _ => Clip(7, offSurface, onSurface), // The first rectangle must not hide a later visible one.
        });
        h.Clock.Advance(100);

        Assert.Equal(visible ? 1 : 0, h.Draws);
        Assert.Equal(visible ? 0u : 1u, h.State<uint>("Hidden"));
        Assert.Equal(0u, h.State<uint>("Drops"));
        Assert.Equal(!visible, Field("_mjpeg").GetValue(h.Session) is null);
        var surface = h.Session.Canvas.Primary!;
        for (int y = 0; y < surface.Height; y++)
            for (int x = 0; x < surface.Width; x++)
                if (visible && x == 9 && y == 7) Assert.NotEqual(0xFF000000u, surface.Row(y)[x]);
                else Assert.Equal(0xFF000000u, surface.Row(y)[x]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestroyCannotOverwriteAnEarlierDesktopDrawExcludedByTheCurrentClip(bool completelyHidden)
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        h.Frame(Epoch + 400);
        h.Send(DisplayMsg.StreamClip, completelyHidden ? Clip(7) : Clip(7, new SpiceRect(0, 0, 1, H)));
        h.Send(DisplayMsg.DrawFill, FillWhite(new SpiceRect(completelyHidden ? 0 : 1, 0, W, H)));
        Assert.Equal(1, h.Draws);
        h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u)));

        int expectedDraws = completelyHidden ? 1 : 2;
        Assert.Equal(expectedDraws, h.Draws);
        Assert.Equal(completelyHidden, Field("_mjpeg").GetValue(h.Session) is null);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (!completelyHidden && x == 0) Assert.NotEqual(0xFF000000u, h.Session.Canvas.Primary!.Row(y)[x]);
                else Assert.Equal(White, h.Session.Canvas.Primary!.Row(y)[x]);
        Assert.Equal(0L, h.QueuedBytes);
        Assert.Null(h.Clock.ArmedAt);
        h.Clock.Advance(1000);
        Assert.Equal(expectedDraws, h.Draws);
    }

    [Fact]
    public async Task HiddenFramesAllocateNothingAndNeverCreateADecoderAfterWarmUp()
    {
        await using var h = new Harness(mode: VideoPacing.Off);
        h.Send(DisplayMsg.StreamClip, Clip(7));
        byte[] frame = Data(Epoch);
        void Work() { for (int n = 0; n < 1000; n++) h.Send(DisplayMsg.StreamData, frame); }
        Work();
        uint hiddenBefore = 0;
        var measurement = AllocationWindow.Measure("hidden stream frames", Work,
            prepare: () => { Work(); hiddenBefore = h.State<uint>("Hidden"); }, report: output.WriteLine);
        Assert.Equal(0L, measurement.Bytes);
        Assert.Equal(hiddenBefore + 1000, h.State<uint>("Hidden"));
        Assert.Equal(0u, h.State<uint>("Drops"));
        Assert.Equal(0, h.Draws);
        Assert.Null(Field("_mjpeg").GetValue(h.Session));
    }
}
