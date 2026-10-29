using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpiceViewer;

/// <summary>
/// In full screen, a thin separate window at the top edge. Hovering expands it to show the title, the menu and
/// an exit button. It paints only on hover changes.
/// </summary>
static unsafe class FullscreenStrip
{
    const string ClassName = "SpiceViewer.Strip";
    static HWND s_hwnd;
    static MainWindow? s_owner;
    static bool s_expanded, s_registered;
    static HFONT s_font;
    static int s_monLeft, s_monTop, s_monWidth;

    static int Dpi(int v) => (int)(v * PInvoke.GetDpiForWindow(s_hwnd.IsNull ? s_owner!.Handle : s_hwnd) / 96);
    static int CollapsedHeight => Math.Max(2, Dpi(3));
    static int ExpandedHeight => Dpi(36);
    static int Width => Math.Min(s_monWidth, Dpi(520));

    public static void Update(MainWindow owner)
    {
        s_owner = owner;
        if (!owner.IsFullscreen) { Destroy(); return; }
        var mon = PInvoke.MonitorFromWindow(owner.Handle, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(mon, ref mi);
        s_monLeft = mi.rcMonitor.left; s_monTop = mi.rcMonitor.top; s_monWidth = mi.rcMonitor.Width;
        if (s_hwnd.IsNull) Create(owner);
        s_expanded = false;
        Place();
    }

    static void Create(MainWindow owner)
    {
        var hinst = PInvoke.GetModuleHandle((PCWSTR)null);
        fixed (char* cls = ClassName)
        {
            if (!s_registered)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = hinst,
                    hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
                    lpszClassName = cls,
                };
                PInvoke.RegisterClassEx(wc);
                s_registered = true;
            }
            s_hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                (PCWSTR)cls, default, WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, owner.Handle, HMENU.Null, (HINSTANCE)hinst, null);
        }
        var lf = new LOGFONTW { lfHeight = -Dpi(14), lfWeight = 400, lfQuality = FONT_QUALITY.CLEARTYPE_QUALITY };
        "Segoe UI".AsSpan().CopyTo(lf.lfFaceName.AsSpan());
        s_font = PInvoke.CreateFontIndirect(&lf);
    }

    static void Place()
    {
        int w = Width, h = s_expanded ? ExpandedHeight : CollapsedHeight;
        PInvoke.SetWindowPos(s_hwnd, HWND.HWND_TOPMOST, s_monLeft + (s_monWidth - w) / 2, s_monTop, w, h,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        PInvoke.InvalidateRect(s_hwnd, (RECT?)null, true);
    }

    public static void Destroy()
    {
        if (s_hwnd.IsNull) return;
        PInvoke.DestroyWindow(s_hwnd);
        s_hwnd = HWND.Null;
        if (!s_font.IsNull) { PInvoke.DeleteObject(s_font); s_font = HFONT.Null; }
    }

    // Layout of the expanded strip: [ Menu ][ title ............ ][ Exit full screen ]
    static RECT MenuRect => new(0, 0, Dpi(70), ExpandedHeight);
    static RECT ExitRect => new(Width - Dpi(140), 0, Width, ExpandedHeight);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            switch (msg)
            {
                case PInvoke.WM_MOUSEMOVE:
                    if (!s_expanded)
                    {
                        s_expanded = true;
                        Place();
                        var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE, hwndTrack = hwnd };
                        PInvoke.TrackMouseEvent(ref tme);
                    }
                    return default;
                case PInvoke.WM_MOUSELEAVE:
                    s_expanded = false;
                    Place();
                    return default;
                case PInvoke.WM_LBUTTONUP:
                {
                    var pt = new System.Drawing.Point(Win32.LoWord(lParam.Value), Win32.HiWord(lParam.Value));
                    if (Contains(ExitRect, pt)) { s_owner?.Post(s_owner.ToggleFullscreen); return default; }
                    if (Contains(MenuRect, pt) && s_owner is not null)
                    {
                        var menu = PInvoke.GetSystemMenu(s_owner.Handle, false);
                        var screen = new System.Drawing.Point(0, ExpandedHeight);
                        PInvoke.ClientToScreen(hwnd, ref screen);
                        PInvoke.SetForegroundWindow(s_owner.Handle);
                        var cmd = PInvoke.TrackPopupMenu(menu, TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_LEFTALIGN,
                            screen.X, screen.Y, 0, s_owner.Handle, null);
                        if (cmd.Value != 0)
                        {
                            uint id = (uint)cmd.Value;
                            if (!s_owner.OnSysCommand(id)) PInvoke.PostMessage(s_owner.Handle, PInvoke.WM_SYSCOMMAND, id, 0);
                        }
                    }
                    return default;
                }
                case PInvoke.WM_PAINT:
                    Paint(hwnd);
                    return default;
                case PInvoke.WM_ERASEBKGND:
                    return (LRESULT)1;
                case PInvoke.WM_MOUSEACTIVATE:
                    return (LRESULT)3; // MA_NOACTIVATE
            }
        }
        catch (Exception e) { Log.Error($"strip: {e}"); }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    static bool Contains(RECT r, System.Drawing.Point p) => p.X >= r.left && p.X < r.right && p.Y >= r.top && p.Y < r.bottom;

    static void Paint(HWND hwnd)
    {
        var hdc = PInvoke.BeginPaint(hwnd, out var ps);
        PInvoke.GetClientRect(hwnd, out var rc);
        var bg = PInvoke.CreateSolidBrush(new COLORREF(0x00303030));
        PInvoke.FillRect(hdc, &rc, bg);
        PInvoke.DeleteObject(bg);
        if (s_expanded && s_owner is not null)
        {
            PInvoke.SetBkMode(hdc, BACKGROUND_MODE.TRANSPARENT);
            PInvoke.SetTextColor(hdc, new COLORREF(0x00F0F0F0));
            var old = PInvoke.SelectObject(hdc, s_font);
            void Text(string s, RECT r, DRAW_TEXT_FORMAT f)
            {
                fixed (char* p = s) PInvoke.DrawText(hdc, p, s.Length, ref r, f | DRAW_TEXT_FORMAT.DT_VCENTER | DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_NOPREFIX | DRAW_TEXT_FORMAT.DT_END_ELLIPSIS);
            }
            Text("☰ Menu", MenuRect, DRAW_TEXT_FORMAT.DT_CENTER);
            var title = new RECT(MenuRect.right, 0, ExitRect.left, ExpandedHeight);
            Text(s_owner.Title, title, DRAW_TEXT_FORMAT.DT_CENTER);
            Text("Exit full screen  ✕", ExitRect, DRAW_TEXT_FORMAT.DT_CENTER);
            PInvoke.SelectObject(hdc, old);
        }
        PInvoke.EndPaint(hwnd, ps);
    }
}
