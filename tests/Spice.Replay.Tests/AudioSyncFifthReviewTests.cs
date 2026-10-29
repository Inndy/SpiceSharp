using Spice.Media;

namespace Spice.Replay.Tests;

[Collection("Allocation profiling")]
public sealed class AudioSyncFifthReviewTests
{
    static short[] Packet(int rate, int channels, int milliseconds, short marker) =>
        Enumerable.Repeat(marker, rate * milliseconds / 1000 * channels).ToArray();

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(48000, 2)]
    public void StaleFillSilencesOnceThenFreshReadyChoiceCanPlayTheReprimedAudio(int rate, int channels)
    {
        var q = new PcmQueue();
        q.Reset(channels, rate);
        short[] a = Packet(rate, channels, 10, 111), b = Packet(rate, channels, 10, 222);
        q.Write(a, 300);
        Assert.True(q.ReadyFrames() > 0);
        q.Write(a); // Switch off after selection: ten fresh ms, not yet primed.
        var dst = new short[rate * 40 / 1000 * channels];
        dst.AsSpan().Fill(-1);
        Assert.Equal(0, q.Transfer(dst, false, rate * 40 / 1000));
        Assert.All(dst, sample => Assert.Equal(0, sample));
        Assert.Equal(a.Length, q.Count);
        Assert.Equal(rate * 40 / 1000, q.DeviceQueuedFrames);
        Assert.Equal(0, q.ReadyFrames());

        q.Write(b);
        Assert.Equal(rate * 20 / 1000, q.ReadyFrames());
        dst.AsSpan().Fill(-1);
        Assert.Equal(a.Length + b.Length, q.Transfer(dst, false, rate * 40 / 1000));
        Assert.Equal(a, dst[..a.Length]);
        Assert.Equal(b, dst[a.Length..(a.Length + b.Length)]);
        Assert.All(dst[(a.Length + b.Length)..], sample => Assert.Equal(0, sample));
        Assert.Equal(0, q.Count);
    }

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(48000, 2)]
    public void ContinuedStreamProtectsThroughTheLastOldFrameThenAllowsRetime(int rate, int channels)
    {
        var q = new PcmQueue();
        q.Reset(channels, rate);
        q.Write(Packet(rate, channels, 10, 111), 300);
        int oldSamples = q.Count;
        Assert.True(q.BeginDrain());
        Assert.True(q.TryContinue(channels, rate));
        Assert.Equal(0, q.Write(Packet(rate, channels, 10, 222), 100));
        Assert.Equal(0, q.Write(Packet(rate, channels, 10, 333), 110));

        // A partial render leaves exactly one frame of the previous stream at the head, even in stereo.
        q.Dequeue(new short[oldSamples - channels], false);
        Assert.Equal(0, q.Write(Packet(rate, channels, 10, 444), 120));
        var lastOldFrame = new short[channels];
        q.Dequeue(lastOldFrame, false);
        Assert.All(lastOldFrame, sample => Assert.Equal(111, sample));

        // There is no old frame left to protect. A later deadline must now retime the new stream.
        Assert.True(q.Write(Packet(rate, channels, 10, 555), 500) > 0);
        Assert.Equal(0, q.Statistics.SyncDroppedFrames);
    }
}
