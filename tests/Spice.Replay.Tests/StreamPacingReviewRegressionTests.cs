using System.Buffers.Binary;
using System.Collections;
using Spice.Session;
using Spice.Transport;
using Spice.Wire;

namespace Spice.Replay.Tests;

// Uses the existing manual-clock harness. Large payloads below deliberately have invalid JPEG bodies:
// queue admission and memory accounting must be safe before a deferred frame's codec sees those bytes.
#pragma warning disable IL2026, IL2075
public sealed partial class StreamPacingTests
{
    const long FrameByteBudget = 64L << 20;

    sealed class SerializedReportSink(int expectedReports) : MemoryStream
    {
        readonly object sync = new();
        public readonly TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Snapshot() { lock (sync) return ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            lock (sync)
            {
                base.Write(bytes.Span);
                if (Length >= expectedReports * 38) Complete.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }
    }

    static SpiceChannel ReportChannel(Harness h, SerializedReportSink sink)
    {
        var channel = (SpiceChannel)typeof(SpiceChannel).GetConstructors(PrivateInstance).Single().Invoke(
            [sink, new ChannelId(ChannelType.Display, 0),
             new LinkReply(LinkError.Ok, [], new CapabilitySet(CommonCap.MiniHeader), new CapabilitySet())]);
        Field("_display").SetValue(h.Session, channel);
        return channel;
    }

    static void ActivateReports(Harness h, uint unique, uint window = 1) => h.Send(DisplayMsg.StreamActivateReport,
        Bytes(w => { w.Write(7u); w.Write(unique); w.Write(window); w.Write(1000u); }));

