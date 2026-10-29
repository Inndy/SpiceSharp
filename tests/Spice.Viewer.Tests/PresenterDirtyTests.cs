using System.Reflection;
using Spice.Wire;

namespace SpiceViewer;

// These tests inspect an untrimmed production assembly; no Native AOT/reflection contract is asserted.
#pragma warning disable IL2026, IL2072, IL2075
public sealed class PresenterDirtyTests
{
    [Fact]
    public void ScaledDirtyRectMustCoverEveryPixelAffectedByLinearFiltering()
    {
        // Load the production calculation without constructing a presenter or creating a window/device.
        var assembly = Assembly.Load("SpiceViewer");
        var viewportType = assembly.GetType("SpiceViewer.Viewport", true)!;
        var viewport = viewportType.GetMethod("Compute")!.Invoke(null, [4000, 2400, 800, 600, false, 16384]);
        var toBuffer = assembly.GetType("SpiceViewer.Presenter", true)!
            .GetMethod("ToBuffer", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rectType = toBuffer.GetParameters()[2].ParameterType;
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var visible = Activator.CreateInstance(rectType)!;
        foreach (var (name, value) in new[] { ("left", 400), ("top", 0), ("right", 3600), ("bottom", 2400) })
            rectType.GetField(name, fields)!.SetValue(visible, value);
        var dirty = toBuffer.Invoke(null, [viewport, new SpiceRect(100, 100, 101, 101), visible])!;
        int Edge(string name) => (int)rectType.GetField(name, fields)!.GetValue(dirty)!;

        // Scale is 4. Output pixel (798,398) samples source coordinate (99.125,99.125):
        // the changed texel (100,100) contributes 1/64 of its value. It must be presented.
        // The shader draws this pixel, but the current one-buffer-pixel pad starts at (799,399).
        Assert.True(Edge("left") <= 798 && Edge("top") <= 398
            && Edge("right") > 798 && Edge("bottom") > 398,
            $"Bilinear footprint pixel (798,398) is outside dirty rectangle ({Edge("left")},{Edge("top")})-({Edge("right")},{Edge("bottom")}).");
    }
}
