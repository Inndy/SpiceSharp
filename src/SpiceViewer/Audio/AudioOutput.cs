using System.Diagnostics;
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
/// thread feeds a ~200 ms ring buffer through <see cref="Write"/>; the render thread
/// drains it on each WASAPI buffer event, writing silence on underflow.
/// </para>
/// All public members are thread-safe.
/// </summary>
sealed unsafe class AudioOutput : IDisposable
{
    const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    const int AUDCLNT_E_RESOURCES_INVALIDATED = unchecked((int)0x88890026);
    const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    const int ErrorNoEvents = unchecked((int)0x800705B4); // HRESULT_FROM_WIN32(ERROR_TIMEOUT)
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 2;

    const long DeviceBufferHns = 400_000; // 40 ms endpoint buffer (100 ns units)
    const int RingMs = 200;               // ring capacity; oldest samples dropped beyond this
    const int PrimeMs = 20;               // after an underrun, wait for this much before resuming
    const int TrimAboveMs = 100;          // queued latency above this is trimmed ...
    const int TrimToMs = 40;              // ... down to this
    const int EventTimeoutMs = 2000;
    const int MaxReopens = 3;

    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    enum Exit { Stop, DefaultChanged, Failed }

    readonly Lock lifecycle = new();
    readonly ManualResetEvent stopEvent = new(false);
    readonly AutoResetEvent bufferEvent = new(false), deviceChanged = new(false);
    Thread? thread;
    bool disposed;

