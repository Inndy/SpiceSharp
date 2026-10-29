using System.Buffers.Binary;
using Spice.Media;
using Spice.Session;
using Spice.Tests;
using Spice.Wire;
using VvFile;
using Xunit.Abstractions;

namespace Spice.Replay.Tests;

// Clock pacing holds audio to the guest clock (docs/decisions/0030). The queue runs at 1000 Hz stereo here, so one
// frame is one millisecond and two samples.
[Collection("Allocation profiling")]
public sealed class AudioSyncTests(ITestOutputHelper output)
{
    const int Rate = 1000, Ch = 2;

    static PcmQueue Queue(int deviceFrames = 0)
    {
        var q = new PcmQueue();
        q.Reset(Ch, Rate);
        q.DeviceQueuedFrames = deviceFrames;
        return q;
    }

    /// <summary><paramref name="frames"/> frames whose samples are 1, 2, 3, ... from <paramref name="first"/>.</summary>
    static short[] Packet(int frames, short first = 1)
    {
        var p = new short[frames * Ch];
        for (int i = 0; i < p.Length; i++) p[i] = (short)(first + i);
        return p;
    }

    static short[] Drain(PcmQueue q, int frames)
    {
        var dst = new short[frames * Ch];
        q.Dequeue(dst, false);
        return dst;
    }

    // ---------------- unsynchronized: as before clock pacing held audio ----------------

    [Fact]
    public void UnsyncedPrimesTrimsAndOverflowsAsBefore()
    {
        var q = Queue();
        q.Write(Packet(10));
        Assert.Equal(0, q.ReadyFrames()); // priming: under 20 ms queued
        q.Write(Packet(15));
        Assert.Equal(25, q.ReadyFrames());
        Assert.Equal(Packet(10).Concat(Packet(15)), Drain(q, 25));

        q = Queue();
        q.Write(Packet(300)); // longer than the 200 ms ring: the oldest 100 ms are dropped
        Assert.Equal(400, q.Count);
        Assert.Equal(100 * Ch, q.Statistics.DroppedSamples);
        Assert.Equal(40, q.ReadyFrames()); // above 100 ms queued: trimmed to 40 ms
        Assert.Equal(Packet(300)[^(40 * Ch)..], Drain(q, 40));
        Assert.Equal((0L, 0L), (q.Statistics.SyncSilenceFrames, q.Statistics.SyncDroppedFrames));
    }

    [Fact]
    public void UnsyncedIgnoresTheDeviceAndAPartialFrame()
    {
        var q = Queue(deviceFrames: 500);
        q.Write(Packet(30).AsSpan(0, 30 * Ch - 1)); // the odd sample of an incomplete frame is not queued
        Assert.Equal(29 * Ch, q.Count);
        Assert.Equal(29, q.ReadyFrames());
    }

    // ---------------- synchronized ----------------

    [Fact]
    public void SynchronizedPacketWaitsBehindSilenceUntilItsDelay()
    {
        var q = Queue(deviceFrames: 10);
        Assert.Equal(290, q.Write(Packet(10), delayMs: 300));
        // 10 ms already in the device, 290 ms of silence, then the packet: heard 300 ms from now.
        Assert.Equal(300 * Ch, q.Count);
        Assert.Equal(290, q.Statistics.SyncSilenceFrames);
        Assert.Equal(300, q.ReadyFrames()); // not trimmed while synchronized
        var played = Drain(q, 300);
        Assert.All(played[..(290 * Ch)], s => Assert.Equal(0, s));
        Assert.Equal(Packet(10), played[(290 * Ch)..]);
    }

