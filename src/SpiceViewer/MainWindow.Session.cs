using System.Runtime.InteropServices;
using Spice.Canvas;
using Spice.Session;
using Spice.Transport;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpiceViewer;

sealed unsafe partial class MainWindow
{
    const uint CF_UNICODETEXT = 13, CF_DIB = 8;
    readonly CancellationTokenSource _connectCts = new();
    bool _settingClipboard;

    /// <summary>Starts connecting at once — before the window exists, since the ticket expires quickly.</summary>
    public void StartSession()
    {
        var s = new SpiceSession(_vv);
        _session = s;
        s.PrimarySurfaceChanged += (_, _) => Post(OnPrimaryChanged);
        s.CopiedBits += (r, dx, dy) => _presenter?.CopyBits(r, dx, dy);
        s.Invalidated += r => _presenter?.Invalidate(r);
        s.CursorChanged += c => Post(() => OnCursor(c));
        s.CursorMoved += OnGuestCursorMoved;
        s.MouseModeChanged += _ => Post(OnMouseModeChanged);
        s.AgentConnectedChanged += _ => Post(OnAgentChanged);
        s.Disconnected += e => Post(() => OnDisconnected(e));
        s.GuestClipboardGrab += types => Post(() => OnGuestClipboardGrab(types));
        s.GuestClipboardData += (t, d) => Post(() => OnGuestClipboardData(t, d));
        s.GuestClipboardRequest += t => Post(() => OnGuestClipboardRequest(t));
        AttachAudio(s);
        SessionStarter.Connect(this, s, _connectCts.Token);
    }

    void OnPrimaryChanged()
    {
        if (_session is null || _state == ViewState.Disconnected) return;
        int w, h;
        lock (_session.Canvas.Sync)
        {
            var p = _session.Canvas.Primary;
            if (p is null) return;
            w = p.Width; h = p.Height;
        }
        bool first = !_gotFirstFrame;
        bool sizeChanged = w != _guestW || h != _guestH;
        _guestW = w; _guestH = h;
        _state = ViewState.Connected;
        if (_presenter is null)
        {
            try { _presenter = new Presenter(_hwnd, _session.Canvas); }
            catch (Exception e)
            {
                Log.Error($"presenter: {e}");
                Dialogs.Error(_hwnd, "SpiceViewer", "The display could not be initialised.", e.Message);
                PInvoke.DestroyWindow(_hwnd);
                return;
            }
        }
        _gotFirstFrame = true;
        Log.Info($"guest display {w}x{h}{(first ? $" — first frame {Program.Uptime.ElapsedMilliseconds} ms after start" : "")}");
        // The guest is following the window: don't fight it by resizing the window back. A remembered window keeps
        // its place and size (in 1:1 it still takes the guest's size, in place).
        if (first) { if (!_restoredPlacement || OneToOneActive) SizeWindowToGuest(center: !_restoredPlacement); }
        else if (sizeChanged && !GuestFollowsWindow) SizeWindowToGuest(center: false);
        _presenter.InvalidateAll();
        if (first)
        {
            UpdateKeyboardCapture();
            if ((_vv.Fullscreen || _restoreFullscreen) && !_fullscreen) ToggleFullscreen();
            OnHostClipboardChanged();
        }
    }

    CursorShape? _guestShape;
    (double X, double Y) _guestCursorScale;

    /// <summary>Guest-to-client pixel scale, the one the guest cursor is drawn at.</summary>
    (double X, double Y) CursorScale()
    {
        var rc = GuestRect();
        return (_guestW > 0 ? (double)rc.Width / _guestW : 1, _guestH > 0 ? (double)rc.Height / _guestH : 1);
    }

    /// <summary>(Re)builds the native cursor for the current shape at the current scale (resize, DPI, fullscreen).</summary>
    void RebuildGuestCursor()
    {
        if (_guestShape is null) return;
        _guestCursorScale = CursorScale();
        _guestCursor = _cursors.Get(_guestShape, _guestCursorScale.X, _guestCursorScale.Y);
    }

    void OnCursor(CursorShape? shape)
    {
        if (shape is null) { _guestCursorHidden = true; }
        else
        {
            _guestCursorHidden = false;
            _guestShape = shape;
            RebuildGuestCursor();
        }
        // Apply now if the pointer is over our client area.
        PInvoke.GetCursorPos(out var pt);
        PInvoke.ScreenToClient(_hwnd, ref pt);
        var r = ClientRect();
        if (pt.X >= 0 && pt.Y >= 0 && pt.X < r.right && pt.Y < r.bottom && PInvoke.GetForegroundWindow() == _hwnd)
            PInvoke.SetCursor(CurrentClientCursor());
    }

