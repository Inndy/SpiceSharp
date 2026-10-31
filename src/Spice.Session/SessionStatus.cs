using Spice.Wire;

namespace Spice.Session;

/// <summary>How a video stream's frames are decoded (for the status overlay).</summary>
public enum VideoDecodePath : byte
{
    /// <summary>No decoder: the stream was reported unsupported and the server sends its area as images.</summary>
    None,
    /// <summary>MJPEG, decoded by the session with libjpeg-turbo.</summary>
    Mjpeg,
    /// <summary>A GPU video decoder (Media Foundation).</summary>
    Hardware,
    /// <summary>libavcodec on the CPU.</summary>
    Software,
}

/// <summary>One video stream as the status overlay shows it. Counters are lifetime totals of the stream.</summary>
public readonly record struct VideoStreamStatus(uint Id, VideoCodecType Codec, VideoDecodePath Path, int Width, int Height,
    long Frames, long Drops, long Hidden, long Shown);

/// <summary>Playback as the status overlay shows it.</summary>
public readonly record struct AudioStatus(bool Playing, ushort Mode, int Channels, int Rate, uint LatencyMs);

/// <summary>
/// Read-only diagnostics for the viewer's status overlay (docs/decisions/0044). Every member is cheap, allocation-free
/// and safe to call from any thread; none takes a lock for long or wakes anything.
/// </summary>
public sealed partial class SpiceSession
{
    /// <summary>
    /// A value the status overlay shows changed without a frame to show it with: a video stream started or ended or
    /// changed decoder, playback started or stopped or its latency changed, memory was trimmed. Raised on the thread that
    /// made the change, sometimes under a session lock: handlers only set a flag.
    /// </summary>
    public event Action? StatusChanged;

    void RaiseStatusChanged() => StatusChanged?.Invoke();

    /// <summary>Channels linked so far (main included).</summary>
    public int LinkedChannelCount
    {
        get { lock (_channels) return _channels.Count; }
    }

    /// <summary>
    /// Totals received on every channel since it linked: bytes, messages, and the display channel's bytes. Counters only
    /// grow; the overlay turns them into rates.
    /// </summary>
    public void ReadTraffic(out long bytes, out long messages, out long displayBytes)
    {
        bytes = messages = displayBytes = 0;
        lock (_channels)
        {
            foreach (var ch in _channels)
            {
                long b = ch.BytesReceived;
                bytes += b;
                messages += ch.MessagesReceived;
                if (ch.Id.Type == ChannelType.Display) displayBytes += b;
            }
        }
    }

    /// <summary>
    /// Copies up to <paramref name="into"/>.Length live video streams (lowest ids first is not guaranteed) and returns how
    /// many there are in all, or -1 when the stream state is busy (a frame is being decoded): the caller keeps what it
    /// showed before rather than wait for the decoder.
    /// </summary>
    public int ReadVideoStreams(Span<VideoStreamStatus> into)
    {
        if (!_streamLock.TryEnter()) return -1;
        try
        {
            int n = 0;
            foreach (var s in _streams.Values)
            {
                if (n < into.Length)
                {
                    var path = s.Unsupported ? VideoDecodePath.None
                        : s.Decoder is { } d ? (d.Software ? VideoDecodePath.Software : VideoDecodePath.Hardware)
                        : s.Codec == VideoCodecType.Mjpeg ? VideoDecodePath.Mjpeg : VideoDecodePath.None;
                    int w = s.Frame?.Width ?? s.StreamWidth, h = s.Frame?.Height ?? s.StreamHeight;
                    into[n] = new VideoStreamStatus(s.Id, s.Codec, path, w, h, (long)s.EarlierFrames + s.Frames,
                        (long)s.EarlierDrops + s.Drops, s.Hidden, s.Shown);
                }
                n++;
            }
            return n;
        }
        finally { _streamLock.Exit(); }
    }

    /// <summary>Whether playback is running, its codec (<see cref="AudioDataMode"/>), format and the server's latency.</summary>
    public AudioStatus Audio => new(Volatile.Read(ref _audio) is not null, _audioMode, _audioChannels, _audioRate, PlaybackLatencyMs);
}
