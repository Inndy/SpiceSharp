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
        s.DisplayStateChanged += _ => Post(OnDisplayStateChanged);
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
        int w, h, x, y;
        bool provisional;
        lock (_session.Canvas.Sync)
        {
            // No primary surface: OnDisplayStateChanged handles that.
            if (_session.Canvas.Primary is null) return;
            // Everything the window does follows the guest's monitor area, not the surface behind it (docs/decisions/0035).
            var area = _session.Canvas.PrimaryArea;
            w = area.Width; h = area.Height; x = area.Left; y = area.Top;
            provisional = !_session.Canvas.PrimaryAreaIsMonitor;
        }
        bool first = !_gotFirstFrame;
        bool sizeChanged = w != _guestW || h != _guestH;
        // The first frame can come before its monitors config, showing the whole surface (640x512 for a 640x480 mode)
        // for a moment. The next notification is then still part of the first frame for window sizing: the window takes
        // the monitor's size under the first-frame rules, even when the guest follows the window, and a remembered
        // window keeps its size.
        bool firstSizing = first || (_firstSizingProvisional && sizeChanged);
        _firstSizingProvisional = first && provisional;
        bool wasOff = _state == ViewState.DisplayOff, wasFailed = _state == ViewState.DisplayFailed;
        _guestW = w; _guestH = h; _guestX = x; _guestY = y;
        _state = ViewState.Connected;
        // The display is back (or a mode change finished): no "display off" message.
        _messageTitle = _messageBody = _messageDetail = null;
        _presenter?.SetPlaceholder(null);
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
        Log.Info($"guest display {w}x{h}{(x != 0 || y != 0 ? $" at ({x},{y})" : "")}" +
            $"{(first ? $" — first frame {Program.Uptime.ElapsedMilliseconds} ms after start" : wasOff ? " — back on" : "")}");
        // The guest is following the window: don't fight it by resizing the window back. A remembered window keeps
        // its place and size (in 1:1 it still takes the guest's size, in place).
        if (firstSizing) { if (!_restoredPlacement || OneToOneActive) SizeWindowToGuest(center: first && !_restoredPlacement); }
        else if (sizeChanged && !GuestFollowsWindow) SizeWindowToGuest(center: false);
        _presenter.InvalidateAll();
        if (first)
        {
            UpdateKeyboardCapture();
            if ((_vv.Fullscreen || _restoreFullscreen) && !_fullscreen) ToggleFullscreen();
            OnHostClipboardChanged();
        }
        else if (wasOff || wasFailed) UpdateKeyboardCapture(); // the hook was removed while there was nothing to show
    }

    /// <summary>
    /// The guest has no display: at link (Waiting: nothing sent yet, e.g. a guest already blank; or Off: a DISPLAY_MARK
    /// without a primary surface) or later (Off: the primary surface was destroyed without a new one). The window says so
    /// instead of "Connecting…" or a frozen frame, and keys keep going to the guest so one can wake it
    /// (docs/decisions/0035). The way back is <see cref="OnPrimaryChanged"/>. A mode change whose destroy and create are
    /// handled apart shows the message for those few tens of milliseconds (accepted by the human, decision 0035).
    /// </summary>
    void OnDisplayStateChanged()
    {
        // Read now: a destroy followed at once by a create (a mode change) is On by the time this runs.
        if (_session is not { DisplayState: GuestDisplayState.Off or GuestDisplayState.Waiting } s) return;
        if (_state is ViewState.Connecting or ViewState.Connected or ViewState.DisplayOff) EnterDisplayOff(s.DisplayState);
    }

    void EnterDisplayOff(GuestDisplayState state)
    {
        if (_state == ViewState.Connected)
        {
            // Nothing to point at any more: let go of the pointer and any held button. Keys stay as they are, since
            // they keep going to the guest (their key-ups too).
            ReleasePointer();
            _session?.ReleaseAllButtons();
            if (PInvoke.GetCapture() == _hwnd) PInvoke.ReleaseCapture();
        }
        _state = ViewState.DisplayOff;
        UpdateKeyboardCapture();
        // Waiting is neutral: every normal connect spends ~0.1 s in it before the first surface (L11, the human's
        // decision in 0035), so it must not suggest a problem.
        if (state == GuestDisplayState.Waiting)
            ShowMessage("Waiting for the guest's display…", "Press a key if the guest is asleep.",
                "Connected — waiting for the guest's display… (press a key if the guest is asleep).");
        else
            ShowMessage("The guest's display is off", "Press a key (Shift) to wake it. Mouse clicks are not sent until the picture is back.",
                "Connected. The guest's display is off — press a key (Shift) to wake it.");
        Log.Info($"guest display: {(state == GuestDisplayState.Waiting ? "none yet" : "off")}; keys still go to the guest");
    }

    // The message shown instead of the guest display, while there is none (null otherwise); the detail is optional.
    string? _messageTitle, _messageBody, _messageDetail;

    /// <summary>
    /// Shows a message instead of the guest display. Before any frame the window paints <paramref name="status"/> with
    /// GDI. Once a presenter exists, its flip-model swap chain stays composited over GDI painting even after it is
    /// released (finding L5), so the presenter shows the message instead, as an image drawn at the client size.
    /// <paramref name="detail"/>, if any, is a smaller, dimmer line under the body (or the status).
    /// </summary>
    void ShowMessage(string title, string body, string status, string? detail = null)
    {
        _messageTitle = title; _messageBody = body; _messageDetail = detail;
        _messageW = _messageH = 0;
        if (_presenter is null) { SetStatus(status, detail); return; }
        RenderMessage();
    }

    int _messageW, _messageH; // the size the message image was last drawn at

    /// <summary>
    /// (Re)draws the message image for the presenter at the current client size: when shown, and after a resize ends
    /// (during a drag DXGI stretches the last image). Not at all while there is no message.
    /// </summary>
    void RenderMessage()
    {
        if (_presenter is null || _messageTitle is null || _messageBody is null || PInvoke.IsIconic(_hwnd)) return;
        var rc = ClientRect();
        int w = Math.Max(1, rc.Width), h = Math.Max(1, rc.Height);
        // Within the device's texture limit; DXGI stretches the image back to the client.
        while (w > _presenter.MaxDimension || h > _presenter.MaxDimension) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
        if (w == _messageW && h == _messageH) return;
        _messageW = w; _messageH = h;
        var pixels = new byte[w * h * 4];
        MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()).Fill(0xFF202020); // the window background
        // A disconnected window has the Close button over the image: keep the text above it (in image pixels).
        int textBottom = _state == ViewState.Disconnected && rc.Height > 0
            ? (int)((long)CloseButtonTop(rc, PInvoke.GetDpiForWindow(_hwnd)) * h / rc.Height) : h;
        DrawTextBlock(pixels, w, h, new SpiceRect(0, 0, w, h), _messageTitle, _messageBody, _messageDetail, textBottom);
        _presenter.SetPlaceholder(new Presenter.Placeholder(pixels, w, h));
    }

    /// <summary>The first frame showed a whole surface not yet narrowed by its monitors config (see OnPrimaryChanged).</summary>
    bool _firstSizingProvisional;

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
        var (px, py) = GuestView.GuestToClient(gx, gy, _guestX, _guestY, g.left, g.top, g.Width, g.Height, _guestW, _guestH);
        var pt = new System.Drawing.Point(px, py);
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

    /// <summary>Every channel is linked: from now on keys reach the guest, whether or not it has a display yet.</summary>
    internal void OnSessionLinked(SpiceSession s)
    {
        Log.Info($"connected: {string.Join(", ", s.OpenChannels)}");
        if (s != _session) return;
        _linked = true;
    }

    // ---------------- disconnect and errors ----------------

    internal void OnDisconnected(Exception? error)
    {
        if (_state == ViewState.Disconnected) return;
        // A session that got as far as a picture, or as far as saying the guest's display is off, keeps its window
        // with the reason and a Close button; one that never got there reports the failure in a dialog.
        bool displayOff = _state == ViewState.DisplayOff;
        bool hadFrame = _gotFirstFrame || displayOff;
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
        // The reason, then the detail the dialog would show (e.g. which channel the server closed) as a second, dimmer
        // line (finding L14, decision 0035).
        var (reason, detail) = Describe(error);
        DrawDisconnectedOverlay(reason, detail == reason ? null : detail);
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
        // The server ended the connection (L12). No SPICE message names a takeover by another client (DISCONNECTING
        // carries only a numeric reason; spice-gtk just logs it), so say only what we know.
        SpiceConnectException { Failure: ConnectFailure.Closed } c => ("The server closed the connection.", c.Message),
        SpiceConnectException c => ("The connection to the virtual machine failed.", c.Message),
        null => ("The virtual machine closed the connection.", null),
        _ => ("The connection to the virtual machine was lost.", e.Message),
    };

    /// <summary>Dims the last frame, writes the reason (and its detail, if any) on it, and offers a Close button.</summary>
    void DrawDisconnectedOverlay(string text, string? detail)
    {
        if (_session is null) return;
        bool drawn = false;
        lock (_session.Canvas.Sync)
        {
            var s = _session.Canvas.Primary;
            if (s is not null)
            {
                var px = MemoryMarshal.Cast<byte, uint>(s.Pixels.AsSpan());
                for (int i = 0; i < px.Length; i++) px[i] = ((px[i] >> 2) & 0x3F3F3F) | 0xFF000000;
                // Keep the text above where the Close button sits in the window now, mapped to surface pixels. The
                // overlay is drawn once; a later resize scales it with the frame while the button keeps its offset.
                var area = _session.Canvas.PrimaryArea;
                var guest = GuestRect();
                int textBottom = guest.Height > 0
                    ? area.Top + (int)((long)(CloseButtonTop(ClientRect(), PInvoke.GetDpiForWindow(_hwnd)) - guest.top) * area.Height / guest.Height)
                    : area.Bottom;
                DrawTextBlock(s.Pixels, s.Width, s.Height, area, "Disconnected", text, detail, textBottom);
                drawn = true;
            }
        }
        // No frame to draw on (the guest's display was off): show the reason as the message instead.
        if (!drawn) ShowMessage("Disconnected", text, text, detail);
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
        PInvoke.SetWindowPos(_closeButton, HWND.Null, (rc.Width - bw) / 2, CloseButtonTop(rc, dpi), bw, bh,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>Below the centre, where the overlay's text ends; kept inside a small window (320x240 at 150 %).</summary>
    static int CloseButtonTop(RECT client, uint dpi) =>
        Math.Max(0, Math.Min(client.Height / 2 + (int)(56 * dpi / 96), client.Height - (int)(40 * dpi / 96)));

    /// <summary><see cref="DrawTextBlock"/> with nothing to keep clear but the edge of the shown area.</summary>
    void DrawText(byte[] pixels, int width, int height, SpiceRect area, string title, string body, string? detail = null) =>
        DrawTextBlock(pixels, width, height, area, title, body, detail, area.Bottom);

    /// <summary>
    /// Renders text into a top-down BGRA image (a surface or a message image) with GDI (on a DIB copy of the pixels),
    /// centered on the shown area: the title, the body under it, and the optional detail under that, smaller and dimmer.
    /// Each part gets its full wrapped height (finding R1: with DT_EDITCONTROL a capped rectangle drops the last line).
    /// The block sits just below the centre, and moves up as one when it would pass <paramref name="textBottom"/> (the
    /// Close button) or the area's bottom, but never above the area's top.
    /// </summary>
    void DrawTextBlock(byte[] pixels, int width, int height, SpiceRect area, string title, string body, string? detail, int textBottom)
    {
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        void* bits;
        var dib = PInvoke.CreateDIBSection(HDC.Null, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (dib.IsNull) return;
        var dc = PInvoke.CreateCompatibleDC(HDC.Null);
        var oldBmp = PInvoke.SelectObject(dc, dib);
        new Span<byte>(bits, pixels.Length).Clear();
        pixels.AsSpan().CopyTo(new Span<byte>(bits, pixels.Length));
        PInvoke.SetBkMode(dc, BACKGROUND_MODE.TRANSPARENT);
        PInvoke.SetTextColor(dc, new COLORREF(0x00FFFFFF));
        // Size text to the guest resolution so it reads well once scaled to the window.
        int ax = area.Left, ay = area.Top, aw = area.Width, ah = area.Height;
        var big = MakeFont(-Math.Max(18, ah / 22), 600);
        var small = MakeFont(-Math.Max(12, ah / 45), 400);
        var detailFont = detail is null ? default : MakeFont(-Math.Max(11, ah / 55), 400);
        const DRAW_TEXT_FORMAT Title = DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_NOPREFIX;
        const DRAW_TEXT_FORMAT Wrap = DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_WORDBREAK | DRAW_TEXT_FORMAT.DT_EDITCONTROL | DRAW_TEXT_FORMAT.DT_NOPREFIX;
        int textLeft = ax + aw / 10, textRight = ax + aw * 9 / 10;
        // Measure every part first, each in its own font.
        var oldFont = PInvoke.SelectObject(dc, big);
        int titleH = title.Length == 0 ? 0 : MeasureText(dc, title, ax, ax + aw, Title);
        PInvoke.SelectObject(dc, small);
        int bodyH = MeasureText(dc, body, textLeft, textRight, Wrap);
        int detailH = 0, gap = Math.Max(2, ah / 150);
        if (detail is not null) { PInvoke.SelectObject(dc, detailFont); detailH = MeasureText(dc, detail, textLeft, textRight, Wrap); }
        // The title ends just above the centre and the body starts just below it, as before; the block moves up as one
        // if its end would pass the Close button or the area's bottom, but not above the area's top.
        int titleTop = ay + ah / 2 - ah / 40 - titleH, bodyTop = ay + ah / 2 - ah / 50;
        int end = bodyTop + bodyH + (detail is null ? 0 : gap + detailH);
        int limit = Math.Min(textBottom, ay + ah) - Math.Max(2, ah / 100);
        int shift = Math.Max(0, Math.Min(end - limit, Math.Min(titleTop, bodyTop) - ay));
        titleTop -= shift; bodyTop -= shift;
        if (titleH > 0)
        {
            PInvoke.SelectObject(dc, big);
            var r1 = new RECT(ax, titleTop, ax + aw, titleTop + titleH);
            fixed (char* t = title) PInvoke.DrawText(dc, t, title.Length, ref r1, Title);
        }
        PInvoke.SelectObject(dc, small);
        var r2 = new RECT(textLeft, bodyTop, textRight, bodyTop + bodyH);
        fixed (char* t = body) PInvoke.DrawText(dc, t, body.Length, ref r2, Wrap);
        if (detail is not null)
        {
            PInvoke.SelectObject(dc, detailFont);
            PInvoke.SetTextColor(dc, new COLORREF(0x00A8A8A8));
            int top = bodyTop + bodyH + gap;
            var r3 = new RECT(textLeft, top, textRight, top + detailH);
            fixed (char* t = detail) PInvoke.DrawText(dc, t, detail.Length, ref r3, Wrap);
        }
        PInvoke.SelectObject(dc, oldFont);
        PInvoke.DeleteObject(big);
        PInvoke.DeleteObject(small);
        if (!detailFont.IsNull) PInvoke.DeleteObject(detailFont);
        new Span<byte>(bits, pixels.Length).CopyTo(pixels);
        var px = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
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
            window.Post(() => window.OnSessionLinked(s));
        }
        catch (Exception e) { window.Post(() => window.OnDisconnected(e)); }
    });
}
