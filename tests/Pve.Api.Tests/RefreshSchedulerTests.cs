namespace Pve.Api.Tests;

/// <summary>A clock with timers that fire only when the test moves it, on the test's thread.</summary>
sealed class TimerClock : TimeProvider
{
    long _ticks = TimeSpan.TicksPerDay;
    readonly List<FakeTimer> _timers = [];

    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Timers that are set (not disposed, not fired).</summary>
    public int Pending => _timers.Count(t => t.Due is not null);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, period); // one-shot only: nothing repeats on its own
        var t = new FakeTimer(this, callback, state);
        t.Change(dueTime, period);
        _timers.Add(t);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        long end = _ticks + by.Ticks;
        while (true)
        {
            var next = _timers.Where(t => t.Due is long d && d <= end).OrderBy(t => t.Due).FirstOrDefault();
            if (next is null) break;
            _ticks = next.Due!.Value;
            next.Due = null;
            next.Callback(next.State);
        }
        _ticks = end;
        _timers.RemoveAll(t => t.Due is null);
    }

    internal sealed class FakeTimer(TimerClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long? Due;
        public TimerCallback Callback => callback;
        public object? State => state;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
            return true;
        }

        public void Dispose() => Due = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>The automatic refresh's schedule (docs/decisions/0038, answer 4): nothing while hidden or with nothing
/// to refresh, one refresh on restore, one at a time, and a bounded follow-up after a Start.</summary>
public class RefreshSchedulerTests
{
    static readonly TimeSpan Five = TimeSpan.FromSeconds(5);

    sealed class Rig
    {
        public readonly TimerClock Clock = new();
        public readonly RefreshScheduler S;
        public int Refreshes;
        /// <summary>When set, a refresh waits for the test to complete it.</summary>
        public TaskCompletionSource? Pending;
        public bool Hold;

        public Rig(TimeSpan interval)
        {
            S = new RefreshScheduler(interval, () =>
            {
                Refreshes++;
                if (!Hold) return Task.CompletedTask;
                // Continuations run inline on the test thread when the test completes it.
                Pending = new TaskCompletionSource();
                return Pending.Task;
            }, a => a(), Clock);
        }

        public void Shown() { S.Armed = true; S.Visible = true; }
    }

