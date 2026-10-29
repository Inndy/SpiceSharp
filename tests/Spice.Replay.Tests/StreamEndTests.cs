using Spice.Session;
using Spice.Wire;

namespace Spice.Replay.Tests;

// Live finding F2 (docs/verification/2026-10-30-live-options.md): in clock mode the last ~0.4 s of a stream never
// reached the screen and wasn't counted. The messages below are spice-server's for a video window that closes: the
// desktop redrawn over the stream area is an opaque draw, which first shrinks the stream's clip (STREAM_CLIP, here to
// nothing) and is then sent itself; the stream is destroyed only by its 1 s timeout. Frames still waiting for the
// guest clock are then blitted with an empty clip. See docs/decisions/0025.
#pragma warning disable IL2026, IL2075
public sealed partial class StreamPacingTests
{
    const uint White = 0xFFFFFFFF;

    static byte[] Clip(uint id, params SpiceRect[] rects) => Bytes(w =>
    {
        w.Write(id); w.Write(SpiceClip.TypeRects); w.Write((uint)rects.Length);
        foreach (var r in rects) Rect(w, r);
    });

    static byte[] FillWhite(SpiceRect box) => Bytes(w =>
    {
        w.Write(0u); Rect(w, box); w.Write(SpiceClip.TypeNone);
        w.Write((byte)BrushType.Solid); w.Write(0xFFFFFFu); w.Write(Ropd.OpPut);
        w.Write((byte)0); w.Write(0); w.Write(0); w.Write(0u); // no mask
    });

    static List<string> CaptureLog(Action act)
    {
        var lines = new List<string>();
        var (sink, level) = (Log.Sink, Log.MinLevel);
        Log.Sink = (_, m) => { lock (lines) lines.Add(m); };
        Log.MinLevel = LogLevel.Debug;
        try { act(); }
        finally { Log.Sink = sink; Log.MinLevel = level; }
        return lines;
    }

    static bool StreamPixelsAre(Harness h, uint color)
    {
        var p = h.Session.Canvas.Primary!;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (p.Row(y)[x] != color) return false;
        return true;
    }

    [Fact]
    public async Task ClockModeFramesHiddenByTheClipWhileQueuedAreCountedAndNotDecoded()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        // 21 fps with the server's ~420 ms lead, as on win7: nine frames wait for the guest clock.
        for (uint k = 0; k < 9; k++) h.Frame(Epoch + 420 + 47 * k);
        Assert.Equal(9, h.State<int>("QueueCount")); Assert.Equal(0, h.Draws);
        // The window closes: the stream clip shrinks to nothing, then the desktop is drawn over the stream area.
        h.Send(DisplayMsg.StreamClip, Clip(7));
        h.Send(DisplayMsg.DrawFill, FillWhite(Destination));
        Assert.Equal(1, h.Draws); Assert.True(StreamPixelsAre(h, White));
        for (int t = 0; t < 1000; t++) h.Clock.Advance(1); // each queued frame comes due in turn; none may cover the desktop
        Assert.True(StreamPixelsAre(h, White)); Assert.Equal(1, h.Draws);
        Assert.Equal(0, h.State<int>("QueueCount")); Assert.Null(h.Clock.ArmedAt);
        Assert.Equal(9u, h.State<uint>("Hidden")); Assert.Equal(0u, h.State<uint>("Drops"));
        Assert.Null(Field("_mjpeg").GetValue(h.Session)); // hidden frames aren't decoded
        var log = CaptureLog(() => h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u)))); // the 1 s timeout
        Assert.Contains("display: stream 7 ended after 9 frames, 0 dropped, 9 hidden by its clip", log);
    }

    [Fact]
    public async Task APartlyCoveredStreamStillDrawsItsQueuedFrames()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        h.Frame(Epoch + 100);
        h.Send(DisplayMsg.StreamClip, Clip(7, new SpiceRect(0, 0, 1, 1))); // a window raised over all but one pixel
        h.Clock.Advance(100);
        Assert.Equal(1, h.Draws); Assert.Equal(0u, h.State<uint>("Hidden")); Assert.NotNull(Field("_mjpeg").GetValue(h.Session));
    }

    [Fact]
    public async Task HiddenFrameOnArrivalIsNotDecodedAndNotReportedAsDropped()
    {
        await using var h = new Harness(mode: VideoPacing.Off);
        h.Send(DisplayMsg.StreamClip, Clip(7, new SpiceRect(5, 5, 6, 6))); // clip outside the destination
        h.Frame(Epoch);
        Assert.Equal(0, h.Draws); Assert.Null(Field("_mjpeg").GetValue(h.Session));
        Assert.Equal(1u, h.State<uint>("Frames")); Assert.Equal(0u, h.State<uint>("Drops")); Assert.Equal(1u, h.State<uint>("Hidden"));
    }

    [Fact]
    public async Task StreamDestroyDrawsTheNewestQueuedFrameBeforeLaterDrawsAndCountsTheRest()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        h.Frame(Epoch + 400); h.Frame(Epoch + 447); h.Frame(Epoch + 494);
        Assert.Equal(0, h.Draws); Assert.Equal(400, h.Clock.ArmedAt);
        var log = CaptureLog(() => h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u))));
        // The server believes the last frame is on screen: it is drawn now, in message order, and the older two dropped.
        Assert.Equal(1, h.Draws); Assert.False(StreamPixelsAre(h, 0xFF000000));
        Assert.Contains("display: stream 7 ended after 3 frames, 2 dropped, 0 hidden by its clip", log);
        Assert.Null(h.Clock.ArmedAt); Assert.Equal(0L, h.QueuedBytes);
        // What the server draws over the area after the destroy stays: nothing queued is left to cover it.
        h.Send(DisplayMsg.DrawFill, FillWhite(Destination));
        h.Clock.Advance(1000);
        Assert.Equal(2, h.Draws); Assert.True(StreamPixelsAre(h, White));
    }
}
