using Spice.Session;
using Spice.Wire;
using VvFile;
using Windows.Win32;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpiceViewer;

/// <summary>The status overlay's toggle, setting and data source (docs/decisions/0044).</summary>
sealed unsafe partial class MainWindow
{
    const uint IdStatusOverlay = 0x2A0;

    /// <summary>
    /// Ctrl+Alt+Shift+F12: no .vv default (Shift+F11, Ctrl+Alt+R, Ctrl+Alt+Ins, virt-viewer's Shift+F12, Shift+F8/F9),
    /// no Windows or common application shortcut, no Linux console switch (Ctrl+Alt+Fn without Shift), and no AltGr
    /// character (a function key). A .vv hotkey with the same keys wins: it is checked first.
    /// </summary>
    static readonly Hotkey StatusOverlayHotkey = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x7B /* VK_F12 */);

    bool _statusOverlay;

    /// <summary>The overlay's title for a .vv file without one.</summary>
    const string UntitledOverlay = "(untitled console)";

    /// <summary>The menu item and the hotkey: shows or hides the overlay at once and saves the choice for all VMs.</summary>
    void ToggleStatusOverlay()
    {
        bool on = _statusOverlay = !_statusOverlay;
        UpdateMenuChecks();
        ApplyStatusOverlay();
        Log.Info($"status overlay: {(on ? "shown" : "hidden")}");
        SaveSetting(s => s.StatusOverlay = on);
    }

    /// <summary>Gives the presenter a new overlay (fresh rates) or none. Also called when a presenter is created.</summary>
    void ApplyStatusOverlay() => _presenter?.SetOverlay(_statusOverlay ? new StatusOverlay(new OverlaySource(this)) : null);

    /// <summary>A value the overlay shows changed (mouse mode, agent, a setting): refresh it if it is shown.</summary>
    void InvalidateStatusOverlay() => _presenter?.InvalidateOverlay();

    sealed class OverlaySource(MainWindow window) : IOverlaySource
    {
        public void Fill(OverlaySnapshot s) => window.FillOverlay(s);
    }

    /// <summary>
    /// On the presenter thread, at an overlay refresh: the window's and the session's figures. Reads UI-thread fields
    /// without a lock (each is a single value; a refresh racing a change shows the old or the new one). No ticket,
    /// password or proxy address is shown.
    /// </summary>
    void FillOverlay(OverlaySnapshot s)
    {
        // The .vv title only: DisplayTitle falls back to the host, which the overlay doesn't show (review SO3).
        s.Title = string.IsNullOrEmpty(_vv.Title) ? UntitledOverlay : _vv.Title;
        s.Tls = _vv.UseTls;
        s.Proxy = _vv.Proxy is not null;
        s.State = _state switch
        {
            ViewState.Connecting => "connecting",
            ViewState.Connected => "connected",
            ViewState.DisplayOff => "display off",
            ViewState.DisplayFailed => "display failed",
            _ => "disconnected",
        };
        s.OneToOne = _scaling == ScalingMode.OneToOne || _presenter?.IsSoftware == true;
        s.GcHeapBytes = GC.GetTotalMemory(false);
        s.PrivateBytes = PrivateBytes();
        var session = _session;
        if (session is null) return;
        s.Channels = session.LinkedChannelCount;
        s.ClientMouse = session.MouseMode == MouseMode.Client;
        s.Agent = session.AgentConnected;
        var canvas = session.Canvas;
        lock (canvas.Sync)
        {
            var primary = canvas.Primary;
            s.HasSurface = primary is not null;
            if (primary is not null)
            {
                var area = canvas.PrimaryArea;
                s.SurfaceW = primary.Width; s.SurfaceH = primary.Height;
                s.AreaX = area.Left; s.AreaY = area.Top; s.AreaW = area.Width; s.AreaH = area.Height;
                s.AreaInferred = canvas.PrimaryAreaInferred;
            }
        }
        // -1: a frame is being decoded; the streams shown last stay.
        int streams = session.ReadVideoStreams(s.Streams);
        if (streams >= 0)
        {
            s.StreamCount = streams;
            for (int i = 0; i < Math.Min(streams, OverlaySnapshot.MaxStreams); i++)
                s.DecoderNames[i] = Program.HardwareVideo?.DecoderName(s.Streams[i].Codec);
        }
        s.Pacing = session.VideoPacing;
        session.ReadTraffic(out s.NetBytes, out s.NetMessages, out s.DisplayBytes);
        s.Audio = session.Audio;
        if (session.LastTrim is { } trim) { s.LastTrimMs = trim.TickMs; s.LastTrimReason = trim.Reason; }
    }

    /// <summary>The process's private bytes (what decision 0041 measures); 0 if Windows doesn't say.</summary>
    static long PrivateBytes()
    {
        var counters = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX) };
        return PInvoke.K32GetProcessMemoryInfo(PInvoke.GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&counters, counters.cb)
            ? (long)counters.PrivateUsage : 0;
    }

    void UpdateStatusOverlayMenu()
    {
        if (_sysMenu.IsNull) return;
        PInvoke.CheckMenuItem(_sysMenu, IdStatusOverlay, (uint)(_statusOverlay ? MENU_ITEM_FLAGS.MF_CHECKED : MENU_ITEM_FLAGS.MF_UNCHECKED));
    }
}
