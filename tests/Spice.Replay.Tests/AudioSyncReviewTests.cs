using Spice.Media;
using Spice.Session;
using VvFile;

namespace Spice.Replay.Tests;

// Window-free regressions from the clock A/V review. Failing cases stay failing until the fixes land.
[Collection("Allocation profiling")]
public sealed class AudioSyncReviewTests
{
    const int Channels = 2;

    sealed class ManualClock : PacingClock
    {
        public long Now;
        public override long NowMs => Now;
        public override void Arm(long atMs) { }
        public override void Disarm() { }
        public override void Dispose() { }
    }

    static SpiceSession Session(PacingClock clock)
    {
        var s = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        s.PacingClock = clock;
        s.VideoPacing = VideoPacing.Clock;
        return s;
    }

    static short[] Packet(int frames, short value) => Enumerable.Repeat(value, frames * Channels).ToArray();

    [Fact]
    public async Task BackwardClockRebaseAlsoDelaysAudioAlreadyWaitingInTheQueue()
    {
        var clock = new ManualClock();
        await using var session = Session(clock);
        session.MultimediaTimeBase(1000);
        var q = new PcmQueue();
        q.Reset(Channels, 1000);

        // Packet A is due at guest time 1300. Stop draining 50 ms before its first sample.
        short[] a = Packet(10, 111), b = Packet(10, 222);
        Assert.Equal(300, q.Write(a, session.AudioDelayMs(1300)));
        Assert.Equal(310, q.ReadyFrames());
        q.Dequeue(new short[250 * Channels], false);
        clock.Now = 250;

        // A new main-channel base moves video A's deadline back by 200 ms. B is due 10 ms after A.
        session.MultimediaTimeBase(1050);
        Assert.Equal(260, session.AudioDelayMs(1310));
        Assert.Equal(200, q.Write(b, session.AudioDelayMs(1310)));
        var played = new short[270 * Channels];
        q.Dequeue(played, false);

        // Both packets must wait for the rebased clock, not just the newly received B.
        Assert.Equal(new short[250 * Channels], played[..(250 * Channels)]);
        Assert.Equal(a, played[(250 * Channels)..(260 * Channels)]);
        Assert.Equal(b, played[(260 * Channels)..]);
    }

    sealed class PausingClock : PacingClock
    {
        public long Now;
        public int PauseNextRead;
        public readonly ManualResetEventSlim Paused = new(false, 0), Resume = new(false, 0);
        public override long NowMs
        {
            get
            {
                if (Interlocked.Exchange(ref PauseNextRead, 0) != 0)
                {
                    Paused.Set();
                    if (!Resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Clock rebase was not released");
                }
                return Volatile.Read(ref Now);
            }
        }
        public override void Arm(long atMs) { }
        public override void Disarm() { }
        public override void Dispose() { Paused.Dispose(); Resume.Dispose(); }
    }

    [Fact]
    public async Task PlaybackThreadNeverSeesAHybridGuestClockDuringRebase()
    {
        var clock = new PausingClock { Now = 1000 };
        await using var session = Session(clock);
        session.MultimediaTimeBase(10_000);
        clock.Now = 2000;
        Assert.Equal(400, session.AudioDelayMs(11_400));

        // A no-op rebase: both the complete old and complete new bases give guest time 11000 now.
        // Pause the main-channel writer while it samples the new local base time. Audio runs on another channel.
        clock.PauseNextRead = 1;
        Task rebase = Task.Run(() => session.MultimediaTimeBase(11_000));
        Task<int>? read = null;
        using var readEntered = new ManualResetEventSlim(false, 0);
        try
        {
            Assert.True(clock.Paused.Wait(TimeSpan.FromSeconds(5)), "The rebase did not reach the controlled clock read");
            read = Task.Run(() => { readEntered.Set(); return session.AudioDelayMs(11_400); });
            Assert.True(readEntered.Wait(TimeSpan.FromSeconds(5)), "The playback reader did not start");
            // A correct implementation may serialize this reader behind the rebase. Do not require lock-free reads
            // or hold the writer indefinitely while waiting for such a reader. The current unlocked reader completes
            // with the hybrid value during this controlled window.
            await Task.WhenAny(read, Task.Delay(100));
        }
        finally
        {
            clock.Resume.Set();
            await rebase;
        }
        Assert.NotNull(read);
        int observed = await read;
        Assert.Equal(400, session.AudioDelayMs(11_400));
        Assert.Equal(400, observed); // A mixed (new guest base, old local base) instead returns -600.
    }

