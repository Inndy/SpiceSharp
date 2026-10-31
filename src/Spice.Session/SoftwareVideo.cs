using Spice.Native;
using Spice.Wire;

namespace Spice.Session;

/// <summary>
/// Software H.264 decoding with libavcodec in spicecodec.dll (docs/decisions/0037, amended 2026-10-31): for the streams
/// no hardware decoder takes, above all spice-server's default High 4:4:4 Predictive. Available only with a
/// spicecodec.dll that exports the sc_h264_* functions.
/// </summary>
public sealed class SoftwareVideo : IVideoDecoderFactory
{
    readonly int _threads;

    /// <summary><paramref name="threads"/>: libavcodec slice threads per stream; 0 picks min(4, logical processors).</summary>
    public SoftwareVideo(int threads = 0)
    {
        _threads = threads > 0 ? threads : DefaultThreads;
        Codecs = Task.FromResult<IReadOnlyList<VideoCodecType>>(H264Decoder.IsAvailable ? [VideoCodecType.H264] : []);
    }

    /// <summary>A small fixed count: x264's sliced-thread streams have about that many slices, and idle workers block.</summary>
    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount, 1, 4);

    public Task<IReadOnlyList<VideoCodecType>> Codecs { get; }

    /// <summary>libavcodec's H.264 decoder is in this spicecodec.dll.</summary>
    public static bool IsAvailable => H264Decoder.IsAvailable;

    public IVideoDecoder? Create(VideoCodecType codec, int width, int height)
    {
        if (codec != VideoCodecType.H264) return null;
        var d = H264Decoder.TryCreate(_threads);
        if (d is null) { Log.Warn("video: the software H.264 decoder could not be set up"); return null; }
        return new SoftwareH264VideoDecoder(d);
    }

    public bool CanDecode(in VideoFormat format) => H264Decoder.IsAvailable && Takes(format);

    public bool CanDecodeInSoftware(in VideoFormat format) => CanDecode(format);

    public bool CanDecodeInHardware(in VideoFormat format) => false;

    public IVideoDecoder? Create(in VideoFormat format, int width, int height, bool softwareOnly) =>
        CanDecode(format) ? Create(format.Codec, width, height) : null;

    /// <summary>
    /// The formats the software path converts: 8-bit H.264 in the profiles libavcodec decodes fully (Baseline, Main,
    /// Extended, High, High 10, High 4:2:2, High 4:4:4 Predictive and CAVLC 4:4:4 Intra), any chroma format. Deeper
    /// pictures would need another conversion; the scalable and multiview profiles decode only their base layer.
    /// </summary>
    public static bool Takes(in VideoFormat format) =>
        format.Codec == VideoCodecType.H264 && format.BitDepth == 8 && format.ChromaFormat is >= 0 and <= 3 &&
        format.Profile is 66 or 77 or 88 or 100 or 110 or 122 or 244 or 44;
}

/// <summary>
/// One stream's software H.264 decoder: every frame is decoded in order; the newest picture is converted to BGRA at its
/// own size straight into the session's frame buffer (the canvas scales it to the destination, as for MJPEG).
/// </summary>
sealed class SoftwareH264VideoDecoder(H264Decoder decoder) : IVideoDecoder
{
    /// <summary>Damaged frames in a row before the first picture after which the stream is given up.</summary>
    const int MaxFramesWithoutPicture = 30;
    readonly H264Decoder _decoder = decoder;
    bool _decoded;
    int _withoutPicture;

    public bool NeedsEveryFrame => true;
    public bool HasDecoded => _decoded;
    public bool Software => true;
    public bool Takes(in VideoFormat format) => SoftwareVideo.Takes(format);

    public VideoDecodeResult Decode(ReadOnlySpan<byte> data, VideoOutput? output)
    {
        if (data.IsEmpty) return output is null ? VideoDecodeResult.NoPicture : VideoDecodeResult.Dropped;
        var status = _decoder.Decode(data, out var picture);
        switch (status)
        {
            case H264DecodeStatus.Fatal:
                Log.Warn("video: software H.264 decoding failed");
                return VideoDecodeResult.Failed;
            case H264DecodeStatus.Invalid or H264DecodeStatus.NoPicture:
                // Damage after pictures were shown is one lost frame (later intra refresh repairs it); a stream that
                // never makes a picture is not one this decoder can take.
                if (!_decoded && ++_withoutPicture > MaxFramesWithoutPicture) return VideoDecodeResult.Failed;
                return output is null || status == H264DecodeStatus.NoPicture ? VideoDecodeResult.NoPicture : VideoDecodeResult.Dropped;
        }
        _decoded = true;
        if (picture.Supported == 0)
        {
            // The stream switched to a format the conversion doesn't take (a deeper bit depth): it can't go on.
            Log.Warn($"video: software H.264 picture format not supported (chroma {picture.Chroma}, {picture.Width}x{picture.Height})");
            return VideoDecodeResult.Failed;
        }
        if (output is null) return VideoDecodeResult.NoPicture;
        bool mirror = output.BottomUp;
        var buffer = output.Begin(picture.Width, picture.Height, mirror);
        if (buffer.IsEmpty || !_decoder.TryConvert(buffer, picture.Width * 4, mirror)) return VideoDecodeResult.Dropped;
        return VideoDecodeResult.Picture;
    }

    public void Dispose() => _decoder.Dispose();
}

