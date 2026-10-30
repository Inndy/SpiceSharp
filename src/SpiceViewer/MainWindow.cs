using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Spice.Session;
using Spice.Wire;
using VvFile;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpiceViewer;

/// <summary>The viewer window: plain Win32, a flip-model swap chain once the guest display exists.</summary>
sealed unsafe partial class MainWindow
{
    static MainWindow? s_instance;
    const string ClassName = "SpiceViewer.Window";

    // System menu command ids (below 0xF000, multiples of 16).
    const uint IdCtrlAltDel = 0x100, IdWinL = 0x110, IdFullscreen = 0x120, IdRelease = 0x130,
        IdScaleFit = 0x140, IdScale1To1 = 0x150, IdKeysAlways = 0x160, IdKeysFullscreen = 0x170, IdKeysNever = 0x180,
        IdAbout = 0x190, IdResizeGuest = 0x1A0, IdServerCursor = 0x1B0,
        IdPacingSpacing = 0x1C0, IdPacingClock = 0x1D0, IdPacingOff = 0x1E0,
        IdFastJpeg = 0x1F0, IdCompressionAuto = 0x200, IdCompressionLz4 = 0x210,
        IdCodecAuto = 0x220, IdCodecServer = 0x230, IdCodecMjpeg = 0x240, IdCodecH264 = 0x250, IdCodecVp9 = 0x260, IdCodecH265 = 0x270;
    const int IdCloseButton = 1001;

    readonly VvConnection _vv;
    readonly ConcurrentQueue<Action> _uiQueue = new();
    readonly Cursors _cursors = new();
    HWND _hwnd;
    HWND _closeButton;
    HFONT _font, _bigFont;
    SpiceSession? _session;
    Presenter? _presenter;
    ViewState _state = ViewState.Connecting;
    string _status;
    /// <summary>A dimmer line under <see cref="_status"/> (a disconnect's detail), or null.</summary>
    string? _statusDetail;
    bool _gotFirstFrame;
    /// <summary>Every channel is linked (ConnectAsync returned): keys may go to the guest even before it has a display.</summary>
    bool _linked;
    // The guest's monitor area of the primary surface (docs/decisions/0035): the size everything in the window follows,
    // and its offset in the surface (cursor-channel positions are in surface pixels).
    int _guestW, _guestH, _guestX, _guestY;
    ScalingMode _scaling = ScalingMode.Fit;
    ShortcutMode _shortcuts = ShortcutMode.Always;
    bool _resizeGuest = true;
    bool _fullscreen;
    WINDOWPLACEMENT _savedPlacement;
    uint _savedStyle;
    HCURSOR _guestCursor;
    bool _guestCursorHidden;
    bool _sizingByUser;

    public MainWindow(VvConnection vv)
    {
        _vv = vv;
        _status = $"Connecting to {vv.DisplayTitle}…";
        s_instance = this;
    }

    public HWND Handle => _hwnd;
    public string Title => _vv.DisplayTitle;
    public bool IsFullscreen => _fullscreen;

