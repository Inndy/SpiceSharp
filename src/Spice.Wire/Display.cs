using System.Buffers.Binary;

namespace Spice.Wire;

public static class DisplayMsg
{
    public const ushort Mode = 101, Mark = 102, Reset = 103, CopyBits = 104, InvalList = 105, InvalAllPixmaps = 106,
        InvalPalette = 107, InvalAllPalettes = 108,
        StreamCreate = 122, StreamData = 123, StreamClip = 124, StreamDestroy = 125, StreamDestroyAll = 126,
        DrawFill = 302, DrawOpaque = 303, DrawCopy = 304, DrawBlend = 305, DrawBlackness = 306, DrawWhiteness = 307,
        DrawInvers = 308, DrawRop3 = 309, DrawStroke = 310, DrawText = 311, DrawTransparent = 312, DrawAlphaBlend = 313,
        SurfaceCreate = 314, SurfaceDestroy = 315, StreamDataSized = 316, MonitorsConfig = 317, DrawComposite = 318,
        StreamActivateReport = 319, GlScanoutUnix = 320, GlDraw = 321, QualityIndicator = 322;
    // client
    public const ushort CInit = 101, CStreamReport = 102, CPreferredCompression = 103, CGlDrawDone = 104, CPreferredVideoCodecType = 105;
}

public readonly record struct SpicePoint(int X, int Y)
{
    public static SpicePoint Read(ref SpiceReader r) => new(r.I32(), r.I32());
}

/// <summary>SpiceRect. Coordinates are as sent; <see cref="Read"/> rejects rectangles whose size overflows.</summary>
public readonly record struct SpiceRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public static SpiceRect Read(ref SpiceReader r)
    {
        int top = r.I32(), left = r.I32(), bottom = r.I32(), right = r.I32();
        if ((long)right - left > int.MaxValue || (long)bottom - top > int.MaxValue) throw WireException.BadValue("rect", right);
        return new SpiceRect(left, top, right, bottom);
    }

    public SpiceRect Intersect(SpiceRect o)
    {
        var r = new SpiceRect(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
        return r.IsEmpty ? default : r;
    }

    public SpiceRect Union(SpiceRect o) =>
        IsEmpty ? o : o.IsEmpty ? this : new(Math.Min(Left, o.Left), Math.Min(Top, o.Top), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

    public SpiceRect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);
    public override string ToString() => $"({Left},{Top} {Width}x{Height})";
}

/// <summary>SpiceClip: none, or a list of rectangles kept as raw wire bytes (no per-message array).</summary>
public readonly struct SpiceClip
{
    public const byte TypeNone = 0, TypeRects = 1;
    readonly ReadOnlyMemory<byte> _rects;
    public byte Type { get; }
    public int Count => _rects.Length / 16;

    SpiceClip(byte type, ReadOnlyMemory<byte> rects) { Type = type; _rects = rects; }

    public SpiceRect this[int i]
    {
        get
        {
            var s = _rects.Span.Slice(i * 16, 16);
            return new SpiceRect(BinaryPrimitives.ReadInt32LittleEndian(s[4..]), BinaryPrimitives.ReadInt32LittleEndian(s),
                                 BinaryPrimitives.ReadInt32LittleEndian(s[12..]), BinaryPrimitives.ReadInt32LittleEndian(s[8..]));
        }
    }

    public static SpiceClip Read(ref SpiceReader r)
    {
        byte t = r.U8();
        switch (t)
        {
            case TypeNone: return new SpiceClip(TypeNone, default);
            case TypeRects:
                uint n = r.U32();
                if (n > 1 << 16) throw WireException.BadValue("num_rects", n);
                return new SpiceClip(TypeRects, r.Bytes((int)n * 16));
            default: throw WireException.BadValue("clip_type", t);
        }
    }
}

public enum ImageType : byte
{
    Bitmap = 0, Quic = 1, LzPlt = 100, LzRgb = 101, GlzRgb = 102, FromCache = 103, Surface = 104,
    Jpeg = 105, FromCacheLossless = 106, ZlibGlzRgb = 107, JpegAlpha = 108, Lz4 = 109,
}

