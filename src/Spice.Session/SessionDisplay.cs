using System.Numerics;
using Spice.Canvas;
using Spice.Native;
using Spice.Transport;
using Spice.Wire;

namespace Spice.Session;

public sealed partial class SpiceSession
{
    /// <summary>A received frame waiting for its paced deadline: the compressed bytes, copied into a pooled buffer.</summary>
    struct QueuedFrame
    {
        public byte[] Data;
        public int Length;
        public SpiceRect Dest;
        public long Deadline;
        public uint Stamp;
        public uint Window; // the stream's report window when the frame arrived
    }

    sealed class VideoStream
    {
        public required uint Id;
        public required uint SurfaceId;
        public required VideoCodecType Codec;
        public required bool TopDown;
        public required SpiceRect Dest;
        public SpiceClip Clip;
        public DecodedImage? Frame;
        /// <summary>The stream's own decoder (H.264, VP9, HEVC); null for MJPEG, which shares <see cref="_mjpeg"/>.</summary>
        public IVideoDecoder? Decoder;
        /// <summary>
        /// Reported to the server as unsupported (no decoder, or it failed): the server sends the area as images, and any
        /// frame still arriving is ignored (docs/decisions/0037).
        /// </summary>
        public bool Unsupported;
        /// <summary>The decoder has made a picture; its frames before that are checked for their declared format.</summary>
        public bool Pictured;
        /// <summary>Frames left to look for the declared format in (<see cref="CheckVideoFormat"/>); 0 once known.</summary>
        public int FormatChecks = MaxFormatChecks;
        /// <summary>The format the stream's frames declare, once read.</summary>
        public VideoFormat? Format;
        /// <summary>STREAM_CREATE's encoded size (0 when implausible), the size hint for a replacement decoder.</summary>
        public int StreamWidth, StreamHeight;
        // stream report bookkeeping: Frames and Drops count the frames that arrived in the current report window and
        // those of them dropped, whenever the drop happens (a queued frame can be dropped after its window was sent)
        public uint UniqueId, MaxWindow, Frames, Drops, StartMm, LastMm, Window;
        // counts from earlier report windows, for the lifetime totals in the log
        public uint EarlierFrames, EarlierDrops;
        // frames not decoded because the stream's clip hid all of them when they came due; for the log only, the
        // report counts them as shown (as spice-gtk does: they say nothing about keeping up)
        public uint Hidden;
        public bool Reporting;
        // pacing: frames waiting for their deadline, oldest first, in a fixed ring (deadlines never decrease along it)
        public readonly QueuedFrame[] Queue = new QueuedFrame[MaxQueuedFrames];
        public int QueueHead, QueueCount;
        // the previous accepted frame's timestamp and deadline, once there is one
        public bool Paced;
        public uint LastStamp;
        public long LastDeadline;
    }

    readonly Dictionary<uint, VideoStream> _streams = [];
    MjpegVideoDecoder? _mjpeg;
    VideoOutput? _videoOutput;
    /// <summary>The codecs besides MJPEG advertised on the display channel, from <see cref="VideoDecoders"/>.</summary>
    VideoCodecType[] _videoCodecs = [];

    /// <summary>Concurrent video streams kept (spice-server uses at most 50).</summary>
    const int MaxStreams = 64;
    /// <summary>Total decoded-frame bytes held across streams.</summary>
    const long MaxStreamFrameBytes = 256L << 20;

    // ---------------- stream pacing (docs/decisions/0025) ----------------

    /// <summary>A frame this far (ms) behind the guest's multimedia clock on arrival is dropped before decode (SpiceSee).</summary>
    const int LateMs = 80;
    /// <summary>Longest (ms) a frame is held after it arrives, to restore its spacing on the guest timeline.</summary>
    const int MaxLeadMs = 50;
    /// <summary>Per frame, the schedule moves this much (ms) toward arrival time, so delay added by a burst drains.</summary>
    const int CatchUpMs = 2;
    /// <summary>A frame due within this many ms is drawn now: shorter waits aren't worth a wake.</summary>
    const int PaceSlackMs = 4;
    /// <summary>A timestamp step backwards or beyond this (ms) starts a new timeline.</summary>
    const int MaxStampStepMs = 1000;
    /// <summary>Clock mode: longest (ms) a frame waits for the guest clock, against a clock far off the stamps.</summary>
    const int ClockMaxWaitMs = 2000;
    /// <summary>Frames queued per stream (clock mode holds the server's ~400 ms lead); when full, the oldest is dropped.</summary>
    const int MaxQueuedFrames = 32;
    /// <summary>Frame buffer bytes (their full capacity) held by the queues across all streams.</summary>
    const long MaxQueuedBytes = 64L << 20;
    /// <summary>Spare frame buffers kept for reuse while a stream exists.</summary>
    const int MaxPooledBuffers = MaxQueuedFrames;
    /// <summary>Spare frame buffer bytes kept for reuse.</summary>
    const long MaxPooledBytes = 64L << 20;

    /// <summary>
    /// Guards the stream state (<see cref="_streams"/>, queues, the frame pool, the decoder and the wake), which the
    /// display thread and the pacing wake both use. Taken before <see cref="Spice.Canvas.Canvas.Sync"/>, never after.
    /// </summary>
    readonly Lock _streamLock = new();
    PacingClock _pacing = new SystemPacingClock();
    Action? _pacingWake;
    bool _wakeArmed, _pacingStopped;
    long _wakeAt, _queuedBytes, _pooledBytes;
    readonly byte[]?[] _framePool = new byte[]?[MaxPooledBuffers];
    int _framePoolCount;
    VideoPacing _videoPacing = VideoPacing.Off;

