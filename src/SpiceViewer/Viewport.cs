using Windows.Win32.Foundation;

namespace SpiceViewer;

/// <summary>
/// How the guest image reaches the window: one of the presenter's two paths (docs/design/spec.md, Rendering and
/// presentation). Pure function of its inputs: the presenter and <c>MainWindow.GuestRect()</c> use the same numbers.
/// </summary>
/// <remarks>
/// <para><b>Direct</b> (<see cref="Direct"/>): the swap chain is the guest surface's size and DXGI stretches it over the
/// whole client area. Chosen in Fit when the client has the guest's aspect ratio within a pixel of rounding (the
/// window aspect-locks during WM_SIZING, and with the agent the guest takes the window's size), and in 1:1 when the
/// client is exactly the guest's size. No shader; dirty and scroll rects go straight to Present1.</para>
/// <para><b>Letterbox</b>: the swap chain is the client size; one shader draws the guest texture into the destination
/// rectangle (<see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>, <see cref="Height"/>, in buffer pixels) and the rest
/// is bars. Fit scales uniformly and centers; 1:1 draws unscaled, centered on an axis where the client is larger and
/// cropped right/bottom where it is smaller.</para>
/// <para>Limits: a guest larger than a texture (<c>maxDim</c>) shows only its top-left
/// <see cref="SourceWidth"/>×<see cref="SourceHeight"/>. A letterboxed client larger than a texture (a window spanning
/// monitors) gets the largest buffer with the client's aspect ratio, which DXGI stretches back: the image is scaled
/// uniformly, and 1:1 is then not exact.</para>
/// </remarks>
readonly record struct Viewport(bool Direct, int BufferWidth, int BufferHeight, int X, int Y, int Width, int Height,
    int SourceWidth, int SourceHeight)
{
    /// <summary>D3D11 feature level 11 texture limit; lower levels pass their own.</summary>
    public const int DefaultMaxDimension = 16384;

    public static Viewport Compute(int clientW, int clientH, int guestW, int guestH, bool oneToOne, int maxDim = DefaultMaxDimension)
    {
        clientW = Math.Max(1, clientW); clientH = Math.Max(1, clientH);
        guestW = Math.Max(1, guestW); guestH = Math.Max(1, guestH);
        maxDim = Math.Max(1, maxDim);
        int sw = Math.Min(guestW, maxDim), sh = Math.Min(guestH, maxDim);

        // The guest image's destination in client pixels.
        int dw, dh;
        if (oneToOne)
        {
            if (clientW == sw && clientH == sh) return new Viewport(true, sw, sh, 0, 0, sw, sh, sw, sh);
            dw = sw; dh = sh;
        }
        else
        {
            if ((long)clientW * sh > (long)clientH * sw)
            {
                dh = clientH; // client is wider than the guest: bars left and right
                dw = (int)Math.Max(1, ((long)clientH * sw + sh / 2) / sh);
            }
            else
            {
                dw = clientW;
                dh = (int)Math.Max(1, ((long)clientW * sh + sw / 2) / sw);
            }
            // Aspect-locked window (rounding leaves at most a pixel): stretch the guest-sized swap chain.
            if (Math.Abs(dw - clientW) <= 1 && Math.Abs(dh - clientH) <= 1) return new Viewport(true, sw, sh, 0, 0, sw, sh, sw, sh);
        }
        int dx = clientW > dw ? (clientW - dw) / 2 : 0;
        int dy = clientH > dh ? (clientH - dh) / 2 : 0;

        int bw = clientW, bh = clientH;
        if (bw > maxDim || bh > maxDim)
        {
            // Largest buffer with the client's aspect ratio; DXGI stretches it back uniformly.
            double s = Math.Max((double)clientW / maxDim, (double)clientH / maxDim);
            bw = Math.Clamp((int)Math.Round(clientW / s), 1, maxDim);
            bh = Math.Clamp((int)Math.Round(clientH / s), 1, maxDim);
            double kx = (double)bw / clientW, ky = (double)bh / clientH;
            int l = (int)Math.Round(dx * kx), t = (int)Math.Round(dy * ky);
            int r = (int)Math.Round((dx + dw) * kx), b = (int)Math.Round((dy + dh) * ky);
            dx = l; dy = t; dw = Math.Max(1, r - l); dh = Math.Max(1, b - t);
        }
        return new Viewport(false, bw, bh, dx, dy, dw, dh, sw, sh);
    }

    /// <summary>The source is drawn 1:1 into the buffer (point sampling is exact).</summary>
    public bool Unscaled => Width == SourceWidth && Height == SourceHeight;

    /// <summary>The guest image in client pixels (may extend past the client area in cropped 1:1).</summary>
    public RECT GuestRectInClient(int clientW, int clientH, int guestW, int guestH)
    {
        clientW = Math.Max(1, clientW); clientH = Math.Max(1, clientH);
        guestW = Math.Max(1, guestW); guestH = Math.Max(1, guestH);
        double kx = (double)clientW / BufferWidth, ky = (double)clientH / BufferHeight;
        // The destination holds the source; a guest cropped at the texture limit continues past it at the same scale.
        double w = (double)Width * guestW / SourceWidth, h = (double)Height * guestH / SourceHeight;
        int l = (int)Math.Round(X * kx), t = (int)Math.Round(Y * ky);
        int r = (int)Math.Round((X + w) * kx), b = (int)Math.Round((Y + h) * ky);
        return new RECT(l, t, Math.Max(r, l + 1), Math.Max(b, t + 1));
    }
}
