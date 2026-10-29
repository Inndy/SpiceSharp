using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using Spice.Native;
using Spice.Session;
using Spice.Wire;
using VvFile;
using Xunit.Abstractions;

namespace Spice.Replay.Tests;

// Stream pacing (docs/decisions/0025), driven through the display handler with a manual clock: no timers, no sleeps.
// Reflection reads private stream state and the internal codec binding in the untrimmed test build.
#pragma warning disable IL2026, IL2075
[Collection("Allocation profiling")]
public sealed partial class StreamPacingTests(ITestOutputHelper output)
{
    const int W = 3, H = 2;
    const uint Epoch = 1000;
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly SpiceRect Destination = new(0, 0, W, H);
    static readonly Lazy<byte[]> Jpeg = new(EncodeJpeg);

    /// <summary>Time moves only when a test says so; an armed wake fires once its deadline is reached.</summary>
    sealed class ManualClock : PacingClock
    {
        public long Now;
        public long? ArmedAt;
        public bool Disposed;
        public override long NowMs => Now;
        public override void Arm(long atMs) { if (!Disposed) ArmedAt = atMs; }
        public override void Disarm() => ArmedAt = null;
        public override void Dispose() { Disposed = true; ArmedAt = null; }
        public void Advance(long ms)
        {
            Now += ms;
            if (ArmedAt is long at && at <= Now) { ArmedAt = null; Wake?.Invoke(); }
        }
    }

