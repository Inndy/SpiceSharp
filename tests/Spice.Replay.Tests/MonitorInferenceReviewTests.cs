using System.Buffers.Binary;
using System.Reflection;
using Spice.Canvas;
using Spice.Session;
using Spice.Wire;
using VvFile;
using static Spice.Replay.Tests.MonitorAreaTests;

namespace Spice.Replay.Tests;

// Synthetic wire messages and CPU frame buffers only. No viewer, network, native decoder or guest is needed.
// The MI regressions intentionally fail until the implementation is fixed; see the accompanying review.
#pragma warning disable IL2026, IL2075
[Collection("Allocation profiling")]
public sealed class MonitorInferenceReviewTests
{
    static readonly SpiceRect Whole = new(0, 0, 128, 128), Mode = new(0, 0, 128, 96);
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    static void Rect(BinaryWriter w, SpiceRect r)
    { w.Write(r.Top); w.Write(r.Left); w.Write(r.Bottom); w.Write(r.Right); }

    static byte[] Fill(SpiceRect box, params SpiceRect[] clips)
    {
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        w.Write(0u); Rect(w, box);
        if (clips.Length == 0) w.Write(SpiceClip.TypeNone);
        else
        {
            w.Write(SpiceClip.TypeRects); w.Write((uint)clips.Length);
            foreach (var clip in clips) Rect(w, clip);
        }
        w.Write((byte)BrushType.Solid); w.Write(0x336699u); w.Write(Ropd.OpPut);
        w.Write((byte)0); w.Write(0); w.Write(0); w.Write(0u);
        return m.ToArray();
    }

