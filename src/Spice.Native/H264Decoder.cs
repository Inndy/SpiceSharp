using System.Runtime.InteropServices;

namespace Spice.Native;

/// <summary>sc_h264_picture: the picture <see cref="H264Decoder.Decode"/> produced.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct H264Picture
{
    /// <summary>The cropped picture size.</summary>
    public int Width, Height;
    /// <summary>0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4.</summary>
    public int Chroma;
    /// <summary>Nonzero when <see cref="H264Decoder.TryConvert"/> takes it (8-bit YUV, GBR or gray).</summary>
    public int Supported;
    public int FullRange;
    /// <summary>H.264 matrix_coefficients: 0 GBR, 1 BT.709, 2 unspecified, 5 and 6 BT.601, 9 BT.2020.</summary>
    public int Matrix;
}

public enum H264DecodeStatus
{
    /// <summary>A picture was decoded and is held for <see cref="H264Decoder.TryConvert"/>.</summary>
    Picture = 1,
    /// <summary>The data was taken; no picture came out of it.</summary>
    NoPicture = 0,
    /// <summary>The data is damaged; the decoder goes on with the next access unit.</summary>
    Invalid = -1,
    /// <summary>Out of memory or an internal error: the decoder can't go on.</summary>
    Fatal = -2,
}

/// <summary>
/// Software H.264 decoding with libavcodec, in spicecodec.dll (docs/decisions/0037, amended 2026-10-31). One per stream;
/// not thread-safe (the session decodes a stream from one thread at a time, under its stream lock). The exports are
/// optional: with a spicecodec.dll that lacks them, <see cref="IsAvailable"/> is false and there is no software path.
/// Decoding allocates no managed memory.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    nint _handle;

    /// <summary>spicecodec.dll is the interface version this build calls and exports the sc_h264_* functions.</summary>
    public static bool IsAvailable { get; } = Probe();

    static bool Probe()
    {
        if (NativeCodecs.SpiceCodecError is not null) return false;
        if (!NativeLibrary.TryLoad("spicecodec.dll", typeof(H264Decoder).Assembly, DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories, out nint h)) return false;
        try
        {
            foreach (string name in (ReadOnlySpan<string>)["sc_h264_open", "sc_h264_close", "sc_h264_decode", "sc_h264_convert"])
                if (!NativeLibrary.TryGetExport(h, name, out _)) return false;
            return true;
        }
        finally { NativeLibrary.Free(h); }
    }

    H264Decoder(nint handle) => _handle = handle;

    /// <summary>A decoder with <paramref name="threads"/> slice threads (1-16), or null without the exports or when libavcodec can't be set up.</summary>
    public static H264Decoder? TryCreate(int threads)
    {
        if (!IsAvailable) return null;
        nint h = CodecApi.sc_h264_open(Math.Clamp(threads, 1, 16));
        return h == 0 ? null : new H264Decoder(h);
    }

    /// <summary>Decodes one Annex B access unit. On <see cref="H264DecodeStatus.Picture"/>, <paramref name="picture"/> describes it.</summary>
    public H264DecodeStatus Decode(ReadOnlySpan<byte> accessUnit, out H264Picture picture)
    {
        picture = default;
        if (_handle == 0) return H264DecodeStatus.Fatal;
        if (accessUnit.IsEmpty) return H264DecodeStatus.Invalid;
        int r;
        fixed (byte* data = accessUnit)
        fixed (H264Picture* info = &picture)
            r = CodecApi.sc_h264_decode(_handle, data, (nuint)accessUnit.Length, info);
        return r switch
        {
            1 => H264DecodeStatus.Picture,
            0 => H264DecodeStatus.NoPicture,
            -1 => H264DecodeStatus.Invalid,
            _ => H264DecodeStatus.Fatal,
        };
    }

    /// <summary>
    /// Converts the newest picture to opaque B8G8R8A8 rows <paramref name="stride"/> bytes apart, at its own size;
    /// <paramref name="mirror"/> writes them bottom-up. False without a picture, for an unsupported format or a short buffer.
    /// </summary>
    public bool TryConvert(Span<byte> destination, int stride, bool mirror)
    {
        if (_handle == 0 || destination.IsEmpty) return false;
        fixed (byte* dst = destination)
            return CodecApi.sc_h264_convert(_handle, dst, (nuint)destination.Length, stride, mirror ? 1 : 0) == 0;
    }

    public void Dispose()
    {
        if (_handle != 0) CodecApi.sc_h264_close(_handle);
        _handle = 0;
        GC.SuppressFinalize(this);
    }

    ~H264Decoder() => Dispose();
}
