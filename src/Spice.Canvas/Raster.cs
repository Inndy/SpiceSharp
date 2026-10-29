using System.Buffers;
using Spice.Wire;

namespace Spice.Canvas;

/// <summary>Where a draw's input pixels come from. Image sources map destination (x, y) to (x + Dx, y + Dy).</summary>
public readonly struct PixelSource
{
    public enum SourceKind : byte { Solid, Image, Pattern }
    public SourceKind Kind { get; init; }
    public uint Color { get; init; }
    public ImageView Image { get; init; }
    public int Dx { get; init; }
    public int Dy { get; init; }

    public static PixelSource Solid(uint color) => new() { Kind = SourceKind.Solid, Color = color | 0xFF000000 };
    public static PixelSource FromImage(ImageView img, int dx, int dy) => new() { Kind = SourceKind.Image, Image = img, Dx = dx, Dy = dy };
    /// <summary>A tiled pattern whose (0,0) sits at surface point (seedX, seedY).</summary>
    public static PixelSource Pattern(ImageView img, int seedX, int seedY) => new() { Kind = SourceKind.Pattern, Image = img, Dx = -seedX, Dy = -seedY };

    /// <summary>The source pixel for destination (x, y); false when an image source has no pixel there.</summary>
    public bool TrySample(int x, int y, out uint px)
    {
        switch (Kind)
        {
            case SourceKind.Solid: px = Color; return true;
            case SourceKind.Image:
            {
                int sx = x + Dx, sy = y + Dy;
                if ((uint)sx >= (uint)Image.Width || (uint)sy >= (uint)Image.Height) { px = 0; return false; }
                px = Image.Pixel(sx, sy);
                return true;
            }
            default:
            {
                int w = Image.Width, h = Image.Height;
                if (w <= 0 || h <= 0) { px = 0; return false; }
                int sx = (x + Dx) % w, sy = (y + Dy) % h;
                if (sx < 0) sx += w;
                if (sy < 0) sy += h;
                px = Image.Pixel(sx, sy);
                return true;
            }
        }
    }
}

/// <summary>
/// 8-bit coverage (0 = none, 255 = full) in surface coordinates: mask pixel (0,0) sits at (OriginX, OriginY).
/// Callers size masks to the visible part of the draw, so this never exceeds the surface.
/// Like <see cref="DecodedImage"/>, disposed masks go to a per-thread free list that <see cref="Rent"/> reuses, so
/// text and masked draws allocate nothing once warm; nothing may touch a mask after its <see cref="Dispose"/>.
/// </summary>
public sealed class CoverageMask : IDisposable
{
    /// <summary>Defence-in-depth ceiling (one byte per pixel of the largest surface).</summary>
    public const long MaxBytes = (long)Surface.MaxSide * Surface.MaxSide;

    const int MaxFreePerThread = 8;
    [ThreadStatic] static Stack<CoverageMask>? t_free;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int OriginX { get; private set; }
    public int OriginY { get; private set; }
    public byte[] Coverage { get; private set; } = [];

    public CoverageMask(int width, int height, int originX, int originY) => Init(width, height, originX, originY);

    /// <summary>Like the constructor, but reuses a disposed mask object when one is free on this thread.</summary>
    public static CoverageMask Rent(int width, int height, int originX, int originY)
    {
        var free = t_free;
        if (free is null || free.Count == 0) return new CoverageMask(width, height, originX, originY);
        var mask = free.Pop();
        mask.Init(width, height, originX, originY);
        return mask;
    }

    void Init(int width, int height, int originX, int originY)
    {
        long bytes = (long)width * height;
        if (width < 0 || height < 0 || bytes > MaxBytes) throw new CanvasException($"bad mask size {width}x{height}");
        Width = width; Height = height; OriginX = originX; OriginY = originY;
        Coverage = ArrayPool<byte>.Shared.Rent((int)Math.Max(1, bytes));
        Array.Clear(Coverage, 0, (int)bytes);
    }

    /// <summary>Coverage at surface (x, y); 0 outside the mask.</summary>
    public byte At(int x, int y)
    {
        int mx = x - OriginX, my = y - OriginY;
        return (uint)mx < (uint)Width && (uint)my < (uint)Height ? Coverage[my * Width + mx] : (byte)0;
    }

    public bool Covers(int x, int y) => At(x, y) != 0;

