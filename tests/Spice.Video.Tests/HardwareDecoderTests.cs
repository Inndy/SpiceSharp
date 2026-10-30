using System.Collections;
using System.Reflection;
using Spice.Session;
using Spice.Tests;
using Spice.Wire;
using SpiceViewer.Video;
using VvFile;
using Xunit.Abstractions;

namespace Spice.Video.Tests;

/// <summary>The process-wide probe, as the viewer runs it at start-up.</summary>
static class Hardware
{
    /// <summary>What the probe and the decoders logged (Info and up), for the test output.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> LogLines = Capture();
    public static readonly HardwareVideo Factory = new();

    static System.Collections.Concurrent.ConcurrentQueue<string> Capture()
    {
        var q = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Log.Sink = (level, m) => { if (m.StartsWith("video:", StringComparison.Ordinal)) q.Enqueue($"{level}: {m}"); };
        return q;
    }
    static readonly Lazy<IReadOnlyList<VideoCodecType>> Probed = new(() =>
        Factory.Codecs.Wait(TimeSpan.FromSeconds(30)) ? Factory.Codecs.Result : []);
    public static IReadOnlyList<VideoCodecType> Codecs => Probed.Value;

    public static string? SkipReason(VideoCodecType codec)
    {
        if (!Codecs.Contains(codec))
            return $"{codec}: no hardware Media Foundation decoder on this machine (adapter '{Factory.AdapterName}'); see the probe's log lines";
        if (codec == VideoCodecType.H264 && TestClip.Error is { } e) return e;
        return null;
    }
}

/// <summary>Runs only where <paramref name="codec"/> decodes in hardware (and, for H.264, the inbox encoder made the clip).</summary>
sealed class HardwareCodecFactAttribute : FactAttribute
{
    public HardwareCodecFactAttribute(VideoCodecType codec) => Skip = Hardware.SkipReason(codec);
}

// Real Media Foundation decoding on a headless D3D11 device, driven through the session's display handler with a synthetic
// clip from the inbox H.264 encoder (docs/decisions/0037). No window.
#pragma warning disable IL2026, IL2075
[Collection("Hardware video")]
public sealed class HardwareDecoderTests(ITestOutputHelper output)
{
    const int SurfaceW = 800, SurfaceH = 600;
    const int Tolerance = 12;
    const uint Black = 0xFF000000;

