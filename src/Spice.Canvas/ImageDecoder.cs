using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using Spice.Native;
using Spice.Wire;

namespace Spice.Canvas;

/// <summary>
/// Turns a <see cref="SpiceImage"/> into BGRA pixels. Owns the stateful codecs; the GLZ window lives for the
/// whole session because GLZ images reference earlier ones.
/// </summary>
public sealed class ImageDecoder : IDisposable
{
    readonly QuicDecoder _quic = new();
    readonly LzDecoder _lz = new();
    readonly GlzWindow _glzWindow = new();
    readonly GlzDecoder _glz;
    readonly JpegDecoder _jpeg = new();
    readonly Dictionary<ulong, uint[]> _palettes = [];
    readonly uint[] _paletteScratch = new uint[256];

    /// <summary>Cached palettes kept at once (each ≤ 1 KiB).</summary>
    public const int MaxCachedPalettes = 1024;

    public ImageDecoder() { _glz = new GlzDecoder(_glzWindow); }

    /// <summary>
    /// Fast IDCT and chroma upsampling for JPEG and JPEG_ALPHA images (<see cref="JpegDecoder.Fast"/>): slightly lower
    /// quality. May change at any time, from any thread; the next JPEG decode uses it.
    /// </summary>
    public bool FastJpeg { get => _jpeg.Fast; set => _jpeg.Fast = value; }

    public void ClearGlzWindow() => _glzWindow.Clear();
    /// <summary>Frees the GLZ window's reuse pool (not the images the window holds). Returns the bytes freed.</summary>
    public long TrimGlzPool() => _glzWindow.TrimPool();
    public void RemovePalette(ulong id) => _palettes.Remove(id);
    public void ClearPalettes() => _palettes.Clear();

    public void Dispose()
    {
        _quic.Dispose(); _lz.Dispose(); _glz.Dispose(); _glzWindow.Dispose(); _jpeg.Dispose();
    }

    /// <summary>Decodes a pixel-carrying image (not FROM_CACHE or SURFACE, which the canvas resolves).</summary>
    public DecodedImage Decode(in SpiceImage image)
    {
        int w = image.Width, h = image.Height;
        if (w <= 0 || h <= 0) throw new CanvasException($"empty {image.Type} image");
        switch (image.Type)
        {
            case ImageType.Bitmap: return DecodeBitmap(image);
            case ImageType.Quic:
            {
                var img = DecodedImage.Rent(w, h, false);
                if (!_quic.TryDecode(image.Data.Span, w, h, img.Bytes, img.Stride, out var type)) { img.Release(); throw new CanvasException("corrupt QUIC image"); }
                img.HasAlpha = type == ScImageType.Rgba;
                if (!img.HasAlpha) img.SetOpaque();
                return img;
            }
            case ImageType.LzRgb or ImageType.LzPlt:
            {
                ReadOnlySpan<uint> pal = image.Type == ImageType.LzPlt ? ResolvePalette(image.Palette, image.AuxFlags) : default;
                var img = DecodedImage.Rent(w, h, false);
                if (!_lz.TryDecode(image.Data.Span, pal, w, h, img.Bytes, img.Stride, out var type) || type == ScImageType.Xxxa)
                {
                    img.Release();
                    throw new CanvasException("corrupt LZ image");
                }
                img.HasAlpha = type == ScImageType.Rgba;
                if (!img.HasAlpha) img.SetOpaque();
                return img;
            }
            case ImageType.GlzRgb: return DecodeGlz(image.Data.Span, w, h);
            case ImageType.ZlibGlzRgb:
            {
                int size = (int)image.AuxSize;
                var buf = PixelBuffers.Rent(size + 1);
                try
                {
                    int n = Inflate(image.Data, buf.AsSpan(0, size + 1));
                    if (n != size) throw new CanvasException($"zlib-GLZ inflated {n} bytes, expected {size}");
                    return DecodeGlz(buf.AsSpan(0, size), w, h);
                }
                finally { PixelBuffers.Return(buf); }
            }
            case ImageType.Jpeg:
            {
                var img = DecodedImage.Rent(w, h, false);
                if (!_jpeg.TryDecode(image.Data.Span, img.Bytes, w, h, img.Stride)) { img.Release(); throw new CanvasException("corrupt JPEG image"); }
                img.SetOpaque();
                return img;
            }
            case ImageType.JpegAlpha: return DecodeJpegAlpha(image, w, h);
            case ImageType.Lz4: return DecodeLz4(image.Data.Span, w, h);
            default:
                throw new CanvasException($"unsupported image type {image.Type}");
        }
    }

