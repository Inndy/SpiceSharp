using System.Reflection;
using Spice.Canvas;
using Spice.Native;
using Spice.Session;
using Spice.Tests;
using Spice.Transport;
using Spice.Wire;
using VvFile;

namespace Spice.Replay.Tests;

// LZ4 images (spice-common canvas_get_lz4) and the session's codec settings (docs/decisions/0028).
#pragma warning disable IL2026, IL2075
public sealed class Lz4ImageTests
{
    const int W = 37, H = 19;

    public static IEnumerable<object[]> FormatsAndOrientations() =>
        from f in new[] { BitmapFormat.Rgb16, BitmapFormat.Rgb24, BitmapFormat.Rgb32, BitmapFormat.Rgba }
        from top in new[] { true, false }
        select new object[] { (byte)f, top };

    static int Bpp(BitmapFormat f) => f switch { BitmapFormat.Rgb16 => 2, BitmapFormat.Rgb24 => 3, _ => 4 };
    static uint Expand5(uint c) => c << 3 | c >> 2;

    /// <summary>
    /// The decoded LZ4 rows (in stream order: bottom row first unless top-down) and the BGRA pixels they must give,
    /// in image order. Bits a format ignores (32BIT's X byte, 16BIT's top bit) carry junk.
    /// </summary>
    static (byte[] Raw, uint[] Expected) Sample(BitmapFormat fmt, bool topDown, int w = W, int h = H, int seed = 5)
    {
        var rng = new Random(seed);
        int bpp = Bpp(fmt), rowBytes = w * bpp;
        var raw = new byte[rowBytes * h];
        var expected = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // Runs of repeated pixels give the encoder matches; the noise keeps literals.
                int r = x / 5 * 40 + y, g = (x + y) * 7, b = rng.Next(3) == 0 ? rng.Next(256) : 0x80, a = (x * 13 + y) & 0xFF;
                var p = raw.AsSpan(((topDown ? y : h - 1 - y) * rowBytes) + x * bpp, bpp);
                uint e;
                switch (fmt)
                {
                    case BitmapFormat.Rgb16:
                    {
                        uint r5 = (uint)r & 31, g5 = (uint)g & 31, b5 = (uint)b & 31;
                        uint v = r5 << 10 | g5 << 5 | b5 | (uint)(rng.Next(2) << 15);
                        p[0] = (byte)v; p[1] = (byte)(v >> 8);
                        e = 0xFF000000u | Expand5(r5) << 16 | Expand5(g5) << 8 | Expand5(b5);
                        break;
                    }
                    case BitmapFormat.Rgb24:
                        p[0] = (byte)b; p[1] = (byte)g; p[2] = (byte)r;
                        e = 0xFF000000u | (uint)(byte)r << 16 | (uint)(byte)g << 8 | (byte)b;
                        break;
                    case BitmapFormat.Rgb32:
                        p[0] = (byte)b; p[1] = (byte)g; p[2] = (byte)r; p[3] = (byte)rng.Next(256);
                        e = 0xFF000000u | (uint)(byte)r << 16 | (uint)(byte)g << 8 | (byte)b;
                        break;
                    default: // RGBA
                        p[0] = (byte)b; p[1] = (byte)g; p[2] = (byte)r; p[3] = (byte)a;
                        e = (uint)a << 24 | (uint)(byte)r << 16 | (uint)(byte)g << 8 | (byte)b;
                        break;
                }
                expected[y * w + x] = e;
            }
        return (raw, expected);
    }

    static void AssertPixels(uint[] expected, DecodedImage img)
    {
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                if (img.Row(y)[x] != expected[y * img.Width + x])
                    Assert.Fail($"pixel ({x},{y}): {img.Row(y)[x]:X8}, expected {expected[y * img.Width + x]:X8}");
    }

    static SpiceImage Image(byte[] data, int w = W, int h = H) =>
        new() { Descriptor = new ImageDescriptor(1, ImageType.Lz4, 0, (uint)w, (uint)h), Data = data };

    // ---------------- conversion (no native decoder needed) ----------------

    [Theory]
    [MemberData(nameof(FormatsAndOrientations))]
    public void RowsConvertToBgraForEveryFormatAndOrientation(byte format, bool topDown)
    {
        var fmt = (BitmapFormat)format;
        var (raw, expected) = Sample(fmt, topDown);
        Assert.Equal(raw.Length, W * H * ImageDecoder.Lz4BytesPerPixel(fmt));
        var img = DecodedImage.Rent(W, H, fmt == BitmapFormat.Rgba);
        try
        {
            ImageDecoder.ConvertRows(raw, fmt, topDown, img);
            AssertPixels(expected, img);
        }
        finally { img.Release(); }
    }

    [Theory]
    [InlineData((byte)BitmapFormat.Invalid)]
    [InlineData((byte)BitmapFormat.A1Le)]
    [InlineData((byte)BitmapFormat.A4Be)]
    [InlineData((byte)BitmapFormat.A8)]
    [InlineData((byte)BitmapFormat.A8Alpha)]
    [InlineData(11)]
    [InlineData(255)]
    public void FormatsLz4CannotCarryAreCanvasExceptions(byte format)
    {
        using var decoder = new ImageDecoder();
        var (raw, _) = Sample(BitmapFormat.Rgb32, true);
        byte[] data = Lz4TestEncoder.Image(true, format, raw);
        var e = Assert.Throws<CanvasException>(() => decoder.Decode(Image(data)));
        Assert.Contains("format", e.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MissingHeaderIsACanvasException(int length)
    {
        using var decoder = new ImageDecoder();
        Assert.Throws<CanvasException>(() => decoder.Decode(Image(new byte[length])));
    }

    [Fact]
    public void WithoutTheNativeExportAnLz4ImageIsACanvasExceptionNotACrash()
    {
        using var decoder = new ImageDecoder();
        var (raw, expected) = Sample(BitmapFormat.Rgb24, false);
        var image = Image(Lz4TestEncoder.Image(false, (byte)BitmapFormat.Rgb24, raw));
        if (Lz4Decoder.IsAvailable)
        {
            var img = decoder.Decode(image);
            try { AssertPixels(expected, img); } finally { img.Release(); }
        }
        else Assert.Throws<CanvasException>(() => decoder.Decode(image));
    }

    // ---------------- through the native decoder ----------------

    [Lz4NativeTheory]
    [MemberData(nameof(FormatsAndOrientations))]
    public void ImagesRoundTripForEveryFormatAndOrientation(byte format, bool topDown)
    {
        var fmt = (BitmapFormat)format;
        var (raw, expected) = Sample(fmt, topDown);
        using var decoder = new ImageDecoder();
        // Three rows per block, each block free to refer back into earlier ones, as spice-server's streaming encoder.
        foreach (int chunk in new[] { int.MaxValue, W * Bpp(fmt) * 3 })
        {
            var img = decoder.Decode(Image(Lz4TestEncoder.Image(topDown, format, raw, chunk)));
            try
            {
                Assert.Equal(fmt == BitmapFormat.Rgba, img.HasAlpha);
                AssertPixels(expected, img);
            }
            finally { img.Release(); }
        }
    }

    [Lz4NativeFact]
    public void MalformedDataIsACanvasException()
    {
        using var decoder = new ImageDecoder();
        var fmt = BitmapFormat.Rgb32;
        var (raw, _) = Sample(fmt, true);
        int rowBytes = W * 4;
        byte[] good = Lz4TestEncoder.Image(true, (byte)fmt, raw, rowBytes * 4);
        byte[] fewerRows = Lz4TestEncoder.Image(true, (byte)fmt, raw.AsSpan(0, rowBytes * (H - 1)), rowBytes * 4);
        byte[] moreRows = Lz4TestEncoder.Image(true, (byte)fmt, [.. raw, .. raw.AsSpan(0, rowBytes)], rowBytes * 4);
        byte[] oversized = (byte[])good.Clone();
        oversized[2] = 0x40; // the first block's size word now runs far past the data
        var cases = new Dictionary<string, byte[]>
        {
            ["one row short"] = fewerRows,
            ["one row too many"] = moreRows,
            ["truncated"] = good[..^7],
            ["header only"] = good[..2],
            ["block size past the end"] = oversized,
        };
        // Same rows, 24-bit bottom-up (the scratch path).
        var (raw24, _) = Sample(BitmapFormat.Rgb24, false);
        cases["24-bit one row short"] = Lz4TestEncoder.Image(false, (byte)BitmapFormat.Rgb24, raw24.AsSpan(0, W * 3 * (H - 1)));
        var rng = new Random(77);
        for (int i = 0; i < 50; i++)
        {
            var junk = new byte[rng.Next(3, 400)];
            rng.NextBytes(junk);
            junk[1] = (byte)BitmapFormat.Rgb24;
            cases[$"junk {i}"] = junk;
        }
        foreach (var (name, data) in cases)
        {
            try { decoder.Decode(Image(data)).Release(); Assert.Fail($"{name}: decoded"); }
            catch (CanvasException) { }
        }
        // The decoder still works afterwards.
        decoder.Decode(Image(good)).Release();
    }

    [Lz4NativeFact]
    public void CanvasDrawsAnLz4ImageAndCountsBadOnesAsErrors()
    {
        using var canvas = new Spice.Canvas.Canvas();
        canvas.Apply(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write((uint)W); w.Write((uint)H); w.Write(32u); w.Write(1u); }));
        var (raw, expected) = Sample(BitmapFormat.Rgb32, false);
        canvas.Apply(DisplayMsg.DrawCopy, DrawCopy(Lz4TestEncoder.Image(false, (byte)BitmapFormat.Rgb32, raw)));
        Assert.Equal(0, canvas.Errors);
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Assert.Equal(expected[y * W + x], canvas.Primary!.Row(y)[x]);
        canvas.Apply(DisplayMsg.DrawCopy, DrawCopy([1, (byte)BitmapFormat.Rgb32, 0, 0, 0, 9, 1, 2, 3]));
        Assert.Equal(1, canvas.Errors);
    }

    internal static byte[] Bytes(Action<BinaryWriter> write)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }

    /// <summary>DRAW_COPY of a W×H LZ4 image (not cached) at (0,0) on surface 0.</summary>
    internal static byte[] DrawCopy(byte[] lz4, int w = W, int h = H) => Bytes(b =>
    {
        b.Write(0u); b.Write(0); b.Write(0); b.Write(h); b.Write(w); b.Write(SpiceClip.TypeNone); // surface, box (t,l,b,r), clip
        b.Write(57u); b.Write(0); b.Write(0); b.Write(h); b.Write(w); b.Write(Ropd.OpPut); b.Write((byte)0); // image at 57, src area
        b.Write((byte)0); b.Write(0); b.Write(0); b.Write(0u); // no mask
        b.Write(1UL); b.Write((byte)ImageType.Lz4); b.Write((byte)0); b.Write((uint)w); b.Write((uint)h);
        b.Write((uint)lz4.Length); b.Write(lz4);
    });

    // ---------------- session settings ----------------

    static SpiceSession NewSession() => new(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));

    [Fact]
    public void DisplayAdvertisesLz4ExactlyWhenSpicecodecDecodesIt()
    {
        var caps = SpiceSession.DisplayCaps();
        Assert.Equal(Lz4Decoder.IsAvailable, caps.Has(DisplayCap.Lz4Compression));
        foreach (int cap in new[] { DisplayCap.SizedStream, DisplayCap.MonitorsConfig, DisplayCap.MultiCodec, DisplayCap.CodecMjpeg, DisplayCap.StreamReport })
            Assert.True(caps.Has(cap));
        Assert.False(caps.Has(DisplayCap.PrefCompression)); // a server capability, not ours
    }

    [Fact]
    public async Task FastJpegReachesTheCanvasDecoder()
    {
        await using var session = NewSession();
        Assert.False(session.FastJpeg); Assert.False(session.Canvas.FastJpeg);
        session.FastJpeg = true;
        Assert.True(session.Canvas.FastJpeg);
        session.FastJpeg = false;
        Assert.False(session.Canvas.FastJpeg);
    }

    /// <summary>Collects what a channel's writer loop writes.</summary>
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

    static SpiceChannel Display(Stream stream, bool prefCompression) =>
        (SpiceChannel)typeof(SpiceChannel).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([stream, new ChannelId(ChannelType.Display, 0), new LinkReply(LinkError.Ok, [], new CapabilitySet(CommonCap.MiniHeader),
                prefCompression ? new CapabilitySet(DisplayCap.PrefCompression) : new CapabilitySet())]);

    // Mini header: u16 type, u32 size; then the one image_compression byte.
    static byte[] PreferredCompressionFrame(ImageCompression c) => [103, 0, 1, 0, 0, 0, (byte)c];
    static readonly byte[] Sentinel = [0xE7, 0x03, 0, 0, 0, 0]; // message 999, empty: everything before it was written

    static async Task<byte[]> WrittenBeforeSentinel(SpiceChannel channel, Capture capture)
    {
        channel.Send(999, []);
        static bool Done(byte[] b) => b.AsSpan().EndsWith(Sentinel);
        byte[] all = capture.Snapshot();
        for (int i = 0; i < 500 && !Done(all); i++) { await Task.Delay(10); all = capture.Snapshot(); }
        Assert.True(Done(all), "the channel's writer never wrote the sentinel");
        return all[..^Sentinel.Length];
    }

    [Fact]
    public async Task PreferenceIsSentOnlyWhenSetAndTheServerTakesIt()
    {
        var capture = new Capture();
        await using (var channel = Display(capture, prefCompression: true))
        {
            Assert.False(SpiceSession.SendPreferredCompression(channel, ImageCompression.Invalid)); // auto: nothing to send
            Assert.True(SpiceSession.SendPreferredCompression(channel, ImageCompression.Quic));
            Assert.Equal(PreferredCompressionFrame(ImageCompression.Quic), await WrittenBeforeSentinel(channel, capture));
        }
        var none = new Capture();
        await using (var channel = Display(none, prefCompression: false))
        {
            Assert.False(SpiceSession.SendPreferredCompression(channel, ImageCompression.Quic));
            Assert.Empty(await WrittenBeforeSentinel(channel, none));
        }
    }

    [Lz4NativeFact]
    public async Task Lz4PreferenceIsByte7()
    {
        var capture = new Capture();
        await using var channel = Display(capture, prefCompression: true);
        Assert.True(SpiceSession.SendPreferredCompression(channel, ImageCompression.Lz4));
        Assert.Equal(new byte[] { 103, 0, 1, 0, 0, 0, 7 }, await WrittenBeforeSentinel(channel, capture));
    }

    [Fact]
    public async Task SessionSendsEachChangeOnceTheDisplayIsUpAndNothingForServerDefault()
    {
        await using var session = NewSession();
        var capture = new Capture();
        await using var channel = Display(capture, prefCompression: true);
        session.PreferredCompression = ImageCompression.Quic; // before the display channel: only remembered
        Assert.Equal(ImageCompression.Quic, session.PreferredCompression);
        typeof(SpiceSession).GetField("_display", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, channel);
        session.PreferredCompression = ImageCompression.Glz; // a change: sent
        session.PreferredCompression = ImageCompression.Glz; // no change: not sent again
        session.PreferredCompression = ImageCompression.Invalid; // server default: the protocol has nothing to send
        session.PreferredCompression = ImageCompression.Off;
        byte[] written = await WrittenBeforeSentinel(channel, capture);
        Assert.Equal([.. PreferredCompressionFrame(ImageCompression.Glz), .. PreferredCompressionFrame(ImageCompression.Off)], written);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.PreferredCompression = (ImageCompression)8);
    }
}
