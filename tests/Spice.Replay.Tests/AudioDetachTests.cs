using System.Collections.Concurrent;
using System.Reflection;
using Spice.Media;
using Spice.Tests;
using Spice.Wire;
using SpiceViewer;
using Xunit.Abstractions;

namespace Spice.Replay.Tests;

// The stream outlives its device (docs/decisions/0031). The queue tests run at 1000 Hz stereo on a manual clock, so one
// frame is one millisecond; the renderer tests drive the linked AudioOutput against AudioOutputFakeBackend (no device,
// no window).
[Collection("Allocation profiling")]
public sealed class AudioDetachTests(ITestOutputHelper output)
{
    const int Rate = 1000, Ch = 2;
    const int E_FAIL = unchecked((int)0x80004005);

    static short[] Packet(int frames, short first = 1)
    {
        var p = new short[frames * Ch];
        for (int i = 0; i < p.Length; i++) p[i] = (short)(first + i);
        return p;
    }

    sealed class ManualClock
    {
        public long Now;
        public Func<long> Read => () => Now;
    }

    static PcmQueue Queue(ManualClock clock)
    {
        var q = new PcmQueue(clock.Read);
        q.Reset(Ch, Rate);
        q.Attach();
        return q;
    }

    // ---------------- the queue ----------------

    [Fact]
    public void ClockModeReattachDropsWhatPlayedMeanwhileAndStaysOnTheStamps()
    {
        var clock = new ManualClock();
        var q = Queue(clock);
        // Packet 1 (stamp s) due in 300 ms: 300 ms of silence ahead of it.
        q.Write(Packet(10, 1), 300);
        Assert.Equal(1, q.Statistics.SyncCorrections);
        Assert.True(q.ReadyFrames() >= 40);
        q.Transfer(new short[40 * Ch], false, 40); // the device holds 40 ms
        q.Detach(); // at t = 0: the device is gone with its 40 ms

        // While detached, a write sees the queue as a device playing in real time would leave it: packet 2 (stamp s + 10,
        // due at 310 - 20 = 290) lines up with no correction.
        clock.Now = 20;
        Assert.Equal(0, q.Write(Packet(10, 21), 290));
        Assert.Equal(0, q.Statistics.ExpiredFrames); // the virtual device still plays the real one's 40 ms

        clock.Now = 100;
        q.Attach(); // 100 ms played: the device's 40 ms, then 60 ms of the queue
        var s = q.Statistics;
        Assert.Equal((60L, 1L, 100L), (s.ExpiredFrames, s.Detaches, s.DetachedMs));
        Assert.Equal(220 * Ch, q.Count);
        Assert.Equal(0, q.DeviceQueuedFrames);

        // Packet 3 (stamp s + 20, due at 320 - 100 = 220) still needs no correction: the held audio is on its stamps.
        Assert.Equal(0, q.Write(Packet(10, 41), 220));
        Assert.Equal(1, q.Statistics.SyncCorrections);
        var played = new short[230 * Ch];
        q.Dequeue(played, false);
        Assert.All(played[..(200 * Ch)], v => Assert.Equal(0, v)); // packet 1 is heard 200 ms after the reattach
        Assert.Equal(Packet(10, 1).Concat(Packet(10, 21)).Concat(Packet(10, 41)), played[(200 * Ch)..]);
    }

    [Fact]
    public void ClockModeDetachedLongerThanTheHeldAudioCorrectsOnTheNextPacket()
    {
        var clock = new ManualClock();
        var q = Queue(clock);
        q.Write(Packet(10), 300);
        q.Detach();
        clock.Now = 1000;
        q.Attach();
        Assert.Equal(0, q.Count); // all of it was due before now
        Assert.Equal(310, q.Statistics.ExpiredFrames);
        // The stream went on, stamped in real time: the next packet is placed at its own deadline again (the virtual
        // device ran dry, so the timeline slipped as after an underrun, and one correction puts it back).
        Assert.Equal(300, q.Write(Packet(10, 11), 300));
        Assert.Equal(310 * Ch, q.Count);
    }

