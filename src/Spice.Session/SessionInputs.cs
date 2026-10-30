using Spice.Wire;

namespace Spice.Session;

public sealed partial class SpiceSession
{
    readonly object _inputLock = new();
    readonly HashSet<ushort> _heldKeys = [];
    MouseButtonMask _buttons;
    // spice-server acks every 4 motion messages; keep at most 8 in flight and coalesce the rest (as spice-gtk does).
    const int MotionAckBunch = 4, MaxMotionInFlight = 8;
    int _motionInFlight;
    bool _pendingMotion, _pendingIsPosition;
    int _pendDx, _pendDy;
    uint _pendX, _pendY;
    byte _pendDisplay; // the monitor the pending position is relative to

    public LockKeys? GuestLockKeys { get; private set; }

    void OnInputsMessage(ushort type, ReadOnlyMemory<byte> m)
    {
        switch (type)
        {
            case InputsMsg.Init or InputsMsg.KeyModifiers:
                GuestLockKeys = (LockKeys)new SpiceReader(m).U16();
                break;
            case InputsMsg.MouseMotionAck:
                lock (_inputLock)
                {
                    _motionInFlight = Math.Max(0, _motionInFlight - MotionAckBunch);
                    if (_pendingMotion) SendPendingMotion();
                }
                break;
        }
    }

    /// <summary>
    /// Sends a key as an XT set-1 scancode: <c>0x00xx</c> for normal keys, <c>0xE0xx</c> for extended keys
    /// (Windows' KBDLLHOOKSTRUCT.scanCode plus LLKHF_EXTENDED map directly onto this).
    /// </summary>
    public void KeyEvent(ushort scancode, bool down)
    {
        var ch = _inputs;
        if (ch is null) return;
        lock (_inputLock)
        {
            if (down) _heldKeys.Add(scancode); else _heldKeys.Remove(scancode);
            SendKey(ch, scancode, down);
        }
    }

    static void SendKey(Transport.SpiceChannel ch, ushort scancode, bool down)
    {
        Span<byte> b = stackalloc byte[8];
        if (ch.ServerChannelCaps.Has(InputsCap.KeyScancode))
        {
            // KEY_SCANCODE carries the raw scancode bytes.
            int n = 0;
            if ((scancode & 0xFF00) == 0xE000) b[n++] = 0xE0;
            b[n++] = (byte)((scancode & 0x7F) | (down ? 0 : 0x80));
            ch.Send(InputsMsg.CKeyScancode, b[..n]);
        }
        else
        {
            int n = ClientMsg.U32(b, ClientMsg.KeyCode(scancode, down));
            ch.Send(down ? InputsMsg.CKeyDown : InputsMsg.CKeyUp, b[..n]);
        }
    }

    /// <summary>Releases every key the guest believes is held (on focus loss, so nothing sticks).</summary>
    public void ReleaseAllKeys()
    {
        var ch = _inputs;
        if (ch is null) return;
        lock (_inputLock)
        {
            foreach (var k in _heldKeys) SendKey(ch, k, false);
            _heldKeys.Clear();
        }
    }

    /// <summary>Ctrl+Alt+Del, which Windows never lets an app intercept.</summary>
    public void SendCtrlAltDel() => SendCombo(0x1D, 0x38, 0xE053);

    /// <summary>Win+L.</summary>
    public void SendWinL() => SendCombo(0xE05B, 0x26);

    public void SendCombo(params ushort[] keys)
    {
        foreach (var k in keys) KeyEvent(k, true);
        for (int i = keys.Length - 1; i >= 0; i--) KeyEvent(keys[i], false);
    }

    public void SetLockKeys(LockKeys keys)
    {
        Span<byte> b = stackalloc byte[4];
        _inputs?.Send(InputsMsg.CKeyModifiers, b[..ClientMsg.KeyModifiers(b, keys)]);
    }

    /// <summary>
    /// Client mouse mode: absolute position relative to the shown monitor (<see cref="Spice.Canvas.Canvas.PrimaryArea"/>),
    /// sent with that monitor's ID (<see cref="Spice.Canvas.Canvas.PrimaryMonitorId"/>), as spice-gtk does.
    /// </summary>
    public void MousePosition(int x, int y)
    {
        if (_inputs is null) return;
        lock (_inputLock)
        {
            _pendX = (uint)Math.Max(0, x); _pendY = (uint)Math.Max(0, y);
            // display_id is a u8 on the wire; spice-server allows at most 16 heads.
            _pendDisplay = (byte)Math.Min(Canvas.PrimaryMonitorId, byte.MaxValue);
            _pendingIsPosition = true; _pendingMotion = true;
            if (_motionInFlight < MaxMotionInFlight) SendPendingMotion();
        }
    }