    [Fact]
    public void SmallScheduleErrorsAreLeftAlone()
    {
        var q = Queue(deviceFrames: 10);
        q.Write(Packet(10), 300);
        // With audio held, a shift up to 40 - 10 = 30 ms is left alone (each held packet may keep a 10 ms pending lead).
        Assert.Equal(0, q.Write(Packet(10), 310 + 30)); // 30 ms early: within tolerance
        Assert.Equal(310 * Ch, q.Count);
        q.Write(Packet(10), 320 - 30); // 30 ms late: within tolerance
        Assert.Equal(320 * Ch, q.Count);
        Assert.Equal((290L, 0L), (q.Statistics.SyncSilenceFrames, q.Statistics.SyncDroppedFrames));
        q.Write(Packet(10), 330 + 31); // beyond it: realigned exactly (31 ms of silence, then the packet)
        Assert.Equal((320 + 31 + 10) * Ch, q.Count);
        Assert.Equal(321, q.Statistics.SyncSilenceFrames);
    }

    [Fact]
    public void QueueAheadOfTheScheduleDropsItsOldestAudio()
    {
        // Device clock slower than the guest clock, a clock rebased backwards, or silence written during an underrun:
        // the queue plays later than the stamps say.
        var q = Queue(deviceFrames: 10);
        q.Write(Packet(10), 300); // 290 ms silence + 10 ms packet
        Assert.Equal(-110, q.Write(Packet(10, 101), 200)); // should play after 200 ms, but 310 ms are ahead of it
        Assert.Equal(110, q.Statistics.SyncDroppedFrames);
        Assert.Equal(200 * Ch, q.Count); // 190 queued + 10 in the device = 200 ms before it
        var played = Drain(q, 200);
        Assert.All(played[..(180 * Ch)], s => Assert.Equal(0, s)); // the oldest (silence) went first
        Assert.Equal(Packet(10), played[(180 * Ch)..(190 * Ch)]);
        Assert.Equal(Packet(10, 101), played[(190 * Ch)..]);
        Assert.Equal(0L, q.Statistics.DroppedSamples); // schedule drops are counted apart from overflow
    }

