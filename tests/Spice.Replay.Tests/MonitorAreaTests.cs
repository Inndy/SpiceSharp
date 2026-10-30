using System.Buffers.Binary;
using Spice.Canvas;
using Spice.Session;
using Spice.Wire;
using SpiceViewer;
using VvFile;
using Xunit.Abstractions;

namespace Spice.Replay.Tests;

/// <summary>
/// DISPLAY_MONITORS_CONFIG crops what is shown of the primary surface (finding L2 of
/// docs/verification/2026-10-30-ubuntu.md, docs/decisions/0035).
/// </summary>
public sealed class MonitorAreaTests(ITestOutputHelper output)
{
    internal static byte[] SurfaceCreate(uint w, uint h, bool primary = true, uint id = 0)
    {
        var b = new byte[20];
        uint[] fields = [id, w, h, Spice.Wire.SurfaceCreate.Format32_xRGB, primary ? 1u : 0u];
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), fields[i]);
        return b;
    }

    internal static byte[] SurfaceDestroy(uint id = 0)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, id);
        return b;
    }

    internal static byte[] MonitorsConfig(params (uint Surface, uint W, uint H, uint X, uint Y)[] heads)
    {
        var b = new byte[4 + heads.Length * DisplayParse.MonitorHeadSize];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)heads.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 4);
        for (int i = 0; i < heads.Length; i++)
        {
            var (surface, w, h, x, y) = heads[i];
            uint[] fields = [(uint)i, surface, w, h, x, y, 0];
            for (int f = 0; f < fields.Length; f++)
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 + i * DisplayParse.MonitorHeadSize + f * 4), fields[f]);
        }
        return b;
    }

    static (Canvas.Canvas Canvas, List<Surface?> Changes) NewCanvas(uint w, uint h)
    {
        var canvas = new Canvas.Canvas();
        var changes = new List<Surface?>();
        canvas.PrimaryChanged += changes.Add;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(w, h));
        return (canvas, changes);
    }

    [Fact]
    public void WithoutAMonitorsConfigTheWholeSurfaceIsShown()
    {
        var (canvas, _) = NewCanvas(800, 600);
        using var _c = canvas;
        Assert.Equal(new SpiceRect(0, 0, 800, 600), canvas.PrimaryArea);
    }

    [Fact]
    public void TheMonitorCropsTheSurfaceAndIsAnnounced()
    {
        var (canvas, changes) = NewCanvas(640, 512);
        using var _c = canvas;
        changes.Clear();
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 640, 480, 0, 0)));
        Assert.Equal(new SpiceRect(0, 0, 640, 480), canvas.PrimaryArea);
        Assert.Single(changes);
        Assert.Same(canvas.Primary, changes[0]);
        // The same config again changes nothing and announces nothing.
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 640, 480, 0, 0)));
        Assert.Single(changes);
        Assert.Equal(0, canvas.Errors);
    }

    [Theory]
    // An oversized monitor is clamped to the surface.
    [InlineData(true, 0u, 0u, 10_000u, 10_000u, 0, 0, 640, 512)]
    [InlineData(true, 0u, 0u, uint.MaxValue, uint.MaxValue, 0, 0, 640, 512)]
    // A lone head is shown from the surface's origin whatever its position, as spice-gtk does.
    [InlineData(true, 100u, 50u, 200u, 100u, 0, 0, 200, 100)]
    [InlineData(true, uint.MaxValue, uint.MaxValue, 320u, 200u, 0, 0, 320, 200)]
    // One head of several on the surface keeps its offset.
    [InlineData(false, 100u, 50u, 200u, 100u, 100, 50, 300, 150)]
    // Partly outside: the overlap.
    [InlineData(false, 600u, 500u, 100u, 100u, 600, 500, 640, 512)]
    // Entirely outside, or past every u32 boundary: the whole surface.
    [InlineData(false, 640u, 0u, 100u, 100u, 0, 0, 640, 512)]
    [InlineData(false, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, 0, 0, 640, 512)]
    public void TheMonitorIsClampedToTheSurface(bool alone, uint x, uint y, uint w, uint h, int l, int t, int r, int b)
    {
        var (canvas, _) = NewCanvas(640, 512);
        using var _c = canvas;
        // With company: a second head on another surface (ignored, as multi-monitor is post-v0).
        canvas.Apply(DisplayMsg.MonitorsConfig, alone ? MonitorsConfig((0, w, h, x, y)) : MonitorsConfig((0, w, h, x, y), (1, 800, 600, 0, 0)));
        Assert.Equal(new SpiceRect(l, t, r, b), canvas.PrimaryArea);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void ZeroHeadsOrHeadsOnOtherSurfacesShowTheWholeSurface()
    {
        var (canvas, _) = NewCanvas(640, 512);
        using var _c = canvas;
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 640, 480, 0, 0)));
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig());
        Assert.Equal(new SpiceRect(0, 0, 640, 512), canvas.PrimaryArea);
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 640, 480, 0, 0)));
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((2, 320, 200, 0, 0)));
        Assert.Equal(new SpiceRect(0, 0, 640, 512), canvas.PrimaryArea);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void TheShownHeadsIdFollowsTheAreaAndIsForgottenWithTheSurface()
    {
        var (canvas, _) = NewCanvas(640, 512);
        using var _c = canvas;
        Assert.Equal(0u, canvas.PrimaryMonitorId);
        Assert.False(canvas.PrimaryAreaIsMonitor);
        // Head #0 is disabled (zero size); head #1 is the one shown. Same area as a head #0 would give.
        var config = MonitorsConfig((0, 0, 0, 0, 0), (0, 640, 480, 0, 0));
        canvas.Apply(DisplayMsg.MonitorsConfig, config);
        Assert.Equal(1u, canvas.PrimaryMonitorId);
        Assert.True(canvas.PrimaryAreaIsMonitor);
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 640, 480, 0, 0)));
        Assert.Equal(0u, canvas.PrimaryMonitorId);
        canvas.Apply(DisplayMsg.MonitorsConfig, config);
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(800, 600));
        Assert.Equal(0u, canvas.PrimaryMonitorId);
        Assert.False(canvas.PrimaryAreaIsMonitor);
    }

    [Fact]
    public void AMalformedConfigIsAnErrorAndKeepsTheArea()
    {
        var (canvas, changes) = NewCanvas(640, 512);
        using var _c = canvas;
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 640, 480, 0, 0)));
        changes.Clear();
        var truncated = MonitorsConfig((0, 320, 200, 0, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(truncated, 2); // claims two heads, carries one
        canvas.Apply(DisplayMsg.MonitorsConfig, truncated);
        Assert.Equal(1, canvas.Errors);
        Assert.Equal(new SpiceRect(0, 0, 640, 480), canvas.PrimaryArea);
        Assert.Empty(changes);
    }

    [Fact]
    public void ANewPrimarySurfaceForgetsTheOldMonitorAndDestroyClearsTheArea()
    {
        var (canvas, changes) = NewCanvas(640, 512);
        using var _c = canvas;
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 640, 480, 0, 0)));
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        Assert.Null(canvas.Primary);
        Assert.Equal(default, canvas.PrimaryArea);
        // A primary surface of another size starts whole until its own config arrives.
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1024, 768));
        Assert.Equal(new SpiceRect(0, 0, 1024, 768), canvas.PrimaryArea);
        // A config without a primary surface is remembered nowhere: nothing to show it on.
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        changes.Clear();
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 640, 480, 0, 0)));
        Assert.Empty(changes);
        Assert.Equal(default, canvas.PrimaryArea);
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1024, 768));
        Assert.Equal(new SpiceRect(0, 0, 1024, 768), canvas.PrimaryArea);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void AGuestWakingItsDisplayGetsItsMonitorBackWithoutANewConfig()
    {
        // Finding L6 (live re-check of decision 0035): a 1600x900 mode on a 1600x960 qxl surface; the guest blanks
        // (SURFACE_DESTROY) and wakes with SURFACE_CREATE 1600x960, one full-surface DrawCopy and MARK, and no
        // MONITORS_CONFIG. The 60 rows below the mode hold stale guest memory and must stay hidden.
        var (canvas, changes) = NewCanvas(1600, 960);
        using var _c = canvas;
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 0, 0, 0, 0), (0, 1600, 900, 0, 0)));
        Assert.Equal(1u, canvas.PrimaryMonitorId);
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        canvas.Apply(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
        changes.Clear();
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        canvas.Apply(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new SpiceRect(0, 0, 1600, 900), canvas.PrimaryArea);
        Assert.True(canvas.PrimaryAreaIsMonitor);
        Assert.Equal(1u, canvas.PrimaryMonitorId);
        Assert.Single(changes); // announced once, with the kept area already in place
        var (_, w, h) = Png.Decode(canvas.SnapshotPrimaryPng()!);
        Assert.Equal((1600, 900), (w, h));
        // A second blank and wake keeps it again; a later config still wins.
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Assert.Equal(new SpiceRect(0, 0, 1600, 900), canvas.PrimaryArea);
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1600, 960, 0, 0)));
        Assert.Equal(new SpiceRect(0, 0, 1600, 960), canvas.PrimaryArea);
        Assert.Equal(0u, canvas.PrimaryMonitorId);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void ARecreateWithoutADestroyKeepsTheMonitorAtTheSameSize()
    {
        var (canvas, _) = NewCanvas(640, 512);
        using var _c = canvas;
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 640, 480, 0, 0)));
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(640, 512));
        Assert.Equal(new SpiceRect(0, 0, 640, 480), canvas.PrimaryArea);
    }

    [Fact]
    public void WindowsModeChangesWithoutAConfigShowTheNewSurfaceWhole()
    {
        // The live re-check's win81 order. Grow: SURFACE_DESTROY and SURFACE_CREATE only; keeping the old 1280x800 head
        // would crop the new 1800x1059 mode.
        var (canvas, _) = NewCanvas(1280, 800);
        using var _c = canvas;
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1280, 800, 0, 0)));
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1800, 1059));
        Assert.Equal(new SpiceRect(0, 0, 1800, 1059), canvas.PrimaryArea);
        Assert.False(canvas.PrimaryAreaIsMonitor);
        // Shrink: MONITORS_CONFIG with the new size on the old surface first, then destroy and create.
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1600, 1000, 0, 0)));
        Assert.Equal(new SpiceRect(0, 0, 1600, 1000), canvas.PrimaryArea);
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 1000));
        Assert.Equal(new SpiceRect(0, 0, 1600, 1000), canvas.PrimaryArea);
        Assert.False(canvas.PrimaryAreaIsMonitor); // the head was paired with the 1800x1059 surface
    }

    [Fact]
    public void AConnectToABlankGuestThatWakesShowsTheWholeSurface()
    {
        // The live capture of finding L7/L6 (rec3, 3.2 MB, not added as a fixture): a guest blank at connect sends SET_ACK
        // and pings; on waking, SURFACE_CREATE 1600x960, one DrawCopy of the whole surface, MARK, then DrawCopies of
        // (0,0)-(1600,900) only, and no MONITORS_CONFIG. Nothing in the main, cursor or display channels names the
        // 1600x900 mode (the draw extents only hint at it), so the viewer shows the whole surface; spice-gtk would show
        // nothing until a config arrives. Recorded as a limit in docs/decisions/0035.
        var canvas = new Canvas.Canvas();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        canvas.Apply(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new SpiceRect(0, 0, 1600, 960), canvas.PrimaryArea);
        Assert.False(canvas.PrimaryAreaIsMonitor);
    }

    [Fact]
    public void TheSnapshotHoldsOnlyTheMonitorArea()
    {
        var (canvas, _) = NewCanvas(64, 48);
        using var _c = canvas;
        lock (canvas.Sync)
        {
            var p = canvas.Primary!;
            for (int y = 0; y < p.Height; y++)
            {
                var row = p.Row(y);
                for (int x = 0; x < p.Width; x++) row[x] = 0xFF000000u | (uint)(x << 16) | (uint)(y << 8) | 0x40;
            }
        }
        // Two heads side by side on the surface: the first one is shown, at its offset.
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 20, 10, 5, 7), (0, 20, 10, 25, 7)));
        Assert.Equal(new SpiceRect(5, 7, 25, 17), canvas.PrimaryArea);
        var (bgra, w, h) = Png.Decode(canvas.SnapshotPrimaryPng()!);
        Assert.Equal((20, 10), (w, h));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                Assert.Equal(((byte)0x40, (byte)(y + 7), (byte)(x + 5)), (bgra[i], bgra[i + 1], bgra[i + 2]));
            }
    }

    [Fact]
    public void ApplyingAnUnchangedMonitorsConfigAllocatesNothing()
    {
        var (canvas, _) = NewCanvas(640, 512);
        using var _c = canvas;
        ReadOnlyMemory<byte> m = MonitorsConfig((0, 640, 480, 0, 0));
        // Warm the whole loop first, so tiered JIT and on-stack replacement of the loop itself happen outside the count
        // (a single warm-up call once let 5,688 B through, docs/reviews/2026-10-30-display-off-and-monitor-area.md).
        void Work() { for (int i = 0; i < 10_000; i++) canvas.Apply(DisplayMsg.MonitorsConfig, m); }
        Work();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Work();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task UbuntuRichShowsTheMonitorAreaWithoutABlackBand()
    {
        string fixture = Fixture("ubuntu-rich");
        var opener = new ReplayOpener(fixture);
        var vv = VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=replay\nport=5900\n");
        await using var session = new SpiceSession(vv, opener);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.ConnectAsync(ct.Token);
        await opener.Display.Completion.WaitAsync(ct.Token);
        Assert.Equal(0, session.Canvas.Errors);
        Assert.Equal(GuestDisplayState.On, session.DisplayState);
        byte[] surfaceRows;
        lock (session.Canvas.Sync)
        {
            var p = session.Canvas.Primary!;
            // The qxl surface keeps its 640x512; the guest's monitor is 640x480.
            Assert.Equal((640, 512), (p.Width, p.Height));
            Assert.Equal(new SpiceRect(0, 0, 640, 480), session.Canvas.PrimaryArea);
            // What the old viewer showed below the monitor: 32 rows that nothing draws (still opaque black).
            for (int y = 480; y < 512; y++) Assert.True(p.Row(y).IndexOfAnyExcept(0xFF000000u) < 0, $"surface row {y} was drawn");
            surfaceRows = p.Pixels.AsSpan(0, 480 * p.Stride).ToArray();
        }
        byte[] png = session.Canvas.SnapshotPrimaryPng()!;
        var (bgra, w, h) = Png.Decode(png);
        Assert.Equal((640, 480), (w, h));
        // No black band: every one of the bottom 32 rows of the shown image has drawn pixels (the dock and wallpaper).
        for (int y = h - 32; y < h; y++)
        {
            bool drawn = false;
            for (int x = 0; x < w && !drawn; x++)
            {
                int i = (y * w + x) * 4;
                drawn = bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0;
            }
            Assert.True(drawn, $"row {y} of the shown image is black");
        }
        // The shown image is exactly the top 480 rows of the surface.
        for (int i = 0; i < 640 * 480; i++)
            Assert.Equal((surfaceRows[i * 4], surfaceRows[i * 4 + 1], surfaceRows[i * 4 + 2]), (bgra[i * 4], bgra[i * 4 + 1], bgra[i * 4 + 2]));
        // The golden needs the human's approval (tests/fixtures/README.md): leave the candidate next to the fixture.
        if (!File.Exists(Path.Combine(fixture, "golden.png")))
        {
            await File.WriteAllBytesAsync(Path.Combine(fixture, "golden.actual.png"), png);
            output.WriteLine($"wrote {Path.Combine(fixture, "golden.actual.png")} (640x480) for review");
        }
    }

    [Theory]
    [InlineData("win7-desktop", 800, 600)]
    [InlineData("win81-desktop", 1024, 768)]
    [InlineData("win7-rich", 800, 600)]
    [InlineData("win81-rich", 1024, 768)]
    public async Task WindowsGuestsMonitorIsTheirWholeSurface(string name, int width, int height)
    {
        var opener = new ReplayOpener(Fixture(name));
        var vv = VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=replay\nport=5900\n");
        await using var session = new SpiceSession(vv, opener);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.ConnectAsync(ct.Token);
        await opener.Display.Completion.WaitAsync(ct.Token);
        lock (session.Canvas.Sync)
        {
            Assert.Equal((width, height), (session.Canvas.Primary!.Width, session.Canvas.Primary.Height));
            Assert.Equal(new SpiceRect(0, 0, width, height), session.Canvas.PrimaryArea);
        }
    }

    internal static string Fixture(string name)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tests", "fixtures"))) root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("tests/fixtures");
        return Path.Combine(root, "tests", "fixtures", name);
    }
}