/// <summary>SpiceImageCompression (spice/enums.h; enum8 image_compression in spice.proto).</summary>
public enum ImageCompression : byte
{
    Invalid = 0, Off = 1, AutoGlz = 2, AutoLz = 3, Quic = 4, Glz = 5, Lz = 6, Lz4 = 7,
}

public static class ImageFlags { public const byte CacheMe = 1, HighBitsSet = 2, CacheReplaceMe = 4; }

public enum BitmapFormat : byte { Invalid = 0, A1Le = 1, A1Be = 2, A4Le = 3, A4Be = 4, A8 = 5, Rgb16 = 6, Rgb24 = 7, Rgb32 = 8, Rgba = 9, A8Alpha = 10 }
public static class BitmapFlags { public const byte PalCacheMe = 1, PalFromCache = 2, TopDown = 4; }

public readonly record struct ImageDescriptor(ulong Id, ImageType Type, byte Flags, uint Width, uint Height);

/// <summary>A palette by value, or (when <see cref="FromCacheId"/> is set) a reference to a cached one.</summary>
public readonly struct PaletteRef
{
    public ulong? FromCacheId { get; init; }
    public ulong Unique { get; init; }
    /// <summary>Raw u32 entries; empty when absent.</summary>
    public ReadOnlyMemory<byte> Entries { get; init; }
    public bool IsPresent => FromCacheId is not null || !Entries.IsEmpty;
    public int Count => Entries.Length / 4;

    public static PaletteRef Read(ref SpiceReader r, byte bitmapFlags)
    {
        if ((bitmapFlags & BitmapFlags.PalFromCache) != 0) return new PaletteRef { FromCacheId = r.U64() };
        uint ptr = r.U32();
        if (ptr == 0) return default;
        var p = r.At(ptr);
        ulong unique = p.U64();
        ushort n = p.U16();
        if (n > 256) throw WireException.BadValue("num_ents", n);
        return new PaletteRef { Unique = unique, Entries = p.Bytes(n * 4) };
    }

    public void CopyTo(Span<uint> dest)
    {
        var s = Entries.Span;
        for (int i = 0; i < Count && i < dest.Length; i++) dest[i] = BinaryPrimitives.ReadUInt32LittleEndian(s[(i * 4)..]);
    }
}