    /// <summary>
    /// How video-stream frames are timed (default <see cref="VideoPacing.Off"/>). May change at any time, from any
    /// thread: frames already queued are rescheduled under the new mode at once (under <see cref="VideoPacing.Off"/>
    /// they are all due, so the newest is drawn and the older ones are dropped).
    /// </summary>
    public VideoPacing VideoPacing
    {
        get => _videoPacing;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_streamLock)
            {
                if (value == _videoPacing) return;
                _videoPacing = value;
                if (!_pacingStopped) RescheduleQueued(_pacing.NowMs);
            }
        }
    }

    volatile bool _fastJpeg;

    /// <summary>
    /// Fast JPEG decoding (fast IDCT and chroma upsampling, slightly lower quality) for MJPEG stream frames and JPEG and
    /// JPEG_ALPHA images. May change at any time, from any thread; the next decode of each kind uses it.
    /// </summary>
    public bool FastJpeg
    {
        get => _fastJpeg;
        set { _fastJpeg = value; Canvas.FastJpeg = value; }
    }

    int _preferredCompression;

    /// <summary>
    /// The image compression asked of the server: <see cref="ImageCompression.Invalid"/> (default) asks for nothing and
    /// leaves the choice to the server. Any other value is sent as DISPLAY_PREFERRED_COMPRESSION once the display
    /// channel is up, and again on each change, if the server has SPICE_DISPLAY_CAP_PREF_COMPRESSION. The protocol can't
    /// return to the server's own setting: going back to Invalid takes effect on the next connection.
    /// </summary>
    public ImageCompression PreferredCompression
    {
        get => (ImageCompression)Volatile.Read(ref _preferredCompression);
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (Interlocked.Exchange(ref _preferredCompression, (int)value) == (int)value) return;
            if (Volatile.Read(ref _display) is not { } display) return;
            if (value == ImageCompression.Invalid) Log.Info("display: the server keeps the requested image compression until the next connection");
            else SendPreferredCompression(display, value);
        }
    }

    /// <summary>Sends DISPLAY_PREFERRED_COMPRESSION when there is a preference the server and this client can use.</summary>
    internal static bool SendPreferredCompression(SpiceChannel display, ImageCompression compression)
    {
        if (compression == ImageCompression.Invalid) return false;
        string name = compression.ToString().ToUpperInvariant();
        if (!display.ServerChannelCaps.Has(DisplayCap.PrefCompression))
        {
            Log.Info($"display: the server takes no preferred image compression; {name} not requested");
            return false;
        }
        if (compression == ImageCompression.Lz4 && !Lz4Decoder.IsAvailable)
        {
            Log.Warn("display: spicecodec.dll has no LZ4 decoder; LZ4 not requested");
            return false;
        }
        Span<byte> b = stackalloc byte[1];
        display.Send(DisplayMsg.CPreferredCompression, b[..ClientMsg.PreferredCompression(b, compression)]);
        Log.Info($"display: asked the server to prefer {name} image compression");
        return true;
    }

    VideoCodecType[]? _preferredVideoCodecs;

    /// <summary>
    /// The video codecs asked of the server, most preferred first (docs/decisions/0037); null (default) asks for nothing
    /// and leaves the server's order. Sent as DISPLAY_PREFERRED_VIDEO_CODEC_TYPE once the display channel is up, and again
    /// on each change, if the server has SPICE_DISPLAY_CAP_PREF_VIDEO_CODEC_TYPE. Only codecs this session advertises are
    /// sent; when none is left nothing is sent. spice-server sorts its own codec list by it, codecs not named keeping
    /// their order after the named ones, and stops the running streams so new ones use it. The protocol can't return to
    /// the server's order: going back to null takes effect on the next connection.
    /// </summary>
    public IReadOnlyList<VideoCodecType>? PreferredVideoCodecs
    {
        get => Volatile.Read(ref _preferredVideoCodecs);
        set
        {
            VideoCodecType[]? list = null;
            if (value is not null)
            {
                foreach (var c in value) if (ClientMsg.CodecCap(c) < 0) throw new ArgumentOutOfRangeException(nameof(value));
                list = [.. value];
            }
            lock (_preferenceLock)
            {
                var old = Interlocked.Exchange(ref _preferredVideoCodecs, list);
                if (old is null ? list is null : list is not null && old.AsSpan().SequenceEqual(list)) return;
                if (Volatile.Read(ref _display) is not { } display) return;
                if (list is null) Log.Info("display: the server keeps the requested video codec order until the next connection");
                else SendPreferredVideoCodecs(display, list);
            }
        }
    }

    /// <summary>
    /// Serializes a preference's filtering and sending with avoidance (<see cref="AvoidVideoCodec"/>), so the last
    /// preference sent never names a codec avoided before it. Taken after _streamLock when both are held, never before.
    /// </summary>
    readonly Lock _preferenceLock = new();

    /// <summary>
    /// Sends DISPLAY_PREFERRED_VIDEO_CODEC_TYPE with the codecs of <paramref name="preferred"/> that this session
    /// advertises (MJPEG and the decodable ones), in order. False, logged, when the server takes no preference or no
    /// codec is left.
    /// </summary>
    internal bool SendPreferredVideoCodecs(SpiceChannel display, IReadOnlyList<VideoCodecType> preferred)
    {
        lock (_preferenceLock) return SendPreferredVideoCodecsLocked(display, preferred);
    }

    bool SendPreferredVideoCodecsLocked(SpiceChannel display, IReadOnlyList<VideoCodecType> preferred)
    {
        var advertised = Volatile.Read(ref _videoCodecs);
        Span<VideoCodecType> codecs = stackalloc VideoCodecType[8];
        int n = 0;
        foreach (var c in preferred)
        {
            if (c != VideoCodecType.Mjpeg && (Array.IndexOf(advertised, c) < 0 || IsAvoided(c))) continue;
            if (codecs[..n].Contains(c) || n == codecs.Length) continue;
            codecs[n++] = c;
        }
        // An avoided codec is still advertised; the server ranks codecs not named after the named ones, so naming MJPEG
        // keeps it ahead of them.
        if (Volatile.Read(ref _avoidedCodecs) != 0 && !codecs[..n].Contains(VideoCodecType.Mjpeg) && n < codecs.Length)
            codecs[n++] = VideoCodecType.Mjpeg;
        if (n == 0)
        {
            Log.Info($"display: none of the preferred video codecs ({string.Join(", ", preferred)}) can be decoded here; the server's order stands");
            return false;
        }
        if (!display.ServerChannelCaps.Has(DisplayCap.PrefVideoCodecType))
        {
            Log.Info("display: the server takes no preferred video codec; its own order stands");
            return false;
        }
        Span<byte> b = stackalloc byte[1 + 8];
        display.Send(DisplayMsg.CPreferredVideoCodecType, b[..ClientMsg.PreferredVideoCodecType(b, codecs[..n])]);
        Log.Info($"display: asked the server to prefer video codecs {string.Join(", ", codecs[..n].ToArray())}");
        return true;
    }

    /// <summary>The pacing clock. Tests replace it before the first stream message to replay deterministically.</summary>
    internal PacingClock PacingClock
    {
        get => _pacing;
        set
        {
            lock (_streamLock)
            {
                _pacing.Dispose();
                _pacing = value;
                _wakeArmed = false;
                if (_pacingWake is not null) value.Wake = _pacingWake;
            }
        }
    }

    internal void OnDisplayMessage(ushort type, ReadOnlyMemory<byte> m)
    {
        // Bad stream data drops that message only, as Canvas.Apply does for draw commands; anything else
        // escaping here would end the display loop and the session.
        try { OnDisplayMessageCore(type, m); }
        catch (Exception e) when (e is CanvasException or WireException)
        {
            Log.Warn($"display: message {type}: {e.Message}");
        }
    }

    void OnDisplayMessageCore(ushort type, ReadOnlyMemory<byte> m)
    {
        switch (type)
        {
            case DisplayMsg.StreamCreate:
            {
                var sc = StreamCreate.Parse(m);
                // Made outside the stream lock: a hardware decoder takes a few milliseconds to set up, and a paced
                // frame of another stream may be due meanwhile.
                var decoder = sc.Codec == VideoCodecType.Mjpeg ? null : CreateDecoder(sc);
                bool unsupported = sc.Codec != VideoCodecType.Mjpeg && decoder is null;
                if (unsupported) Log.Warn($"display: stream {sc.Id} uses {sc.Codec}, which can't be decoded here");
                else Log.Info($"display: stream {sc.Id} {sc.Codec} {sc.StreamWidth}x{sc.StreamHeight} to {sc.Dest.Width}x{sc.Dest.Height} on surface {sc.SurfaceId}");
                lock (_streamLock)
                {
                    // Avoidance happens under this lock; the codec may have been avoided while the decoder was made.
                    if (decoder is not null && IsAvoided(sc.Codec))
                    {
                        DisposeDecoderQuietly(sc.Id, decoder);
                        decoder = null;
                        unsupported = true;
                        Log.Info($"display: stream {sc.Id}: {sc.Codec} was avoided while its decoder was made");
                    }
                    try
                    {
                        if (_streams.Remove(sc.Id, out var old)) EndStream(old);
                        if (_streams.Count >= MaxStreams) throw new CanvasException($"too many video streams ({_streams.Count})");
                        // SpiceClip slices the message buffer, which is reused: copy the rects now.
                        _streams[sc.Id] = new VideoStream
                        {
                            Id = sc.Id, SurfaceId = sc.SurfaceId, Codec = sc.Codec, TopDown = (sc.Flags & StreamCreate.FlagTopDown) != 0,
                            Dest = sc.Dest, Clip = CopyClip(sc.Clip), Decoder = decoder, Unsupported = unsupported,
                            StreamWidth = HintSide(sc.StreamWidth), StreamHeight = HintSide(sc.StreamHeight),
                        };
                    }
                    catch { decoder?.Dispose(); throw; }
                    // As spice-gtk does when it has no decoder: the server stops encoding and sends the area as images.
                    if (unsupported) ReportUnsupported(sc.Id);
                }
                break;
            }
            case DisplayMsg.StreamData or DisplayMsg.StreamDataSized:
                OnStreamData(StreamData.Parse(m, type == DisplayMsg.StreamDataSized));
                break;
            case DisplayMsg.StreamClip:
            {
                var (id, clip) = DisplayParse.StreamClip(m);
                lock (_streamLock)
                    if (_streams.TryGetValue(id, out var s)) s.Clip = CopyClip(clip);
                break;
            }
            case DisplayMsg.StreamDestroy:
            {
                uint id = new SpiceReader(m).U32();
                bool lastEnded = false;
                lock (_streamLock)
                {
                    if (_streams.Remove(id, out var s))
                    {
                        // The server may draw over the stream area from the next message on, believing the last frame
                        // is on screen: draw the newest queued frame now, before those draws, and drop the older ones
                        // (counted). Drawn later on their schedule they would overwrite the server's newer drawing.
                        try { if (s.QueueCount > 0) DrawNewestQueued(s); }
                        finally
                        {
                            Log.Info($"display: stream {id} ended after {s.EarlierFrames + s.Frames} frames, " +
                                $"{s.EarlierDrops + s.Drops} dropped, {s.Hidden} hidden by its clip");
                            EndStream(s);
                            lastEnded = _streams.Count == 0;
                        }
                    }
                }
                // The video is over: what its frames and decoding left behind can go (rate-limited, decision 0041).
                if (lastEnded) TrimMemory("video ended");
                break;
            }
            case DisplayMsg.StreamDestroyAll:
            {
                bool hadStreams;
                lock (_streamLock)
                {
                    hadStreams = _streams.Count > 0;
                    foreach (var s in _streams.Values) { DiscardQueue(s); s.Frame?.Release(); s.Frame = null; DisposeDecoder(s); }
                    _streams.Clear();
                    ClearFramePool();
                    UpdateWake();
                }
                if (hadStreams) TrimMemory("video ended");
                break;
            }
            case DisplayMsg.StreamActivateReport:
            {
                var (sid, unique, window, _) = DisplayParse.StreamActivateReport(m);
                lock (_streamLock)
                {
                    if (_streams.TryGetValue(sid, out var s) && !s.Unsupported)
                    {
                        s.Reporting = true; s.UniqueId = unique; s.MaxWindow = window;
                        NextReportWindow(s);
                    }
                }
                break;
            }
            default:
                // Draw commands apply at once, even over a stream frame still waiting for its deadline; that frame is
                // drawn over them when it comes due (spice-gtk has the same ordering). See docs/decisions/0025.
                Canvas.Apply(type, m);
                if (type == DisplayMsg.Mark) OnDisplayMark();
                break;
        }
    }

    /// <summary>Under _streamLock: drops a removed stream's queued frames and decoded frame, and the wake if idle.</summary>
    void EndStream(VideoStream s)
    {
        DiscardQueue(s);
        s.Frame?.Release();
        s.Frame = null;
        DisposeDecoder(s);
        if (_streams.Count == 0) ClearFramePool();
        UpdateWake();
    }

    static void DisposeDecoder(VideoStream s)
    {
        var d = s.Decoder;
        s.Decoder = null;
        if (d is not null) DisposeDecoderQuietly(s.Id, d);
    }

    static void DisposeDecoderQuietly(uint id, IVideoDecoder d)
    {
        try { d.Dispose(); }
        catch (Exception e) { Log.Error($"display: stream {id} decoder: {e.Message}"); }
    }

    /// <summary>The encoded size is a hint for the decoder (the bitstream carries the real one); an implausible one is left out.</summary>
    static int HintSide(uint side) => side <= Surface.MaxSide ? (int)side : 0;

    /// <summary>
    /// A decoder for a new non-MJPEG stream, if its codec was advertised and the app's factory can make one now. When an
    /// earlier stream of the codec declared its format, the factory picks the path for that format (hardware, or
    /// software for what the hardware refuses), so the stream needn't change decoders on its first frame.
    /// </summary>
    IVideoDecoder? CreateDecoder(in StreamCreate sc)
    {
        if (VideoDecoders is not { } factory || Array.IndexOf(Volatile.Read(ref _videoCodecs), sc.Codec) < 0) return null;
        if (IsAvoided(sc.Codec)) return null; // logged when it was avoided
        int w = HintSide(sc.StreamWidth), h = HintSide(sc.StreamHeight);
        VideoFormat? known;
        bool softwareOnly;
        lock (_streamLock)
        {
            known = _knownFormats[(int)sc.Codec];
            softwareOnly = SoftwareOnly(sc.Codec);
        }
        try
        {
            if (known is not { } format) return factory.Create(sc.Codec, w, h);
            // The setting may have turned software decoding off since: then no path takes this server's format.
            if (!factory.CanDecode(format) || (softwareOnly && !factory.CanDecodeInSoftware(format)))
            {
                AvoidVideoCodec(sc.Codec, $"the server encodes it as {format}, which can't be decoded here now");
                return null;
            }
            return factory.Create(format, w, h, softwareOnly);
        }
        catch (Exception e)
        {
            Log.Error($"display: stream {sc.Id}: no {sc.Codec} decoder: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Tells the server the client can't show this stream: spice-gtk's report_invalid_stream (a STREAM_REPORT with
    /// num_frames 0 and num_drops UINT32_MAX). spice-server's dcc_handle_stream_report then drops the stream's encoder and
    /// sends what the stream would have carried as ordinary draw commands.
    /// </summary>
    void ReportUnsupported(uint id)
    {
        if (Volatile.Read(ref _display) is not { } display) return;
        if (!display.ServerChannelCaps.Has(DisplayCap.StreamReport))
        {
            Log.Warn($"display: stream {id} can't be shown and the server takes no stream reports");
            return;
        }
        Span<byte> b = stackalloc byte[32];
        display.Send(DisplayMsg.CStreamReport, b[..ClientMsg.StreamReport(b, id, 0, 0, 0, 0, uint.MaxValue, 0, 0)]);
        Log.Info($"display: asked the server to send stream {id} as images");
    }

    /// <summary>
    /// Under _streamLock: the stream's decoder failed. Its queue and picture go, the server is told (it then sends the
    /// area as images), and the frames still arriving for it are ignored.
    /// </summary>
    void FailStream(VideoStream s, ReadOnlySpan<byte> data)
    {
        // The format search may have ended before an SPS came (review H264-R2): the failing frame may declare it, which
        // decides whether software can take the codec's later streams. Read only on failure.
        if (s.Format is null && !s.Unsupported && VideoBitstream.TryReadFormat(s.Codec, data, out var declared))
        {
            s.Format = declared;
            _knownFormats[(int)s.Codec] = declared;
        }
        FailStream(s);
    }

    void FailStream(VideoStream s)
    {
        if (s.Unsupported) return;
        // A decoder that never made a picture likely can't take what this server sends (a profile the format check
        // doesn't know): the next streams of the codec would fail the same way.
        if (!s.Pictured && s.Decoder is { HasDecoded: false } failed && s.Codec != VideoCodecType.Mjpeg)
        {
            // A hardware decoder that fails where a software one would take the format: the codec's later streams
            // decode in software instead of avoiding the codec (docs/decisions/0037, amended 2026-10-31).
            if (!failed.Software && s.Format is { } f && VideoDecoders?.CanDecodeInSoftware(f) == true)
            {
                _softwareOnlyCodecs |= 1 << (int)s.Codec;
                Log.Warn($"display: stream {s.Id}: the hardware {s.Codec} decoder failed before its first picture; later {s.Codec} streams decode in software");
            }
            else AvoidVideoCodec(s.Codec, $"stream {s.Id} failed before its first picture");
        }
        s.Unsupported = true;
        s.Reporting = false;
        DiscardQueue(s);
        s.Frame?.Release();
        s.Frame = null;
        DisposeDecoder(s);
        ReportUnsupported(s.Id);
        UpdateWake();
    }

    /// <summary>Frames of a new stream searched for its declared format (an H.264 SPS comes with the first frame).</summary>
    const int MaxFormatChecks = 8;

    /// <summary>
    /// Under _streamLock: looks for the format a new stream's frame declares (<see cref="VideoBitstream"/>) and asks the
    /// factory whether its decoders take it. False, with the stream failed and its codec avoided, when they don't:
    /// spice-server's x264enc and vp9enc encode 4:4:4 by default, which no hardware decoder takes. Only the first frames
    /// are read; nothing is allocated unless the format is logged.
    /// </summary>
    bool CheckVideoFormat(VideoStream s, ReadOnlySpan<byte> data)
    {
        if (!VideoBitstream.TryReadFormat(s.Codec, data, out var format))
        {
            s.FormatChecks--;
            return true;
        }
        s.FormatChecks = 0;
        s.Format = format;
        _knownFormats[(int)s.Codec] = format;
        if (VideoDecoders is not { } factory || s.Decoder is not { } decoder) return true;
        if (factory.CanDecode(format))
        {
            bool softwareOnly = SoftwareOnly(s.Codec);
            // A software decoder made from an earlier stream's format (a hint) gives way to hardware when this stream's
            // format turns out to be one the hardware takes (review H264-R1).
            bool keep = decoder.Software ? softwareOnly || !factory.CanDecodeInHardware(format) : !softwareOnly;
            if (decoder.Takes(format) && keep)
            {
                NoteDecoder(s.Id, format, decoder);
                return true;
            }
            // The other path takes it (hardware for 4:2:0, software for 4:4:4): swap decoders before this frame decodes.
            IVideoDecoder? replacement = null;
            try { replacement = factory.Create(format, s.StreamWidth, s.StreamHeight, softwareOnly); }
            catch (Exception e) { Log.Error($"display: stream {s.Id}: no {s.Codec} decoder for {format}: {e.Message}"); }
            if (replacement is not null)
            {
                DisposeDecoderQuietly(s.Id, decoder);
                s.Decoder = replacement;
                NoteDecoder(s.Id, format, replacement);
                return true;
            }
        }
        Log.Warn($"display: stream {s.Id} is {format}, which can't be decoded here");
        AvoidVideoCodec(s.Codec, $"the server encodes it as {format}");
        FailStream(s);
        return false;
    }

    /// <summary>Logs which path decodes the stream and remembers it for About.</summary>
    void NoteDecoder(uint id, in VideoFormat format, IVideoDecoder decoder)
    {
        string path = decoder.Software ? "software" : "hardware";
        Log.Info($"display: stream {id} is {format}; decoding in {path}");
        Volatile.Write(ref _videoDecoding, $"{format}, {path}");
    }

    string? _videoDecoding;

    /// <summary>
    /// The newest non-MJPEG stream's declared format and the path decoding it ("..., hardware" or "..., software"), or
    /// null before one; for About.
    /// </summary>
    public string? VideoDecoding => Volatile.Read(ref _videoDecoding);

    /// <summary>Under _streamLock: the format each codec's last stream declared (indexed by VideoCodecType), for the next ones.</summary>
    readonly VideoFormat?[] _knownFormats = new VideoFormat?[8];
    /// <summary>Under _streamLock: bit per VideoCodecType whose hardware decoder failed before a picture; software only.</summary>
    int _softwareOnlyCodecs;

    bool SoftwareOnly(VideoCodecType codec) => (_softwareOnlyCodecs & 1 << (int)codec) != 0;

    int _avoidedCodecs; // bit per VideoCodecType

    bool IsAvoided(VideoCodecType codec) => (Volatile.Read(ref _avoidedCodecs) & 1 << (int)codec) != 0;

    /// <summary>
    /// Stops asking for <paramref name="codec"/> for the rest of the session: new streams of it are reported unsupported
    /// at creation, and the codec preference is sent again without it, MJPEG ahead of it (docs/decisions/0037). Its
    /// capability stays advertised; capabilities can't change during a session.
    /// </summary>
    void AvoidVideoCodec(VideoCodecType codec, string why)
    {
        int bit = 1 << (int)codec;
        lock (_preferenceLock)
        {
            int old = Volatile.Read(ref _avoidedCodecs);
            if ((old & bit) != 0) return;
            Volatile.Write(ref _avoidedCodecs, old | bit); // written only here, under the lock
            Log.Warn($"display: {codec} video is off for this session ({why}); asking the server for other codecs");
            if (Volatile.Read(ref _display) is { } display) SendPreferredVideoCodecsLocked(display, PreferredVideoCodecs ?? []);
        }
    }

    /// <summary>
    /// Under _streamLock: passes a frame that won't be shown to a decoder whose frames depend on earlier ones, so the
    /// frames after it still decode (docs/decisions/0037). Nothing for MJPEG.
    /// </summary>
    void SkipFrame(VideoStream s, ReadOnlySpan<byte> data)
    {
        if (s.Decoder is not { NeedsEveryFrame: true } decoder) return;
        VideoDecodeResult r;
        try { r = decoder.Decode(data, null); }
        catch (Exception e)
        {
            Log.Error($"display: stream {s.Id} decoder: {e.Message}");
            r = VideoDecodeResult.Failed;
        }
        if (r == VideoDecodeResult.Failed) FailStream(s, data);
    }

    /// <summary>
    /// Under _streamLock: a frame dropped on arrival (late, or too large to queue). For a decoder that needs every frame,
    /// the queued frames are older, so they go to it first, in order (dropped and counted), then this one.
    /// </summary>
    void SkipArrived(VideoStream s, ReadOnlySpan<byte> data)
    {
        if (s.Decoder is not { NeedsEveryFrame: true }) return;
        if (s.QueueCount > 0)
        {
            while (s.QueueCount > 0) DropHead(s);
            UpdateWake();
        }
        SkipFrame(s, data);
    }

    void OnStreamData(in StreamData d)
    {
        lock (_streamLock)
        {
            if (!_streams.TryGetValue(d.Id, out var s)) { Log.Debug($"display: data for unknown stream {d.Id}"); return; }
            if (s.Unsupported) return; // reported; the server stops sending frames for it
            if (s.FormatChecks > 0 && s.Decoder is not null && !CheckVideoFormat(s, d.Data.Span)) return;
            if (d.Dest is SpiceRect dest) s.Dest = dest;
            s.Frames++;
            if (s.Frames == 1) s.StartMm = d.MultiMediaTime;
            s.LastMm = d.MultiMediaTime;
            PaceFrame(s, d.MultiMediaTime, d.Data.Span);
            if (s.Reporting && s.Frames % Math.Max(1, s.MaxWindow) == 0) SendStreamReport(d.Id, s);
        }
    }

    /// <summary>
    /// Under _streamLock: draws a frame now, queues it for its deadline, or drops it (docs/decisions/0025). Spacing: the
    /// deadline keeps the frame's spacing from the previous one on the guest's multimedia timeline, held at most
    /// <see cref="MaxLeadMs"/> past arrival, and a frame more than <see cref="LateMs"/> behind the guest clock is dropped.
    /// Clock: the deadline is when the guest clock reaches the frame's timestamp, and a frame behind it is dropped.
    /// </summary>
    void PaceFrame(VideoStream s, uint stamp, ReadOnlySpan<byte> data)
    {
        var mode = _videoPacing;
        // Without the guest clock (MAIN_INIT seeds it) nothing is late or early, as in SpiceSee. After the session
        // has stopped, a handler already running draws as before and nothing is queued.
        if (mode == VideoPacing.Off || !MmSeeded || _pacingStopped) { DecodeStreamFrame(s, data, s.Dest, s.Window); return; }
        long now = _pacing.NowMs;
        int margin = unchecked((int)(stamp - MultimediaTimeAt(now))); // > 0: ahead of the guest clock
        if (mode == VideoPacing.Clock ? margin < 0 : margin < -LateMs) { s.Drops++; SkipArrived(s, data); return; }

        int step = unchecked((int)(stamp - s.LastStamp));
        bool sameTimeline = s.Paced && step >= 0 && step <= MaxStampStepMs;
        if (!sameTimeline && s.QueueCount > 0)
        {
            // A new timeline (the timestamps went backwards or jumped): what is queued belongs to the old one.
            while (s.QueueCount > 0) DropHead(s);
            UpdateWake();
            if (s.Unsupported) return; // the decoder failed on a dropped frame
        }
        long deadline = mode == VideoPacing.Clock ? now + Math.Min(margin, ClockMaxWaitMs)
            : sameTimeline ? Math.Clamp(s.LastDeadline + step - CatchUpMs, now, now + MaxLeadMs) : now;
        if (s.QueueCount > 0) deadline = Math.Max(deadline, s.Queue[(s.QueueHead + s.QueueCount - 1) % MaxQueuedFrames].Deadline);
        s.Paced = true; s.LastStamp = stamp; s.LastDeadline = deadline;

        if (s.QueueCount == 0 && deadline - now <= PaceSlackMs) { DecodeStreamFrame(s, data, s.Dest, s.Window); return; }
        Enqueue(s, data, stamp, deadline);
        PumpStreams(now);
    }

    /// <summary>
    /// Under _streamLock: recomputes every queued deadline for the current mode (after a mode change, or a new guest
    /// clock base in clock mode), keeping them non-decreasing along each queue, then draws what is due.
    /// </summary>
    void RescheduleQueued(long now)
    {
        bool seeded = MmSeeded;
        foreach (var s in _streams.Values)
        {
            long floor = now;
            for (int i = 0; i < s.QueueCount; i++)
            {
                ref var f = ref s.Queue[(s.QueueHead + i) % MaxQueuedFrames];
                long d = _videoPacing switch
                {
                    VideoPacing.Off => now,
                    VideoPacing.Clock => seeded ? now + Math.Min(unchecked((int)(f.Stamp - MultimediaTimeAt(now))), ClockMaxWaitMs) : now,
                    _ => Math.Clamp(f.Deadline, now, now + MaxLeadMs),
                };
                f.Deadline = floor = Math.Max(d, floor);
            }
            if (s.QueueCount > 0) s.LastDeadline = floor;
        }
        PumpStreams(now);
    }

    /// <summary>A new guest clock base: in clock mode the queued deadlines move with it.</summary>
    void OnGuestClockRebased()
    {
        lock (_streamLock)
            if (_videoPacing == VideoPacing.Clock && !_pacingStopped) RescheduleQueued(_pacing.NowMs);
    }

    /// <summary>Under _streamLock: copies the frame into a pooled buffer at the queue's tail, dropping the oldest to fit.</summary>
    void Enqueue(VideoStream s, ReadOnlySpan<byte> data, uint stamp, long deadline)
    {
        if (data.Length > MaxQueuedBytes) { s.Drops++; SkipArrived(s, data); return; }
        if (s.QueueCount == MaxQueuedFrames) DropHead(s);
        if (s.Unsupported) return; // the decoder failed on a frame dropped above
        var buffer = RentFrameBuffer(data.Length, MaxQueuedBytes - _queuedBytes);
        // The budget counts each buffer's capacity, so the queues never hold more than MaxQueuedBytes of memory.
        while (s.QueueCount > 0 && _queuedBytes + buffer.Length > MaxQueuedBytes) DropHead(s);
        if (s.Unsupported || _queuedBytes + buffer.Length > MaxQueuedBytes)
        {
            ReturnFrameBuffer(buffer);
            if (!s.Unsupported) { s.Drops++; SkipArrived(s, data); }
            return;
        }
        data.CopyTo(buffer);
        s.Queue[(s.QueueHead + s.QueueCount) % MaxQueuedFrames] =
            new QueuedFrame { Data = buffer, Length = data.Length, Dest = s.Dest, Deadline = deadline, Stamp = stamp, Window = s.Window };
        s.QueueCount++;
        _queuedBytes += buffer.Length;
    }

    QueuedFrame Dequeue(VideoStream s)
    {
        ref var slot = ref s.Queue[s.QueueHead];
        var f = slot;
        slot = default;
        s.QueueHead = (s.QueueHead + 1) % MaxQueuedFrames;
        s.QueueCount--;
        _queuedBytes -= f.Data.Length;
        return f;
    }

    /// <summary>
    /// Drops the oldest queued frame and counts it. A decoder that needs every frame still gets it, unshown; if that
    /// fails, the stream fails and its queue empties.
    /// </summary>
    void DropHead(VideoStream s)
    {
        var f = Dequeue(s);
        CountDrop(s, f.Window);
        try { SkipFrame(s, f.Data.AsSpan(0, f.Length)); }
        finally { ReturnFrameBuffer(f.Data); }
    }

    /// <summary>Releases the queued frames without counting them (the stream or the session is ending).</summary>
    void DiscardQueue(VideoStream s)
    {
        while (s.QueueCount > 0) ReturnFrameBuffer(Dequeue(s).Data);
    }

    /// <summary>
    /// Counts a dropped frame against the report window it arrived in: a window already sent keeps its counts, so the
    /// drop goes only into the lifetime total (spice-gtk likewise keeps playback drops out of the arrival report).
    /// </summary>
    static void CountDrop(VideoStream s, uint window)
    {
        if (window == s.Window) s.Drops++;
        else s.EarlierDrops++;
    }

    static void NextReportWindow(VideoStream s)
    {
        s.EarlierFrames += s.Frames; s.EarlierDrops += s.Drops;
        s.Frames = 0; s.Drops = 0;
        s.Window++;
    }

    /// <summary>Under _streamLock: draws every queued frame that is due, in order, then re-arms or cancels the wake.</summary>
    void PumpStreams(long now)
    {
        foreach (var s in _streams.Values)
        {
            while (s.QueueCount > 0 && s.Queue[s.QueueHead].Deadline - now <= PaceSlackMs)
            {
                // Deadlines never decrease along the queue; when the next frame is due as well, this one is stale.
                if (s.QueueCount > 1 && s.Queue[(s.QueueHead + 1) % MaxQueuedFrames].Deadline - now <= PaceSlackMs)
                {
                    DropHead(s);
                    continue;
                }
                DrawHead(s);
            }
        }
        UpdateWake();
    }

    /// <summary>Under _streamLock: dequeues the oldest queued frame and draws it now.</summary>
    void DrawHead(VideoStream s)
    {
        var f = Dequeue(s);
        try { DecodeStreamFrame(s, f.Data.AsSpan(0, f.Length), f.Dest, f.Window); }
        finally { ReturnFrameBuffer(f.Data); }
    }

    /// <summary>Under _streamLock: drops every queued frame but the newest (counted) and draws that one now.</summary>
    void DrawNewestQueued(VideoStream s)
    {
        while (s.QueueCount > 1) DropHead(s);
        if (s.QueueCount > 0) DrawHead(s); // empty when the decoder failed on a dropped frame
    }

    /// <summary>Under _streamLock: arms the wake for the earliest queued deadline, or cancels it when nothing is queued.</summary>
    void UpdateWake()
    {
        long next = long.MaxValue;
        foreach (var s in _streams.Values)
            if (s.QueueCount > 0) next = Math.Min(next, s.Queue[s.QueueHead].Deadline);
        if (next == long.MaxValue || _pacingStopped)
        {
            if (_wakeArmed) { _wakeArmed = false; _pacing.Disarm(); }
            return;
        }
        if (_wakeArmed && _wakeAt == next) return;
        if (_pacingWake is null)
        {
            _pacingWake = OnPacingWake;
            _pacing.Wake = _pacingWake;
            // Session end (disconnect or dispose) cancels the wake and drops queued frames. Registered once; runs
            // at once if the session has already ended.
            _cts.Token.UnsafeRegister(static s => ((SpiceSession)s!).StopPacing(), this);
            if (_pacingStopped) return;
        }
        _wakeArmed = true;
        _wakeAt = next;
        _pacing.Arm(next);
    }

    void OnPacingWake()
    {
        // Runs on a thread-pool thread: nothing may escape.
        try
        {
            lock (_streamLock)
            {
                _wakeArmed = false;
                if (_pacingStopped) return;
                PumpStreams(_pacing.NowMs);
            }
        }
        catch (Exception e) { Log.Error($"display: paced stream frame: {e.Message}"); }
    }

    void StopPacing()
    {
        lock (_streamLock)
        {
            _pacingStopped = true;
            _wakeArmed = false;
            _pacing.Dispose();
            foreach (var s in _streams.Values) DiscardQueue(s);
        }
    }

    /// <summary>Under _streamLock: a buffer of at least <paramref name="length"/> bytes, reused when one is free.</summary>
    /// <summary>
    /// A buffer for <paramref name="length"/> bytes: the smallest fitting spare, if it is at most one size class larger
    /// and fits in <paramref name="budget"/> (a large spare must not crowd out frames that would fit), else a new one.
    /// </summary>
    byte[] RentFrameBuffer(int length, long budget)
    {
        // Power-of-two sizes, so frames of a similar size share buffers. length is at most MaxQueuedBytes.
        int size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(length, 4096));
        long limit = Math.Max(size, Math.Min(2L * size, budget));
        int best = -1;
        for (int i = 0; i < _framePoolCount; i++)
        {
            int n = _framePool[i]!.Length;
            if (n >= length && n <= limit && (best < 0 || n < _framePool[best]!.Length)) best = i;
        }
        if (best >= 0)
        {
            var b = _framePool[best]!;
            _framePool[best] = _framePool[--_framePoolCount];
            _framePool[_framePoolCount] = null;
            _pooledBytes -= b.Length;
            return b;
        }
        return GC.AllocateUninitializedArray<byte>(size);
    }

    void ReturnFrameBuffer(byte[] buffer)
    {
        // Full (by count or bytes): evict the smallest spares while that makes room, since larger buffers fit any frame
        // the smaller ones would; a buffer that still doesn't fit is left to the GC.
        while (_framePoolCount > 0 && (_framePoolCount == MaxPooledBuffers || _pooledBytes + buffer.Length > MaxPooledBytes))
        {
            int smallest = 0;
            for (int i = 1; i < _framePoolCount; i++) if (_framePool[i]!.Length < _framePool[smallest]!.Length) smallest = i;
            if (_framePool[smallest]!.Length >= buffer.Length) return;
            _pooledBytes -= _framePool[smallest]!.Length;
            _framePool[smallest] = _framePool[--_framePoolCount];
            _framePool[_framePoolCount] = null;
        }
        if (_pooledBytes + buffer.Length > MaxPooledBytes) return;
        _framePool[_framePoolCount++] = buffer;
        _pooledBytes += buffer.Length;
    }

    void ClearFramePool()
    {
        Array.Clear(_framePool);
        _framePoolCount = 0;
        _pooledBytes = 0;
    }

    static SpiceClip CopyClip(SpiceClip clip)
    {
        if (clip.Type == SpiceClip.TypeNone) return clip;
        // Re-encode into an owned buffer and parse it back.
        var buf = new byte[1 + 4 + clip.Count * 16];
        var w = new SpiceWriter(buf);
        w.U8(SpiceClip.TypeRects); w.U32((uint)clip.Count);
        for (int i = 0; i < clip.Count; i++) { var r = clip[i]; w.I32(r.Top); w.I32(r.Left); w.I32(r.Bottom); w.I32(r.Right); }
        var reader = new SpiceReader(buf);
        return SpiceClip.Read(ref reader);
    }

    /// <summary>
    /// Under _streamLock: decodes one frame and blits it at <paramref name="dest"/> with the stream's current clip. A
    /// frame the clip hides entirely (the server has drawn over the whole stream area since, e.g. the video's window
    /// closed while frames waited for the guest clock) is counted as hidden and not decoded, unless the stream's decoder
    /// needs every frame: then it is decoded without a picture.
    /// </summary>
    void DecodeStreamFrame(VideoStream s, ReadOnlySpan<byte> data, SpiceRect dest, uint window)
    {
        if (s.Unsupported) return;
        if (!Canvas.IsFrameVisible(s.SurfaceId, dest, s.Clip)) { s.Hidden++; SkipFrame(s, data); return; }
        IVideoDecoder decoder;
        if (s.Decoder is { } own) decoder = own;
        else if (s.Codec == VideoCodecType.Mjpeg)
        {
            _mjpeg ??= new MjpegVideoDecoder();
            _mjpeg.Fast = _fastJpeg; // applied to the codec handle only when it changes
            decoder = _mjpeg;
        }
        else { CountDrop(s, window); return; } // not reached: such a stream is Unsupported from its creation
        var output = _videoOutput ??= new VideoOutput(this);
        output.Prepare(s, dest, bottomUp: !s.TopDown);
        VideoDecodeResult r;
        bool mirrored = false;
        try { r = decoder.Decode(data, output); mirrored = output.Mirrored; }
        catch (Exception e) when (s.Decoder is not null)
        {
            // A hardware decoder's failure (a lost device, a driver error) ends this stream, not the session.
            Log.Error($"display: stream {s.Id} decoder: {e.Message}");
            r = VideoDecodeResult.Failed;
        }
        finally { output.Prepare(null, default, false); }
        switch (r)
        {
            case VideoDecodeResult.Picture: s.Pictured = true; break;
            case VideoDecodeResult.NoPicture: return;
            case VideoDecodeResult.Dropped: CountDrop(s, window); return;
            default: FailStream(s, data); return;
        }
        // The stream's TOP_DOWN flag describes how the source surface was laid out; spice-gtk flips the pictures of
        // bottom-up streams, whatever their codec. The clip is the current one, as in spice-gtk: a STREAM_CLIP that
        // arrived while this frame waited keeps it off whatever was drawn over the stream since.
        try { Canvas.BlitFrame(s.SurfaceId, dest, s.Clip, s.Frame!.View, s.TopDown || mirrored); }
        catch (CanvasException e) { CountDrop(s, window); Log.Warn($"display: stream frame dropped: {e.Message}"); }
    }

    /// <summary>
    /// Under _streamLock (from <see cref="VideoOutput.Begin"/>): the stream's reused picture, resized to
    /// <paramref name="w"/>×<paramref name="h"/> if needed, or null when that is impossible or over the frame budget.
    /// </summary>
    internal DecodedImage? StreamFrameBuffer(object stream, int w, int h)
    {
        var s = (VideoStream)stream;
        if (w <= 0 || h <= 0 || w > Spice.Canvas.Canvas.MaxStreamSide || h > Spice.Canvas.Canvas.MaxStreamSide) return null;
        if (s.Frame is { } current && current.Width == w && current.Height == h) return current;
        s.Frame?.Release();
        s.Frame = null;
        long held = 0;
        foreach (var other in _streams.Values) if (other.Frame is { } f) held += (long)f.Stride * f.Height;
        if (held + (long)w * h * 4 > MaxStreamFrameBytes) { Log.Warn($"display: stream frame {w}x{h} exceeds the frame budget"); return null; }
        s.Frame = new DecodedImage(w, h, false);
        return s.Frame;
    }

    void SendStreamReport(uint id, VideoStream s)
    {
        Span<byte> b = stackalloc byte[32];
        int n = ClientMsg.StreamReport(b, id, s.UniqueId, s.StartMm, s.LastMm, s.Frames, s.Drops, 0, 0);
        _display?.Send(DisplayMsg.CStreamReport, b[..n]);
        NextReportWindow(s);
    }

    void DisposeMedia()
    {
        lock (_streamLock)
        {
            _pacingStopped = true;
            _wakeArmed = false;
            _pacing.Dispose();
            foreach (var s in _streams.Values) { DiscardQueue(s); s.Frame?.Release(); DisposeDecoder(s); }
            _streams.Clear();
            ClearFramePool();
            _mjpeg?.Dispose();
        }
        DisposeAudio();
    }
}