    static void AssertReport(byte[] bytes, int index, uint unique, uint stamp, uint drops = 0)
    {
        int offset = index * 38;
        var header = DataHeader.Parse(bytes.AsSpan(offset, 6), true);
        Assert.Equal(DisplayMsg.CStreamReport, header.Type);
        Assert.Equal(32u, header.Size);
        uint[] fields = Enumerable.Range(0, 8)
            .Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 6 + i * 4))).ToArray();
        Assert.Equal(new uint[] { 7, unique, stamp, stamp, 1, drops, 0, 0 }, fields);
    }

    [Fact]
    public async Task DeferredDropsDoNotLeakIntoTheNextSerializedReportWindow()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        using var sink = new SerializedReportSink(5);
        await using var channel = ReportChannel(h, sink);
        ActivateReports(h, 0x1234);
        foreach (uint stamp in new uint[] { 1400, 1433, 1466, 1499 }) h.Frame(stamp);
        Assert.Equal(4, h.State<int>("QueueCount"));
        Assert.Equal(0, h.Draws);

        h.Clock.Advance(500); // One late wake drops three frames whose one-frame reports were already sent.
        Assert.Equal(1, h.Draws);
        Assert.Equal(3u, h.State<uint>("EarlierDrops"));
        h.Frame(1532);
        await sink.Complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] reports = sink.Snapshot();
        Assert.Equal(5 * 38, reports.Length);
        uint[] stamps = [1400, 1433, 1466, 1499, 1532];
        for (int i = 0; i < stamps.Length; i++) AssertReport(reports, i, 0x1234, stamps[i]);
        Assert.Equal(0u, h.State<uint>("Drops"));
        Assert.Equal(5u, h.State<uint>("EarlierFrames"));
    }

    [Fact]
    public async Task ReactivationKeepsOldQueuedDropsOutOfTheNewReportId()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        using var sink = new SerializedReportSink(1);
        await using var channel = ReportChannel(h, sink);
        ActivateReports(h, 11, 10);
        h.Frame(1400); h.Frame(1433);
        ActivateReports(h, 22);
        h.Clock.Advance(500);
        h.Frame(1532);
        await sink.Complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] reports = sink.Snapshot();
        Assert.Equal(38, reports.Length);
        AssertReport(reports, 0, 22, 1532);
        Assert.Equal(1u, h.State<uint>("EarlierDrops"));
        Assert.Equal(1, h.Draws);
    }

    static byte[] LargeFrame(int payloadBytes, uint stamp = Epoch + 400, uint id = 7)
    {
        var message = new byte[12 + payloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(message, id);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), stamp);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), (uint)payloadBytes);
        return message;
    }

    // These inspect owned arrays, not just the counters which could themselves undercount capacity.
    static (long Queued, long Pooled) AssertBufferBudgets(Harness h)
    {
        long queued = 0;
        foreach (object stream in ((IDictionary)Field("_streams").GetValue(h.Session)!).Values)
        {
            var type = stream.GetType();
            foreach (object frame in (Array)type.GetField("Queue")!.GetValue(stream)!)
                if (frame.GetType().GetField("Data")!.GetValue(frame) is byte[] data) queued += data.Length;
        }
        var spares = (byte[]?[])Field("_framePool").GetValue(h.Session)!;
        long pooled = spares.Sum(b => (long)(b?.Length ?? 0));
        Assert.Equal(queued, h.QueuedBytes);
        Assert.Equal(pooled, (long)Field("_pooledBytes").GetValue(h.Session)!);
        Assert.InRange(queued, 0, FrameByteBudget);
        Assert.InRange(pooled, 0, FrameByteBudget);
        return (queued, pooled);
    }

    [Fact]
    public async Task QueueByteCapCountsRoundedCapacityAcrossStreamsAndRejectsOverflow()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        for (uint id = 8; id <= 11; id++) h.Send(DisplayMsg.StreamCreate, Create(id));
        byte[] message = LargeFrame((8 << 20) + 1); // Each payload needs a 16 MiB array.
        for (uint id = 7; id <= 11; id++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(message, id);
            h.Send(DisplayMsg.StreamData, message);
            AssertBufferBudgets(h);
        }
        Assert.Equal(FrameByteBudget, AssertBufferBudgets(h).Queued);
        for (uint id = 7; id <= 10; id++) Assert.Equal(1, h.State<int>("QueueCount", id));
        Assert.Equal(0, h.State<int>("QueueCount", 11));
        Assert.Equal(1u, h.State<uint>("Drops", 11));
        h.Send(DisplayMsg.StreamDestroyAll, []);
        Assert.Equal((0L, 0L), AssertBufferBudgets(h));
        Assert.Null(h.Clock.ArmedAt);
    }

    [Fact]
    public async Task ReturningLargerBuffersEvictsSparesToKeepThePoolWithinItsByteCap()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        byte[] message = LargeFrame((8 << 20) + 1);
        for (int i = 0; i < 4; i++)
        {
            h.Send(DisplayMsg.StreamData, message);
            AssertBufferBudgets(h);
        }
        h.Session.VideoPacing = VideoPacing.Off; // Return four 16 MiB arrays, keeping the stream alive.
        Assert.Equal((0L, FrameByteBudget), AssertBufferBudgets(h));
        h.Session.VideoPacing = VideoPacing.Clock;
        h.Send(DisplayMsg.StreamData, LargeFrame((16 << 20) + 1)); // Requires a new 32 MiB array.
        Assert.Equal((32L << 20, FrameByteBudget), AssertBufferBudgets(h));
        h.Session.VideoPacing = VideoPacing.Off;
        Assert.Equal((0L, FrameByteBudget), AssertBufferBudgets(h)); // Original code retained 96 MiB here.
        h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u)));
        Assert.Equal((0L, 0L), AssertBufferBudgets(h));
    }

    [Fact]
    public async Task OversizedSpareDoesNotCrowdOutTwoSmallEarlyFramesOnDifferentStreams()
    {
        await using var h = new Harness(mode: VideoPacing.Clock);
        h.Send(DisplayMsg.StreamCreate, Create(8));
        h.Send(DisplayMsg.StreamData, LargeFrame((32 << 20) + 1));
        Assert.Equal(FrameByteBudget, AssertBufferBudgets(h).Queued);
        h.Clock.Advance(400); // Invalid JPEG is rejected, but its 64 MiB array is now a spare.
        Assert.Equal((0L, FrameByteBudget), AssertBufferBudgets(h));
        uint previousDrops = h.State<uint>("Drops");

        h.Frame(Epoch + 800);
        h.Send(DisplayMsg.StreamData, Data(Epoch + 800, 8));
        Assert.Equal(1, h.State<int>("QueueCount"));
        Assert.Equal(1, h.State<int>("QueueCount", 8));
        Assert.Equal(previousDrops, h.State<uint>("Drops"));
        Assert.Equal(0u, h.State<uint>("Drops", 8));
        Assert.InRange(AssertBufferBudgets(h).Queued, 1, 16 << 10);
        h.Clock.Advance(400);
        Assert.Equal(2, h.Draws);
        Assert.Equal(0, AssertBufferBudgets(h).Queued);
    }
}
