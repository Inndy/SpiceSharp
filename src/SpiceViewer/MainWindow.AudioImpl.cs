using Spice.Session;
using Spice.Wire;

namespace SpiceViewer;

sealed partial class MainWindow
{
    AudioOutput? _audioOutput;
    static int s_audioSelfTestStarted;

    AudioOutput AudioOut => LazyInitializer.EnsureInitialized(ref _audioOutput);

    /// <summary>
    /// Playback events arrive on the session's network thread; <see cref="AudioOutput"/> is
    /// thread-safe and its Write only copies into a ring buffer.
    /// </summary>
    partial void AttachAudioCore(SpiceSession s)
    {
        var audio = AudioOut;
        audio.Stop(); // a previous session may have died mid-playback
        // A playback handler already running when the session ends can still raise AudioStarted after Disconnected
        // (see SpiceSession.Disconnected): never leave a stream open for an ended session.
        bool ended = false;
        s.AudioStarted += (channels, rate) =>
        {
            if (Volatile.Read(ref ended)) return;
            audio.Start(channels, rate);
            if (Volatile.Read(ref ended)) audio.Stop();
        };
        s.AudioData += (pcm, delayMs, gapMs) => audio.Write(pcm.Span, delayMs, gapMs);
        s.AudioStopped += audio.EndOfStream; // clock pacing: held audio plays out first (docs/decisions/0030)
        s.AudioVolume += OnAudioVolume;
        s.AudioMute += audio.SetMute;
        s.Disconnected += _ => { Volatile.Write(ref ended, true); audio.Stop(); };

        if (Environment.GetEnvironmentVariable("SPICEVIEWER_AUDIO_TEST") == "1"
            && Interlocked.Exchange(ref s_audioSelfTestStarted, 1) == 0)
        {
            new Thread(() =>
            {
                try { AudioOutput.SelfTest(); }
                catch (Exception e) { Log.Error($"Audio self-test failed: {e.Message}"); }
            }) { IsBackground = true, Name = "SpiceViewer audio self-test" }.Start();
        }
    }

    void OnAudioVolume(ushort[] perChannel)
    {
        int max = 0;
        foreach (ushort v in perChannel) if (v > max) max = v;
        _audioOutput?.SetVolume(max / 65535f);
    }

    /// <summary>Closes the audio stream and its thread. Optional on exit; safe to call more than once.</summary>
    void DisposeAudioOutput()
    {
        Interlocked.Exchange(ref _audioOutput, null)?.Dispose();
    }
}