    /// <summary>Server mouse mode: relative motion.</summary>
    public void MouseMotion(int dx, int dy)
    {
        if (_inputs is null || (dx == 0 && dy == 0)) return;
        lock (_inputLock)
        {
            if (_pendingMotion && _pendingIsPosition) { _pendDx = 0; _pendDy = 0; }
            _pendDx += dx; _pendDy += dy;
            _pendingIsPosition = false; _pendingMotion = true;
            if (_motionInFlight < MaxMotionInFlight) SendPendingMotion();
        }
    }

    void SendPendingMotion()
    {
        Span<byte> b = stackalloc byte[16];
        if (_pendingIsPosition)
            _inputs!.Send(InputsMsg.CMousePosition, b[..ClientMsg.MousePosition(b, _pendX, _pendY, _buttons, _pendDisplay)]);
        else
            _inputs!.Send(InputsMsg.CMouseMotion, b[..ClientMsg.MouseMotion(b, _pendDx, _pendDy, _buttons)]);
        _pendDx = _pendDy = 0;
        _pendingMotion = false;
        _motionInFlight++;
    }

    public void MouseButton(MouseButton button, bool down)
    {
        if (_inputs is null) return;
        lock (_inputLock)
        {
            // Flush pending motion first so the click lands where the pointer is.
            if (_pendingMotion) SendPendingMotion();
            var mask = button switch
            {
                Wire.MouseButton.Left => MouseButtonMask.Left,
                Wire.MouseButton.Middle => MouseButtonMask.Middle,
                Wire.MouseButton.Right => MouseButtonMask.Right,
                Wire.MouseButton.Side => MouseButtonMask.Side,
                Wire.MouseButton.Extra => MouseButtonMask.Extra,
                _ => MouseButtonMask.None,
            };
            if (down) _buttons |= mask; else _buttons &= ~mask;
            Span<byte> b = stackalloc byte[8];
            _inputs.Send(down ? InputsMsg.CMousePress : InputsMsg.CMouseRelease, b[..ClientMsg.MouseButtonEvent(b, button, _buttons)]);
        }
    }

    /// <summary>One wheel notch: a press and release of the Up/Down pseudo-button.</summary>
    public void MouseWheel(int notches)
    {
        var button = notches > 0 ? Wire.MouseButton.Up : Wire.MouseButton.Down;
        for (int i = 0; i < Math.Abs(notches); i++) { MouseButton(button, true); MouseButton(button, false); }
    }

    /// <summary>Releases held mouse buttons (focus or capture loss, input release).</summary>
    public void ReleaseAllButtons()
    {
        lock (_inputLock)
        {
            if ((_buttons & MouseButtonMask.Left) != 0) MouseButton(Wire.MouseButton.Left, false);
            if ((_buttons & MouseButtonMask.Middle) != 0) MouseButton(Wire.MouseButton.Middle, false);
            if ((_buttons & MouseButtonMask.Right) != 0) MouseButton(Wire.MouseButton.Right, false);
            if ((_buttons & MouseButtonMask.Side) != 0) MouseButton(Wire.MouseButton.Side, false);
            if ((_buttons & MouseButtonMask.Extra) != 0) MouseButton(Wire.MouseButton.Extra, false);
        }
    }

    /// <summary>
    /// Pause/Break has no break code: it is the make-only sequence E1 1D 45 E1 9D C5.
    /// </summary>
    public void SendPause()
    {
        var ch = _inputs;
        if (ch is null) return;
        lock (_inputLock)
        {
            if (ch.ServerChannelCaps.Has(InputsCap.KeyScancode))
            {
                ch.Send(InputsMsg.CKeyScancode, [0xE1, 0x1D, 0x45, 0xE1, 0x9D, 0xC5]);
            }
            else
            {
                // spice-server pushes a KEY_DOWN/UP code's bytes low byte first until a zero byte.
                Span<byte> b = stackalloc byte[4];
                ch.Send(InputsMsg.CKeyDown, b[..ClientMsg.U32(b, 0x451DE1)]);
                ch.Send(InputsMsg.CKeyUp, b[..ClientMsg.U32(b, 0xC59DE1)]);
            }
        }
    }
}