    public void Dispose()
    {
        if (Coverage.Length == 0) return;
        ArrayPool<byte>.Shared.Return(Coverage);
        Coverage = [];
        var free = t_free ??= new Stack<CoverageMask>(MaxFreePerThread);
        if (free.Count < MaxFreePerThread) free.Push(this);
    }
}

/// <summary>Pixel kernels. Every function clips to the surface itself; callers pass already clipped rects.</summary>
public static class Raster
{
    /// <summary>SPICE ROPD applied bitwise to whole pixels (canvas_base.c semantics).</summary>
    public static uint RopCombine(uint dst, uint src, ushort rop)
    {
        if ((rop & Ropd.OpBlackness) != 0) return 0;
        if ((rop & Ropd.OpWhiteness) != 0) return 0xFFFFFFFF;
        if ((rop & Ropd.OpInvers) != 0) return ~dst;
        uint s = (rop & (Ropd.InversSrc | Ropd.InversBrush)) != 0 ? ~src : src;
        uint d = (rop & Ropd.InversDest) != 0 ? ~dst : dst;
        uint r = (rop & Ropd.OpPut) != 0 ? s
            : (rop & Ropd.OpOr) != 0 ? d | s
            : (rop & Ropd.OpAnd) != 0 ? d & s
            : (rop & Ropd.OpXor) != 0 ? d ^ s
            : s;
        return (rop & Ropd.InversRes) != 0 ? ~r : r;
    }

    /// <summary>GDI ternary raster op: bit i of <paramref name="code"/> is the result for (p,s,d) = bits of i.</summary>
    public static uint Rop3(byte code, uint p, uint s, uint d)
    {
        uint r = 0;
        for (int m = 0; m < 8; m++)
        {
            if ((code & (1 << m)) == 0) continue;
            uint pm = (m & 4) != 0 ? p : ~p, sm = (m & 2) != 0 ? s : ~s, dm = (m & 1) != 0 ? d : ~d;
            r |= pm & sm & dm;
        }
        return r;
    }

    static bool Clip(Surface dst, ref SpiceRect r)
    {
        r = r.Intersect(dst.Bounds);
        return !r.IsEmpty;
    }

    /// <summary>The common draw: source combined into the destination through an optional mask.</summary>
    public static void Draw(Surface dst, SpiceRect r, in PixelSource src, ushort rop, CoverageMask? mask)
    {
        if (!Clip(dst, ref r)) return;
        if (mask is null && rop == Ropd.OpPut)
        {
            if (src.Kind == PixelSource.SourceKind.Solid) { Fill(dst, r, src.Color); return; }
            if (src.Kind == PixelSource.SourceKind.Image) { Copy(dst, r, src.Image, src.Dx, src.Dy); return; }
        }
        for (int y = r.Top; y < r.Bottom; y++)
        {
            var row = dst.Row(y);
            for (int x = r.Left; x < r.Right; x++)
            {
                if (mask is not null && !mask.Covers(x, y)) continue;
                if (!src.TrySample(x, y, out uint s)) continue;
                row[x] = RopCombine(row[x], s, rop) | 0xFF000000;
            }
        }
    }

    /// <summary>
    /// Pixman OVER of <paramref name="src"/> IN an 8-bit coverage mask (anti-aliased text, sw_canvas.c canvas_draw_text):
    /// with m = coverage/255, dst = src·m + dst·(1 − αsrc·m). Pattern pixels carrying alpha are premultiplied.
    /// </summary>
    public static void DrawOver(Surface dst, SpiceRect r, in PixelSource src, CoverageMask mask)
    {
        if (!Clip(dst, ref r)) return;
        bool srcAlpha = src.Kind != PixelSource.SourceKind.Solid && src.Image.HasAlpha;
        for (int y = r.Top; y < r.Bottom; y++)
        {
            var row = dst.Row(y);
            for (int x = r.Left; x < r.Right; x++)
            {
                uint m = mask.At(x, y);
                if (m == 0 || !src.TrySample(x, y, out uint sp)) continue;
                uint sa = srcAlpha ? sp >> 24 : 255;
                if (m == 255 && sa == 255) { row[x] = sp | 0xFF000000; continue; }
                uint dp = row[x];
                uint inv = 255 - Div255(sa * m);
                uint b = Math.Min(255, Div255((sp & 0xFF) * m) + Div255((dp & 0xFF) * inv));
                uint g = Math.Min(255, Div255(((sp >> 8) & 0xFF) * m) + Div255(((dp >> 8) & 0xFF) * inv));
                uint rr = Math.Min(255, Div255(((sp >> 16) & 0xFF) * m) + Div255(((dp >> 16) & 0xFF) * inv));
                row[x] = 0xFF000000 | rr << 16 | g << 8 | b;
            }
        }
    }

