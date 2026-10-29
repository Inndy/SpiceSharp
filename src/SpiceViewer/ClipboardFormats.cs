using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Spice.Canvas;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;

namespace SpiceViewer;

/// <summary>
/// Conversions between Windows clipboard blocks (CF_UNICODETEXT, CF_DIB) and the agent's formats.
/// Every HGLOBAL is bounded by GlobalSize and read through a span, so a malformed block cannot cause a read past
/// its allocation. Guest-supplied images are decoded and re-encoded as a canonical DIB, never published as-is.
/// </summary>
static unsafe class ClipboardFormats
{
    const int MaxDimension = 16384;
    const long MaxPixels = 64L << 20; // 64 Mpx: 256 MiB of BGRA
    const uint BI_RGB = 0, BI_BITFIELDS = 3, BI_ALPHABITFIELDS = 6;
    const int InfoHeaderSize = 40; // BITMAPINFOHEADER

    // ---------------- host → guest ----------------

    /// <summary>Reads a CF_UNICODETEXT block up to its first NUL or the end of the block, whichever comes first.</summary>
    public static string? ReadUnicodeText(HGLOBAL h)
    {
        nuint size = PInvoke.GlobalSize(h);
        if (size == 0) return null; // invalid handle or discarded block
        var p = (char*)PInvoke.GlobalLock(h);
        if (p is null) return null;
        try
        {
            int chars = (int)Math.Min(size / 2, (nuint)int.MaxValue);
            var span = new ReadOnlySpan<char>(p, chars);
            int n = span.IndexOf('\0');
            return new string(n < 0 ? span : span[..n]);
        }
        finally { PInvoke.GlobalUnlock(h); }
    }

    /// <summary>Converts a CF_DIB block to PNG; null if the block is malformed or in an unsupported format.</summary>
    public static byte[]? DibToPng(HGLOBAL h)
    {
        nuint size = PInvoke.GlobalSize(h);
        if (size < InfoHeaderSize || size > int.MaxValue) return null;
        var p = (byte*)PInvoke.GlobalLock(h);
        if (p is null) return null;
        byte[] bgra;
        int w, hgt;
        try
        {
            if (!TryDecodeDib(new ReadOnlySpan<byte>(p, (int)size), -1, out bgra, out w, out hgt)) return null;
        }
        finally { PInvoke.GlobalUnlock(h); }
        return Png.Encode(bgra, w, hgt, w * 4);
    }

    // ---------------- guest → host ----------------

    public static byte[]? PngToDib(byte[] png)
    {
        try
        {
            var (bgra, w, h) = Png.Decode(png);
            if (w <= 0 || h <= 0 || w > MaxDimension || h > MaxDimension || bgra.Length != (long)w * h * 4)
            {
                Log.Warn($"clipboard: PNG from guest has an invalid size {w}x{h}");
                return null;
            }
            return BuildDib(bgra, w, h);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or IndexOutOfRangeException or ArgumentException or EndOfStreamException or OverflowException)
        {
            Log.Warn($"clipboard: unsupported PNG from guest: {e.Message}");
            return null;
        }
    }

