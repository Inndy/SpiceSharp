using Spice.Wire;
using VvFile;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpiceViewer;

sealed unsafe partial class MainWindow
{
    /// <summary>Input is released (release-cursor hotkey or menu): keys go to Windows until the next click.</summary>
    bool _released;
    bool _focused;
    bool _pointerCaptured;   // server mouse mode: pointer confined, relative motion via raw input; shows the guest cursor or none
    int _wheelAccum;
    readonly HashSet<ushort> _localDown = [];
    KeyboardHook? _hook;

    bool ServerMouse => _session is not null && _session.MouseMode != MouseMode.Client;

    /// <summary>Whether system shortcuts (Alt+Tab, Win…) should be captured by the low-level hook right now.</summary>
    /// <remarks>
    /// Only while the guest display is shown: with the display off the window shows a message, and Alt+Tab and the Win
    /// key stay with Windows (plain keys still reach the guest through WM_KEYDOWN, docs/decisions/0035).
    /// </remarks>
    bool WantHook => _focused && !_released && _state == ViewState.Connected && _shortcuts switch
    {
        ShortcutMode.Always => true,
        ShortcutMode.FullscreenOnly => _fullscreen,
        _ => false,
    };

    void OnFocus(bool focused)
    {
        _focused = focused;
        // Nothing may stay pressed in the guest when we lose the keyboard.
        if (!focused) CancelGuestInput();
        UpdateKeyboardCapture();
    }

    /// <summary>
    /// The one place that lets go of everything: guest keys and buttons, local key state, queued hook events,
    /// mouse capture, raw input and the cursor clip. Sends never block (SpiceChannel queues them), so this
    /// always completes even if the server has stopped reading.
    /// </summary>
    void CancelGuestInput()
    {
        _session?.ReleaseAllKeys();
        _session?.ReleaseAllButtons();
        _localDown.Clear();
        _modKeys.Clear();
        _physDown.Clear();
        // A hotkey released while we didn't have focus never sends us its key-up; don't let it swallow a later press.
        _hotkeyKeys.Clear();
        KeyboardHook.Clear();
        if (PInvoke.GetCapture() == _hwnd) PInvoke.ReleaseCapture();
        ReleasePointer();
    }

    void UpdateKeyboardCapture()
    {
        if (WantHook)
        {
            _hook ??= new KeyboardHook(this);
            _hook.Install();
        }
        else _hook?.Uninstall();
    }

    void UninstallCapture()
    {
        CancelGuestInput();
        _hook?.Dispose();
        _hook = null;
    }

    /// <summary>The release-cursor hotkey: give keyboard and pointer back to Windows.</summary>
    void ReleaseInput()
    {
        _released = true;
        CancelGuestInput();
        UpdateKeyboardCapture();
        PInvoke.SetWindowText(_hwnd, $"{_vv.DisplayTitle} — input released (click to capture)");
    }

    void Recapture()
    {
        if (!_released) return;
        _released = false;
        PInvoke.SetWindowText(_hwnd, _vv.DisplayTitle);
        UpdateKeyboardCapture();
    }

    // ---------------- keyboard ----------------

