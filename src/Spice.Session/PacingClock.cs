namespace Spice.Session;

/// <summary>
/// Time source and single one-shot wake for video-stream pacing (docs/decisions/0025). The session arms it only while
/// stream frames are queued, so an idle session never wakes. Callers serialize Arm, Disarm and Dispose. Tests substitute
/// a manual clock so paced frames replay deterministically.
/// </summary>
internal abstract class PacingClock : IDisposable
{
    /// <summary>Monotonic milliseconds.</summary>
    public abstract long NowMs { get; }
    /// <summary>Runs when an armed wake comes due, on any thread. Set once by the session.</summary>
    public Action? Wake { get; set; }
    /// <summary>Arms the wake for <paramref name="atMs"/> (in <see cref="NowMs"/> time), replacing any armed one.</summary>
    public abstract void Arm(long atMs);
    /// <summary>Cancels an armed wake. A wake already running still finishes.</summary>
    public abstract void Disarm();
    /// <summary>Cancels the wake for good; later Arm calls do nothing.</summary>
    public abstract void Dispose();
}

/// <summary>The real clock: <see cref="Environment.TickCount64"/> and one thread-pool timer, created on first use.</summary>
internal sealed class SystemPacingClock : PacingClock
{
    Timer? _timer;
    bool _disposed;

    public override long NowMs => Environment.TickCount64;

    public override void Arm(long atMs)
    {
        if (_disposed) return;
        long due = Math.Max(0, atMs - Environment.TickCount64);
        _timer ??= new Timer(static s => ((SystemPacingClock)s!).Wake?.Invoke(), this, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(due, Timeout.Infinite);
    }

    public override void Disarm() => _timer?.Change(Timeout.Infinite, Timeout.Infinite);

    public override void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