    [Fact]
    public void NothingIsScheduledUntilVisibleAndArmed()
    {
        var r = new Rig(Five);
        Assert.False(r.S.IsScheduled);
        r.S.Armed = true;
        Assert.False(r.S.IsScheduled); // the window hasn't been shown
        r.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, r.Refreshes);
        r.S.Visible = true;
        Assert.Equal(1, r.Refreshes); // shown: once at once
        Assert.True(r.S.IsScheduled);
    }

    [Fact]
    public void ArmingDoesNotRefreshTheListJustFetched()
    {
        var r = new Rig(Five);
        r.S.Visible = true;
        Assert.Equal(0, r.Refreshes); // nothing to refresh yet
        Assert.False(r.S.IsScheduled);
        r.S.Armed = true;
        Assert.Equal(0, r.Refreshes);
        Assert.True(r.S.IsScheduled);
    }

    [Fact]
    public void RefreshesEveryIntervalWhileShown()
    {
        var r = new Rig(Five);
        r.Shown();
        Assert.Equal(1, r.Refreshes);
        r.Clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal(1, r.Refreshes);
        r.Clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, r.Refreshes);
        r.Clock.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(12, r.Refreshes);
        Assert.Equal(1, r.Clock.Pending); // one timer, re-armed after each refresh
    }

    [Fact]
    public void MinimizedOrHiddenStopsEverythingAndRestoreRefreshesOnce()
    {
        var r = new Rig(Five);
        r.Shown();
        r.S.Visible = false;
        Assert.False(r.S.IsScheduled);
        Assert.Equal(0, r.Clock.Pending); // no timer at all while minimized
        r.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, r.Refreshes);
        r.S.Visible = true;
        Assert.Equal(2, r.Refreshes);
        r.S.Visible = true; // activation of a window already shown: no extra refresh
        Assert.Equal(2, r.Refreshes);
    }

    [Fact]
    public void DisarmingStopsTheTimer()
    {
        var r = new Rig(Five);
        r.Shown();
        r.S.Armed = false; // signed out, another server, the session ended
        Assert.Equal(0, r.Clock.Pending);
        r.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, r.Refreshes);
    }

    [Fact]
    public void ZeroIsOff()
    {
        var r = new Rig(TimeSpan.Zero);
        r.Shown();
        Assert.Equal(0, r.Refreshes); // not even on restore
        Assert.False(r.S.IsScheduled);
        r.S.Visible = false;
        r.S.Visible = true;
        r.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, r.Refreshes);
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(5, 5)]
    [InlineData(100000, 3600)]
    public void TheIntervalIsClamped(int seconds, int expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), RefreshScheduler.Clamp(TimeSpan.FromSeconds(seconds)));
        Assert.Equal(TimeSpan.FromSeconds(expected), new RefreshScheduler(TimeSpan.FromSeconds(seconds), () => Task.CompletedTask, a => a()).Interval);
    }

    [Fact]
    public void AManualFetchPushesTheNextRefreshAWholeIntervalAway()
    {
        var r = new Rig(Five);
        r.Shown();
        r.Clock.Advance(TimeSpan.FromSeconds(4));
        r.S.Fetched(); // F5
        r.Clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(1, r.Refreshes);
        r.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, r.Refreshes);
    }

    [Fact]
    public void OneRefreshAtATimeAndTheNextIsTimedFromItsEnd()
    {
        // The refresh's continuation then runs inline when the test completes it, as on the window's own thread.
        SynchronizationContext.SetSynchronizationContext(null);
        var r = new Rig(Five) { Hold = true };
        r.Shown();
        Assert.Equal(1, r.Refreshes);
        Assert.True(r.S.IsRefreshing);
        Assert.False(r.S.IsScheduled); // nothing ticks while a slow server answers
        r.Clock.Advance(TimeSpan.FromSeconds(20));
        r.S.Visible = false;
        r.S.Visible = true; // a restore while one runs doesn't start a second
        Assert.Equal(1, r.Refreshes);
        r.Pending!.SetResult();
        Assert.False(r.S.IsRefreshing);
        Assert.True(r.S.IsScheduled);
        r.Clock.Advance(TimeSpan.FromSeconds(4.9));
        Assert.Equal(1, r.Refreshes);
        r.Clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(2, r.Refreshes);
        r.Pending!.SetResult();
        r.S.Dispose();
    }

    [Fact]
    public void AnEscapedErrorIsReportedAndSchedulingGoesOn()
    {
        var clock = new TimerClock();
        var errors = new List<Exception>();
        int n = 0;
        var s = new RefreshScheduler(Five, () => { n++; throw new InvalidOperationException("bug"); }, a => a(), clock) { Error = errors.Add };
        s.Armed = true;
        s.Visible = true;
        clock.Advance(Five);
        Assert.Equal(2, n);
        Assert.Equal(2, errors.Count);
        Assert.True(s.IsScheduled);
    }

    [Fact]
    public void AFollowUpRefreshesQuicklyUntilItsConditionHolds()
    {
        var r = new Rig(Five);
        r.Shown();
        bool started = false;
        r.S.FollowUp(() => started);
        Assert.Equal(2, r.Refreshes); // at once
        r.Clock.Advance(RefreshScheduler.FollowUpInterval);
        Assert.Equal(3, r.Refreshes);
        started = true;
        r.Clock.Advance(RefreshScheduler.FollowUpInterval);
        Assert.Equal(4, r.Refreshes); // this one saw it
        Assert.False(r.S.IsFollowingUp);
        r.Clock.Advance(RefreshScheduler.FollowUpInterval);
        Assert.Equal(4, r.Refreshes); // back to the interval
        r.Clock.Advance(Five - RefreshScheduler.FollowUpInterval);
        Assert.Equal(5, r.Refreshes);
    }

    [Fact]
    public void AFollowUpIsBoundedAndWorksWithTheIntervalOff()
    {
        var r = new Rig(TimeSpan.Zero);
        r.Shown();
        r.S.FollowUp(() => false);
        r.Clock.Advance(TimeSpan.FromMinutes(5));
        int expected = 1 + (int)(RefreshScheduler.FollowUpLimit / RefreshScheduler.FollowUpInterval);
        Assert.Equal(expected, r.Refreshes);
        Assert.False(r.S.IsFollowingUp);
        Assert.False(r.S.IsScheduled); // off again: nothing pending
    }

    [Fact]
    public void AFollowUpWhileHiddenWaitsForTheRestore()
    {
        var r = new Rig(Five);
        r.Shown();
        r.S.Visible = false;
        r.S.FollowUp(() => true);
        Assert.Equal(1, r.Refreshes);
        Assert.Equal(0, r.Clock.Pending);
        r.S.Visible = true;
        Assert.Equal(2, r.Refreshes);
        Assert.False(r.S.IsFollowingUp);
    }

    [Fact]
    public void ManualFetchEndsAFollowUpWhoseConditionHolds()
    {
        var r = new Rig(Five);
        r.Shown();
        bool done = false;
        r.S.FollowUp(() => done);
        Assert.True(r.S.IsFollowingUp);
        done = true;
        r.S.Fetched();
        Assert.False(r.S.IsFollowingUp);
    }

    [Fact]
    public void ChangingTheIntervalTakesEffectAtOnce()
    {
        var r = new Rig(Five);
        r.Shown();
        r.S.Interval = TimeSpan.Zero;
        Assert.Equal(0, r.Clock.Pending);
        r.S.Interval = TimeSpan.FromSeconds(10);
        r.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, r.Refreshes);
    }

    [Fact]
    public void DisposedNothingRuns()
    {
        var r = new Rig(Five);
        r.Shown();
        r.S.Dispose();
        Assert.Equal(0, r.Clock.Pending);
        r.S.Visible = false;
        r.S.Visible = true;
        r.S.FollowUp(() => false);
        r.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, r.Refreshes);
    }

    [Fact]
    public void AStaleTimerCallbackDoesNothing()
    {
        // A tick already queued on the window's thread when the window was minimized must not refresh.
        var clock = new TimerClock();
        var queued = new List<Action>();
        int n = 0;
        var s = new RefreshScheduler(Five, () => { n++; return Task.CompletedTask; }, queued.Add, clock);
        s.Armed = true;
        s.Visible = true; // n = 1
        clock.Advance(Five); // the tick is queued, not run
        Assert.Single(queued);
        s.Visible = false;
        queued[0]();
        Assert.Equal(1, n);
    }
}