    [Fact]
    public void UnsyncedDetachedQueueKeepsOnlyASmallRecentWindow()
    {
        var clock = new ManualClock();
        var q = Queue(clock);
        q.Detach();
        // A burst with no time passing: never more than TrimAboveMs, the newest audio kept.
        for (int i = 0; i < 1000; i++)
        {
            q.Write(Packet(10, (short)i));
            Assert.True(q.Count <= PcmQueue.TrimAboveMs * Ch, $"{q.Count} samples after {i + 1} writes");
        }
        // Real time: the virtual device takes 10 ms per 10 ms packet.
        for (int i = 0; i < 1000; i++)
        {
            clock.Now += 10;
            q.Write(Packet(10));
            Assert.True(q.Count <= PcmQueue.TrimAboveMs * Ch);
        }
        Assert.True(q.Statistics.ExpiredFrames >= 1000 * 10 + 1000 * 10 - PcmQueue.TrimAboveMs);
    }

    [Fact]
    public void SynchronizedDetachedQueueTracksItsScheduleWithoutCorrections()
    {
        var clock = new ManualClock();
        var q = Queue(clock);
        q.Detach();
        for (int i = 0; i < 1000; i++)
        {
            q.Write(Packet(10), 300);
            clock.Now += 10;
        }
        var s = q.Statistics;
        Assert.Equal(1, s.SyncCorrections); // the first packet's hold only
        Assert.InRange(q.Count / Ch, 290, 310);
    }

    [Fact]
    public void DetachedStreamEndsInsteadOfDrainingAndContinuesOnStart()
    {
        var clock = new ManualClock();
        var q = Queue(clock);
        q.Write(Packet(10), 300);

        // STOP with no device: nothing to play out (the caller closes at once).
        q.Detach();
        Assert.True(q.IsDetached);
        Assert.False(q.BeginDrain());

        // START with no device: the same format continues (a new timeline after the held audio), another restarts.
        long held = q.Count;
        Assert.False(q.TryContinue(Ch, 44100));
        Assert.True(q.TryContinue(Ch, Rate));
        Assert.Equal(held, q.Count);
        Assert.Equal(0, q.Write(Packet(10, 11), 500)); // placed at its own deadline, nothing held is moved
        Assert.Equal((300 + 10 + 190 + 10) * Ch, q.Count);

        // A drain that loses its device ends when the render thread would wait for one; atomic with TryContinue.
        q.Attach();
        Assert.True(q.BeginDrain());
        Assert.False(q.TryEndDetachedDrain()); // attached: the drain goes on
        q.Detach();
        Assert.True(q.TryEndDetachedDrain());
        Assert.False(q.TryContinue(Ch, Rate));
        Assert.Equal(held + (190 + 10) * Ch, q.Statistics.DiscardedSamples);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void DetachedWritesAllocateNothing(bool synced)
    {
        var clock = new ManualClock();
        var q = Queue(clock);
        short[] packet = Packet(10), dst = new short[10 * Ch];
        int i = 0;
        void Step()
        {
            // Every 100 packets the device goes away for 50 and comes back.
            if (i % 100 == 0) q.Detach();
            else if (i % 100 == 50) q.Attach();
            q.Write(packet, synced ? 300 : PcmQueue.Unsynced);
            if (i % 100 >= 50 && q.ReadyFrames() >= 10) q.Transfer(dst, false, 10);
            clock.Now += 10;
            i++;
        }
        for (int k = 0; k < 1000; k++) Step();
        var m = AllocationWindow.Measure($"PcmQueue detached synced={synced}",
            () => { for (int k = 0; k < 10_000; k++) Step(); },
            prepare: () => { for (int k = 0; k < 100; k++) Step(); },
            report: output.WriteLine);
        output.WriteLine($"synced={synced}: {m.Bytes} B, attempts {m.Attempts}; {q.Statistics}");
        Assert.True(q.Statistics.ExpiredFrames > 0);
    }

    // ---------------- the renderer ----------------

    static T Field<T>(AudioOutput audio, string name) =>
        (T)typeof(AudioOutput).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audio)!;
    static void SetField(AudioOutput audio, string name, object value) =>
        typeof(AudioOutput).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(audio, value);
    static void Signal(AudioOutput audio, string name) => Field<EventWaitHandle>(audio, name).Set();
    static short[] Stereo(short marker) => Enumerable.Repeat(marker, 480 * 2).ToArray();

