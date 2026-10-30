using System.Diagnostics;
using System.Runtime.InteropServices;
using Spice.Media;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace SpiceViewer;

/// <summary>
/// Shared-mode, event-driven WASAPI playback of interleaved s16 PCM.
/// <para>
/// <see cref="Start"/> spawns a render thread that owns every COM object (MTA) and
/// <see cref="Stop"/> ends it, so nothing wakes the CPU between playbacks. The network
/// thread feeds a <see cref="PcmQueue"/> (~200 ms; longer while clock pacing holds audio
/// back, docs/decisions/0030) through <see cref="Write"/>; the render thread drains it on
/// each WASAPI buffer event, writing silence on underflow, and tells the queue how much
/// the device still holds.
/// </para>
/// <para>
/// The stream outlives its device (docs/decisions/0031). The render thread attaches to the default device, and when
/// the device fails or the default changes it detaches (<see cref="PcmQueue.Detach"/>) while the stream goes on, and
/// reattaches when it can: at once after a default change (not a failure); after a failure, after a backoff of 50 ms
/// doubling to 2 s, for 10 s from the first failure of a run (a run ends only once a device has played for 1 s);
/// then only when a device notification, a same-format PLAYBACK_START, or the end of the stream wakes it (no
/// timeout). Stop or a notification ends any wait at once.
/// </para>
/// All public members are thread-safe.
/// </summary>
sealed unsafe class AudioOutput : IDisposable
{
    const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    const int ErrorNoEvents = unchecked((int)0x800705B4); // HRESULT_FROM_WIN32(ERROR_TIMEOUT)
    const int E_NOTFOUND = unchecked((int)0x80070490); // HRESULT_FROM_WIN32(ERROR_NOT_FOUND): no render endpoint
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 2;

    const long DeviceBufferHns = 400_000; // 40 ms endpoint buffer (100 ns units)
    const int EventTimeoutMs = 2000;

    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    enum Exit { Stop, DefaultChanged, Failed }

    // Reattach backoff (docs/decisions/0031): fields rather than constants so tests can shorten them.
    // healthyMs: how long a device must keep asking for buffers before a run of failures is over.
    int retryFirstMs = 50, retryMaxMs = 2000, retryBudgetMs = 10_000, healthyMs = 1000;

    readonly Lock lifecycle = new();
    readonly ManualResetEvent stopEvent = new(false);
    // deviceChanged: the default render device (console role) changed. devicesChanged: a device was added or became
    // active, or a same-format START asks a detached stream to try again; only a detached wait listens to it.
    readonly AutoResetEvent bufferEvent = new(false), deviceChanged = new(false), devicesChanged = new(false);
    Thread? thread;
    bool disposed;

    readonly PcmQueue queue = new();
    int channels, sampleRate; // set by Start while no render thread runs

    // Render thread only, reset with each thread.
    int failures;            // consecutive attaches that failed before playing healthyMs (default changes don't count)
    long failingSince;       // TickCount64 at the first of them
    int defaultNotifications, defaultIgnored; // default-device notifications, and those naming the device in use
    /// <summary>The detached wait's timeout while it waits (ms; <see cref="Timeout.Infinite"/>: events only), else 0.</summary>
    volatile int detachedWaitMs;

    volatile int gainQ16 = 65536;
    volatile bool muted;

    /// <summary>Opens the default render device for <paramref name="channels"/> x s16 at <paramref name="sampleRate"/>. Restarts if already playing.</summary>
    public void Start(int channels, int sampleRate)
    {
        if (channels is < 1 or > 8 || sampleRate is < 8000 or > 384000)
        {
            Log.Warn($"Audio: unsupported format {channels} ch {sampleRate} Hz; ignoring playback");
            return;
        }
        lock (lifecycle)
        {
            if (disposed) return;
            // A stream that ended is still playing out its held audio, or a stream waits for a device: continue it if
            // the format matches, and let a waiting render thread try the device again now; otherwise (or once it
            // ended) restart (docs/decisions/0030, 0031).
            if (thread is not null && queue.TryContinue(channels, sampleRate))
            {
                if (queue.IsDetached) devicesChanged.Set();
                return;
            }
            StopCore();
            this.channels = channels;
            this.sampleRate = sampleRate;
            queue.Reset(channels, sampleRate);
            stopEvent.Reset();
            deviceChanged.Reset();
            devicesChanged.Reset();
            bufferEvent.Reset();
            thread = new Thread(RenderThread) { IsBackground = true, Name = "SpiceViewer audio", Priority = ThreadPriority.Highest };
            thread.Start();
        }
    }