    [Fact]
    public void ADropLongerThanTheQueueCutsTheStartOfThePacket()
    {
        var q = Queue(deviceFrames: 100);
        q.Write(Packet(20), -500); // late: aims for the 40 ms cushion; 100 ms are in the device already
        Assert.Equal(20, q.Statistics.SyncDroppedFrames);
        Assert.Equal(0, q.Count);
        q.DeviceQueuedFrames = 70;
        q.Write(Packet(20), -500); // 30 ms over the cushion: within tolerance
        Assert.Equal(20 * Ch, q.Count);
        q.DeviceQueuedFrames = 85;
        q.Write(Packet(20), 0); // 20 queued + 85 in the device, 65 over the cushion: all 20 queued, then all 20 of the packet
        Assert.Equal(20 + 20 + 20, q.Statistics.SyncDroppedFrames);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void LatePacketsKeepAMinimumCushionWithoutRepeatedDrops()
    {
        // Audio that arrives after its time (network delay longer than the clock lead) can't be heard on time: it plays
        // with the 40 ms cushion, as unsynchronized audio does, instead of being dropped packet after packet.
        var q = Queue(deviceFrames: 10);
        for (int i = 0; i < 50; i++)
        {
            q.Write(Packet(10), -200);
            if (q.ReadyFrames() >= 10) Drain(q, 10); // the device plays 10 ms per packet
            Assert.True(q.Count <= 20 * Ch);
        }
        Assert.Equal((0L, 0L, 0L), (q.Statistics.SyncSilenceFrames, q.Statistics.SyncDroppedFrames, q.Statistics.DroppedSamples));
    }

    [Fact]
    public void DelayIsCappedAtTwoSeconds()
    {
        var q = Queue();
        q.Write(Packet(10), 60_000); // a clock far off the stamps
        Assert.Equal((PcmQueue.MaxDelayMs + 10) * Ch, q.Count);
        q.Write(Packet(10), int.MaxValue);
        Assert.Equal((PcmQueue.MaxDelayMs + 20) * Ch, q.Count); // already more than the cap ahead: nothing added
    }

    [Fact]
    public void LeavingSynchronizedPlaybackRestartsAtTheDepthOfAFreshStream()
    {
        var q = Queue(deviceFrames: 10);
        q.Write(Packet(10), 300);
        // Pacing switched off: the held audio is dropped (counted) and the queue primes again, as a new stream does,
        // instead of keeping 40 ms more than a fresh stream (live F2).
        q.Write(Packet(10, 101));
        Assert.Equal(10 * Ch, q.Count);
        Assert.Equal(300 * Ch, q.Statistics.DroppedSamples);
        Assert.Equal(0, q.ReadyFrames()); // 10 ms: under the 20 ms prime
        q.Write(Packet(10, 201));
        Assert.Equal(20, q.ReadyFrames());
        Assert.Equal(Packet(10, 101).Concat(Packet(10, 201)), Drain(q, 20));
    }

    [Fact]
    public void EnteringSynchronizedPlaybackHoldsTheNextPacket()
    {
        var q = Queue(deviceFrames: 10);
        q.Write(Packet(30));
        Assert.Equal(30, q.ReadyFrames());
        q.Write(Packet(10), 400); // pacing switched to clock: 360 ms of silence after what is queued
        Assert.Equal(360, q.Statistics.SyncSilenceFrames);
        Assert.Equal(400, q.ReadyFrames());
    }

    [Fact]
    public void DequeueZeroFillsWhatASynchronizedWriteDroppedMeanwhile()
    {
        var q = Queue(deviceFrames: 10);
        q.Write(Packet(10), 300);
        int ready = q.ReadyFrames();
        q.Write(Packet(10, 101), 50); // on the network thread, between ReadyFrames and Dequeue
        Assert.Equal(50 * Ch, q.Count);
        var dst = new short[ready * Ch];
        dst.AsSpan().Fill(-1);
        q.Dequeue(dst, false);
        Assert.Equal(Packet(10, 101), dst[(40 * Ch)..(50 * Ch)]);
        Assert.All(dst[(50 * Ch)..], s => Assert.Equal(0, s));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void ClosedQueueIgnoresWritesUntilReset()
    {
        var q = Queue();
        q.Write(Packet(10), 300);
        q.Close();
        q.Write(Packet(10), 300);
        Assert.Equal(0, q.Count);
        q.Reset(Ch, Rate);
        Assert.Equal(0, q.DeviceQueuedFrames);
        q.Write(Packet(30));
        Assert.Equal(30, q.ReadyFrames());
    }

    [Fact]
    public void TransferMovesAudioIntoTheDeviceCountInOneStep()
    {
        var q = Queue();
        q.Write(Packet(100));
        Assert.Equal(100, q.ReadyFrames());
        var dst = new short[40 * Ch];
        q.Transfer(dst, false, deviceFrames: 40 + 5); // 40 written into an empty device with 5 ms stream latency
        Assert.Equal(Packet(100)[..(40 * Ch)], dst);
        Assert.Equal((60 * Ch, 45), (q.Count, q.DeviceQueuedFrames));
        // 60 queued + 45 in the device precede the next packet: due in 105 ms it is on time.
        Assert.Equal(0, q.Write(Packet(10), 105));
        q.Transfer([], true, deviceFrames: 10); // starving, nothing taken: re-primes and still publishes
        Assert.Equal((1L, 10), (q.Statistics.Underruns, q.DeviceQueuedFrames));
    }

    [Fact]
    public void AStampGapStaysBetweenThePacketsWhileTheRestRetimesHeldAudio()
    {
        var q = Queue();
        q.Write(Packet(10), 100); // 100 ms ahead: 100 ms of silence, then the packet
        Assert.Equal(110 * Ch, q.Count);
        // The next packet's stamp is 50 ms past the end of the previous one, and the clock also stepped back 60 ms:
        // the 50 ms gap goes between the packets, the 60 ms before everything held.
        Assert.Equal(60, q.Write(Packet(10, 101), 110 + 50 + 60, gapMs: 50)); // the clock correction only
        var played = Drain(q, 230);
        Assert.All(played[..(160 * Ch)], s => Assert.Equal(0, s));
        Assert.Equal(Packet(10), played[(160 * Ch)..(170 * Ch)]);
        Assert.All(played[(170 * Ch)..(220 * Ch)], s => Assert.Equal(0, s));
        Assert.Equal(Packet(10, 101), played[(220 * Ch)..]);
        Assert.Equal((60L, 50L), (q.Statistics.SyncSilenceFrames - 100, q.Statistics.GapSilenceFrames));
        // Stamps 400 ms apart, but the packet due in 160 ms: the held packet was due 240 ms ago, so it is dropped and
        // the next one plays on time.
        q = Queue();
        q.Write(Packet(10), 100);
        Assert.Equal(-110, q.Write(Packet(10, 101), 160, gapMs: 400));
        played = Drain(q, 160 + 10);
        Assert.All(played[..(160 * Ch)], s => Assert.Equal(0, s));
        Assert.Equal(Packet(10, 101), played[(160 * Ch)..]);
        Assert.Equal(2L, q.Statistics.SyncCorrections);
    }

    [Fact]
    public void SmallStampGapsAreLaidOutOnceTheyAddUpAndNeverMoveEarlierAudio()
    {
        var q = Queue();
        q.Write(Packet(10), 300);
        // 4 ms pauses between 10 ms packets: each is within the 10 ms jitter allowance on its own.
        Assert.Equal(0, q.Write(Packet(10, 101), 314, gapMs: 4)); // 4 pending: appended right after
        Assert.Equal(0, q.Write(Packet(10, 201), 328, gapMs: 4)); // 8 pending: still within the allowance
        Assert.Equal(0, q.Write(Packet(10, 301), 342, gapMs: 4)); // 12 pending: laid out before this packet
        var played = Drain(q, 352);
        Assert.Equal(Packet(10), played[(300 * Ch)..(310 * Ch)]); // the first packet never moved
        Assert.Equal(Packet(10, 301), played[(342 * Ch)..]);
        Assert.Equal((0L, 12L), (q.Statistics.SyncCorrections - 1, q.Statistics.GapSilenceFrames));
        // A pause beyond the allowance is laid out at once, exactly.
        Assert.Equal(0, q.Write(Packet(10, 401), 41, gapMs: 11)); // the queue is empty: 30 ms off is within the budget
        Assert.Equal(11L, q.Statistics.GapSilenceFrames - 12);
    }

    [Fact]
    public void ClockShiftAndPendingGapShareOneBudget()
    {
        // With audio held, each held packet may keep a pending lead within ±10 ms: a shift beyond 30 ms is corrected.
        var q = Queue();
        q.Write(Packet(10), 300);
        Assert.Equal(0, q.Write(Packet(10), 310 + 30));
        Assert.Equal(31, q.Write(Packet(10), 320 + 31));
        // With nothing held, only the packet's own pending lead counts with the shift: 32 + 8 = 40 ms is left alone,
        // 33 + 8 = 41 ms is corrected in full, leaving the packet only its pending 8 ms early.
        q = Queue();
        Assert.Equal(0, q.Write(Packet(10), 40, gapMs: 8));
        q = Queue();
        Assert.Equal(33, q.Write(Packet(10, 101), 41, gapMs: 8));
        Assert.Equal(Packet(10, 101), Drain(q, 43)[(33 * Ch)..]);
    }

    [Fact]
    public void AClockShiftMovesEveryHeldPacketIntoTheBudgetWhateverItsPendingPhase()
    {
        // Review D1, with assertions on every packet: A at +300; a 50 ms backward rebase and B overlapping A's stamps
        // by 10 ms (pending). The shift (50 ms) is corrected for all held audio: A moves to its new +350 ms deadline and
        // B, appended after A, lands 10 ms after its own (its pending lead).
        var q = Queue();
        q.Write(Packet(10), 300);
        Assert.Equal(50, q.Write(Packet(10, 101), 350, gapMs: -10));
        var played = Drain(q, 370);
        Assert.Equal(Packet(10), played[(350 * Ch)..(360 * Ch)]);
        Assert.Equal(Packet(10, 101), played[(360 * Ch)..]);
        // ±3 ms jitter: B +3 ms (pending), a 39 ms backward rebase, C -3 ms. A, B and C each end up within 3 ms of
        // their new deadlines (+339, +352, +356).
        q = Queue();
        q.Write(Packet(10), 300);
        Assert.Equal(0, q.Write(Packet(10, 101), 313, gapMs: 3));
        Assert.Equal(39, q.Write(Packet(10, 201), 356, gapMs: -6));
        played = Drain(q, 369);
        Assert.Equal(Packet(10), played[(339 * Ch)..(349 * Ch)]);
        Assert.Equal(Packet(10, 101), played[(349 * Ch)..(359 * Ch)]);
        Assert.Equal(Packet(10, 201), played[(359 * Ch)..]);
    }

    // ---------------- end of stream under clock pacing (live F1) ----------------

    [Fact]
    public void EndedStreamPlaysOutWhatIsHeldThenFinishes()
    {
        var q = Queue();
        q.Write(Packet(10), 300); // 300 ms of silence, then the sound: PLAYBACK_STOP comes long before it is due
        Assert.True(q.BeginDrain());
        Assert.True(q.Draining);
        Assert.False(q.TryFinishDrain(0)); // still queued
        Assert.Equal(310, q.ReadyFrames());
        var dst = new short[310 * Ch];
        q.Transfer(dst, false, 310);
        Assert.Equal(Packet(10), dst[(300 * Ch)..]); // the sound is played, not discarded
        Assert.False(q.TryFinishDrain(40)); // the device still plays it
        Assert.True(q.TryFinishDrain(0));
        Assert.False(q.Draining);
        Assert.Equal(0, q.Statistics.DiscardedSamples);
        q.Write(Packet(10), 300); // closed until the next Reset
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void EndedStreamPlaysItsLastSamplesWithoutThePrimeCushion()
    {
        var q = Queue();
        q.Write(Packet(5), 40); // 5 ms: under the 20 ms prime
        Assert.Equal(0, q.ReadyFrames());
        Assert.True(q.BeginDrain());
        Assert.Equal(5, q.ReadyFrames());
    }

    [Fact]
    public void UnsynchronizedStreamEndsAtOnceAsBefore()
    {
        var q = Queue();
        q.Write(Packet(30));
        Assert.False(q.BeginDrain());
        q.Close();
        Assert.Equal(30 * Ch, q.Statistics.DiscardedSamples); // what a close throws away is counted
    }

    [Fact]
    public void ContinuedStreamIsPlacedByDeadlineUntilThePreviousStreamHasPlayed()
    {
        var q = Queue();
        q.Write(Packet(10), 300);
        q.BeginDrain();
        Assert.True(q.TryContinue(Ch, Rate));
        Assert.Equal(0, q.Write(Packet(10, 101), 100)); // due before the old sound ends: right after it
        Assert.Equal(0, q.Write(Packet(10, 201), 110)); // still no correction while the old sound is held
        Assert.Equal(0, q.Statistics.SyncDroppedFrames);
        var played = Drain(q, 310);
        Assert.Equal(Packet(10), played[(300 * Ch)..]); // the old sound kept its schedule
        // Once it has played, the new stream's own timeline is corrected as usual.
        Assert.Equal(380, q.Write(Packet(10, 301), 400));
    }

    [Fact]
    public void FillChosenBeforeASwitchToOffTakesNothing()
    {
        var q = Queue();
        q.Write(Packet(10), 300);
        Assert.Equal(310, q.ReadyFrames()); // the render thread chose its frames ...
        q.Write(Packet(10, 101)); // ... then pacing was switched off: the hold is flushed, the new packet must prime
        var dst = new short[40 * Ch];
        dst.AsSpan().Fill(-1);
        Assert.Equal(0, q.Transfer(dst, false, 40));
        Assert.All(dst, s => Assert.Equal(0, s));
        Assert.Equal(10 * Ch, q.Count);
        Assert.Equal(0, q.ReadyFrames()); // a fresh choice: still priming
    }

    [Fact]
    public void NewStreamDuringThePlayOutContinuesAfterTheHeldAudio()
    {
        var q = Queue();
        q.Write(Packet(10), 300);
        Assert.True(q.BeginDrain());
        Assert.False(q.TryContinue(Ch, 44100)); // another format: the caller restarts the device
        Assert.True(q.TryContinue(Ch, Rate));
        Assert.False(q.Draining);
        // The new stream's first packet (gap 0 after PLAYBACK_START) starts its own timeline: placed at its deadline
        // after the held sound, which keeps its own.
        Assert.Equal(0, q.Write(Packet(10, 101), 500));
        var played = Drain(q, 510);
        Assert.Equal(Packet(10), played[(300 * Ch)..(310 * Ch)]);
        Assert.Equal(Packet(10, 101), played[(500 * Ch)..]);
        Assert.Equal(1L, q.Statistics.SyncCorrections);
        // A new stream due before the held audio ends is appended right after it, and nothing held is dropped.
        q = Queue();
        q.Write(Packet(10), 300);
        q.BeginDrain();
        Assert.True(q.TryContinue(Ch, Rate));
        Assert.Equal(0, q.Write(Packet(10, 101), 100));
        Assert.Equal(Packet(10, 101), Drain(q, 320)[(310 * Ch)..]);
        // Once the drain finished, a new stream restarts.
        q = Queue();
        q.Write(Packet(10), 300);
        q.BeginDrain();
        q.Transfer(new short[310 * Ch], false, 0);
        Assert.True(q.TryFinishDrain(0));
        Assert.False(q.TryContinue(Ch, Rate));
    }

    [Fact]
    public void JitteringStampsNeitherInsertSilenceNorCorrect()
    {
        var q = Queue();
        q.Write(Packet(10), 300);
        Drain(q, 10); // the device plays 10 ms per packet: each contiguous packet is due 300 ms from now
        for (int i = 0; i < 200; i++)
        {
            int jitter = i % 2 == 0 ? 3 : -3; // stamps alternately 3 ms late and back
            q.Write(Packet(10), 300 + jitter, gapMs: jitter * (i == 0 ? 1 : 2));
            Drain(q, 10);
        }
        Assert.Equal((1L, 0L, 0L), (q.Statistics.SyncCorrections, q.Statistics.GapSilenceFrames, q.Statistics.GapTrimmedFrames));
    }

    [Fact]
    public void OverlappingStampsBeyondTheAllowanceCutThePacketStart()
    {
        var q = Queue();
        q.Write(Packet(10), 300);
        // The stamp is 50 ms before the end of the held audio (and so is its delay): its first 50 ms are already held.
        Assert.Equal(0, q.Write(Packet(60, 101), 260, gapMs: -50));
        Assert.Equal(50, q.Statistics.GapTrimmedFrames);
        var played = Drain(q, 320);
        Assert.Equal(Packet(60, 101)[(50 * Ch)..], played[(310 * Ch)..]);
    }

    [Fact]
    public void PrependedSilenceWrapsAroundTheRing()
    {
        var q = Queue();
        // Move the ring's head 5 frames in, queue 10 frames, then hold them back by 60 ms: the silence goes before the
        // head, across the end of the 200-frame ring.
        q.Write(Packet(5));
        Drain(q, 5);
        q.Write(Packet(5)); q.Write(Packet(5, 11));
        Assert.Equal(60, q.Write(Packet(5, 21), 70));
        var played = Drain(q, 75);
        Assert.All(played[..(60 * Ch)], s => Assert.Equal(0, s));
        Assert.Equal(Packet(5).Concat(Packet(5, 11)).Concat(Packet(5, 21)), played[(60 * Ch)..]);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void WritesAndTransfersAllocateNothingAfterWarmUp(bool synced)
    {
        // A device that plays 10 ms per packet; synchronized, the target moves by 100 ms every 100 packets, so the
        // measured run inserts silence and drops audio too, and counts each correction without formatting anything.
        var q = Queue(deviceFrames: 10);
        short[] packet = Packet(10), dst = new short[10 * Ch];
        int i = 0;
        void Step()
        {
            q.Write(packet, synced ? 300 + (i / 100 % 2) * 100 : PcmQueue.Unsynced);
            if (q.ReadyFrames() >= 10) q.Transfer(dst, false, 10);
            i++;
        }
        for (int k = 0; k < 1000; k++) Step();
        var before = q.Statistics;
        var m = AllocationWindow.Measure($"PcmQueue synced={synced}",
            () => { for (int k = 0; k < 10_000; k++) Step(); },
            prepare: () => { for (int k = 0; k < 100; k++) Step(); },
            report: output.WriteLine);
        var after = q.Statistics;
        output.WriteLine($"synced={synced}: 10,000 packets: {m.Bytes} B, attempts {m.Attempts}; corrections "
            + $"{after.SyncCorrections - before.SyncCorrections}, silence {after.SyncSilenceFrames - before.SyncSilenceFrames}, "
            + $"dropped {after.SyncDroppedFrames - before.SyncDroppedFrames} frames");
        if (synced) Assert.True(after.SyncCorrections - before.SyncCorrections >= 100
            && after.SyncSilenceFrames > before.SyncSilenceFrames && after.SyncDroppedFrames > before.SyncDroppedFrames);
        else Assert.Equal(before.SyncCorrections, after.SyncCorrections);
    }

    // ---------------- the session: each packet's delay ----------------

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
        public readonly ManualClock Clock = new();
        public int Packets, LastDelay, LastGap, LastSamples;
        readonly byte[] _data = new byte[4 + 480 * 2 * 2]; // stamp + 10 ms of 48 kHz stereo s16
        readonly byte[] _start = new byte[14];
        public Harness(VideoPacing mode, bool seed = true)
        {
            Session.PacingClock = Clock;
            Session.VideoPacing = mode;
            if (seed) Session.MultimediaTimeBase(1000);
            Session.AudioData += (pcm, delay, gap) => { Packets++; LastDelay = delay; LastGap = gap; LastSamples = pcm.Length; };
            BinaryPrimitives.WriteUInt32LittleEndian(_start, 2); // channels
            BinaryPrimitives.WriteUInt16LittleEndian(_start.AsSpan(4), 1); // s16
            BinaryPrimitives.WriteUInt32LittleEndian(_start.AsSpan(6), 48000);
            Start();
        }
        public void Start() => Session.OnPlaybackMessage(PlaybackMsg.Start, _start);
        public int Data(uint mm)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_data, mm);
            Session.OnPlaybackMessage(PlaybackMsg.Data, _data);
            return LastDelay;
        }
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    [Fact]
    public async Task ClockPacingDelaysEachPacketByItsLeadOverTheGuestClock()
    {
        await using var h = new Harness(VideoPacing.Clock);
        Assert.Equal(400, h.Data(1400)); // guest clock at 1000
        Assert.Equal(960, h.LastSamples);
        h.Clock.Now += 100;
        Assert.Equal(310, h.Data(1410));
        Assert.Equal(-90, h.Data(1010)); // late: PcmQueue keeps its minimum cushion
        h.Session.MultimediaTimeBase(900); // the server raised its latency: the clock steps back
        Assert.Equal(510, h.Data(1410));
    }

    [Theory] [InlineData(VideoPacing.Off)] [InlineData(VideoPacing.Spacing)]
    public async Task OtherModesPlayAudioOnArrival(VideoPacing mode)
    {
        await using var h = new Harness(mode);
        Assert.Equal(PcmQueue.Unsynced, h.Data(1400));
        Assert.Equal(1, h.Packets);
    }

    [Fact]
    public async Task UnseededClockPlaysAudioOnArrival()
    {
        await using var h = new Harness(VideoPacing.Clock, seed: false);
        Assert.Equal(PcmQueue.Unsynced, h.Data(1400));
    }

    [Fact]
    public async Task PacingChangesApplyFromTheNextPacket()
    {
        await using var h = new Harness(VideoPacing.Off);
        Assert.Equal(PcmQueue.Unsynced, h.Data(1400));
        h.Session.VideoPacing = VideoPacing.Clock;
        Assert.Equal(400, h.Data(1400));
        h.Session.VideoPacing = VideoPacing.Spacing;
        Assert.Equal(PcmQueue.Unsynced, h.Data(1400));
    }

    [Fact]
    public async Task PlaybackLatencyIsRecorded()
    {
        await using var h = new Harness(VideoPacing.Clock);
        Assert.Equal(0u, h.Session.PlaybackLatencyMs);
        var latency = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(latency, 600);
        h.Session.OnPlaybackMessage(PlaybackMsg.Latency, latency);
        Assert.Equal(600u, h.Session.PlaybackLatencyMs);
        Assert.Equal(400, h.Data(1400)); // the schedule follows the stamps against the clock the video uses
    }

    [Fact]
    public async Task EachPacketCarriesTheGapAfterThePreviousPacketsStamps()
    {
        await using var h = new Harness(VideoPacing.Clock);
        h.Data(1400); Assert.Equal(0, h.LastGap); // nothing before it
        h.Data(1410); Assert.Equal(0, h.LastGap); // contiguous: 10 ms packets
        h.Data(1520); Assert.Equal(100, h.LastGap); // the guest's stamps skipped 100 ms
        h.Data(1525); Assert.Equal(-5, h.LastGap); // overlapping stamps: a negative gap
        h.Start();
        h.Data(3000); Assert.Equal(0, h.LastGap); // a new playback starts a new timeline
        h.Session.VideoPacing = VideoPacing.Off;
        h.Data(4000); Assert.Equal(0, h.LastGap); // unsynchronized: no gap
    }

    [Fact]
    public async Task NewClockBaseIsReadWholeByThePlaybackThread()
    {
        // The base is one published object: the delay comes from either the old or the new base, never a mix (review A1;
        // AudioSyncReviewTests drives the race itself).
        await using var h = new Harness(VideoPacing.Clock);
        h.Clock.Now = 1000;
        Assert.Equal(-600, h.Data(1400)); // the base from local time 0: guest clock at 2000
        h.Session.MultimediaTimeBase(1000); // the guest clock steps back 1000 ms
        Assert.Equal(400, h.Data(1400));
        Assert.Equal(1000u, h.Session.MultimediaTime);
    }

    [Fact]
    public async Task ClockPacedPacketsAllocateNothingAfterWarmUp()
    {
        await using var h = new Harness(VideoPacing.Clock);
        uint i = 0;
        void Step() { h.Data(1400 + 10 * i); h.Clock.Now += 10; i++; }
        for (int k = 0; k < 1000; k++) Step();
        var m = AllocationWindow.Measure("clock-paced PLAYBACK_DATA",
            () => { for (int k = 0; k < 10_000; k++) Step(); },
            prepare: () => { for (int k = 0; k < 100; k++) Step(); },
            report: output.WriteLine);
        output.WriteLine($"10,000 clock-paced PLAYBACK_DATA messages: {m.Bytes} B, attempts {m.Attempts}");
        Assert.Equal((int)i, h.Packets); Assert.Equal(400, h.LastDelay); Assert.Equal(0, h.LastGap);
    }
}
