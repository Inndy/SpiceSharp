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
    /// <summary>
    /// (pcm, delayMs, gapMs): decoded interleaved s16 PCM, only valid during the call; when its first sample should be
    /// heard, in ms from now, or <see cref="PcmQueue.Unsynced"/> to play it as soon as possible; and how far (ms) its
    /// timestamp is past the end of the previous packet's, 0 when they are contiguous or unsynchronized. Only
    /// <see cref="VideoPacing.Clock"/> gives a delay: the packet's timestamp ahead of the guest clock, the clock its video
    /// frames wait for, so sound and video line up (docs/decisions/0030). Pass all three to <see cref="PcmQueue.Write"/>.
    /// </summary>
    public event Action<ReadOnlyMemory<short>, int, int>? AudioData;

    int _audioRate = 48000;
    /// <summary>
    /// Where the previous packet's timestamps end, kept exactly: whole ms plus a remainder in 1/rate ms (a packet of
    /// 256 frames at 44.1 kHz lasts 5.805 ms), so whole-ms stamps of contiguous audio never read as gaps (review C2).
    /// Valid once a packet was seen.
    /// </summary>
    uint _audioEndMs;
    int _audioEndRem;
    bool _audioStampValid;

    /// <summary>The last SPICE_MSG_PLAYBACK_LATENCY (ms), or 0 if none came. Informational: see <see cref="AudioData"/>.</summary>
    public uint PlaybackLatencyMs { get; private set; }

    /// <summary>
    /// The delay (ms from now) at which audio stamped <paramref name="mm"/> should be heard: its lead over the guest's
    /// multimedia clock in <see cref="VideoPacing.Clock"/> once the clock is seeded, else <see cref="PcmQueue.Unsynced"/>.
    /// Read per packet, so a pacing change applies from the next packet.
    /// </summary>
    internal int AudioDelayMs(uint mm)
    {
        // _videoPacing is written under _streamLock; a plain read of the enum sees the old or the new mode.
        if (_videoPacing != VideoPacing.Clock || !MmSeeded) return PcmQueue.Unsynced;
        return unchecked((int)(mm - MultimediaTime));
    }
    /// <summary>Playback stopped: the app closes its audio stream so nothing idles.</summary>
    public event Action? AudioStopped;
    /// <summary>Per-channel volume (0..65535) and mute.</summary>
    public event Action<ushort[]>? AudioVolume;
    public event Action<bool>? AudioMute;

    internal void OnPlaybackMessage(ushort type, ReadOnlyMemory<byte> m)
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
                _audioRate = (int)s.Frequency;
                _audioStampValid = false;
                AudioStarted?.Invoke((int)s.Channels, (int)s.Frequency);
                RaiseStatusChanged();
                break;
            }
            case PlaybackMsg.Data:
            {
                if (_audio is null) break;
                uint mm = r.U32();
                var payload = r.Rest().Span;
                if (_pcm.Length < payload.Length) _pcm = new short[payload.Length];
                int samples = _audio.Decode(payload, _pcm);
                if (samples <= 0) break;
                int delay = AudioDelayMs(mm);
                // A gap in the stamps belongs between this packet and the audio already held; any other schedule
                // error (a new clock base, drift, entering clock pacing) retimes the held audio too (review A2).
                int gap = 0;
                if (_audioStampValid)
                {
                    // Stamps are whole ms: the part of the difference under 1 ms is their rounding, not a gap.
                    long diff = (long)unchecked((int)(mm - _audioEndMs)) * _audioRate - _audioEndRem;
                    int whole = (int)(diff / _audioRate); // toward zero
                    _audioEndMs = unchecked(_audioEndMs + (uint)whole); // follow the stamps, keeping the sub-ms phase
                    if (delay != PcmQueue.Unsynced) gap = whole;
                }
                else
                {
                    _audioEndMs = mm;
                    _audioEndRem = 0;
                    _audioStampValid = true;
                }
                long end = _audioEndRem + (long)samples * 1000;
                _audioEndMs = unchecked(_audioEndMs + (uint)(end / _audioRate));
                _audioEndRem = (int)(end % _audioRate);
                AudioData?.Invoke(_pcm.AsMemory(0, samples * _audioChannels), delay, gap);
                break;
            }
            case PlaybackMsg.Latency:
            {
                uint latency = r.U32();
                if (latency == PlaybackLatencyMs) break;
                Log.Info($"playback: server latency {latency} ms");
                PlaybackLatencyMs = latency;
                RaiseStatusChanged();
                break;
            }
            case PlaybackMsg.Stop:
                _audio?.Dispose();
                _audio = null;
                AudioStopped?.Invoke();
                RaiseStatusChanged();
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
