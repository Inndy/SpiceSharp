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
    /// window's reuse pool and the spare pixel buffers of <see cref="PixelBuffers"/>. No collection is forced: the
    /// dropped buffers become garbage and the GC frees them when it decides to. What the protocol needs stays: the GLZ
    /// window's images, the image cache, the surfaces. Called only on events (the window is minimized or hidden, the
    /// last video stream ended), never on a timer. Unless <paramref name="force"/>, a trim within
    /// <see cref="MinTrimIntervalMs"/> of the previous one is skipped, so streams that come and go don't each pay for a
    /// re-warm. A request that arrives while another trim runs, forced or not, is skipped: the running trim does the
    /// same work (review K1-R1). Any thread; returns false when skipped.
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
            Volatile.Write(ref _lastTrimReason, reason);
            long start = Stopwatch.GetTimestamp();
            long glz = Canvas.TrimGlzPool();
            long spare = PixelBuffers.Trim();
            // No forced collection (the human, 2026-10-31): the dropped arrays are garbage now; the GC frees them when
            // it decides to.
            Log.Info($"memory: trimmed ({reason}): GLZ reuse pool {Mb(glz)} MB, spare pixel buffers {Mb(spare)} MB " +
                $"released to the GC; {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms");
            RaiseStatusChanged();
            return true;
        }
        finally { Volatile.Write(ref _trimming, 0); }
    }

    string? _lastTrimReason;

    /// <summary>When the last trim ran (<see cref="Environment.TickCount64"/>) and why; null before any. Any thread.</summary>
    public (long TickMs, string Reason)? LastTrim =>
        Volatile.Read(ref _lastTrimReason) is { } reason ? (Interlocked.Read(ref _lastTrimMs), reason) : null;

    static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1");
}
