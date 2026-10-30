using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Spice.Session;
using Spice.Tests;
using Spice.Transport;
using Spice.Wire;
using SpiceViewer;
using VvFile;

namespace Spice.Replay.Tests;

// No window or host input API: protocol messages run in memory, and GuestView is the linked production pure helper.
#pragma warning disable IL2026, IL2075, IL2077, IL2080
[Collection("Allocation profiling")]
public sealed class DisplayReviewTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static SpiceSession Session() => new(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n"));

    sealed class PacketSink(int expectedBytes = 17) : MemoryStream
    {
        internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            lock (this) { base.Write(bytes.Span); if (Length >= expectedBytes) Ready.TrySetResult(); }
            return ValueTask.CompletedTask;
        }
        internal byte[] Snapshot() { lock (this) return ToArray(); }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public async Task AbsoluteMousePositionAddressesTheMonitorThatIsActuallyShown(uint monitorId)
    {
        await using var session = Session();
        using var sink = new PacketSink();
        await using var inputs = (SpiceChannel)typeof(SpiceChannel).GetConstructors(Private).Single().Invoke(
            [sink, new ChannelId(ChannelType.Inputs, 0),
                new LinkReply(LinkError.Ok, [], new CapabilitySet(CommonCap.MiniHeader), new CapabilitySet())]);
        typeof(SpiceSession).GetField("_inputs", Private)!.SetValue(session, inputs);
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(640, 512));
        byte[] config = MonitorAreaTests.MonitorsConfig((0, 640, 480, 0, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(config.AsSpan(4), monitorId);
        session.OnDisplayMessage(DisplayMsg.MonitorsConfig, config);
        Assert.True(DisplayParse.TryPrimaryHead(config, out var head, out _));
        Assert.Equal(monitorId, head.Id);
        Assert.Equal(new SpiceRect(0, 0, 640, 480), session.Canvas.PrimaryArea);

        // This is the widget's real mapping followed by the session's real serialized send, not OS input injection.
        var (x, y) = GuestView.ClientToGuest(1279, 959, 0, 0, 1280, 960, 640, 480);
        session.MousePosition(x, y);
        await sink.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] message = sink.Snapshot();
        Assert.Equal(InputsMsg.CMousePosition, BinaryPrimitives.ReadUInt16LittleEndian(message));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(2)));
        Assert.Equal(639u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(6)));
        Assert.Equal(479u, BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(10)));
        Assert.Equal((byte)monitorId, message[16]); // The selected head can remain #1 after server filters empty #0.
    }

    [Theory]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(3840, 2160, 640, 360)]
    [InlineData(640, 480, 320, 240)]
    public void DownscaledMonitorEdgesRemainReachable(int guestW, int guestH, int clientW, int clientH)
    {
        var atLastPixel = GuestView.ClientToGuest(clientW - 1, clientH - 1, 0, 0, clientW, clientH, guestW, guestH);
        Assert.Equal((guestW - 1, guestH - 1), atLastPixel);
        Assert.Equal((0, 0), GuestView.ClientToGuest(0, 0, 0, 0, clientW, clientH, guestW, guestH));
    }

    [Fact]
    public async Task DisplayStateEventsDescribeTheCanvasUnderItsLock()
    {
        await using var session = Session();
        var seen = new List<GuestDisplayState>();
        session.DisplayStateChanged += state =>
        {
            Assert.True(Monitor.IsEntered(session.Canvas.Sync));
            Assert.Equal(state, session.DisplayState);
            Assert.Equal(state == GuestDisplayState.On, session.Canvas.Primary is not null);
            seen.Add(state);
        };
        session.OnDisplayMessage(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(640, 512));
        session.OnDisplayMessage(DisplayMsg.MonitorsConfig, MonitorAreaTests.MonitorsConfig((0, 640, 480, 0, 0)));
        session.OnDisplayMessage(DisplayMsg.SurfaceDestroy, MonitorAreaTests.SurfaceDestroy());
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(800, 600));
        Assert.Equal([GuestDisplayState.Off, GuestDisplayState.On, GuestDisplayState.Off, GuestDisplayState.On], seen);
    }

    [Fact]
    public async Task UnlinkedKeysDoNotEnterTheSessionsHeldKeySet()
    {
        await using var session = Session();
        session.OnDisplayMessage(DisplayMsg.Mark, ReadOnlyMemory<byte>.Empty);
        session.KeyEvent(0x2A, true);
        var held = (HashSet<ushort>)typeof(SpiceSession).GetField("_heldKeys", Private)!.GetValue(session)!;
        Assert.Empty(held);
        Assert.False(GuestView.PointerToGuest(ViewState.DisplayOff));
        Assert.False(GuestView.KeysToGuest(ViewState.Connecting, false));
        Assert.True(GuestView.KeysToGuest(ViewState.Connecting, true));
        Assert.True(GuestView.KeysToGuest(ViewState.DisplayOff, true));
    }

    [Fact]
    public void WarmMonitorParsingAndUnchangedCanvasAreaAllocateNothingInACleanWindow()
    {
        using var canvas = new Spice.Canvas.Canvas();
        canvas.Apply(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(640, 512));
        byte[] config = MonitorAreaTests.MonitorsConfig((0, 640, 480, 0, 0));
        void Work() { for (int i = 0; i < 1000; i++) canvas.Apply(DisplayMsg.MonitorsConfig, config); }
        Work();
        var measurement = AllocationWindow.Measure("monitor area unchanged", Work, prepare: Work);
        Assert.Equal(0L, measurement.Bytes);
        Assert.Equal(new SpiceRect(0, 0, 640, 480), canvas.PrimaryArea);
    }

    static SpiceChannel Inputs(SpiceSession session, PacketSink sink)
    {
        var channel = (SpiceChannel)typeof(SpiceChannel).GetConstructors(Private).Single().Invoke(
            [sink, new ChannelId(ChannelType.Inputs, 0),
                new LinkReply(LinkError.Ok, [], new CapabilitySet(CommonCap.MiniHeader), new CapabilitySet())]);
        typeof(SpiceSession).GetField("_inputs", Private)!.SetValue(session, channel);
        return channel;
    }

    static void Head(SpiceSession session, uint id)
    {
        byte[] config = MonitorAreaTests.MonitorsConfig((0, 640, 480, 0, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(config.AsSpan(4), id);
        session.OnDisplayMessage(DisplayMsg.MonitorsConfig, config);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoalescedPositionKeepsItsCoordinatesAndMonitorTogetherAcrossAnIdOnlyChange(bool replacePending)
    {
        await using var session = Session();
        using var sink = new PacketSink(expectedBytes: 10 * 17);
        await using var inputs = Inputs(session, sink);
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(640, 512));
        Head(session, 1);
        // Fill the protocol's eight-motion window. The ninth position waits for an ACK.
        for (int i = 0; i < 8; i++) session.MousePosition(i, i);
        session.MousePosition(100, 200);
        Head(session, 2); // Same rectangle: no PrimaryChanged notification is required to publish the new ID.
        if (replacePending) session.MousePosition(111, 222);
        Head(session, 3);
        typeof(SpiceSession).GetMethod("OnInputsMessage", Private)!.Invoke(session,
            [InputsMsg.MouseMotionAck, ReadOnlyMemory<byte>.Empty]);
        session.MousePosition(123, 234); // A fresh position must use the new ID, not the saved one.
        await sink.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] bytes = sink.Snapshot();
        Assert.Equal(10 * 17, bytes.Length);
        Assert.Equal(InputsMsg.CMousePosition, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8 * 17)));
        Assert.Equal(replacePending ? 111u : 100u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 * 17 + 6)));
        Assert.Equal(replacePending ? 222u : 200u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8 * 17 + 10)));
        Assert.Equal(replacePending ? (byte)2 : (byte)1, bytes[8 * 17 + 16]);
        Assert.Equal((byte)3, bytes[9 * 17 + 16]);
        Assert.Equal(new SpiceRect(0, 0, 640, 480), session.Canvas.PrimaryArea);
    }

    [Theory]
    [InlineData(255u)]
    [InlineData(256u)]
    [InlineData(uint.MaxValue)]
    public async Task MonitorIdsAreCappedRatherThanWrappedOnTheWire(uint monitorId)
    {
        await using var session = Session();
        using var sink = new PacketSink();
        await using var inputs = Inputs(session, sink);
        session.OnDisplayMessage(DisplayMsg.SurfaceCreate, MonitorAreaTests.SurfaceCreate(640, 512));
        Head(session, monitorId);
        Assert.Equal(monitorId, session.Canvas.PrimaryMonitorId);
        session.MousePosition(12, 34);
        await sink.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(byte.MaxValue, sink.Snapshot()[16]);
        session.OnDisplayMessage(DisplayMsg.SurfaceDestroy, MonitorAreaTests.SurfaceDestroy());
        Assert.Equal(0u, session.Canvas.PrimaryMonitorId);
    }

    [Theory]
    // Enlarged: the last pixel of a guest pixel's footprint still addresses that pixel (not the previous one).
    [InlineData(1, 1, 1280, 960, 640, 480, 0, 0)]
    [InlineData(3, 3, 1280, 960, 640, 480, 1, 1)]
    [InlineData(639, 479, 640, 480, 640, 480, 639, 479)]
    // Each axis chooses its own policy; the image may shrink in one dimension and enlarge in the other.
    [InlineData(319, 1, 320, 960, 640, 480, 639, 0)]
    [InlineData(1, 239, 1280, 240, 640, 480, 0, 479)]
    // A single client pixel cannot represent both guest endpoints; the specified choice is the first.
    [InlineData(0, 0, 1, 1, 640, 480, 0, 0)]
    // Letterbox coordinates clamp after removing the image origin.
    [InlineData(-5, 300, 320, 240, 640, 480, 0, 479)]
    public void PerAxisMappingPreservesTheChosenScalePolicyWithAnImageOffset(
        int x, int y, int width, int height, int guestW, int guestH, int expectedX, int expectedY)
        => Assert.Equal((expectedX, expectedY),
            GuestView.ClientToGuest(x + 37, y + 53, 37, 53, width, height, guestW, guestH));

    // These cases call the real window's state handler without Create(), StartSession(), a message pump, a hook,
    // a graphics device or a Presenter worker. All user32 calls see an invalid, nonzero HWND: never HWND_NULL,
    // since InvalidateRect(HWND_NULL) would invalidate other applications' windows.
    sealed class WindowsStateFactAttribute : FactAttribute
    {
        public WindowsStateFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "The window-free state handler calls user32.";
        }
    }

    static Assembly ViewerAssembly()
    {
        // Build src/SpiceViewer/SpiceViewer.csproj --no-restore in the same configuration first.
        // Loading its assembly does not execute Program.Main or create a window/device. Keep this an explicit
        // prerequisite rather than adding a viewer ProjectReference that needs refreshed NuGet restore assets.
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "SpiceViewer.sln"))) root = Directory.GetParent(root)!.FullName;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string path = Path.Combine(root, "src", "SpiceViewer", "bin", configuration,
            "net10.0-windows10.0.17763.0", "SpiceViewer.dll");
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    sealed class WindowStateHarness : IDisposable
    {
        readonly Type windowType, presenterType;
        readonly AutoResetEvent wake = new(false);
        readonly ManualResetEvent stop = new(false);
        readonly CancellationTokenSource connect;
        public readonly object Window;

        public WindowStateHarness(SpiceSession session, string state)
        {
            var assembly = ViewerAssembly();
            windowType = assembly.GetType("SpiceViewer.MainWindow", throwOnError: true)!;
            presenterType = assembly.GetType("SpiceViewer.Presenter", throwOnError: true)!;
            Window = Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n")], culture: null)!;
            var hwnd = windowType.GetField("_hwnd", Private)!;
            // HWND_MESSAGE is a pseudo-handle, never an actual window. Unlike HWND_NULL, invalidating it cannot
            // invalidate the desktop. No CreateWindow call is made anywhere in this harness.
            var fromIntPtr = hwnd.FieldType.GetMethods(BindingFlags.Static | BindingFlags.Public).Single(m =>
                m.Name is "op_Explicit" or "op_Implicit" && m.ReturnType == hwnd.FieldType &&
                m.GetParameters() is [{ ParameterType: var p }] && p == typeof(nint));
            hwnd.SetValue(Window, fromIntPtr.Invoke(null, [(nint)(-3)]));
            Set("_session", session);
            Set("_state", Enum.Parse(windowType.GetField("_state", Private)!.FieldType, state));
            Set("_linked", true);
            Set("_focused", true);
            Set("_gotFirstFrame", true);
            connect = (CancellationTokenSource)Get("_connectCts")!;

            // A failed, already-exited worker: Shutdown signals its handles and returns on the current thread.
            // The production constructor is deliberately bypassed, so no D3D or native-window code can run.
            object presenter = RuntimeHelpers.GetUninitializedObject(presenterType);
            presenterType.GetField("_failed", Private)!.SetValue(presenter, true);
            presenterType.GetField("_thread", Private)!.SetValue(presenter, Thread.CurrentThread);
            presenterType.GetField("_wake", Private)!.SetValue(presenter, wake);
            presenterType.GetField("_stopEvent", Private)!.SetValue(presenter, stop);
            Set("_presenter", presenter);
        }

        public object? Get(string field) => windowType.GetField(field, Private)!.GetValue(Window);
        void Set(string field, object? value) => windowType.GetField(field, Private)!.SetValue(Window, value);
        // A const nuint's metadata constant reads back as a UInt32; OnPresenterNotify takes an nuint.
        public void NotifyFailure() => windowType.GetMethod("OnPresenterNotify", Private)!.Invoke(Window,
            [(nuint)Convert.ToUInt64(presenterType.GetField("NotifyFailed")!.GetValue(null))]);
        public bool Key(ushort vk, ushort scan, bool down) => (bool)windowType.GetMethod("OnKey")!
            .Invoke(Window, [vk, scan, down])!;
        public void Dispose()
        {
            // Never call OnDestroy (which saves settings or posts WM_QUIT) for an object with no real window.
            connect.Dispose(); wake.Dispose(); stop.Dispose();
            windowType.GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        }
    }

    [WindowsStateFact]
    public async Task PresenterFailureWhileDisplayOffMustEnterDisplayFailed()
    {
        await using var session = Session();
        using var window = new WindowStateHarness(session, "DisplayOff");
        window.NotifyFailure();
        Assert.Null(window.Get("_presenter"));
        Assert.Equal("DisplayFailed", window.Get("_state")!.ToString());
    }

    [WindowsStateFact]
    public async Task PresenterFailureWhileDisplayOffMustStopForwardingKeys()
    {
        await using var session = Session();
        using var sink = new PacketSink();
        await using var inputs = Inputs(session, sink);
        using var window = new WindowStateHarness(session, "DisplayOff");
        window.NotifyFailure();
        bool down = window.Key(0x41, 0x1E, true);
        bool up = window.Key(0x41, 0x1E, false);
        Assert.False(down || up, "A fresh A down/up was forwarded after the display-off presenter failed.");
        Assert.Empty(sink.Snapshot());
    }

    [WindowsStateFact]
    public async Task PresenterFailureWhileConnectedStillEntersDisplayFailed()
    {
        await using var session = Session();
        using var window = new WindowStateHarness(session, "Connected");
        window.NotifyFailure();
        Assert.Equal("DisplayFailed", window.Get("_state")!.ToString());
        Assert.False(window.Key(0x41, 0x1E, true));
    }
}