    // Server mouse mode: the guest owns the pointer position. Moves arrive on the cursor channel thread;
    // keep only the latest and post at most one UI update at a time (no per-move allocation).
    long _guestCursorPos; // (x << 32) | (uint)y
    int _cursorMovePosted;
    Action? _applyGuestCursorPos;

    void OnGuestCursorMoved(int x, int y)
    {
        Interlocked.Exchange(ref _guestCursorPos, ((long)x << 32) | (uint)y);
        if (_serverCursor != ServerCursorMode.Guest) return; // hidden pointer: nothing to move
        if (Interlocked.Exchange(ref _cursorMovePosted, 1) == 0)
            Post(_applyGuestCursorPos ??= ApplyGuestCursorPos);
    }

    /// <summary>
    /// While the pointer is captured, show the guest's cursor shape on the real pointer and put it where the guest says
    /// (the "Show guest cursor in server mouse mode" setting; otherwise the pointer stays hidden where it was).
    /// Raw input deltas don't depend on the pointer position, so moving it doesn't feed back into the guest.
    /// </summary>
    void ApplyGuestCursorPos()
    {
        Volatile.Write(ref _cursorMovePosted, 0);
        if (!_pointerCaptured || _serverCursor != ServerCursorMode.Guest || _guestW == 0 || _guestH == 0) return;
        long p = Interlocked.Read(ref _guestCursorPos);
        int gx = (int)(p >> 32), gy = (int)p;
        var g = GuestRect();
        var pt = new System.Drawing.Point(g.left + (int)((long)gx * g.Width / _guestW), g.top + (int)((long)gy * g.Height / _guestH));
        // Never warp the pointer outside the visible image (cropped 1:1).
        var v = VisibleGuestRect();
        pt.X = Math.Clamp(pt.X, v.left, v.right - 1);
        pt.Y = Math.Clamp(pt.Y, v.top, v.bottom - 1);
        PInvoke.ClientToScreen(_hwnd, ref pt);
        PInvoke.SetCursorPos(pt.X, pt.Y);
    }

    void OnMouseModeChanged()
    {
        if (!ServerMouse) ReleasePointer();
    }

    void OnAgentChanged()
    {
        Log.Info($"agent: connected={_session?.AgentConnected} monitors={_session?.AgentSupportsMonitorsConfig} clipboard={_session?.AgentClipboardReady}");
        RequestGuestResize();
        OnHostClipboardChanged();
    }

    // ---------------- disconnect and errors ----------------

    internal void OnDisconnected(Exception? error)
    {
        if (_state == ViewState.Disconnected) return;
        bool hadFrame = _gotFirstFrame;
        _state = ViewState.Disconnected;
        UninstallCapture();
        Log.Info($"disconnected: {error?.Message ?? "closed by server"}");
        if (!hadFrame)
        {
            var (instruction, content) = Describe(error);
            SetStatus(instruction);
            Dialogs.Error(_hwnd, _vv.DisplayTitle, instruction, content);
            PInvoke.DestroyWindow(_hwnd);
            return;
        }
        if (_fullscreen) { _fullscreenWhenEnded = true; ToggleFullscreen(); }
        DrawDisconnectedOverlay(error is null ? "The virtual machine closed the connection." : Describe(error).Instruction);
        PInvoke.SetWindowText(_hwnd, $"{_vv.DisplayTitle} — disconnected");
        _guestCursorHidden = false;
        _guestCursor = HCURSOR.Null;
        _guestShape = null;
    }

    static (string Instruction, string? Content) Describe(Exception? e) => e switch
    {
        SpiceConnectException { Failure: ConnectFailure.TicketRejected } =>
            ("This console link has expired. In Proxmox, choose Console → SPICE again.", null),
        SpiceConnectException { Failure: ConnectFailure.Proxy } p when p.Message.Contains("401") =>
            ("This console link has expired. In Proxmox, choose Console → SPICE again.", p.Message),
        SpiceConnectException { Failure: ConnectFailure.Untrusted or ConnectFailure.SubjectMismatch } c =>
            ("The server's certificate does not match this connection file, so the viewer refused to connect.", c.Message),
        SpiceConnectException { Failure: ConnectFailure.Network } n =>
            ("The server can't be reached. Check your network or VPN connection and try again.", n.Message),
        SpiceConnectException c => ("The connection to the virtual machine failed.", c.Message),
        null => ("The virtual machine closed the connection.", null),
        _ => ("The connection to the virtual machine was lost.", e.Message),
    };