    // Ring state, guarded by ringLock. Lengths and offsets are always whole frames.
    readonly Lock ringLock = new();
    short[] ring = [];
    int head, count, channels, sampleRate, primeSamples, trimAbove, trimTo;
    bool active, primed;
    long droppedSamples, underruns;

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
            StopCore();
            lock (ringLock)
            {
                int length = sampleRate * RingMs / 1000 * channels;
                if (ring.Length != length) ring = new short[length];
                this.channels = channels;
                this.sampleRate = sampleRate;
                primeSamples = sampleRate * PrimeMs / 1000 * channels;
                trimAbove = sampleRate * TrimAboveMs / 1000 * channels;
                trimTo = sampleRate * TrimToMs / 1000 * channels;
                head = count = 0;
                droppedSamples = underruns = 0;
                primed = false;
                active = true;
            }
            stopEvent.Reset();
            deviceChanged.Reset();
            bufferEvent.Reset();
            thread = new Thread(RenderThread) { IsBackground = true, Name = "SpiceViewer audio", Priority = ThreadPriority.Highest };
            thread.Start();
        }
    }

    /// <summary>Queues interleaved PCM. Never blocks beyond a short copy; drops the oldest samples on overflow.</summary>
    public void Write(ReadOnlySpan<short> interleavedPcm)
    {
        lock (ringLock)
        {
            if (!active || interleavedPcm.IsEmpty) return;
            short[] r = ring;
            int n = interleavedPcm.Length - interleavedPcm.Length % channels;
            if (n > r.Length)
            {
                droppedSamples += n - r.Length;
                interleavedPcm = interleavedPcm.Slice(n - r.Length, r.Length);
                n = r.Length;
            }
            else interleavedPcm = interleavedPcm[..n];

            int overflow = count + n - r.Length;
            if (overflow > 0)
            {
                head = Wrap(head + overflow);
                count -= overflow;
                droppedSamples += overflow;
            }
            int tail = Wrap(head + count);
            int first = Math.Min(n, r.Length - tail);
            interleavedPcm[..first].CopyTo(r.AsSpan(tail));
            interleavedPcm[first..].CopyTo(r);
            count += n;
        }
    }

    /// <summary>Closes the stream and ends the render thread.</summary>
    public void Stop()
    {
        lock (lifecycle) { if (!disposed) StopCore(); }
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
    }

    void StopCore()
    {
        lock (ringLock) active = false;
        if (thread is null) return;
        stopEvent.Set();
        thread.Join();
        thread = null;
    }

    int Wrap(int index) => index >= ring.Length ? index - ring.Length : index;

    // ---- render thread -------------------------------------------------------------------

    void RenderThread()
    {
        int ch = channels, rate = sampleRate; // fixed for this thread; Start joins before changing them
        HRESULT com = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        if (com.Value < 0 && com.Value != RPC_E_CHANGED_MODE)
        {
            Log.Error($"Audio: CoInitializeEx failed (0x{com.Value:X8})");
            Finish();
            return;
        }
        IMMDeviceEnumerator* enumerator = null;
        DeviceNotifications.Native* notifications = null;
        bool registered = false;
        try
        {
            Guid clsid = CLSID_MMDeviceEnumerator, iid = IMMDeviceEnumerator.IID_Guid;
            PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&enumerator).ThrowOnFailure();

            notifications = DeviceNotifications.Create(deviceChanged);
            try
            {
                enumerator->RegisterEndpointNotificationCallback((IMMNotificationClient*)notifications);
                registered = true;
            }
            catch (Exception e) { Log.Debug($"Audio: device notifications unavailable (0x{e.HResult:X8})"); }

            WaitHandle[] waits = [stopEvent, deviceChanged, bufferEvent];
            int reopens = 0;
            while (true)
            {
                long opened = Environment.TickCount64;
                Exit exit = Play(enumerator, ch, rate, waits, out int error);
                if (exit == Exit.Stop || stopEvent.WaitOne(0)) break;
                bool recoverable = exit == Exit.DefaultChanged
                    || error is AUDCLNT_E_DEVICE_INVALIDATED or AUDCLNT_E_RESOURCES_INVALIDATED or ErrorNoEvents;
                if (Environment.TickCount64 - opened > 5000) reopens = 0;
                if (!recoverable || ++reopens > MaxReopens)
                {
                    Log.Warn("Audio: output closed; the next playback start reopens the default device");
                    break;
                }
                Log.Info(exit == Exit.DefaultChanged
                    ? "Audio: default output device changed; reopening"
                    : "Audio: output device lost; reopening on the default device");
                lock (ringLock) primed = false;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"Audio: cannot open output: {e.Message} (0x{e.HResult:X8})");
        }
        finally
        {
            if (registered) { try { enumerator->UnregisterEndpointNotificationCallback((IMMNotificationClient*)notifications); } catch { } }
            if (enumerator != null) enumerator->Release();
            if (notifications != null) DeviceNotifications.ReleaseOwner(notifications);
            if (com.Value >= 0) PInvoke.CoUninitialize();
            Finish();
        }
    }

    void Finish()
    {
        long dropped, under;
        lock (ringLock)
        {
            active = false;
            head = count = 0;
            dropped = droppedSamples;
            under = underruns;
        }
        if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"Audio: stream closed; underruns {under}, dropped samples {dropped}");
    }

    /// <summary>Opens a stream on the current default device and pumps it until stop, device change or failure.</summary>
    Exit Play(IMMDeviceEnumerator* enumerator, int ch, int rate, WaitHandle[] waits, out int error)
    {
        IMMDevice* device = null;
        IAudioClient* client = null;
        IAudioRenderClient* render = null;
        bool started = false;
        error = 0;
        try
        {
            enumerator->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, &device);
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
            Log.Info($"Audio: playing {ch} ch {rate} Hz s16, device buffer {bufferFrames * 1000 / (uint)rate} ms");

            Fill(client, render, bufferFrames, ch); // pre-roll: queued audio, or ~10 ms of silence
            client->Start();
            started = true;

            while (true)
            {
                int which = WaitHandle.WaitAny(waits, EventTimeoutMs);
                if (which == 0) return Exit.Stop;
                if (which == 1) return Exit.DefaultChanged;
                if (which == WaitHandle.WaitTimeout)
                {
                    Log.Warn("Audio: no buffer events from the device");
                    error = ErrorNoEvents;
                    return Exit.Failed;
                }
                Fill(client, render, bufferFrames, ch);
            }
        }
        catch (Exception e)
        {
            error = e.HResult;
            Log.Warn(error == AUDCLNT_E_DEVICE_INVALIDATED
                ? "Audio: output device was removed or reconfigured"
                : $"Audio: WASAPI error: {e.Message} (0x{error:X8})");
            return Exit.Failed;
        }
        finally
        {
            if (started) { try { client->Stop(); } catch { } }
            if (render != null) render->Release();
            if (client != null) client->Release();
            if (device != null) device->Release();
        }
    }

    /// <summary>
    /// Tops up the endpoint buffer with whatever the ring holds. The endpoint buffer itself absorbs
    /// network jitter: silence is written only when it is about to run dry (below ~10 ms queued).
    /// </summary>
    void Fill(IAudioClient* client, IAudioRenderClient* render, uint bufferFrames, int ch)
    {
        uint padding;
        client->GetCurrentPadding(&padding);
        if (padding >= bufferFrames) return;
        uint free = bufferFrames - padding;
        uint lowWater = bufferFrames / 4;
        uint real = Math.Min(free, (uint)ReadyFrames());
        uint frames = real;
        bool starving = padding + real < lowWater;
        if (starving) frames = Math.Min(free, lowWater - padding);
        if (frames == 0) return;

        byte* data;
        render->GetBuffer(frames, &data);
        var dst = new Span<short>(data, (int)frames * ch);
        int n = (int)real * ch;
        if (n > 0) Dequeue(dst[..n], starving);
        else if (starving) Dequeue([], true);
        uint flags = 0;
        if (n == 0 || muted) flags = AUDCLNT_BUFFERFLAGS_SILENT;
        else
        {
            if (n < dst.Length) dst[n..].Clear();
            int gain = gainQ16;
            if (gain < 65536) Scale(dst[..n], gain);
        }
        render->ReleaseBuffer(frames, flags);
    }

    /// <summary>Frames ready to play; 0 while (re)priming after an underrun. Trims excess latency.</summary>
    int ReadyFrames()
    {
        lock (ringLock)
        {
            if (!primed)
            {
                if (count < primeSamples) return 0;
                primed = true;
            }
            if (count > trimAbove)
            {
                // The queue grew (network burst after a stall); cut back so latency stays low.
                int drop = count - trimTo;
                head = Wrap(head + drop);
                count -= drop;
                droppedSamples += drop;
            }
            return count / channels;
        }
    }

    /// <summary>
    /// Removes dst.Length samples (never more than <see cref="ReadyFrames"/> reported; Write only
    /// grows the queue). On starvation, re-primes so playback resumes with a small cushion.
    /// </summary>
    void Dequeue(Span<short> dst, bool starving)
    {
        lock (ringLock)
        {
            int n = dst.Length;
            int first = Math.Min(n, ring.Length - head);
            ring.AsSpan(head, first).CopyTo(dst);
            ring.AsSpan(0, n - first).CopyTo(dst[first..]);
            head = Wrap(head + n);
            count -= n;
            if (starving && primed) { primed = false; underruns++; }
        }
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
