using System.Reflection;
using Spice.Canvas;
using Spice.Session;
using Spice.Wire;
using VvFile;
using static Spice.Replay.Tests.MonitorAreaTests;

namespace Spice.Replay.Tests;

/// <summary>
/// Finding L16 (docs/verification/2026-10-31-ubuntu-monitor-area.md, docs/decisions/0035): after the Ubuntu guest's qxl
/// KMS driver recreates its primary surface (a display wake), spice-server names the whole 1600x960 surface as the
/// monitor and the guest never corrects it, so the viewer showed the 60 stale rows below the 1600x900 desktop. The
/// sequences below are the live captures' message orders; the guest's own draws (after the MARK) are full-frame copies of
/// (0,0)-(1600,900). Log.Sink is process-wide, so this runs in the serial collection.
/// </summary>
#pragma warning disable IL2026, IL2075
[Collection("Allocation profiling")]
public sealed class MonitorInferenceTests
{
    static readonly SpiceRect Mode = new(0, 0, 1600, 900), Surface = new(0, 0, 1600, 960);

    static byte[] Fill(SpiceRect box, params SpiceRect[] clip)
    {
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        w.Write(0u); Rect(w, box);
        if (clip.Length == 0) w.Write(SpiceClip.TypeNone);
        else { w.Write(SpiceClip.TypeRects); w.Write((uint)clip.Length); foreach (var r in clip) Rect(w, r); }
        w.Write((byte)BrushType.Solid); w.Write(0x336699u); w.Write(Ropd.OpPut);
        w.Write((byte)0); w.Write(0); w.Write(0); w.Write(0u); // no mask
        return m.ToArray();
    }

    static void Rect(BinaryWriter w, SpiceRect r) { w.Write(r.Top); w.Write(r.Left); w.Write(r.Bottom); w.Write(r.Right); }

