using Spice.Session;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpiceViewer;

/// <summary>Builds native HCURSORs from guest cursor shapes, cached per decoded shape and scale.</summary>
sealed unsafe class Cursors : IDisposable
{
    // Keyed on the shape instance, not the guest's id: the session hands out the same instance for a cached
    // cursor, and decodes a new one after the guest invalidates or reuses an id.
    readonly Dictionary<(CursorShape Shape, int ScaleX, int ScaleY), HCURSOR> _cache = new(new KeyComparer());

    public HCURSOR Get(CursorShape shape, double scaleX, double scaleY)
    {
        var key = (shape, (int)Math.Round(scaleX * 1000), (int)Math.Round(scaleY * 1000));
        if (_cache.TryGetValue(key, out var c)) return c;
        c = Create(shape, scaleX, scaleY);
        if (c.IsNull) return c;
        if (_cache.Count >= 64) Clear(); // the caller replaces its current cursor with c, so none of these stay in use
        _cache[key] = c;
        return c;
    }

    sealed class KeyComparer : IEqualityComparer<(CursorShape Shape, int ScaleX, int ScaleY)>
    {
        public bool Equals((CursorShape Shape, int ScaleX, int ScaleY) a, (CursorShape Shape, int ScaleX, int ScaleY) b) =>
            ReferenceEquals(a.Shape, b.Shape) && a.ScaleX == b.ScaleX && a.ScaleY == b.ScaleY;
        public int GetHashCode((CursorShape Shape, int ScaleX, int ScaleY) k) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Shape), k.ScaleX, k.ScaleY);
    }

    static HCURSOR Create(CursorShape s, double scaleX, double scaleY)
    {
        int w = Math.Max(1, (int)Math.Round(s.Width * scaleX)), h = Math.Max(1, (int)Math.Round(s.Height * scaleY));
        // Nearest-neighbour scale of the source to the on-screen size.
        uint Px(int x, int y) => s.Pixels[Math.Min(s.Height - 1, y * s.Height / h) * s.Width + Math.Min(s.Width - 1, x * s.Width / w)];
        byte And(int x, int y) => s.AndMask![Math.Min(s.Height - 1, y * s.Height / h) * s.Width + Math.Min(s.Width - 1, x * s.Width / w)];

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB
        void* bits;
        var color = PInvoke.CreateDIBSection(HDC.Null, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (color.IsNull || bits is null) return HCURSOR.Null;
        var px = (uint*)bits;
        // Monochrome AND mask: rows padded to 16 bits.
        int maskStride = (w + 15) / 16 * 2;
        var mask = new byte[maskStride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint p = Px(x, y);
                if (s.HasAlpha)
                {
                    px[y * w + x] = p;
                    // Alpha cursors: Windows uses the alpha channel; the mask only matters where alpha is 0.
                    if ((p >> 24) == 0) mask[y * maskStride + x / 8] |= (byte)(0x80 >> (x & 7));
                }
                else
                {
                    px[y * w + x] = p & 0xFFFFFF; // alpha 0 => Windows applies AND/XOR semantics
                    if (And(x, y) != 0) mask[y * maskStride + x / 8] |= (byte)(0x80 >> (x & 7));
                }
            }
        HBITMAP maskBmp;
        fixed (byte* m = mask) maskBmp = PInvoke.CreateBitmap(w, h, 1, 1, m);
        if (maskBmp.IsNull) { PInvoke.DeleteObject(color); return HCURSOR.Null; }
        var info = new ICONINFO
        {
            fIcon = false,
            xHotspot = (uint)Math.Clamp((int)Math.Round(s.HotX * scaleX), 0, w - 1),
            yHotspot = (uint)Math.Clamp((int)Math.Round(s.HotY * scaleY), 0, h - 1),
            hbmMask = maskBmp,
            hbmColor = color,
        };
        var icon = PInvoke.CreateIconIndirect(&info);
        PInvoke.DeleteObject(color);
        PInvoke.DeleteObject(maskBmp);
        return new HCURSOR(icon.Value);
    }

    public void Clear()
    {
        foreach (var c in _cache.Values) PInvoke.DestroyCursor(c);
        _cache.Clear();
    }

    public void Dispose() => Clear();
}