    public void Create(SettingsStore settings)
    {
        var hinst = PInvoke.GetModuleHandle((PCWSTR)null);
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProcThunk,
                hInstance = hinst,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
                hIcon = (HICON)PInvoke.LoadImage(hinst, (PCWSTR)(char*)32512, GDI_IMAGE_TYPE.IMAGE_ICON, 0, 0, IMAGE_FLAGS.LR_DEFAULTSIZE | IMAGE_FLAGS.LR_SHARED).Value,
                lpszClassName = cls,
            };
            if (wc.hIcon.IsNull) wc.hIcon = PInvoke.LoadIcon(HINSTANCE.Null, PInvoke.IDI_APPLICATION);
            PInvoke.RegisterClassEx(wc);
        }

        var (r, maximized) = ApplySettings(settings);
        fixed (char* cls = ClassName)
        fixed (char* title = _vv.DisplayTitle)
        {
            _hwnd = PInvoke.CreateWindowEx(0, (PCWSTR)cls, (PCWSTR)title, WINDOW_STYLE.WS_OVERLAPPEDWINDOW | WINDOW_STYLE.WS_CLIPCHILDREN,
                r.left, r.top, r.Width, r.Height, HWND.Null, HMENU.Null, (HINSTANCE)hinst, null);
        }
        if (_hwnd.IsNull) throw new InvalidOperationException("CreateWindowEx failed");

        ApplyDarkTitleBar();
        CreateFonts();
        BuildSystemMenu();
        PInvoke.ShowWindow(_hwnd, maximized ? SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED : SHOW_WINDOW_CMD.SW_SHOWNORMAL);
        PInvoke.UpdateWindow(_hwnd);
        PInvoke.AddClipboardFormatListener(_hwnd);
        if (!_uiQueue.IsEmpty) PInvoke.PostMessage(_hwnd, Win32.WM_APP_SESSION, 0, 0);
    }

    void ApplyDarkTitleBar()
    {
        int on = 1;
        PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &on, sizeof(int));
        if (Win32.IsWindows11)
        {
            int backdrop = (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW;
            PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, &backdrop, sizeof(int));
        }
    }

    void CreateFonts()
    {
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        _font = MakeFont(-(int)(14 * dpi / 96), 400);
        _bigFont = MakeFont(-(int)(22 * dpi / 96), 600);
    }

    static HFONT MakeFont(int height, int weight)
    {
        var lf = new LOGFONTW { lfHeight = height, lfWeight = weight, lfQuality = FONT_QUALITY.CLEARTYPE_QUALITY };
        "Segoe UI".AsSpan().CopyTo(lf.lfFaceName.AsSpan());
        return PInvoke.CreateFontIndirect(&lf);
    }

    /// <summary>Runs the message loop until the window closes.</summary>
    public void Run()
    {
        MSG msg;
        while (PInvoke.GetMessage(out msg, HWND.Null, 0, 0))
        {
            // No TranslateMessage for the viewer: keys go to the guest as scancodes, never characters.
            if (msg.hwnd != _hwnd) PInvoke.TranslateMessage(msg);
            PInvoke.DispatchMessage(msg);
        }
    }

    /// <summary>Runs <paramref name="a"/> on the UI thread.</summary>
    public void Post(Action a)
    {
        _uiQueue.Enqueue(a);
        // Before the window exists, Create() flushes the queue.
        if (!_hwnd.IsNull) PInvoke.PostMessage(_hwnd, Win32.WM_APP_SESSION, 0, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT WndProcThunk(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        var self = s_instance;
        if (self is null) return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        if (self._hwnd.IsNull) self._hwnd = hwnd;
        try { return self.WndProc(msg, wParam, lParam); }
        catch (Exception e)
        {
            Log.Error($"window: unhandled exception in message {msg:x}: {e}");
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    LRESULT WndProc(uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case Win32.WM_APP_SESSION:
                while (_uiQueue.TryDequeue(out var a)) a();
                return default;
            case KeyboardHook.WM_APP_KEY:
                KeyboardHook.Drain(this);
                return default;
            case Presenter.WM_APP_PRESENTER:
                OnPresenterNotify(wParam.Value);
                return default;
            case PInvoke.WM_ERASEBKGND:
                return (LRESULT)1;
            case PInvoke.WM_PAINT:
                Paint();
                return default;
            case PInvoke.WM_SIZE:
                OnSize((uint)wParam.Value);
                return default;
            case PInvoke.WM_SIZING:
                OnSizing((uint)wParam.Value, (RECT*)lParam.Value);
                return (LRESULT)1;
            case PInvoke.WM_ENTERSIZEMOVE:
                _sizingByUser = true;
                return default;
            case PInvoke.WM_EXITSIZEMOVE:
                _sizingByUser = false;
                RequestGuestResize();
                RenderMessage(); // during the drag DXGI stretched the old message image
                return default;
            case PInvoke.WM_GETMINMAXINFO:
            {
                var mmi = (MINMAXINFO*)lParam.Value;
                mmi->ptMinTrackSize = new System.Drawing.Point(320, 240);
                return default;
            }
            case PInvoke.WM_DPICHANGED:
            {
                var r = (RECT*)lParam.Value;
                PInvoke.SetWindowPos(_hwnd, HWND.Null, r->left, r->top, r->Width, r->Height,
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                PInvoke.DeleteObject(_font); PInvoke.DeleteObject(_bigFont);
                CreateFonts();
                return default;
            }
            case PInvoke.WM_SYSCOMMAND:
                if (OnSysCommand((uint)wParam.Value)) return default;
                break;
            case PInvoke.WM_COMMAND:
                if ((wParam.Value & 0xFFFF) == IdCloseButton) { PInvoke.DestroyWindow(_hwnd); return default; }
                break;
            case PInvoke.WM_TIMER:
                OnTimer(wParam.Value);
                return default;
            case PInvoke.WM_SETCURSOR:
                if ((lParam.Value & 0xFFFF) == PInvoke.HTCLIENT && GuestView.PointerToGuest(_state))
                {
                    PInvoke.SetCursor(CurrentClientCursor());
                    return (LRESULT)1;
                }
                break;
            case PInvoke.WM_SETFOCUS:
                OnFocus(true);
                return default;
            case PInvoke.WM_KILLFOCUS:
                OnFocus(false);
                return default;
            case PInvoke.WM_ACTIVATE:
                if ((wParam.Value & 0xFFFF) != 0) _presenter?.InvalidateAll();
                break;
            case PInvoke.WM_CLIPBOARDUPDATE:
                OnHostClipboardChanged();
                return default;
            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(_hwnd);
                return default;
            case PInvoke.WM_DESTROY:
                OnDestroy();
                PInvoke.PostQuitMessage(0);
                return default;
        }
        if (HandleInput(msg, wParam, lParam, out var result)) return result;
        return PInvoke.DefWindowProc(_hwnd, msg, wParam, lParam);
    }

    // ---------------- painting ----------------

    void Paint()
    {
        var ps = new PAINTSTRUCT();
        var hdc = PInvoke.BeginPaint(_hwnd, out ps);
        if (_presenter is null || !_gotFirstFrame)
        {
            PInvoke.GetClientRect(_hwnd, out var rc);
            var brush = PInvoke.CreateSolidBrush(new COLORREF(0x00202020));
            PInvoke.FillRect(hdc, &rc, brush);
            PInvoke.DeleteObject(brush);
            PInvoke.SetBkMode(hdc, BACKGROUND_MODE.TRANSPARENT);
            var old = PInvoke.SelectObject(hdc, _font);
            PaintStatus(hdc, rc);
            PInvoke.SelectObject(hdc, old);
        }
        else _presenter.InvalidateAll();
        PInvoke.EndPaint(_hwnd, ps);
    }

    /// <summary>
    /// The status, word-wrapped and centred as a block within a margin, so a narrow window shows all of it (finding
    /// L13; a word too long for a line is broken as well). The detail line follows, dimmer. With the Close button
    /// shown, the block stays above it. Laid out on every paint; WM_SIZE invalidates (L10).
    /// </summary>
    void PaintStatus(HDC hdc, RECT client)
    {
        const DRAW_TEXT_FORMAT Wrap = DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_WORDBREAK | DRAW_TEXT_FORMAT.DT_EDITCONTROL | DRAW_TEXT_FORMAT.DT_NOPREFIX;
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        int margin = (int)(16 * dpi / 96), gap = (int)(6 * dpi / 96);
        int left = client.left + margin, right = Math.Max(left + 1, client.right - margin);
        int top = client.top + margin, bottom = client.bottom - margin;
        if (!_closeButton.IsNull) bottom = Math.Min(bottom, CloseButtonTop(client, dpi) - 2 * gap);
        int h1 = MeasureText(hdc, _status, left, right, Wrap);
        int h2 = _statusDetail is null ? 0 : MeasureText(hdc, _statusDetail, left, right, Wrap);
        int total = h1 + (_statusDetail is null ? 0 : gap + h2);
        // Centred on the client area, as the single line was; moved up if it would reach the Close button.
        int y = Math.Max(top, Math.Min((client.top + client.bottom - total) / 2, bottom - total));
        PInvoke.SetTextColor(hdc, new COLORREF(0x00E0E0E0));
        var r1 = new RECT(left, y, right, y + h1);
        fixed (char* s = _status) PInvoke.DrawText(hdc, s, _status.Length, ref r1, Wrap);
        if (_statusDetail is null) return;
        PInvoke.SetTextColor(hdc, new COLORREF(0x00A0A0A0));
        var r2 = new RECT(left, y + h1 + gap, right, y + h1 + gap + h2);
        fixed (char* s = _statusDetail) PInvoke.DrawText(hdc, s, _statusDetail.Length, ref r2, Wrap);
    }

    /// <summary>The height <paramref name="text"/> takes when wrapped between <paramref name="left"/> and <paramref name="right"/>.</summary>
    static int MeasureText(HDC hdc, string text, int left, int right, DRAW_TEXT_FORMAT format)
    {
        var r = new RECT(left, 0, right, 0);
        fixed (char* s = text) PInvoke.DrawText(hdc, s, text.Length, ref r, format | DRAW_TEXT_FORMAT.DT_CALCRECT);
        return r.Height;
    }

    /// <summary>Shows <paramref name="status"/> (and a dimmer <paramref name="detail"/> line) while nothing else is drawn.</summary>
    void SetStatus(string status, string? detail = null)
    {
        _status = status;
        _statusDetail = detail;
        PInvoke.InvalidateRect(_hwnd, (RECT?)null, false);
    }

    // ---------------- sizing ----------------

    RECT ClientRect() { PInvoke.GetClientRect(_hwnd, out var rc); return rc; }

    /// <summary>1:1 pixels: chosen by the user, or forced by the software (WARP) profile.</summary>
    bool OneToOneActive => _scaling == ScalingMode.OneToOne || _presenter?.IsSoftware == true;

    /// <summary>
    /// Where the guest image is drawn, in client pixels: the single source of truth for input and cursor mapping.
    /// Same <see cref="Viewport"/> computation as the presenter: the whole client area on the direct path (aspect-locked
    /// Fit, or 1:1 at exactly the guest size), the letterbox destination otherwise (uniformly scaled in Fit, unscaled
    /// and centered or cropped in 1:1). Before the first frame it is the client rectangle.
    /// </summary>
    internal RECT GuestRect()
    {
        var rc = ClientRect();
        if (_guestW <= 0 || _guestH <= 0 || rc.Width <= 0 || rc.Height <= 0) return rc;
        var vp = Viewport.Compute(rc.Width, rc.Height, _guestW, _guestH, OneToOneActive, _presenter?.MaxDimension ?? Viewport.DefaultMaxDimension);
        return vp.GuestRectInClient(rc.Width, rc.Height, _guestW, _guestH);
    }

    /// <summary>Tells the presenter the client size and effective scaling mode.</summary>
    void PushLayout()
    {
        if (_presenter is null || PInvoke.IsIconic(_hwnd)) return;
        var rc = ClientRect();
        _presenter.SetLayout(rc.Width, rc.Height, OneToOneActive);
    }

    void SetScaling(ScalingMode mode)
    {
        _scaling = mode;
        UpdateMenuChecks();
        if (OneToOneActive) SizeWindowToGuest(false);
        PushLayout();
    }

    /// <summary>Presenter worker notifications (posted, so they arrive on this thread).</summary>
    void OnPresenterNotify(nuint code)
    {
        var p = _presenter;
        if (p is null) return;
        if (code == Presenter.NotifyFailed)
        {
            if (!p.HasFailed) return; // from an earlier presenter
            _presenter = null;
            p.Shutdown(2000); // the worker is already exiting and releases its own objects
            Log.Error("presenter: stopped after a graphics failure");
            // Keys and clicks must not keep going to a guest the user can't see: leaving Connected stops all
            // forwarding (and the hook, via WantHook); let go of everything already held in the guest.
            // A new presenter is tried when the guest display changes next.
            // DisplayOff (and Waiting) too: since decision 0035 the presenter outlives a blanked display to show the
            // message, and keys must stop there as well (review M3). A disconnected window stays disconnected.
            if (_state != ViewState.Disconnected) _state = ViewState.DisplayFailed;
            _messageTitle = _messageBody = _messageDetail = null; // the failure text below replaces any display-off message
            CancelGuestInput();
            UpdateKeyboardCapture();
            if (_fullscreen) { _fullscreenWhenEnded = true; ToggleFullscreen(); }
            SetStatus("The display stopped working because the graphics device failed. Close this window and open the console again.");
            UpdateMenuChecks();
            return;
        }
        // Device created or recreated: the software profile may have changed.
        UpdateMenuChecks();
        PushLayout();
        p.InvalidateAll();
    }

    /// <summary>Window rect for a client area of w×h, clamped to the work area (keeping the aspect ratio).</summary>
    RECT WindowRectForClient(int w, int h, bool center)
    {
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        var style = (WINDOW_STYLE)(uint)GetWindowLong(WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var mon = PInvoke.MonitorFromWindow(_hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(mon, ref mi);
        var work = mi.rcWork;

        var probe = new RECT(0, 0, 100, 100);
        PInvoke.AdjustWindowRectExForDpi(ref probe, style, false, 0, dpi);
        int ncW = probe.Width - 100, ncH = probe.Height - 100;
        double scale = Math.Min(1.0, Math.Min((double)(work.Width - ncW) / w, (double)(work.Height - ncH) / h));
        int cw = (int)Math.Round(w * scale), ch = (int)Math.Round(h * scale);

        PInvoke.GetWindowRect(_hwnd, out var cur);
        int x = center ? work.left + (work.Width - (cw + ncW)) / 2 : cur.left;
        int y = center ? work.top + (work.Height - (ch + ncH)) / 2 : cur.top;
        // Keep the window on screen.
        x = Math.Clamp(x, work.left, Math.Max(work.left, work.right - (cw + ncW)));
        y = Math.Clamp(y, work.top, Math.Max(work.top, work.bottom - (ch + ncH)));
        return new RECT(x, y, x + cw + ncW, y + ch + ncH);
    }

    void SizeWindowToGuest(bool center)
    {
        if (_fullscreen || PInvoke.IsZoomed(_hwnd) || _guestW == 0) return;
        var r = WindowRectForClient(_guestW, _guestH, center);
        PInvoke.SetWindowPos(_hwnd, HWND.Null, r.left, r.top, r.Width, r.Height, SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    bool GuestFollowsWindow => _resizeGuest && _session is { AgentSupportsMonitorsConfig: true };

    void OnSizing(uint edge, RECT* r)
    {
        if (_guestW == 0 || GuestFollowsWindow) return;
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        var style = (WINDOW_STYLE)(uint)GetWindowLong(WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var probe = new RECT(0, 0, 100, 100);
        PInvoke.AdjustWindowRectExForDpi(ref probe, style, false, 0, dpi);
        int ncW = probe.Width - 100, ncH = probe.Height - 100;
        if (OneToOneActive)
        {
            // 1:1 locks the client area to the guest size.
            r->right = r->left + _guestW + ncW;
            r->bottom = r->top + _guestH + ncH;
            return;
        }
        // Fit: lock the aspect ratio to the guest's.
        int cw = r->Width - ncW, ch = r->Height - ncH;
        double aspect = (double)_guestW / _guestH;
        bool horizontal = edge is PInvoke.WMSZ_LEFT or PInvoke.WMSZ_RIGHT;
        bool vertical = edge is PInvoke.WMSZ_TOP or PInvoke.WMSZ_BOTTOM;
        if (horizontal || (!vertical && (double)cw / ch > aspect)) ch = (int)Math.Round(cw / aspect);
        else cw = (int)Math.Round(ch * aspect);
        if (edge is PInvoke.WMSZ_LEFT or PInvoke.WMSZ_TOPLEFT or PInvoke.WMSZ_BOTTOMLEFT) r->left = r->right - cw - ncW;
        else r->right = r->left + cw + ncW;
        if (edge is PInvoke.WMSZ_TOP or PInvoke.WMSZ_TOPLEFT or PInvoke.WMSZ_TOPRIGHT) r->top = r->bottom - ch - ncH;
        else r->bottom = r->top + ch + ncH;
    }

    void OnSize(uint kind)
    {
        bool minimized = kind == PInvoke.SIZE_MINIMIZED;
        _presenter?.SetSuspended(minimized);
        if (minimized)
        {
            // Nobody is watching: give back what the busy period left (docs/decisions/0041).
            _session?.TrimMemory("minimized", force: true);
            return;
        }
        // The GDI-painted status (no presenter: "Connecting…", waiting, a failed display) is centred on the client
        // area: lay it out again (finding L10). With a presenter nothing is painted with GDI, so nothing is invalidated.
        if (_presenter is null) PInvoke.InvalidateRect(_hwnd, (RECT?)null, false);
        PushLayout();
        PositionCloseButton();
        UpdateCursorClip();
        // Maximize/restore/snap don't go through WM_EXITSIZEMOVE.
        if (!_sizingByUser) { RequestGuestResize(); RenderMessage(); }
        else if (GuestFollowsWindow) PInvoke.SetTimer(_hwnd, TimerGuestResize, 300, null);
    }

    const nuint TimerGuestResize = 1, TimerClipboard = 2;

    void OnTimer(nuint id)
    {
        PInvoke.KillTimer(_hwnd, id);
        if (id == TimerGuestResize) RequestGuestResize();
    }

    int _lastRequestW, _lastRequestH;

    /// <summary>
    /// With the agent, the guest takes the window's size so output stays 1:1. Compared with the guest's monitor area, not
    /// its surface: a qxl surface can be taller than the mode it holds (docs/decisions/0035).
    /// </summary>
    void RequestGuestResize()
    {
        if (!GuestFollowsWindow || _state != ViewState.Connected || PInvoke.IsIconic(_hwnd)) return;
        var rc = ClientRect();
        int w = rc.Width & ~7, h = rc.Height;
        if (w == _lastRequestW && h == _lastRequestH) return;
        if (w == _guestW && h == _guestH) return;
        if (_session!.RequestGuestResolution(w, h)) { _lastRequestW = w; _lastRequestH = h; Log.Info($"agent: requested guest resolution {w}x{h}"); }
    }

    // ---------------- full screen ----------------

    public void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _savedPlacement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            PInvoke.GetWindowPlacement(_hwnd, ref _savedPlacement);
            _savedStyle = (uint)GetWindowLong(WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            var mon = PInvoke.MonitorFromWindow(_hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            PInvoke.GetMonitorInfo(mon, ref mi);
            SetWindowLong(WINDOW_LONG_PTR_INDEX.GWL_STYLE, unchecked((nint)(uint)(WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_CLIPCHILDREN)));
            var m = mi.rcMonitor;
            _fullscreen = true;
            PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOP, m.left, m.top, m.Width, m.Height,
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }
        else
        {
            _fullscreen = false;
            SetWindowLong(WINDOW_LONG_PTR_INDEX.GWL_STYLE, (nint)_savedStyle);
            PInvoke.SetWindowPlacement(_hwnd, _savedPlacement);
            PInvoke.SetWindowPos(_hwnd, HWND.Null, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }
        FullscreenStrip.Update(this);
        UpdateMenuChecks();
        UpdateKeyboardCapture();
        RequestGuestResize();
    }

    // ---------------- system menu ----------------

    HMENU _sysMenu;

    void BuildSystemMenu()
    {
        _sysMenu = PInvoke.GetSystemMenu(_hwnd, false);
        var scaling = PInvoke.CreatePopupMenu();
        AppendItem(scaling, IdScaleFit, "Fit to window");
        AppendItem(scaling, IdScale1To1, "1:1 pixels");
        AppendItem(scaling, 0, null);
        AppendItem(scaling, IdResizeGuest, "Resize guest with window");
        var keys = PInvoke.CreatePopupMenu();
        AppendItem(keys, IdKeysAlways, "Always");
        AppendItem(keys, IdKeysFullscreen, "In full screen only");
        AppendItem(keys, IdKeysNever, "Never");
        var pacing = PInvoke.CreatePopupMenu();
        AppendItem(pacing, IdPacingSpacing, "Smooth bursts (no added delay)");
        AppendItem(pacing, IdPacingClock, "Follow guest clock (adds delay)");
        AppendItem(pacing, IdPacingOff, "Off (draw on arrival)");
        var compression = PInvoke.CreatePopupMenu();
        AppendItem(compression, IdCompressionAuto, "Server default");
        AppendItem(compression, IdCompressionLz4, "LZ4 (fast, more bandwidth)");
        var codec = PInvoke.CreatePopupMenu();
        AppendItem(codec, IdCodecAuto, "Automatic (hardware decoding first)");
        AppendItem(codec, IdCodecServer, "Server default");
        AppendItem(codec, IdCodecMjpeg, "MJPEG");
        AppendItem(codec, IdCodecH264, "H.264");
        AppendItem(codec, IdCodecVp9, "VP9");
        AppendItem(codec, IdCodecH265, "HEVC");

        AppendItem(_sysMenu, 0, null);
        AppendItem(_sysMenu, IdCtrlAltDel, "Send Ctrl+Alt+Del");
        AppendItem(_sysMenu, IdWinL, "Send Win+L");
        AppendItem(_sysMenu, 0, null);
        AppendItem(_sysMenu, IdFullscreen, $"Full screen\t{_vv.ToggleFullscreen}");
        AppendItem(_sysMenu, IdRelease, $"Release keyboard and mouse\t{_vv.ReleaseCursor}");
        AppendPopup(_sysMenu, scaling, "Scaling");
        AppendPopup(_sysMenu, keys, "System shortcuts");
        AppendItem(_sysMenu, IdServerCursor, "Show guest cursor in server mouse mode");
        AppendPopup(_sysMenu, pacing, "Video pacing");
        AppendItem(_sysMenu, IdFastJpeg, "Fast JPEG decoding (slightly lower quality)");
        AppendPopup(_sysMenu, compression, "Image compression");
        AppendPopup(_sysMenu, codec, "Video codec");
        AppendItem(_sysMenu, 0, null);
        AppendItem(_sysMenu, IdAbout, "About SpiceViewer…");
        _scalingMenu = scaling;
        _keysMenu = keys;
        _pacingMenu = pacing;
        _compressionMenu = compression;
        _codecMenu = codec;
        UpdateMenuChecks();
    }

    HMENU _scalingMenu, _keysMenu, _pacingMenu, _compressionMenu, _codecMenu;

    static void AppendItem(HMENU m, uint id, string? text)
    {
        if (text is null) { PInvoke.AppendMenu(m, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null); return; }
        fixed (char* t = text) PInvoke.AppendMenu(m, MENU_ITEM_FLAGS.MF_STRING, id, t);
    }

    static void AppendPopup(HMENU m, HMENU sub, string text)
    {
        fixed (char* t = text) PInvoke.AppendMenu(m, MENU_ITEM_FLAGS.MF_POPUP, (nuint)sub.Value, t);
    }

    void UpdateMenuChecks()
    {
        if (_sysMenu.IsNull) return;
        PInvoke.CheckMenuItem(_sysMenu, IdFullscreen, (uint)(_fullscreen ? MENU_ITEM_FLAGS.MF_CHECKED : MENU_ITEM_FLAGS.MF_UNCHECKED));
        PInvoke.CheckMenuRadioItem(_scalingMenu, IdScaleFit, IdScale1To1, OneToOneActive ? IdScale1To1 : IdScaleFit, (uint)MENU_ITEM_FLAGS.MF_BYCOMMAND);
        // Software profile: 1:1 only.
        PInvoke.EnableMenuItem(_scalingMenu, IdScaleFit, MENU_ITEM_FLAGS.MF_BYCOMMAND | (_presenter?.IsSoftware == true ? MENU_ITEM_FLAGS.MF_GRAYED : MENU_ITEM_FLAGS.MF_ENABLED));
        PInvoke.CheckMenuItem(_scalingMenu, IdResizeGuest, (uint)(_resizeGuest ? MENU_ITEM_FLAGS.MF_CHECKED : MENU_ITEM_FLAGS.MF_UNCHECKED));
        uint k = _shortcuts switch { ShortcutMode.Always => IdKeysAlways, ShortcutMode.FullscreenOnly => IdKeysFullscreen, _ => IdKeysNever };
        PInvoke.CheckMenuRadioItem(_keysMenu, IdKeysAlways, IdKeysNever, k, (uint)MENU_ITEM_FLAGS.MF_BYCOMMAND);
        PInvoke.CheckMenuItem(_sysMenu, IdServerCursor, (uint)(_serverCursor == ServerCursorMode.Guest ? MENU_ITEM_FLAGS.MF_CHECKED : MENU_ITEM_FLAGS.MF_UNCHECKED));
        uint p = _videoPacing switch { VideoPacingMode.Spacing => IdPacingSpacing, VideoPacingMode.Clock => IdPacingClock, _ => IdPacingOff };
        PInvoke.CheckMenuRadioItem(_pacingMenu, IdPacingSpacing, IdPacingOff, p, (uint)MENU_ITEM_FLAGS.MF_BYCOMMAND);
        PInvoke.CheckMenuItem(_sysMenu, IdFastJpeg, (uint)(_fastJpeg ? MENU_ITEM_FLAGS.MF_CHECKED : MENU_ITEM_FLAGS.MF_UNCHECKED));
        uint c = _imageCompression == ImageCompressionMode.Lz4 ? IdCompressionLz4 : IdCompressionAuto;
        PInvoke.CheckMenuRadioItem(_compressionMenu, IdCompressionAuto, IdCompressionLz4, c, (uint)MENU_ITEM_FLAGS.MF_BYCOMMAND);
        uint v = _videoCodec switch
        {
            VideoCodecMode.Server => IdCodecServer, VideoCodecMode.Mjpeg => IdCodecMjpeg, VideoCodecMode.H264 => IdCodecH264,
            VideoCodecMode.Vp9 => IdCodecVp9, VideoCodecMode.H265 => IdCodecH265, _ => IdCodecAuto,
        };
        PInvoke.CheckMenuRadioItem(_codecMenu, IdCodecAuto, IdCodecH265, v, (uint)MENU_ITEM_FLAGS.MF_BYCOMMAND);
        // A codec without a hardware decoder here can't be chosen (until the probe answers, none can).
        var decodable = DecodableVideoCodecs();
        foreach (var (id, codecType) in (ReadOnlySpan<(uint, VideoCodecType)>)[(IdCodecH264, VideoCodecType.H264), (IdCodecVp9, VideoCodecType.Vp9), (IdCodecH265, VideoCodecType.H265)])
            PInvoke.EnableMenuItem(_codecMenu, id, MENU_ITEM_FLAGS.MF_BYCOMMAND | (decodable.Contains(codecType) ? MENU_ITEM_FLAGS.MF_ENABLED : MENU_ITEM_FLAGS.MF_GRAYED));
    }

    public bool OnSysCommand(uint cmd)
    {
        switch (cmd)
        {
            case IdCtrlAltDel: _session?.SendCtrlAltDel(); return true;
            case IdWinL: _session?.SendWinL(); return true;
            case IdFullscreen: ToggleFullscreen(); return true;
            case IdRelease: ReleaseInput(); return true;
            case IdScaleFit: SetScaling(ScalingMode.Fit); SaveScaling(); return true;
            case IdScale1To1: SetScaling(ScalingMode.OneToOne); SaveScaling(); return true;
            case IdResizeGuest:
            {
                bool on = _resizeGuest = !_resizeGuest;
                UpdateMenuChecks();
                RequestGuestResize();
                SaveSetting(s => s.ResizeGuest = on);
                return true;
            }
            case IdKeysAlways: SetShortcuts(ShortcutMode.Always); return true;
            case IdKeysFullscreen: SetShortcuts(ShortcutMode.FullscreenOnly); return true;
            case IdKeysNever: SetShortcuts(ShortcutMode.Never); return true;
            case IdServerCursor: SetServerCursor(_serverCursor == ServerCursorMode.Guest ? ServerCursorMode.Hidden : ServerCursorMode.Guest); return true;
            case IdPacingSpacing: SetVideoPacing(VideoPacingMode.Spacing); return true;
            case IdPacingClock: SetVideoPacing(VideoPacingMode.Clock); return true;
            case IdPacingOff: SetVideoPacing(VideoPacingMode.Off); return true;
            case IdFastJpeg: SetFastJpeg(!_fastJpeg); return true;
            case IdCompressionAuto: SetImageCompression(ImageCompressionMode.Auto); return true;
            case IdCompressionLz4: SetImageCompression(ImageCompressionMode.Lz4); return true;
            case IdCodecAuto: SetVideoCodec(VideoCodecMode.Auto); return true;
            case IdCodecServer: SetVideoCodec(VideoCodecMode.Server); return true;
            case IdCodecMjpeg: SetVideoCodec(VideoCodecMode.Mjpeg); return true;
            case IdCodecH264: SetVideoCodec(VideoCodecMode.H264); return true;
            case IdCodecVp9: SetVideoCodec(VideoCodecMode.Vp9); return true;
            case IdCodecH265: SetVideoCodec(VideoCodecMode.H265); return true;
            case IdAbout: ShowAbout(); return true;
        }
        return false;
    }

    void SetShortcuts(ShortcutMode mode)
    {
        _shortcuts = mode;
        UpdateMenuChecks();
        UpdateKeyboardCapture();
        SaveSetting(s => s.SystemShortcuts = mode);
    }

    void ShowAbout()
    {
        var p = _presenter;
        Dialogs.Info(_hwnd, "About SpiceViewer", $"SpiceViewer {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}",
            "A native Windows SPICE client for Proxmox .vv files. MIT License.\n\n" +
            "Includes libjpeg-turbo (IJG and BSD-3-Clause licenses), libopus (BSD-3-Clause), and SPICE QUIC/LZ/GLZ " +
            "decoders from spice-common and spice-gtk (LGPL-2.1-or-later, in the replaceable spicecodec.dll). " +
            "Architecture after SpiceSee (MIT).\n\n" +
            $"Renderer: {p?.AdapterName ?? "-"}{(p?.IsSoftware == true ? " (software)" : "")}\n" +
            $"Video decoding: MJPEG (libjpeg-turbo){string.Concat(DecodableVideoCodecs().Select(c => $", {c} (hardware)"))}\n" +
            $"Session: {(_session?.AgentConnected == true ? "guest agent connected" : "no guest agent")}, " +
            $"mouse {(_session?.MouseMode == MouseMode.Client ? "client" : "server")} mode");
    }

    // ---------------- helpers ----------------

    nint GetWindowLong(WINDOW_LONG_PTR_INDEX index) => GetWindowLongPtrW((nint)_hwnd.Value, (int)index);
    void SetWindowLong(WINDOW_LONG_PTR_INDEX index, nint value) => SetWindowLongPtrW((nint)_hwnd.Value, (int)index, value);

    // CsWin32 doesn't generate these (x64-only exports); the draft requires LibraryImport for hand-written bindings.
    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll")]
    private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    void OnDestroy()
    {
        // First, while the window still has its placement; written in the background alongside the rest of shutdown.
        var save = SaveWindowState();
        PInvoke.RemoveClipboardFormatListener(_hwnd);
        UninstallCapture();
        FullscreenStrip.Destroy();
        // Stop the presenter while the window still exists. It releases its own D3D/DXGI objects on its thread;
        // the wait dispatches sent messages so a Present waiting on this thread can finish. Never blocks forever.
        var presenter = _presenter;
        _presenter = null;
        presenter?.Shutdown(2000);
        _cursors.Dispose();
        var s = _session;
        _session = null;
        if (s is not null)
        {
            // Don't hang shutdown on a slow network close.
            Task.Run(() => s.DisposeAsync().AsTask()).Wait(1500);
        }
        if (save is not null && !save.Wait(1000)) Log.Warn("settings: still saving at exit; the previous settings.json stays in place");
    }
}
