using System.Collections;
using System.Reflection;
using Spice.Session;
using Spice.Wire;
using SpiceViewer.Video;
using VvFile;
using Xunit.Abstractions;

namespace Spice.Video.Tests;

/// <summary>Runs only where spicecodec.dll has the FFmpeg H.264 decoder and the inbox encoder made the clip.</summary>
sealed class SoftwareH264FactAttribute : FactAttribute
{
    public SoftwareH264FactAttribute()
    {
        if (!SoftwareVideo.IsAvailable) Skip = "spicecodec.dll has no software H.264 decoder (sc_h264_*)";
        else if (TestClip.Error is { } e) Skip = e;
    }
}

// docs/decisions/0037, the human (2026-10-31): without any hardware H.264 decoder (WARP, Windows N without the Media
// Feature Pack), H.264 is still advertised and every stream decodes in software whenever spicecodec.dll has the FFmpeg
// decoder; softwareVideo off advertises nothing through software. The real probe runs as on a software adapter, the
// real router and software decoder decode the inbox encoder's 4:2:0 clip through the session. No window, no GPU use.
#pragma warning disable IL2026, IL2075
[Collection("Hardware video")]
public sealed class SoftwareOnlyVideoTests(ITestOutputHelper output)
{
    const int SurfaceW = 800, SurfaceH = 600;
    const int Tolerance = 12;

    static readonly HardwareVideo NoHardware = new(softwareAdapter: true);

    sealed class Harness : IAsyncDisposable
    {
        public readonly SpiceSession Session;
        public Harness(VideoDecoderRouter router)
        {
            Session = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n")) { VideoDecoders = router };
            Session.OnDisplayMessage(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write((uint)SurfaceW); w.Write((uint)SurfaceH); w.Write(32u); w.Write(1u); }));
        }

        /// <summary>The session's own advertising step (what the display channel link runs), returning what it advertised.</summary>
        public Task<VideoCodecType[]> Advertise() => (Task<VideoCodecType[]>)typeof(SpiceSession)
            .GetMethod("DecodableVideoCodecs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Session, [CancellationToken.None])!;

        public void Send(ushort type, byte[] body) => Session.OnDisplayMessage(type, body);
        public void Frame(int i) => Send(DisplayMsg.StreamData, Bytes(w =>
        {
            var unit = TestClip.Frames[i];
            w.Write(7u); w.Write((uint)(1000 + i * 33)); w.Write((uint)unit.Length); w.Write(unit);
        }));
        object Stream() => ((IDictionary)typeof(SpiceSession).GetField("_streams", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Session)!)[7u]!;
        public T State<T>(string name) { var s = Stream(); return (T)s.GetType().GetField(name)!.GetValue(s)!; }
        public uint Pixel(int x, int y) => Session.Canvas.Primary!.Row(y)[x];
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    static byte[] Bytes(Action<BinaryWriter> write)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }

    static byte[] Create(SpiceRect dest) => Bytes(w =>
    {
        w.Write(0u); w.Write(7u); w.Write(StreamCreate.FlagTopDown); w.Write((byte)VideoCodecType.H264); w.Write(0UL);
        w.Write((uint)TestClip.Width); w.Write((uint)TestClip.Height); w.Write((uint)TestClip.Width); w.Write((uint)TestClip.Height);
        w.Write(dest.Top); w.Write(dest.Left); w.Write(dest.Bottom); w.Write(dest.Right);
        w.Write(SpiceClip.TypeNone);
    });

    [Fact]
    public async Task TheProbeFindsNoHardwareAndSaysSoftwareMayStillDecode()
    {
        Assert.Empty(await NoHardware.Codecs);
        Assert.Null(NoHardware.Create(VideoCodecType.H264, 320, 240));
        var router = new VideoDecoderRouter(NoHardware, new SoftwareVideo());
        Assert.Empty(router.HardwareCodecs);
        if (SoftwareVideo.IsAvailable)
        {
            Assert.Equal([VideoCodecType.H264], await router.Codecs);
            Assert.Equal([VideoCodecType.H264], router.KnownCodecs);
            // 4:2:0 too: with no hardware decoder, software takes every H.264 format it decodes.
            Assert.True(router.CanDecode(new VideoFormat(VideoCodecType.H264, 100, 1, 8)));
            Assert.True(router.CanDecode(new VideoFormat(VideoCodecType.H264, 244, 3, 8)));
        }
        router.SoftwareEnabled = false;
        Assert.Empty(await router.Codecs);
        Assert.False(router.CanDecode(new VideoFormat(VideoCodecType.H264, 100, 1, 8)));
        Assert.Null(router.Create(VideoCodecType.H264, 320, 240));
    }

    [SoftwareH264Fact]
    public async Task WithoutHardwareTheSessionAdvertisesH264AndDecodesItInSoftware()
    {
        var router = new VideoDecoderRouter(NoHardware, new SoftwareVideo());
        await using var h = new Harness(router);
        Assert.Equal([VideoCodecType.H264], await h.Advertise());

        var dest = new SpiceRect(40, 20, 40 + TestClip.Width, 20 + TestClip.Height);
        h.Send(DisplayMsg.StreamCreate, Create(dest));
        Assert.False(h.State<bool>("Unsupported"));
        int worst = 0;
        for (int i = 0; i < 12; i++) h.Frame(i);
        foreach (var (cx, cy) in new[] { (80, 70), (240, 70), (80, 180), (240, 180) })
        {
            uint p = h.Pixel(dest.Left + cx, dest.Top + cy);
            var rgb = TestClip.ColorAt(11, cx, cy);
            int err = Math.Max(Math.Abs((int)(p >> 16 & 0xFF) - rgb.R),
                Math.Max(Math.Abs((int)(p >> 8 & 0xFF) - rgb.G), Math.Abs((int)(p & 0xFF) - rgb.B)));
            worst = Math.Max(worst, err);
            Assert.True(err <= Tolerance, $"({cx},{cy}): #{p & 0xFFFFFF:X6}, expected #{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}");
        }
        Assert.False(h.State<bool>("Unsupported"));
        Assert.Equal(0u, h.State<uint>("Drops"));
        output.WriteLine($"software-only 4:2:0 H.264, worst channel error {worst}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvertisingWaitsForTheSavedSoftwareSetting(bool saved)
    {
        // Review AF-R2: the viewer starts connecting before it reads settings.json. With no hardware the probe answers
        // at once, so advertising must wait for the saved softwareVideo value, not use the default "on".
        Assert.Empty(await NoHardware.Codecs);
        var router = new VideoDecoderRouter(NoHardware, new SoftwareVideo(), awaitSoftwareSetting: true);
        await using var h = new Harness(router);
        var advertised = h.Advertise();
        Assert.False(advertised.IsCompleted);
        Assert.Null(router.KnownCodecs);
        router.ApplySoftwareSetting(saved);
        VideoCodecType[] expected = saved && SoftwareVideo.IsAvailable ? [VideoCodecType.H264] : [];
        Assert.Equal(expected, await advertised.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(expected, router.KnownCodecs);
    }

    [SoftwareH264Fact]
    public async Task WithoutHardwareAndSoftwareVideoOffOnlyMjpegIsAdvertised()
    {
        var router = new VideoDecoderRouter(NoHardware, new SoftwareVideo()) { SoftwareEnabled = false };
        await using var h = new Harness(router);
        Assert.Empty(await h.Advertise());
    }
}