/// <summary>SpiceImage with its type-specific payload. Data slices point into the message buffer.</summary>
public readonly struct SpiceImage
{
    public ImageDescriptor Descriptor { get; init; }
    // BITMAP
    public BitmapFormat BitmapFormat { get; init; }
    public byte BitmapFlags { get; init; }
    public uint BitmapWidth { get; init; }
    public uint BitmapHeight { get; init; }
    public uint Stride { get; init; }
    // BITMAP and LZ_PLT
    public PaletteRef Palette { get; init; }
    /// <summary>Compressed (or raw bitmap) bytes.</summary>
    public ReadOnlyMemory<byte> Data { get; init; }
    /// <summary>ZLIB_GLZ_RGB: inflated size. JPEG_ALPHA: size of the JPEG part.</summary>
    public uint AuxSize { get; init; }
    /// <summary>LZ_PLT and JPEG_ALPHA flags.</summary>
    public byte AuxFlags { get; init; }
    /// <summary>SURFACE: source surface id.</summary>
    public uint SurfaceId { get; init; }

    public ImageType Type => Descriptor.Type;
    public int Width => (int)Descriptor.Width;
    public int Height => (int)Descriptor.Height;

    /// <summary>Resolves an Image pointer; null for a 0 pointer.</summary>
    public static SpiceImage? At(in SpiceReader r, uint pointer) => pointer == 0 ? null : Read(r.At(pointer));

    public static SpiceImage Read(SpiceReader r)
    {
        var d = new ImageDescriptor(r.U64(), (ImageType)r.U8(), r.U8(), r.U32(), r.U32());
        if (d.Width > 16384 || d.Height > 16384) throw WireException.BadValue("image size", Math.Max(d.Width, d.Height));
        switch (d.Type)
        {
            case ImageType.Bitmap:
            {
                var fmt = (BitmapFormat)r.U8();
                if (fmt is BitmapFormat.Invalid or > BitmapFormat.A8Alpha) throw WireException.BadValue("bitmap format", (int)fmt);
                byte flags = r.U8();
                uint w = r.U32(), h = r.U32(), stride = r.U32();
                // The bitmap's own size must be the descriptor's (which is already capped): consumers size their output
                // from either, so a mismatch would let the payload escape the descriptor limit.
                if (w == 0 || h == 0 || w != d.Width || h != d.Height) throw WireException.BadValue("bitmap size", Math.Max(w, h));
                ulong rowBytes = BitmapRowBytes(fmt, w);
                if (stride < rowBytes) throw WireException.BadValue("bitmap stride", stride);
                var pal = PaletteRef.Read(ref r, flags);
                ulong size = (ulong)stride * h;
                if (size > 1 << 28) throw WireException.BadValue("bitmap size", (long)size);
                return new SpiceImage
                {
                    Descriptor = d, BitmapFormat = fmt, BitmapFlags = flags, BitmapWidth = w, BitmapHeight = h,
                    Stride = stride, Palette = pal, Data = r.Bytes((int)size),
                };
            }
            case ImageType.Quic or ImageType.LzRgb or ImageType.GlzRgb or ImageType.Jpeg or ImageType.Lz4:
                return new SpiceImage { Descriptor = d, Data = r.SizedBytes("image data_size") };
            case ImageType.LzPlt:
            {
                byte flags = r.U8();
                uint n = r.U32();
                var pal = PaletteRef.Read(ref r, flags);
                if (n > 1 << 26) throw WireException.BadValue("image data_size", n);
                return new SpiceImage { Descriptor = d, AuxFlags = flags, Palette = pal, Data = r.Bytes((int)n) };
            }
            case ImageType.ZlibGlzRgb:
            {
                uint glzSize = r.U32();
                if (glzSize > 1 << 28) throw WireException.BadValue("glz_data_size", glzSize);
                return new SpiceImage { Descriptor = d, AuxSize = glzSize, Data = r.SizedBytes("image data_size") };
            }
            case ImageType.JpegAlpha:
            {
                byte flags = r.U8();
                uint jpegSize = r.U32();
                var data = r.SizedBytes("image data_size");
                if (jpegSize > data.Length) throw WireException.BadValue("jpeg_size", jpegSize);
                return new SpiceImage { Descriptor = d, AuxFlags = flags, AuxSize = jpegSize, Data = data };
            }
            case ImageType.Surface:
                return new SpiceImage { Descriptor = d, SurfaceId = r.U32() };
            case ImageType.FromCache or ImageType.FromCacheLossless:
                return new SpiceImage { Descriptor = d };
            default:
                throw WireException.BadValue("image type", (int)d.Type);
        }
    }

    /// <summary>Bytes one row of <paramref name="width"/> pixels occupies in a BITMAP payload (no padding).</summary>
    public static ulong BitmapRowBytes(BitmapFormat f, ulong width) => f switch
    {
        BitmapFormat.Rgb32 or BitmapFormat.Rgba => width * 4,
        BitmapFormat.Rgb24 => width * 3,
        BitmapFormat.Rgb16 => width * 2,
        BitmapFormat.A8 or BitmapFormat.A8Alpha => width,
        BitmapFormat.A4Le or BitmapFormat.A4Be => (width + 1) / 2,
        _ => (width + 7) / 8,
    };
}

public enum BrushType : byte { None = 0, Solid = 1, Pattern = 2 }

public readonly struct SpiceBrush
{
    public BrushType Type { get; init; }
    public uint Color { get; init; }
    public SpiceImage? Pattern { get; init; }
    public SpicePoint PatternPos { get; init; }

    public static SpiceBrush Read(ref SpiceReader r)
    {
        var t = (BrushType)r.U8();
        switch (t)
        {
            case BrushType.None: return default;
            case BrushType.Solid: return new SpiceBrush { Type = t, Color = r.U32() };
            case BrushType.Pattern:
                uint ptr = r.U32();
                var pos = SpicePoint.Read(ref r);
                var img = SpiceImage.At(r, ptr) ?? throw WireException.BadValue("pattern", 0);
                return new SpiceBrush { Type = t, Pattern = img, PatternPos = pos };
            default: throw WireException.BadValue("brush type", (int)t);
        }
    }
}