/// <summary>The window's mapping between client pixels and the guest's monitor area (src/SpiceViewer/GuestView.cs).</summary>
public sealed class GuestMappingTests
{
    [Theory]
    // Fit, 640x480 monitor drawn over a 1280x960 client: the bottom-right client pixel is the monitor's last pixel,
    // never a row of the 640x512 surface below it.
    [InlineData(1279, 959, 0, 0, 1280, 960, 639, 479)]
    [InlineData(0, 0, 0, 0, 1280, 960, 0, 0)]
    [InlineData(640, 480, 0, 0, 1280, 960, 320, 240)]
    // Letterboxed (bars of 160 left and right): bars clamp to the edge.
    [InlineData(100, 300, 160, 0, 960, 720, 0, 300 * 480 / 720)]
    [InlineData(1200, 719, 160, 0, 960, 720, 639, 479)]
    // 1:1 cropped: the point maps unscaled.
    [InlineData(10, 20, 0, 0, 640, 480, 10, 20)]
    public void ClientPointsMapIntoTheMonitorArea(int cx, int cy, int l, int t, int w, int h, int gx, int gy)
        => Assert.Equal((gx, gy), GuestView.ClientToGuest(cx, cy, l, t, w, h, 640, 480));

    [Theory]
    // Shrunk: both ends of the image reach both ends of the monitor, and the middle stays in order.
    [InlineData(0, 0, 0, 0)]
    [InlineData(319, 239, 639, 479)]
    [InlineData(160, 120, 320, 240)]
    // Outside the image (a letterbox bar would be, in client terms): clamped.
    [InlineData(-5, 400, 0, 479)]
    public void ShrunkImagesMapEndToEnd(int cx, int cy, int gx, int gy)
        => Assert.Equal((gx, gy), GuestView.ClientToGuest(cx, cy, 0, 0, 320, 240, 640, 480));