    /// <summary>Validates a BMP file from the guest and converts it to a canonical 32-bit top-down DIB.</summary>
    public static byte[]? BmpToDib(byte[] bmp)
    {
        const int FileHeaderSize = 14; // BITMAPFILEHEADER
        if (bmp.Length < FileHeaderSize + InfoHeaderSize || bmp[0] != 'B' || bmp[1] != 'M')
        {
            Log.Warn("clipboard: BMP from guest has no valid file header");
            return null;
        }
        uint offBits = BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10));
        if (offBits < FileHeaderSize + InfoHeaderSize || offBits > (uint)bmp.Length
            || !TryDecodeDib(bmp.AsSpan(FileHeaderSize), offBits - FileHeaderSize, out var bgra, out int w, out int h))
        {
            Log.Warn("clipboard: BMP from guest is malformed or unsupported");
            return null;
        }
        return BuildDib(bgra, w, h);
    }

    /// <summary>A packed DIB: BITMAPINFOHEADER (32 bpp, BI_RGB, top-down) followed by the pixels.</summary>
    static byte[] BuildDib(byte[] bgra, int w, int h)
    {
        var dib = new byte[checked(InfoHeaderSize + bgra.Length)];
        var s = dib.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], w);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], -h);
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 32); // bits per pixel
        // compression BI_RGB, sizes and color counts stay 0
        bgra.CopyTo(s[InfoHeaderSize..]);
        return dib;
    }

    // ---------------- HGLOBAL helpers ----------------

    /// <summary>Copies <paramref name="data"/> plus <paramref name="zeroBytes"/> zero bytes into a new movable block; null on failure.</summary>
    public static HGLOBAL ToGlobal(ReadOnlySpan<byte> data, int zeroBytes = 0)
    {
        nuint size = checked((nuint)data.Length + (nuint)zeroBytes);
        if (size == 0) return default;
        var mem = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, size);
        if (mem.IsNull) return default;
        var p = (byte*)PInvoke.GlobalLock(mem);
        if (p is null) { PInvoke.GlobalFree(mem); return default; }
        try
        {
            data.CopyTo(new Span<byte>(p, data.Length));
            new Span<byte>(p + data.Length, zeroBytes).Clear();
        }
        finally { PInvoke.GlobalUnlock(mem); }
        return mem;
    }

    /// <summary>A NUL-terminated CF_UNICODETEXT block; null on failure.</summary>
    public static HGLOBAL TextToGlobal(string text) => ToGlobal(MemoryMarshal.AsBytes(text.AsSpan()), sizeof(char));

    // ---------------- DIB decoding ----------------

    /// <summary>
    /// Decodes a DIB (BITMAPINFOHEADER, V2–V5 headers) with 24 or 32 bits per pixel, BI_RGB or BI_BITFIELDS with the
    /// standard BGR masks, into top-down BGRA with opaque alpha. Every offset is checked against <paramref name="dib"/>.
    /// </summary>
    /// <param name="pixelOffset">Offset of the pixels from the header start (BMP files), or -1 for a packed DIB.</param>
    static bool TryDecodeDib(ReadOnlySpan<byte> dib, long pixelOffset, out byte[] bgra, out int width, out int height)
    {
        bgra = [];
        width = height = 0;
        if (dib.Length < InfoHeaderSize) return false;
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        // BITMAPINFOHEADER, BITMAPV2/V3INFOHEADER (masks inside), BITMAPV4HEADER, BITMAPV5HEADER.
        if (headerSize is not (40 or 52 or 56 or 108 or 124) || headerSize > dib.Length) return false;
        int w = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        int h = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(dib[12..]);
        ushort bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        uint clrUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (planes != 1 || w <= 0 || w > MaxDimension || h == 0 || h < -MaxDimension || h > MaxDimension) return false;
        int hgt = Math.Abs(h);
        if ((long)w * hgt > MaxPixels) return false;
        if (bpp is not (24 or 32)) return false;

        // Bit masks: BI_BITFIELDS needs the standard BGR layout. A plain BITMAPINFOHEADER is followed by 12 (16 for
        // BI_ALPHABITFIELDS) bytes of masks; V2+ headers carry them inside, at the same offset 40.
        long maskBytes = 0;
        if (compression is BI_BITFIELDS or BI_ALPHABITFIELDS)
        {
            if (bpp != 32) return false;
            if (headerSize == InfoHeaderSize) maskBytes = compression == BI_ALPHABITFIELDS ? 16 : 12;
            if (dib.Length < InfoHeaderSize + 12) return false;
            uint r = BinaryPrimitives.ReadUInt32LittleEndian(dib[40..]);
            uint g = BinaryPrimitives.ReadUInt32LittleEndian(dib[44..]);
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(dib[48..]);
            if (r != 0x00FF0000 || g != 0x0000FF00 || b != 0x000000FF) return false;
        }
        else if (compression != BI_RGB) return false;

        // Optional color table (allowed, unused, for 24/32 bpp).
        if (clrUsed > 256) return false;
        long minOffset = headerSize + maskBytes + clrUsed * 4L;
        long offset = pixelOffset < 0 ? minOffset : pixelOffset;
        if (offset < minOffset) return false;
        long stride = ((long)w * bpp + 31) / 32 * 4;
        if (offset + stride * hgt > dib.Length) return false;

        int bytesPerPixel = bpp / 8;
        var outPx = new byte[w * hgt * 4];
        bool bottomUp = h > 0;
        for (int y = 0; y < hgt; y++)
        {
            int srcY = bottomUp ? hgt - 1 - y : y;
            var row = dib.Slice((int)(offset + stride * srcY), w * bytesPerPixel);
            var dst = outPx.AsSpan(y * w * 4, w * 4);
            for (int x = 0, s = 0, d = 0; x < w; x++, s += bytesPerPixel, d += 4)
            {
                dst[d] = row[s];
                dst[d + 1] = row[s + 1];
                dst[d + 2] = row[s + 2];
                dst[d + 3] = 255;
            }
        }
        bgra = outPx;
        width = w;
        height = hgt;
        return true;
    }
}