    sealed class Harness : IAsyncDisposable
    {
        public readonly SpiceSession Session = new(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        public readonly ManualClock Clock = new();
        public int Draws;
        public Harness(bool seed = true, VideoPacing mode = VideoPacing.Spacing)
        {
            Session.PacingClock = Clock;
            Session.VideoPacing = mode;
            if (seed) Session.MultimediaTimeBase(Epoch);
            Session.Invalidated += _ => Draws++;
            Session.OnDisplayMessage(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write(10u); w.Write(8u); w.Write(32u); w.Write(1u); }));
            Session.OnDisplayMessage(DisplayMsg.StreamCreate, Create());
            Draws = 0; // creating the primary surface invalidates it; count stream frames only
        }
        public void Frame(uint mm) => Session.OnDisplayMessage(DisplayMsg.StreamData, Data(mm));
        public void Send(ushort type, byte[] body) => Session.OnDisplayMessage(type, body);
        public object? Stream(uint id = 7) => ((IDictionary)Field("_streams").GetValue(Session)!)[id];
        public T State<T>(string name, uint id = 7) { var s = Stream(id)!; return (T)s.GetType().GetField(name)!.GetValue(s)!; }
        public long QueuedBytes => (long)Field("_queuedBytes").GetValue(Session)!;
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    static FieldInfo Field(string name) => typeof(SpiceSession).GetField(name, PrivateInstance)!;
    static byte[] Bytes(Action<BinaryWriter> write)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }
    static void Rect(BinaryWriter w, SpiceRect r) { w.Write(r.Top); w.Write(r.Left); w.Write(r.Bottom); w.Write(r.Right); }
    static byte[] Create(uint id = 7) => Bytes(w =>
    {
        w.Write(0u); w.Write(id); w.Write(StreamCreate.FlagTopDown); w.Write((byte)VideoCodecType.Mjpeg); w.Write(0UL);
        w.Write((uint)W); w.Write((uint)H); w.Write((uint)W); w.Write((uint)H); Rect(w, Destination); w.Write(SpiceClip.TypeNone);
    });
    static byte[] Data(uint mm, uint id = 7) => Bytes(w => { w.Write(id); w.Write(mm); w.Write((uint)Jpeg.Value.Length); w.Write(Jpeg.Value); });

    unsafe delegate int Compress(nint handle, byte* pixels, int width, int stride, int height, int format, ref nint output, ref nuint size);
    static T Native<T>(string name) where T : Delegate
        => typeof(JpegDecoder).Assembly.GetType("Spice.Native.CodecApi", true)!
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<T>();
    static unsafe byte[] EncodeJpeg()
    {
        byte[] pixels = [20, 40, 220, 255, 20, 40, 220, 255, 20, 40, 220, 255, 210, 170, 30, 255, 210, 170, 30, 255, 210, 170, 30, 255];
        var init = Native<Func<int, nint>>("tj3Init"); var set = Native<Func<nint, int, int, int>>("tj3Set");
        var compress = Native<Compress>("tj3Compress8"); var free = Native<Action<nint>>("tj3Free");
        var destroy = Native<Action<nint>>("tj3Destroy");
        nint compressor = init(0), result = 0; nuint size = 0;
        Assert.NotEqual(0, compressor);
        try
        {
            Assert.Equal(0, set(compressor, 3, 90)); // quality
            Assert.Equal(0, set(compressor, 4, 0)); // 4:4:4
            fixed (byte* src = pixels) Assert.Equal(0, compress(compressor, src, W, W * 4, H, 3, ref result, ref size));
            return new ReadOnlySpan<byte>((void*)result, checked((int)size)).ToArray();
        }
        finally { if (result != 0) free(result); destroy(compressor); }
    }

    [Fact]
    public async Task FrameAheadOfItsPredecessorWaitsForItsSpacingOnTheGuestTimeline()
    {
        await using var h = new Harness();
        h.Frame(Epoch); // on the guest clock: drawn on arrival
        Assert.Equal(1, h.Draws); Assert.Null(h.Clock.ArmedAt);
        h.Frame(Epoch + 40); // arrives with its predecessor: held for the 40 ms between them, less the 2 ms catch-up
        Assert.Equal(1, h.Draws); Assert.Equal(38, h.Clock.ArmedAt); Assert.Equal(1, h.State<int>("QueueCount"));
        h.Clock.Advance(37);
        Assert.Equal(1, h.Draws);
        h.Clock.Advance(1);
        Assert.Equal(2, h.Draws); Assert.Null(h.Clock.ArmedAt); Assert.Equal(0, h.State<int>("QueueCount"));
        Assert.Equal(0L, h.QueuedBytes);
        Assert.Equal(2u, h.State<uint>("Frames")); Assert.Equal(0u, h.State<uint>("Drops"));
    }

    [Fact]
    public async Task FirstFrameAheadOfTheGuestClockDrawsOnArrivalWithoutAddedLatency()
    {
        // spice-server stamps frames ahead of the clock it gives the client; waiting out that lead would delay all
        // video by it (the decision record explains why the schedule follows frame spacing, not absolute time).
        await using var h = new Harness();
        h.Frame(Epoch + 400);
        Assert.Equal(1, h.Draws); Assert.Null(h.Clock.ArmedAt);
    }

    [Fact]
    public async Task LateFrameIsDroppedBeforeDecodeAndCountedForTheReport()
    {
        await using var h = new Harness();
        h.Clock.Advance(200); // guest clock now Epoch + 200
        h.Frame(Epoch + 200 - 81);
        Assert.Equal(0, h.Draws); Assert.Null(Field("_mjpeg").GetValue(h.Session)); Assert.Null(h.Clock.ArmedAt);
        Assert.Equal(1u, h.State<uint>("Frames")); Assert.Equal(1u, h.State<uint>("Drops"));
        h.Frame(Epoch + 200 - 80); // exactly at the tolerance: still drawn
        Assert.Equal(1, h.Draws); Assert.Equal(2u, h.State<uint>("Frames")); Assert.Equal(1u, h.State<uint>("Drops"));
    }

    [Fact]
    public async Task UnseededClockDrawsEveryFrameOnArrival()
    {
        await using var h = new Harness(seed: false);
        h.Frame(5); h.Frame(45); h.Frame(4000);
        Assert.Equal(3, h.Draws); Assert.Null(h.Clock.ArmedAt); Assert.Equal(0u, h.State<uint>("Drops"));
    }

    public static IEnumerable<object[]> Removals() => [["destroy"], ["destroy-all"], ["recreate"]];

    [Theory] [MemberData(nameof(Removals))]
    public async Task RemovingTheStreamDiscardsQueuedFramesAndCancelsTheWake(string removal)
    {
        await using var h = new Harness();
        h.Frame(Epoch); h.Frame(Epoch + 40);
        Assert.Equal(38, h.Clock.ArmedAt); Assert.NotEqual(0L, h.QueuedBytes);
        switch (removal)
        {
            case "destroy": h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u))); break;
            case "destroy-all": h.Send(DisplayMsg.StreamDestroyAll, []); break;
            default: h.Send(DisplayMsg.StreamCreate, Create()); break;
        }
        Assert.Null(h.Clock.ArmedAt); Assert.Equal(0L, h.QueuedBytes);
        if (removal == "recreate") Assert.Equal(0, h.State<int>("QueueCount"));
        h.Clock.Advance(1000);
        Assert.Equal(1, h.Draws);
    }

    [Fact]
    public async Task OtherStreamsKeepTheirWakeWhenOneIsDestroyed()
    {
        await using var h = new Harness();
        h.Send(DisplayMsg.StreamCreate, Create(8));
        h.Session.OnDisplayMessage(DisplayMsg.StreamData, Data(Epoch, 8)); h.Session.OnDisplayMessage(DisplayMsg.StreamData, Data(Epoch + 30, 8));
        h.Frame(Epoch); h.Frame(Epoch + 20);
        Assert.Equal(2, h.Draws); Assert.Equal(18, h.Clock.ArmedAt);
        h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u)));
        Assert.Equal(28, h.Clock.ArmedAt);
        h.Clock.Advance(28);
        Assert.Equal(3, h.Draws); Assert.Null(h.Clock.ArmedAt);
    }

    [Fact]
    public async Task SessionEndCancelsTheWakeAndDropsQueuedFrames()
    {
        var h = new Harness();
        h.Frame(Epoch); h.Frame(Epoch + 40);
        Assert.Equal(38, h.Clock.ArmedAt);
        await h.DisposeAsync();
        Assert.True(h.Clock.Disposed); Assert.Null(h.Clock.ArmedAt); Assert.Equal(0L, h.QueuedBytes);
        h.Clock.Wake!(); // a wake already in flight when the session ended does nothing
        Assert.Equal(1, h.Draws);
    }

    [Fact]
    public async Task TimestampsGoingBackwardsDropTheQueuedTimelineAndDrawNow()
    {
        await using var h = new Harness();
        h.Frame(Epoch); h.Frame(Epoch + 40);
        h.Frame(Epoch - 10);
        Assert.Equal(2, h.Draws); Assert.Null(h.Clock.ArmedAt); Assert.Equal(1u, h.State<uint>("Drops"));
    }

    [Fact]
    public async Task BurstIsBoundedAndOnlyTheNewestDueFrameIsDecoded()
    {
        await using var h = new Harness();
        h.Frame(Epoch);
        for (uint k = 1; k <= 40; k++) h.Frame(Epoch + 10 * k); // a stalled link delivering 40 frames at once
        // Deadlines 8, 16, ... 48, then capped at 50 ms after arrival; the queue keeps the newest 32.
        Assert.Equal(1, h.Draws); Assert.Equal(32, h.State<int>("QueueCount")); Assert.Equal(8u, h.State<uint>("Drops"));
        Assert.Equal(50, h.Clock.ArmedAt);
        h.Clock.Advance(50);
        // All 32 are due at once: the 31 superseded ones are dropped without decoding, the newest drawn.
        Assert.Equal(2, h.Draws); Assert.Equal(39u, h.State<uint>("Drops")); Assert.Equal(41u, h.State<uint>("Frames"));
        Assert.Null(h.Clock.ArmedAt); Assert.Equal(0L, h.QueuedBytes);
    }

    // ---------------- clock mode (spice-gtk) ----------------

    [Fact]
    public async Task ClockModeHoldsAnEarlyFrameUntilTheGuestClockReachesItsTimestamp()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        h.Frame(Epoch + 400); // the server's typical lead: waits it out, unlike spacing mode
        Assert.Equal(0, h.Draws); Assert.Equal(400, h.Clock.ArmedAt);
        h.Frame(Epoch + 433);
        Assert.Equal(400, h.Clock.ArmedAt); Assert.Equal(2, h.State<int>("QueueCount"));
        h.Clock.Advance(399);
        Assert.Equal(0, h.Draws);
        h.Clock.Advance(1);
        Assert.Equal(1, h.Draws); Assert.Equal(433, h.Clock.ArmedAt);
        h.Clock.Advance(33);
        Assert.Equal(2, h.Draws); Assert.Null(h.Clock.ArmedAt); Assert.Equal(0u, h.State<uint>("Drops"));
    }

    [Fact]
    public async Task ClockModeDropsAnyFrameBehindTheGuestClock()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        h.Clock.Advance(200);
        h.Frame(Epoch + 199); // 1 ms late: dropped before decode, as spice-gtk drops a late MJPEG frame
        Assert.Equal(0, h.Draws); Assert.Null(Field("_mjpeg").GetValue(h.Session)); Assert.Equal(1u, h.State<uint>("Drops"));
        h.Frame(Epoch + 200);
        Assert.Equal(1, h.Draws); Assert.Equal(1u, h.State<uint>("Drops"));
    }

    [Fact]
    public async Task ClockModeFollowsANewGuestClockBase()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        h.Frame(Epoch + 400);
        Assert.Equal(400, h.Clock.ArmedAt);
        h.Session.MultimediaTimeBase(Epoch + 300); // MAIN_MULTI_MEDIA_TIME: the server lowered its latency
        Assert.Equal(100, h.Clock.ArmedAt);
    }

    // ---------------- off ----------------

    [Fact]
    public async Task OffDrawsEveryFrameOnArrivalAndDropsNothing()
    {
        await using var h = new Harness(mode: VideoPacing.Off);
        h.Frame(Epoch); h.Frame(Epoch + 40); h.Frame(Epoch + 400);
        h.Clock.Advance(5000);
        h.Frame(Epoch + 41); // far behind the guest clock, and backwards
        Assert.Equal(4, h.Draws); Assert.Null(h.Clock.ArmedAt); Assert.Equal(0u, h.State<uint>("Drops"));
        Assert.Equal(4u, h.State<uint>("Frames")); Assert.Equal(0L, h.QueuedBytes);
    }

    // ---------------- switching ----------------

    [Fact]
    public async Task SwitchingToOffDrawsTheNewestQueuedFrameNowAndCancelsTheWake()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        h.Frame(Epoch + 100); h.Frame(Epoch + 133);
        Assert.Equal(0, h.Draws); Assert.Equal(100, h.Clock.ArmedAt);
        h.Session.VideoPacing = VideoPacing.Off;
        Assert.Equal(1, h.Draws); Assert.Equal(1u, h.State<uint>("Drops")); // the older one is superseded
        Assert.Null(h.Clock.ArmedAt); Assert.Equal(0L, h.QueuedBytes);
        h.Clock.Advance(1000);
        Assert.Equal(1, h.Draws);
    }

    [Fact]
    public async Task SwitchingFromClockToSpacingCapsTheWaitAtTheSpacingLimit()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        h.Frame(Epoch + 400);
        Assert.Equal(400, h.Clock.ArmedAt);
        h.Session.VideoPacing = VideoPacing.Spacing;
        Assert.Equal(50, h.Clock.ArmedAt);
        h.Clock.Advance(50);
        Assert.Equal(1, h.Draws); Assert.Null(h.Clock.ArmedAt);
    }

    [Fact]
    public async Task SwitchingFromSpacingToClockWaitsForTheTimestamp()
    {
        await using var h = new Harness();
        h.Frame(Epoch); h.Frame(Epoch + 40);
        Assert.Equal(38, h.Clock.ArmedAt);
        h.Session.VideoPacing = VideoPacing.Clock;
        Assert.Equal(40, h.Clock.ArmedAt);
        h.Clock.Advance(39); Assert.Equal(1, h.Draws);
        h.Clock.Advance(1); Assert.Equal(2, h.Draws); Assert.Null(h.Clock.ArmedAt);
    }

    [Fact]
    public async Task SwitchingAfterTheSessionEndedArmsNothing()
    {
        var h = new Harness();
        h.Frame(Epoch); h.Frame(Epoch + 40);
        await h.DisposeAsync();
        h.Session.VideoPacing = VideoPacing.Clock;
        h.Session.VideoPacing = VideoPacing.Off;
        Assert.Null(h.Clock.ArmedAt); Assert.Equal(1, h.Draws);
    }

    [Theory] [InlineData(VideoPacing.Spacing)] [InlineData(VideoPacing.Clock)] [InlineData(VideoPacing.Off)]
    public async Task PacedFramesAllocateNothingAfterWarmUp(VideoPacing mode)
    {
        await using var h = new Harness(mode: mode);
        byte[] a = Data(0), b = Data(0);
        // Each 66 ms: one frame on time (drawn on arrival) and one 33 ms ahead (queued, drawn by the wake).
        void Step(int i)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), Epoch + (uint)(66 * i));
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), Epoch + (uint)(66 * i + 33));
            h.Session.OnDisplayMessage(DisplayMsg.StreamData, a);
            h.Session.OnDisplayMessage(DisplayMsg.StreamData, b);
            h.Clock.Advance(66);
        }
        int i = 0;
        for (; i < 100; i++) Step(i);
        Assert.Equal(200, h.Draws);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (; i < 1100; i++) Step(i);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{mode}: 1000 frame pairs (2000 frames, up to 1000 queued): {bytes} B.");
        Assert.Equal(2200, h.Draws); Assert.Equal(0u, h.State<uint>("Drops"));
        Assert.Equal(0, bytes);
    }
}