public readonly struct SpiceQMask
{
    public const byte FlagInvers = 1;
    public byte Flags { get; init; }
    public SpicePoint Pos { get; init; }
    public SpiceImage? Bitmap { get; init; }

    public static SpiceQMask Read(ref SpiceReader r)
    {
        byte flags = r.U8();
        var pos = SpicePoint.Read(ref r);
        return new SpiceQMask { Flags = flags, Pos = pos, Bitmap = SpiceImage.At(r, r.U32()) };
    }
}

/// <summary>SPICE_ROPD_* (u16 flags).</summary>
public static class Ropd
{
    public const ushort InversSrc = 1 << 0, InversBrush = 1 << 1, InversDest = 1 << 2, OpPut = 1 << 3, OpOr = 1 << 4,
        OpAnd = 1 << 5, OpXor = 1 << 6, OpBlackness = 1 << 7, OpWhiteness = 1 << 8, OpInvers = 1 << 9, InversRes = 1 << 10;
}

public readonly struct DisplayBase
{
    public uint SurfaceId { get; init; }
    public SpiceRect Box { get; init; }
    public SpiceClip Clip { get; init; }

    public static DisplayBase Read(ref SpiceReader r) =>
        new() { SurfaceId = r.U32(), Box = SpiceRect.Read(ref r), Clip = SpiceClip.Read(ref r) };
}

public readonly record struct SurfaceCreate(uint SurfaceId, uint Width, uint Height, uint Format, uint Flags)
{
    public const uint FormatA1 = 1, FormatA8 = 8, Format16_555 = 16, Format32_xRGB = 32, Format16_565 = 80, Format32_ARGB = 96;
    public bool IsPrimary => (Flags & 1) != 0;

    public static SurfaceCreate Parse(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        var s = new SurfaceCreate(r.U32(), r.U32(), r.U32(), r.U32(), r.U32());
        if (s.Width == 0 || s.Height == 0 || s.Width > 16384 || s.Height > 16384)
            throw WireException.BadValue("surface size", Math.Max(s.Width, s.Height));
        return s;
    }
}