    DecodedImage DecodeGlz(ReadOnlySpan<byte> data, int w, int h)
    {
        var img = DecodedImage.Rent(w, h, false);
        if (!_glz.TryDecode(data, w, h, img.Bytes, img.Stride, out var type) || type == ScImageType.Xxxa)
        {
            img.Release();
            throw new CanvasException("corrupt GLZ image");
        }
        img.HasAlpha = type == ScImageType.Rgba;
        if (!img.HasAlpha) img.SetOpaque();
        return img;
    }

    /// <summary>JPEG colour, then an LZ XXXA plane supplies alpha.</summary>
    DecodedImage DecodeJpegAlpha(in SpiceImage image, int w, int h)
    {
        int jpegSize = (int)image.AuxSize;
        var data = image.Data.Span;
        var img = DecodedImage.Rent(w, h, true);
        var scratch = DecodedImage.Rent(w, h, true);
        try
        {
            if (!_jpeg.TryDecode(data[..jpegSize], img.Bytes, w, h, img.Stride)) throw new CanvasException("corrupt JPEG-alpha colour");
            if (!_lz.TryDecode(data[jpegSize..], default, w, h, scratch.Bytes, scratch.Stride, out var type) || type != ScImageType.Xxxa)
                throw new CanvasException("corrupt JPEG-alpha plane");
            var dst = img.Bytes; var a = scratch.Bytes;
            for (int i = 3; i < dst.Length; i += 4) dst[i] = a[i];
            return img;
        }
        catch { img.Release(); throw; }
        finally { scratch.Release(); }
    }

    /// <summary>
    /// LZ4 (spice-common canvas_get_lz4): a top-down byte and a SPICE bitmap format byte (16BIT x1r5g5b5, 24BIT,
    /// 32BIT xRGB or RGBA), then LZ4 blocks that decode to rows of exactly width × bytes-per-pixel, bottom row first
    /// unless top-down. 32-bit top-down data decodes straight into the image; the rest goes through pooled scratch.
    /// </summary>
    DecodedImage DecodeLz4(ReadOnlySpan<byte> data, int w, int h)
    {
        if (data.Length < 2) throw new CanvasException("LZ4 image without its header");
        bool topDown = data[0] != 0;
        var fmt = (BitmapFormat)data[1];
        int bpp = Lz4BytesPerPixel(fmt);
        if (bpp == 0) throw new CanvasException($"unsupported LZ4 bitmap format {data[1]}");
        int size = (int)(DecodedImage.ByteCount(w, h) / 4 * bpp);
        var img = DecodedImage.Rent(w, h, fmt == BitmapFormat.Rgba);
        byte[]? scratch = null;
        try
        {
            if (bpp == 4 && topDown)
            {
                if (Lz4Decoder.Decode(data[2..], img.Bytes) != size) throw new CanvasException("corrupt LZ4 image");
                if (fmt == BitmapFormat.Rgb32) img.SetOpaque();
            }
            else
            {
                scratch = PixelBuffers.Rent(size);
                if (Lz4Decoder.Decode(data[2..], scratch.AsSpan(0, size)) != size) throw new CanvasException("corrupt LZ4 image");
                ConvertRows(scratch.AsSpan(0, size), fmt, topDown, img);
            }
            return img;
        }
        catch { img.Release(); throw; }
        finally { if (scratch is not null) PixelBuffers.Return(scratch); }
    }

