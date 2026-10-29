using Xunit.Sdk;

namespace Spice.Tests;

/// <summary>Test-only guard: discard disturbed windows, never subtract bytes or accept a dirty sample.</summary>
internal static class AllocationWindow
{
    internal readonly record struct RuntimeStamp(int Gen0, int Gen1, int Gen2, long BackgroundIndex)
    {
        internal static RuntimeStamp Capture() => new(GC.CollectionCount(0), GC.CollectionCount(1),
            GC.CollectionCount(2), GC.GetGCMemoryInfo(GCKind.Background).Index);
    }

    internal readonly record struct Measurement(long Bytes, int Attempts)
    {
        public int Retries => Attempts - 1;
    }

    internal static Measurement Measure(string scenario, Action work, Action? prepare = null,
        long minimumBytes = 0, long maximumBytes = 0, Action<string>? report = null)
        => MeasureCore(scenario, work, prepare, minimumBytes, maximumBytes, 5, RuntimeStamp.Capture,
            GC.WaitForPendingFinalizers, report);

    // Injectable stamps test retry/exhaustion without forcing GCs or creating background load.
    internal static Measurement MeasureCore(string scenario, Action work, Action? prepare,
        long minimumBytes, long maximumBytes, int maxAttempts, Func<RuntimeStamp> readRuntime,
        Action drainFinalizers, Action<string>? report = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumBytes);
        if (maximumBytes < minimumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        long bytes = 0;
        RuntimeStamp start = default, end = default;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Gen2GcCallback invokes ArrayPool.Trim on the finalizer thread, potentially after CollectionCount
            // changed. Drain already queued callbacks, then rewarm outside the byte-measured region.
            // Include preparation in the disturbance interval, so a GC during it invalidates this attempt too.
            start = readRuntime();
            drainFinalizers();
            prepare?.Invoke();
            long before = GC.GetAllocatedBytesForCurrentThread();
            work();
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            end = readRuntime();
            if (start != end)
            {
                report?.Invoke($"{scenario}: discarded attempt {attempt}/{maxAttempts}, {bytes} B; " +
                    $"GC0 {start.Gen0}->{end.Gen0}, GC1 {start.Gen1}->{end.Gen1}, GC2 {start.Gen2}->{end.Gen2}, " +
                    $"background completion {start.BackgroundIndex}->{end.BackgroundIndex}.");
                continue;
            }
            if (bytes < minimumBytes || bytes > maximumBytes)
                throw new XunitException($"{scenario}: {bytes} B in an undisturbed window; expected " +
                    $"[{minimumBytes}, {maximumBytes}] B; attempt {attempt}/{maxAttempts}, retries used {attempt - 1}. " +
                    $"GC0={end.Gen0}, GC1={end.Gen1}, GC2={end.Gen2}, background completion={end.BackgroundIndex}.");
            return new Measurement(bytes, attempt);
        }
        throw new XunitException($"{scenario}: no undisturbed allocation window: all {maxAttempts} attempts " +
            $"were disturbed (retries used {maxAttempts - 1}); last sample {bytes} B. " +
            $"GC2 {start.Gen2}->{end.Gen2}; background completion {start.BackgroundIndex}->{end.BackgroundIndex}. " +
            "This is runtime interference, not a clean-window allocation regression.");
    }
}
