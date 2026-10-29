using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.System.StationsAndDesktops;
using Xunit.Abstractions;
using SpiceCanvas = Spice.Canvas.Canvas;

namespace SpiceViewer;

[CollectionDefinition("Presenter desktop", DisableParallelization = true)]
public sealed class PresenterDesktopCollection;

/// <summary>Skip only when a visible/input desktop or a D3D11 WARP device is unavailable.</summary>
public sealed class PresenterDesktopFactAttribute : FactAttribute
{
    public PresenterDesktopFactAttribute() => Skip = DesktopAvailability.Reason();
}

static unsafe class DesktopAvailability
{
    public static string? Reason()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            return "Presenter integration requires an interactive Windows desktop.";
        USEROBJECTFLAGS flags;
        uint needed;
        if (!PInvoke.GetUserObjectInformation(new HANDLE(PInvoke.GetProcessWindowStation().Value), USER_OBJECT_INFORMATION_INDEX.UOI_FLAGS,
            &flags, (uint)sizeof(USEROBJECTFLAGS), &needed) || (flags.dwFlags & 1) == 0)
            return "The test process has no visible window station (WSF_VISIBLE).";
        var desktop = PInvoke.OpenInputDesktop(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
        if (desktop.IsNull) return "The input desktop is unavailable or inaccessible (locked/noninteractive session).";
        try
        {
            string? input = DesktopName(new HANDLE(desktop.Value));
            string? current = DesktopName(new HANDLE(PInvoke.GetThreadDesktop(PInvoke.GetCurrentThreadId()).Value));
            if (input is null || current is null) return "The test/input desktop names cannot be queried.";
            if (!string.Equals(input, current, StringComparison.OrdinalIgnoreCase))
                return "The test runs on an isolated/background desktop, not the interactive input desktop.";
        }
        finally { PInvoke.CloseDesktop(desktop); }

        ID3D11Device* device = null;
        ID3D11DeviceContext* context = null;
        D3D_FEATURE_LEVEL level;
        var hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0, PInvoke.D3D11_SDK_VERSION,
            &device, &level, &context);
        if (context is not null) context->Release();
        if (device is not null) device->Release();
        return hr.Failed ? $"No usable D3D11 WARP device: {hr}." : null;
    }

    static string? DesktopName(HANDLE desktop)
    {
        char* name = stackalloc char[256];
        uint needed;
        return PInvoke.GetUserObjectInformation(desktop, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, name, 512, &needed)
            ? new string(name) : null;
    }
}

// Calls the real, untrimmed Presenter assembly through its public constructor/members. No private flags,
// worker events or COM pointers are inspected or mutated; reflection only crosses the internal type boundary.
#pragma warning disable IL2026, IL2072, IL2075, IL2080, IL3050
[Collection("Presenter desktop")]
public sealed class PresenterDesktopTests(ITestOutputHelper output)
{
    sealed class WindowPresenter : IDisposable
    {
        const int Width = 192, Height = 128;
        readonly string? previousWarp = Environment.GetEnvironmentVariable("SPICEVIEWER_FORCE_WARP");
        readonly Action<LogLevel, string>? previousLog = Log.Sink;
        readonly LogLevel previousLevel = Log.MinLevel;
        readonly ConcurrentQueue<string> logs = new();
        readonly Type type = Assembly.Load("SpiceViewer").GetType("SpiceViewer.Presenter", true)!;
        readonly SpiceCanvas canvas = new();
        readonly object presenter;
        readonly FieldInfo count;
        public readonly HWND Window;

