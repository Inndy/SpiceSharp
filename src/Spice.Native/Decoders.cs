using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Spice.Native;

public enum ScImageType { Invalid = 0, Gray, Rgb16, Rgb24, Rgb32, Rgba, Plt1Le, Plt1Be, Plt4Le, Plt4Be, Plt8, Xxxa }

internal static class Pixels
{
    internal static bool Fits(int w, int h, int stride, int length) =>
        w > 0 && h > 0 && (long)w * h <= 67108864 && stride >= (long)w * 4 && (long)(h - 1) * stride + (long)w * 4 <= length;
    internal static void Copy(ReadOnlySpan<byte> src, int srcStride, Span<byte> dst, int dstStride, int w, int h, bool topDown, bool alpha)
    {
        for (int y = 0; y < h; y++)
        {
            var row = dst.Slice(y * dstStride, w * 4);
            src.Slice((topDown ? y : h - 1 - y) * srcStride, w * 4).CopyTo(row);
            if (!alpha) for (int i = 3; i < row.Length; i += 4) row[i] = 255;
        }
    }
    internal static void Grow(ref byte[] buffer, int size)
    {
        if (buffer.Length < size) buffer = GC.AllocateUninitializedArray<byte>(size);
    }
}

/// <summary>Reusable, serialized QUIC decoder. Non-alpha output is opaque BGRA.</summary>
public sealed unsafe class QuicDecoder : IDisposable
{
    private readonly object gate = new();
    private nint handle = CodecApi.sc_quic_create();
    public QuicDecoder() { if (handle == 0) throw new OutOfMemoryException(); }
    public bool TryDecode(ReadOnlySpan<byte> data, int width, int height, Span<byte> dst, int dstStride, out ScImageType type)
    {
        type = ScImageType.Invalid;
        if (!Pixels.Fits(width, height, dstStride, dst.Length) || data.Length < 24 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) != width || BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != height) return false;
        lock (gate)
        {
            if (handle == 0) return false;
            fixed (byte* input = data) fixed (byte* output = dst)
            {
                if (CodecApi.sc_quic_begin(handle, input, (nuint)data.Length, out int w, out int h, out var t) != 0 || w != width || h != height) return false;
                if (CodecApi.sc_quic_decode(handle, output, dstStride) != 0) return false;
                if (t != ScImageType.Rgba) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) dst[y * dstStride + x * 4 + 3] = 255;
                type = t;
                return true;
            }
        }
    }
    public void Dispose() { lock (gate) { if (handle != 0) CodecApi.sc_quic_destroy(handle); handle = 0; } GC.SuppressFinalize(this); }
    ~QuicDecoder() { Dispose(); }
}

/// <summary>Reusable LZ decoder. Scratch storage grows only when a larger image is seen.
/// RGB/palette output alpha is 255. XXXA writes alpha while preserving destination RGB.</summary>
public sealed unsafe class LzDecoder : IDisposable
{
    private readonly object gate = new();
    private nint handle = CodecApi.sc_lz_create();
    private byte[] scratch = [];
    public LzDecoder() { if (handle == 0) throw new OutOfMemoryException(); }
    public bool TryDecode(ReadOnlySpan<byte> data, ReadOnlySpan<uint> palette, int width, int height, Span<byte> dst, int dstStride, out ScImageType type)
    {
        type = ScImageType.Invalid;
        if (!Pixels.Fits(width, height, dstStride, dst.Length) || data.Length < 28 || palette.Length > 256 ||
            BinaryPrimitives.ReadUInt32BigEndian(data[12..]) != width || BinaryPrimitives.ReadUInt32BigEndian(data[16..]) != height) return false;
        // sc_lz_decode writes rows of width*4 bytes, palette types included. Room for palette rows padded to whole
        // bytes lets it decode in place without allocating, and keeps a DLL that writes padded rows inside the buffer.
        long bytes = (long)((width + 7) & ~7) * height * 4;
        if (bytes > int.MaxValue) return false;
        // Another interface version could write past the buffer or in another layout.
        if (NativeCodecs.SpiceCodecError is not null) return false;
        lock (gate)
        {
            if (handle == 0) return false;
            fixed (byte* input = data) fixed (uint* pal = palette)
            {
                if (CodecApi.sc_lz_begin(handle, input, (nuint)data.Length, pal, palette.Length, out int w, out int h, out var t, out int top) != 0 || w != width || h != height) return false;
                Pixels.Grow(ref scratch, (int)bytes);
                fixed (byte* output = scratch) if (CodecApi.sc_lz_decode(handle, output, (nuint)scratch.Length) != 0) return false;
                if (t == ScImageType.Xxxa)
                {
                    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) dst[y * dstStride + x * 4 + 3] = scratch[((top != 0 ? y : h - 1 - y) * w + x) * 4 + 3];
                }
                else Pixels.Copy(scratch, w * 4, dst, dstStride, w, h, top != 0, t == ScImageType.Rgba);
                type = t;
                return true;
            }
        }
    }
    public void Dispose() { lock (gate) { if (handle != 0) CodecApi.sc_lz_destroy(handle); handle = 0; scratch = []; } GC.SuppressFinalize(this); }
    ~LzDecoder() { Dispose(); }
}