    public static void Fill(Surface dst, SpiceRect r, uint color)
    {
        if (!Clip(dst, ref r)) return;
        color |= 0xFF000000;
        for (int y = r.Top; y < r.Bottom; y++) dst.Row(y).Slice(r.Left, r.Width).Fill(color);
    }

    /// <summary>
    /// Straight copy: dest (x,y) ← src (x+dx, y+dy). An ARGB surface keeps the source alpha (sw_canvas blits the
    /// bits); any other surface is opaque.
    /// </summary>
    public static void Copy(Surface dst, SpiceRect r, ImageView src, int dx, int dy)
    {
        if (!Clip(dst, ref r)) return;
        // Clip against the source image.
        var srcRect = new SpiceRect(-dx, -dy, src.Width - dx, src.Height - dy);
        r = r.Intersect(srcRect);
        if (r.IsEmpty) return;
        bool forceOpaque = src.HasAlpha && dst.Format != SurfaceCreate.Format32_ARGB;
        for (int y = r.Top; y < r.Bottom; y++)
        {
            var s = src.Row(y + dy).Slice(r.Left + dx, r.Width);
            var d = dst.Row(y).Slice(r.Left, r.Width);
            if (forceOpaque)
                for (int i = 0; i < s.Length; i++) d[i] = s[i] | 0xFF000000;
            else
                s.CopyTo(d);
        }
    }

    /// <summary>COPY_BITS: in-surface move, overlap-safe. Source is (x+dx, y+dy).</summary>
    public static void CopyBits(Surface s, SpiceRect r, int dx, int dy)
    {
        if (!Clip(s, ref r)) return;
        var srcRect = new SpiceRect(-dx, -dy, s.Width - dx, s.Height - dy);
        r = r.Intersect(srcRect);
        if (r.IsEmpty) return;
        if (dy > 0)
            for (int y = r.Top; y < r.Bottom; y++) s.Row(y + dy).Slice(r.Left + dx, r.Width).CopyTo(s.Row(y).Slice(r.Left, r.Width));
        else
            for (int y = r.Bottom - 1; y >= r.Top; y--) s.Row(y + dy).Slice(r.Left + dx, r.Width).CopyTo(s.Row(y).Slice(r.Left, r.Width));
    }

    /// <summary>Pixman OVER with a constant alpha; source pixels are premultiplied when they carry alpha.</summary>
    public static void AlphaBlend(Surface dst, SpiceRect r, ImageView src, int dx, int dy, byte alpha, bool srcHasAlpha)
    {
        if (!Clip(dst, ref r)) return;
        r = r.Intersect(new SpiceRect(-dx, -dy, src.Width - dx, src.Height - dy));
        if (r.IsEmpty) return;
        uint ca = alpha;
        for (int y = r.Top; y < r.Bottom; y++)
        {
            var s = src.Row(y + dy);
            var d = dst.Row(y);
            for (int x = r.Left; x < r.Right; x++)
            {
                uint sp = s[x + dx], dp = d[x];
                uint sa = srcHasAlpha ? sp >> 24 : 255;
                // m = ca/255; out = src*m + dst*(1 - sa*m)
                uint a = Div255(sa * ca);
                uint inv = 255 - a;
                uint b = Math.Min(255, Div255((sp & 0xFF) * ca) + Div255((dp & 0xFF) * inv));
                uint g = Math.Min(255, Div255(((sp >> 8) & 0xFF) * ca) + Div255(((dp >> 8) & 0xFF) * inv));
                uint rr = Math.Min(255, Div255(((sp >> 16) & 0xFF) * ca) + Div255(((dp >> 16) & 0xFF) * inv));
                d[x] = 0xFF000000 | rr << 16 | g << 8 | b;
            }
        }
    }

    static uint Div255(uint v) => (v + 128 + ((v + 128) >> 8)) >> 8;

