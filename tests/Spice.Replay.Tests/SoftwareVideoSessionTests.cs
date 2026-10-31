using System.Collections;
using System.Reflection;
using Spice.Canvas;
using Spice.Native;
using Spice.Session;
using Spice.Tests;
using Spice.Wire;
using VvFile;
using Xunit.Abstractions;

namespace Spice.Replay.Tests;

// Routing between hardware and software video decoding (docs/decisions/0037, amended 2026-10-31): a fake hardware
// factory that takes 8-bit 4:2:0 only, as the probed DXVA profiles do, and the real libavcodec software path, fed the
// committed libx264 clips (tests/fixtures/h264).
#pragma warning disable IL2026, IL2075
[Collection("Allocation profiling")]
public sealed class SoftwareVideoSessionTests(ITestOutputHelper output)
{
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    const uint Epoch = 1000;
    const int W = H264Fixtures.Width, H = H264Fixtures.Height;

    sealed class FakeHardwareDecoder : IVideoDecoder
    {
        public VideoDecodeResult Next = VideoDecodeResult.Picture;
        public bool Disposed;
        public int Frames;
        public bool NeedsEveryFrame => true;
        public bool Takes(in VideoFormat format) => FakeHardware.Takes(format);
        public VideoDecodeResult Decode(ReadOnlySpan<byte> data, VideoOutput? output)
        {
            Frames++;
            if (Next != VideoDecodeResult.Picture || output is null) return Next == VideoDecodeResult.Picture ? VideoDecodeResult.NoPicture : Next;
            var px = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(output.Begin(output.DestinationWidth, output.DestinationHeight, mirrored: false));
            px.Fill(0xFF00FF00);
            return VideoDecodeResult.Picture;
        }
        public void Dispose() => Disposed = true;
    }

    /// <summary>Takes what H264_VLD_NOFGT covers, like HardwareVideo.CanDecode.</summary>
    sealed class FakeHardware(params VideoCodecType[] codecs) : IVideoDecoderFactory
    {
        public readonly List<FakeHardwareDecoder> Made = [];
        public VideoDecodeResult FirstResult = VideoDecodeResult.Picture;
        public Task<IReadOnlyList<VideoCodecType>> Codecs { get; set; } = Task.FromResult<IReadOnlyList<VideoCodecType>>(codecs);
        public static bool Takes(in VideoFormat f) => f.Codec == VideoCodecType.H264 && f.Profile is 66 or 77 or 100 && f.ChromaFormat == 1 && f.BitDepth == 8;
        public bool CanDecode(in VideoFormat format) => Takes(format);
        public IVideoDecoder? Create(VideoCodecType codec, int width, int height)
        {
            var d = new FakeHardwareDecoder { Next = FirstResult };
            Made.Add(d);
            return d;
        }
    }

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
    static byte[] Create(uint id = 7, bool topDown = true) => Bytes(w =>
    {
        w.Write(0u); w.Write(id); w.Write(topDown ? StreamCreate.FlagTopDown : (byte)0); w.Write((byte)VideoCodecType.H264); w.Write(0UL);
        w.Write((uint)W); w.Write((uint)H); w.Write((uint)W); w.Write((uint)H);
        w.Write(0); w.Write(0); w.Write(H); w.Write(W); // top, left, bottom, right
        w.Write(SpiceClip.TypeNone);
    });
    static byte[] Data(byte[] payload, uint id = 7) => Bytes(w => { w.Write(id); w.Write(Epoch); w.Write((uint)payload.Length); w.Write(payload); });
    static byte[] Destroy(uint id = 7) => BitConverter.GetBytes(id);