    static void Mark(Canvas.Canvas c) => c.Apply(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
    static void Draw(Canvas.Canvas c, SpiceRect box, params SpiceRect[] clip) => c.Apply(DisplayMsg.DrawFill, Fill(box, clip));

    static (Canvas.Canvas Canvas, List<Surface?> Changes) Linux()
    {
        var canvas = new Canvas.Canvas { GuestAgentIsLinux = true };
        var changes = new List<Surface?>();
        canvas.PrimaryChanged += changes.Add;
        return (canvas, changes);
    }

    static List<string> CaptureCanvasLog(Action act)
    {
        var lines = new List<string>();
        var (sink, level) = (Log.Sink, Log.MinLevel);
        Log.Sink = (_, m) => { lock (lines) if (m.StartsWith("canvas:", StringComparison.Ordinal)) lines.Add(m); };
        Log.MinLevel = LogLevel.Debug;
        try { act(); }
        finally { Log.Sink = sink; Log.MinLevel = level; }
        return lines;
    }

    [Fact]
    public void AConnectAfterAWakeShowsTheModeOnceTheGuestDraws()
    {
        // Live capture A (display on at connect): SURFACE_CREATE 1600x960, spice-server's image of the whole surface,
        // MONITORS_CONFIG with one 1600x960 head (the server's stand-in), MARK. Then the guest's first frame.
        var (canvas, changes) = Linux();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Draw(canvas, Surface);
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1600, 960, 0, 0)));
        Mark(canvas);
        Assert.Equal(Surface, canvas.PrimaryArea);
        // Provisional: the window's first sizing still follows the narrowed area.
        Assert.False(canvas.PrimaryAreaIsMonitor);
        changes.Clear();
        Draw(canvas, Mode);
        Assert.Equal(Mode, canvas.PrimaryArea);
        Assert.True(canvas.PrimaryAreaIsMonitor);
        Assert.Equal(0u, canvas.PrimaryMonitorId);
        Assert.Single(changes);
        var (_, w, h) = Png.Decode(canvas.SnapshotPrimaryPng()!);
        Assert.Equal((1600, 900), (w, h));
        // Every later frame is the same draw: nothing more to announce.
        Draw(canvas, Mode);
        Draw(canvas, new SpiceRect(100, 100, 300, 200));
        Assert.Single(changes);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void AConnectToABlankGuestThatWakesShowsTheModeFromItsFirstFrame()
    {
        // Live capture B (blank at connect, woken 7 s later): SURFACE_CREATE 1600x960, the server's whole-surface image,
        // MARK, then the guest's (0,0)-(1600,900) frame 30 ms later; no MONITORS_CONFIG at all.
        var (canvas, _) = Linux();
        using var _c = canvas;
        Mark(canvas); // a MARK without a surface (a guest blanking) arms nothing
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Draw(canvas, Surface);
        Assert.Equal(Surface, canvas.PrimaryArea);
        Mark(canvas);
        // As captured: the guest's frame carries a clip of one rectangle, its box.
        Draw(canvas, Mode, Mode);
        Assert.Equal(Mode, canvas.PrimaryArea);
        // Blank and wake while connected: the inferred area is kept for the same-size surface, so no strip flashes.
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Assert.Equal(Mode, canvas.PrimaryArea);
        Draw(canvas, Surface); // the server's image before the MARK doesn't count against it
        Mark(canvas);
        Draw(canvas, Mode);
        Assert.Equal(Mode, canvas.PrimaryArea);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void TheSettingIsOnByDefault() => Assert.True(new Canvas.Canvas().InferPaddedMonitor);

    [Fact]
    public void WithTheSettingOffALinuxGuestKeepsDecision0035sOriginalRule()
    {
        // The human's choice (2026-10-31): the inference is a setting. Off, the capture A and B orders show the whole
        // surface, and it counts as the monitor, as before L16.
        var (canvas, changes) = Linux();
        using var _c = canvas;
        canvas.InferPaddedMonitor = false;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Draw(canvas, Surface);
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1600, 960, 0, 0)));
        Mark(canvas);
        Assert.True(canvas.PrimaryAreaIsMonitor);
        changes.Clear();
        Draw(canvas, Mode);
        Draw(canvas, Mode);
        Assert.Equal(Surface, canvas.PrimaryArea);
        Assert.Empty(changes);
        // A wake while connected keeps that whole-surface head for the same-size surface (the L6 rule), as before.
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Assert.True(canvas.PrimaryAreaIsMonitor);
        Assert.Equal(0, canvas.Errors);

        // No config at all (capture B, a connect to a blank guest): the whole surface, provisional, as before.
        var (blank, _) = Linux();
        using var _b = blank;
        blank.InferPaddedMonitor = false;
        blank.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Draw(blank, Surface);
        Mark(blank);
        Draw(blank, Mode, Mode);
        Assert.Equal(Surface, blank.PrimaryArea);
        Assert.False(blank.PrimaryAreaIsMonitor);
        Assert.Equal(0, blank.Errors);
    }

    [Fact]
    public void TurningTheSettingOffGivesTheWholeSurfaceBackAtOnceAndOnInfersAgain()
    {
        var (canvas, changes) = Linux();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1600, 960, 0, 0)));
        Mark(canvas);
        Draw(canvas, Mode);
        Assert.Equal(Mode, canvas.PrimaryArea);
        changes.Clear();
        var lines = CaptureCanvasLog(() => canvas.InferPaddedMonitor = false);
        Assert.Equal(Surface, canvas.PrimaryArea);
        Assert.True(canvas.PrimaryAreaIsMonitor);
        Assert.Single(changes);
        Assert.Contains(lines, l => l.Contains("trimming Linux guest padding was turned off"));
        Draw(canvas, Mode);
        Assert.Equal(Surface, canvas.PrimaryArea);
        // Setting it again to the same value announces nothing.
        canvas.InferPaddedMonitor = false;
        Assert.Single(changes);
        // On: the guest's next full frame narrows it again.
        canvas.InferPaddedMonitor = true;
        Assert.Equal(Surface, canvas.PrimaryArea);
        Draw(canvas, Mode);
        Assert.Equal(Mode, canvas.PrimaryArea);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void AnInferredAreaKeptForAWakeIsDroppedWhenTheSettingGoesOff()
    {
        var (canvas, _) = Linux();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Mark(canvas);
        Draw(canvas, Mode);
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        canvas.InferPaddedMonitor = false; // while the guest is blank
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Assert.Equal(Surface, canvas.PrimaryArea);
        Assert.False(canvas.PrimaryAreaIsMonitor);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void WithoutALinuxAgentNothingIsInferred()
    {
        // Windows guests (and guests without an agent) keep decision 0035's rule: no config, or a whole-surface head,
        // shows the whole surface whatever the draws.
        var canvas = new Canvas.Canvas();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1024, 768));
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1024, 768, 0, 0)));
        Mark(canvas);
        Draw(canvas, new SpiceRect(0, 0, 1024, 728)); // a maximized window above a 40-row taskbar
        Assert.Equal(new SpiceRect(0, 0, 1024, 768), canvas.PrimaryArea);
        Assert.True(canvas.PrimaryAreaIsMonitor);
    }

    [Fact]
    public void ADrawOutsideTheInferredAreaGivesTheWholeSurfaceBackForGood()
    {
        var (canvas, changes) = Linux();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1024, 768));
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1024, 768, 0, 0)));
        Mark(canvas);
        Draw(canvas, new SpiceRect(0, 0, 1024, 728));
        Assert.Equal(new SpiceRect(0, 0, 1024, 728), canvas.PrimaryArea);
        changes.Clear();
        Draw(canvas, new SpiceRect(900, 740, 1000, 768)); // a bottom panel's clock: the rows are in use
        Assert.Equal(new SpiceRect(0, 0, 1024, 768), canvas.PrimaryArea);
        Assert.True(canvas.PrimaryAreaIsMonitor);
        Assert.Single(changes);
        Draw(canvas, new SpiceRect(0, 0, 1024, 728));
        Assert.Equal(new SpiceRect(0, 0, 1024, 768), canvas.PrimaryArea);
        // A new surface starts over.
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Mark(canvas);
        Draw(canvas, Mode);
        Assert.Equal(Mode, canvas.PrimaryArea);
    }

    [Theory]
    // Rows already drawn below the candidate.
    [InlineData(true, 0, 0, 0, 0)]
    // 64 or more uncovered rows or columns: not qxl padding.
    [InlineData(false, 0, 0, 1600, 896)]
    [InlineData(false, 0, 0, 1536, 960)]
    // Not from the origin, or the whole surface.
    [InlineData(false, 0, 1, 1600, 900)]
    [InlineData(false, 0, 0, 1600, 960)]
    public void OnlyAnUnclippedFrameFromTheOriginHoldingEveryDrawNarrowsTheArea(bool drawBelowFirst, int l, int t, int r, int b)
    {
        var (canvas, _) = Linux();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Mark(canvas);
        if (drawBelowFirst) { Draw(canvas, new SpiceRect(0, 930, 50, 960)); Draw(canvas, Mode); }
        else Draw(canvas, new SpiceRect(l, t, r, b));
        Assert.Equal(Surface, canvas.PrimaryArea);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void AClippedDrawDoesNotNarrowTheArea()
    {
        var (canvas, _) = Linux();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Mark(canvas);
        // Two clip rectangles, even ones that tile the box: not the qxl driver's whole-frame draw.
        Draw(canvas, Mode, new SpiceRect(0, 0, 800, 900), new SpiceRect(800, 0, 1600, 900));
        Draw(canvas, Mode, new SpiceRect(0, 0, 1600, 899));
        Assert.Equal(Surface, canvas.PrimaryArea);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void ConfigsNamingTheMonitorWinAndWholeSurfaceHeadsDoNot()
    {
        var (canvas, _) = Linux();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Mark(canvas);
        Draw(canvas, Mode);
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1600, 960, 0, 0)));
        Assert.Equal(Mode, canvas.PrimaryArea);
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1280, 720, 0, 0)));
        Assert.Equal(new SpiceRect(0, 0, 1280, 720), canvas.PrimaryArea);
        Assert.True(canvas.PrimaryAreaIsMonitor);
        // A real config on the surface leaves nothing to infer.
        Draw(canvas, Mode);
        Assert.Equal(new SpiceRect(0, 0, 1280, 720), canvas.PrimaryArea);
        Assert.Equal(0, canvas.Errors);
    }

    [Fact]
    public void TheL6KeptHeadStillWinsWithoutAnyInference()
    {
        // A real 1600x900 config, then a blank and wake (decision 0035's same-size rule): the area is a monitor at once.
        var (canvas, _) = Linux();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1600, 900, 0, 0)));
        canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Assert.Equal(Mode, canvas.PrimaryArea);
        Assert.True(canvas.PrimaryAreaIsMonitor);
    }

    [Fact]
    public void EveryAreaChangeIsLoggedOnceAndRepeatsAreSilent()
    {
        var (canvas, _) = Linux();
        using var _c = canvas;
        var lines = CaptureCanvasLog(() =>
        {
            canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
            Draw(canvas, Surface);
            canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1600, 960, 0, 0)));
            canvas.Apply(DisplayMsg.MonitorsConfig, MonitorsConfig((0, 1600, 960, 0, 0)));
            Mark(canvas);
            for (int i = 0; i < 5; i++) Draw(canvas, Mode);
            canvas.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        });
        Assert.Equal(4, lines.Count);
        Assert.Contains("(0,0 1600x960), the whole new 1600x960 primary surface", lines[0]);
        Assert.Contains("unchanged, from monitors config head #0 1600x960 at (0,0) of 1", lines[1]);
        Assert.Contains("(0,0 1600x900) of the 1600x960 primary surface, inferred", lines[2]);
        Assert.Contains("cleared", lines[3]);
    }

    [Fact]
    public void GuestDrawsAfterTheInferenceAllocateNothing()
    {
        var (canvas, _) = Linux();
        using var _c = canvas;
        canvas.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(1600, 960));
        Mark(canvas);
        ReadOnlyMemory<byte> frame = Fill(new SpiceRect(0, 0, 64, 64));
        ReadOnlyMemory<byte> full = Fill(Mode);
        canvas.Apply(DisplayMsg.DrawFill, full);
        Assert.Equal(Mode, canvas.PrimaryArea);
        void Work() { for (int i = 0; i < 2_000; i++) canvas.Apply(DisplayMsg.DrawFill, frame); }
        Work();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Work();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    static async Task<bool> InferenceAfterCaps(uint capsWord)
    {
        await using var session = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));
        var body = new byte[8];
        BitConverter.TryWriteBytes(body.AsSpan(4), capsWord); // request = 0: nothing is sent back
        var handle = typeof(SpiceSession).GetMethod("HandleAgentMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handle.Invoke(session, [VdAgent.AnnounceCapabilities, body]);
        return session.Canvas.GuestAgentIsLinux;
    }

    [Fact]
    public async Task OnlyALinuxAgentTurnsTheInferenceOn()
    {
        Assert.True(await InferenceAfterCaps(0x38de7)); // the Ubuntu guest's spice-vdagent: LF line endings
        Assert.False(await InferenceAfterCaps(1u << VdAgent.CapGuestLineEndCrlf | 1u << VdAgent.CapMonitorsConfig));
        Assert.False(await InferenceAfterCaps(1u << VdAgent.CapMonitorsConfig));
    }

    [Fact]
    public async Task UbuntuRichKeepsItsConfiguredMonitorWithTheLinuxAgent()
    {
        // The fixture's agent announces LF and its config names 640x480 on the 640x512 surface: nothing to infer, and
        // the golden image is unchanged.
        var opener = new ReplayOpener(Fixture("ubuntu-rich"));
        var vv = VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=replay\nport=5900\n");
        await using var session = new SpiceSession(vv, opener);
        session.Canvas.GuestAgentIsLinux = true; // as the agent would, whichever channel the replay delivers first
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.ConnectAsync(ct.Token);
        await opener.Display.Completion.WaitAsync(ct.Token);
        lock (session.Canvas.Sync)
        {
            Assert.Equal(new SpiceRect(0, 0, 640, 480), session.Canvas.PrimaryArea);
            Assert.True(session.Canvas.PrimaryAreaIsMonitor);
        }
        Assert.Equal(0, session.Canvas.Errors);
    }
}