    static Canvas.Canvas NewCanvas(bool mark = true)
    {
        var c = new Canvas.Canvas { GuestAgentIsLinux = true };
        c.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(128, 128));
        if (mark) Mark(c);
        return c;
    }

    static void Mark(Canvas.Canvas c) => c.Apply(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
    static void Draw(Canvas.Canvas c, SpiceRect box, params SpiceRect[] clips)
        => c.Apply(DisplayMsg.DrawFill, Fill(box, clips));

    static void Video(Canvas.Canvas c, SpiceRect dest, SpiceClip clip = default)
    {
        // No scaling or codec: a decoded frame delivered through the production stream compositor entry point.
        var pixels = new byte[dest.Width * dest.Height * 4];
        Array.Fill(pixels, (byte)0xff);
        c.BlitFrame(0, dest, clip, new ImageView(pixels, dest.Width, dest.Height, dest.Width * 4, false), true);
    }

    [Fact]
    public void MI1_VideoOutsideACandidateBeforeInferenceMustCountAsAGuestDraw()
    {
        using var c = NewCanvas();
        Video(c, new SpiceRect(0, 112, 16, 128));
        Assert.Equal(0xffffffffu, c.Primary!.Row(127)[0]); // real pixels were written below the candidate
        Draw(c, Mode);
        Assert.Equal(0, c.Errors);
        Assert.Equal(Whole, c.PrimaryArea);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MI1_VideoOutsideAnInferredAreaMustWithdrawItIncludingAfterRecreate(bool recreate)
    {
        using var c = NewCanvas();
        Draw(c, Mode);
        Assert.Equal(Mode, c.PrimaryArea);
        if (recreate)
        {
            c.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
            c.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(128, 128));
            Mark(c);
            Assert.Equal(Mode, c.PrimaryArea);
        }
        int changes = 0;
        c.PrimaryChanged += _ => changes++;
        Video(c, new SpiceRect(0, 112, 16, 128));
        Assert.Equal(0xffffffffu, c.Primary!.Row(127)[0]);
        Assert.Equal(Whole, c.PrimaryArea);
        Assert.Equal(1, changes);
        Draw(c, Mode);
        Assert.Equal(Whole, c.PrimaryArea); // withdrawal is permanent for this surface
    }

    [Theory]
    [InlineData(0)] // one rectangle larger than the box
    [InlineData(1)] // overlapping rectangles normalize to one
    [InlineData(2)] // the second rectangle disappears when intersected with the box
    public void MI2_OnlyTheOriginalSingleClipEqualToTheBoxMayQualify(int variant)
    {
        using var c = NewCanvas();
        SpiceRect[] clips = variant switch
        {
            0 => [Whole],
            1 => [Mode, new SpiceRect(0, 0, 64, 96)],
            _ => [Mode, new SpiceRect(0, 112, 128, 128)],
        };
        Draw(c, Mode, clips);
        Assert.Equal(0, c.Errors);
        Assert.Equal(0xff336699u, c.Primary!.Row(95)[127]);
        Assert.Equal(Whole, c.PrimaryArea);
        // A subsequent genuine one-rectangle frame is still eligible.
        Draw(c, Mode, Mode);
        Assert.Equal(Mode, c.PrimaryArea);
    }

    [Fact]
    public void MI3_RejectingASmallerCandidateMustNotPreventALaterFrameContainingAllDraws()
    {
        using var c = NewCanvas();
        Draw(c, new SpiceRect(0, 100, 16, 112));
        Draw(c, Mode); // cannot crop to 96: the guest has drawn to 112
        Assert.Equal(Whole, c.PrimaryArea);
        // The actual mode is 128x112 (16 padding rows), and contains every post-MARK draw.
        var actual = new SpiceRect(0, 0, 128, 112);
        Draw(c, actual);
        Assert.Equal(0, c.Errors);
        Assert.Equal(actual, c.PrimaryArea);
    }

    [Theory]
    [InlineData(0)] // disable after waking
    [InlineData(1)] // withdraw by drawing outside after waking
    [InlineData(2)] // disable while blank, then wake
    public void MI4_KeptInferenceMustRetainTheConfiguredFallbackMonitorId(int action)
    {
        using var c = NewCanvas();
        byte[] config = MonitorsConfig((0, 128, 128, 0, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(config.AsSpan(4), 1u);
        c.Apply(DisplayMsg.MonitorsConfig, config);
        Draw(c, Mode);
        Assert.Equal(1u, c.PrimaryMonitorId);
        c.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        if (action == 2) c.InferPaddedMonitor = false;
        c.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(128, 128));
        Mark(c);
        if (action == 0) c.InferPaddedMonitor = false;
        if (action == 1) Draw(c, new SpiceRect(0, 112, 16, 128));
        Assert.Equal(Whole, c.PrimaryArea);
        Assert.Equal(0, c.Errors);
        // Inputs serialize this public ID as the MousePosition display_id (decision 0035 / review M1).
        Assert.Equal(1u, c.PrimaryMonitorId);
        Assert.True(c.PrimaryAreaIsMonitor);
    }

    [Fact]
    public void VideoBeforeMarkAndVideoClippedInsideTheAreaDoNotWithdrawInference()
    {
        using var c = NewCanvas(mark: false);
        Video(c, Whole);
        Mark(c);
        Draw(c, Mode);
        Assert.Equal(Mode, c.PrimaryArea);
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        w.Write(SpiceClip.TypeRects); w.Write(1u); Rect(w, Mode);
        var reader = new SpiceReader(m.ToArray());
        Video(c, Whole, SpiceClip.Read(ref reader));
        Assert.Equal(Mode, c.PrimaryArea);
        Assert.Equal(0, c.Errors);
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 0, true)]
    [InlineData(63, 63, true)]
    [InlineData(0, 0, false)]
    [InlineData(0, 64, false)]
    [InlineData(64, 0, false)]
    public void PaddingBoundsApplyIndependentlyToBothAxes(int padW, int padH, bool infer)
    {
        using var c = NewCanvas();
        var frame = new SpiceRect(0, 0, 128 - padW, 128 - padH);
        Draw(c, frame);
        Assert.Equal(infer ? frame : Whole, c.PrimaryArea);
        Assert.Equal(0, c.Errors);
    }

    [Fact]
    public void DifferentSizeRecreateDropsTheOldInferenceAndRearmsOnlyAfterItsOwnMark()
    {
        using var c = NewCanvas();
        Draw(c, Mode);
        c.Apply(DisplayMsg.SurfaceDestroy, SurfaceDestroy());
        c.Apply(DisplayMsg.SurfaceCreate, SurfaceCreate(128, 160));
        var biggerMode = new SpiceRect(0, 0, 128, 144);
        Draw(c, biggerMode);
        Assert.Equal(new SpiceRect(0, 0, 128, 160), c.PrimaryArea);
        Mark(c);
        Draw(c, biggerMode);
        Assert.Equal(biggerMode, c.PrimaryArea);
    }

    [Fact]
    public async Task TurningOffWaitsForTheDisplayLockAndAnnouncesUnderThatLock()
    {
        using var c = NewCanvas();
        Draw(c, Mode);
        bool callbackHeldLock = false;
        c.PrimaryChanged += _ => callbackHeldLock = Monitor.IsEntered(c.Sync);
        using var started = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        Task toggle;
        lock (c.Sync)
        {
            toggle = Task.Run(() =>
            {
                started.Set();
                try { c.InferPaddedMonitor = false; }
                finally { completed.Set(); }
            });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(completed.Wait(TimeSpan.FromMilliseconds(50)));
            Assert.Equal(Mode, c.PrimaryArea);
        }
        await toggle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(callbackHeldLock);
        Assert.Equal(Whole, c.PrimaryArea);
        Assert.False(c.InferPaddedMonitor);
    }

    static void Caps(SpiceSession session, uint bits)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), bits);
        typeof(SpiceSession).GetMethod("HandleAgentMessage", Private)!
            .Invoke(session, [VdAgent.AnnounceCapabilities, body]);
    }

    [Fact]
    public async Task AgentRestartKeepsGuestIdentityButANewSessionStartsUnknown()
    {
        var vv = VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n");
        await using var s = new SpiceSession(vv);
        Caps(s, 1u << VdAgent.CapGuestLineEndLf);
        Assert.True(s.Canvas.GuestAgentIsLinux);
        typeof(SpiceSession).GetMethod("OnAgentDisconnected", Private)!.Invoke(s, null);
        Assert.False(s.AgentConnected);
        Assert.True(s.Canvas.GuestAgentIsLinux); // agent logout/restart is still the same guest
        Caps(s, 0); // a replacement agent without a line-ending announcement doesn't erase known identity
        Assert.True(s.Canvas.GuestAgentIsLinux);
        await using var fresh = new SpiceSession(vv);
        Assert.False(fresh.Canvas.GuestAgentIsLinux);
        Caps(fresh, (1u << VdAgent.CapGuestLineEndLf) | (1u << VdAgent.CapGuestLineEndCrlf));
        Assert.False(fresh.Canvas.GuestAgentIsLinux);
        Caps(fresh, 1u << VdAgent.CapGuestLineEndCrlf);
        Assert.False(fresh.Canvas.GuestAgentIsLinux);
    }
}
