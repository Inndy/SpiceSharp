namespace SpiceViewer;

/// <summary>
/// The window's view of the session. <see cref="DisplayOff"/>: the session is linked but the guest has no primary surface
/// (its display is powered down); the window says so, and keys still go to the guest to wake it (docs/decisions/0035).
/// <see cref="DisplayFailed"/>: the session is up but the presenter stopped; input is released and not forwarded (the
/// user can't see the guest). A new guest display (OnPrimaryChanged) tries a new presenter and returns to Connected.
/// </summary>
enum ViewState { Connecting, Connected, Disconnected, DisplayFailed, DisplayOff }

/// <summary>
/// What may reach the guest in each <see cref="ViewState"/>, and the mapping between client pixels and the guest's
/// monitor area. Pure functions, kept apart from the window so tests can run them without one (docs/decisions/0035).
/// </summary>
static class GuestView
{
    /// <summary>
    /// Keys (and the viewer's hotkeys) go to the guest with or without a guest display: a key is what wakes a guest whose
    /// display is off. In <see cref="ViewState.Connecting"/> only once every channel is linked (<paramref name="linked"/>);
    /// in <see cref="ViewState.Connected"/> and <see cref="ViewState.DisplayOff"/> always, since the display channel got
    /// that far (a DISPLAY_MARK can arrive before the inputs channel is attached, and the session drops keys until it is).
    /// Never while the presenter has failed or after the session ended.
    /// </summary>
    public static bool KeysToGuest(ViewState state, bool linked) => state switch
    {
        ViewState.Connected or ViewState.DisplayOff => true,
        ViewState.Connecting => linked,
        _ => false,
    };

    /// <summary>
    /// The pointer (motion, buttons, wheel, pointer capture) only while the guest display is shown: without one there are
    /// no coordinates to send, and a click would land wherever the guest's invisible pointer happens to be.
    /// </summary>
    public static bool PointerToGuest(ViewState state) => state == ViewState.Connected;

    /// <summary>
    /// A client point over the guest image (drawn at <paramref name="left"/>, <paramref name="top"/>, size
    /// <paramref name="width"/>×<paramref name="height"/> in client pixels) as a point in the guest's monitor area of
    /// <paramref name="guestW"/>×<paramref name="guestH"/>, clamped to it. Area-relative, as spice-gtk sends client-mode
    /// positions; for a monitor at (0,0), the usual case, that equals surface coordinates.
    /// </summary>
    public static (int X, int Y) ClientToGuest(int cx, int cy, int left, int top, int width, int height, int guestW, int guestH)
    {
        if (guestW <= 0 || guestH <= 0 || width <= 0 || height <= 0) return (cx, cy);
        return (Axis(cx - left, width, guestW), Axis(cy - top, height, guestH));
    }

    /// <summary>
    /// One axis of <see cref="ClientToGuest"/>: client offset <paramref name="c"/> on an image <paramref name="size"/>
    /// pixels long showing <paramref name="guest"/> pixels, clamped to the guest. Shown 1:1 or enlarged, each client
    /// pixel maps to the guest pixel under it. Shrunk, that would leave the guest's last pixels out of reach (the last
    /// client pixel of a 320-pixel image of 640 maps to 638), so the ends are mapped onto the ends instead, as
    /// spice-gtk's transform_input does: (size - 1) to (guest - 1).
    /// </summary>
    static int Axis(int c, int size, int guest)
    {
        long g = size >= guest ? (long)c * guest / size
            : size == 1 ? 0 : (long)c * (guest - 1) / (size - 1);
        return (int)Math.Clamp(g, 0, guest - 1);
    }

    /// <summary>
    /// A guest cursor position in surface pixels (the cursor channel's coordinates) as a client point: the monitor
    /// area's offset (<paramref name="areaX"/>, <paramref name="areaY"/>) removed, then scaled onto the guest image.
    /// </summary>
    public static (int X, int Y) GuestToClient(int sx, int sy, int areaX, int areaY, int left, int top, int width, int height,
        int guestW, int guestH)
    {
        if (guestW <= 0 || guestH <= 0) return (left, top);
        return (left + (int)((long)(sx - areaX) * width / guestW), top + (int)((long)(sy - areaY) * height / guestH));
    }
}
