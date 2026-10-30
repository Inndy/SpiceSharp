using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Spice.Native;

namespace Spice.Native.Tests;

// The isolated assembly/reflection path is a JIT-only compatibility probe, not an AOT publishing test.
#pragma warning disable IL2026, IL2072, IL2075
public sealed class IdleMemoryReviewTests
{
    [Fact]
    public void TrimKeepsCrossImageReferencesValidDuringConcurrentDecode()
    {
        using var window = new GlzWindow();
        using var decoder = new GlzDecoder(window);
        byte[] output = new byte[4];
        Assert.True(decoder.TryDecode(Image(0, false), 1, 1, output, 4, out _));
        Assert.True(decoder.TryDecode(Image(1, false), 1, 1, output, 4, out _));
        Assert.Equal(4, window.TrimPool()); // image 0 retired, image 1 still needed
        Assert.True(decoder.TryDecode(Image(2, true), 1, 1, output, 4, out _));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, output);
        Parallel.Invoke(
            () =>
            {
                for (ulong id = 3; id < 503; id++)
                {
                    Assert.True(decoder.TryDecode(Image(id, true), 1, 1, output, 4, out _));
                    Assert.Equal(new byte[] { 10, 20, 30, 255 }, output);
                }
            },
            () => { for (int i = 0; i < 1000; i++) window.TrimPool(); });
    }

    [Fact]
    public void MissingOptionalExportReturnsZeroAndDoesNotDisableDecoding()
    {
        // Give an isolated copy of the assembly a DLL with the trim export hidden. This exercises actual
        // EntryPointNotFoundException binding without changing the process's main codec or s_noTrim flag.
        byte[] dll = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "spicecodec.dll"));
        byte[] export = Encoding.ASCII.GetBytes("sc_glz_window_trim\0");
        int offset = dll.AsSpan().IndexOf(export);
        Assert.True(offset >= 0, "build the current native DLL before running this test");
        dll[offset + export.Length - 2] = (byte)'x';
        string path = Path.Combine(Path.GetTempPath(), $"spicecodec-review-{Guid.NewGuid():N}.dll");
        var context = new AssemblyLoadContext("codec without optional trim", isCollectible: true);
        nint library = 0;
        try
        {
            File.WriteAllBytes(path, dll);
            library = NativeLibrary.Load(path);
            Assert.False(NativeLibrary.TryGetExport(library, "sc_glz_window_trim", out _));
            Assembly assembly = context.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Spice.Native.dll"));
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "spicecodec.dll" ? library : 0);
            Type windowType = assembly.GetType(typeof(GlzWindow).FullName!, throwOnError: true)!;
            using var window = (IDisposable)Activator.CreateInstance(windowType)!;
            MethodInfo trim = windowType.GetMethod(nameof(GlzWindow.TrimPool))!;
            Assert.Equal(0L, trim.Invoke(window, null));
            Assert.Equal(0L, trim.Invoke(window, null));
            // The native decoder can still use the same window after the optional binding failed.
            nint handle = (nint)windowType.GetField("Handle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            unsafe
            {
                var create = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(library, "sc_glz_create");
                var decode = (delegate* unmanaged[Cdecl]<nint, byte*, nuint, byte**, int*, int*, int*, int*, int>)
                    NativeLibrary.GetExport(library, "sc_glz_decode");
                var destroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "sc_glz_destroy");
                nint decoder = create(handle);
                Assert.NotEqual(0, decoder);
                try
                {
                    byte[] data = Image(0, false);
                    byte* output = null;
                    int w, h, stride, top;
                    fixed (byte* input = data)
                        Assert.Equal(0, decode(decoder, input, (nuint)data.Length, &output, &w, &h, &stride, &top));
                    Assert.Equal(10, output[0]); Assert.Equal(20, output[1]); Assert.Equal(30, output[2]);
                }
                finally { destroy(decoder); }
            }
        }
        finally
        {
            context.Unload();
            if (library != 0) NativeLibrary.Free(library);
            File.Delete(path);
        }
    }

    static byte[] Image(ulong id, bool reference)
    {
        byte[] data = new byte[reference ? 36 : 37];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x20205A4C);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x10001);
        data[8] = 0x18;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(9), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(17), 4);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(21), id);
        if (reference)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(29), 1); // retain previous image
            data[33] = 0x20; data[34] = 0; data[35] = 1; // one pixel from previous image, offset 0
        }
        else { data[34] = 10; data[35] = 20; data[36] = 30; }
        return data;
    }
}