/// <summary>
/// One draw command (DRAW_FILL … DRAW_ALPHA_BLEND, COPY_BITS). Fields not used by a kind keep defaults.
/// Layouts follow spice.proto's DisplayChannel.
/// </summary>
public readonly struct DrawCommand
{
    public ushort Kind { get; init; }
    public DisplayBase Base { get; init; }
    public SpiceImage? Source { get; init; }
    public SpiceRect SourceArea { get; init; }
    public SpiceBrush Brush { get; init; }
    public ushort RopDescriptor { get; init; }
    public byte Rop3 { get; init; }
    public byte ScaleMode { get; init; }
    public SpiceQMask Mask { get; init; }
    /// <summary>COPY_BITS source position.</summary>
    public SpicePoint SourcePos { get; init; }
    // TRANSPARENT: the colour key (canvas_base.c keys on true_color; src_color is unused)
    public uint TrueColor { get; init; }
    // ALPHA_BLEND
    public byte AlphaFlags { get; init; }
    public byte Alpha { get; init; }
    // TEXT: the String structure; STROKE: the Path structure (both run to the end of the message)
    public ReadOnlyMemory<byte> Vector { get; init; }
    /// <summary>STROKE line flags.</summary>
    public byte LineFlags { get; init; }
    public SpiceRect BackArea { get; init; }
    public SpiceBrush BackBrush { get; init; }
    public ushort ForeMode { get; init; }
    public ushort BackMode { get; init; }

    public static DrawCommand Parse(ushort kind, ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        var b = DisplayBase.Read(ref r);
        switch (kind)
        {
            case DisplayMsg.CopyBits:
                return new DrawCommand { Kind = kind, Base = b, SourcePos = SpicePoint.Read(ref r) };
            case DisplayMsg.DrawFill:
            {
                var brush = SpiceBrush.Read(ref r);
                ushort rop = r.U16();
                return new DrawCommand { Kind = kind, Base = b, Brush = brush, RopDescriptor = rop, Mask = SpiceQMask.Read(ref r) };
            }
            case DisplayMsg.DrawCopy or DisplayMsg.DrawBlend:
            {
                var src = SpiceImage.At(r, r.U32());
                var area = SpiceRect.Read(ref r);
                ushort rop = r.U16();
                byte scale = r.U8();
                return new DrawCommand { Kind = kind, Base = b, Source = src, SourceArea = area, RopDescriptor = rop, ScaleMode = scale, Mask = SpiceQMask.Read(ref r) };
            }
            case DisplayMsg.DrawOpaque:
            {
                var src = SpiceImage.At(r, r.U32());
                var area = SpiceRect.Read(ref r);
                var brush = SpiceBrush.Read(ref r);
                ushort rop = r.U16();
                byte scale = r.U8();
                return new DrawCommand { Kind = kind, Base = b, Source = src, SourceArea = area, Brush = brush, RopDescriptor = rop, ScaleMode = scale, Mask = SpiceQMask.Read(ref r) };
            }
            case DisplayMsg.DrawBlackness or DisplayMsg.DrawWhiteness or DisplayMsg.DrawInvers:
                return new DrawCommand { Kind = kind, Base = b, Mask = SpiceQMask.Read(ref r) };
            case DisplayMsg.DrawRop3:
            {
                var src = SpiceImage.At(r, r.U32());
                var area = SpiceRect.Read(ref r);
                var brush = SpiceBrush.Read(ref r);
                byte rop3 = r.U8();
                byte scale = r.U8();
                return new DrawCommand { Kind = kind, Base = b, Source = src, SourceArea = area, Brush = brush, Rop3 = rop3, ScaleMode = scale, Mask = SpiceQMask.Read(ref r) };
            }
            case DisplayMsg.DrawTransparent:
            {
                var src = SpiceImage.At(r, r.U32());
                var area = SpiceRect.Read(ref r);
                r.U32(); // src_color
                return new DrawCommand { Kind = kind, Base = b, Source = src, SourceArea = area, TrueColor = r.U32() };
            }
            case DisplayMsg.DrawAlphaBlend:
            {
                byte flags = r.U8(), alpha = r.U8();
                var src = SpiceImage.At(r, r.U32());
                return new DrawCommand { Kind = kind, Base = b, AlphaFlags = flags, Alpha = alpha, Source = src, SourceArea = SpiceRect.Read(ref r) };
            }
            case DisplayMsg.DrawText:
            {
                uint strPtr = r.U32();
                if (strPtr == 0) throw WireException.BadValue("text str", 0);
                var s = r.At(strPtr);
                var back = SpiceRect.Read(ref r);
                var fore = SpiceBrush.Read(ref r);
                var backBrush = SpiceBrush.Read(ref r);
                return new DrawCommand
                {
                    Kind = kind, Base = b, Vector = s.Rest(), BackArea = back, Brush = fore, BackBrush = backBrush,
                    ForeMode = r.U16(), BackMode = r.U16(),
                };
            }
            case DisplayMsg.DrawStroke:
            {
                uint pathPtr = r.U32();
                if (pathPtr == 0) throw WireException.BadValue("stroke path", 0);
                var path = r.At(pathPtr).Rest();
                byte lineFlags = r.U8();
                if ((lineFlags & 8) != 0) { r.U8(); r.U32(); } // STYLED: style_nseg, style pointer (dashes are not drawn)
                var brush = SpiceBrush.Read(ref r);
                return new DrawCommand { Kind = kind, Base = b, Vector = path, LineFlags = lineFlags, Brush = brush, ForeMode = r.U16(), BackMode = r.U16() };
            }
            default:
                throw new WireException($"not a draw command: {kind}");
        }
    }
}

public enum VideoCodecType : byte { Mjpeg = 1, Vp8 = 2, H264 = 3, Vp9 = 4, H265 = 5 }

