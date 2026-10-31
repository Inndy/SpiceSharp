using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace SpiceViewer;

/// <summary>
/// The status overlay's image (docs/decisions/0044): its text drawn with GDI in a monospace font into a DIB section,
/// then turned in place into premultiplied BGRA (white text, translucent dark background). Drawn only when the text or
/// the font size changes. The DIB is kept and reused while the image fits it, so a redraw allocates nothing. Owned by one
/// thread (the presenter's).
/// </summary>
sealed unsafe class OverlayBitmap : IDisposable
{
    /// <summary>Background opacity: dark enough to read white text over any guest picture, light enough to see through.</summary>
    public const byte BackgroundAlpha = 0xB8;

    HDC _dc;
    HBITMAP _dib;
    HGDIOBJ _oldBitmap, _oldFont;
    HFONT _font;
    byte* _bits;
    int _capW, _capH;
    int _charW, _lineH;

    /// <summary>The image: <see cref="Width"/> × <see cref="Height"/> pixels at <see cref="Bits"/>, rows <see cref="Stride"/> bytes apart.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Stride => _capW * 4;
    public byte* Bits => _bits;
    /// <summary>Counts redraws, so a GPU copy can tell it is stale.</summary>
    public int Version { get; private set; }
    /// <summary>The font height (pixels) the image was drawn with; 0 before the first draw.</summary>
    public int FontPx { get; private set; }

    /// <summary>Draws <paramref name="text"/> with a font <paramref name="fontPx"/> pixels high. False if GDI failed.</summary>
    public bool Render(OverlayText text, int fontPx)
    {
        if (_dc.IsNull)
        {
            _dc = PInvoke.CreateCompatibleDC(HDC.Null);
            if (_dc.IsNull) return false;
            PInvoke.SetBkMode(_dc, BACKGROUND_MODE.TRANSPARENT);
            PInvoke.SetTextColor(_dc, new COLORREF(0x00FFFFFF));
        }
        if (fontPx != FontPx || _font.IsNull)
        {
            if (!CreateFont(fontPx)) return false;
            FontPx = fontPx;
        }
        int pad = Math.Max(2, _lineH / 3);
        int w = Math.Max(1, text.Columns) * _charW + 2 * pad, h = Math.Max(1, text.LineCount) * _lineH + 2 * pad;
        if (!EnsureDib(w, h)) return false;

        for (int y = 0; y < h; y++) new Span<byte>(_bits + (long)y * Stride, w * 4).Clear();
        for (int i = 0; i < text.LineCount; i++)
        {
            var line = text.Line(i);
            fixed (char* p = line)
                PInvoke.ExtTextOut(_dc, pad, pad + i * _lineH, (ETO_OPTIONS)0, null, p, (uint)line.Length, null);
        }
        PInvoke.GdiFlush();
        // Grayscale antialiasing draws white as coverage c in every channel: premultiplied white over the background.
        for (int y = 0; y < h; y++)
        {
            uint* row = (uint*)(_bits + (long)y * Stride);
            for (int x = 0; x < w; x++)
            {
                uint c = row[x] & 0xFF;
                uint a = c + ((BackgroundAlpha * (255 - c) + 127) / 255);
                row[x] = (a << 24) | (c << 16) | (c << 8) | c;
            }
        }
        Width = w;
        Height = h;
        Version++;
        return true;
    }

    bool CreateFont(int fontPx)
    {
        if (!_font.IsNull)
        {
            PInvoke.SelectObject(_dc, _oldFont);
            PInvoke.DeleteObject(_font);
            _font = HFONT.Null;
        }
        var lf = new LOGFONTW
        {
            lfHeight = -fontPx,
            lfWeight = 400,
            lfQuality = FONT_QUALITY.ANTIALIASED_QUALITY, // grayscale: coverage is the same in every channel
            lfPitchAndFamily = 1 | (3 << 4), // FIXED_PITCH | FF_MODERN
        };
        "Consolas".AsSpan().CopyTo(lf.lfFaceName.AsSpan());
        _font = PInvoke.CreateFontIndirect(&lf);
        if (_font.IsNull) return false;
        _oldFont = PInvoke.SelectObject(_dc, _font);
        TEXTMETRICW tm;
        if (!PInvoke.GetTextMetrics(_dc, &tm)) return false;
        _charW = Math.Max(1, tm.tmAveCharWidth);
        _lineH = Math.Max(1, tm.tmHeight);
        return true;
    }

    /// <summary>A DIB of at least w×h, reused while it fits; grown in steps so a longer line doesn't reallocate each time.</summary>
    bool EnsureDib(int w, int h)
    {
        if (w <= _capW && h <= _capH && !_dib.IsNull) return true;
        int cw = Math.Max(_capW, (w + 63) & ~63), ch = Math.Max(_capH, (h + 31) & ~31);
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = cw;
        bmi.bmiHeader.biHeight = -ch; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        void* bits;
        var dib = PInvoke.CreateDIBSection(_dc, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (dib.IsNull) return false;
        var old = PInvoke.SelectObject(_dc, dib);
        if (_dib.IsNull) _oldBitmap = old;
        else PInvoke.DeleteObject(_dib);
        _dib = dib;
        _bits = (byte*)bits;
        _capW = cw; _capH = ch;
        return true;
    }

    public void Dispose()
    {
        if (_dc.IsNull) return;
        if (!_font.IsNull) { PInvoke.SelectObject(_dc, _oldFont); PInvoke.DeleteObject(_font); _font = HFONT.Null; }
        if (!_dib.IsNull) { PInvoke.SelectObject(_dc, _oldBitmap); PInvoke.DeleteObject(_dib); _dib = HBITMAP.Null; }
        PInvoke.DeleteDC(_dc);
        _dc = HDC.Null;
        _bits = null;
        _capW = _capH = 0;
        Width = Height = 0;
        FontPx = 0;
    }
}