/// <summary>
/// The session's video decoders (docs/decisions/0037, amended 2026-10-31): hardware first, software for what the
/// hardware refuses. A codec is advertised when either path decodes it; each stream's declared format picks the path.
/// </summary>
public sealed class VideoDecoderRouter : IVideoDecoderFactory
{
    volatile bool _softwareEnabled = true;
    /// <summary>Completed once the saved softwareVideo setting is applied; null when no setting is awaited.</summary>
    readonly TaskCompletionSource? _settingApplied;

    /// <param name="awaitSoftwareSetting">
    /// <see cref="Codecs"/> (what a session advertises) waits for <see cref="ApplySoftwareSetting"/> as well as for the
    /// hardware probe, so a saved "off" read after the connection started can't lose the race with advertising
    /// (review AF-R2). The session's probe timeout still bounds the wait; without an answer it advertises MJPEG only.
    /// </param>
    public VideoDecoderRouter(IVideoDecoderFactory? hardware, IVideoDecoderFactory? software, bool awaitSoftwareSetting = false)
    {
        Hardware = hardware;
        Software = software;
        if (awaitSoftwareSetting) _settingApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public IVideoDecoderFactory? Hardware { get; }
    public IVideoDecoderFactory? Software { get; }

    /// <summary>The saved setting is known: sets <see cref="SoftwareEnabled"/> and releases <see cref="Codecs"/>. Any thread.</summary>
    public void ApplySoftwareSetting(bool enabled)
    {
        SoftwareEnabled = enabled;
        _settingApplied?.TrySetResult();
    }

    bool SettingPending => _settingApplied is { Task.IsCompleted: false };

    /// <summary>
    /// Software decoding is allowed (the softwareVideo setting, on by default). Read when a session advertises its codecs
    /// (after the hardware probe) and when a stream picks its decoder, so a change applies to new streams at once and
    /// to advertising from the next connection.
    /// </summary>
    public bool SoftwareEnabled { get => _softwareEnabled; set => _softwareEnabled = value; }

    /// <summary>The union of both paths' codecs, software's only while enabled. A failed hardware probe leaves software's.
    /// With a setting awaited, it answers only once <see cref="ApplySoftwareSetting"/> has run.</summary>
    public Task<IReadOnlyList<VideoCodecType>> Codecs => Combine();

    async Task<IReadOnlyList<VideoCodecType>> Combine()
    {
        if (_settingApplied is { } setting) await setting.Task.ConfigureAwait(false);
        IReadOnlyList<VideoCodecType> hw = [];
        if (Hardware is { } h)
        {
            try { hw = await h.Codecs.ConfigureAwait(false); }
            catch (Exception e) { Log.Warn($"video: hardware probe failed ({e.Message})"); }
        }
        return Union(hw);
    }

    /// <summary>The codecs either path decodes now, or null until the hardware probe has answered and an awaited
    /// setting is applied (for menus).</summary>
    public IReadOnlyList<VideoCodecType>? KnownCodecs =>
        SettingPending ? null :
        Hardware is null ? Union([]) :
        Hardware.Codecs.IsCompletedSuccessfully ? Union(Hardware.Codecs.Result) :
        Hardware.Codecs.IsCompleted ? Union([]) : null;

    IReadOnlyList<VideoCodecType> Union(IReadOnlyList<VideoCodecType> hw)
    {
        var all = new List<VideoCodecType>(hw);
        foreach (var c in SoftwareCodecs) if (!all.Contains(c)) all.Add(c);
        return all;
    }

    /// <summary>The codecs the hardware path decodes ([] until its probe has answered).</summary>
    public IReadOnlyList<VideoCodecType> HardwareCodecs =>
        Hardware is { Codecs.IsCompletedSuccessfully: true } h ? h.Codecs.Result : [];

    /// <summary>The codecs the software path decodes ([] while disabled).</summary>
    public IReadOnlyList<VideoCodecType> SoftwareCodecs =>
        SoftwareEnabled && Software is { Codecs.IsCompletedSuccessfully: true } s ? s.Codecs.Result : [];

    bool HardwareHas(VideoCodecType codec) => HardwareCodecs.Contains(codec);
    bool SoftwareHas(VideoCodecType codec) => SoftwareCodecs.Contains(codec);

    /// <summary>A stream whose format isn't known yet: hardware when it has the codec (and can make a decoder now), else software.</summary>
    public IVideoDecoder? Create(VideoCodecType codec, int width, int height)
    {
        if (HardwareHas(codec) && Hardware!.Create(codec, width, height) is { } d) return d;
        return SoftwareHas(codec) ? Software!.Create(codec, width, height) : null;
    }

    public bool CanDecode(in VideoFormat format) =>
        (HardwareHas(format.Codec) && Hardware!.CanDecode(format)) || CanDecodeInSoftware(format);

    public bool CanDecodeInSoftware(in VideoFormat format) => SoftwareHas(format.Codec) && Software!.CanDecode(format);

    public bool CanDecodeInHardware(in VideoFormat format) => HardwareHas(format.Codec) && Hardware!.CanDecode(format);

    public IVideoDecoder? Create(in VideoFormat format, int width, int height, bool softwareOnly)
    {
        if (!softwareOnly && HardwareHas(format.Codec) && Hardware!.CanDecode(format) && Hardware.Create(format.Codec, width, height) is { } d)
            return d;
        return CanDecodeInSoftware(format) ? Software!.Create(format.Codec, width, height) : null;
    }
}
