namespace Pve.Api.Tests;

/// <summary>Offline regressions for the sleep/wake contract in decision 0038. No real power events or requests.</summary>
public sealed class LauncherRefreshMinttyReviewTests
{
    [Fact]
    public void WakeDuringAPreSleepRequestRefreshesOnceAfterThatRequestEnds()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var clock = new TimerClock();
            var oldRequest = new TaskCompletionSource();
            int calls = 0;
            using var scheduler = new RefreshScheduler(TimeSpan.FromHours(1), () =>
            {
                calls++;
                return calls == 1 ? oldRequest.Task : Task.CompletedTask;
            }, a => a(), clock);
            scheduler.Armed = true;
            scheduler.Visible = true;
            Assert.Equal(1, calls);

            scheduler.Suspended = true;
            clock.Advance(TimeSpan.FromHours(8));
            scheduler.Suspended = false;
            scheduler.Suspended = false; // the second resume notification must not add a request
            Assert.Equal(1, calls); // no overlap with the pre-sleep request
            Assert.False(scheduler.IsScheduled);

            // This response may contain the list captured before sleep. Wake still owes one fresh request.
            oldRequest.SetResult();
            Assert.False(scheduler.IsRefreshing);
            Assert.Equal(2, calls);
            clock.Advance(TimeSpan.FromMinutes(59));
            Assert.Equal(2, calls);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    [Fact]
    public void BriefSleepDoesNotDiscardAFollowUpBeforeItsDeadline()
    {
        var clock = new TimerClock();
        int calls = 0;
        using var scheduler = new RefreshScheduler(TimeSpan.Zero, () =>
        {
            calls++;
            return Task.CompletedTask;
        }, a => a(), clock);
        scheduler.Armed = true;
        scheduler.Visible = true;
        scheduler.FollowUp(() => false);
        clock.Advance(TimeSpan.FromSeconds(29));
        int beforeSleep = calls;
        Assert.True(scheduler.IsFollowingUp);

        scheduler.Suspended = true;
        Assert.False(scheduler.IsScheduled);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(beforeSleep, calls);
        scheduler.Suspended = false; // still inside the 30-second follow-up window
        Assert.Equal(beforeSleep + 1, calls);
        Assert.False(scheduler.IsScheduled); // the next 2-second tick would be past the deadline
        Assert.False(scheduler.IsFollowingUp);
    }
}
