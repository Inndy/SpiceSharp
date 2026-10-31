using Spice.Canvas;
using Spice.Session;
using Spice.Tests;
using Spice.Wire;
using Xunit.Abstractions;

namespace Spice.Replay.Tests;

// Finding K1 (docs/verification/2026-10-30-v0-dod.md), docs/decisions/0041: the pixel-buffer pool can be emptied, and
// the session trims on events only. PixelBuffers is process-wide, so these run in the non-parallel collection.
[Collection("Allocation profiling")]
public sealed class PixelBufferTests(ITestOutputHelper output)
{
    [Fact]
    public void LargeBuffersAreReusedAndTrimDropsThem()
    {
        PixelBuffers.Trim();
        var a = PixelBuffers.Rent(100_000);
        Assert.Equal(1 << 17, a.Length); // power-of-two classes, as ArrayPool.Shared
        PixelBuffers.Return(a);
        Assert.Equal(1 << 17, PixelBuffers.PooledBytes);
        Assert.Same(a, PixelBuffers.Rent(70_000)); // any request of the same class
        PixelBuffers.Return(a);

        Assert.Equal(1 << 17, PixelBuffers.Trim());
        Assert.Equal(0, PixelBuffers.PooledBytes);
        Assert.NotSame(a, PixelBuffers.Rent(100_000));
        Assert.Equal(0, PixelBuffers.Trim());
    }

    [Fact]
    public void SmallRequestsUseTheSharedPool()
    {
        PixelBuffers.Trim();
        var s = PixelBuffers.Rent(PixelBuffers.SharedLimit);
        Assert.True(s.Length <= PixelBuffers.SharedLimit);
        PixelBuffers.Return(s);
        Assert.Equal(0, PixelBuffers.PooledBytes);
        Assert.True(PixelBuffers.Rent(PixelBuffers.SharedLimit + 1).Length > PixelBuffers.SharedLimit);
    }

    [Fact]
    public void SparesAreBoundedByCountAndBytes()
    {
        PixelBuffers.Trim();
        var small = new byte[PixelBuffers.MaxPerBucket + 1][];
        for (int i = 0; i < small.Length; i++) small[i] = PixelBuffers.Rent(1 << 17);
        foreach (var b in small) PixelBuffers.Return(b);
        Assert.Equal(PixelBuffers.MaxPerBucket * (1L << 17), PixelBuffers.PooledBytes);
        PixelBuffers.Trim();

        int big = (int)(PixelBuffers.MaxPooledBytes / 2);
        var bigs = new[] { PixelBuffers.Rent(big), PixelBuffers.Rent(big), PixelBuffers.Rent(big) };
        foreach (var b in bigs) PixelBuffers.Return(b);
        Assert.Equal(PixelBuffers.MaxPooledBytes, PixelBuffers.PooledBytes);
        Assert.Equal(PixelBuffers.MaxPooledBytes, PixelBuffers.Trim());
    }

    [Fact]
    public void WarmRentAndReturnAllocateNothing()
    {
        // Keep this in the non-parallel Allocation profiling collection: session tests trim this process-wide pool.
        PixelBuffers.Trim();
        try
        {
            PixelBuffers.Return(PixelBuffers.Rent(1 << 20));
            var m = AllocationWindow.Measure(nameof(WarmRentAndReturnAllocateNothing),
                () => { for (int i = 0; i < 10_000; i++) PixelBuffers.Return(PixelBuffers.Rent(1 << 20)); },
                prepare: () => PixelBuffers.Return(PixelBuffers.Rent(1 << 20)), report: output.WriteLine);
            output.WriteLine($"10000 warm 1 MiB rent/return pairs: {m.Bytes} B");
        }
        finally { PixelBuffers.Trim(); }
    }
}

public sealed partial class StreamPacingTests
{
    [Fact]
    public async Task TheLastStreamEndingTrimsMemoryAtMostOncePerInterval()
    {
        await using var h = new Harness();
        h.Frame(Epoch);
        var log = CaptureLog(() => h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u))));
        Assert.Contains(log, l => l.StartsWith("memory: trimmed (video ended)", StringComparison.Ordinal));
        Assert.Equal(0, PixelBuffers.PooledBytes);

        // A second video right after: its end is within the interval, so no second collection.
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Frame(Epoch + 40);
        log = CaptureLog(() => h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u))));
        Assert.DoesNotContain(log, l => l.StartsWith("memory: trimmed", StringComparison.Ordinal));
        Assert.False(h.Session.TrimMemory("test"));

        // Minimize forces it.
        Assert.True(h.Session.TrimMemory("test", force: true));
    }

    [Fact]
    public async Task AStreamEndingWhileAnotherPlaysDoesNotTrim()
    {
        await using var h = new Harness();
        h.Send(DisplayMsg.StreamCreate, Create(8));
        h.Frame(Epoch);
        var log = CaptureLog(() => h.Send(DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u))));
        Assert.DoesNotContain(log, l => l.StartsWith("memory: trimmed", StringComparison.Ordinal));
        log = CaptureLog(() => h.Send(DisplayMsg.StreamDestroyAll, []));
        Assert.Contains(log, l => l.StartsWith("memory: trimmed (video ended)", StringComparison.Ordinal));
    }
}