    static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 1000 && !condition(); i++) await Task.Delay(5);
        Assert.True(condition(), what);
    }

    /// <summary>Captures Info and above (Debug too if asked); restores the log on dispose.</summary>
    sealed class LogCapture : IDisposable
    {
        readonly Action<LogLevel, string>? sink = Log.Sink;
        readonly LogLevel level = Log.MinLevel;
        public readonly ConcurrentQueue<(LogLevel Level, string Message)> Lines = new();
        public LogCapture(LogLevel min = LogLevel.Info)
        {
            Log.MinLevel = min;
            Log.Sink = (l, m) => { if (m.StartsWith("Audio:", StringComparison.Ordinal)) Lines.Enqueue((l, m)); };
        }
        public int Count(string part) => Lines.Count(x => x.Message.Contains(part, StringComparison.Ordinal));
        public int Warnings => Lines.Count(x => x.Level >= LogLevel.Warn);
        public void Dispose() { Log.Sink = sink; Log.MinLevel = level; }
    }

    [Fact]
    public async Task RepeatedDefaultChangesNeverEndTheStream()
    {
        // Live G5: two default-device changes within ~5 s ended the stream and discarded its held audio.
        AudioOutputFakeBackend.Reset();
        using var log = new LogCapture();
        using var audio = new AudioOutput();
        try
        {
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var worker = Field<Thread>(audio, "thread");
            var queue = Field<PcmQueue>(audio, "queue");
            for (int change = 1; change <= 8; change++)
            {
                audio.Write(Stereo((short)change), 300);
                Volatile.Write(ref AudioOutputFakeBackend.DefaultDevice, change % 2);
                Signal(audio, "deviceChanged");
                int expected = change + 1;
                await Until(() => Volatile.Read(ref AudioOutputFakeBackend.Starts) == expected, $"reopen {change}");

                // The same change announced again (live: one change, two reopens): the device in use is the default.
                Signal(audio, "deviceChanged");
                Volatile.Write(ref AudioOutputFakeBackend.Padding, 0);
                Task wrote = AudioOutputFakeBackend.ExpectWrite();
                Signal(audio, "bufferEvent"); // handled after the notification (WaitAny takes the lowest index first)
                await wrote.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(expected, Volatile.Read(ref AudioOutputFakeBackend.Opens));
            }
            Assert.Same(worker, Field<Thread>(audio, "thread"));
            Assert.True(worker.IsAlive);
            Assert.True(queue.Count > 0, "the stream lost its held audio");
            Assert.Equal(8, Field<int>(audio, "defaultIgnored"));
            Assert.Equal(8, log.Count("default output device changed; reopening"));
            Assert.Equal(0, log.Warnings);
            Assert.Equal(8, queue.Statistics.Detaches);
        }
        finally { audio.Stop(); }
    }

    [Fact]
    public async Task NoDeviceWaitsForEventsThenANotificationReattachesMidStream()
    {
        AudioOutputFakeBackend.Reset();
        Volatile.Write(ref AudioOutputFakeBackend.NoDevice, 1);
        using var log = new LogCapture();
        using var audio = new AudioOutput();
        try
        {
            audio.Start(2, 48000);
            // No device is not worth retrying on a timer: straight to waiting for events.
            await Until(() => Field<int>(audio, "detachedWaitMs") == Timeout.Infinite, "event-only wait");
            Assert.Equal(1, Volatile.Read(ref AudioOutputFakeBackend.Attempts));
            var worker = Field<Thread>(audio, "thread");
            var queue = Field<PcmQueue>(audio, "queue");
            audio.Write(Stereo(5));
            Assert.Equal(960, queue.Count);

            Volatile.Write(ref AudioOutputFakeBackend.NoDevice, 0);
            Signal(audio, "devicesChanged"); // what DeviceNotifications does when a device is added or becomes active
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SetField(audio, "healthyMs", 0); // recovered at the second buffer event (1 s in production)
            for (int i = 0; i < 2; i++)
            {
                Volatile.Write(ref AudioOutputFakeBackend.Padding, 0);
                Task wrote = AudioOutputFakeBackend.ExpectWrite();
                Signal(audio, "bufferEvent");
                await wrote.WaitAsync(TimeSpan.FromSeconds(5));
            }
            await Until(() => log.Count("Audio: output back after") == 1, "recovery logged");

            Assert.Same(worker, Field<Thread>(audio, "thread"));
            Assert.True(worker.IsAlive);
            Assert.Equal(0, Field<int>(audio, "failures"));
            Assert.Equal(1, log.Count("no output device; playback goes on without output"));
            Assert.Equal(1, log.Count("waiting for a device change"));
        }
        finally { audio.Stop(); }
    }

    [Fact]
    public async Task FailedOpensBackOffThenWaitWithoutATimeout()
    {
        AudioOutputFakeBackend.Reset();
        Volatile.Write(ref AudioOutputFakeBackend.OpenError, E_FAIL);
        Volatile.Write(ref AudioOutputFakeBackend.FailOpens, int.MaxValue);
        using var log = new LogCapture();
        using var audio = new AudioOutput();
        SetField(audio, "retryFirstMs", 10);
        SetField(audio, "retryMaxMs", 40);
        SetField(audio, "retryBudgetMs", 300);
        try
        {
            audio.Start(2, 48000);
            await Until(() => Field<int>(audio, "detachedWaitMs") == Timeout.Infinite, "event-only wait after the budget");
            int attempts = Volatile.Read(ref AudioOutputFakeBackend.Attempts);
            Assert.InRange(attempts, 4, 20); // 10, 20, 40, 40, ... ms apart for 300 ms
            // Past the budget nothing retries on its own: no timed wait, so no more attempts however long it waits.
            await Task.Delay(400);
            Assert.Equal(attempts, Volatile.Read(ref AudioOutputFakeBackend.Attempts));
            Assert.Equal(Timeout.Infinite, Field<int>(audio, "detachedWaitMs"));
            Assert.True(Field<Thread>(audio, "thread").IsAlive);
            // Logged on transitions only, not per retry.
            Assert.Equal(1, log.Count("playback goes on without output"));
            Assert.Equal(1, log.Count("waiting for a device change"));
            Assert.Equal(2, log.Lines.Count);

            // The default device changes: one more attempt, which succeeds.
            Volatile.Write(ref AudioOutputFakeBackend.FailOpens, 0);
            Signal(audio, "deviceChanged");
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(attempts + 1, Volatile.Read(ref AudioOutputFakeBackend.Attempts));
        }
        finally { audio.Stop(); }
    }

    [Fact]
    public async Task ADeviceThatFailsAfterItsFirstBufferEndsUpWaitingForEvents()
    {
        // A device that opens, takes its first buffer and then fails, every time: a short run is a failure, so the
        // reopens back off and stop at the budget instead of repeating every 50 ms forever.
        AudioOutputFakeBackend.Reset();
        using var log = new LogCapture();
        using var audio = new AudioOutput();
        SetField(audio, "retryFirstMs", 10);
        SetField(audio, "retryMaxMs", 40);
        SetField(audio, "retryBudgetMs", 300);
        try
        {
            audio.Start(2, 48000);
            int runs = 0;
            while (Field<int>(audio, "detachedWaitMs") != Timeout.Infinite)
            {
                Assert.True(runs < 50, "never reached the event-only wait");
                int started = runs + 1;
                await Until(() => Volatile.Read(ref AudioOutputFakeBackend.Starts) == started, $"open {started}");
                runs = started;
                Volatile.Write(ref AudioOutputFakeBackend.Padding, 0);
                Task wrote = AudioOutputFakeBackend.ExpectWrite();
                Signal(audio, "bufferEvent"); // its first buffer
                await wrote.WaitAsync(TimeSpan.FromSeconds(5));
                Interlocked.Exchange(ref AudioOutputFakeBackend.FailNextPadding, 1);
                Signal(audio, "bufferEvent"); // then it fails
                await Until(() => Volatile.Read(ref AudioOutputFakeBackend.FailNextPadding) == 0, $"failure {runs}");
                await Until(() => Field<int>(audio, "detachedWaitMs") != 0
                    || Volatile.Read(ref AudioOutputFakeBackend.Starts) > runs, $"wait after failure {runs}");
            }
            int attempts = Volatile.Read(ref AudioOutputFakeBackend.Attempts);
            Assert.InRange(runs, 2, 30); // 10, 20, 40, 40, ... ms apart (plus the test's polling) for 300 ms
            await Task.Delay(400);
            Assert.Equal(attempts, Volatile.Read(ref AudioOutputFakeBackend.Attempts));
            Assert.Equal(runs, Volatile.Read(ref AudioOutputFakeBackend.Starts));
            Assert.Equal(1, log.Count("playback goes on without output"));
            Assert.Equal(1, log.Count("waiting for a device change"));
            Assert.Equal(2, log.Warnings);
            Assert.Equal(0, log.Count("output back"));
            Assert.Equal(runs, log.Count("Audio: playing")); // one line per open, and the opens are bounded
        }
        finally { audio.Stop(); }
    }

    [Fact]
    public async Task ADeviceThatPlaysForAWhileEndsTheRunOfFailures()
    {
        AudioOutputFakeBackend.Reset();
        Volatile.Write(ref AudioOutputFakeBackend.OpenError, E_FAIL);
        Volatile.Write(ref AudioOutputFakeBackend.FailOpens, 1);
        using var log = new LogCapture();
        using var audio = new AudioOutput();
        SetField(audio, "retryFirstMs", 10);
        SetField(audio, "healthyMs", 50);
        try
        {
            audio.Start(2, 48000); // the first open fails, the second plays
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Field<int>(audio, "failures"));
            for (int i = 0; i < 1000 && Field<int>(audio, "failures") != 0; i++)
            {
                Volatile.Write(ref AudioOutputFakeBackend.Padding, 0);
                Task wrote = AudioOutputFakeBackend.ExpectWrite();
                Signal(audio, "bufferEvent");
                await wrote.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(5);
            }
            Assert.Equal(0, Field<int>(audio, "failures"));
            await Until(() => log.Count("Audio: output back after") == 1, "recovery logged");
        }
        finally { audio.Stop(); }
    }

    [Fact]
    public async Task StopOrADeviceEventEndsABackoffWaitAtOnce()
    {
        AudioOutputFakeBackend.Reset();
        Volatile.Write(ref AudioOutputFakeBackend.OpenError, E_FAIL);
        Volatile.Write(ref AudioOutputFakeBackend.FailOpens, int.MaxValue);
        using var log = new LogCapture();
        using var audio = new AudioOutput();
        SetField(audio, "retryFirstMs", 60_000); // a wait no test would sit out
        SetField(audio, "retryMaxMs", 60_000);
        SetField(audio, "retryBudgetMs", 600_000); // the last backoff is cut to the budget's end
        try
        {
            audio.Start(2, 48000);
            await Until(() => Field<int>(audio, "detachedWaitMs") == 60_000, "backoff wait");
            Volatile.Write(ref AudioOutputFakeBackend.FailOpens, 0);
            Signal(audio, "devicesChanged");
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Interlocked.Exchange(ref AudioOutputFakeBackend.FailNextPadding, 1);
            Volatile.Write(ref AudioOutputFakeBackend.FailOpens, int.MaxValue);
            Signal(audio, "bufferEvent");
            await Until(() => Field<int>(audio, "detachedWaitMs") == 60_000, "backoff wait after the device failed again");
            var worker = Field<Thread>(audio, "thread");
            var stop = Task.Run(audio.Stop);
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(worker.IsAlive);
        }
        finally { audio.Stop(); }
    }

    [Fact]
    public async Task StartWhileDetachedContinuesTheStreamAndTriesTheDeviceAgain()
    {
        AudioOutputFakeBackend.Reset();
        Volatile.Write(ref AudioOutputFakeBackend.NoDevice, 1);
        using var log = new LogCapture();
        using var audio = new AudioOutput();
        try
        {
            audio.Start(2, 48000);
            await Until(() => Field<int>(audio, "detachedWaitMs") == Timeout.Infinite, "event-only wait");
            var worker = Field<Thread>(audio, "thread");
            var queue = Field<PcmQueue>(audio, "queue");
            audio.Write(Stereo(7), 300);
            long held = queue.Count;
            Assert.True(held > 0);

            audio.Start(2, 48000); // same format: continues
            Assert.Same(worker, Field<Thread>(audio, "thread"));
            Assert.Equal(held, queue.Count);
            await Until(() => Volatile.Read(ref AudioOutputFakeBackend.Attempts) == 2, "one more attempt");
            await Until(() => Field<int>(audio, "detachedWaitMs") == Timeout.Infinite, "waits for events again");
            Assert.Equal(1, log.Count("waiting for a device change")); // not logged again

            audio.Start(2, 44100); // another format: restarts
            Assert.False(worker.IsAlive);
            Assert.NotSame(worker, Field<Thread>(audio, "thread"));
        }
        finally { audio.Stop(); }
    }

    [Fact]
    public async Task StopWhileDetachedEndsTheStreamAtOnce()
    {
        AudioOutputFakeBackend.Reset();
        Volatile.Write(ref AudioOutputFakeBackend.NoDevice, 1);
        using var log = new LogCapture(LogLevel.Debug);
        using var audio = new AudioOutput();
        try
        {
            audio.Start(2, 48000);
            await Until(() => Field<int>(audio, "detachedWaitMs") == Timeout.Infinite, "event-only wait");
            var worker = Field<Thread>(audio, "thread");
            var queue = Field<PcmQueue>(audio, "queue");
            audio.Write(Stereo(9), 300);
            long held = queue.Count;
            audio.EndOfStream(); // clock pacing, but nothing can play it out
            Assert.False(worker.IsAlive);
            Assert.False(queue.Draining);
            Assert.Equal(0, queue.Count);
            Assert.Contains(log.Lines, x => x.Message.StartsWith("Audio: stream closed;", StringComparison.Ordinal)
                && x.Message.Contains($"discarded on close {held}", StringComparison.Ordinal)
                && x.Message.Contains("without a device 1 times", StringComparison.Ordinal));
            audio.Write(Stereo(9), 300);
            Assert.Equal(0, queue.Count);
        }
        finally { audio.Stop(); }
    }

    [Fact]
    public async Task ADrainThatLosesItsDeviceEndsInsteadOfWaiting()
    {
        AudioOutputFakeBackend.Reset();
        Volatile.Write(ref AudioOutputFakeBackend.OpenError, E_FAIL);
        using var log = new LogCapture();
        using var audio = new AudioOutput();
        try
        {
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var worker = Field<Thread>(audio, "thread");
            var queue = Field<PcmQueue>(audio, "queue");
            audio.Write(Stereo(3), 300);
            audio.EndOfStream();
            Assert.True(queue.Draining);
            Volatile.Write(ref AudioOutputFakeBackend.FailOpens, int.MaxValue);
            Interlocked.Exchange(ref AudioOutputFakeBackend.FailNextPadding, 1);
            Signal(audio, "bufferEvent");
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.False(queue.Draining);
            Assert.Equal(0, queue.Count);
            Assert.Equal(1, Volatile.Read(ref AudioOutputFakeBackend.Attempts)); // no reopen attempted for a drain
            Assert.Equal(0, log.Warnings); // nothing goes on without output: no failure warning
            // The next START opens again.
            Volatile.Write(ref AudioOutputFakeBackend.FailOpens, 0);
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.SecondStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { audio.Stop(); }
    }
}