public sealed class GlzWindow : IDisposable
{
    internal readonly object Gate = new();
    internal nint Handle = CodecApi.sc_glz_window_create();
    internal bool Closed;
    private int users;
    public GlzWindow() { if (Handle == 0) throw new OutOfMemoryException(); }
    internal void Retain() { ObjectDisposedException.ThrowIf(Closed, this); users++; }
    internal void Release() { users--; DestroyIfUnused(); }
    private void DestroyIfUnused() { if (Closed && users == 0 && Handle != 0) { CodecApi.sc_glz_window_destroy(Handle); Handle = 0; } }
    public void Clear() { lock (Gate) { if (!Closed) CodecApi.sc_glz_window_clear(Handle); } }
    public void Dispose() { lock (Gate) { Closed = true; DestroyIfUnused(); } GC.SuppressFinalize(this); }
    ~GlzWindow() { Dispose(); }
}

public sealed unsafe class GlzDecoder : IDisposable
{
    private readonly GlzWindow window;
    private nint handle;
    public GlzDecoder(GlzWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        this.window = window;
        lock (window.Gate)
        {
            window.Retain();
            handle = CodecApi.sc_glz_create(window.Handle);
            if (handle == 0) { window.Release(); throw new OutOfMemoryException(); }
        }
    }
    public bool TryDecode(ReadOnlySpan<byte> data, int width, int height, Span<byte> dst, int dstStride, out ScImageType type)
    {
        type = ScImageType.Invalid;
        if (!Pixels.Fits(width, height, dstStride, dst.Length) || data.Length < 33 ||
            BinaryPrimitives.ReadUInt32BigEndian(data[9..]) != width || BinaryPrimitives.ReadUInt32BigEndian(data[13..]) != height) return false;
        lock (window.Gate)
        {
            if (handle == 0 || window.Closed) return false;
            fixed (byte* input = data)
            {
                if (CodecApi.sc_glz_decode(handle, input, (nuint)data.Length, out byte* output, out int w, out int h, out int stride, out int top) != 0 || output == null || w != width || h != height || stride < (long)w * 4 || (long)stride * h > int.MaxValue) return false;
                var t = (data[8] & 15) == 9 ? ScImageType.Rgba : (data[8] & 15) == 6 ? ScImageType.Rgb16 : (data[8] & 15) == 7 ? ScImageType.Rgb24 : ScImageType.Rgb32;
                Pixels.Copy(new ReadOnlySpan<byte>(output, stride * h), stride, dst, dstStride, w, h, top != 0, t == ScImageType.Rgba);
                type = t;
                return true;
            }
        }
    }
    public void Dispose() { if (window is null) return; lock (window.Gate) { if (handle != 0) { CodecApi.sc_glz_destroy(handle); handle = 0; window.Release(); } } GC.SuppressFinalize(this); }
    ~GlzDecoder() { Dispose(); }
}

public sealed unsafe class JpegDecoder : IDisposable
{
    private readonly object gate = new();
    private nint handle = CodecApi.tj3Init(1);
    private volatile bool fast;
    private bool fastApplied;
    public JpegDecoder() { if (handle == 0) throw new OutOfMemoryException(); CodecApi.tj3Set(handle, CodecApi.TJPARAM_STOPONWARNING, 1); }
    /// <summary>
    /// Fast IDCT and chroma upsampling (TJPARAM_FASTDCT, TJPARAM_FASTUPSAMPLE): about 12% faster on 720p frames, with
    /// slightly different pixels. May be set from any thread; the next decode applies it to the handle, once per change.
    /// </summary>
    public bool Fast { get => fast; set => fast = value; }
    public bool TryReadHeader(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        width = height = 0;
        lock (gate) { if (handle == 0 || jpeg.IsEmpty) return false; fixed (byte* input = jpeg) { if (CodecApi.tj3DecompressHeader(handle, input, (nuint)jpeg.Length) != 0) return false; width = CodecApi.tj3Get(handle, CodecApi.TJPARAM_JPEGWIDTH); height = CodecApi.tj3Get(handle, CodecApi.TJPARAM_JPEGHEIGHT); return width > 0 && height > 0; } }
    }
    public bool TryDecode(ReadOnlySpan<byte> jpeg, Span<byte> dst, int width, int height, int dstStride)
    {
        if (!Pixels.Fits(width, height, dstStride, dst.Length)) return false;
        lock (gate)
        {
            if (!TryReadHeader(jpeg, out int w, out int h) || w != width || h != height) return false;
            bool wanted = fast;
            if (wanted != fastApplied)
            {
                int v = wanted ? 1 : 0;
                if (CodecApi.tj3Set(handle, CodecApi.TJPARAM_FASTDCT, v) != 0 || CodecApi.tj3Set(handle, CodecApi.TJPARAM_FASTUPSAMPLE, v) != 0) return false;
                fastApplied = wanted;
            }
            fixed (byte* input = jpeg) fixed (byte* output = dst)
            {
                if (CodecApi.tj3Decompress8(handle, input, (nuint)jpeg.Length, output, dstStride, 3) != 0) return false;
            }
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) dst[y * dstStride + x * 4 + 3] = 255;
            return true;
        }
    }
    public void Dispose() { lock (gate) { if (handle != 0) CodecApi.tj3Destroy(handle); handle = 0; } GC.SuppressFinalize(this); }
    ~JpegDecoder() { Dispose(); }
}