        public unsafe WindowPresenter()
        {
            Environment.SetEnvironmentVariable("SPICEVIEWER_FORCE_WARP", "1");
            Log.MinLevel = LogLevel.Debug;
            Log.Sink = (_, message) => logs.Enqueue(message);
            try
            {
                RECT rect = new(0, 0, Width, Height);
                const WINDOW_STYLE style = WINDOW_STYLE.WS_OVERLAPPEDWINDOW;
                const WINDOW_EX_STYLE exStyle = WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                    | WINDOW_EX_STYLE.WS_EX_TOPMOST;
                Assert.True(PInvoke.AdjustWindowRectEx(ref rect, style, false, exStyle));
                Window = PInvoke.CreateWindowEx(exStyle, "STATIC", "SpiceViewer presenter regression test", style,
                    80, 80, rect.Width, rect.Height, HWND.Null, default, default, null);
                Assert.False(Window.IsNull, "Cannot create the test-owned Win32 window.");
                PInvoke.ShowWindow(Window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
                byte[] surface = new byte[20];
                var w = new SpiceWriter(surface);
                w.U32(0); w.U32(Width); w.U32(Height); w.U32(SurfaceCreate.Format32_xRGB); w.U32(1);
                canvas.Apply(DisplayMsg.SurfaceCreate, surface);
                var ctor = type.GetConstructors().Single();
                // CsWin32's HWND types belong to their generating assemblies. Copy the native handle value into
                // the production public-constructor argument rather than depending on its generated conversions.
                nint windowValue = (nint)Window.Value;
                object hwnd = Marshal.PtrToStructure((nint)(&windowValue), ctor.GetParameters()[0].ParameterType)!;
                presenter = ctor.Invoke([hwnd, canvas]);
                count = type.GetField("PresentCount")!;
                Assert.True((bool)type.GetProperty("IsSoftware")!.GetValue(presenter)!);
                type.GetProperty("FullFrames")!.SetValue(presenter, false);
                PInvoke.GetClientRect(Window, out var client);
                Call("SetLayout", client.Width, client.Height, true);
                Call("InvalidateAll");
                Until(() => Presents >= 1, "the initial WARP frame");
                Settle(); // Initial layout and full-frame wakes may straddle the first asynchronous present.
            }
            catch
            {
                if (presenter is not null) Call("Shutdown", 2000);
                if (!Window.IsNull) PInvoke.DestroyWindow(Window);
                canvas.Dispose();
                Environment.SetEnvironmentVariable("SPICEVIEWER_FORCE_WARP", previousWarp);
                Log.Sink = previousLog; Log.MinLevel = previousLevel;
                throw;
            }
        }

        public long Presents => (long)count.GetValue(presenter)!;
        public string Adapter => (string)type.GetProperty("AdapterName")!.GetValue(presenter)!;
        public void Suspend(bool value) => Call("SetSuspended", value);
        public void ChangeGuest(uint color)
        {
            lock (canvas.Sync) canvas.Primary!.Row(0).Fill(color);
            Call("Invalidate", new SpiceRect(0, 0, Width, 1));
        }
        void Call(string name, params object[] args)
        {
            try { type.GetMethod(name)!.Invoke(presenter, args); }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
        }
        void Healthy() => Assert.False((bool)type.GetProperty("HasFailed")!.GetValue(presenter)!, "Presenter worker failed.");
        static void Pump()
        {
            while (PInvoke.PeekMessage(out var message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        public void Until(Func<bool> condition, string description, int timeoutMs = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < timeoutMs)
            {
                Pump(); Healthy(); Thread.Sleep(5);
            }
            Healthy(); Assert.True(condition(), $"Timed out waiting for {description}; PresentCount={Presents}.\n{string.Join('\n', logs)}");
        }
        public void Stable(int milliseconds = 300)
        {
            long before = Presents;
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                Pump(); Healthy();
                Assert.True(before == Presents, $"Unexpected present: {before} -> {Presents}.\n{string.Join('\n', logs)}");
                Thread.Sleep(5);
            }
        }
        void Settle()
        {
            long previous = Presents;
            var total = Stopwatch.StartNew();
            var quiet = Stopwatch.StartNew();
            while (quiet.ElapsedMilliseconds < 150 && total.ElapsedMilliseconds < 3000)
            {
                Pump(); Healthy();
                long now = Presents;
                if (now != previous) { previous = now; quiet.Restart(); }
                Thread.Sleep(5);
            }
            Assert.True(quiet.ElapsedMilliseconds >= 150, "Presenter did not become idle after initialization.");
        }
        public void Dispose()
        {
            try
            {
                PInvoke.ShowWindow(Window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
                Call("Shutdown", 2000);
            }
            finally
            {
                PInvoke.DestroyWindow(Window);
                canvas.Dispose();
                Environment.SetEnvironmentVariable("SPICEVIEWER_FORCE_WARP", previousWarp);
                Log.Sink = previousLog; Log.MinLevel = previousLevel;
            }
        }
    }

    [PresenterDesktopFact]
    public void RepeatedSetSuspendedFalseDoesNotRequestFullFrame()
    {
        using var viewer = new WindowPresenter();
        output.WriteLine($"Adapter: {viewer.Adapter}");
        for (int i = 0; i < 32; i++) viewer.Suspend(false);
        viewer.Stable(1200);
        long before = viewer.Presents;
        viewer.ChangeGuest(0xFF336699);
        viewer.Until(() => viewer.Presents > before, "a subsequent guest update (worker still healthy)");
    }

    [PresenterDesktopFact]
    public void MinimizedSuspensionStopsPresentsAndRestorePresentsPendingFrameWithoutGuestUpdate()
    {
        using var viewer = new WindowPresenter();
        output.WriteLine($"Adapter: {viewer.Adapter}");
        viewer.Suspend(true); // The real window forwards WM_SIZE/SIZE_MINIMIZED through this public API.
        PInvoke.ShowWindow(viewer.Window, SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE);
        Assert.True(PInvoke.IsIconic(viewer.Window));
        for (uint i = 0; i < 8; i++) viewer.ChangeGuest(0xFF000000 | i);
        viewer.Stable(1200);
        long before = viewer.Presents;
        PInvoke.ShowWindow(viewer.Window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        Assert.False(PInvoke.IsIconic(viewer.Window));
        viewer.Suspend(false);
        viewer.Until(() => viewer.Presents > before, "the pending frame after restore, without another guest update");
        viewer.Stable(300);
    }

}
