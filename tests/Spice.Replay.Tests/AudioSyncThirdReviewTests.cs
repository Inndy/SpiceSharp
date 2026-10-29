using System.Buffers.Binary;
using Spice.Media;
using Spice.Session;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

[Collection("Allocation profiling")]
public sealed class AudioSyncThirdReviewTests
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
        readonly byte[] data = new byte[4 + 1024 * 2 * sizeof(short)];
        public int LastDelay, LastGap;

        public Harness(int rate = 48000, uint firstStamp = 1400)
        {
            Session.PacingClock = Clock;
            Session.VideoPacing = VideoPacing.Clock;
            Session.MultimediaTimeBase(unchecked(firstStamp - 400));
            Session.AudioStarted += (channels, hz) => Queue.Reset(channels, hz);
            Session.AudioData += (pcm, delay, gap) =>
            {
                LastDelay = delay; LastGap = gap;
                Queue.Write(pcm.Span, delay, gap);
            };
            Start(rate);
        }

        public void Start(int rate)
        {
            var start = new byte[14];
            BinaryPrimitives.WriteUInt32LittleEndian(start, 2);
            BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(6), (uint)rate);
            Session.OnPlaybackMessage(PlaybackMsg.Start, start);
        }

        public void Packet(uint stamp, int frames = 480, short marker = 111)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data, stamp);
            int length = 4 + frames * 2 * sizeof(short);
            for (int i = 4; i < length; i += sizeof(short))
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i), marker);
            Session.OnPlaybackMessage(PlaybackMsg.Data, data.AsMemory(0, length));
        }

        public short[] Drain()
        {
            var result = new short[Queue.Count];
            Queue.Dequeue(result, false);
            return result;
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    [Fact]
    public async Task NegativeStampJitterCannotHideAnOutOfBudgetRebaseForHeldAudio()
    {
        await using var h = new Harness();
        h.Packet(1300, marker: 111); // A is due +300 ms, its end stamp is 1310.
        h.Session.MultimediaTimeBase(950); // Backward clock step of 50 ms; held A must now wait until +350 ms.
        h.Packet(1300, marker: 222); // A -10 ms stamp overlap, within the new gap-jitter allowance.
        Assert.Equal(-10, h.LastGap);
        Assert.Equal(350, h.LastDelay);
        var played = h.Drain();
        int firstA = Array.FindIndex(played, sample => sample == 111) / 2;
        int firstB = Array.FindIndex(played, sample => sample == 222) / 2;
        // Both packets may move together when the held timeline is corrected. Pinning B at its former +310 ms
        // position wrongly rejected a valid correction (A +350, B +360); check its deadline budget instead.
        Assert.InRange(firstB, (350 - PcmQueue.ToleranceMs) * 48, (350 + PcmQueue.ToleranceMs) * 48);
        Assert.InRange(firstA, (350 - PcmQueue.ToleranceMs) * 48, (350 + PcmQueue.ToleranceMs) * 48);
    }

    [Fact]
    public async Task EarlierPositiveJitterStillSharesTheBudgetAfterAClockRebase()
    {
        await using var h = new Harness();
        h.Packet(1300, marker: 111);
        h.Packet(1313, marker: 222); // B's +3 ms jitter is left pending: B plays at +310 ms rather than +313 ms.
        h.Session.MultimediaTimeBase(961); // Backward step of 39 ms, itself below the 40 ms threshold.
        h.Packet(1317, marker: 333); // C's absolute jitter is -3 ms; its gap is -6 and the pending lead becomes -3.
        Assert.Equal(-6, h.LastGap);
        Assert.Equal(356, h.LastDelay);
        var played = h.Drain();
        int firstA = Array.FindIndex(played, sample => sample == 111) / 2;
        int firstB = Array.FindIndex(played, sample => sample == 222) / 2;
        int firstC = Array.FindIndex(played, sample => sample == 333) / 2;
        // C need not remain at the former +320 ms: a valid 39 ms correction moves A/B/C to +339/+349/+359.
        // Check every held packet against its rebased deadline, retaining the original B budget assertion.
        Assert.InRange(firstA, (339 - PcmQueue.ToleranceMs) * 48, (339 + PcmQueue.ToleranceMs) * 48);
        Assert.InRange(firstC, (356 - PcmQueue.ToleranceMs) * 48, (356 + PcmQueue.ToleranceMs) * 48);
        Assert.InRange(firstB, (352 - PcmQueue.ToleranceMs) * 48, (352 + PcmQueue.ToleranceMs) * 48);
    }

    [Theory]
    [InlineData(11025)]
    [InlineData(44100)]
    [InlineData(88200)]
    public async Task VariableFractionalPacketsStayContiguousAcrossTimestampWrap(int rate)
    {
        const uint firstStamp = uint.MaxValue - 50;
        await using var h = new Harness(rate, firstStamp);
        int[] sizes = [1, 127, 256, 441, 512];
        var rendered = new short[1024 * 2];
        long elapsedFrames = 0;
        bool wrapped = false;
        for (int i = 0; i < 100; i++)
        {
            int frames = sizes[i % sizes.Length];
            long elapsedMs = elapsedFrames * 1000 / rate;
            h.Clock.Now = elapsedMs;
            uint stamp = unchecked(firstStamp + (uint)elapsedMs);
            wrapped |= stamp < firstStamp;
            h.Packet(stamp, frames);
            Assert.Equal(400, h.LastDelay);
            Assert.Equal(0, h.LastGap);
            h.Queue.Dequeue(rendered.AsSpan(0, frames * 2), false);
            elapsedFrames += frames;
        }
        Assert.True(wrapped);
        var s = h.Queue.Statistics;
        Assert.Equal((0L, 0L, 0L, 1L), (s.SyncDroppedFrames, s.GapSilenceFrames, s.GapTrimmedFrames, s.SyncCorrections));
    }

    [Fact]
    public async Task PlaybackRestartResetsFractionalPhaseWhenTheRateChanges()
    {
        await using var h = new Harness(44100);
        h.Packet(1400, 256); // Leave a fractional end phase at the old rate.
        h.Start(48000);
        h.Packet(1400);
        Assert.Equal(0, h.LastGap);
        h.Packet(1410);
        Assert.Equal(0, h.LastGap);
        Assert.Equal((0L, 0L, 1L), (h.Queue.Statistics.SyncDroppedFrames,
            h.Queue.Statistics.GapSilenceFrames, h.Queue.Statistics.SyncCorrections));
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(60000)]
    public async Task LongLogicalPauseIsReconciledWithoutGrowingThePhysicalQueue(int pauseMs)
    {
        await using var h = new Harness();
        h.Packet(1300);
        h.Drain();
        h.Clock.Now = pauseMs;
        h.Packet((uint)(1300 + pauseMs), marker: 222);
        Assert.Equal(300, h.LastDelay);
        Assert.InRange(h.Queue.Count, 0, (PcmQueue.MaxDelayMs + PcmQueue.RingMs) * 48 * 2);
        h.Clock.Now = pauseMs + 10;
        h.Queue.Dequeue(new short[480 * 2], false);
        h.Packet((uint)(1310 + pauseMs), marker: 333);
        var played = h.Drain();
        Assert.Contains((short)222, played);
        Assert.Contains((short)333, played);
        Assert.Equal(0, h.Queue.Statistics.SyncDroppedFrames);
    }
}