    /// <summary>Bytes per pixel of an LZ4 image's format; 0 for a format LZ4 images can't carry.</summary>
    internal static int Lz4BytesPerPixel(BitmapFormat f) => f switch
    {
        BitmapFormat.Rgb16 => 2,
        BitmapFormat.Rgb24 => 3,
        BitmapFormat.Rgb32 or BitmapFormat.Rgba => 4,
        _ => 0,
    };

    /// <summary>
    /// Tightly packed rows of a 16/24/32-bit or RGBA format into <paramref name="img"/> (BGRA), flipping bottom-up
    /// data. Only RGBA keeps its alpha.
    /// </summary>
    internal static void ConvertRows(ReadOnlySpan<byte> src, BitmapFormat fmt, bool topDown, DecodedImage img)
    {
        int w = img.Width, h = img.Height, rowBytes = w * Lz4BytesPerPixel(fmt);
        if (rowBytes == 0 || src.Length < (long)rowBytes * h) throw new CanvasException("LZ4 rows shorter than the image");
        for (int y = 0; y < h; y++)
            RgbRow(fmt, src.Slice((topDown ? y : h - 1 - y) * rowBytes, rowBytes), img.Row(y)[..w]);
    }

    /// <summary>One row of 32BIT, RGBA, 24BIT or 16BIT (x1r5g5b5) pixels to BGRA; alpha is 255 except for RGBA.</summary>
    static void RgbRow(BitmapFormat fmt, ReadOnlySpan<byte> s, Span<uint> d)
    {
        int w = d.Length;
        switch (fmt)
        {
            case BitmapFormat.Rgb32:
                for (int x = 0; x < w; x++) d[x] = BinaryPrimitives.ReadUInt32LittleEndian(s[(x * 4)..]) | 0xFF000000;
                break;
            case BitmapFormat.Rgba:
                for (int x = 0; x < w; x++) d[x] = BinaryPrimitives.ReadUInt32LittleEndian(s[(x * 4)..]);
                break;
            case BitmapFormat.Rgb24:
                for (int x = 0; x < w; x++) d[x] = 0xFF000000u | (uint)s[x * 3 + 2] << 16 | (uint)s[x * 3 + 1] << 8 | s[x * 3];
                break;
            case BitmapFormat.Rgb16:
                for (int x = 0; x < w; x++)
                {
                    uint v = BinaryPrimitives.ReadUInt16LittleEndian(s[(x * 2)..]);
                    uint r5 = (v >> 10) & 0x1F, g5 = (v >> 5) & 0x1F, b5 = v & 0x1F;
                    d[x] = 0xFF000000u | (r5 << 3 | r5 >> 2) << 16 | (g5 << 3 | g5 >> 2) << 8 | (b5 << 3 | b5 >> 2);
                }
                break;
            default:
                throw new CanvasException($"not an RGB format: {fmt}");
        }
    }

    /// <summary>Raw zlib (RFC 1950) inflate into <paramref name="dest"/>; returns bytes produced.</summary>
    static int Inflate(ReadOnlyMemory<byte> zlib, Span<byte> dest)
    {
        using var ms = new MemoryStreamOver(zlib);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        int total = 0, n;
        while (total < dest.Length && (n = z.Read(dest[total..])) > 0) total += n;
        return total;
    }