    public static void DrawRop3(Surface dst, SpiceRect r, ImageView src, int dx, int dy, in PixelSource brush, byte code, CoverageMask? mask)
    {
        if (!Clip(dst, ref r)) return;
        var s = PixelSource.FromImage(src, dx, dy);
        for (int y = r.Top; y < r.Bottom; y++)
        {
            var row = dst.Row(y);
            for (int x = r.Left; x < r.Right; x++)
            {
                if (mask is not null && !mask.Covers(x, y)) continue;
                if (!s.TrySample(x, y, out uint sp) || !brush.TrySample(x, y, out uint bp)) continue;
                row[x] = Rop3(code, bp, sp, row[x]) | 0xFF000000;
            }
        }
    }

    public static void DrawTransparent(Surface dst, SpiceRect r, ImageView src, int dx, int dy, uint key)
    {
        if (!Clip(dst, ref r)) return;
        r = r.Intersect(new SpiceRect(-dx, -dy, src.Width - dx, src.Height - dy));
        if (r.IsEmpty) return;
        key &= 0xFFFFFF;
        for (int y = r.Top; y < r.Bottom; y++)
        {
            var s = src.Row(y + dy);
            var d = dst.Row(y);
            for (int x = r.Left; x < r.Right; x++)
            {
                uint p = s[x + dx];
                if ((p & 0xFFFFFF) != key) d[x] = p | 0xFF000000;
            }
        }
    }

    /// <summary>Crops <paramref name="from"/> out of <paramref name="src"/> and scales it to w×h.</summary>
    public static DecodedImage Scale(ImageView src, SpiceRect from, int w, int h, bool nearest) =>
        ScaleWindow(src, from, w, h, new SpiceRect(0, 0, w, h), nearest, flipY: false);

    /// <summary>
    /// Like <see cref="Scale"/> to <paramref name="w"/>×<paramref name="h"/>, but materialises only the output pixels inside
    /// <paramref name="window"/> (output coordinates; must lie within w×h). Pixel values are exactly those the full scale
    /// would produce, so callers can clip before scaling. <paramref name="flipY"/> mirrors the full output vertically.
    /// </summary>
    public static DecodedImage ScaleWindow(ImageView src, SpiceRect from, int w, int h, SpiceRect window, bool nearest, bool flipY)
    {
        if (w <= 0 || h <= 0 || window.Left < 0 || window.Top < 0 || window.Right > w || window.Bottom > h || window.IsEmpty)
            throw new CanvasException($"bad scale window {window} of {w}x{h}");
        int ow = window.Width, oh = window.Height;
        var img = DecodedImage.Rent(ow, oh, src.HasAlpha);
        int fw = from.Width, fh = from.Height;
        if (fw <= 0 || fh <= 0 || src.Width <= 0 || src.Height <= 0) { img.Bytes.Clear(); return img; }
        if (nearest || fw == 1 || fh == 1)
        {
            for (int oy = 0; oy < oh; oy++)
            {
                int y = flipY ? h - 1 - (window.Top + oy) : window.Top + oy;
                int sy = Math.Clamp(from.Top + (int)((long)y * fh / h), 0, src.Height - 1);
                var srow = src.Row(sy);
                var drow = img.Row(oy);
                for (int ox = 0; ox < ow; ox++)
                {
                    int x = window.Left + ox;
                    drow[ox] = srow[Math.Clamp(from.Left + (int)((long)x * fw / w), 0, src.Width - 1)];
                }
            }
            return img;
        }
        // Bilinear, pixel centres aligned.
        for (int oy = 0; oy < oh; oy++)
        {
            int y = flipY ? h - 1 - (window.Top + oy) : window.Top + oy;
            double fy = Math.Max(0, (y + 0.5) * fh / h - 0.5);
            int y0 = (int)fy;
            double wy = fy - y0;
            int sy0 = Math.Clamp(from.Top + y0, 0, src.Height - 1), sy1 = Math.Clamp(from.Top + Math.Min(y0 + 1, fh - 1), 0, src.Height - 1);
            var r0 = src.Row(sy0); var r1 = src.Row(sy1);
            var drow = img.Row(oy);
            for (int ox = 0; ox < ow; ox++)
            {
                int x = window.Left + ox;
                double fx = Math.Max(0, (x + 0.5) * fw / w - 0.5);
                int x0 = (int)fx;
                double wx = fx - x0;
                int sx0 = Math.Clamp(from.Left + x0, 0, src.Width - 1), sx1 = Math.Clamp(from.Left + Math.Min(x0 + 1, fw - 1), 0, src.Width - 1);
                uint p00 = r0[sx0], p01 = r0[sx1], p10 = r1[sx0], p11 = r1[sx1];
                uint result = 0;
                for (int c = 0; c < 32; c += 8)
                {
                    double top = ((p00 >> c) & 0xFF) * (1 - wx) + ((p01 >> c) & 0xFF) * wx;
                    double bot = ((p10 >> c) & 0xFF) * (1 - wx) + ((p11 >> c) & 0xFF) * wx;
                    result |= (uint)Math.Clamp((int)(top * (1 - wy) + bot * wy + 0.5), 0, 255) << c;
                }
                drow[ox] = result;
            }
        }
        return img;
    }