    [Fact]
    public void AOnePixelImageMapsToTheFirstPixel()
        => Assert.Equal((0, 0), GuestView.ClientToGuest(0, 0, 0, 0, 1, 1, 640, 480));

    [Fact]
    public void WithoutAGuestSizeTheClientPointIsReturnedUnchanged()
        => Assert.Equal((5, 6), GuestView.ClientToGuest(5, 6, 0, 0, 100, 100, 0, 0));

    [Fact]
    public void CursorPositionsLoseTheMonitorOffset()
    {
        // A monitor at (100,50) of its surface, shown 1:1 at the client origin: surface (100,50) is the client's (0,0).
        Assert.Equal((0, 0), GuestView.GuestToClient(100, 50, 100, 50, 0, 0, 640, 480, 640, 480));
        Assert.Equal((20, 10), GuestView.GuestToClient(120, 60, 100, 50, 0, 0, 640, 480, 640, 480));
        // Scaled by 2 into a letterbox at (160,0).
        Assert.Equal((160 + 40, 20), GuestView.GuestToClient(120, 60, 100, 50, 160, 0, 1280, 960, 640, 480));
    }

    [Fact]
    public void ARoundTripStaysOnThePixel()
    {
        for (int gx = 0; gx < 640; gx += 37)
            for (int gy = 0; gy < 480; gy += 29)
            {
                var (cx, cy) = GuestView.GuestToClient(gx, gy, 0, 0, 160, 0, 1280, 960, 640, 480);
                Assert.Equal((gx, gy), GuestView.ClientToGuest(cx, cy, 160, 0, 1280, 960, 640, 480));
            }
    }
}
