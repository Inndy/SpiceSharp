using System.Reflection;
using Spice.Media;
using Spice.Wire;
using SpiceViewer;

namespace Spice.Replay.Tests;

// Runs the real linked renderer lifecycle against AudioOutputFakeBackend; never opens a device or a window.
[Collection("Allocation profiling")]
public sealed class AudioOutputLifecycleReviewTests
{
    static T Field<T>(AudioOutput audio, string name) =>
        (T)typeof(AudioOutput).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audio)!;
    static void Signal(AudioOutput audio, string name) => Field<EventWaitHandle>(audio, name).Set();
    static short[] Packet(short marker) => Enumerable.Repeat(marker, 480 * 2).ToArray();

    [Fact]
    public async Task NormalSameFormatStartContinuesButDisposeStopsTheDrainImmediately()
    {
        AudioOutputFakeBackend.Reset();
        var savedSink = Log.Sink;
        Log.Sink = null;
        using var audio = new AudioOutput();
        try
        {
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var worker = Field<Thread>(audio, "thread");
            var queue = Field<PcmQueue>(audio, "queue");
            audio.Write(Packet(111), 300);
            audio.EndOfStream();
            Assert.True(queue.Draining);
            audio.Start(2, 48000);
            Assert.Same(worker, Field<Thread>(audio, "thread"));
            Assert.Equal(1, Volatile.Read(ref AudioOutputFakeBackend.Starts));
            Assert.False(queue.Draining);
            audio.Write(Packet(222), 500);
            audio.EndOfStream();
            long held = queue.Count;
            audio.Dispose(); // Must stop/join now, not wait for media drain or a fake buffer event.
            Assert.False(worker.IsAlive);
            Assert.Equal(0, queue.Count);
            Assert.Equal(held, queue.Statistics.DiscardedSamples);
            audio.Start(2, 48000);
            audio.EndOfStream();
            audio.Write(Packet(333), 300);
            Assert.Equal(0, queue.Count);
            Assert.Equal(1, Volatile.Read(ref AudioOutputFakeBackend.Starts));
        }
        finally
        {
            audio.Dispose();
            Log.Sink = savedSink;
        }
    }

    [Fact]
    public async Task DifferentFormatStartJoinsOldWorkerAndLogsItsDiscardedQueue()
    {
        AudioOutputFakeBackend.Reset();
        var savedSink = Log.Sink;
        var savedLevel = Log.MinLevel;
        var closed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Log.MinLevel = LogLevel.Debug;
        Log.Sink = (_, message) =>
        {
            if (message.StartsWith("Audio: stream closed;", StringComparison.Ordinal)) closed.Enqueue(message);
        };
        using var audio = new AudioOutput();
        try
        {
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldWorker = Field<Thread>(audio, "thread");
            var queue = Field<PcmQueue>(audio, "queue");
            audio.Write(Packet(111), 300);
            audio.EndOfStream();
            long held = queue.Count;
            audio.Start(2, 44100);
            await AudioOutputFakeBackend.SecondStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(oldWorker.IsAlive);
            Assert.NotSame(oldWorker, Field<Thread>(audio, "thread"));
            Assert.Equal(44100, AudioOutputFakeBackend.Rate);
            Assert.Contains(closed, message => message.Contains($"discarded on close {held}", StringComparison.Ordinal));
        }
        finally
        {
            audio.Stop();
            Log.Sink = savedSink;
            Log.MinLevel = savedLevel;
        }
    }

    [Fact]
    public async Task ReopenedEmptyDrainRetiresBeforeStartingAnotherClient()
    {
        AudioOutputFakeBackend.Reset();
        var savedSink = Log.Sink;
        Log.Sink = null;
        using var audio = new AudioOutput();
        bool retired = false;
        Task? retirement = null;
        try
        {
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var worker = Field<Thread>(audio, "thread");
            var queue = Field<PcmQueue>(audio, "queue");
            audio.Write(Packet(111), 300);
            audio.EndOfStream();
            Assert.True(queue.Draining);

            // Drive the real Fill path with deterministic virtual buffer events until the tail is in the old device.
            for (int i = 0; i < 16 && queue.Count > 0; i++)
            {
                Volatile.Write(ref AudioOutputFakeBackend.Padding, 0);
                Task wrote = AudioOutputFakeBackend.ExpectWrite();
                Signal(audio, "bufferEvent");
                await wrote.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.Equal(0, queue.Count);
            Assert.True(queue.Draining);
            Assert.True(Volatile.Read(ref AudioOutputFakeBackend.Padding) > 0);

            // Default-device replacement while the last device-held tail drains. New endpoint starts empty.
            Signal(audio, "deviceChanged");
            await AudioOutputFakeBackend.SecondOpen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = Task.Run(() => worker.Join());
            Task winner = await Task.WhenAny(retirement, AudioOutputFakeBackend.SecondStart.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
            retired = winner == retirement;
        }
        finally
        {
            audio.Stop();
            if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Log.Sink = savedSink;
        }
        Assert.Equal((true, 1), (retired, Volatile.Read(ref AudioOutputFakeBackend.Starts)));
        // Current code ignores the pre-roll Fill's true result: the queue closes but a second client starts and idles.
    }

    [Fact]
    public async Task SwitchToOffDuringGetBufferMustNotConsumeUnprimedNewAudio()
    {
        AudioOutputFakeBackend.Reset();
        var savedSink = Log.Sink;
        Log.Sink = null;
        using var bufferReached = new ManualResetEventSlim(false, 0);
        using var releaseBuffer = new ManualResetEventSlim(false, 0);
        using var audio = new AudioOutput();
        Task? switched = null;
        try
        {
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queue = Field<PcmQueue>(audio, "queue");
            audio.Write(Packet(111), 300); // The selected old 40 ms fill contains only the leading hold silence.
            AudioOutputFakeBackend.BeforeGetBuffer = () =>
            {
                bufferReached.Set();
                if (!releaseBuffer.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("GetBuffer was not released");
            };
            Volatile.Write(ref AudioOutputFakeBackend.Padding, 0);
            Task wrote = AudioOutputFakeBackend.ExpectWrite();
            Signal(audio, "bufferEvent");
            Assert.True(bufferReached.Wait(TimeSpan.FromSeconds(5)));
            short[] next = Packet(222);
            switched = Task.Run(() => audio.Write(next));
            // Allow a correct serialized switch to wait for the outstanding fill. Do not require racing Write to finish.
            await Task.WhenAny(switched, Task.Delay(100));
            releaseBuffer.Set();
            await switched.WaitAsync(TimeSpan.FromSeconds(5));
            await wrote.WaitAsync(TimeSpan.FromSeconds(5));
            short[] captured = AudioOutputFakeBackend.CapturedSamples();
            Assert.Equal((960, false), (queue.Count, captured.Any(sample => sample != 0)));
            // Fresh 10 ms must remain queued for the 20 ms prime; current Fill consumes it using the old 40 ms readiness.
        }
        finally
        {
            releaseBuffer.Set();
            if (switched is not null) await switched.WaitAsync(TimeSpan.FromSeconds(5));
            AudioOutputFakeBackend.BeforeGetBuffer = null;
            audio.Stop();
            Log.Sink = savedSink;
        }
    }

    [Fact]
    public async Task StartDuringFailedWorkerRetirementCannotContinueOntoTheDyingWorker()
    {
        AudioOutputFakeBackend.Reset();
        using var exitReached = new ManualResetEventSlim(false, 0);
        using var allowExit = new ManualResetEventSlim(false, 0);
        var savedSink = Log.Sink;
        var savedLevel = Log.MinLevel;
        Log.MinLevel = LogLevel.Warn;
        Log.Sink = (_, message) =>
        {
            if (!message.StartsWith("Audio: output closed;", StringComparison.Ordinal)) return;
            exitReached.Set(); // The real render loop has chosen to exit, before Finish closes the queue.
            if (!allowExit.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Retiring worker was not released");
        };
        using var audio = new AudioOutput();
        Task? start = null;
        try
        {
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldWorker = Field<Thread>(audio, "thread");
            var queue = Field<PcmQueue>(audio, "queue");
            audio.Write(Packet(111), 300);
            audio.EndOfStream();
            Interlocked.Exchange(ref AudioOutputFakeBackend.FailNextPadding, 1);
            Signal(audio, "bufferEvent");
            Assert.True(exitReached.Wait(TimeSpan.FromSeconds(5)));

            short[] next = Packet(222);
            start = Task.Run(() => { audio.Start(2, 48000); audio.Write(next, 300); });
            // A correct Start may join the retiring worker. Do not require it to return while that worker is paused.
            await Task.WhenAny(start, Task.Delay(100));
            allowExit.Set();
            await start.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(oldWorker.Join(TimeSpan.FromSeconds(5)));
            Assert.True(queue.Count > 0, "The retiring worker closed the queue accepted by the new START");
            await AudioOutputFakeBackend.SecondStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            allowExit.Set();
            if (start is not null) await start.WaitAsync(TimeSpan.FromSeconds(5));
            audio.Stop();
            Log.Sink = savedSink;
            Log.MinLevel = savedLevel;
        }
    }
}
