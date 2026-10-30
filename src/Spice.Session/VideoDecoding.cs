using Spice.Canvas;
using Spice.Native;
using Spice.Wire;

namespace Spice.Session;

/// <summary>What <see cref="IVideoDecoder.Decode"/> did with one frame.</summary>
public enum VideoDecodeResult
{
    /// <summary>A picture was written through the <see cref="VideoOutput"/>.</summary>
    Picture,
    /// <summary>The frame was taken, but there is no picture to show (none was asked for, or the decoder holds it back).</summary>
    NoPicture,
    /// <summary>This frame can't be shown (bad data, too large); the stream goes on. Counted as a drop.</summary>
    Dropped,
    /// <summary>The stream can't go on (decoder or device failure). The session tells the server, which then sends the area as images.</summary>
    Failed,
}

/// <summary>
/// Decodes the frames of one video stream (docs/decisions/0037). The session calls it from one thread at a time, under
/// its stream lock: the display channel's thread, or the pacing wake's. Implementations must not allocate managed memory
/// per frame.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>
    /// Frames refer to earlier frames (H.264, VP9, HEVC), so every frame must reach the decoder in order, also one that
    /// is not shown; the session then passes a null output instead of dropping it. False for MJPEG, whose frames stand
    /// alone and are dropped without decoding.
    /// </summary>
    bool NeedsEveryFrame { get; }

    /// <summary>
    /// Decodes one STREAM_DATA payload. With <paramref name="output"/> null the frame only advances the decoder (it is
    /// late, hidden or superseded) and the result is <see cref="VideoDecodeResult.NoPicture"/> unless it fails.
    /// </summary>
    VideoDecodeResult Decode(ReadOnlySpan<byte> data, VideoOutput? output);

    /// <summary>
    /// The decoder has produced at least one decoded picture, shown or not (a hidden, late or superseded frame's, or one
    /// the output refused). A stream whose decoder fails after that failed for a passing reason (a lost device), not
    /// because it can't take this server's stream, so the session doesn't avoid its codec (docs/decisions/0037).
    /// </summary>
    bool HasDecoded => false;
}

/// <summary>Creates decoders for the codecs the app can decode besides MJPEG, which the session decodes itself.</summary>
public interface IVideoDecoderFactory
{
    /// <summary>
    /// The codecs to advertise (SPICE_DISPLAY_CAP_CODEC_*), once known. The session waits for it before it links the
    /// display channel, so a probe should start early and finish quickly; a fault or an empty list advertises MJPEG only.
    /// </summary>
    Task<IReadOnlyList<VideoCodecType>> Codecs { get; }

    /// <summary>
    /// A decoder for a new stream of <paramref name="codec"/>, encoded at <paramref name="width"/>×<paramref name="height"/>
    /// (STREAM_CREATE's stream size), or null when none can be made now (the factory logs why). The session then reports
    /// the stream as unsupported, as spice-gtk does, and the server sends that area as ordinary images.
    /// </summary>
    IVideoDecoder? Create(VideoCodecType codec, int width, int height);

    /// <summary>
    /// Whether the decoders take the format a stream's first frames declare (<see cref="VideoBitstream"/>). The session
    /// checks it before decoding: a stream they can't take is reported unsupported at once, and the session asks the
    /// server for other codecs (docs/decisions/0037). Formats not read (HEVC) are not checked.
    /// </summary>
    bool CanDecode(in VideoFormat format) => true;
}

/// <summary>
/// Where a decoder puts its picture: the session's reused frame for the stream. One per session, set up before each
/// <see cref="IVideoDecoder.Decode"/>; nothing is allocated per frame once the frame size is steady.
/// </summary>
public sealed class VideoOutput
{
    readonly SpiceSession _session;
    object? _stream;

    internal VideoOutput(SpiceSession session) => _session = session;

    /// <summary>The stream's destination size on the guest surface. A decoder that scales on its own produces this size.</summary>
    public int DestinationWidth { get; private set; }
    public int DestinationHeight { get; private set; }
    /// <summary>
    /// The stream is bottom-up (no SPICE_STREAM_FLAGS_TOP_DOWN): its pictures show the image upside down, as spice-gtk
    /// treats every codec. A decoder that can mirror rows for free does so and says so in <see cref="Begin"/>.
    /// </summary>
    public bool BottomUp { get; private set; }
    /// <summary>Set by <see cref="Begin"/>: the picture is already mirrored, so it is drawn top-down.</summary>
    internal bool Mirrored { get; private set; }

    internal void Prepare(object? stream, SpiceRect dest, bool bottomUp)
    {
        _stream = stream;
        DestinationWidth = dest.Width;
        DestinationHeight = dest.Height;
        BottomUp = bottomUp;
        Mirrored = false;
    }

    /// <summary>
    /// The buffer for a <paramref name="width"/>×<paramref name="height"/> picture: opaque B8G8R8A8 rows, top-down,
    /// <c>width × 4</c> bytes apart. Empty when the size is impossible or over the session's frame budget (the decoder
    /// then returns <see cref="VideoDecodeResult.Dropped"/>). <paramref name="mirrored"/>: the decoder writes a bottom-up
    /// stream's rows in reverse, so the picture needs no flip when drawn.
    /// </summary>
    public Span<byte> Begin(int width, int height, bool mirrored)
    {
        if (_stream is null) throw new InvalidOperationException("no frame is being decoded");
        Mirrored = mirrored && BottomUp;
        var image = _session.StreamFrameBuffer(_stream, width, height);
        return image is null ? default : image.Bytes;
    }
}

/// <summary>MJPEG through libjpeg-turbo: every frame stands alone. The session keeps one for all MJPEG streams.</summary>
sealed class MjpegVideoDecoder : IVideoDecoder
{
    /// <summary>Largest side of an MJPEG frame accepted (a frame larger than the budget is dropped before decoding).</summary>
    const int MaxSide = 8192;
    readonly JpegDecoder _jpeg = new();

    /// <summary>See <see cref="JpegDecoder.Fast"/>: applied to the codec handle only when it changes.</summary>
    public bool Fast { get => _jpeg.Fast; set => _jpeg.Fast = value; }

    public bool NeedsEveryFrame => false;

    public VideoDecodeResult Decode(ReadOnlySpan<byte> data, VideoOutput? output)
    {
        if (output is null) return VideoDecodeResult.NoPicture;
        if (!_jpeg.TryReadHeader(data, out int w, out int h) || w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) return VideoDecodeResult.Dropped;
        // MJPEG frames are top-down images; the stream's TOP_DOWN flag describes how the source surface was laid out,
        // and the canvas flips bottom-up streams when it draws them.
        var buffer = output.Begin(w, h, mirrored: false);
        if (buffer.IsEmpty || !_jpeg.TryDecode(data, buffer, w, h, w * 4)) return VideoDecodeResult.Dropped;
        var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(buffer);
        for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000;
        return VideoDecodeResult.Picture;
    }

    public void Dispose() => _jpeg.Dispose();
}
