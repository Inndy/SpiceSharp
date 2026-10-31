using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Spice.Wire;
using VvFile;
using SpiceCanvas = Spice.Canvas.Canvas;

namespace Spice.Diagnostics.Tests;

// Production methods on constructor-free objects; no HWND, device, worker, hook or connection is created.
// RenderOnce's two COM calls use recording vtables, never graphics APIs.
[Collection("Overlay presenter probes")]
public sealed unsafe class StatusOverlayReviewTests
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static readonly Assembly Viewer = Assembly.Load("SpiceViewer");
    static Type T(string name) => Viewer.GetType("SpiceViewer." + name, true)!;
    static void Set(object o, string name, object? value) => o.GetType().GetField(name, Fields)!.SetValue(o, value);
    static object? Get(object o, string name) => o.GetType().GetField(name, Fields)!.GetValue(o);
    static object? Call(object o, string name, params object?[] args) => o.GetType().GetMethod(name, Fields)!.Invoke(o, args);
    static object Window(VvConnection vv)
    {
        object w = RuntimeHelpers.GetUninitializedObject(T("MainWindow"));
        Set(w, "_vv", vv);
        return w;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingTitleDoesNotPutTheHostInOverlayText(string? title)
    {
        const string host = "private-host-sentinel.invalid";
        object w = Window(new VvConnection { Host = host, Title = title, Password = "ticket-sentinel" });
        object s = Activator.CreateInstance(T("OverlaySnapshot"), true)!;
        Call(w, "FillOverlay", s);
        Assert.DoesNotContain(host, (string)Get(s, "Title")!);
    }

    sealed class Probe : IDisposable
    {
        public readonly SpiceCanvas Canvas = new();
        public readonly object Presenter = RuntimeHelpers.GetUninitializedObject(T("Presenter"));
        public readonly object Overlay;
        public Probe()
        {
            object w = Window(new VvConnection { Host = "unused.invalid", Title = "guest" });
            var source = Activator.CreateInstance(T("MainWindow+OverlaySource"), Fields, null, [w], null)!;
            Overlay = Activator.CreateInstance(T("StatusOverlay"), Fields, null, [source], null)!;
            Set(Presenter, "_canvas", Canvas);
            Set(Presenter, "<AdapterName>k__BackingField", "test adapter");
            Set(Presenter, "_lock", new object());
            Set(Presenter, "_maxDim", 16384);
            Set(Presenter, "_layoutSet", true);
            Set(Presenter, "_clientW", 1600); Set(Presenter, "_clientH", 900);
            Set(Presenter, "_pending", new List<SpiceRect>());
            Set(Presenter, "_pendingPresent", new List<SpiceRect>());
            Set(Presenter, "_previous", new List<SpiceRect>());
            Set(Presenter, "_overlay", Overlay);
            // A placeholder supplies dimensions without constructing a surface or loading a native codec.
            var image = Activator.CreateInstance(T("Presenter+Placeholder"), [new byte[1600 * 900 * 4], 1600, 900]);
            Set(Presenter, "_placeholder", image);
            var compute = T("Viewport").GetMethod("Compute")!;
            Set(Presenter, "_vp", compute.Invoke(null, [1600, 900, 1600, 900, false, 16384]));
            Set(Presenter, "_guestW", 1600); Set(Presenter, "_guestH", 900);
            Set(Presenter, "_loggedComposition", true);
            Set(Presenter, "_overlayDirty", 1);
            Assert.True(Prepare());
        }
        public bool Prepare() => (bool)Call(Presenter, "PrepareOverlay", Overlay)!;
        public SpiceRect Place() => (SpiceRect)Call(Presenter, "PlaceOverlay", Get(Presenter, "_vp"))!;
        public void Resize()
        {
            Set(Presenter, "_clientW", 3200); Set(Presenter, "_clientH", 1800);
        }
        public void Dispose()
        {
            ((IDisposable)Get(Presenter, "_overlayBitmap")!).Dispose();
            Canvas.Dispose();
        }
    }

    [Fact]
    public void AspectLockedResizeDoesNotRedrawAnOtherwiseStaticOverlay()
    {
        using var p = new Probe();
        // Viewport is identical: DWM stretches the existing guest-sized buffer. No guest/event invalidation.
        p.Resize();
        Assert.False(p.Prepare());
    }

    static bool s_scroll;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static void Upload(void* self, void* resource, uint subresource, void* box, void* data, uint pitch, uint depth) { }
    [StructLayout(LayoutKind.Sequential)]
    struct PresentParameters { public uint Count; public void* Dirty; public void* Scroll; public void* Offset; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Present(void* self, uint sync, uint flags, PresentParameters* parameters)
    {
        s_scroll = parameters->Scroll != null;
        return 0;
    }
    static void PointerField(object o, string name, void* pointer)
    {
        var f = o.GetType().GetField(name, Fields)!;
        f.SetValue(o, Pointer.Box(pointer, f.FieldType));
    }

    [Fact]
    public void ScrollCannotCopyTheOldOverlayWhenTheOverlayShrinksAndMoves()
    {
        using var p = new Probe();
        var old = p.Place();
        Set(p.Presenter, "_overlayRect", old);
        p.Resize();
        // The guest scrolls during the resize: its damage is pending at the wake, so the overlay may be redrawn at the
        // new size on this present (a layout-only wake defers it; see the SO1 case above).
        var dest = new SpiceRect(1400, 800, 1404, 804);
        ((List<SpiceRect>)Get(p.Presenter, "_pending")!).Add(dest);
        Assert.True(p.Prepare());
        var current = p.Place();
        // Source is in the old overlay, outside the smaller new overlay. Destination is outside both.
        var from = new SpiceRect(old.Right - 4, old.Bottom - 4, old.Right, old.Bottom);
        Assert.True(from.Intersect(current).IsEmpty);
        Assert.False(from.Intersect(old).IsEmpty);
        Set(p.Presenter, "_scroll", true);
        Set(p.Presenter, "_scrollDest", dest);
        Set(p.Presenter, "_scrollDx", from.Left - dest.Left);
        Set(p.Presenter, "_scrollDy", from.Top - dest.Top);
        // ID3D11DeviceContext::UpdateSubresource (slot 48), IDXGISwapChain1::Present1 (slot 22).
        void** contextTable = stackalloc void*[49];
        contextTable[48] = (delegate* unmanaged[Stdcall]<void*, void*, uint, void*, void*, uint, uint, void>)&Upload;
        void** swapTable = stackalloc void*[23];
        swapTable[22] = (delegate* unmanaged[Stdcall]<void*, uint, uint, PresentParameters*, int>)&Present;
        void** context = contextTable; void** swap = swapTable;
        PointerField(p.Presenter, "_context", &context);
        PointerField(p.Presenter, "_swapChain", &swap);
        PointerField(p.Presenter, "_back", (void*)1); // non-null, only passed to the recording upload
        int* dirty = stackalloc int[20 * 4];
        var render = T("Presenter").GetMethod("RenderOnce", Fields)!;
        object?[] args = [new List<SpiceRect>(20), new List<SpiceRect>(20),
            Pointer.Box(dirty, render.GetParameters()[2].ParameterType), false];
        s_scroll = false;
        render.Invoke(p.Presenter, args);
        Assert.True((bool)args[3]!);
        Assert.False(s_scroll); // Otherwise Present1 moves the old text/background into dest.
    }
}

[CollectionDefinition("Overlay presenter probes", DisableParallelization = true)]
public sealed class OverlayPresenterProbeCollection;