    /// <summary>
    /// One key event from the hook or from WM_KEYDOWN/UP. <paramref name="scancode"/> is XT set 1 (0xE0xx = extended).
    /// Returns true if the event was consumed (sent to the guest or used as a hotkey).
    /// </summary>
    public bool OnKey(ushort vk, ushort scancode, bool down)
    {
        // Events queued by the hook before a focus loss arrive after CancelGuestInput: drop them, or they would
        // press keys in the guest whose key-up then goes to another application.
        if (!_focused) return false;
        // Injected input (on-screen keyboards, automation, some remote tools) often carries no scancode.
        if (scancode == 0 && vk != 0)
        {
            uint sc = PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC_EX);
            scancode = (ushort)((sc & 0xFF00) == 0xE000 ? 0xE000 | (sc & 0xFF) : sc & 0xFF);
        }
        if (IsModifierScan(scancode)) { if (down) _modKeys.Add(scancode); else _modKeys.Remove(scancode); }
        // Auto-repeat is a down for a key that is already down; hotkeys fire once per physical press.
        bool repeat = down && !_physDown.Add(vk);
        if (!down) _physDown.Remove(vk);
        // Keys go to the guest once the session is linked, even before it has a display: that is how a guest whose
        // display is off is woken (docs/decisions/0035).
        if (_session is null || !GuestView.KeysToGuest(_state, _linked)) return false;
        if (down && !repeat && CheckHotkeys(vk)) { _hotkeyKeys.Add(vk); return true; }
        if (_hotkeyKeys.Contains(vk)) { if (!down) _hotkeyKeys.Remove(vk); return true; } // repeats and release of a hotkey
        if (_released) return false;
        // Windows reports NumLock as extended and Pause as Ctrl+NumLock; fix both up for XT.
        if (vk == (ushort)VIRTUAL_KEY.VK_NUMLOCK) scancode = 0x45;
        if (vk == (ushort)VIRTUAL_KEY.VK_PAUSE)
        {
            if (down && !repeat) _session.SendPause();
            return true;
        }
        if (scancode == 0) return false;
        if (down) _localDown.Add(scancode); else _localDown.Remove(scancode);
        _session.KeyEvent(scancode, down);
        return true;
    }

    // Modifiers are tracked from the key events themselves: keys swallowed by the low-level hook
    // never update GetAsyncKeyState.
    readonly HashSet<ushort> _modKeys = [];
    // Physically held keys (by VK), to tell auto-repeat from a new press, and keys whose press was a hotkey.
    readonly HashSet<ushort> _physDown = [];
    readonly HashSet<ushort> _hotkeyKeys = [];

    static bool IsModifierScan(ushort sc) => sc is 0x1D or 0xE01D or 0x38 or 0xE038 or 0x2A or 0x36 or 0xE05B or 0xE05C;

    HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        foreach (var sc in _modKeys)
            m |= sc switch
            {
                0x1D or 0xE01D => HotkeyModifiers.Ctrl,
                0x38 or 0xE038 => HotkeyModifiers.Alt,
                0x2A or 0x36 => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.Win,
            };
        return m;
    }

    bool Matches(Hotkey hk, ushort vk, HotkeyModifiers mods)
    {
        if (hk.VirtualKey == 0)
        {
            // Modifier-only combo (e.g. "Ctrl+Alt" for release-cursor): fires when the set is complete.
            bool isModifier = vk is (ushort)VIRTUAL_KEY.VK_CONTROL or (ushort)VIRTUAL_KEY.VK_LCONTROL or (ushort)VIRTUAL_KEY.VK_RCONTROL
                or (ushort)VIRTUAL_KEY.VK_MENU or (ushort)VIRTUAL_KEY.VK_LMENU or (ushort)VIRTUAL_KEY.VK_RMENU
                or (ushort)VIRTUAL_KEY.VK_SHIFT or (ushort)VIRTUAL_KEY.VK_LSHIFT or (ushort)VIRTUAL_KEY.VK_RSHIFT;
            return isModifier && mods == hk.Modifiers;
        }
        return vk == hk.VirtualKey && mods == hk.Modifiers;
    }

    bool CheckHotkeys(ushort vk)
    {
        var mods = CurrentModifiers();
        if (Matches(_vv.ReleaseCursor, vk, mods))
        {
            if (_released) Recapture(); else ReleaseInput();
            return true;
        }
        if (Matches(_vv.ToggleFullscreen, vk, mods))
        {
            // Let go of the modifiers in the guest; the hotkey itself never reaches it.
            _session?.ReleaseAllKeys();
            Post(ToggleFullscreen);
            return true;
        }
        if (Matches(_vv.SecureAttention, vk, mods))
        {
            _session?.ReleaseAllKeys();
            _session?.SendCtrlAltDel();
            return true;
        }
        return false;
    }

    // ---------------- mouse ----------------

    /// <summary>A client point as a point in the guest's monitor area (client mouse mode).</summary>
    (int X, int Y) ToGuest(int cx, int cy)
    {
        var g = GuestRect();
        return GuestView.ClientToGuest(cx, cy, g.left, g.top, g.Width, g.Height, _guestW, _guestH);
    }

    HCURSOR CurrentClientCursor()
    {
        if (_guestCursorHidden) return HCURSOR.Null;
        // The window was resized (or changed DPI/fullscreen) since the cursor was built: rebuild at the new scale.
        if (_guestShape is not null && CursorScale() != _guestCursorScale) RebuildGuestCursor();
        // Captured (server mode): only a cursor-channel shape can follow the guest; a guest that draws its own
        // cursor into the framebuffer gets no second pointer. With the setting off, the pointer is simply hidden.
        if (_pointerCaptured) return _serverCursor == ServerCursorMode.Guest ? _guestCursor : HCURSOR.Null;
        return _guestCursor.IsNull ? PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW) : _guestCursor;
    }

    void CapturePointer()
    {
        if (_pointerCaptured || !ServerMouse) return;
        var rid = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0, hwndTarget = _hwnd };
        PInvoke.RegisterRawInputDevices([rid], (uint)sizeof(RAWINPUTDEVICE));
        _pointerCaptured = true;
        UpdateCursorClip();
        PInvoke.SetCursor(CurrentClientCursor());
        ApplyGuestCursorPos();
    }

    void ReleasePointer()
    {
        if (!_pointerCaptured) return;
        _pointerCaptured = false;
        var rid = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE, hwndTarget = HWND.Null };
        PInvoke.RegisterRawInputDevices([rid], (uint)sizeof(RAWINPUTDEVICE));
        PInvoke.ClipCursor((RECT?)null);
    }

    /// <summary>GuestRect() cut to the client area (in cropped 1:1 the guest extends past it).</summary>
    RECT VisibleGuestRect()
    {
        var g = GuestRect();
        var c = ClientRect();
        var r = new RECT(Math.Max(g.left, c.left), Math.Max(g.top, c.top), Math.Min(g.right, c.right), Math.Min(g.bottom, c.bottom));
        return r.right > r.left && r.bottom > r.top ? r : c;
    }

    void UpdateCursorClip()
    {
        if (!_pointerCaptured) return;
        // Only the visible guest image: not the letterbox bars, and not the cropped-away part past the client edge.
        var rc = VisibleGuestRect();
        var tl = new System.Drawing.Point(rc.left, rc.top);
        var br = new System.Drawing.Point(rc.right, rc.bottom);
        PInvoke.ClientToScreen(_hwnd, ref tl);
        PInvoke.ClientToScreen(_hwnd, ref br);
        PInvoke.ClipCursor(new RECT(tl.X, tl.Y, br.X, br.Y));
    }

    bool HandleInput(uint msg, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = default;
        switch (msg)
        {
            case PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN or PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP:
            {
                bool down = msg is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
                ushort sc = (ushort)((lParam.Value >> 16) & 0xFF);
                if (((lParam.Value >> 24) & 1) != 0) sc |= 0xE000;
                if (OnKey((ushort)wParam.Value, sc, down)) return true;
                return false;
            }
            case PInvoke.WM_SYSCHAR or PInvoke.WM_CHAR:
                // Alt+key would beep (menu mnemonic search); swallow while the guest owns the keyboard.
                return !_released && _session is not null && GuestView.KeysToGuest(_state, _linked);

            case PInvoke.WM_CAPTURECHANGED:
                // Something else took the mouse (menu, drag, another window): the guest must not keep a button down.
                // Our own ReleaseCapture after the last button-up lands here too; releasing again is a no-op.
                if ((HWND)(nint)lParam.Value != _hwnd) _session?.ReleaseAllButtons();
                return false;
            case PInvoke.WM_CANCELMODE:
                CancelGuestInput();
                return false; // DefWindowProc cancels its own modes too

            case PInvoke.WM_MOUSEMOVE:
            {
                // Released input: the guest gets nothing until the recapture click.
                if (_session is null || !GuestView.PointerToGuest(_state) || _released) return false;
                if (ServerMouse) return true; // relative motion comes from WM_INPUT while captured
                var (gx, gy) = ToGuest(Win32.LoWord(lParam.Value), Win32.HiWord(lParam.Value));
                _session.MousePosition(gx, gy);
                return true;
            }
            case PInvoke.WM_INPUT:
            {
                if (!_pointerCaptured || _session is null) return false;
                RAWINPUT raw;
                uint size = (uint)sizeof(RAWINPUT);
                if (PInvoke.GetRawInputData(new HRAWINPUT(lParam.Value), RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, &raw, &size,(uint)sizeof(RAWINPUTHEADER)) != unchecked((uint)-1)
                    && raw.header.dwType == 0 /* RIM_TYPEMOUSE */ && ((ushort)raw.data.mouse.usFlags & 1) == 0 /* relative */)
                    _session.MouseMotion(raw.data.mouse.lLastX, raw.data.mouse.lLastY);
                return false; // DefWindowProc must see WM_INPUT
            }
            case PInvoke.WM_LBUTTONDOWN or PInvoke.WM_LBUTTONUP or PInvoke.WM_RBUTTONDOWN or PInvoke.WM_RBUTTONUP
                or PInvoke.WM_MBUTTONDOWN or PInvoke.WM_MBUTTONUP or PInvoke.WM_XBUTTONDOWN or PInvoke.WM_XBUTTONUP
                or PInvoke.WM_LBUTTONDBLCLK or PInvoke.WM_RBUTTONDBLCLK or PInvoke.WM_MBUTTONDBLCLK or PInvoke.WM_XBUTTONDBLCLK:
            {
                // No guest display (or none shown): clicks stay with Windows; see GuestView.PointerToGuest.
                if (_session is null || !GuestView.PointerToGuest(_state)) return false;
                bool down = msg is PInvoke.WM_LBUTTONDOWN or PInvoke.WM_RBUTTONDOWN or PInvoke.WM_MBUTTONDOWN or PInvoke.WM_XBUTTONDOWN
                    or PInvoke.WM_LBUTTONDBLCLK or PInvoke.WM_RBUTTONDBLCLK or PInvoke.WM_MBUTTONDBLCLK or PInvoke.WM_XBUTTONDBLCLK;
                if (_released) { if (down) Recapture(); return true; }
                if (down && ServerMouse && !_pointerCaptured) { CapturePointer(); return true; }
                var button = msg switch
                {
                    PInvoke.WM_LBUTTONDOWN or PInvoke.WM_LBUTTONUP or PInvoke.WM_LBUTTONDBLCLK => MouseButton.Left,
                    PInvoke.WM_RBUTTONDOWN or PInvoke.WM_RBUTTONUP or PInvoke.WM_RBUTTONDBLCLK => MouseButton.Right,
                    PInvoke.WM_MBUTTONDOWN or PInvoke.WM_MBUTTONUP or PInvoke.WM_MBUTTONDBLCLK => MouseButton.Middle,
                    _ => ((wParam.Value >> 16) & 0xFFFF) == 1 ? MouseButton.Side : MouseButton.Extra,
                };
                if (!ServerMouse)
                {
                    var (gx, gy) = ToGuest(Win32.LoWord(lParam.Value), Win32.HiWord(lParam.Value));
                    _session.MousePosition(gx, gy);
                }
                _session.MouseButton(button, down);
                if (down) PInvoke.SetCapture(_hwnd);
                else if ((wParam.Value & 0x73) == 0) PInvoke.ReleaseCapture(); // no buttons left down
                result = msg is PInvoke.WM_XBUTTONDOWN or PInvoke.WM_XBUTTONUP or PInvoke.WM_XBUTTONDBLCLK ? (LRESULT)1 : default;
                return true;
            }
            case PInvoke.WM_MOUSEWHEEL:
            {
                if (_session is null || !GuestView.PointerToGuest(_state) || _released) return false;
                _wheelAccum += (short)((wParam.Value >> 16) & 0xFFFF);
                int notches = _wheelAccum / 120;
                _wheelAccum -= notches * 120;
                if (notches != 0) _session.MouseWheel(notches);
                return true;
            }
        }
        return false;
    }
}