    [Fact]
    public void PublishingDeviceTransferBeforeDequeueMustNotDropInToleranceAudio()
    {
        var q = new PcmQueue();
        q.Reset(Channels, 1000);
        q.Write(Packet(80, 111));
        Assert.Equal(0, q.Write(Packet(10, 222), 90));
        Assert.Equal(90, q.ReadyFrames());

        // Replay the actual AudioOutput.Fill handoff, without opening WASAPI:
        // padding=0, bufferFrames=40, real=frames=40. Fill now publishes padding+frames in the same Transfer as the dequeue.
        // The network callback runs between ReadyFrames and that transfer. The 40 frames being transferred are still in
        // the ring: physically 90 frames precede this packet, so a target of 89 is only 1 ms late, within tolerance.
        int correction = q.Write(Packet(10, 333), 89);
        var transferred = new short[40 * Channels];
        q.Transfer(transferred, false, deviceFrames: 40);
        Assert.Equal(0, correction); // Current split publication double-counts 40 frames and drops 41 ms.
        Assert.Equal(0, q.Statistics.SyncDroppedFrames);
        Assert.All(transferred, sample => Assert.Equal(111, sample));
    }

    [Theory]
    [InlineData(8000, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 2)]
    [InlineData(96000, 8)]
    public void DelayAndCapacityUseFramesAtRealPlaybackRates(int rate, int channels)
    {
        var q = new PcmQueue();
        q.Reset(channels, rate);
        int packetFrames = rate / 100, heldFrames = rate / 100;
        q.DeviceQueuedFrames = heldFrames;
        short[] packet = Enumerable.Repeat((short)123, packetFrames * channels).ToArray();
        int gap = rate * 300 / 1000 - heldFrames;
        Assert.Equal(gap, q.Write(packet, 300));
        Assert.Equal(gap + packetFrames, q.ReadyFrames());
        var played = new short[(gap + packetFrames) * channels];
        q.Dequeue(played, false);
        Assert.All(played[..(gap * channels)], sample => Assert.Equal(0, sample));
        Assert.Equal(packet, played[(gap * channels)..]);

        q.Reset(channels, rate);
        for (int i = 0; i < 100; i++) q.Write(packet, int.MaxValue);
        Assert.InRange(q.Count, 0, (PcmQueue.MaxDelayMs + PcmQueue.RingMs) * rate / 1000 * channels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnderrunAndDeviceReopenReprimeWholeFrames(bool synced)
    {
        var q = new PcmQueue();
        q.Reset(Channels, 1000);
        int delay = synced ? 40 : PcmQueue.Unsynced;
        short[] packet = Packet(20, 321);
        q.Write(packet, delay);
        Assert.Equal(20, q.ReadyFrames()); // 40 ms is within the correction tolerance.
        q.Dequeue(new short[20 * Channels], true);
        Assert.Equal(1, q.Statistics.Underruns);
        Assert.Equal(0, q.ReadyFrames());
        q.Write(Packet(10, 123), delay);
        Assert.Equal(0, q.ReadyFrames());
        q.Write(Packet(10, 456), delay);
        Assert.Equal(20, q.ReadyFrames());
        q.DeviceQueuedFrames = 10;
        q.Unprime();
        Assert.Equal(0, q.DeviceQueuedFrames);
        Assert.Equal(20, q.ReadyFrames());
    }
}
