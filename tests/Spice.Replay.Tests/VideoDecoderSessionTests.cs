using System.Collections;
using System.Reflection;
using Spice.Canvas;
using Spice.Session;
using Spice.Tests;
using Spice.Transport;
using Spice.Wire;
using VvFile;
using Xunit.Abstractions;

namespace Spice.Replay.Tests;

// The session side of hardware video decoding (docs/decisions/0037), with a fake decoder: codec advertising, the
// preferred-codec message, unsupported streams, frames that are not shown, and allocations. The Media Foundation
// decoders themselves are tested in Spice.Video.Tests.
#pragma warning disable IL2026, IL2075
[Collection("Allocation profiling")]
public sealed class VideoDecoderSessionTests(ITestOutputHelper output)
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    const uint Epoch = 1000;
    const uint Red = 0xFFC83228, Black = 0xFF000000;
    static readonly SpiceRect Destination = new(2, 1, 6, 4); // 4x3

    /// <summary>Fills each picture with <see cref="Color"/>; records every frame's first byte and whether it was shown.</summary>
    sealed class FakeDecoder : IVideoDecoder
    {
        public readonly List<(byte Id, bool Shown)> Calls = [];
        public bool Record = true;
        public VideoDecodeResult Next = VideoDecodeResult.Picture;
        public uint Color = Red;
        public bool Disposed;
        public bool NeedsEveryFrame => true;
        public VideoDecodeResult Decode(ReadOnlySpan<byte> data, VideoOutput? output)
        {
            if (Record) Calls.Add((data[0], output is not null));
            if (Next != VideoDecodeResult.Picture || output is null) return Next == VideoDecodeResult.Picture ? VideoDecodeResult.NoPicture : Next;
            var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(output.Begin(output.DestinationWidth, output.DestinationHeight, mirrored: true));
            if (px.IsEmpty) return VideoDecodeResult.Dropped;
            // Top row Color, the rest a darker shade: a bottom-up stream must still show Color on top (the decoder mirrored).
            px[..output.DestinationWidth].Fill(Color);
            px[output.DestinationWidth..].Fill(0xFF101010);
            return VideoDecodeResult.Picture;
        }
        public void Dispose() => Disposed = true;
    }

    sealed class FakeFactory(params VideoCodecType[] codecs) : IVideoDecoderFactory
    {
        public readonly List<FakeDecoder> Made = [];
        public bool Refuse;
        public Action? Creating;
        public Func<VideoFormat, bool>? Accept;
        public readonly List<VideoFormat> Checked = [];
        public bool CanDecode(in VideoFormat format) { Checked.Add(format); return Accept?.Invoke(format) ?? true; }
        public Task<IReadOnlyList<VideoCodecType>> Codecs { get; set; } = Task.FromResult<IReadOnlyList<VideoCodecType>>(codecs);
        public IVideoDecoder? Create(VideoCodecType codec, int width, int height)
        {
            if (Refuse) return null;
            Creating?.Invoke();
            var d = new FakeDecoder();
            Made.Add(d);
            return d;
        }
    }

    /// <summary>Time stands still at 0: nothing queued comes due unless a test changes the mode.</summary>
    sealed class ManualClock : PacingClock
    {
        public override long NowMs => 0;
        public override void Arm(long atMs) { }
        public override void Disarm() { }
        public override void Dispose() { }
    }

    static FieldInfo Field(string name) => typeof(SpiceSession).GetField(name, PrivateInstance)!;
    static byte[] Bytes(Action<BinaryWriter> write)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }
    static void Rect(BinaryWriter w, SpiceRect r) { w.Write(r.Top); w.Write(r.Left); w.Write(r.Bottom); w.Write(r.Right); }
    static byte[] Create(VideoCodecType codec = VideoCodecType.H264, bool topDown = true, uint id = 7, SpiceRect[]? clip = null) => Bytes(w =>
    {
        w.Write(0u); w.Write(id); w.Write(topDown ? StreamCreate.FlagTopDown : (byte)0); w.Write((byte)codec); w.Write(0UL);
        w.Write(4u); w.Write(3u); w.Write(4u); w.Write(3u); Rect(w, Destination);
        if (clip is null) w.Write(SpiceClip.TypeNone);
        else { w.Write(SpiceClip.TypeRects); w.Write((uint)clip.Length); foreach (var r in clip) Rect(w, r); }
    });
    static byte[] Data(byte frameId, uint mm = Epoch, uint id = 7) => Bytes(w => { w.Write(id); w.Write(mm); w.Write(3u); w.Write([frameId, 0xAA, 0xBB]); });
    static byte[] Data(byte[] payload, uint id = 7) => Bytes(w => { w.Write(id); w.Write(Epoch); w.Write((uint)payload.Length); w.Write(payload); });
    /// <summary>An access unit delimiter and an SPS of High 4:4:4 Predictive (profile 244, chroma_format_idc 3), as
    /// spice-server's x264enc sends by default.</summary>
    static readonly byte[] High444Frame = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x67, 244, 0, 31, 0x91, 0x80];
    /// <summary>An SPS of High (profile 100), 4:2:0, 8-bit.</summary>
    static readonly byte[] High420Frame = [0, 0, 1, 0x67, 100, 0, 31, 0xAC, 0x80];

    sealed class Harness : IAsyncDisposable
    {
        public readonly FakeFactory Factory;
        public readonly SpiceSession Session;
        public Harness(FakeFactory factory, VideoPacing pacing = VideoPacing.Off, params VideoCodecType[] advertised)
        {
            Factory = factory;
            Session = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n")) { VideoDecoders = factory };
            Session.PacingClock = Clock;
            Session.VideoPacing = pacing;
            Session.MultimediaTimeBase(Epoch);
            Field("_videoCodecs").SetValue(Session, advertised.Length > 0 ? advertised : new[] { VideoCodecType.H264 });
            Send(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write(10u); w.Write(8u); w.Write(32u); w.Write(1u); }));
        }
        public readonly ManualClock Clock = new();
        public void Send(ushort type, byte[] body) => Session.OnDisplayMessage(type, body);
        public object Stream(uint id = 7) => ((IDictionary)Field("_streams").GetValue(Session)!)[id]!;
        public T State<T>(string name, uint id = 7) { var s = Stream(id); return (T)s.GetType().GetField(name)!.GetValue(s)!; }
        public uint Pixel(int x, int y) => Session.Canvas.Primary!.Row(y)[x];
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    [Fact]
    public void DecodableCodecsAreAdvertisedBesideMjpeg()
    {
        var caps = SpiceSession.DisplayCaps([VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.H265]);
        foreach (int cap in new[] { DisplayCap.CodecMjpeg, DisplayCap.CodecH264, DisplayCap.CodecVp9, DisplayCap.CodecH265, DisplayCap.MultiCodec, DisplayCap.StreamReport })
            Assert.True(caps.Has(cap));
        Assert.False(caps.Has(DisplayCap.CodecVp8));
        var mjpegOnly = SpiceSession.DisplayCaps(Array.Empty<VideoCodecType>());
        Assert.True(mjpegOnly.Has(DisplayCap.CodecMjpeg));
        Assert.False(mjpegOnly.Has(DisplayCap.CodecH264) || mjpegOnly.Has(DisplayCap.CodecVp9) || mjpegOnly.Has(DisplayCap.CodecH265));
    }

    static Task<VideoCodecType[]> Probe(SpiceSession s) =>
        (Task<VideoCodecType[]>)typeof(SpiceSession).GetMethod("DecodableVideoCodecs", PrivateInstance)!.Invoke(s, [CancellationToken.None])!;

    [Fact]
    public async Task TheFactorysCodecsAreAdvertisedAndAFailedProbeMeansMjpegOnly()
    {
        var factory = new FakeFactory(VideoCodecType.H264, VideoCodecType.Mjpeg, VideoCodecType.H265, VideoCodecType.H264);
        await using (var h = new Harness(factory))
            Assert.Equal([VideoCodecType.H264, VideoCodecType.H265], await Probe(h.Session));
        var broken = new FakeFactory { Codecs = Task.FromException<IReadOnlyList<VideoCodecType>>(new InvalidOperationException("no MF")) };
        await using (var h = new Harness(broken))
        {
            Assert.Empty(await Probe(h.Session));
            h.Send(DisplayMsg.StreamCreate, Create());
            Assert.True(h.State<bool>("Unsupported")); // H.264 was not advertised after all: no decoder is asked for
            Assert.Empty(broken.Made);
        }
    }

    [Fact]
    public async Task AStreamIsDecodedByItsOwnDecoderAndDrawnAtItsDestination()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264));
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Send(DisplayMsg.StreamData, Data(1));
        var d = Assert.Single(h.Factory.Made);
        Assert.Equal([(1, true)], d.Calls);
        Assert.Equal(Red, h.Pixel(2, 1)); Assert.Equal(Red, h.Pixel(5, 1));
        Assert.Equal(0xFF101010, h.Pixel(2, 3));
        Assert.Equal(Black, h.Pixel(1, 1)); Assert.Equal(Black, h.Pixel(6, 1)); // outside the destination
        Assert.Null(Field("_mjpeg").GetValue(h.Session));
        h.Send(DisplayMsg.StreamDestroy, BitConverter.GetBytes(7u));
        Assert.True(d.Disposed);
    }

    [Fact]
    public async Task ABottomUpStreamIsNotFlippedAgainWhenTheDecoderMirroredIt()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264));
        h.Send(DisplayMsg.StreamCreate, Create(topDown: false));
        h.Send(DisplayMsg.StreamData, Data(1));
        Assert.Equal(Red, h.Pixel(3, 1));
        Assert.Equal(0xFF101010, h.Pixel(3, 3));
    }

    [Fact]
    public async Task TheClipLimitsThePicture()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264));
        h.Send(DisplayMsg.StreamCreate, Create(clip: [new SpiceRect(2, 1, 4, 4)])); // the left half
        h.Send(DisplayMsg.StreamData, Data(1));
        Assert.Equal(Red, h.Pixel(3, 1));
        Assert.Equal(Black, h.Pixel(4, 1)); Assert.Equal(Black, h.Pixel(5, 1));
    }

    [Fact]
    public async Task FramesThatAreNotShownStillReachTheDecoderInOrder()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264), VideoPacing.Clock);
        h.Send(DisplayMsg.StreamCreate, Create());
        var d = h.Factory.Made[0];
        h.Send(DisplayMsg.StreamData, Data(1, Epoch + 100)); // ahead of the guest clock: queued
        h.Send(DisplayMsg.StreamData, Data(2, Epoch + 120)); // queued
        h.Send(DisplayMsg.StreamData, Data(3, Epoch - 50)); // behind the clock: dropped on arrival...
        // ...but decoded unshown, after the two older queued frames (also dropped, so the order holds).
        Assert.Equal([(1, false), (2, false), (3, false)], d.Calls);
        Assert.Equal(3u, h.State<uint>("Drops"));
        // Hidden by its clip: decoded, not shown, counted as hidden.
        h.Send(DisplayMsg.StreamClip, Bytes(w => { w.Write(7u); w.Write(SpiceClip.TypeRects); w.Write(0u); }));
        h.Session.VideoPacing = VideoPacing.Off;
        h.Send(DisplayMsg.StreamData, Data(4));
        Assert.Equal((4, false), d.Calls[^1]);
        Assert.Equal(1u, h.State<uint>("Hidden"));
        Assert.All(new[] { h.Pixel(2, 1), h.Pixel(5, 3) }, p => Assert.Equal(Black, p));
    }

    [Fact]
    public async Task ADecoderThatFailsEndsItsStreamAndLaterFramesAreIgnored()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264));
        h.Send(DisplayMsg.StreamCreate, Create());
        var d = h.Factory.Made[0];
        d.Next = VideoDecodeResult.Failed;
        h.Send(DisplayMsg.StreamData, Data(1));
        Assert.True(h.State<bool>("Unsupported"));
        Assert.True(d.Disposed);
        Assert.Null(h.State<IVideoDecoder?>("Decoder"));
        h.Send(DisplayMsg.StreamData, Data(2));
        Assert.Single(d.Calls);
        Assert.Equal(0u, h.State<uint>("Drops"));
        Assert.Equal(Black, h.Pixel(3, 2));
    }

    [Fact]
    public async Task ADroppedPictureIsCountedAndNoPictureIsNot()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264));
        h.Send(DisplayMsg.StreamCreate, Create());
        var d = h.Factory.Made[0];
        d.Next = VideoDecodeResult.Dropped;
        h.Send(DisplayMsg.StreamData, Data(1));
        d.Next = VideoDecodeResult.NoPicture;
        h.Send(DisplayMsg.StreamData, Data(2));
        Assert.Equal(1u, h.State<uint>("Drops"));
        Assert.False(h.State<bool>("Unsupported"));
    }

    [Fact]
    public async Task AStreamWithoutADecoderIsReportedUnsupportedAsSpiceGtkDoes()
    {
        var factory = new FakeFactory(VideoCodecType.H264) { Refuse = true };
        await using var h = new Harness(factory);
        var capture = new Capture();
        await using var channel = Display(capture, DisplayCap.StreamReport);
        Field("_display").SetValue(h.Session, channel);
        h.Send(DisplayMsg.StreamCreate, Create());
        Assert.True(h.State<bool>("Unsupported"));
        h.Send(DisplayMsg.StreamActivateReport, Bytes(w => { w.Write(7u); w.Write(99u); w.Write(10u); w.Write(1000u); }));
        Assert.False(h.State<bool>("Reporting"));
        // Mini header (u16 102, u32 32), then stream_id 7, unique_id 0, start/end 0, num_frames 0, num_drops UINT32_MAX,
        // last_frame_delay 0, audio_delay 0 (spice-gtk's report_invalid_stream).
        byte[] expected = Bytes(w =>
        {
            w.Write((ushort)102); w.Write(32u);
            w.Write(7u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(uint.MaxValue); w.Write(0); w.Write(0u);
        });
        Assert.Equal(expected, await WrittenBeforeSentinel(channel, capture));
    }

    // ---------------- the declared format ----------------

    [Fact]
    public async Task AFormatTheDecoderCantTakeFailsTheStreamAndTheCodecIsAvoidedForTheSession()
    {
        var factory = new FakeFactory(VideoCodecType.H264) { Accept = f => f.ChromaFormat == 1 };
        await using var h = new Harness(factory);
        var capture = new Capture();
        await using var channel = Display(capture, DisplayCap.StreamReport, DisplayCap.PrefVideoCodecType);
        Field("_display").SetValue(h.Session, channel);
        h.Session.PreferredVideoCodecs = [VideoCodecType.H264];
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Send(DisplayMsg.StreamData, Data(High444Frame));
        Assert.Equal(new VideoFormat(VideoCodecType.H264, 244, 3, 8), Assert.Single(factory.Checked));
        Assert.True(h.State<bool>("Unsupported"));
        Assert.Empty(factory.Made[0].Calls); // never decoded
        Assert.True(factory.Made[0].Disposed);
        // The next H.264 stream gets no decoder and is reported at once.
        h.Send(DisplayMsg.StreamCreate, Create(id: 8));
        Assert.True(h.State<bool>("Unsupported", 8));
        Assert.Single(factory.Made);
        // Asking for H.264 again still names MJPEG, ahead of the avoided codec.
        h.Session.PreferredVideoCodecs = [VideoCodecType.H264, VideoCodecType.Vp9];
        static byte[] Report(uint id) => Bytes(w =>
        {
            w.Write((ushort)102); w.Write(32u);
            w.Write(id); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(uint.MaxValue); w.Write(0); w.Write(0u);
        });
        byte[] expected = [105, 0, 2, 0, 0, 0, 1, 3, 105, 0, 2, 0, 0, 0, 1, 1, .. Report(7), .. Report(8), 105, 0, 2, 0, 0, 0, 1, 1];
        Assert.Equal(expected, await WrittenBeforeSentinel(channel, capture));
    }

    [Fact]
    public async Task ADecodableFormatIsCheckedOnceAndDecoded()
    {
        var factory = new FakeFactory(VideoCodecType.H264) { Accept = f => f.ChromaFormat == 1 };
        await using var h = new Harness(factory);
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Send(DisplayMsg.StreamData, Data(High420Frame));
        h.Send(DisplayMsg.StreamData, Data(High420Frame));
        Assert.Equal(new VideoFormat(VideoCodecType.H264, 100, 1, 8), Assert.Single(factory.Checked));
        Assert.False(h.State<bool>("Unsupported"));
        Assert.Equal(2, factory.Made[0].Calls.Count);
    }

    [Fact]
    public async Task ADecoderFailingBeforeItsFirstPictureAvoidsTheCodecButNotAfter()
    {
        await using (var h = new Harness(new FakeFactory(VideoCodecType.H264)))
        {
            h.Send(DisplayMsg.StreamCreate, Create());
            h.Factory.Made[0].Next = VideoDecodeResult.Failed;
            h.Send(DisplayMsg.StreamData, Data(1));
            h.Send(DisplayMsg.StreamCreate, Create(id: 8));
            Assert.True(h.State<bool>("Unsupported", 8));
            Assert.Single(h.Factory.Made);
        }
        await using (var h = new Harness(new FakeFactory(VideoCodecType.H264)))
        {
            h.Send(DisplayMsg.StreamCreate, Create());
            h.Send(DisplayMsg.StreamData, Data(1));
            h.Factory.Made[0].Next = VideoDecodeResult.Failed;
            h.Send(DisplayMsg.StreamData, Data(2));
            Assert.True(h.State<bool>("Unsupported"));
            h.Send(DisplayMsg.StreamCreate, Create(id: 8));
            Assert.False(h.State<bool>("Unsupported", 8));
            Assert.Equal(2, h.Factory.Made.Count);
        }
    }

    // ---------------- DISPLAY_PREFERRED_VIDEO_CODEC_TYPE ----------------

    [Fact]
    public async Task CodecAvoidedWhileADecoderIsBeingCreatedIsRecheckedBeforeInstallingIt()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264), VideoPacing.Clock);
        var capture = new Capture();
        await using var channel = Display(capture, DisplayCap.StreamReport, DisplayCap.PrefVideoCodecType);
        Field("_display").SetValue(h.Session, channel);
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Send(DisplayMsg.StreamActivateReport, Bytes(w => { w.Write(7u); w.Write(99u); w.Write(10u); w.Write(1000u); }));
        h.Send(DisplayMsg.StreamData, Data(1, Epoch + 100));
        h.Factory.Made[0].Next = VideoDecodeResult.Failed;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        h.Factory.Creating = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var creating = Task.Run(() => h.Send(DisplayMsg.StreamCreate, Create(id: 8)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            h.Session.VideoPacing = VideoPacing.Off; // queued frame fails on the pacing/UI side while Create is outside the lock
        }
        finally { release.Set(); await creating.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.True(h.State<bool>("Unsupported"));
        byte[] written = await WrittenBeforeSentinel(channel, capture);
        byte[] expected = [.. Preference(VideoCodecType.Mjpeg), .. InvalidReport(7), .. InvalidReport(8)];
        Assert.Equal(expected, written); // both the already-open stream and the in-flight stream need invalid reports
        Assert.True(h.State<bool>("Unsupported", 8));
        Assert.True(h.Factory.Made[1].Disposed);
    }

    sealed class PausedPreference(ManualResetEventSlim entered, ManualResetEventSlim release) : IReadOnlyList<VideoCodecType>
    {
        public int Count => 1;
        public VideoCodecType this[int index] => index == 0 ? VideoCodecType.H264 : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<VideoCodecType> GetEnumerator()
        {
            yield return VideoCodecType.H264;
            // The sender has now filtered H264 using the old avoidance state, but has not enqueued its request.
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    static byte[] Preference(params VideoCodecType[] codecs) => Bytes(w =>
    {
        w.Write((ushort)105); w.Write((uint)(1 + codecs.Length)); w.Write((byte)codecs.Length);
        foreach (var codec in codecs) w.Write((byte)codec);
    });

    static byte[] InvalidReport(uint id) => Bytes(w =>
    {
        w.Write((ushort)102); w.Write(32u);
        w.Write(id); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); w.Write(uint.MaxValue); w.Write(0); w.Write(0u);
    });

    [Fact]
    public async Task APreferenceFilteredBeforeAvoidanceCannotRestoreTheAvoidedCodecAfterTheResend()
    {
        var factory = new FakeFactory(VideoCodecType.H264) { Accept = f => f.ChromaFormat == 1 };
        await using var h = new Harness(factory);
        var capture = new Capture();
        await using var channel = Display(capture, DisplayCap.StreamReport, DisplayCap.PrefVideoCodecType);
        Field("_display").SetValue(h.Session, channel);
        h.Send(DisplayMsg.StreamCreate, Create());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sending = Task.Run(() => h.Session.SendPreferredVideoCodecs(channel, new PausedPreference(entered, release)));
        Task? avoiding = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            avoiding = Task.Run(() => h.Send(DisplayMsg.StreamData, Data(High444Frame)));
            // Current code completes the resend before the paused sender resumes. A fix that serializes the
            // entire operation may block avoidance here; release the sender in that case and check the final wire order.
            try { await avoiding.WaitAsync(TimeSpan.FromMilliseconds(500)); }
            catch (TimeoutException) { }
        }
        finally
        {
            release.Set();
            await sending.WaitAsync(TimeSpan.FromSeconds(10));
            if (avoiding is not null) await avoiding.WaitAsync(TimeSpan.FromSeconds(10));
        }
        byte[] written = await WrittenBeforeSentinel(channel, capture);
        byte[]? lastPreference = null;
        int reports = 0;
        for (int at = 0; at < written.Length;)
        {
            ushort type = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(at));
            int length = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(at + 2)));
            var message = written.AsSpan(at, 6 + length).ToArray();
            if (type == 105) lastPreference = message;
            else { Assert.Equal(InvalidReport(7), message); reports++; }
            at += 6 + length;
        }
        Assert.Equal(1, reports);
        Assert.Equal(Preference(VideoCodecType.Mjpeg), lastPreference);
    }

    [Fact]
    public async Task ANewSessionDoesNotInheritAvoidanceFromAnEarlierConnection()
    {
        var factory = new FakeFactory(VideoCodecType.H264) { Accept = f => f.ChromaFormat == 1 };
        await using (var previous = new Harness(factory))
        {
            previous.Send(DisplayMsg.StreamCreate, Create());
            previous.Send(DisplayMsg.StreamData, Data(High444Frame));
            Assert.True(previous.State<bool>("Unsupported"));
        }
        await using var next = new Harness(factory);
        next.Send(DisplayMsg.StreamCreate, Create());
        next.Send(DisplayMsg.StreamData, Data(High420Frame));
        Assert.False(next.State<bool>("Unsupported"));
        var capture = new Capture();
        await using var channel = Display(capture, DisplayCap.PrefVideoCodecType);
        Assert.True(next.Session.SendPreferredVideoCodecs(channel, [VideoCodecType.H264]));
        Assert.Equal(Preference(VideoCodecType.H264), await WrittenBeforeSentinel(channel, capture));
    }

    [Fact]
    public async Task ThePreferenceNamesOnlyAdvertisedCodecsInOrderAndNeedsTheServerCap()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264), advertised: [VideoCodecType.H264]);
        var capture = new Capture();
        await using (var channel = Display(capture, DisplayCap.PrefVideoCodecType))
        {
            Assert.True(h.Session.SendPreferredVideoCodecs(channel, [VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.H265]));
            Assert.False(h.Session.SendPreferredVideoCodecs(channel, [VideoCodecType.Vp9, VideoCodecType.H265])); // none decodable
            Assert.True(h.Session.SendPreferredVideoCodecs(channel, [VideoCodecType.Mjpeg]));
            Assert.True(h.Session.SendPreferredVideoCodecs(channel, [VideoCodecType.Vp9, VideoCodecType.H264, VideoCodecType.Mjpeg, VideoCodecType.H264]));
            // Mini header: u16 105, u32 size; then num_of_codecs and the codec bytes.
            Assert.Equal([105, 0, 2, 0, 0, 0, 1, 3, 105, 0, 2, 0, 0, 0, 1, 1, 105, 0, 3, 0, 0, 0, 2, 3, 1], await WrittenBeforeSentinel(channel, capture));
        }
        var none = new Capture();
        await using (var channel = Display(none))
        {
            Assert.False(h.Session.SendPreferredVideoCodecs(channel, [VideoCodecType.H264]));
            Assert.Empty(await WrittenBeforeSentinel(channel, none));
        }
    }

    [Fact]
    public async Task TheSessionSendsEachPreferenceChangeOnceTheDisplayIsUp()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264));
        var capture = new Capture();
        await using var channel = Display(capture, DisplayCap.PrefVideoCodecType);
        h.Session.PreferredVideoCodecs = [VideoCodecType.H264]; // before the display channel: only remembered
        Field("_display").SetValue(h.Session, channel);
        h.Session.PreferredVideoCodecs = [VideoCodecType.Mjpeg]; // a change: sent
        h.Session.PreferredVideoCodecs = [VideoCodecType.Mjpeg]; // no change
        h.Session.PreferredVideoCodecs = null; // server default: nothing to send
        Assert.Equal([105, 0, 2, 0, 0, 0, 1, 1], await WrittenBeforeSentinel(channel, capture));
        Assert.Throws<ArgumentOutOfRangeException>(() => h.Session.PreferredVideoCodecs = [(VideoCodecType)9]);
    }

    // ---------------- allocations ----------------

    [Fact]
    public async Task SteadyHardwareStreamFramesAllocateNothingInTheSession()
    {
        await using var h = new Harness(new FakeFactory(VideoCodecType.H264));
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Factory.Made[0].Record = false;
        var shown = Data(1);
        var hidden = Bytes(w => { w.Write(7u); w.Write(SpiceClip.TypeRects); w.Write(0u); });
        for (int i = 0; i < 32; i++) h.Send(DisplayMsg.StreamData, shown);
        var m = AllocationWindow.Measure("hardware stream frames", () =>
        {
            for (int i = 0; i < 1000; i++) h.Send(DisplayMsg.StreamData, shown);
        }, report: output.WriteLine);
        output.WriteLine($"1000 shown frames: {m.Bytes} B");
        h.Send(DisplayMsg.StreamClip, hidden);
        for (int i = 0; i < 32; i++) h.Send(DisplayMsg.StreamData, shown);
        var m2 = AllocationWindow.Measure("hidden hardware stream frames", () =>
        {
            for (int i = 0; i < 1000; i++) h.Send(DisplayMsg.StreamData, shown);
        }, report: output.WriteLine);
        output.WriteLine($"1000 hidden frames: {m2.Bytes} B");
    }

    // ---------------- channel capture (as Lz4ImageTests) ----------------

    sealed class Capture : Stream
    {
        readonly List<byte> _bytes = [];
        public byte[] Snapshot() { lock (_bytes) return [.. _bytes]; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
        { lock (_bytes) _bytes.AddRange(b.ToArray()); return ValueTask.CompletedTask; }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
    }

    static SpiceChannel Display(Stream stream, params int[] serverCaps) =>
        (SpiceChannel)typeof(SpiceChannel).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([stream, new ChannelId(ChannelType.Display, 0), new LinkReply(LinkError.Ok, [], new CapabilitySet(CommonCap.MiniHeader),
                new CapabilitySet(serverCaps))]);

    static readonly byte[] Sentinel = [0xE7, 0x03, 0, 0, 0, 0];

    static async Task<byte[]> WrittenBeforeSentinel(SpiceChannel channel, Capture capture)
    {
        channel.Send(999, []);
        static bool Done(byte[] b) => b.AsSpan().EndsWith(Sentinel);
        byte[] all = capture.Snapshot();
        for (int i = 0; i < 500 && !Done(all); i++) { await Task.Delay(10); all = capture.Snapshot(); }
        Assert.True(Done(all), "the channel's writer never wrote the sentinel");
        return all[..^Sentinel.Length];
    }
}