    /// <summary>
    /// Queues interleaved PCM to be heard <paramref name="delayMs"/> from now, or as soon as possible
    /// (<see cref="PcmQueue.Unsynced"/>). Never blocks beyond a short copy; see <see cref="PcmQueue.Write"/>.
    /// </summary>
    public void Write(ReadOnlySpan<short> interleavedPcm, int delayMs = PcmQueue.Unsynced, int gapMs = 0)
        => queue.Write(interleavedPcm, delayMs, gapMs); // corrections are counted, and logged when the stream closes

    /// <summary>Closes the stream and ends the render thread.</summary>
    public void Stop()
    {
        lock (lifecycle) { if (!disposed) StopCore(); }
    }

    /// <summary>
    /// The stream ended (PLAYBACK_STOP). Under clock pacing the audio still held back plays out to its deadline and the
    /// render thread then closes the device on its own, from its buffer events (no timer); otherwise, or while no device
    /// is attached (nothing could be heard, docs/decisions/0031), it closes now, as <see cref="Stop"/> does. A
    /// <see cref="Start"/> meanwhile continues on the open device.
    /// </summary>
    public void EndOfStream()
    {
        lock (lifecycle)
        {
            if (disposed || thread is null) return;
            if (!queue.BeginDrain()) StopCore();
        }
    }

    /// <summary>Linear gain 0..1 applied to the samples (does not touch the Windows mixer).</summary>
    public void SetVolume(float volume)
    {
        if (!float.IsFinite(volume)) return;
        gainQ16 = (int)(Math.Clamp(volume, 0f, 1f) * 65536f);
    }

