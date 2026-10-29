using Spice.Media;
using Spice.Wire;

namespace Spice.Session;

public sealed partial class SpiceSession
{
    ushort _audioMode = AudioDataMode.Raw;
    AudioDecoder? _audio;
    int _audioChannels = 2;
    short[] _pcm = new short[2 * 5760];

    /// <summary>Playback began: (channels, sample rate). The app opens its audio stream now.</summary>
    public event Action<int, int>? AudioStarted;
    /// <summary>Decoded interleaved s16 PCM; only valid during the call.</summary>
    public event Action<ReadOnlyMemory<short>>? AudioData;
    /// <summary>Playback stopped: the app closes its audio stream so nothing idles.</summary>
    public event Action? AudioStopped;
    /// <summary>Per-channel volume (0..65535) and mute.</summary>
    public event Action<ushort[]>? AudioVolume;
    public event Action<bool>? AudioMute;

    void OnPlaybackMessage(ushort type, ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        switch (type)
        {
            case PlaybackMsg.Mode:
                r.U32();
                _audioMode = r.U16();
                break;
            case PlaybackMsg.Start:
            {
                var s = PlaybackStart.Parse(m);
                _audio?.Dispose();
                _audio = null;
                try { _audio = new AudioDecoder(_audioMode, (int)s.Channels, (int)s.Frequency); }
                catch (ArgumentException e) { Log.Warn($"playback: {e.Message}"); break; }
                _audioChannels = (int)s.Channels;
                AudioStarted?.Invoke((int)s.Channels, (int)s.Frequency);
                break;
            }
            case PlaybackMsg.Data:
            {
                if (_audio is null) break;
                r.U32(); // mm time
                var payload = r.Rest().Span;
                if (_pcm.Length < payload.Length) _pcm = new short[payload.Length];
                int samples = _audio.Decode(payload, _pcm);
                if (samples > 0) AudioData?.Invoke(_pcm.AsMemory(0, samples * _audioChannels));
                break;
            }
            case PlaybackMsg.Stop:
                _audio?.Dispose();
                _audio = null;
                AudioStopped?.Invoke();
                break;
            case PlaybackMsg.Volume:
            {
                int n = r.U8();
                var v = new ushort[n];
                for (int i = 0; i < n; i++) v[i] = r.U16();
                AudioVolume?.Invoke(v);
                break;
            }
            case PlaybackMsg.Mute:
                AudioMute?.Invoke(r.U8() != 0);
                break;
        }
    }

    /// <summary>
    /// The playback channel ended on its own while the session goes on: close the audio stream as a STOP would,
    /// so it doesn't keep waking the CPU. Runs after the channel's loop, so no playback handler is running.
    /// </summary>
    void OnPlaybackClosed()
    {
        // A session that is ending cleans up in DisposeMedia and raises nothing more.
        if (_audio is null || _cts.IsCancellationRequested || Volatile.Read(ref _disconnected) != 0) return;
        DisposeAudio();
        AudioStopped?.Invoke();
    }

    void DisposeAudio()
    {
        _audio?.Dispose();
        _audio = null;
    }
}
