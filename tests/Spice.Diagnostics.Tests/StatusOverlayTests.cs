using Spice.Session;
using Spice.Tests;
using Spice.Wire;
using SpiceViewer;
using Xunit.Abstractions;

namespace Spice.Diagnostics.Tests;

// The status overlay (docs/decisions/0044) without a window, a device or a session: its text, the refresh rule that
// keeps it from waking anything, its rates, its blending and its GDI image (a memory DC only).
public sealed unsafe class StatusOverlayTests(ITestOutputHelper output)
{
    /// <summary>A source that fills what a busy session would, from fields the test moves on, allocating nothing.</summary>
    sealed class FakeSource : IOverlaySource
    {
        public long Bytes, Messages, Shown;
        public int Fills;
        public bool Streaming = true;

        public void Fill(OverlaySnapshot s)
        {
            Fills++;
            s.Title = "win7";
            s.Tls = true; s.Proxy = true;
            s.Channels = 5;
            s.State = "connected";
            s.ClientMouse = true; s.Agent = true;
            s.HasSurface = true;
            s.SurfaceW = 1600; s.SurfaceH = 960; s.AreaX = 0; s.AreaY = 0; s.AreaW = 1600; s.AreaH = 900; s.AreaInferred = true;
            s.OneToOne = false;
            s.StreamCount = Streaming ? 1 : 0;
            s.Streams[0] = new VideoStreamStatus(3, VideoCodecType.H264, VideoDecodePath.Hardware, 1280, 720, Shown + 2, 2, 0, Shown);
            s.DecoderNames[0] = "AMD D3D11 Hardware MFT Playback Decoder";
            s.Pacing = VideoPacing.Off;
            s.NetBytes = Bytes; s.NetMessages = Messages; s.DisplayBytes = Bytes / 2;
            s.Audio = new AudioStatus(true, AudioDataMode.Opus, 2, 48000, 400);
            s.PrivateBytes = 150L << 20; s.GcHeapBytes = 30L << 20;
            s.LastTrimMs = 1000; s.LastTrimReason = "video ended";
        }
    }

    static string[] Lines(OverlayText t)
    {
        var lines = new string[t.LineCount];
        for (int i = 0; i < lines.Length; i++) lines[i] = t.Line(i).ToString();
        return lines;
    }

    [Fact]
    public void TheTextNamesEachFieldAndNoSecret()
    {
        var source = new FakeSource { Bytes = 1000, Messages = 10, Shown = 100 };
        var overlay = new StatusOverlay(source);
        overlay.Snapshot.Direct = true; overlay.Snapshot.BufferW = 1600; overlay.Snapshot.BufferH = 900; overlay.Snapshot.Scale = 1;
        overlay.Snapshot.Adapter = "Microsoft Basic Render Driver"; overlay.Snapshot.Software = true;
        overlay.Refresh(10_000, new TimeSpan(14, 3, 21));
        source.Bytes += 2 * 1024 * 1024; source.Messages += 500; source.Shown += 60; overlay.Snapshot.Presents = 60;
        overlay.Refresh(12_000, new TimeSpan(14, 3, 23));
        string[] lines = Lines(overlay.Text);
        foreach (var l in lines) output.WriteLine(l);
        string all = string.Join("\n", lines);

        Assert.StartsWith("win7  14:03:23  rates over 2.0 s", lines[0]);
        Assert.Contains("conn  connected, TLS via proxy, 5 ch, mouse client, agent", all);
        Assert.Contains("disp  1600x900 of 1600x960 (inferred), fit 1.00x", all);
        Assert.Contains("pres  direct 1600x900, 30.0/s, Microsoft Basic Render Driver (software)", all);
        Assert.Contains("net   1.0 MB/s, 250 msg/s, display 512.0 KB/s", all);
        Assert.Contains("video 1 stream, pacing off", all);
        Assert.Contains("  #3 H.264 hardware (MF) 1280x720, 30.0 fps, 160 shown, 2 dropped", all);
        Assert.Contains("    AMD D3D11 Hardware MFT Playback Decoder", all);
        Assert.Contains("audio Opus 48 kHz 2 ch, server latency 400 ms", all);
        Assert.Contains("mem   150.0 MB private, GC heap 30.0 MB, trimmed 11 s ago (video ended)", all);
        Assert.All(lines, l => Assert.True(l.Length <= OverlayText.MaxColumns));
    }

