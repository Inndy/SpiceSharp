using Spice.Tests;
using Xunit.Sdk;

namespace Spice.Replay.Tests;

[Collection("Allocation profiling")]
public sealed class AllocationWindowTests
{
    [Fact]
    public void DeliberateAllocationInACleanWindowStillFails()
    {
        // KeepAlive prevents removal of the allocation. No artificial GC/load is introduced.
        var error = Assert.Throws<XunitException>(() => AllocationWindow.Measure("deliberate allocation",
            static () => GC.KeepAlive(new byte[256])));
        Assert.Contains("deliberate allocation", error.Message);
        Assert.Contains("B in an undisturbed window", error.Message);
        Assert.Contains("retries used", error.Message);
    }

    [Fact]
    public void DisturbedWindowIsDiscardedAndPreparationIsRepeated()
    {
        int generation = 0, calls = 0, preparations = 0;
        AllocationWindow.RuntimeStamp Stamp() => new(0, 0, generation, 0);
        var result = AllocationWindow.MeasureCore("one disturbance",
            () => { if (++calls == 1) generation++; }, () => preparations++,
            0, 0, 5, Stamp, static () => { });
        Assert.Equal(2, result.Attempts);
        Assert.Equal(1, result.Retries);
        Assert.Equal(0, result.Bytes);
        Assert.Equal(2, preparations);
    }

    [Fact]
    public void EveryDisturbedAttemptFailsWithADistinctDiagnostic()
    {
        long completed = 0;
        AllocationWindow.RuntimeStamp Stamp() => new(0, 0, 0, completed);
        var error = Assert.Throws<XunitException>(() => AllocationWindow.MeasureCore("continuous interference",
            () => completed++, null, 0, 0, 5, Stamp, static () => { }));
        Assert.Equal(5, completed);
        Assert.Contains("all 5 attempts were disturbed", error.Message);
        Assert.Contains("last sample 0 B", error.Message);
        Assert.Contains("not a clean-window allocation regression", error.Message);
    }
}
