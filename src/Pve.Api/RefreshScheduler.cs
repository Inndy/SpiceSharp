namespace Pve.Api;

/// <summary>
/// When the launcher's guest list refreshes by itself (docs/decisions/0038, answer 4 and the follow-up D-d77a4f645c2f): a timer exists only
/// while the window is open, the system is awake, a list is shown, and either an interval is set or a follow-up is
/// due. A minimized window keeps refreshing (the human's choice); while the system sleeps nothing is
/// scheduled, and on wake (or when the window is shown again) the list refreshes once at once. At most one refresh
/// runs at a time, and the next one is timed from when the last one ended.
/// <para>
/// A follow-up (<see cref="FollowUp"/>, after a Start) refreshes every <see cref="FollowUpInterval"/> until its
/// condition holds or <see cref="FollowUpLimit"/> has passed, even when the interval is 0 (off).
/// </para>
/// <para>
/// Not thread-safe: every member, and <c>refresh</c>, runs on one thread (the window's). The timer's callback is
/// handed to <c>post</c>, which queues it there.
/// </para>
/// </summary>
public sealed class RefreshScheduler : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    /// <summary>The shortest interval accepted; a shorter positive setting is raised to it.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan FollowUpInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FollowUpLimit = TimeSpan.FromSeconds(30);

    readonly Func<Task> _refresh;
    readonly Action<Action> _post;
    readonly TimeProvider _time;
    ITimer? _timer;
    int _generation;
    TimeSpan _interval;
    bool _visible, _suspended, _armed, _running, _disposed;
    /// <summary>A wake came while a refresh from before the sleep was still running: its answer may be stale, so one
    /// more refresh follows it (review R1).</summary>
    bool _wakeOwed;
    Func<bool>? _followUpDone;
    long _followUpStart;

    /// <param name="interval">0 (or less) is off.</param>
    /// <param name="refresh">Fetches the list. Exceptions are the caller's to handle; one that escapes is passed to
    /// <see cref="Error"/>, and scheduling goes on.</param>
    /// <param name="post">Queues an action on the thread that owns this object.</param>
    public RefreshScheduler(TimeSpan interval, Func<Task> refresh, Action<Action> post, TimeProvider? time = null)
    {
        _interval = Clamp(interval);
        _refresh = refresh;
        _post = post;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Called with an exception <c>refresh</c> let escape.</summary>
    public Action<Exception>? Error { get; set; }

    /// <summary>A setting in seconds as an interval: 0 or less is off, otherwise between
    /// <see cref="MinimumInterval"/> and <see cref="MaximumInterval"/>.</summary>
    public static TimeSpan Clamp(TimeSpan interval) =>
        interval <= TimeSpan.Zero ? TimeSpan.Zero
        : interval < MinimumInterval ? MinimumInterval
        : interval > MaximumInterval ? MaximumInterval
        : interval;

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = Clamp(value);
            Reschedule();
        }
    }

    /// <summary>The window is shown; a minimized window counts as shown (the human's choice). Becoming visible refreshes at once
    /// (when anything would be scheduled at all); becoming hidden stops the timer.</summary>
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            Changed(value);
        }
    }

    /// <summary>The system is going to sleep (a suspend notification, not polled). Suspending stops the timer;
    /// resuming refreshes once at once (when anything would be scheduled at all), then the schedule goes on.</summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            _wakeOwed = !value && _running;
            Changed(!value);
        }
    }

    void Changed(bool cameBack)
    {
        if (cameBack && Wanted) Fire();
        else Reschedule();
    }

    /// <summary>A signed-in server's list is shown, so there is something to refresh. Arming doesn't refresh: the
    /// caller has just fetched the list. Disarming also drops a follow-up.</summary>
    public bool Armed
    {
        get => _armed;
        set
        {
            if (_armed == value) return;
            _armed = value;
            if (!value) _followUpDone = null;
            Reschedule();
        }
    }

    /// <summary>A timer is set (for tests and the log).</summary>
    public bool IsScheduled => _timer is not null;

    /// <summary>A refresh is running.</summary>
    public bool IsRefreshing => _running;

    public bool IsFollowingUp => _followUpDone is not null;

    /// <summary>The list was just fetched another way (Refresh, a sign-in): the next automatic one is a whole interval
    /// away, and a follow-up whose condition now holds ends.</summary>
    public void Fetched()
    {
        CheckFollowUp();
        Reschedule();
    }

    /// <summary>Refreshes now and then every <see cref="FollowUpInterval"/> until <paramref name="done"/> returns
    /// true after a refresh, or <see cref="FollowUpLimit"/> has passed. A later call replaces an earlier one.</summary>
    public void FollowUp(Func<bool> done)
    {
        _followUpDone = done;
        _followUpStart = _time.GetTimestamp();
        if (Wanted) Fire();
        else Reschedule();
    }

    /// <summary>Whether anything would be scheduled now.</summary>
    bool Wanted => !_disposed && _visible && !_suspended && _armed && (_followUpDone is not null || _interval > TimeSpan.Zero);

    TimeSpan Next => _followUpDone is not null ? FollowUpInterval : _interval;

    void CheckFollowUp()
    {
        if (_followUpDone is null) return;
        if (_followUpDone() || _time.GetElapsedTime(_followUpStart) >= FollowUpLimit) _followUpDone = null;
    }

    /// <summary>Ends a follow-up whose next refresh, <paramref name="ahead"/> from now, would fall after
    /// <see cref="FollowUpLimit"/>: no request starts past the promised window (review R6).</summary>
    void ExpireFollowUp(TimeSpan ahead)
    {
        if (_followUpDone is not null && _time.GetElapsedTime(_followUpStart) + ahead > FollowUpLimit) _followUpDone = null;
    }

    void Reschedule()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
        // A running refresh reschedules when it ends. Nothing is scheduled while hidden or asleep, so a follow-up is
        // kept then and judged by the clock when it comes back (review R2).
        if (!Wanted || _running) return;
        ExpireFollowUp(FollowUpInterval);
        if (!Wanted) return;
        int generation = _generation;
        _timer = _time.CreateTimer(_ => _post(() =>
        {
            if (generation == _generation) Fire();
        }), null, Next, Timeout.InfiniteTimeSpan);
    }

    void Fire() => _ = FireAsync();

    /// <summary>Never throws: what <c>refresh</c> lets escape goes to <see cref="Error"/>. Its continuation runs on the
    /// caller's synchronization context (the window's thread).</summary>
    async Task FireAsync()
    {
        if (_running) return;
        // A follow-up that ran out while the window was hidden or the system slept (or a slow refresh ran) doesn't
        // start a request.
        ExpireFollowUp(TimeSpan.Zero);
        if (!Wanted)
        {
            Reschedule();
            return;
        }
        _generation++;
        _timer?.Dispose();
        _timer = null;
        _running = true;
        try { await _refresh(); }
        catch (Exception e) { Error?.Invoke(e); }
        finally { _running = false; }
        CheckFollowUp();
        if (_wakeOwed)
        {
            _wakeOwed = false;
            if (Wanted)
            {
                Fire();
                return;
            }
        }
        Reschedule();
    }

    public void Dispose()
    {
        _disposed = true;
        _followUpDone = null;
        Reschedule();
    }
}
