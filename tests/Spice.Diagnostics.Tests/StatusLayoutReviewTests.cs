using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Spice.Wire;

namespace Spice.Diagnostics.Tests;

// Calls only GDI memory-DC operations and the production text renderer on an uninitialized window object.
// Never constructs MainWindow, creates an HWND, connects a session, or starts a presenter.
public sealed partial class StatusLayoutReviewTests
{
    static readonly Type Window = Assembly.Load("SpiceViewer").GetType("SpiceViewer.MainWindow", true)!;
    const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(320, 240, 144)]
    [InlineData(296, 183, 144)]
    [InlineData(320, 240, 192)]
    public void CloseButtonStaysInsideASmallClient(int width, int height, uint dpi)
    {
        var method = Window.GetMethod("CloseButtonTop", PrivateStatic)!;
        object rect = Activator.CreateInstance(method.GetParameters()[0].ParameterType,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [0, 0, width, height], null)!;
        int top = (int)method.Invoke(null, [rect, dpi])!;
        Assert.InRange(top, 0, height - (int)(32 * dpi / 96));
        Assert.True(110 * dpi / 96 <= width);
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(320, 180)]
    [InlineData(800, 480)]
    public void MessageImageKeepsEveryWrappedReasonLine(int width, int height)
    {
        const string body = "The server can't be reached. Check your network or VPN connection and try again.";
        // No title/detail: all non-background pixels must belong to the reason, including its last wrapped line.
        byte[] pixels = new byte[width * height * 4];
        MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()).Fill(0xFF202020);
        Window.GetMethod("DrawText", PrivateInstance)!.Invoke(RuntimeHelpers.GetUninitializedObject(Window),
            [pixels, width, height, new SpiceRect(0, 0, width, height), "", body, null]);

        nint dc = CreateCompatibleDC(0);
        Assert.NotEqual(0, dc);
        object font = Window.GetMethod("MakeFont", PrivateStatic)!.Invoke(null, [-Math.Max(12, height / 45), 400])!;
        nint fontHandle = HandleValue(font);
        nint old = SelectObject(dc, fontHandle);
        try
        {
            var rect = new NativeRect { Left = width / 10, Right = width * 9 / 10 };
            // Same wrapping flags as production, but the full measured height rather than the capped body rectangle.
            const uint wrap = 0x1 | 0x10 | 0x2000 | 0x800; // CENTER, WORDBREAK, EDITCONTROL, NOPREFIX
            Assert.True(DrawText(dc, body, body.Length, ref rect, wrap | 0x400) > 0);
            int bodyTop = height / 2 - height / 50;
            int lastInkRow = -1;
            var px = MemoryMarshal.Cast<byte, uint>(pixels);
            for (int y = bodyTop; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (px[y * width + x] != 0xFF202020) lastInkRow = y;
            Assert.True(lastInkRow >= bodyTop + rect.Bottom - Math.Max(12, height / 45),
                $"Reason needs {rect.Bottom} px; last painted row is {lastInkRow - bodyTop} px below its top.");
        }
        finally { SelectObject(dc, old); DeleteObject(fontHandle); DeleteDC(dc); }
    }

    [Fact]
    public unsafe void StatusWrapsATitleWithNoWordBreaks()
    {
        const int width = 288;
        string title = "Connecting to " + new string('W', 120);
        nint dc = CreateCompatibleDC(0);
        Assert.NotEqual(0, dc);
        object font = Window.GetMethod("MakeFont", PrivateStatic)!.Invoke(null, [-14, 400])!;
        nint fontHandle = HandleValue(font), old = SelectObject(dc, fontHandle);
        try
        {
            var method = Window.GetMethod("MeasureText", PrivateStatic)!;
            var parameters = method.GetParameters();
            object hdc = Marshal.PtrToStructure((nint)(&dc), parameters[0].ParameterType)!;
            const uint wrap = 0x1 | 0x10 | 0x2000 | 0x800;
            object format = Enum.ToObject(parameters[4].ParameterType, wrap);
            int actual = (int)method.Invoke(null, [hdc, title, 0, width, format])!;
            // Independently measure a single W and line height to establish a lower bound on the rows needed.
            var glyph = new NativeRect();
            DrawText(dc, "W", 1, ref glyph, 0x400 | 0x20); // CALCRECT | SINGLELINE
            int minimumRows = (120 * glyph.Right + width - 1) / width;
            Assert.True(actual >= minimumRows * glyph.Bottom,
                $"120 W glyphs need at least {minimumRows} rows ({minimumRows * glyph.Bottom} px); measured {actual} px.");
        }
        finally { SelectObject(dc, old); DeleteObject(fontHandle); DeleteDC(dc); }
    }

    static unsafe nint HandleValue(object handle)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        object value = handle.GetType().GetField("Value", flags)?.GetValue(handle)
            ?? handle.GetType().GetProperty("Value", flags)!.GetValue(handle)!;
        return value is nint p ? p : (nint)Pointer.Unbox(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] private static partial int DeleteDC(nint dc);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DrawText(nint dc, string text, int length, ref NativeRect rect, uint format);
}