    /// <summary>
    /// The part of the 1-pixel line (x0,y0)→(x1,y1) (both ends inclusive) inside <paramref name="r"/>, combined into
    /// <paramref name="dst"/> with <paramref name="rop"/>. Pixels are exactly those of the all-octant integer Bresenham
    /// (err = dx − dy; step x when 2·err ≥ −dy, y when 2·err ≤ dx), but the visible step range is solved in closed form,
    /// so cost depends on the visible length, not the line length. Returns the work done (pixels plus search steps).
    /// </summary>
    /// <remarks>
    /// With a = |Δmajor| ≥ b = |Δminor| (n = a steps), the major axis advances every step and after i steps the minor
    /// offset is q(i) = ⌊(2·b·i + a) / (2·a)⌋. Coordinates come from FIXED28_4 (|v| &lt; 2^28) and rect edges are ints,
    /// so every product below fits in 64 bits.
    /// </remarks>
    public static long LineInRect(Surface dst, int x0, int y0, int x1, int y1, SpiceRect r, in PixelSource src, ushort rop)
    {
        if (!Clip(dst, ref r)) return 1;
        long a = Math.Abs((long)x1 - x0), b = Math.Abs((long)y1 - y0);
        bool xMajor = a >= b;
        long n = xMajor ? a : b, d = xMajor ? b : a;
        long maj0 = xMajor ? x0 : y0, min0 = xMajor ? y0 : x0;
        int sMaj = (xMajor ? x1 >= x0 : y1 >= y0) ? 1 : -1, sMin = (xMajor ? y1 >= y0 : x1 >= x0) ? 1 : -1;
        long majLo = xMajor ? r.Left : r.Top, majHi = (xMajor ? r.Right : r.Bottom) - 1L;
        long minLo = xMajor ? r.Top : r.Left, minHi = (xMajor ? r.Bottom : r.Right) - 1L;
        // Steps whose major coordinate is inside the rect.
        long iLo = sMaj > 0 ? majLo - maj0 : maj0 - majHi, iHi = sMaj > 0 ? majHi - maj0 : maj0 - majLo;
        iLo = Math.Max(iLo, 0); iHi = Math.Min(iHi, n);
        if (iLo > iHi) return 1;
        // Minor offsets inside the rect; q(i) is non-decreasing, so the matching steps are one interval.
        long qLo = sMin > 0 ? minLo - min0 : min0 - minHi, qHi = sMin > 0 ? minHi - min0 : min0 - minLo;
        long work = 1;
        long Q(long i) => n == 0 ? 0 : (2 * d * i + n) / (2 * n);
        // First step in [iLo, iHi] with q ≥ qLo.
        long lo = iLo, hi = iHi + 1;
        while (lo < hi) { long mid = lo + (hi - lo) / 2; work++; if (Q(mid) >= qLo) hi = mid; else lo = mid + 1; }
        long first = lo;
        // First step in [first, iHi] with q > qHi.
        lo = first; hi = iHi + 1;
        while (lo < hi) { long mid = lo + (hi - lo) / 2; work++; if (Q(mid) > qHi) hi = mid; else lo = mid + 1; }
        long end = lo;
        for (long i = first; i < end; i++)
        {
            long maj = maj0 + sMaj * i, min = min0 + sMin * Q(i);
            int x = (int)(xMajor ? maj : min), y = (int)(xMajor ? min : maj);
            var row = dst.Row(y);
            if (src.TrySample(x, y, out uint p)) row[x] = RopCombine(row[x], p, rop) | 0xFF000000;
        }
        return work + (end - first);
    }
}
