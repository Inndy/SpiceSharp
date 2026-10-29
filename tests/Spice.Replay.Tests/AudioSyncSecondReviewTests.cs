using System.Buffers.Binary;
using Spice.Media;
using Spice.Session;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

[Collection("Allocation profiling")]
public sealed class AudioSyncSecondReviewTests
{
    sealed class ManualClock : PacingClock
    {
        public long Now;
        public override long NowMs => Now;
        public override void Arm(long atMs) { }
        public override void Disarm() { }
        public override void Dispose() { }
    }

    sealed class Harness : IAsyncDisposable
    {
        public readonly SpiceSession Session = new(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        public readonly PcmQueue Queue = new();
        public readonly ManualClock Clock = new();
        readonly byte[] data;
        public int LastDelay, LastGap;

        public Harness(int rate = 48000, int frames = 480)
        {
            Session.PacingClock = Clock;
            Session.VideoPacing = VideoPacing.Clock;
            Session.MultimediaTimeBase(1000);
            Queue.Reset(2, rate);
            Session.AudioData += (pcm, delay, gap) =>
            {
                LastDelay = delay; LastGap = gap;
                Queue.Write(pcm.Span, delay, gap);
            };
            var start = new byte[14];
            BinaryPrimitives.WriteUInt32LittleEndian(start, 2);
            BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(6), (uint)rate);
            Session.OnPlaybackMessage(PlaybackMsg.Start, start);
            data = new byte[4 + frames * 2 * sizeof(short)];
        }

        public void Packet(uint stamp, short marker = 111)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data, stamp);
            for (int i = 4; i < data.Length; i += sizeof(short))
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i), marker);
            Session.OnPlaybackMessage(PlaybackMsg.Data, data);
        }

        public short[] Drain(int frames)
        {
            var pcm = new short[frames * 2];
            Queue.Dequeue(pcm, false);
            return pcm;
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    [Fact]
    public async Task ResumingAfterFiveSecondSourcePauseDoesNotDropTheResumedPacket()
    {
        await using var h = new Harness();
        h.Packet(1300);
        h.Drain(h.Queue.Count / 2); // A was played; nothing remains queued during the source pause.
        h.Clock.Now = 5000;
        h.Packet(6300, 222); // A real 4990 ms source gap, but this packet itself is due in just 300 ms.
        Assert.Equal(4990, h.LastGap);
        Assert.Equal(300, h.LastDelay);
        h.Drain(480); // Ten milliseconds of ordinary playback before the next packet.
        h.Clock.Now = 5010;
        h.Packet(6310, 333);
        Assert.Equal(0, h.LastGap);
        Assert.Equal(300, h.LastDelay);
        var dropped = h.Queue.Statistics.SyncDroppedFrames;
        var played = h.Drain(h.Queue.Count / 2);
        Assert.Contains((short)333, played);
        // Expired source-gap debt must not discard correctly scheduled resumed audio.
        Assert.Equal((0L, true), (dropped, played.Contains((short)222)));
    }

    [Fact]
    public async Task ContiguousFractionalMillisecondPacketsDoNotAccumulatePhantomGaps()
    {
        const int rate = 44100, frames = 256;
        await using var h = new Harness(rate, frames);
        var rendered = new short[frames * 2];
        for (int i = 0; i < 200; i++)
        {
            // Absolute timestamps rounded to whole milliseconds, for genuinely contiguous 256-frame PCM blocks.
            // The packet duration is about 5.805 ms, not an integral number of milliseconds.
            long elapsedMs = (long)i * frames * 1000 / rate;
            h.Clock.Now = elapsedMs;
            h.Packet(1400 + (uint)elapsedMs);
            Assert.Equal(400, h.LastDelay);
            h.Queue.Dequeue(rendered, false); // The ideal device consumes exactly the packet's frame count.
        }
        var s = h.Queue.Statistics;
        // Initial hold only; there are no source pauses or clock changes.
        Assert.Equal((0L, 0L, 1L), (s.SyncDroppedFrames, s.GapSilenceFrames, s.SyncCorrections));
    }

    [Fact]
    public async Task BoundedThreeMillisecondStampJitterDoesNotCorrectTheTimeline()
    {
        await using var h = new Harness();
        h.Packet(1400);
        h.Drain(480);
        for (int i = 1; i <= 100; i++)
        {
            h.Clock.Now = i * 10;
            int jitter = i % 2 == 0 ? 3 : -3;
            h.Packet((uint)(1400 + i * 10 + jitter));
            h.Drain(480);
        }
        var s = h.Queue.Statistics;
        Assert.Equal(1, s.SyncCorrections);
        Assert.Equal(0, s.SyncDroppedFrames);
        Assert.Equal(0, s.GapSilenceFrames);
        Assert.Equal(0, s.GapTrimmedFrames);
    }

    [Fact]
    public async Task ThirtyMillisecondPauseRemainsAnEarlyPhaseOffsetUnderTheChosenPolicy()
    {
        // A characterization of the explicit policy departure, not approval of that departure.
        // Updated by the implementer for the 10 ms gap jitter allowance (PcmQueue.GapJitterMs): a 30 ms pause is beyond
        // it, so it is now laid out exactly and later audio keeps its stamps.
        await using var h = new Harness();
        h.Packet(1300, 111);
        h.Packet(1340, 222); // Thirty milliseconds after A's end stamp.
        h.Packet(1350, 333); // Contiguous with B.
        var played = h.Drain(h.Queue.Count / 2);
        Assert.Equal((short)111, played[300 * 48 * 2]);
        Assert.Equal((short)222, played[340 * 48 * 2]); // B is on its stamp; the pause was inserted.
        Assert.Equal((short)333, played[350 * 48 * 2]); // Later contiguous packets keep their stamps.
        Assert.Equal(30 * 48, h.Queue.Statistics.GapSilenceFrames);
    }

    [Fact]
    public async Task GapAndClockDeadbandsCanCombineIntoSeventyMillisecondsOfEarlyAudio()
    {
        // Characterize the combined allowance; the design has two independent deadbands.
        // Updated by the implementer for the one combined budget: the 40 ms pause is beyond the 10 ms gap jitter
        // allowance and is laid out exactly; the 30 ms clock shift alone is within the 40 ms budget, so B is 30 ms early.
        await using var h = new Harness();
        h.Packet(1300, 111);
        h.Session.MultimediaTimeBase(970); // Thirty ms backwards, inside the clock-shift allowance.
        h.Packet(1350, 222); // Forty ms source pause after A's end.
        Assert.Equal(40, h.LastGap);
        Assert.Equal(380, h.LastDelay);
        var played = h.Drain(h.Queue.Count / 2);
        int firstB = Array.FindIndex(played, sample => sample == 222) / 2;
        Assert.Equal(350 * 48, firstB); // +350 ms actual versus +380 ms requested: 30 ms early.
        Assert.Equal(30 * 48, h.LastDelay * 48 - firstB);
        Assert.Equal(1, h.Queue.Statistics.SyncCorrections);
        Assert.Equal(40 * 48, h.Queue.Statistics.GapSilenceFrames);
    }
}