    sealed class Harness : IAsyncDisposable
    {
        public readonly FakeHardware Hardware;
        public readonly VideoDecoderRouter Router;
        public readonly SpiceSession Session;
        public Harness(FakeHardware? hardware = null, bool software = true)
        {
            Hardware = hardware ?? new FakeHardware(VideoCodecType.H264);
            Router = new VideoDecoderRouter(Hardware, new SoftwareVideo()) { SoftwareEnabled = software };
            Session = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n")) { VideoDecoders = Router };
            Session.PacingClock = new ManualClock();
            Session.VideoPacing = VideoPacing.Off;
            Session.MultimediaTimeBase(Epoch);
            Field("_videoCodecs").SetValue(Session, new[] { VideoCodecType.H264 });
            Send(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write((uint)W); w.Write((uint)H); w.Write(32u); w.Write(1u); }));
        }
        public void Send(ushort type, byte[] body) => Session.OnDisplayMessage(type, body);
        public object? Stream(uint id = 7) => ((IDictionary)Field("_streams").GetValue(Session)!)[id];
        public T State<T>(string name, uint id = 7) { var s = Stream(id)!; return (T)s.GetType().GetField(name)!.GetValue(s)!; }
        public IVideoDecoder? Decoder(uint id = 7) => State<IVideoDecoder?>("Decoder", id);
        public bool Avoided => (int)Field("_avoidedCodecs").GetValue(Session)! != 0;
        public uint Pixel(int x, int y) => Session.Canvas.Primary!.Row(y)[x];
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    static readonly VideoFormat High444 = new(VideoCodecType.H264, 244, 3, 8);
    static readonly VideoFormat High420 = new(VideoCodecType.H264, 100, 1, 8);

    // ---------------- the router ----------------

    [Fact]
    public async Task H264IsAdvertisedWhenEitherPathDecodesIt()
    {
        Assert.True(SoftwareVideo.IsAvailable);
        var none = new FakeHardware();
        Assert.Equal([VideoCodecType.H264], await new VideoDecoderRouter(none, new SoftwareVideo()).Codecs);
        Assert.Empty(await new VideoDecoderRouter(none, new SoftwareVideo()) { SoftwareEnabled = false }.Codecs);
        Assert.Equal([VideoCodecType.H264], await new VideoDecoderRouter(new FakeHardware(VideoCodecType.H264), new SoftwareVideo()).Codecs);
        Assert.Equal([VideoCodecType.H264, VideoCodecType.Vp9],
            await new VideoDecoderRouter(new FakeHardware(VideoCodecType.H264, VideoCodecType.Vp9), new SoftwareVideo()) { SoftwareEnabled = false }.Codecs);
        // A failed hardware probe leaves the software path.
        var broken = new FakeHardware { Codecs = Task.FromException<IReadOnlyList<VideoCodecType>>(new InvalidOperationException("no MF")) };
        Assert.Equal([VideoCodecType.H264], await new VideoDecoderRouter(broken, new SoftwareVideo()).Codecs);
        // The menu waits for the hardware probe.
        var pending = new TaskCompletionSource<IReadOnlyList<VideoCodecType>>();
        var router = new VideoDecoderRouter(new FakeHardware { Codecs = pending.Task }, new SoftwareVideo());
        Assert.Null(router.KnownCodecs);
        pending.SetResult([]);
        Assert.Equal([VideoCodecType.H264], router.KnownCodecs);
    }

    [Fact]
    public void EachFormatGoesToThePathThatTakesIt()
    {
        var hw = new FakeHardware(VideoCodecType.H264);
        var router = new VideoDecoderRouter(hw, new SoftwareVideo());
        Assert.True(router.CanDecode(High444));
        Assert.True(router.CanDecodeInSoftware(High444));
        Assert.True(router.CanDecode(High420));
        Assert.False(router.CanDecode(High444 with { BitDepth = 10 }));
        using (var d = router.Create(High444, W, H, softwareOnly: false)) Assert.True(d is { Software: true });
        using (var d = router.Create(High420, W, H, softwareOnly: false)) Assert.IsType<FakeHardwareDecoder>(d);
        using (var d = router.Create(High420, W, H, softwareOnly: true)) Assert.True(d is { Software: true });
        // Unknown format: hardware first, as before.
        using (var d = router.Create(VideoCodecType.H264, W, H)) Assert.IsType<FakeHardwareDecoder>(d);
        router.SoftwareEnabled = false;
        Assert.False(router.CanDecode(High444));
        Assert.Null(router.Create(High444, W, H, softwareOnly: false));
        Assert.True(router.CanDecode(High420));
        // No hardware H.264 at all: software takes every stream.
        var swOnly = new VideoDecoderRouter(new FakeHardware(), new SoftwareVideo());
        using (var d = swOnly.Create(VideoCodecType.H264, W, H)) Assert.True(d is { Software: true });
        Assert.True(swOnly.CanDecode(High420));
    }

    // ---------------- the session ----------------

    void AssertPicture(Harness h, int frame, bool flipped = false)
    {
        foreach (var (x, y) in H264Fixtures.SamplePoints(frame))
        {
            uint px = h.Pixel(x, flipped ? H - 1 - y : y);
            int diff = H264Fixtures.Difference(px, H264Fixtures.ColorAt(frame, x, y));
            Assert.True(diff <= 6, $"frame {frame} ({x},{y}): {px:X8} off by {diff}");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A444StreamMovesFromHardwareToSoftwareAndIsShown(bool topDown)
    {
        await using var h = new Harness();
        var units = H264Fixtures.AccessUnits("clip-444.h264");
        h.Send(DisplayMsg.StreamCreate, Create(topDown: topDown));
        var first = Assert.IsType<FakeHardwareDecoder>(h.Decoder()); // the format isn't known yet
        for (int i = 0; i < units.Length; i++)
        {
            h.Send(DisplayMsg.StreamData, Data(units[i]));
            // A bottom-up stream shows its pictures flipped, as spice-gtk does for every codec.
            AssertPicture(h, i, flipped: !topDown);
        }
        Assert.True(first.Disposed);
        Assert.Equal(0, first.Frames);
        Assert.True(h.Decoder() is { Software: true });
        Assert.False(h.Avoided);
        Assert.False(h.State<bool>("Unsupported"));
        Assert.Equal("H.264 High 4:4:4 Predictive (profile 244), 4:4:4, 8-bit, software", h.Session.VideoDecoding);
        // The next stream is made for the known format straight away: no hardware decoder this time.
        h.Send(DisplayMsg.StreamDestroy, Destroy());
        h.Send(DisplayMsg.StreamCreate, Create(id: 8));
        Assert.Single(h.Hardware.Made);
        Assert.True(h.Decoder(8) is { Software: true });
        h.Send(DisplayMsg.StreamData, Data(units[0], id: 8));
        AssertPicture(h, 0);
    }

    [Fact]
    public async Task A420StreamAfterA444StreamReturnsToHardware()
    {
        await using var h = new Harness();
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Send(DisplayMsg.StreamData, Data(H264Fixtures.AccessUnits("clip-444.h264")[0]));
        Assert.True(h.Decoder() is { Software: true });
        h.Send(DisplayMsg.StreamDestroy, Destroy());

        h.Send(DisplayMsg.StreamCreate, Create(id: 8));
        // The last stream's format is only a hint. This stream's SPS declares 4:2:0,
        // which the hardware factory accepts, and must determine its decoding path.
        h.Send(DisplayMsg.StreamData, Data(H264Fixtures.AccessUnits("clip-420.h264")[0], id: 8));
        Assert.False(h.Avoided);
        Assert.False(h.State<bool>("Unsupported", 8));
        Assert.IsType<FakeHardwareDecoder>(h.Decoder(8));
        Assert.EndsWith(", hardware", h.Session.VideoDecoding);
    }

    [Fact]
    public async Task A420StreamStaysOnTheHardwarePath()
    {
        await using var h = new Harness();
        var units = H264Fixtures.AccessUnits("clip-420.h264");
        h.Send(DisplayMsg.StreamCreate, Create());
        var hw = Assert.IsType<FakeHardwareDecoder>(h.Decoder());
        foreach (var u in units) h.Send(DisplayMsg.StreamData, Data(u));
        Assert.Same(hw, h.Decoder());
        Assert.Equal(units.Length, hw.Frames);
        Assert.Equal(0xFF00FF00u, h.Pixel(10, 10));
        Assert.EndsWith(", hardware", h.Session.VideoDecoding);
    }

    [Fact]
    public async Task WithSoftwareOffA444StreamAvoidsH264AsBefore()
    {
        await using var h = new Harness(software: false);
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Send(DisplayMsg.StreamData, Data(H264Fixtures.AccessUnits("clip-444.h264")[0]));
        Assert.True(h.State<bool>("Unsupported"));
        Assert.True(h.Avoided);
        // Software turned off mid-session after a 4:4:4 stream: the next stream avoids the codec at creation.
        await using var h2 = new Harness();
        h2.Send(DisplayMsg.StreamCreate, Create());
        h2.Send(DisplayMsg.StreamData, Data(H264Fixtures.AccessUnits("clip-444.h264")[0]));
        Assert.False(h2.Avoided);
        h2.Router.SoftwareEnabled = false;
        h2.Send(DisplayMsg.StreamDestroy, Destroy());
        h2.Send(DisplayMsg.StreamCreate, Create(id: 8));
        Assert.True(h2.State<bool>("Unsupported", 8));
        Assert.True(h2.Avoided);
    }

    [Fact]
    public async Task ADeeperStreamThatNeitherPathTakesAvoidsH264()
    {
        await using var h = new Harness();
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Send(DisplayMsg.StreamData, Data(H264Fixtures.AccessUnits("clip-444-10bit.h264")[0]));
        Assert.True(h.State<bool>("Unsupported"));
        Assert.True(h.Avoided);
    }

    [Fact]
    public async Task HardwareFailureAfterTheFormatSearchExpiresStillTriesSoftware()
    {
        var hw = new FakeHardware(VideoCodecType.H264) { FirstResult = VideoDecodeResult.NoPicture };
        await using var h = new Harness(hw);
        h.Send(DisplayMsg.StreamCreate, Create());
        // Valid access-unit delimiters with no SPS/picture exhaust the bounded format search.
        // No decoder has proved the stream un-decodable by software.
        for (int i = 0; i < 8; i++) h.Send(DisplayMsg.StreamData, Data([0, 0, 0, 1, 9, 0xF0]));
        Assert.Equal(0, h.State<int>("FormatChecks"));
        Assert.IsType<FakeHardwareDecoder>(h.Decoder()).Next = VideoDecodeResult.Failed;
        var firstPicture = H264Fixtures.AccessUnits("clip-444.h264")[0];
        h.Send(DisplayMsg.StreamData, Data(firstPicture));
        Assert.True(h.State<bool>("Unsupported"));
        Assert.False(h.Avoided);
        h.Send(DisplayMsg.StreamDestroy, Destroy());
        h.Send(DisplayMsg.StreamCreate, Create(id: 8));
        h.Send(DisplayMsg.StreamData, Data(firstPicture, id: 8));
        Assert.True(h.Decoder(8) is { Software: true });
        AssertPicture(h, 0);
    }

    [Fact]
    public async Task AHardwareDecoderFailingBeforeItsFirstPictureHandsTheCodecToSoftware()
    {
        var hw = new FakeHardware(VideoCodecType.H264) { FirstResult = VideoDecodeResult.Failed };
        await using var h = new Harness(hw);
        var units = H264Fixtures.AccessUnits("clip-420.h264");
        h.Send(DisplayMsg.StreamCreate, Create());
        h.Send(DisplayMsg.StreamData, Data(units[0]));
        Assert.True(h.State<bool>("Unsupported")); // this stream goes to images
        Assert.False(h.Avoided); // but H.264 isn't avoided: software takes the next streams
        h.Send(DisplayMsg.StreamDestroy, Destroy());
        h.Send(DisplayMsg.StreamCreate, Create(id: 8));
        Assert.Single(hw.Made);
        Assert.True(h.Decoder(8) is { Software: true });
        foreach (var u in units) h.Send(DisplayMsg.StreamData, Data(u, id: 8));
        Assert.False(h.State<bool>("Unsupported", 8));
        AssertPicture(h, units.Length - 1);
        // A software decoder failing before a picture: no path is left, so the codec is avoided.
        h.Send(DisplayMsg.StreamDestroy, Destroy(8));
        h.Send(DisplayMsg.StreamCreate, Create(id: 9));
        var junk = new byte[400];
        new Random(7).NextBytes(junk);
        for (int i = 0; i < 40 && !h.State<bool>("Unsupported", 9); i++) h.Send(DisplayMsg.StreamData, Data(junk, id: 9));
        Assert.True(h.State<bool>("Unsupported", 9));
        Assert.True(h.Avoided);
    }

    [Fact]
    public async Task SteadySoftwareStreamFramesAllocateNothingInTheSession()
    {
        await using var h = new Harness();
        var frames = H264Fixtures.AccessUnits("clip-444.h264").Select(u => Data(u)).ToArray();
        h.Send(DisplayMsg.StreamCreate, Create());
        foreach (var f in frames) h.Send(DisplayMsg.StreamData, f);
        Assert.True(h.Decoder() is { Software: true });
        var m = AllocationWindow.Measure("software stream frames", () =>
        {
            foreach (var f in frames) h.Send(DisplayMsg.StreamData, f);
        }, report: output.WriteLine);
        output.WriteLine($"{frames.Length} shown frames: {m.Bytes} B");
        // Hidden by the clip: decoded without a picture.
        h.Send(DisplayMsg.StreamClip, Bytes(w => { w.Write(7u); w.Write(SpiceClip.TypeRects); w.Write(0u); }));
        foreach (var f in frames) h.Send(DisplayMsg.StreamData, f);
        var m2 = AllocationWindow.Measure("hidden software stream frames", () =>
        {
            foreach (var f in frames) h.Send(DisplayMsg.StreamData, f);
        }, report: output.WriteLine);
        output.WriteLine($"{frames.Length} hidden frames: {m2.Bytes} B");
    }
}
