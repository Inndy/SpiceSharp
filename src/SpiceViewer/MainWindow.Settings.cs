using Spice.Session;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpiceViewer;

/// <summary>
/// Preferences and the per-VM window. Read once before the window is created; written only when the window closes and
/// when a setting changes in the menu (both in the background, see <see cref="SettingsStore"/>).
/// </summary>
sealed unsafe partial class MainWindow
{
    SettingsStore? _settings;
    /// <summary>Null when the .vv has no title: nothing is remembered for this window.</summary>
    string? _vmKey;
    ServerCursorMode _serverCursor = ServerCursorMode.Guest;
    VideoPacingMode _videoPacing = VideoPacingMode.Off;
    bool _fastJpeg;
    ImageCompressionMode _imageCompression = ImageCompressionMode.Auto;
    VideoCodecMode _videoCodec = VideoCodecMode.Auto;
    bool _softwareVideo = true;
    /// <summary>The window opened at a remembered rectangle, so the first frame doesn't resize or center it.</summary>
    bool _restoredPlacement;
    bool _restoreFullscreen;
    /// <summary>Full screen was left because the session ended or the display failed; still remembered as full screen.</summary>
    bool _fullscreenWhenEnded;

    /// <summary>Takes the global preferences and this VM's entry; returns where to create the window.</summary>
    (RECT Rect, bool Maximized) ApplySettings(SettingsStore settings)
    {
        _settings = settings;
        var s = settings.Current;
        _scaling = s.Scaling;
        _shortcuts = s.SystemShortcuts;
        _resizeGuest = s.ResizeGuest;
        _trimGuestPadding = s.TrimGuestPadding;
        _serverCursor = s.ServerCursor;
        _videoPacing = s.VideoPacing;
        // The session started connecting before the settings were read; a mode change reschedules anything queued.
        _fastJpeg = s.FastJpeg;
        _imageCompression = s.ImageCompression;
        _videoCodec = s.VideoCodec;
        _softwareVideo = s.SoftwareVideo;
        _statusOverlay = s.StatusOverlay;
        // Read by the session when it advertises its codecs, after the hardware probe (over 100 ms from start).
        if (Program.Video is { } video) video.SoftwareEnabled = _softwareVideo;
        if (_session is { } session)
        {
            session.VideoPacing = SessionPacing(_videoPacing);
            // The display channel may not be up yet; the session sends the preference once it is.
            session.FastJpeg = _fastJpeg;
            // Off also withdraws an area inferred before the settings were read.
            session.Canvas.InferPaddedMonitor = _trimGuestPadding;
            session.PreferredCompression = SessionCompression(_imageCompression);
            session.PreferredVideoCodecs = _videoCodec.ServerOrder();
        }
        _vmKey = SettingsStore.VmKey(_vv.Host, _vv.Proxy?.Host, _vv.Title);
        if (_vmKey is null || !s.Vms.TryGetValue(_vmKey, out var vm)) return (DefaultRect(), false);

        _scaling = vm.Scaling;
        _restoreFullscreen = vm.Fullscreen;
        if (RestoredRect(vm) is { } r)
        {
            _restoredPlacement = true;
            return (r, vm.Maximized);
        }
        Log.Info("settings: the remembered window position is not on any monitor; using the default placement");
        return (DefaultRect(), vm.Maximized);
    }

    /// <summary>On the monitor under the mouse, sized for a typical guest until the real size is known.</summary>
    static RECT DefaultRect()
    {
        PInvoke.GetCursorPos(out var pt);
        var mon = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(mon, ref mi);
        var work = mi.rcWork;
        int w = Math.Min(1024, work.Width * 3 / 4), h = Math.Min(768, work.Height * 3 / 4);
        int x = work.left + (work.Width - w) / 2, y = work.top + (work.Height - h) / 2;
        return new RECT(x, y, x + w, y + h);
    }