    sealed class Harness : IAsyncDisposable
    {
        public readonly SpiceSession Session;
        public Harness()
        {
            Session = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n")) { VideoDecoders = Hardware.Factory };
            typeof(SpiceSession).GetField("_videoCodecs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Session, Hardware.Codecs.ToArray());
            Send(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write((uint)SurfaceW); w.Write((uint)SurfaceH); w.Write(32u); w.Write(1u); }));
        }
        public void Send(ushort type, byte[] body) => Session.OnDisplayMessage(type, body);
        public void Frame(int i, uint id = 7) => Send(DisplayMsg.StreamData, Data(TestClip.Frames[i], id, (uint)(1000 + i * 33)));
        public object Stream(uint id = 7) => ((IDictionary)typeof(SpiceSession).GetField("_streams", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Session)!)[id]!;
        public T State<T>(string name, uint id = 7) { var s = Stream(id); return (T)s.GetType().GetField(name)!.GetValue(s)!; }
        public uint Pixel(int x, int y) => Session.Canvas.Primary!.Row(y)[x];
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    static byte[] Bytes(Action<BinaryWriter> write)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }
    static void Rect(BinaryWriter w, SpiceRect r) { w.Write(r.Top); w.Write(r.Left); w.Write(r.Bottom); w.Write(r.Right); }
    static byte[] Create(SpiceRect dest, bool topDown = true, SpiceRect[]? clip = null, uint id = 7) => Bytes(w =>
    {
        w.Write(0u); w.Write(id); w.Write(topDown ? StreamCreate.FlagTopDown : (byte)0); w.Write((byte)VideoCodecType.H264); w.Write(0UL);
        w.Write((uint)TestClip.Width); w.Write((uint)TestClip.Height); w.Write((uint)TestClip.Width); w.Write((uint)TestClip.Height);
        Rect(w, dest);
        if (clip is null) w.Write(SpiceClip.TypeNone);
        else { w.Write(SpiceClip.TypeRects); w.Write((uint)clip.Length); foreach (var r in clip) Rect(w, r); }
    });
    static byte[] Data(byte[] unit, uint id, uint mm) => Bytes(w => { w.Write(id); w.Write(mm); w.Write((uint)unit.Length); w.Write(unit); });
    static byte[] Clip(SpiceRect[] rects, uint id = 7) => Bytes(w =>
    {
        w.Write(id); w.Write(SpiceClip.TypeRects); w.Write((uint)rects.Length); foreach (var r in rects) Rect(w, r);
    });

    int _worst;

    /// <summary>The surface pixel at (x, y) is <paramref name="rgb"/> within the tolerance (B8G8R8A8, opaque).</summary>
    void AssertColor(Harness h, int x, int y, (byte R, byte G, byte B) rgb, string what)
    {
        uint p = h.Pixel(x, y);
        int b = (int)(p & 0xFF), g = (int)(p >> 8 & 0xFF), r = (int)(p >> 16 & 0xFF);
        int err = Math.Max(Math.Abs(r - rgb.R), Math.Max(Math.Abs(g - rgb.G), Math.Abs(b - rgb.B)));
        _worst = Math.Max(_worst, err);
        Assert.True(err <= Tolerance && p >> 24 == 0xFF,
            $"{what} at ({x},{y}): got #{r:X2}{g:X2}{b:X2} alpha {p >> 24:X2}, expected #{rgb.R:X2}{rgb.G:X2}{rgb.B:X2} (±{Tolerance})");
    }

    /// <summary>Checks the four quadrant centres and the block of <paramref name="frame"/> drawn into <paramref name="dest"/>.</summary>
    void AssertFrame(Harness h, int frame, SpiceRect dest, bool mirrored = false)
    {
        double sx = (double)dest.Width / TestClip.Width, sy = (double)dest.Height / TestClip.Height;
        void At(int cx, int cy, string what)
        {
            int px = dest.Left + (int)(cx * sx), py = dest.Top + (int)((mirrored ? TestClip.Height - 1 - cy : cy) * sy);
            AssertColor(h, px, py, TestClip.ColorAt(frame, cx, cy), $"frame {frame} {what}");
        }
        At(80, 70, "top-left quadrant"); At(240, 70, "top-right quadrant");
        At(80, 180, "bottom-left quadrant"); At(240, 180, "bottom-right quadrant");
        int bx = TestClip.BlockLeft(frame) + TestClip.BlockSize / 2;
        At(bx, TestClip.BlockTop + TestClip.BlockSize / 2, "moving block");
    }

    [Fact]
    public void TheProbeAnswersAndNamesOnlyTheHardwareCodecs()
    {
        var codecs = Hardware.Codecs;
        output.WriteLine($"adapter '{Hardware.Factory.AdapterName}': hardware decoding for [{string.Join(", ", codecs)}]");
        foreach (var c in new[] { VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.H265 })
            output.WriteLine(Hardware.SkipReason(c) ?? $"{c}: available");
        foreach (var line in Hardware.LogLines) output.WriteLine(line);
        Assert.True(Hardware.Factory.Codecs.IsCompletedSuccessfully);
        Assert.All(codecs, c => Assert.Contains(c, new[] { VideoCodecType.H264, VideoCodecType.Vp9, VideoCodecType.H265 }));
        Assert.DoesNotContain(VideoCodecType.Vp8, codecs);
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task H264DecodesToBgraAtTheDestination()
    {
        await using var h = new Harness();
        var dest = new SpiceRect(40, 20, 40 + TestClip.Width, 20 + TestClip.Height);
        h.Send(DisplayMsg.StreamCreate, Create(dest));
        Assert.False(h.State<bool>("Unsupported"));
        for (int i = 0; i < TestClip.Frames.Length; i++)
        {
            h.Frame(i);
            if (i is 0 or 1 or 17 or 59) AssertFrame(h, i, dest);
        }
        Assert.Equal(Black, h.Pixel(39, 100)); Assert.Equal(Black, h.Pixel(40 + TestClip.Width, 100)); // outside
        Assert.Equal(0u, h.State<uint>("Drops"));
        output.WriteLine($"{TestClip.Frames.Length} frames ({TestClip.Frames.Sum(f => f.Length)} bytes of H.264), worst channel error {_worst}");
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task H264IsScaledToTheDestinationByTheVideoProcessor()
    {
        await using var h = new Harness();
        var dest = new SpiceRect(10, 10, 10 + TestClip.Width * 2, 10 + TestClip.Height * 2);
        h.Send(DisplayMsg.StreamCreate, Create(dest));
        for (int i = 0; i < 12; i++) h.Frame(i);
        AssertFrame(h, 11, dest);
        var small = new SpiceRect(500, 400, 500 + 160, 400 + 120);
        h.Send(DisplayMsg.StreamDataSized, Bytes(w =>
        {
            w.Write(7u); w.Write(2000u); w.Write((uint)TestClip.Width); w.Write((uint)TestClip.Height); Rect(w, small);
            w.Write((uint)TestClip.Frames[12].Length); w.Write(TestClip.Frames[12]);
        }));
        AssertFrame(h, 12, small);
        output.WriteLine($"worst channel error {_worst}");
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task ABottomUpStreamIsShownMirrored()
    {
        await using var h = new Harness();
        var dest = new SpiceRect(0, 0, TestClip.Width, TestClip.Height);
        h.Send(DisplayMsg.StreamCreate, Create(dest, topDown: false));
        for (int i = 0; i < 3; i++) h.Frame(i);
        AssertFrame(h, 2, dest, mirrored: true);
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task TheClipKeepsThePictureInsideIt()
    {
        await using var h = new Harness();
        var dest = new SpiceRect(0, 0, TestClip.Width, TestClip.Height);
        h.Send(DisplayMsg.StreamCreate, Create(dest, clip: [new SpiceRect(0, 0, TestClip.Width / 2, TestClip.Height)]));
        for (int i = 0; i < 3; i++) h.Frame(i);
        AssertColor(h, 80, 180, TestClip.Quadrants[2], "inside the clip");
        Assert.Equal(Black, h.Pixel(240, 70));
        Assert.Equal(Black, h.Pixel(240, 180));
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task FramesHiddenByTheClipStillDecodeSoTheNextShownFrameIsRight()
    {
        await using var h = new Harness();
        var dest = new SpiceRect(0, 0, TestClip.Width, TestClip.Height);
        h.Send(DisplayMsg.StreamCreate, Create(dest));
        h.Send(DisplayMsg.StreamClip, Clip([])); // everything covered
        for (int i = 0; i < 30; i++) h.Frame(i);
        Assert.Equal(30u, h.State<uint>("Hidden"));
        Assert.Equal(Black, h.Pixel(80, 70));
        h.Send(DisplayMsg.StreamClip, Clip([dest]));
        h.Frame(30); // a P-frame: right only if the 30 hidden frames reached the decoder
        AssertFrame(h, 30, dest);
        Assert.Equal(0u, h.State<uint>("Drops"));
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task EmptyAndGarbageFramesNeverThrow()
    {
        await using var h = new Harness();
        var dest = new SpiceRect(0, 0, TestClip.Width, TestClip.Height);
        h.Send(DisplayMsg.StreamCreate, Create(dest));
        h.Send(DisplayMsg.StreamData, Data([], 7, 1000));
        Assert.False(h.State<bool>("Unsupported"));
        var noise = new byte[4096];
        new Random(37).NextBytes(noise);
        for (int i = 0; i < 4; i++) h.Send(DisplayMsg.StreamData, Data(noise, 7, (uint)(1001 + i)));
        output.WriteLine($"after noise: unsupported={h.State<bool>("Unsupported")}, drops={h.State<uint>("Drops")}");
        // Either the decoder skipped the noise or the stream was handed back to the server as images; nothing escaped.
        Assert.Equal(Black, h.Pixel(80, 180));
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task SteadyDecodingAllocatesNothing()
    {
        await using var h = new Harness();
        var dest = new SpiceRect(0, 0, TestClip.Width, TestClip.Height);
        h.Send(DisplayMsg.StreamCreate, Create(dest));
        var messages = TestClip.Frames.Select((f, i) => Data(f, 7, (uint)(1000 + i * 33))).ToArray();
        // Warm-up: the processor, its views (one per decoder texture slice), the staging copy and the picture.
        for (int pass = 0; pass < 2; pass++) foreach (var m in messages) h.Send(DisplayMsg.StreamData, m);
        var shown = AllocationWindow.Measure("H.264 frames decoded, converted and read back", () =>
        {
            foreach (var m in messages) h.Send(DisplayMsg.StreamData, m);
        }, report: output.WriteLine);
        output.WriteLine($"{messages.Length} shown frames: {shown.Bytes} B");
        h.Send(DisplayMsg.StreamClip, Clip([]));
        foreach (var m in messages) h.Send(DisplayMsg.StreamData, m);
        var hidden = AllocationWindow.Measure("H.264 frames decoded without a picture", () =>
        {
            foreach (var m in messages) h.Send(DisplayMsg.StreamData, m);
        }, report: output.WriteLine);
        output.WriteLine($"{messages.Length} hidden frames: {hidden.Bytes} B");
        Assert.False(h.State<bool>("Unsupported"));
    }

    [Fact]
    public void TheDecodersTakeOnlyFormatsTheirDxvaProfilesCover()
    {
        IVideoDecoderFactory f = Hardware.Factory;
        Assert.True(f.CanDecode(new VideoFormat(VideoCodecType.H264, 66, 1, 8)));
        Assert.True(f.CanDecode(new VideoFormat(VideoCodecType.H264, 77, 1, 8)));
        Assert.True(f.CanDecode(new VideoFormat(VideoCodecType.H264, 100, 1, 8)));
        // spice-server's default x264enc output (videoconvert picks Y444 from BGRx).
        Assert.False(f.CanDecode(new VideoFormat(VideoCodecType.H264, 244, 3, 8)));
        Assert.False(f.CanDecode(new VideoFormat(VideoCodecType.H264, 110, 1, 10)));
        Assert.False(f.CanDecode(new VideoFormat(VideoCodecType.H264, 122, 2, 8)));
        Assert.True(f.CanDecode(new VideoFormat(VideoCodecType.Vp9, 0, 1, 8)));
        Assert.False(f.CanDecode(new VideoFormat(VideoCodecType.Vp9, 1, 3, 8)));
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public void TheTestClipDeclaresAFormatTheDecoderTakes()
    {
        Assert.True(VideoBitstream.TryReadFormat(VideoCodecType.H264, TestClip.Frames[0], out var format));
        output.WriteLine($"inbox encoder: {format}");
        Assert.Equal(1, format.ChromaFormat);
        Assert.True(((IVideoDecoderFactory)Hardware.Factory).CanDecode(format));
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task TheDecodingDeviceIsReleasedWhenItsLastDecoderCloses()
    {
        var factory = new HardwareVideo();
        Assert.Contains(VideoCodecType.H264, await factory.Codecs.WaitAsync(TimeSpan.FromSeconds(10)));
        unsafe nint Device() => (nint)System.Reflection.Pointer.Unbox(typeof(HardwareVideo)
            .GetField("_device", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(factory)!);
        var a = factory.Create(VideoCodecType.H264, 320, 240)!;
        var b = factory.Create(VideoCodecType.H264, 320, 240)!;
        Assert.NotEqual(0, Device());
        a.Dispose();
        Assert.NotEqual(0, Device()); // b still decodes on it
        b.Dispose();
        b.Dispose(); // twice is harmless
        Assert.Equal(0, Device());
        using var c = factory.Create(VideoCodecType.H264, 320, 240)!; // a new stream makes a new one
        Assert.NotEqual(0, Device());
    }

    [HardwareCodecFact(VideoCodecType.Vp9)]
    public async Task Vp9DecoderCanBeCreated()
    {
        await using var h = new Harness();
        h.Send(DisplayMsg.StreamCreate, Bytes(w =>
        {
            w.Write(0u); w.Write(8u); w.Write(StreamCreate.FlagTopDown); w.Write((byte)VideoCodecType.Vp9); w.Write(0UL);
            w.Write(320u); w.Write(240u); w.Write(320u); w.Write(240u); Rect(w, new SpiceRect(0, 0, 320, 240)); w.Write(SpiceClip.TypeNone);
        }));
        Assert.False(h.State<bool>("Unsupported", 8));
    }

    [HardwareCodecFact(VideoCodecType.H265)]
    public async Task HevcDecoderCanBeCreated()
    {
        await using var h = new Harness();
        h.Send(DisplayMsg.StreamCreate, Bytes(w =>
        {
            w.Write(0u); w.Write(9u); w.Write(StreamCreate.FlagTopDown); w.Write((byte)VideoCodecType.H265); w.Write(0UL);
            w.Write(320u); w.Write(240u); w.Write(320u); w.Write(240u); Rect(w, new SpiceRect(0, 0, 320, 240)); w.Write(SpiceClip.TypeNone);
        }));
        Assert.False(h.State<bool>("Unsupported", 9));
    }
}