    public void SetMute(bool mute) => muted = mute;

    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return;
            StopCore();
            disposed = true;
        }
        stopEvent.Dispose();
        bufferEvent.Dispose();
        deviceChanged.Dispose();
        devicesChanged.Dispose();
    }

    void StopCore()
    {
        queue.Close();
        if (thread is null) return;
        stopEvent.Set();
        thread.Join();
        thread = null;
    }

    // ---- render thread -------------------------------------------------------------------

    void RenderThread()
    {
        int ch = channels, rate = sampleRate; // fixed for this thread; Start joins before changing them
        HRESULT com = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        if (com.Value < 0 && com.Value != RPC_E_CHANGED_MODE)
        {
            Retire();
            Log.Error($"Audio: CoInitializeEx failed (0x{com.Value:X8})");
            Finish();
            return;
        }
        IMMDeviceEnumerator* enumerator = null;
        DeviceNotifications.Native* notifications = null;
        bool registered = false;
        failures = 0;
        defaultNotifications = defaultIgnored = 0;
        try
        {
            Guid clsid = CLSID_MMDeviceEnumerator, iid = IMMDeviceEnumerator.IID_Guid;
            PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&enumerator).ThrowOnFailure();

            notifications = DeviceNotifications.Create(deviceChanged, devicesChanged);
            try
            {
                enumerator->RegisterEndpointNotificationCallback((IMMNotificationClient*)notifications);
                registered = true;
            }
            catch (Exception e) { Log.Debug($"Audio: device notifications unavailable (0x{e.HResult:X8})"); }

            WaitHandle[] playing = [stopEvent, deviceChanged, bufferEvent];
            WaitHandle[] detached = [stopEvent, deviceChanged, devicesChanged];
            bool eventsOnly = false;
            while (true)
            {
                Exit exit = Play(enumerator, ch, rate, playing, out int error, out string? message);
                if (exit == Exit.Stop || stopEvent.WaitOne(0)) break;
                // The device is gone; the stream is not (docs/decisions/0031).
                queue.Detach();
                if (exit == Exit.DefaultChanged)
                {
                    // Not a failure: the stream moves to the new default device at once.
                    Log.Info("Audio: default output device changed; reopening");
                    continue;
                }
                // A stream that ended can't play out without a device: it ends here instead (atomic with TryContinue).
                if (queue.TryEndDetachedDrain()) break;
                long now = Environment.TickCount64;
                if (failures++ == 0)
                {
                    failingSince = now;
                    eventsOnly = false;
                    Log.Warn($"Audio: {Describe(error, message)}; playback goes on without output and the default "
                        + "device is reopened when it can be");
                }
                bool retry = error != E_NOTFOUND && now - failingSince < retryBudgetMs;
                // The last backoff ends at the budget, so the final attempt doesn't run past it.
                int timeout = retry ? (int)Math.Min(Math.Min((long)retryFirstMs << Math.Min(failures - 1, 20), retryMaxMs),
                    retryBudgetMs - (now - failingSince)) : Timeout.Infinite;
                if (!retry && !eventsOnly)
                {
                    eventsOnly = true;
                    Log.Warn("Audio: no output device can be opened; waiting for a device change or the next playback start");
                }
                detachedWaitMs = timeout;
                int which = WaitHandle.WaitAny(detached, timeout);
                detachedWaitMs = 0;
                if (which == 0) break;
            }
        }
        catch (Exception e)
        {
            Retire();
            Log.Warn($"Audio: cannot open output: {e.Message} (0x{e.HResult:X8})");
        }
        finally
        {
            if (registered) { try { enumerator->UnregisterEndpointNotificationCallback((IMMNotificationClient*)notifications); } catch { } }
            if (enumerator != null) enumerator->Release();
            if (notifications != null)
            {
                defaultNotifications = DeviceNotifications.DefaultChanges(notifications);
                DeviceNotifications.ReleaseOwner(notifications);
            }
            if (com.Value >= 0) PInvoke.CoUninitialize();
            Finish();
        }
    }

    static string Describe(int error, string? message) => error switch
    {
        AUDCLNT_E_DEVICE_INVALIDATED => "output device was removed or reconfigured",
        ErrorNoEvents => "no buffer events from the output device",
        E_NOTFOUND => "no output device",
        _ => $"output error: {message} (0x{error:X8})",
    };

    /// <summary>
    /// The worker exits (any path): it gives the queue up first, under the queue's lock, so from then on a same-format
    /// <see cref="Start"/> fails <see cref="PcmQueue.TryContinue"/> and restarts (joining this worker) instead of
    /// continuing onto it (review E4). Most exits closed it already: <see cref="StopCore"/> before signalling stop,
    /// <see cref="PcmQueue.TryFinishDrain"/> at the end of a drain, <see cref="PcmQueue.TryEndDetachedDrain"/> for a
    /// drain without a device. A device failure no longer ends the stream (docs/decisions/0031); only a failure before
    /// any device can be tried (COM or the device enumerator) does, and a START continuing in that window loses its
    /// stream.
    /// </summary>
    void Retire() => queue.Close();

    void Finish()
    {
        Retire();
        if (!Log.IsEnabled(LogLevel.Debug)) return;
        var s = queue.Statistics;
        string sync = s.SyncCorrections == 0 && s.GapSilenceFrames == 0 && s.GapTrimmedFrames == 0 ? ""
            : $"; clock pacing made {s.SyncCorrections} corrections ({s.SyncSilenceFrames} frames of silence inserted, "
              + $"{s.SyncDroppedFrames} dropped), stamp gaps {s.GapSilenceFrames} frames of silence, {s.GapTrimmedFrames} cut";
        string detached = s.Detaches == 0 ? ""
            : $"; without a device {s.Detaches} times, {s.DetachedMs} ms, {s.ExpiredFrames} frames expired";
        string notified = defaultNotifications == 0 && defaultIgnored == 0 ? ""
            : $"; default device notifications {defaultNotifications}, {defaultIgnored} for the device in use";
        Log.Debug($"Audio: stream closed; underruns {s.Underruns}, dropped samples {s.DroppedSamples}, "
            + $"discarded on close {s.DiscardedSamples}{sync}{detached}{notified}");
    }

    /// <summary>True if the default render device (console role) is still <paramref name="id"/>.</summary>
    static bool IsDefault(IMMDeviceEnumerator* enumerator, char* id)
    {
        if (id == null) return false;
        IMMDevice* device = null;
        PWSTR current = default;
        try
        {
            enumerator->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, &device);
            device->GetId(&current);
            return current.Value != null && MemoryMarshal.CreateReadOnlySpanFromNullTerminated(current.Value)
                .SequenceEqual(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(id));
        }
        catch { return false; }
        finally
        {
            if (current.Value != null) Marshal.FreeCoTaskMem((nint)current.Value);
            if (device != null) device->Release();
        }
    }

    /// <summary>
    /// Attaches to the current default device and pumps it until stop, the end of a drain, a default device change, or
    /// failure. Logs only success; the caller logs failures, once per run of them. A device that keeps asking for
    /// buffers for <see cref="healthyMs"/> after its first one ends a run of failures (<see cref="failures"/>).
    /// </summary>
    Exit Play(IMMDeviceEnumerator* enumerator, int ch, int rate, WaitHandle[] waits, out int error, out string? message)
    {
        IMMDevice* device = null;
        IAudioClient* client = null;
        IAudioRenderClient* render = null;
        PWSTR id = default;
        bool started = false;
        error = 0;
        message = null;
        try
        {
            // Earlier notifications are answered by the device opened now: only later ones count (one default change
            // can be announced more than once; a stale one would reopen the same device, live G5).
            deviceChanged.Reset();
            devicesChanged.Reset();
            enumerator->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, &device);
            try { device->GetId(&id); } catch { id = default; } // only to recognize repeated notifications
            Guid iid = IAudioClient.IID_Guid;
            device->Activate(&iid, CLSCTX.CLSCTX_INPROC_SERVER, null, (void**)&client);

            WAVEFORMATEX format = new()
            {
                wFormatTag = 1, // WAVE_FORMAT_PCM
                nChannels = (ushort)ch,
                nSamplesPerSec = (uint)rate,
                wBitsPerSample = 16,
                nBlockAlign = (ushort)(ch * 2),
                nAvgBytesPerSec = (uint)(rate * ch * 2),
                cbSize = 0,
            };
            uint flags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                | PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, DeviceBufferHns, 0, &format, null);
            bufferEvent.Reset();
            client->SetEventHandle((HANDLE)bufferEvent.SafeWaitHandle.DangerousGetHandle());
            iid = IAudioRenderClient.IID_Guid;
            client->GetService(&iid, (void**)&render);
            uint bufferFrames;
            client->GetBufferSize(&bufferFrames);
            // Audio the device holds past its buffer; clock pacing counts it as already scheduled (docs/decisions/0030).
            long latencyHns = 0;
            try { client->GetStreamLatency(&latencyHns); } catch { latencyHns = 0; }
            int latencyFrames = (int)Math.Clamp(latencyHns * rate / 10_000_000, 0, rate);
            Log.Info($"Audio: playing {ch} ch {rate} Hz s16, device buffer {bufferFrames * 1000 / (uint)rate} ms, "
                + $"stream latency {latencyHns / 10_000} ms");

            // The queue expires what played while detached and primes again (docs/decisions/0031).
            queue.Attach();
            // Pre-roll: queued audio, or ~10 ms of silence. A drain can end here (the replaced device already played the
            // rest): then no client starts (review E3).
            if (Fill(client, render, bufferFrames, ch, latencyFrames)) return Exit.Stop;
            client->Start();
            started = true;

            long firstBuffer = -1; // TickCount64 of the first buffer event while failures are counted
            while (true)
            {
                int which = WaitHandle.WaitAny(waits, EventTimeoutMs);
                if (which == 0) return Exit.Stop;
                if (which == 1)
                {
                    if (!IsDefault(enumerator, id.Value)) return Exit.DefaultChanged;
                    defaultIgnored++; // announced again, or changed and changed back: the device in use is the default
                    continue;
                }
                if (which == WaitHandle.WaitTimeout)
                {
                    error = ErrorNoEvents;
                    return Exit.Failed;
                }
                if (Fill(client, render, bufferFrames, ch, latencyFrames)) return Exit.Stop; // drained after the stream ended
                if (failures > 0)
                {
                    // A run of failures ends only once the device has played for healthyMs, judged from its buffer
                    // events (no timer); a device that fails soon after its first buffer keeps counting.
                    long now = Environment.TickCount64;
                    if (firstBuffer < 0) firstBuffer = now;
                    else if (now - firstBuffer >= healthyMs)
                    {
                        Log.Info($"Audio: output back after {now - failingSince} ms");
                        failures = 0;
                    }
                }
            }
        }
        catch (Exception e)
        {
            error = e.HResult;
            message = e.Message;
            return Exit.Failed;
        }
        finally
        {
            if (started) { try { client->Stop(); } catch { } }
            if (render != null) render->Release();
            if (client != null) client->Release();
            if (device != null) device->Release();
            if (id.Value != null) Marshal.FreeCoTaskMem((nint)id.Value);
        }
    }

    /// <summary>
    /// Tops up the endpoint buffer with whatever the ring holds. The endpoint buffer itself absorbs
    /// network jitter: silence is written only when it is about to run dry (below ~10 ms queued).
    /// Afterwards the queue learns how much the device holds (padding plus stream latency).
    /// </summary>
    bool Fill(IAudioClient* client, IAudioRenderClient* render, uint bufferFrames, int ch, int latencyFrames)
    {
        uint padding;
        client->GetCurrentPadding(&padding);
        // After PLAYBACK_STOP under clock pacing: no silence is padded, and once the queue and the device are empty
        // the stream is done (returns true).
        bool draining = queue.Draining;
        if (draining && queue.TryFinishDrain((int)padding)) return true;
        if (padding >= bufferFrames) { queue.DeviceQueuedFrames = (int)padding + latencyFrames; return false; }
        uint free = bufferFrames - padding;
        uint lowWater = bufferFrames / 4;
        uint real = Math.Min(free, (uint)queue.ReadyFrames());
        uint frames = real;
        bool starving = !draining && padding + real < lowWater;
        if (starving) frames = Math.Min(free, lowWater - padding);
        if (frames == 0) { queue.DeviceQueuedFrames = (int)padding + latencyFrames; return false; }

        byte* data;
        render->GetBuffer(frames, &data);
        var dst = new Span<short>(data, (int)frames * ch);
        // One locked step takes the audio out of the queue and counts it, with any silence pad, as in the device. It
        // takes less than chosen (or nothing) if the queue changed meanwhile; the rest is silence.
        int n = queue.Transfer(dst[..((int)real * ch)], starving, (int)(padding + frames) + latencyFrames);
        uint flags = 0;
        if (n == 0 || muted) flags = AUDCLNT_BUFFERFLAGS_SILENT;
        else
        {
            if (n < dst.Length) dst[n..].Clear();
            int gain = gainQ16;
            if (gain < 65536) Scale(dst[..n], gain);
        }
        render->ReleaseBuffer(frames, flags);
        return false;
    }

    static void Scale(Span<short> samples, int gainQ16)
    {
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)((samples[i] * gainQ16) >> 16);
    }

    // ---- self test ------------------------------------------------------------------------

    /// <summary>Plays a 1 s 440 Hz tone at 48 kHz stereo through Start/Write/Stop.</summary>
    public static void SelfTest()
    {
        const int Rate = 48000, Chunk = 480, LeadMs = 60; // 10 ms packets, kept ~60 ms ahead
        Log.Info("Audio self-test: 1 s 440 Hz tone, 48 kHz stereo");
        using var audio = new AudioOutput();
        audio.Start(2, Rate);
        short[] pcm = new short[Chunk * 2];
        double phase = 0, step = 2 * Math.PI * 440 / Rate;
        long written = 0;
        var clock = Stopwatch.StartNew();
        while (written < Rate)
        {
            long due = (clock.ElapsedMilliseconds + LeadMs) * Rate / 1000;
            if (written >= due) { Thread.Sleep(5); continue; }
            for (int i = 0; i < Chunk; i++)
            {
                short s = (short)(Math.Sin(phase) * 8000);
                pcm[2 * i] = pcm[2 * i + 1] = s;
                phase += step;
            }
            audio.Write(pcm);
            written += Chunk;
        }
        Thread.Sleep(LeadMs + 100); // drain the queue and the device buffer
        audio.Stop();
        Log.Info("Audio self-test: done");
    }
}