    /// <summary>The remembered rectangle, scaled to its monitor's current DPI; null when it is on no monitor now.</summary>
    static RECT? RestoredRect(VmWindowState vm)
    {
        var mon = PInvoke.MonitorFromRect(new RECT(vm.Left, vm.Top, vm.Right, vm.Bottom), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
        if (mon.IsNull) return null;
        var (l, t, r, b) = vm.RectForDpi((int)MonitorDpi(mon));
        return new RECT(l, t, r, b);
    }

    static uint MonitorDpi(HMONITOR mon) =>
        PInvoke.GetDpiForMonitor(mon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint x, out _).Succeeded && x > 0 ? x : 96;

    /// <summary>
    /// The window as it should reopen: the normal (restored) rectangle even when maximized or full screen, in screen
    /// pixels with its monitor's DPI.
    /// </summary>
    VmWindowState? CaptureWindowState()
    {
        // In full screen the window's own placement is the monitor; the one saved on entering full screen is the window's.
        var wp = _savedPlacement;
        if (!_fullscreen)
        {
            wp = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            if (!PInvoke.GetWindowPlacement(_hwnd, ref wp)) return null;
        }
        bool maximized = wp.showCmd == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED
            || (wp.showCmd == SHOW_WINDOW_CMD.SW_SHOWMINIMIZED && (wp.flags & WINDOWPLACEMENT_FLAGS.WPF_RESTORETOMAXIMIZED) != 0);
        // rcNormalPosition is in workspace coordinates (relative to its monitor's work area); store screen pixels.
        var r = wp.rcNormalPosition;
        var mon = PInvoke.MonitorFromRect(r, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(mon, ref mi)) return null;
        int dx = mi.rcWork.left - mi.rcMonitor.left, dy = mi.rcWork.top - mi.rcMonitor.top;
        return new VmWindowState
        {
            Left = r.left + dx, Top = r.top + dy, Right = r.right + dx, Bottom = r.bottom + dy,
            Dpi = (int)MonitorDpi(mon),
            Maximized = maximized,
            Fullscreen = _fullscreen || _fullscreenWhenEnded,
            Scaling = _scaling,
            LastUsed = DateTime.UtcNow,
        };
    }

    /// <summary>At close: remembers this VM's window if it ever showed the guest. Returns the background save, if any.</summary>
    Task? SaveWindowState()
    {
        if (_settings is null || _vmKey is null || !_gotFirstFrame) return null;
        try
        {
            if (CaptureWindowState() is not { } state) return null;
            string key = _vmKey;
            var store = _settings;
            return Task.Run(() => store.Update(s => s.Vms[key] = state));
        }
        catch (Exception e)
        {
            Log.Warn($"settings: could not capture the window state: {e.Message}");
            return null;
        }
    }

    /// <summary>A menu change: written in the background (the store serializes writes and never throws).</summary>
    void SaveSetting(Action<ViewerSettings> change)
    {
        var store = _settings;
        if (store is not null) Task.Run(() => store.Update(change));
    }

    /// <summary>Scaling is remembered for this VM and becomes the default for VMs without an entry.</summary>
    void SaveScaling()
    {
        var mode = _scaling;
        var key = _vmKey;
        SaveSetting(s =>
        {
            s.Scaling = mode;
            if (key is not null && s.Vms.TryGetValue(key, out var vm)) vm.Scaling = mode;
        });
    }

    void SetServerCursor(ServerCursorMode mode)
    {
        _serverCursor = mode;
        UpdateMenuChecks();
        if (_pointerCaptured)
        {
            PInvoke.SetCursor(CurrentClientCursor());
            ApplyGuestCursorPos();
        }
        SaveSetting(s => s.ServerCursor = mode);
    }

    static VideoPacing SessionPacing(VideoPacingMode mode) => mode switch
    {
        VideoPacingMode.Spacing => VideoPacing.Spacing,
        VideoPacingMode.Clock => VideoPacing.Clock,
        _ => VideoPacing.Off,
    };

    /// <summary>A menu change: applies to the running session at once (its queued frames are rescheduled) and is saved.</summary>
    void SetVideoPacing(VideoPacingMode mode)
    {
        if (mode == _videoPacing) return;
        _videoPacing = mode;
        UpdateMenuChecks();
        if (_session is { } session) session.VideoPacing = SessionPacing(mode);
        InvalidateStatusOverlay();
        SaveSetting(s => s.VideoPacing = mode);
    }

    /// <summary>
    /// A menu change, saved for all VMs. Off gives the running session's configured (or whole-surface) area back at once;
    /// on narrows the area at the guest's next full-frame draw (docs/decisions/0035, finding L16).
    /// </summary>
    void SetTrimGuestPadding(bool on)
    {
        _trimGuestPadding = on;
        UpdateMenuChecks();
        if (_session is { } session) session.Canvas.InferPaddedMonitor = on;
        SaveSetting(s => s.TrimGuestPadding = on);
    }

    /// <summary>A menu change: the next JPEG decode of the running session uses it; saved for all VMs.</summary>
    void SetFastJpeg(bool on)
    {
        _fastJpeg = on;
        UpdateMenuChecks();
        if (_session is { } session) session.FastJpeg = on;
        SaveSetting(s => s.FastJpeg = on);
    }

    static ImageCompression SessionCompression(ImageCompressionMode mode) =>
        mode == ImageCompressionMode.Lz4 ? ImageCompression.Lz4 : ImageCompression.Invalid;

    /// <summary>
    /// A menu change: a preference is sent to the running session's server at once (docs/decisions/0028); going back to
    /// the server default takes effect on the next connection. Saved for all VMs.
    /// </summary>
    void SetImageCompression(ImageCompressionMode mode)
    {
        if (mode == _imageCompression) return;
        _imageCompression = mode;
        UpdateMenuChecks();
        if (_session is { } session) session.PreferredCompression = SessionCompression(mode);
        SaveSetting(s => s.ImageCompression = mode);
    }

    /// <summary>The codecs decoded here, in hardware or in software; empty until the hardware probe has answered.</summary>
    static IReadOnlyList<VideoCodecType> DecodableVideoCodecs() => Program.Video?.KnownCodecs ?? [];

    /// <summary>How a codec is decoded here, for the menu and About: "hardware", "software", both, or null.</summary>
    static string? VideoPath(VideoCodecType codec)
    {
        if (Program.Video is not { KnownCodecs: not null } video) return null;
        bool hw = video.HardwareCodecs.Contains(codec), sw = video.SoftwareCodecs.Contains(codec);
        return hw && sw ? "hardware; 4:4:4 in software" : hw ? "hardware" : sw ? "software" : null;
    }

    /// <summary>
    /// A menu change, saved for all VMs. New streams pick their decoder with it at once; the codecs advertised to the
    /// server change from the next connection (capabilities are fixed for a session).
    /// </summary>
    void SetSoftwareVideo(bool on)
    {
        _softwareVideo = on;
        if (Program.Video is { } video) video.SoftwareEnabled = on;
        UpdateMenuChecks();
        Log.Info($"video: software decoding {(on ? "on" : "off")} (advertising follows from the next connection)");
        SaveSetting(s => s.SoftwareVideo = on);
    }

    /// <summary>
    /// A menu change: sent to the running session's server at once (it then restarts its streams with the new order);
    /// going back to the server default takes effect on the next connection. Saved for all VMs.
    /// </summary>
    void SetVideoCodec(VideoCodecMode mode)
    {
        if (mode == _videoCodec) return;
        _videoCodec = mode;
        UpdateMenuChecks();
        if (_session is { } session) session.PreferredVideoCodecs = mode.ServerOrder();
        SaveSetting(s => s.VideoCodec = mode);
    }
}