    /// <summary>Dims the last frame, writes the reason on it, and offers a Close button.</summary>
    void DrawDisconnectedOverlay(string text)
    {
        if (_session is null) return;
        lock (_session.Canvas.Sync)
        {
            var s = _session.Canvas.Primary;
            if (s is not null)
            {
                var px = MemoryMarshal.Cast<byte, uint>(s.Pixels.AsSpan());
                for (int i = 0; i < px.Length; i++) px[i] = ((px[i] >> 2) & 0x3F3F3F) | 0xFF000000;
                DrawText(s, "Disconnected", text);
            }
        }
        _presenter?.InvalidateAll();
        fixed (char* cls = "BUTTON")
        fixed (char* label = "Close")
        {
            _closeButton = PInvoke.CreateWindowEx(0, cls, label,
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_TABSTOP | (WINDOW_STYLE)1 /* BS_DEFPUSHBUTTON */,
                0, 0, 100, 32, _hwnd, (HMENU)(nint)IdCloseButton, PInvoke.GetModuleHandle((PCWSTR)null), null);
        }
        PInvoke.SendMessage(_closeButton, PInvoke.WM_SETFONT, (WPARAM)(nuint)_font.Value, 1);
        PositionCloseButton();
        PInvoke.SetFocus(_closeButton);
    }

