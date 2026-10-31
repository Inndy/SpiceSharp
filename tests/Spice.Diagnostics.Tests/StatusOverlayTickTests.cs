using Spice.Session;
using SpiceViewer;

namespace Spice.Diagnostics.Tests;

// The overlay's 1 s refresh tick (docs/decisions/0044, the human's decision of 2026-10-31): armed only while the
// figures move, stopped by the first tick that finds nothing changing. A fake timer and a simulated clock: no waiting.
public sealed class StatusOverlayTickTests
{
    sealed class FakeTimer : IOverlayTimer
    {
        public long? DueAt;
        public long Now;
        public int Arms, Disarms;
        public void Arm(int ms) { DueAt = Now + ms; Arms++; }
        public void Disarm() { DueAt = null; Disarms++; }
    }

    sealed class Guest : IOverlaySource
    {
        public long Bytes;
        public int Streams;
        public bool Audio;
        public void Fill(OverlaySnapshot s)
        {
            s.Title = "t"; s.State = "connected";
            s.NetBytes = Bytes;
            s.StreamCount = Streams;
            s.Audio = new AudioStatus(Audio, 3, 2, 48000, 0);
        }
    }

    /// <summary>The presenter's part: a tick or another cause refreshes the overlay, then the ticker decides.</summary>
    sealed class Rig
    {
        public readonly FakeTimer Timer = new();
        public readonly Guest Guest = new();
        public readonly StatusOverlay Overlay;
        public readonly OverlayTicker Ticker;
        public readonly List<long> Wakeups = [];
        public int Presents;

        public Rig() { Overlay = new StatusOverlay(Guest); Ticker = new OverlayTicker(Timer); }

        public void Refresh()
        {
            if (Overlay.Refresh(Timer.Now, TimeSpan.FromMilliseconds(Timer.Now))) Presents++;
            Ticker.AfterRefresh(Overlay.Live);
        }

        /// <summary>Runs the simulated clock to <paramref name="until"/>, firing the tick whenever it is due.</summary>
        public void RunUntil(long until, Action<long>? eachMs = null)
        {
            for (long t = Timer.Now + 1; t <= until; t++)
            {
                Timer.Now = t;
                eachMs?.Invoke(t);
                if (Timer.DueAt == t)
                {
                    Timer.DueAt = null;
                    Ticker.Fired();
                    Wakeups.Add(t);
                    Refresh();
                }
            }
        }
    }

    [Fact]
    public void AnIdleGuestReachesNoWakeupsWithinTwoSecondsOfShowingTheOverlay()
    {
        var rig = new Rig();
        rig.Refresh(); // shown: the first refresh counts as moving
        Assert.True(rig.Ticker.Armed);
        rig.RunUntil(60_000);
        Assert.Single(rig.Wakeups);                 // one tick shows the final, zero figures...
        Assert.True(rig.Wakeups[0] <= 2_000);
        Assert.False(rig.Ticker.Armed);             // ...and stops
        Assert.Null(rig.Timer.DueAt);
    }

    [Fact]
    public void TicksWhileDataArrivesAndStopsOneTickAfterItEnds()
    {
        var rig = new Rig();
        rig.Refresh();
        // Data for 5 s (a byte every 10 ms), then nothing.
        rig.RunUntil(60_000, t => { if (t <= 5_000 && t % 10 == 0) rig.Guest.Bytes += 100; });
        Assert.All(rig.Wakeups.Take(5), (w, i) => Assert.Equal((i + 1) * OverlayTicker.TickMs, w));
        long last = rig.Wakeups[^1];
        Assert.InRange(last, 5_000, 5_000 + 2 * OverlayTicker.TickMs); // zero wakeups within about 2 s of the end
        Assert.False(rig.Ticker.Armed);
        // The last tick showed the zero rates.
        Assert.Contains("net   0 B/s, 0 msg/s", string.Join("\n", Enumerable.Range(0, rig.Overlay.Text.LineCount).Select(i => rig.Overlay.Text.Line(i).ToString())));
    }

    [Theory]
    [InlineData(1, false)] // a video stream
    [InlineData(0, true)]  // playback
    public void AStreamOrPlaybackKeepsTicking(int streams, bool audio)
    {
        var rig = new Rig();
        rig.Guest.Streams = streams; rig.Guest.Audio = audio;
        rig.Refresh();
        rig.RunUntil(10_000);
        Assert.Equal(10_000 / OverlayTicker.TickMs, rig.Wakeups.Count);
        Assert.True(rig.Ticker.Armed);
        // It ends: one more tick, then none.
        rig.Guest.Streams = 0; rig.Guest.Audio = false;
        int before = rig.Wakeups.Count;
        rig.RunUntil(30_000);
        Assert.Equal(before + 1, rig.Wakeups.Count);
        Assert.False(rig.Ticker.Armed);
    }

    [Fact]
    public void AStoppedTickIsReArmedOnlyByARefreshFromAnExistingPath()
    {
        var rig = new Rig();
        rig.Refresh();
        rig.RunUntil(5_000);
        Assert.False(rig.Ticker.Armed);
        int wakeups = rig.Wakeups.Count;
        rig.Guest.Bytes += 1; // data that neither presents nor raises an event: nothing notices, nothing polls
        rig.RunUntil(20_000);
        Assert.Equal(wakeups, rig.Wakeups.Count);
        rig.Refresh();         // a guest present or a session event refreshes the overlay: the change shows, and it ticks
        Assert.True(rig.Ticker.Armed);
        rig.RunUntil(40_000);
        Assert.Equal(wakeups + 1, rig.Wakeups.Count); // nothing new since: one tick, then stop
    }

    [Fact]
    public void AnUnchangedTickPresentsNothingAndHidingStopsTheTimer()
    {
        var rig = new Rig();
        rig.Refresh();
        int presents = rig.Presents;
        // Same figures and the same clock second: the text is the same, so no present.
        rig.Refresh();
        Assert.Equal(presents, rig.Presents);
        rig.Guest.Streams = 1;
        rig.Refresh();
        Assert.True(rig.Ticker.Armed);
        int disarms = rig.Timer.Disarms;
        rig.Ticker.Stop(); // the overlay is hidden
        Assert.False(rig.Ticker.Armed);
        Assert.Null(rig.Timer.DueAt);
        rig.Ticker.Stop();
        Assert.Equal(disarms + 1, rig.Timer.Disarms); // nothing to disarm twice
    }
}
