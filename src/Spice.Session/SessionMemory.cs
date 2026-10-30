using System.Diagnostics;
using Spice.Canvas;
using Spice.Wire;

namespace Spice.Session;

public sealed partial class SpiceSession
{
    /// <summary>Least time (ms) between two trims that aren't forced (video ended, window hidden).</summary>
    public const long MinTrimIntervalMs = 30_000;

    long _lastTrimMs = long.MinValue / 2;
    /// <summary>1 while a trim runs: admission, separate from the interval, so a forced trim can't run alongside one.</summary>
    int _trimming;

    /// <summary>
    /// Gives back memory a busy period left behind and an idle session doesn't use (docs/decisions/0041): the GLZ
    /// window's reuse pool, the spare pixel buffers of <see cref="PixelBuffers"/>, then one blocking compacting GC in
    /// aggressive mode, which frees those buffers and returns the GC's free space to the system. What the protocol
    /// needs stays: the GLZ window's images, the image cache, the surfaces. Called only on events (the window is
    /// minimized or hidden, the last video stream ended), never on a timer. Unless <paramref name="force"/>, a trim
    /// within <see cref="MinTrimIntervalMs"/> of the previous one is skipped, so streams that come and go don't each
    /// pay for a collection and a re-warm. A request that arrives while another trim runs, forced or not, is skipped:
    /// the running trim does the same work, and two blocking collections back to back would reclaim little (review
    /// K1-R1). Any thread; returns false when skipped.
    /// </summary>
    public bool TrimMemory(string reason, bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastTrimMs) < MinTrimIntervalMs) return false;
        if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0) return false; // another thread is trimming
        try
        {
            // Re-checked under admission: a trim that finished between the first check and here counts too.
            if (!force && now - Interlocked.Read(ref _lastTrimMs) < MinTrimIntervalMs) return false;
            Interlocked.Exchange(ref _lastTrimMs, now);
            long start = Stopwatch.GetTimestamp();
            long glz = Canvas.TrimGlzPool();
            long spare = PixelBuffers.Trim();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var gc = GC.GetGCMemoryInfo();
            Log.Info($"memory: trimmed ({reason}): GLZ reuse pool {Mb(glz)} MB, spare pixel buffers {Mb(spare)} MB; " +
                $"GC heap {Mb(gc.HeapSizeBytes)} MB, {Mb(gc.TotalCommittedBytes)} MB committed; " +
                $"{Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms");
            return true;
        }
        finally { Volatile.Write(ref _trimming, 0); }
    }

    static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1");
}
