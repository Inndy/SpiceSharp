using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
[assembly: InternalsVisibleTo("Spice.Native.Tests")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
namespace Spice.Native;
internal static unsafe partial class CodecApi
{
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint sc_quic_create();
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sc_quic_destroy(nint h);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sc_quic_begin(nint h, byte* data, nuint len, out int width, out int height, out ScImageType type);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sc_quic_decode(nint h, byte* dst, int stride);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sc_quic_encode_rgb32(byte* src, int width, int height, int stride, byte* dst, nuint cap);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint sc_lz_create();
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sc_lz_destroy(nint h);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sc_lz_begin(nint h, byte* data, nuint len, uint* palette, int count, out int width, out int height, out ScImageType type, out int topDown);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sc_lz_decode(nint h, byte* dst, nuint cap);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sc_lz_encode_rgb32(byte* src, int width, int height, int stride, byte* dst, nuint cap);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sc_lz_encode_xxxa(byte* src, int width, int height, int stride, byte* dst, nuint cap);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint sc_glz_window_create();
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sc_glz_window_clear(nint h);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sc_glz_window_destroy(nint h);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint sc_glz_create(nint window);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void sc_glz_destroy(nint h);
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sc_glz_decode(nint h, byte* data, nuint len, out byte* dst, out int width, out int height, out int stride, out int topDown);
    /// <summary>
    /// SPICE LZ4 image data after its 2-byte header: big-endian u32 block sizes, each followed by an LZ4 block, decoded
    /// as one stream into <paramref name="dst"/>. Returns the total bytes decoded, or -1.
    /// </summary>
    [LibraryImport("spicecodec.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int sc_lz4_decode(byte* src, nuint srcLen, byte* dst, nuint dstCap);
    // TJPARAM (turbojpeg.h of libjpeg-turbo 3.1.2; the enum has no explicit values, so these are its positions).
    internal const int TJPARAM_STOPONWARNING = 0, TJPARAM_QUALITY = 3, TJPARAM_SUBSAMP = 4, TJPARAM_JPEGWIDTH = 5,
        TJPARAM_JPEGHEIGHT = 6, TJPARAM_FASTUPSAMPLE = 9, TJPARAM_FASTDCT = 10;
    [LibraryImport("turbojpeg.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint tj3Init(int type);
    [LibraryImport("turbojpeg.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void tj3Destroy(nint h);
    [LibraryImport("turbojpeg.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int tj3Get(nint h, int param);
    [LibraryImport("turbojpeg.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int tj3Set(nint h, int param, int value);
    [LibraryImport("turbojpeg.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int tj3DecompressHeader(nint h, byte* data, nuint len);
    [LibraryImport("turbojpeg.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int tj3Decompress8(nint h, byte* data, nuint len, byte* dst, int stride, int format);
    [LibraryImport("turbojpeg.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int tj3Compress8(nint h, byte* src, int width, int stride, int height, int format, ref nint dst, ref nuint size);
    [LibraryImport("turbojpeg.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void tj3Free(nint data);
    [LibraryImport("opus.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint opus_decoder_create(int sampleRate, int channels, out int error);
    [LibraryImport("opus.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void opus_decoder_destroy(nint h);
    [LibraryImport("opus.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int opus_decode(nint h, byte* data, int len, short* pcm, int frameSize, int fec);
}
