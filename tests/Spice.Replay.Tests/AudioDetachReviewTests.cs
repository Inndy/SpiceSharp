using System.Reflection;
using System.Runtime.InteropServices;
using Spice.Media;
using Spice.Tests;
using Spice.Wire;
using SpiceViewer;

namespace Spice.Replay.Tests;

// Queue tests use a manual clock. Callback tests invoke the linked production notification vtable directly;
// endpoint enumeration, registration and playback remain fake, with no host audio or UI access.
[Collection("Allocation profiling")]
public sealed class AudioDetachReviewTests
{
    static short[] Packet(int frames, int channels, short marker) => Enumerable.Repeat(marker, frames * channels).ToArray();
    static T Field<T>(AudioOutput audio, string name) =>
        (T)typeof(AudioOutput).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audio)!;

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(48000, 2)]
    public void FastReattachDuringADrainKeepsEveryHeldPacketInsideTheClockBudget(int rate, int channels)
    {
        long now = 0;
        var q = new PcmQueue(() => now);
        q.Reset(channels, rate);
        q.Attach();
        int perTenMs = rate / 100;
        q.Write(Packet(perTenMs, channels, 111), 300); // A stamped 1300, clock 1000.
        Assert.True(q.ReadyFrames() >= rate * 40 / 1000);
        q.Transfer(new short[rate * 40 / 1000 * channels], false, rate * 40 / 1000);
        // A 30 ms clock rebase is within the held gate. A/B's new deadlines are +330/+340 ms.
        Assert.Equal(0, q.Write(Packet(perTenMs, channels, 222), 340));
        Assert.True(q.BeginDrain()); // There will be no new packet to repair an incorrect reattach.
        q.Detach();
        now = 10;
        q.Attach(); // New endpoint opens quickly; the virtual device still held 30 ms.
        Assert.True(q.Draining);
        Assert.Equal(0, q.DeviceQueuedFrames); // The new physical endpoint is empty.

        var played = new short[q.Count];
        q.Dequeue(played, false);
        int a = Array.FindIndex(played, sample => sample == 111) / channels;
        int b = Array.FindIndex(played, sample => sample == 222) / channels;
        Assert.InRange(a, (320 - PcmQueue.ToleranceMs) * rate / 1000, (320 + PcmQueue.ToleranceMs) * rate / 1000);
        Assert.InRange(b, (330 - PcmQueue.ToleranceMs) * rate / 1000, (330 + PcmQueue.ToleranceMs) * rate / 1000);
        // Current Attach drops the residual 30 ms device lead. A/B start at +260/+270: 60 ms early.
    }

    [Theory]
    [InlineData(44100, 1)]
    [InlineData(48000, 2)]
    public void ReattachSilenceProtectsThePreviousStreamsTailUntilItsFinalFrameLeaves(int rate, int channels)
    {
        long now = 0;
        var q = new PcmQueue(() => now);
        q.Reset(channels, rate);
        q.Attach();
        q.Write(Packet(rate / 100, channels, 111), 300);
        q.ReadyFrames();
        q.Transfer(new short[rate * 40 / 1000 * channels], false, rate * 40 / 1000);
        int oldSamples = q.Count;
        Assert.True(q.BeginDrain());
        q.Detach();
        Assert.True(q.TryContinue(channels, rate));
        Assert.Equal(0, q.Write(Packet(rate / 100, channels, 222), 100));
        now = 10;
        q.Attach(); // Thirty ms is now a real prefix of the old stream's held schedule.
        q.Dequeue(new short[oldSamples], false);

        // Consuming the old count must leave the inserted 30 ms protected, including the old PCM at +20 ms.
        Assert.Equal(0, q.Write(Packet(rate / 100, channels, 333), 100));
        var finalOldPart = new short[rate * 30 / 1000 * channels];
        q.Dequeue(finalOldPart, false);
        Assert.All(finalOldPart[..(rate * 20 / 1000 * channels)], sample => Assert.Equal(0, sample));
        Assert.Equal(Packet(rate / 100, channels, 111), finalOldPart[(rate * 20 / 1000 * channels)..]);
        Assert.True(q.Write(Packet(rate / 100, channels, 444), 500) > 0); // Protection ends with the old tail.
    }

    [Theory]
    [InlineData(40, 2190)]
    [InlineData(100, 2200)]
    public void ReattachNearTheQueueCapPreservesPcmAndBoundsTheAddedSilence(int oldDeviceMs, int expectedQueuedMs)
    {
        const int rate = 48000, channels = 2;
        long now = 0;
        var q = new PcmQueue(() => now);
        q.Reset(channels, rate);
        q.Attach();
        short[] pcm = Packet(rate * PcmQueue.RingMs / 1000, channels, 111);
        q.Write(pcm, PcmQueue.MaxDelayMs);
        q.ReadyFrames();
        q.Transfer(new short[rate * 40 / 1000 * channels], false, rate * oldDeviceMs / 1000);
        // 40 ms models the endpoint buffer; 100 ms includes a 60 ms stream latency. The latter exhausts ring room.
        q.Detach();
        now = 10;
        q.Attach();
        Assert.Equal(rate * expectedQueuedMs / 1000 * channels, q.Count);
        Assert.InRange(q.Count, 0, rate * (PcmQueue.MaxDelayMs + PcmQueue.RingMs) / 1000 * channels);
        var held = new short[q.Count];
        q.Dequeue(held, false);
        Assert.All(held[..^pcm.Length], sample => Assert.Equal(0, sample));
        Assert.Equal(pcm, held[^pcm.Length..]);
    }

    [Fact]
    public void ReattachDoesNotReviveAnEmptyDrainOrAddDelayToUnsyncedAudio()
    {
        long now = 0;
        var q = new PcmQueue(() => now);
        q.Reset(2, 48000);
        q.Attach();
        q.Write(Packet(480, 2, 111), 40);
        Assert.True(q.BeginDrain());
        Assert.Equal(480, q.ReadyFrames());
        q.Transfer(new short[q.Count], false, 480);
        q.Detach();
        now = 1;
        q.Attach();
        Assert.Equal(0, q.Count);
        Assert.True(q.TryFinishDrain(0)); // E3: no newly inserted silence can start another client.

        q.Reset(2, 48000);
        q.Attach();
        q.Write(Packet(960, 2, 222));
        q.ReadyFrames();
        q.Transfer(new short[960], false, 480);
        q.Detach();
        now = 2;
        q.Attach();
        var off = new short[q.Count];
        q.Dequeue(off, false);
        Assert.Equal(Packet(480, 2, 222), off); // Off mode plays the remaining PCM now, with no synthetic prefix.
    }

    [Fact]
    public void RepeatedFastReattachWithSilenceRemainsAllocationFreeAfterWarmUp()
    {
        long now = 0;
        var q = new PcmQueue(() => now);
        q.Reset(2, 48000);
        q.Attach();
        short[] pcm = Packet(480, 2, 111), device = new short[1920 * 2];
        q.Write(pcm, 300);
        void Cycle()
        {
            q.ReadyFrames();
            q.Transfer(device, false, 1920);
            q.Detach();
            now += 10;
            q.Attach();
            q.Write(pcm, 300);
        }
        for (int i = 0; i < 100; i++) Cycle();
        AllocationWindow.Measure("fast reattach with prefix", () => { for (int i = 0; i < 1000; i++) Cycle(); });
        Assert.Equal(310 * 48 * 2, q.Count);
        Assert.Equal(1, q.Statistics.SyncCorrections);
    }

    [Fact]
    public async Task LastBackoffEndsAtTheBudgetInsteadOfWaitingTheWholeRetryInterval()
    {
        AudioOutputFakeBackend.Reset();
        Volatile.Write(ref AudioOutputFakeBackend.OpenError, unchecked((int)0x80004005));
        Volatile.Write(ref AudioOutputFakeBackend.FailOpens, int.MaxValue);
        var savedSink = Log.Sink;
        Log.Sink = null;
        using var audio = new AudioOutput();
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(AudioOutput).GetField("retryFirstMs", fields)!.SetValue(audio, 60_000);
        typeof(AudioOutput).GetField("retryMaxMs", fields)!.SetValue(audio, 60_000);
        typeof(AudioOutput).GetField("retryBudgetMs", fields)!.SetValue(audio, 50);
        try
        {
            audio.Start(2, 48000);
            for (int i = 0; i < 1000 && Field<int>(audio, "detachedWaitMs") != Timeout.Infinite; i++)
                await Task.Delay(5);
            Assert.Equal(Timeout.Infinite, Field<int>(audio, "detachedWaitMs"));
            // TickCount64 can quantize several short waits into the same tick near the deadline. Do not pin the
            // exact attempt count; the budget must promptly park a retry interval that would otherwise take 60 s.
            int attempts = Volatile.Read(ref AudioOutputFakeBackend.Attempts);
            Assert.True(attempts >= 2);
            await Task.Delay(100);
            Assert.Equal(attempts, Volatile.Read(ref AudioOutputFakeBackend.Attempts));
        }
        finally { audio.Stop(); Log.Sink = savedSink; }
    }

    [Fact]
    public async Task DetachedDrainCompletionAndContinuationHaveExactlyOneWinner()
    {
        var q = new PcmQueue(() => 0);
        q.Reset(2, 48000);
        q.Attach();
        q.Write(Packet(480, 2, 111), 300);
        Assert.True(q.BeginDrain());
        q.Detach();
        using var gate = new ManualResetEventSlim(false, 0);
        Task<bool> end = Task.Run(() => { gate.Wait(); return q.TryEndDetachedDrain(); });
        Task<bool> start = Task.Run(() => { gate.Wait(); return q.TryContinue(2, 48000); });
        gate.Set();
        bool[] results = await Task.WhenAll(end, start).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(results[0] ^ results[1]);
        q.Write(Packet(480, 2, 222), 500);
        Assert.Equal(!results[0], q.Count > 0);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void FractionalExpiryUsesElapsedTimeWithoutRoundingEachWrite(int rate)
    {
        long now = 0;
        var q = new PcmQueue(() => now);
        q.Reset(2, rate);
        q.Attach();
        q.Write(Packet(rate / 10, 2, 111), 300);
        int initial = q.Count;
        q.Detach();
        for (now = 1; now <= 99; now++)
            q.Write(new short[2], 400 + (int)((now - 1) * 1000 / rate) - (int)now);
        now = 100;
        q.Attach();
        Assert.Equal(rate / 10, q.Statistics.ExpiredFrames);
        Assert.Equal(initial + 99 * 2 - rate / 10 * 2, q.Count);
    }

    [Fact]
    public async Task StartWinningDuringAnEndpointFailureKeepsItsAudioOnTheSameWorker()
    {
        AudioOutputFakeBackend.Reset();
        var savedSink = Log.Sink;
        Log.Sink = null;
        using var entered = new ManualResetEventSlim(false, 0);
        using var release = new ManualResetEventSlim(false, 0);
        using var audio = new AudioOutput();
        try
        {
            audio.Start(2, 48000);
            await AudioOutputFakeBackend.FirstStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var worker = Field<Thread>(audio, "thread");
            var q = Field<PcmQueue>(audio, "queue");
            audio.Write(Packet(480, 2, 111), 300);
            AudioOutputFakeBackend.BeforeGetBuffer = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("failed GetBuffer was not released");
                AudioOutputFakeBackend.BeforeGetBuffer = null;
                throw new COMException("injected endpoint failure during GetBuffer", unchecked((int)0x80004005));
            };
            Volatile.Write(ref AudioOutputFakeBackend.Padding, 0);
            Field<EventWaitHandle>(audio, "bufferEvent").Set();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            audio.EndOfStream();
            Assert.True(q.Draining);
            audio.Start(2, 48000); // Wins before failure processing can end the detached drain.
            audio.Write(Packet(480, 2, 222), 300);
            release.Set();
            await AudioOutputFakeBackend.SecondStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(worker, Field<Thread>(audio, "thread"));
            Assert.True(worker.IsAlive);
            Assert.False(q.Draining);
            Assert.False(q.IsDetached);
            var held = new short[q.Count];
            q.Dequeue(held, false);
            Assert.Contains((short)222, held);
        }
        finally
        {
            release.Set();
            AudioOutputFakeBackend.BeforeGetBuffer = null;
            audio.Stop();
            Log.Sink = savedSink;
        }
    }

    [Fact]
    public unsafe void NativeNotificationsRouteEventsAndDoNotAllocateOnTheirNormalPath()
    {
        using var defaults = new AutoResetEvent(false);
        using var devices = new AutoResetEvent(false);
        DeviceNotifications.Native* native = DeviceNotifications.Create(defaults, devices);
        try
        {
            var defaultCall = (delegate* unmanaged[Stdcall]<DeviceNotifications.Native*, int, int, char*, int>)native->Vtable[6];
            var addedCall = (delegate* unmanaged[Stdcall]<DeviceNotifications.Native*, char*, int>)native->Vtable[4];
            var stateCall = (delegate* unmanaged[Stdcall]<DeviceNotifications.Native*, char*, uint, int>)native->Vtable[3];
            Assert.Equal(0, defaultCall(native, 1, 0, null)); // Capture is not the render default.
            Assert.Equal(0, defaultCall(native, 0, 1, null)); // Multimedia is not the console role.
            Assert.False(defaults.WaitOne(0));
            Assert.Equal(0, DeviceNotifications.DefaultChanges(native));
            Assert.Equal(0, stateCall(native, null, 2)); // Disabled is not active.
            Assert.False(devices.WaitOne(0));
            Assert.Equal(0, addedCall(native, null));
            Assert.True(devices.WaitOne(0));
            Assert.Equal(0, stateCall(native, null, 1));
            Assert.True(devices.WaitOne(0));

            // Warm the unmanaged-callable methods and event handles before measuring callbacks themselves.
            defaultCall(native, 0, 0, null); defaults.WaitOne(0);
            AllocationWindow.Measure("native device callbacks", () =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    defaultCall(native, 0, 0, null); defaults.WaitOne(0);
                    addedCall(native, null); devices.WaitOne(0);
                    stateCall(native, null, 1); devices.WaitOne(0);
                }
            });
            Assert.True(DeviceNotifications.DefaultChanges(native) >= 1001);
        }
        finally { DeviceNotifications.ReleaseOwner(native); }
    }

    [Fact]
    public unsafe void LateNativeNotificationsCannotThrowAcrossTheBoundaryAfterEventDisposal()
    {
        using var defaults = new AutoResetEvent(false);
        using var devices = new AutoResetEvent(false);
        DeviceNotifications.Native* native = DeviceNotifications.Create(defaults, devices);
        try
        {
            defaults.Dispose(); devices.Dispose();
            var defaultCall = (delegate* unmanaged[Stdcall]<DeviceNotifications.Native*, int, int, char*, int>)native->Vtable[6];
            var addedCall = (delegate* unmanaged[Stdcall]<DeviceNotifications.Native*, char*, int>)native->Vtable[4];
            var stateCall = (delegate* unmanaged[Stdcall]<DeviceNotifications.Native*, char*, uint, int>)native->Vtable[3];
            Assert.Equal(0, defaultCall(native, 0, 0, null));
            Assert.Equal(0, addedCall(native, null));
            Assert.Equal(0, stateCall(native, null, 1));
        }
        finally { DeviceNotifications.ReleaseOwner(native); }
    }
}