public readonly struct StreamCreate
{
    public const byte FlagTopDown = 1;
    public uint SurfaceId { get; init; }
    public uint Id { get; init; }
    public byte Flags { get; init; }
    public VideoCodecType Codec { get; init; }
    public uint StreamWidth { get; init; }
    public uint StreamHeight { get; init; }
    public uint SrcWidth { get; init; }
    public uint SrcHeight { get; init; }
    public SpiceRect Dest { get; init; }
    public SpiceClip Clip { get; init; }

    public static StreamCreate Parse(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        uint surface = r.U32(), id = r.U32();
        byte flags = r.U8();
        var codec = (VideoCodecType)r.U8();
        r.U64(); // stamp
        return new StreamCreate
        {
            SurfaceId = surface, Id = id, Flags = flags, Codec = codec,
            StreamWidth = r.U32(), StreamHeight = r.U32(), SrcWidth = r.U32(), SrcHeight = r.U32(),
            Dest = SpiceRect.Read(ref r), Clip = SpiceClip.Read(ref r),
        };
    }
}

public readonly struct StreamData
{
    public uint Id { get; init; }
    public uint MultiMediaTime { get; init; }
    /// <summary>STREAM_DATA_SIZED only: new frame size and destination.</summary>
    public uint Width { get; init; }
    public uint Height { get; init; }
    public SpiceRect? Dest { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }

    public static StreamData Parse(ReadOnlyMemory<byte> m, bool sized)
    {
        var r = new SpiceReader(m);
        uint id = r.U32(), mm = r.U32();
        if (!sized) return new StreamData { Id = id, MultiMediaTime = mm, Data = r.SizedBytes("stream data_size") };
        uint w = r.U32(), h = r.U32();
        var dest = SpiceRect.Read(ref r);
        return new StreamData { Id = id, MultiMediaTime = mm, Width = w, Height = h, Dest = dest, Data = r.SizedBytes("stream data_size") };
    }
}

/// <summary>One SpiceHead of DISPLAY_MONITORS_CONFIG, in wire order (spice.proto: monitor_id, surface_id, width, height, x, y, flags).</summary>
public readonly record struct MonitorHead(uint Id, uint SurfaceId, uint Width, uint Height, uint X, uint Y, uint Flags);

public static class DisplayParse
{
    /// <summary>Bytes of one SpiceHead on the wire.</summary>
    public const int MonitorHeadSize = 28;
    /// <summary>Heads accepted in one DISPLAY_MONITORS_CONFIG (spice-server allows at most 16).</summary>
    public const int MaxMonitorHeads = 64;

    /// <summary>
    /// DISPLAY_MONITORS_CONFIG ({u16 count, u16 max_allowed, SpiceHead heads[count]}): the first head on surface 0 with a
    /// non-zero size, which is the monitor a single-monitor client shows (docs/decisions/0035), and the number of heads
    /// in the message. False when there is none (no heads, or none on surface 0). Throws <see cref="WireException"/> when
    /// the message is shorter than its count says or the count is implausible. Reads in place; allocates nothing.
    /// </summary>
    public static bool TryPrimaryHead(ReadOnlyMemory<byte> m, out MonitorHead head, out int count)
    {
        var r = new SpiceReader(m);
        count = r.U16();
        r.U16(); // max_allowed
        if (count > MaxMonitorHeads) throw WireException.BadValue("monitors count", count);
        if (r.Remaining < count * MonitorHeadSize) throw WireException.Truncated(count * MonitorHeadSize, r.Remaining);
        for (int i = 0; i < count; i++)
        {
            var h = new MonitorHead(r.U32(), r.U32(), r.U32(), r.U32(), r.U32(), r.U32(), r.U32());
            if (h.SurfaceId == 0 && h.Width != 0 && h.Height != 0) { head = h; return true; }
        }
        head = default;
        return false;
    }

    /// <summary>INVAL_LIST: (resource type, id) pairs.</summary>
    public static (byte Type, ulong Id)[] InvalList(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        ushort n = r.U16();
        var list = new (byte, ulong)[n];
        for (int i = 0; i < n; i++) list[i] = (r.U8(), r.U64());
        return list;
    }

    public static (uint Id, SpiceClip Clip) StreamClip(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        uint id = r.U32();
        return (id, SpiceClip.Read(ref r));
    }

    public static (uint StreamId, uint UniqueId, uint MaxWindowSize, uint TimeoutMs) StreamActivateReport(ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        return (r.U32(), r.U32(), r.U32(), r.U32());
    }
}