    [Fact]
    public void AMonitorAwayFromTheOriginOneToOneAndNoDisplayAreNamed()
    {
        var s = new OverlaySnapshot
        {
            Title = "a very long virtual machine title that goes on and on", State = "display off", HasSurface = true,
            SurfaceW = 2048, SurfaceH = 1536, AreaX = 1024, AreaY = 0, AreaW = 1024, AreaH = 768, OneToOne = true, Scale = 1,
        };
        var t = new OverlayText();
        OverlayFormat.Write(s, new OverlayRates(), t);
        string all = string.Join(Environment.NewLine, Lines(t));
        output.WriteLine(all);
        Assert.StartsWith("a very long virtual machine titl  00:00:00", Lines(t)[0]); // cut to 32, no rates yet
        Assert.Contains("disp  1024x768 of 2048x1536 at 1024,0 (monitor), 1:1 1.00x", all);
        Assert.Contains("pres  letterbox 0x0, –/s", all);
        Assert.Contains("audio idle", all);
        s.HasSurface = false;
        OverlayFormat.Write(s, new OverlayRates(), t);
        Assert.Contains("disp  no guest display", string.Join(Environment.NewLine, Lines(t)));
    }

    [Fact]
    public void RatesAreUnknownUntilTwoSamplesAndANewStreamHasNoFpsYet()
    {
        var rates = new OverlayRates();
        var s = new OverlaySnapshot { StreamCount = 1, Presents = 10 };
        s.Streams[0] = new VideoStreamStatus(1, VideoCodecType.Mjpeg, VideoDecodePath.Mjpeg, 640, 480, 0, 0, 0, 0);
        Assert.True(rates.Due(5_000));
        rates.Sample(s, 5_000);
        Assert.False(rates.Valid);
        Assert.True(double.IsNaN(rates.Presents));
        Assert.False(rates.Due(5_999));
        Assert.True(rates.Due(6_000));

        s.Presents = 40;
        s.StreamCount = 2;
        s.Streams[0] = s.Streams[0] with { Shown = 25 };
        s.Streams[1] = new VideoStreamStatus(2, VideoCodecType.H264, VideoDecodePath.Software, 0, 0, 5, 0, 0, 5);
        rates.Sample(s, 6_000);
        Assert.True(rates.Valid);
        Assert.Equal(1.0, rates.Seconds);
        Assert.Equal(30.0, rates.Presents);
        Assert.Equal(25.0, rates.FpsOf(1));
        Assert.True(double.IsNaN(rates.FpsOf(2)));
        Assert.True(double.IsNaN(rates.FpsOf(99)));
    }

