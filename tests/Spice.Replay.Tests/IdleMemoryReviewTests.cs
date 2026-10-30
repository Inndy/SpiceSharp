using System.Collections.Concurrent;
using System.Reflection;
using Spice.Canvas;
using Spice.Session;
using VvFile;

namespace Spice.Replay.Tests;

// Private fields are inspected only by this untrimmed test assembly to coordinate real production locks.
#pragma warning disable IL2075
[Collection("Allocation profiling")]
public sealed class IdleMemoryReviewTests
{
    [Fact]
    public async Task ForcedTrimDoesNotDuplicateAnAlreadyAdmittedTrim()
    {
        await using var session = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        // Hold the existing decode gate to model a decode still copying native output. The first trim has
        // passed admission but cannot yet release the pool or collect. No production hook or wall-clock sleep.
        object decoder = Field(session.Canvas, "_decoder");
        object window = Field(decoder, "_glzWindow");
        object gate = Field(window, "Gate");
        long Stamp() => (long)Field(session, "_lastTrimMs");
        long initial = Stamp();
        bool firstResult = false, secondResult = false;
        var errors = new ConcurrentQueue<Exception>();
        using var secondFinished = new ManualResetEventSlim();
        var first = new Thread(() =>
        {
            try { firstResult = session.TrimMemory("review stream end"); }
            catch (Exception e) { errors.Enqueue(e); }
        }) { IsBackground = true };
        var second = new Thread(() =>
        {
            try { secondResult = session.TrimMemory("review minimize", force: true); }
            catch (Exception e) { errors.Enqueue(e); }
            finally { secondFinished.Set(); }
        }) { IsBackground = true };
        bool firstStarted = false, secondStarted = false;
        Monitor.Enter(gate);
        try
        {
            first.Start(); firstStarted = true;
            Assert.True(SpinWait.SpinUntil(() => Stamp() != initial, TimeSpan.FromSeconds(5)), "first trim reached admission");
            long admittedAt = Stamp();
            Assert.True(SpinWait.SpinUntil(() => Environment.TickCount64 > admittedAt, TimeSpan.FromSeconds(5)));
            second.Start(); secondStarted = true;
            Assert.True(SpinWait.SpinUntil(() => secondFinished.IsSet ||
                (second.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)),
                "second trim either declined or reached the occupied decode gate");
        }
        finally
        {
            Monitor.Exit(gate);
            if (firstStarted) Assert.True(first.Join(TimeSpan.FromSeconds(10)));
            if (secondStarted) Assert.True(second.Join(TimeSpan.FromSeconds(10)));
        }
        Assert.Empty(errors);
        Assert.True(firstResult);
        // K1-R1: currently fails -- the timestamp CAS admits both, causing two blocking full collections.
        Assert.False(secondResult);
    }

    static object Field(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    [Fact]
    public void TrimAndRepeatedOwnerReleasePreserveOutstandingBuffers()
    {
        PixelBuffers.Trim();
        var image = new DecodedImage(256, 256, false);
        byte[] held = image.Pixels;
        held[0] = 123;
        var spare = PixelBuffers.Rent(held.Length);
        PixelBuffers.Return(spare);
        Assert.Equal(spare.Length, PixelBuffers.Trim());
        var other = PixelBuffers.Rent(held.Length);
        Assert.NotSame(held, other);
        Assert.Equal(123, held[0]);
        image.Release();
        image.Release(); // public owner's duplicate release must not insert the same buffer twice
        Assert.Equal(held.Length, PixelBuffers.PooledBytes);
        var a = PixelBuffers.Rent(held.Length);
        var b = PixelBuffers.Rent(held.Length);
        Assert.NotSame(a, b);
        PixelBuffers.Return(a); PixelBuffers.Return(b); PixelBuffers.Return(other);
        PixelBuffers.Trim();

        var mask = new CoverageMask(256, 256, 0, 0);
        int maskBytes = mask.Coverage.Length;
        mask.Dispose(); mask.Dispose();
        Assert.Equal(maskBytes, PixelBuffers.PooledBytes);
        a = PixelBuffers.Rent(maskBytes); b = PixelBuffers.Rent(maskBytes);
        Assert.NotSame(a, b);
        PixelBuffers.Return(a); PixelBuffers.Return(b);
        PixelBuffers.Trim();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(32768, 32768)]
    [InlineData(32769, 65536)]
    [InlineData(65536, 65536)]
    [InlineData(65537, 131072)]
    public void SizeClassBoundaries(int request, int expectedLength)
    {
        PixelBuffers.Trim();
        var buffer = PixelBuffers.Rent(request);
        Assert.Equal(expectedLength, buffer.Length);
        PixelBuffers.Return(buffer);
        Assert.Equal(request <= PixelBuffers.SharedLimit ? 0 : expectedLength, PixelBuffers.PooledBytes);
        PixelBuffers.Trim();
    }

    [Fact]
    public void ParallelRentReturnAndTrimNeverShareAnOutstandingArray()
    {
        PixelBuffers.Trim();
        var active = new ConcurrentDictionary<byte[], byte>(ReferenceEqualityComparer.Instance);
        Parallel.For(0, 4, worker =>
        {
            for (int i = 0; i < 500; i++)
            {
                var a = PixelBuffers.Rent((1 << (16 + worker)) - i % 17);
                Assert.True(active.TryAdd(a, 0), "an outstanding buffer was rented twice");
                a[0] = (byte)worker;
                if (i % 7 == 0) PixelBuffers.Trim();
                Assert.Equal((byte)worker, a[0]);
                Assert.True(active.TryRemove(a, out _));
                PixelBuffers.Return(a);
                Assert.InRange(PixelBuffers.PooledBytes, 0, PixelBuffers.MaxPooledBytes);
            }
        });
        Assert.Empty(active);
        PixelBuffers.Trim();
    }
}
