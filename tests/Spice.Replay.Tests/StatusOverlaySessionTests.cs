using Spice.Session;
using Spice.Tests;
using Spice.Wire;

namespace Spice.Replay.Tests;

// The session's side of the status overlay (docs/decisions/0044): the stream figures it reads, and the StatusChanged
// event that refreshes it when a stream starts or ends without a frame to carry the change.
public sealed partial class StreamPacingTests
{
    [Fact]
    public async Task StreamStatusFollowsTheStreamAndItsEndRaisesStatusChanged()
    {
        await using var h = new Harness(mode: VideoPacing.Off);
        int changes = 0;
        h.Session.StatusChanged += () => changes++;
        var streams = new VideoStreamStatus[4];

        Assert.Equal(1, h.Session.ReadVideoStreams(streams));
        Assert.Equal(new VideoStreamStatus(7, VideoCodecType.Mjpeg, VideoDecodePath.Mjpeg, W, H, 0, 0, 0, 0), streams[0]);
        h.Frame(Epoch);
        h.Frame(Epoch + 40);
        Assert.Equal(1, h.Session.ReadVideoStreams(streams));
        Assert.Equal(2, streams[0].Frames);
        Assert.Equal(2, streams[0].Shown);
        Assert.Equal(0, changes); // frames present anyway: no extra refresh

        h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u)));
        Assert.True(changes >= 1);
        Assert.Equal(0, h.Session.ReadVideoStreams(streams));
        Assert.Equal(0, h.Session.ReadVideoStreams([])); // nothing to copy into: still counts
    }

    [Fact]
    public async Task ReadingTheOverlayFiguresAllocatesNothing()
    {
        await using var h = new Harness(mode: VideoPacing.Off);
        var streams = new VideoStreamStatus[4];
        void Read()
        {
            for (int i = 0; i < 1000; i++)
            {
                h.Session.ReadVideoStreams(streams);
                h.Session.ReadTraffic(out _, out _, out _);
                _ = h.Session.Audio;
                _ = h.Session.LastTrim;
                _ = h.Session.LinkedChannelCount;
            }
        }
        Read();
        var m = AllocationWindow.Measure(nameof(ReadingTheOverlayFiguresAllocatesNothing), Read);
        Assert.Equal(0, m.Bytes);
    }
}
