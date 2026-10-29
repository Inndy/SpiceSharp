using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Spice.Canvas;
using Spice.Native;
using Spice.Session;
using Spice.Transport;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

// Reflection targets the untrimmed test build, not a published Native AOT executable.
#pragma warning disable IL2026, IL2075
public sealed class DisplayStreamTests
{
    const int W = 3, H = 2;
    const uint Black = 0xFF000000;
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly SpiceRect Destination = new(2, 1, 5, 3);
    static readonly Lazy<byte[]> Jpeg = new(EncodeJpeg);
    static FieldInfo Field(string name) => typeof(SpiceSession).GetField(name, PrivateInstance)!;
    static T Limit<T>(string name) => (T)typeof(SpiceSession)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
    static IDictionary Streams(SpiceSession s) => (IDictionary)Field("_streams").GetValue(s)!;
    static object StreamState(SpiceSession s, uint id = 7) => Streams(s)[id]!;
    static T State<T>(SpiceSession s, string name, uint id = 7)
        => (T)StreamState(s, id).GetType().GetField(name)!.GetValue(StreamState(s, id))!;
    static DecodedImage? Frame(SpiceSession s, uint id = 7) => State<DecodedImage?>(s, "Frame", id);
    static byte[] Bytes(Action<BinaryWriter> write)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }
    static void Rect(BinaryWriter w, SpiceRect r)
    { w.Write(r.Top); w.Write(r.Left); w.Write(r.Bottom); w.Write(r.Right); }
    static void Clip(BinaryWriter w, SpiceRect[]? rects)
    {
        w.Write(rects is null ? SpiceClip.TypeNone : SpiceClip.TypeRects);
        if (rects is null) return;
        w.Write((uint)rects.Length); foreach (var r in rects) Rect(w, r);
    }
    static byte[] Create(uint id = 7, uint surface = 0, bool topDown = true,
        VideoCodecType codec = VideoCodecType.Mjpeg, SpiceRect? dest = null, SpiceRect[]? clip = null)
        => Bytes(w =>
        {
            w.Write(surface); w.Write(id); w.Write(topDown ? StreamCreate.FlagTopDown : (byte)0);
            w.Write((byte)codec); w.Write(0x123456789ABCDEF0UL);
            w.Write((uint)W); w.Write((uint)H); w.Write((uint)W); w.Write((uint)H);
            Rect(w, dest ?? Destination); Clip(w, clip);
        });
    static byte[] Data(byte[]? jpeg = null, uint id = 7, uint mm = 100, SpiceRect? sizedDest = null)
        => Bytes(w =>
        {
            w.Write(id); w.Write(mm);
            if (sizedDest is { } dest) { w.Write((uint)W); w.Write((uint)H); Rect(w, dest); }
            var payload = jpeg ?? Jpeg.Value; w.Write((uint)payload.Length); w.Write(payload);
        });
    static void Dispatch(SpiceSession s, ushort type, byte[] message)
    {
        try { typeof(SpiceSession).GetMethod("OnDisplayMessage", PrivateInstance)!.Invoke(s, [type, new ReadOnlyMemory<byte>(message)]); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
    static SpiceSession Session()
    {
        var s = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        Surface(s, 0, true); return s;
    }
    static void Surface(SpiceSession s, uint id, bool primary)
        => Dispatch(s, DisplayMsg.SurfaceCreate, Bytes(w =>
        { w.Write(id); w.Write(10u); w.Write(8u); w.Write(32u); w.Write(primary ? 1u : 0u); }));
    static uint[] Pixels(Surface surface) => Enumerable.Range(0, surface.Height).SelectMany(y => surface.Row(y).ToArray()).ToArray();
    static uint[] ReferencePixels()
    {
        using var decoder = new JpegDecoder(); var bytes = new byte[W * H * 4];
        Assert.True(decoder.TryDecode(Jpeg.Value, bytes, W, H, W * 4));
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
    }
    unsafe delegate int Compress(nint handle, byte* pixels, int width, int stride, int height, int format, ref nint output, ref nuint size);
    static T Native<T>(string name) where T : Delegate
        => typeof(JpegDecoder).Assembly.GetType("Spice.Native.CodecApi", true)!
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<T>();
    static unsafe byte[] EncodeJpeg()
    {
        // Distinct upper/lower rows expose vertical orientation, channel order and placement mistakes.
        byte[] pixels = [20, 40, 220, 255, 20, 40, 220, 255, 20, 40, 220, 255,
                         210, 170, 30, 255, 210, 170, 30, 255, 210, 170, 30, 255];
        // CodecApi is internal and only friends Spice.Native.Tests; bind its existing methods without production edits.
        var init = Native<Func<int, nint>>("tj3Init"); var set = Native<Func<nint, int, int, int>>("tj3Set");
        var compress = Native<Compress>("tj3Compress8"); var free = Native<Action<nint>>("tj3Free");
        var destroy = Native<Action<nint>>("tj3Destroy");
        nint compressor = init(0), output = 0; nuint size = 0;
        Assert.NotEqual(0, compressor);
        try
        {
            Assert.Equal(0, set(compressor, 3, 100)); // quality
            Assert.Equal(0, set(compressor, 4, 0)); // 4:4:4
            fixed (byte* src = pixels)
                Assert.Equal(0, compress(compressor, src, W, W * 4, H, 3, ref output, ref size));
            return new ReadOnlySpan<byte>((void*)output, checked((int)size)).ToArray();
        }
        finally { if (output != 0) free(output); destroy(compressor); }
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task StreamDataBlitsOnlyItsDestinationAndHonorsOrientation(bool topDown)
    {
        await using var s = Session(); var invalidated = new List<SpiceRect>(); s.Invalidated += invalidated.Add;
        Dispatch(s, DisplayMsg.StreamCreate, Create(topDown: topDown));
        Dispatch(s, DisplayMsg.StreamData, Data());
        uint[] expected = ReferencePixels(); Assert.NotEqual(expected[0], expected[W]);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 10; x++)
        {
            bool inside = x >= 2 && x < 5 && y >= 1 && y < 3;
            uint pixel = inside ? expected[(topDown ? y - 1 : H - y) * W + x - 2] : Black;
            Assert.Equal(pixel, s.Canvas.Primary!.Row(y)[x]);
        }
        Assert.Equal(new[] { Destination }, invalidated);
        Assert.Equal(1u, State<uint>(s, "Frames")); Assert.Equal(0u, State<uint>(s, "Drops"));
    }

    [Fact]
    public async Task StreamTargetsTheSpecifiedNonPrimarySurface()
    {
        await using var s = Session(); Surface(s, 9, false); int invalidations = 0; s.Invalidated += _ => invalidations++;
        Dispatch(s, DisplayMsg.StreamCreate, Create(surface: 9)); Dispatch(s, DisplayMsg.StreamData, Data());
        var surfaces = (IDictionary)typeof(Spice.Canvas.Canvas).GetField("_surfaces", PrivateInstance)!.GetValue(s.Canvas)!;
        var target = (Surface)surfaces[9u]!;
        Assert.Equal(ReferencePixels()[0], target.Row(1)[2]);
        Assert.All(Pixels(s.Canvas.Primary!), p => Assert.Equal(Black, p)); Assert.Equal(0, invalidations);
    }

    [Fact]
    public async Task SizedDataChangesDestinationAndScalesSubsequentFramesThere()
    {
        await using var s = Session(); var dest = new SpiceRect(2, 2, 8, 6);
        var invalidated = new List<SpiceRect>(); s.Invalidated += invalidated.Add;
        Dispatch(s, DisplayMsg.StreamCreate, Create(dest: new SpiceRect(0, 0, 3, 2)));
        byte[] message = Data(sizedDest: dest);
        var parsed = StreamData.Parse(message, true);
        Assert.Equal((uint)W, parsed.Width); Assert.Equal((uint)H, parsed.Height); Assert.Equal(dest, parsed.Dest);
        Assert.Equal(Jpeg.Value, parsed.Data.ToArray());
        Dispatch(s, DisplayMsg.StreamDataSized, message);
        Dispatch(s, DisplayMsg.StreamData, Data(mm: 101));
        Assert.Equal(dest, State<SpiceRect>(s, "Dest")); Assert.Equal(new[] { dest, dest }, invalidated);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 10; x++)
            if (x >= 2 && x < 8 && y >= 2 && y < 6) Assert.NotEqual(Black, s.Canvas.Primary!.Row(y)[x]);
            else Assert.Equal(Black, s.Canvas.Primary!.Row(y)[x]);
        // Scaling still keeps the red upper row above the blue lower row.
        Assert.True(((s.Canvas.Primary!.Row(2)[2] >> 16) & 255) > 180);
        Assert.True((s.Canvas.Primary.Row(5)[2] & 255) > 180);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CreateAndClipOwnTheirRectanglesAfterReceiveBufferReuse(bool updateClip)
    {
        await using var s = Session();
        SpiceRect[] clips = [new(2, 1, 3, 2), new(4, 2, 5, 3)];
        byte[] message = updateClip ? Bytes(w => { w.Write(7u); Clip(w, clips); }) : Create(clip: clips);
        if (updateClip) Dispatch(s, DisplayMsg.StreamCreate, Create());
        Dispatch(s, updateClip ? DisplayMsg.StreamClip : DisplayMsg.StreamCreate, message);
        Array.Fill(message, (byte)0); // RunAsync reuses its payload buffer after the handler returns.
        Dispatch(s, DisplayMsg.StreamData, Data()); uint[] expected = ReferencePixels();
        for (int y = 0; y < 8; y++) for (int x = 0; x < 10; x++)
            Assert.Equal((x, y) switch { (2, 1) => expected[0], (4, 2) => expected[5], _ => Black }, s.Canvas.Primary!.Row(y)[x]);
    }

    [Fact]
    public async Task ClipUpdatesReplacePriorClipAndEmptyRectsSuppressDrawing()
    {
        await using var s = Session(); Dispatch(s, DisplayMsg.StreamCreate, Create(clip: [new(2, 1, 3, 2)]));
        Dispatch(s, DisplayMsg.StreamClip, Bytes(w => { w.Write(7u); Clip(w, []); }));
        Dispatch(s, DisplayMsg.StreamData, Data()); Assert.All(Pixels(s.Canvas.Primary!), p => Assert.Equal(Black, p));
        Dispatch(s, DisplayMsg.StreamClip, Bytes(w => { w.Write(7u); Clip(w, null); }));
        Dispatch(s, DisplayMsg.StreamData, Data()); Assert.Equal(ReferencePixels()[5], s.Canvas.Primary!.Row(2)[4]);
        Assert.Equal(0u, State<uint>(s, "Drops"));
    }

    [Fact]
    public async Task DestroyAndDestroyAllReleaseRetainedFramesAndIgnoreLaterData()
    {
        await using var s = Session();
        foreach (uint id in new uint[] { 7, 8, 9 }) { Dispatch(s, DisplayMsg.StreamCreate, Create(id)); Dispatch(s, DisplayMsg.StreamData, Data(id: id)); }
        var first = Frame(s)!; var others = new[] { Frame(s, 8)!, Frame(s, 9)! };
        Dispatch(s, DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u)));
        Assert.Empty(first.Pixels); Assert.Equal(2, Streams(s).Count); Assert.All(others, f => Assert.NotEmpty(f.Pixels));
        Dispatch(s, DisplayMsg.StreamDestroyAll, []);
        Assert.Empty(Streams(s)); Assert.All(others, f => Assert.Empty(f.Pixels));
        var before = Pixels(s.Canvas.Primary!); Dispatch(s, DisplayMsg.StreamData, Data());
        Assert.Equal(before, Pixels(s.Canvas.Primary!));
        Dispatch(s, DisplayMsg.StreamDestroy, Bytes(w => w.Write(999u))); Dispatch(s, DisplayMsg.StreamDestroyAll, []);
    }

    [Fact]
    public async Task RecreatingStreamIdReleasesOldFrame()
    {
        await using var s = Session(); Dispatch(s, DisplayMsg.StreamCreate, Create()); Dispatch(s, DisplayMsg.StreamData, Data());
        var old = Frame(s)!; Dispatch(s, DisplayMsg.StreamCreate, Create());
        Assert.Empty(old.Pixels); Assert.Null(Frame(s)); Assert.Single(Streams(s));
        Dispatch(s, DisplayMsg.StreamData, Data()); Assert.NotNull(Frame(s)); Assert.NotSame(old, Frame(s));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task UnknownStreamDataIsIgnoredWithoutConstructingDecoder(bool sized)
    {
        await using var s = Session();
        Dispatch(s, sized ? DisplayMsg.StreamDataSized : DisplayMsg.StreamData, Data(sizedDest: sized ? Destination : null));
        Dispatch(s, DisplayMsg.StreamClip, Bytes(w => { w.Write(7u); Clip(w, null); }));
        Dispatch(s, DisplayMsg.StreamActivateReport, Bytes(w => { w.Write(7u); w.Write(1u); w.Write(1u); w.Write(100u); }));
        Assert.Empty(Streams(s)); Assert.Null(Field("_mjpeg").GetValue(s)); Assert.All(Pixels(s.Canvas.Primary!), p => Assert.Equal(Black, p));
    }

    [Theory] [InlineData(VideoCodecType.Vp8)] [InlineData(VideoCodecType.H264)] [InlineData(VideoCodecType.Vp9)] [InlineData(VideoCodecType.H265)]
    public async Task UnsupportedCodecDoesNotDecodeEvenAValidJpeg(VideoCodecType codec)
    {
        await using var s = Session(); Dispatch(s, DisplayMsg.StreamCreate, Create(codec: codec)); Dispatch(s, DisplayMsg.StreamData, Data());
        Assert.Null(Field("_mjpeg").GetValue(s)); Assert.Null(Frame(s));
        Assert.Equal(1u, State<uint>(s, "Drops")); Assert.Equal(1u, State<uint>(s, "Frames"));
        Assert.All(Pixels(s.Canvas.Primary!), p => Assert.Equal(Black, p));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CorruptJpegDropsOnlyThatFrameAndNextGoodFrameStillDecodes(bool validHeader)
    {
        await using var s = Session(); Dispatch(s, DisplayMsg.StreamCreate, Create()); Dispatch(s, DisplayMsg.StreamData, Data());
        byte[] bad = [1, 2, 3];
        if (validHeader)
        {
            // Retain the scan header, but remove all entropy data and EOI: header succeeds, decode fails.
            byte[] jpeg = Jpeg.Value; int sos = FindMarker(jpeg, 0xDA);
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(sos + 2)); bad = jpeg[..(sos + 2 + length)];
            using var decoder = new JpegDecoder(); Assert.True(decoder.TryReadHeader(bad, out _, out _));
        }
        var before = Pixels(s.Canvas.Primary!); var frame = Frame(s);
        Dispatch(s, DisplayMsg.StreamData, Data(bad, mm: 101));
        Assert.Equal(before, Pixels(s.Canvas.Primary!)); Assert.Same(frame, Frame(s));
        Assert.Equal(1u, State<uint>(s, "Drops")); Assert.Equal(2u, State<uint>(s, "Frames"));
        Dispatch(s, DisplayMsg.StreamData, Data(mm: 102));
        Assert.Equal(before, Pixels(s.Canvas.Primary!)); Assert.Equal(3u, State<uint>(s, "Frames")); Assert.Equal(1u, State<uint>(s, "Drops"));
    }

    static int FindMarker(byte[] jpeg, byte marker)
    {
        for (int i = 0; i < jpeg.Length - 1; i++) if (jpeg[i] == 0xFF && jpeg[i + 1] == marker) return i;
        throw new InvalidOperationException($"Missing JPEG marker {marker:X2}");
    }

    [Fact]
    public async Task MaxStreamsRejectsNewIdButAllowsReplacementAndReuseAfterDestroy()
    {
        await using var s = Session();
        int limit = Limit<int>("MaxStreams"); Assert.Equal(64, limit);
        for (uint id = 0; id < limit; id++) Dispatch(s, DisplayMsg.StreamCreate, Create(id));
        Assert.Equal(limit, Streams(s).Count); Dispatch(s, DisplayMsg.StreamCreate, Create(1000));
        Assert.Equal(limit, Streams(s).Count); Assert.False(Streams(s).Contains(1000u));
        Dispatch(s, DisplayMsg.StreamData, Data()); var old = Frame(s)!;
        Dispatch(s, DisplayMsg.StreamCreate, Create()); Assert.Empty(old.Pixels); Assert.Equal(limit, Streams(s).Count);
        Dispatch(s, DisplayMsg.StreamDestroy, Bytes(w => w.Write(7u))); Dispatch(s, DisplayMsg.StreamCreate, Create(1000));
        Assert.True(Streams(s).Contains(1000u)); Assert.Equal(limit, Streams(s).Count);
    }

    [Theory] [InlineData(0)] [InlineData(4)]
    public async Task DecodedFrameBudgetAcceptsExactBoundaryAndRejectsOnePixelOver(int excess)
    {
        await using var s = Session(); Dispatch(s, DisplayMsg.StreamCreate, Create(8)); Dispatch(s, DisplayMsg.StreamCreate, Create());
        long limit = Limit<long>("MaxStreamFrameBytes"); Assert.Equal(256L << 20, limit);
        // Seed only accounting metadata, as AgentQueueTests seeds queue accounting. No 256 MiB rent.
        // This frame is never decoded/blitted; Release still returns its real tiny pooled buffer.
        var retained = new DecodedImage(1, 1, false);
        typeof(DecodedImage).GetField("<Height>k__BackingField", PrivateInstance)!
            .SetValue(retained, checked((int)((limit - W * H * 4 + excess) / 4)));
        StreamState(s, 8).GetType().GetField("Frame")!.SetValue(StreamState(s, 8), retained);
        Dispatch(s, DisplayMsg.StreamData, Data());
        Assert.Equal(excess == 0 ? 0u : 1u, State<uint>(s, "Drops"));
        if (excess == 0) Assert.NotNull(Frame(s)); else Assert.Null(Frame(s));
        Dispatch(s, DisplayMsg.StreamDestroy, Bytes(w => w.Write(8u))); Assert.Empty(retained.Pixels);
        // Releasing retained accounting makes space again, including after a rejected frame.
        Dispatch(s, DisplayMsg.StreamData, Data()); Assert.NotNull(Frame(s));
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task OversizedJpegHeaderIsDroppedBeforeFrameAllocation(bool width)
    {
        await using var s = Session(); Dispatch(s, DisplayMsg.StreamCreate, Create());
        var jpeg = (byte[])Jpeg.Value.Clone(); int sof = FindMarker(jpeg, 0xC0);
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(sof + (width ? 7 : 5)), 8193);
        using var decoder = new JpegDecoder(); Assert.True(decoder.TryReadHeader(jpeg, out _, out _));
        Dispatch(s, DisplayMsg.StreamData, Data(jpeg)); Assert.Null(Frame(s)); Assert.Equal(1u, State<uint>(s, "Drops"));
    }

    [Fact]
    public async Task ActivatedReportsContainWindowCountsDropsIdsAndTimestamps()
    {
        await using var s = Session(); using var output = new ReportSink();
        var ctor = typeof(SpiceChannel).GetConstructors(PrivateInstance).Single();
        await using var channel = (SpiceChannel)ctor.Invoke([output, new ChannelId(ChannelType.Display, 0),
            new LinkReply(LinkError.Ok, [], new CapabilitySet(CommonCap.MiniHeader), new CapabilitySet())]);
        Field("_display").SetValue(s, channel);
        Dispatch(s, DisplayMsg.StreamCreate, Create()); Dispatch(s, DisplayMsg.StreamData, Data([1, 2, 3], mm: 1));
        byte[] activate = Bytes(w => { w.Write(7u); w.Write(0xABCDEF12u); w.Write(3u); w.Write(1000u); });
        Assert.Equal((7u, 0xABCDEF12u, 3u, 1000u), DisplayParse.StreamActivateReport(activate));
        Dispatch(s, DisplayMsg.StreamActivateReport, activate);
        Dispatch(s, DisplayMsg.StreamData, Data(mm: 100)); Dispatch(s, DisplayMsg.StreamData, Data([1, 2, 3], mm: 110));
        Assert.Empty(output.Snapshot());
        Dispatch(s, DisplayMsg.StreamData, Data(mm: 120));
        for (uint mm = 200; mm <= 220; mm += 10) Dispatch(s, DisplayMsg.StreamData, Data(mm: mm));
        await output.TwoReports.Task.WaitAsync(TimeSpan.FromSeconds(5)); byte[] bytes = output.Snapshot();
        Assert.Equal(76, bytes.Length);
        for (int report = 0; report < 2; report++)
        {
            int offset = report * 38; var header = DataHeader.Parse(bytes.AsSpan(offset, 6), true);
            Assert.Equal(DisplayMsg.CStreamReport, header.Type); Assert.Equal(32u, header.Size);
            uint[] fields = Enumerable.Range(0, 8).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 6 + 4 * i))).ToArray();
            Assert.Equal(new uint[] { 7, 0xABCDEF12, report == 0 ? 100u : 200u, report == 0 ? 120u : 220u, 3, report == 0 ? 1u : 0u, 0, 0 }, fields);
        }
        Assert.Equal(0u, State<uint>(s, "Frames")); Assert.Equal(0u, State<uint>(s, "Drops"));
    }

    sealed class ReportSink : MemoryStream
    {
        readonly object sync = new();
        public readonly TaskCompletionSource TwoReports = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Snapshot() { lock (sync) return ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            lock (sync) { base.Write(buffer.Span); if (Length >= 76) TwoReports.TrySetResult(); }
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void StreamParsersRejectEveryTruncatedFixedHeaderAndPayload()
    {
        byte[] create = Create(clip: [Destination]); byte[] data = Data(); byte[] sized = Data(sizedDest: Destination);
        byte[] clip = Bytes(w => { w.Write(7u); Clip(w, [Destination]); });
        byte[] report = Bytes(w => { w.Write(7u); w.Write(1u); w.Write(2u); w.Write(3u); });
        foreach (var (bytes, parse) in new (byte[], Action<ReadOnlyMemory<byte>>)[]
        {
            (create, b => StreamCreate.Parse(b)), (data, b => StreamData.Parse(b, false)),
            (sized, b => StreamData.Parse(b, true)), (clip, b => DisplayParse.StreamClip(b)),
            (report, b => DisplayParse.StreamActivateReport(b)),
        })
        {
            parse(bytes);
            for (int length = 0; length < bytes.Length; length++)
                Assert.Throws<WireException>(() => parse(bytes.AsMemory(0, length)));
        }
        var parsed = StreamCreate.Parse(create);
        Assert.Equal(0u, parsed.SurfaceId); Assert.Equal(7u, parsed.Id); Assert.Equal(VideoCodecType.Mjpeg, parsed.Codec);
        Assert.Equal(StreamCreate.FlagTopDown, parsed.Flags); Assert.Equal(Destination, parsed.Dest);
        Assert.Equal(((uint)W, (uint)H, (uint)W, (uint)H), (parsed.StreamWidth, parsed.StreamHeight, parsed.SrcWidth, parsed.SrcHeight));
        Assert.Equal(Destination, parsed.Clip[0]); Assert.Equal((7u, 100u), (StreamData.Parse(data, false).Id, StreamData.Parse(data, false).MultiMediaTime));
        Assert.Equal(Destination, DisplayParse.StreamClip(clip).Clip[0]);
    }
}
