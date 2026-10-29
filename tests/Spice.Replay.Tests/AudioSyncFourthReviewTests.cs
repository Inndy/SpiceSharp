using Spice.Media;

namespace Spice.Replay.Tests;

// Window-free queue/lifecycle regressions. Renderer-order tests replay its public queue calls, not WASAPI.
[Collection("Allocation profiling")]
public sealed class AudioSyncFourthReviewTests
{
    const int Channels = 2, Rate = 1000;

    static PcmQueue Queue()
    {
        var q = new PcmQueue();
        q.Reset(Channels, Rate);
        return q;
    }

    static short[] Packet(int frames, short marker) => Enumerable.Repeat(marker, frames * Channels).ToArray();

    [Fact]
    public void PartiallyLaidSourceGapCannotInvalidateTheHeldPacketBudgetAfterAnotherRebase()
    {
        var q = Queue();
        q.Write(Packet(10, 111), 300); // Clock 1000: A stamped 1300.
        // Clock steps forward 30 ms to 1030; B stamped 1370 follows A's end by a real 60 ms pause.
        q.Write(Packet(10, 222), 340, gapMs: 60);
        // Clock now steps backwards 60 ms to 970. A/B/C's deadlines become +330/+400/+410 ms.
        q.Write(Packet(10, 333), 410);
        var played = new short[q.Count];
        q.Dequeue(played, false);
        int a = Array.FindIndex(played, sample => sample == 111) / Channels;
        int b = Array.FindIndex(played, sample => sample == 222) / Channels;
        int c = Array.FindIndex(played, sample => sample == 333) / Channels;
        Assert.InRange(a, 330 - PcmQueue.ToleranceMs, 330 + PcmQueue.ToleranceMs);
        Assert.InRange(c, 410 - PcmQueue.ToleranceMs, 410 + PcmQueue.ToleranceMs);
        Assert.InRange(b, 400 - PcmQueue.ToleranceMs, 400 + PcmQueue.ToleranceMs);
        // Only 30 of the first gap's 60 ms was laid out. That wider historical phase defeats the assumed ±10 ms bound:
        // current code plays B at +340 ms (60 ms early), while A and C are each only 30 ms early.
    }

    [Fact]
    public void SecondPacketOfContinuedStreamCannotRetimeThePreviousStreamsTail()
    {
        var q = Queue();
        q.Write(Packet(10, 111), 300);
        Assert.True(q.BeginDrain());
        Assert.True(q.TryContinue(Channels, Rate));
        Assert.Equal(0, q.Write(Packet(10, 222), 100)); // An earlier new deadline: append after the held tail.
        q.Write(Packet(10, 333), 110); // The rest of the same new timeline, not a guest-clock rebase.

        var played = new short[q.Count];
        q.Dequeue(played, false);
        int firstOldFrame = Array.FindIndex(played, sample => sample == 111) / Channels;
        Assert.InRange(firstOldFrame, 300 - PcmQueue.ToleranceMs, 300 + PcmQueue.ToleranceMs);
        // The new stream may need a late-packet policy; this assertion only protects the previous tail's schedule.
        // Currently the second new packet drops 210 ms from the held timeline: the old tail plays at +90 ms.
    }

    [Fact]
    public async Task ContinueAndFinishDrainHaveExactlyOneWinner()
    {
        var q = Queue();
        q.Write(Packet(5, 111), 40);
        Assert.True(q.BeginDrain());
        q.Transfer(new short[5 * Channels], false, 0);
        using var gate = new ManualResetEventSlim(false, 0);
        Task<bool> resume = Task.Run(() => { gate.Wait(); return q.TryContinue(Channels, Rate); });
        Task<bool> finish = Task.Run(() => { gate.Wait(); return q.TryFinishDrain(0); });
        gate.Set();
        bool[] winners = await Task.WhenAll(resume, finish).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(winners[0] ^ winners[1]);
        Assert.False(q.Draining);
        q.Write(Packet(10, 222), 100);
        if (winners[0]) Assert.True(q.Count > 0);
        else Assert.Equal(0, q.Count);
    }

    [Fact]
    public void ImmediateCloseCountsDiscardedQueueOnlyOnceAndCannotContinue()
    {
        var q = Queue();
        q.Write(Packet(10, 111), 300);
        long held = q.Count;
        Assert.True(q.BeginDrain());
        q.Close(); // Stop/disconnect.
        q.Close(); // RenderThread.Finish after StopCore: must not count the same queue twice.
        Assert.Equal(held, q.Statistics.DiscardedSamples);
        Assert.False(q.BeginDrain());
        Assert.False(q.TryContinue(Channels, Rate));
        q.Write(Packet(10, 222), 300);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void EmptyDrainOnAReopenedDeviceRequestsImmediateRetirement()
    {
        var q = Queue();
        q.Write(Packet(10, 111), 300);
        Assert.True(q.BeginDrain());
        q.Transfer(new short[q.Count], false, 40); // All samples handed to the previous device.
        q.Unprime(); // Old device went away; a fresh endpoint starts with zero padding.
        Assert.True(q.TryFinishDrain(0)); // Pre-roll Fill must propagate this result instead of starting a new client.
        Assert.False(q.Draining);
        Assert.False(q.TryContinue(Channels, Rate));
    }
}