    void PositionCloseButton()
    {
        if (_closeButton.IsNull) return;
        var rc = ClientRect();
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        int bw = (int)(110 * dpi / 96), bh = (int)(32 * dpi / 96);
        PInvoke.SetWindowPos(_closeButton, HWND.Null, (rc.Width - bw) / 2, rc.Height / 2 + (int)(56 * dpi / 96), bw, bh,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>Renders text into a surface with GDI (on a DIB copy of the pixels).</summary>
    void DrawText(Surface s, string title, string body)
    {
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = s.Width;
        bmi.bmiHeader.biHeight = -s.Height;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        void* bits;
        var dib = PInvoke.CreateDIBSection(HDC.Null, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (dib.IsNull) return;
        var dc = PInvoke.CreateCompatibleDC(HDC.Null);
        var oldBmp = PInvoke.SelectObject(dc, dib);
        new Span<byte>(bits, s.Pixels.Length).Clear();
        s.Pixels.AsSpan().CopyTo(new Span<byte>(bits, s.Pixels.Length));
        PInvoke.SetBkMode(dc, BACKGROUND_MODE.TRANSPARENT);
        PInvoke.SetTextColor(dc, new COLORREF(0x00FFFFFF));
        // Size text to the guest resolution so it reads well once scaled to the window.
        var big = MakeFont(-Math.Max(18, s.Height / 22), 600);
        var small = MakeFont(-Math.Max(12, s.Height / 45), 400);
        var oldFont = PInvoke.SelectObject(dc, big);
        var r1 = new RECT(0, s.Height / 2 - s.Height / 10, s.Width, s.Height / 2 - s.Height / 40);
        fixed (char* t = title) PInvoke.DrawText(dc, t, title.Length, ref r1, DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_BOTTOM | DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        PInvoke.SelectObject(dc, small);
        var r2 = new RECT(s.Width / 10, s.Height / 2 - s.Height / 50, s.Width * 9 / 10, s.Height / 2 + s.Height / 12);
        fixed (char* t = body) PInvoke.DrawText(dc, t, body.Length, ref r2, DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_WORDBREAK | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        PInvoke.SelectObject(dc, oldFont);
        PInvoke.DeleteObject(big);
        PInvoke.DeleteObject(small);
        new Span<byte>(bits, s.Pixels.Length).CopyTo(s.Pixels);
        var px = MemoryMarshal.Cast<byte, uint>(s.Pixels.AsSpan());
        for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000; // GDI clears alpha where it draws
        PInvoke.SelectObject(dc, oldBmp);
        PInvoke.DeleteDC(dc);
        PInvoke.DeleteObject(dib);
    }

    // ---------------- clipboard ----------------

    /// <summary>The host clipboard changed (or the agent appeared): offer it to the guest.</summary>
    void OnHostClipboardChanged()
    {
        if (_settingClipboard || _session is not { AgentClipboardReady: true }) return;
        if (PInvoke.GetClipboardOwner() == _hwnd) return; // our own copy of the guest clipboard
        var types = new List<ClipboardType>();
        if (PInvoke.IsClipboardFormatAvailable(CF_UNICODETEXT)) types.Add(ClipboardType.Utf8Text);
        if (PInvoke.IsClipboardFormatAvailable(CF_DIB)) types.Add(ClipboardType.ImagePng);
        if (types.Count > 0) _session.ClipboardGrab([.. types]);
        else _session.ClipboardRelease();
    }

    void OnGuestClipboardRequest(ClipboardType type)
    {
        if (_session is null || !OpenClipboardRetry()) return;
        try
        {
            if (type == ClipboardType.Utf8Text)
            {
                var h = PInvoke.GetClipboardData(CF_UNICODETEXT);
                if (h.IsNull) return;
                var text = ClipboardFormats.ReadUnicodeText((HGLOBAL)h.Value);
                if (text is not null) _session.SendClipboardText(text);
            }
            else if (type is ClipboardType.ImagePng or ClipboardType.ImageBmp)
            {
                var h = PInvoke.GetClipboardData(CF_DIB);
                if (h.IsNull) return;
                var png = ClipboardFormats.DibToPng((HGLOBAL)h.Value);
                if (png is not null) _session.SendClipboardData(ClipboardType.ImagePng, png);
            }
        }
        finally { PInvoke.CloseClipboard(); }
    }

    void OnGuestClipboardGrab(ClipboardType[] types)
    {
        if (_session is null) return;
        // Text first; images otherwise. Fetched eagerly: clipboard payloads are small and this avoids blocking
        // inside WM_RENDERFORMAT while the network round-trips.
        if (types.Contains(ClipboardType.Utf8Text)) _session.ClipboardRequest(ClipboardType.Utf8Text);
        else if (types.Contains(ClipboardType.ImagePng)) _session.ClipboardRequest(ClipboardType.ImagePng);
        else if (types.Contains(ClipboardType.ImageBmp)) _session.ClipboardRequest(ClipboardType.ImageBmp);
    }

    void OnGuestClipboardData(ClipboardType type, byte[] data)
    {
        if (_session is null) return;
        HGLOBAL mem;
        uint format;
        if (type == ClipboardType.Utf8Text)
        {
            mem = ClipboardFormats.TextToGlobal(_session.DecodeClipboardText(data));
            format = CF_UNICODETEXT;
        }
        else
        {
            // Guest images are validated and re-encoded as a canonical DIB, never published as received.
            byte[]? dib = type switch
            {
                ClipboardType.ImagePng => ClipboardFormats.PngToDib(data),
                ClipboardType.ImageBmp => ClipboardFormats.BmpToDib(data),
                _ => null,
            };
            if (dib is null) return;
            mem = ClipboardFormats.ToGlobal(dib);
            format = CF_DIB;
        }
        if (mem.IsNull) { Log.Warn("clipboard: could not allocate memory for guest clipboard data"); return; }
        if (!OpenClipboardRetry()) { PInvoke.GlobalFree(mem); return; }
        _settingClipboard = true;
        try
        {
            // On success the clipboard owns the block; on any failure it is still ours to free.
            bool owned = PInvoke.EmptyClipboard() && !PInvoke.SetClipboardData(format, (HANDLE)mem.Value).IsNull;
            if (!owned) PInvoke.GlobalFree(mem);
        }
        finally { PInvoke.CloseClipboard(); _settingClipboard = false; }
    }

    bool OpenClipboardRetry()
    {
        for (int i = 0; i < 5; i++)
        {
            if (PInvoke.OpenClipboard(_hwnd)) return true;
            Thread.Sleep(10);
        }
        return false;
    }
}

/// <summary>Kept out of the unsafe MainWindow: C# forbids await in an unsafe context.</summary>
static class SessionStarter
{
    public static void Connect(MainWindow window, SpiceSession s, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            await s.ConnectAsync(ct);
            window.Post(() => Log.Info($"connected: {string.Join(", ", s.OpenChannels)}"));
        }
        catch (Exception e) { window.Post(() => window.OnDisconnected(e)); }
    });
}