    [Theory]
    // forced, guest presenting, rates due -> refresh
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)] // a static guest: never, however stale the rates
    [InlineData(false, true, false, false)] // a busy guest: at most once per interval
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]  // a stream started, the overlay was just shown, the layout changed
    [InlineData(true, true, true, true)]
    public void RefreshesOnlyOnAForcedChangeOrADuePresent(bool forced, bool guestPresenting, bool due, bool expected)
        => Assert.Equal(expected, StatusOverlay.ShouldRefresh(forced, guestPresenting, due));

    [Fact]
    public void AStaticGuestKeepsTheLastValuesAndAnUnchangedTextIsNotRedrawn()
    {
        var source = new FakeSource();
        var overlay = new StatusOverlay(source);
        Assert.True(overlay.WantsRefresh(0, forced: true, guestPresenting: false));
        Assert.True(overlay.Refresh(0, new TimeSpan(10, 0, 0)));
        // Nothing presents while the guest is static, and a wake that presents nothing doesn't refresh: no work at all.
        for (long t = 1_000; t <= 600_000; t += 1_000) Assert.False(overlay.WantsRefresh(t, forced: false, guestPresenting: false));
        Assert.Equal(1, source.Fills);
        // A forced refresh within the same second with the same figures: the text is the same, so nothing is presented.
        Assert.False(overlay.Refresh(500, new TimeSpan(10, 0, 0)));
        // A non-rate change (the stream ended) shows at once.
        source.Streaming = false;
        Assert.True(overlay.Refresh(600, new TimeSpan(10, 0, 0)));
        Assert.Contains("video no stream", string.Join("\n", Lines(overlay.Text)));
    }

    [Fact]
    public void LongLinesAreCutAndCommitComparesWholeLines()
    {
        var t = new OverlayText();
        t.NewLine("x").Add(new string('y', 200));
        Assert.Equal(OverlayText.MaxColumns, t.Line(0).Length);
        Assert.True(t.Commit());
        t.Clear();
        t.NewLine("x").Add(new string('y', 200));
        Assert.False(t.Commit());
        t.Clear();
        t.NewLine("x").Add(new string('y', 199)).Add('z');
        Assert.False(t.Commit()); // the change is past the cut
        t.Clear();
        t.NewLine("x");
        Assert.True(t.Commit());
        t.NewLine("second");
        Assert.True(t.Commit());
        for (int i = 0; i < 40; i++) t.NewLine("more");
        Assert.Equal(OverlayText.MaxLines, t.LineCount);
    }

    [Fact]
    public void BlendingIsPremultipliedOverAndOpaque()
    {
        uint[] overlay = [0x00000000, 0xFFFFFFFF, 0xB8000000, 0x80404040];
        uint[] guest = [0x00123456, 0xFF000000, 0xFFFFFFFF, 0xFF808080];
        var result = new uint[4];
        OverlayBlend.Row(overlay, guest, result);
        Assert.Equal(0xFF123456u, result[0]);              // transparent: the guest, opaque
        Assert.Equal(0xFFFFFFFFu, result[1]);              // opaque: the overlay
        uint dark = (uint)Math.Round(255 * (255 - 0xB8) / 255.0);
        Assert.Equal(0xFF000000u | dark << 16 | dark << 8 | dark, result[2]);
        uint mid = 0x40 + (uint)Math.Round(0x80 * (255 - 0x80) / 255.0);
        Assert.Equal(0xFF000000u | mid << 16 | mid << 8 | mid, result[3]);
    }

    [Fact]
    public void TheImageIsTranslucentDarkWithLightTextAndGrowsWithTheText()
    {
        using var bitmap = new OverlayBitmap();
        var text = new OverlayText();
        text.NewLine("conn  connected");
        Assert.True(bitmap.Render(text, 16));
        int w1 = bitmap.Width, h1 = bitmap.Height, v1 = bitmap.Version;
        Assert.True(w1 > 0 && h1 > 0);
        int bright = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            var row = new ReadOnlySpan<uint>(bitmap.Bits + (long)y * bitmap.Stride, bitmap.Width);
            foreach (uint p in row)
            {
                uint a = p >> 24, c = p & 0xFF;
                Assert.InRange(a, (uint)OverlayBitmap.BackgroundAlpha, 255u); // never more see-through than the background
                Assert.True(c <= a);                                      // premultiplied
                if (c > 0x80) bright++;
            }
        }
        Assert.True(bright > 20, $"only {bright} bright pixels");
        text.NewLine("video 1 stream, pacing off, and a longer line");
        Assert.True(bitmap.Render(text, 16));
        Assert.True(bitmap.Width > w1 && bitmap.Height > h1);
        Assert.Equal(v1 + 1, bitmap.Version);
        Assert.True(bitmap.Render(text, 24));
        Assert.Equal(24, bitmap.FontPx);
        Assert.True(bitmap.Height > h1 * 2);
    }

    [Fact]
    public void ARefreshAndItsRedrawAllocateNothingOnceWarm()
    {
        var source = new FakeSource();
        var overlay = new StatusOverlay(source);
        using var bitmap = new OverlayBitmap();
        uint[] guest = new uint[512], blended = new uint[512];
        long now = 0;
        void Step()
        {
            for (int i = 0; i < 50; i++)
            {
                now += 1_000;
                source.Bytes += 123_456; source.Messages += 77; source.Shown += 30;
                overlay.Snapshot.Presents += 30;
                if (overlay.WantsRefresh(now, forced: false, guestPresenting: true)
                    && overlay.Refresh(now, TimeSpan.FromMilliseconds(now)))
                    Assert.True(bitmap.Render(overlay.Text, 14));
                var row = new ReadOnlySpan<uint>(bitmap.Bits, Math.Min(bitmap.Width, guest.Length));
                OverlayBlend.Row(row, guest.AsSpan(0, row.Length), blended.AsSpan(0, row.Length));
            }
        }
        Step(); // warm: the DIB, the font, ArrayPool-free formatting paths
        var m = AllocationWindow.Measure(nameof(ARefreshAndItsRedrawAllocateNothingOnceWarm), Step, report: output.WriteLine);
        output.WriteLine($"{m.Bytes} B for 50 refreshes and redraws");
        Assert.Equal(0, m.Bytes);
    }
}