/// <summary>
/// SPICE LZ4 image data (spice-common canvas_get_lz4): after the 2-byte header, big-endian u32 block sizes, each followed
/// by an LZ4 block, decoded as one stream. Stateless and thread-safe.
/// </summary>
public static unsafe class Lz4Decoder
{
    /// <summary>
    /// spicecodec.dll exports sc_lz4_decode. spicecodec.dll is replaceable (LGPL), so an older build without it must
    /// not make the client advertise LZ4.
    /// </summary>
    public static bool IsAvailable { get; } = Probe();

    static bool Probe()
    {
        if (!NativeLibrary.TryLoad("spicecodec.dll", typeof(Lz4Decoder).Assembly, DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories, out nint h)) return false;
        try { return NativeLibrary.TryGetExport(h, "sc_lz4_decode", out _); }
        finally { NativeLibrary.Free(h); }
    }

    /// <summary>Decodes <paramref name="src"/> into <paramref name="dst"/>; returns the bytes written, or -1 (also without the export).</summary>
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (src.IsEmpty || dst.IsEmpty || !IsAvailable) return -1;
        fixed (byte* input = src) fixed (byte* output = dst)
        {
            int n = CodecApi.sc_lz4_decode(input, (nuint)src.Length, output, (nuint)dst.Length);
            return n >= 0 && n <= dst.Length ? n : -1;
        }
    }
}

public sealed unsafe class OpusDecoderHandle : IDisposable
{
    private readonly object gate = new();
    private nint handle;
    private readonly int channels;
    public OpusDecoderHandle(int sampleRate, int channels)
    {
        this.channels = channels;
        handle = CodecApi.opus_decoder_create(sampleRate, channels, out int error);
        if (handle == 0 || error < 0) throw new ArgumentException($"Opus decoder creation failed ({error}).");
    }
    public int Decode(ReadOnlySpan<byte> packet, Span<short> pcm, int maxSamplesPerChannel)
    {
        if (packet.IsEmpty || maxSamplesPerChannel <= 0 || (long)maxSamplesPerChannel * channels > pcm.Length) return -1;
        lock (gate) { if (handle == 0) return -1; fixed (byte* input = packet) fixed (short* output = pcm) { int n = CodecApi.opus_decode(handle, input, packet.Length, output, maxSamplesPerChannel, 0); return n < 0 ? -1 : n; } }
    }
    public void Dispose() { lock (gate) { if (handle != 0) CodecApi.opus_decoder_destroy(handle); handle = 0; } GC.SuppressFinalize(this); }
    ~OpusDecoderHandle() { Dispose(); }
}

public static unsafe class NativeCodecs
{
    /// <summary>The spicecodec.dll interface this build calls (SC_ABI_VERSION in spice_codec.h).</summary>
    public const int SpiceCodecAbi = 2;

    /// <summary>Why the spicecodec.dll next to the program can't be used (missing, or another interface version), or null.</summary>
    public static string? SpiceCodecError { get; } = ProbeSpiceCodec();

    static string? ProbeSpiceCodec()
    {
        if (!NativeLibrary.TryLoad("spicecodec.dll", typeof(NativeCodecs).Assembly, DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories, out nint h))
            return "spicecodec.dll: not found";
        try { return AbiMatches(h, out string? error) ? null : error; }
        finally { NativeLibrary.Free(h); }
    }

    public static bool TryLoad(out string? error)
    {
        foreach (string name in new[] { "spicecodec.dll", "turbojpeg.dll", "opus.dll" })
        {
            try
            {
                nint h = NativeLibrary.Load(name, typeof(NativeCodecs).Assembly, DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories);
                try { if (name == "spicecodec.dll" && !AbiMatches(h, out error)) return false; }
                finally { NativeLibrary.Free(h); }
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException) { error = $"{name}: {e.Message}"; return false; }
        }
        error = null; return true;
    }

    /// <summary>
    /// spicecodec.dll is replaceable (LGPL): a build of another version can export the same functions with different
    /// contracts (before version 2, sc_lz_decode wrote padded palette rows), so it must declare the version this calls.
    /// </summary>
    static bool AbiMatches(nint library, out string? error)
    {
        int version = NativeLibrary.TryGetExport(library, "sc_abi_version", out nint f) ? ((delegate* unmanaged[Cdecl]<int>)f)() : 1;
        error = version == SpiceCodecAbi ? null
            : $"spicecodec.dll: interface version {version}, this viewer needs {SpiceCodecAbi}; build it from the matching source\\native";
        return error is null;
    }
}