    ReadOnlySpan<uint> ResolvePalette(in PaletteRef p, byte flags)
    {
        if (p.FromCacheId is ulong id)
            return _palettes.TryGetValue(id, out var cached) ? cached : throw new CanvasException($"palette {id} not cached");
        if (p.Entries.IsEmpty) throw new CanvasException("missing palette");
        int n = p.Count;
        p.CopyTo(_paletteScratch);
        if ((flags & BitmapFlags.PalCacheMe) != 0)
        {
            // At most 256 entries each; the count cap bounds the cache (spice-server keeps far fewer client-side).
            if (_palettes.Count >= MaxCachedPalettes && !_palettes.ContainsKey(p.Unique))
                throw new CanvasException($"palette cache full ({MaxCachedPalettes} entries)");
            _palettes[p.Unique] = _paletteScratch[..n];
        }
        return _paletteScratch.AsSpan(0, n);
    }

    DecodedImage DecodeBitmap(in SpiceImage b)
    {
        // SpiceImage.Read already ties these to the (capped) descriptor; re-check here as defence in depth.
        if (b.BitmapWidth == 0 || b.BitmapHeight == 0 || b.BitmapWidth != b.Descriptor.Width || b.BitmapHeight != b.Descriptor.Height)
            throw new CanvasException("bitmap size does not match its descriptor");
        int w = (int)b.BitmapWidth, h = (int)b.BitmapHeight;
        var fmt = b.BitmapFormat;
        ulong rowBytesL = SpiceImage.BitmapRowBytes(fmt, b.BitmapWidth);
        if (b.Stride < rowBytesL || (ulong)b.Data.Length < (ulong)(h - 1) * b.Stride + rowBytesL)
            throw new CanvasException("bitmap shorter than stride × height");
        int rowBytes = (int)rowBytesL, stride = (int)b.Stride; // both ≤ Data.Length here
        bool topDown = (b.BitmapFlags & BitmapFlags.TopDown) != 0;
        bool paletted = fmt is BitmapFormat.A1Le or BitmapFormat.A1Be or BitmapFormat.A4Le or BitmapFormat.A4Be or BitmapFormat.A8;
        ReadOnlySpan<uint> pal = paletted ? ResolvePalette(b.Palette, b.BitmapFlags) : default;
        var img = DecodedImage.Rent(w, h, fmt == BitmapFormat.Rgba);
        var src = b.Data.Span;
        for (int y = 0; y < h; y++)
        {
            var s = src.Slice((topDown ? y : h - 1 - y) * stride, rowBytes);
            var d = img.Row(y)[..w];
            switch (fmt)
            {
                case BitmapFormat.Rgb32 or BitmapFormat.Rgba or BitmapFormat.Rgb24 or BitmapFormat.Rgb16:
                    RgbRow(fmt, s, d);
                    break;
                case BitmapFormat.A8Alpha:
                    for (int x = 0; x < w; x++) d[x] = (uint)s[x] << 24;
                    break;
                case BitmapFormat.A8:
                    for (int x = 0; x < w; x++) d[x] = Pal(pal, s[x]);
                    break;
                case BitmapFormat.A4Le or BitmapFormat.A4Be:
                    for (int x = 0; x < w; x++)
                    {
                        int by = s[x / 2];
                        int idx = (fmt == BitmapFormat.A4Be) == (x % 2 == 0) ? by >> 4 : by & 0xF;
                        d[x] = Pal(pal, idx);
                    }
                    break;
                default: // A1
                    for (int x = 0; x < w; x++)
                    {
                        int by = s[x / 8];
                        int bit = fmt == BitmapFormat.A1Be ? (by >> (7 - x % 8)) & 1 : (by >> (x % 8)) & 1;
                        d[x] = Pal(pal, bit);
                    }
                    break;
            }
        }
        return img;
    }

    static uint Pal(ReadOnlySpan<uint> pal, int i) =>
        (uint)i < (uint)pal.Length ? pal[i] | 0xFF000000 : throw new CanvasException("palette index out of range");
}

/// <summary>A read-only stream over a memory slice (avoids copying compressed data).</summary>
sealed class MemoryStreamOver(ReadOnlyMemory<byte> data) : Stream
{
    int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, data.Length - _pos);
        data.Span.Slice(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
