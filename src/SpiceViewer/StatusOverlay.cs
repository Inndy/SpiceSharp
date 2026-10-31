using System.Globalization;
using Spice.Session;
using Spice.Wire;

namespace SpiceViewer;

/// <summary>Fills the parts of an <see cref="OverlaySnapshot"/> that come from the window and the session.</summary>
interface IOverlaySource
{
    /// <summary>
    /// Called on the presenter thread, only when the overlay is refreshed. Must not allocate, block or take the canvas
    /// lock while holding another session lock.
    /// </summary>
    void Fill(OverlaySnapshot s);
}

/// <summary>
/// What the status overlay shows (docs/decisions/0044). One instance per overlay, filled in place at each refresh: the
/// window and session fields by the <see cref="IOverlaySource"/>, the presenter fields by the presenter.
/// </summary>
sealed class OverlaySnapshot
{
    public const int MaxStreams = 4;

    // Connection (window and session).
    public string Title = "";
    public bool Tls, Proxy;
    public int Channels;
    public string State = "";
    public bool ClientMouse, Agent;
    // Display (canvas).
    public bool HasSurface;
    public int SurfaceW, SurfaceH, AreaX, AreaY, AreaW, AreaH;
    public bool AreaInferred, OneToOne;
    // Presenter.
    public bool Direct, Software;
    public int BufferW, BufferH;
    public double Scale;
    public string Adapter = "";
    public long Presents;
    // Video. StreamCount may exceed MaxStreams: only the first MaxStreams are listed.
    public int StreamCount;
    public readonly VideoStreamStatus[] Streams = new VideoStreamStatus[MaxStreams];
    public readonly string?[] DecoderNames = new string?[MaxStreams];
    public VideoPacing Pacing;
    // Traffic: lifetime totals.
    public long NetBytes, NetMessages, DisplayBytes;
    // Audio.
    public AudioStatus Audio;
    // Memory.
    public long PrivateBytes, GcHeapBytes;
    public long LastTrimMs;
    public string? LastTrimReason;
    // When the snapshot was taken: Environment.TickCount64 and the local time of day.
    public long NowMs;
    public TimeSpan TimeOfDay;
}

/// <summary>
/// Rates from the snapshot's counters. Sampled only when the overlay refreshes, at most once per
/// <see cref="IntervalMs"/>, over the time since the previous sample: no timer ever runs for them.
/// </summary>
sealed class OverlayRates
{
    public const long IntervalMs = 1000;

    long _at;
    bool _sampled;
    long _presents, _bytes, _messages, _display;
    readonly uint[] _streamIds = new uint[OverlaySnapshot.MaxStreams];
    readonly long[] _streamShown = new long[OverlaySnapshot.MaxStreams];
    int _streamCount;

    /// <summary>A previous sample exists, so the rates below are meaningful.</summary>
    public bool Valid { get; private set; }
    /// <summary>The span the rates cover, in seconds.</summary>
    public double Seconds { get; private set; }
    public double Presents = double.NaN, Bytes = double.NaN, Messages = double.NaN, DisplayBytes = double.NaN;
    readonly double[] _streamFps = new double[OverlaySnapshot.MaxStreams];

    /// <summary>Shown frames per second of stream <paramref name="id"/> at the last sample; NaN if it wasn't in two samples.</summary>
    public double FpsOf(uint id)
    {
        for (int i = 0; i < _streamCount; i++) if (_streamIds[i] == id) return _streamFps[i];
        return double.NaN;
    }

    public bool Due(long nowMs) => !_sampled || nowMs - _at >= IntervalMs;

    public void Sample(OverlaySnapshot s, long nowMs)
    {
        double seconds = (nowMs - _at) / 1000.0;
        Valid = _sampled && seconds > 0;
        Seconds = Valid ? seconds : 0;
        Presents = Rate(s.Presents, _presents);
        Bytes = Rate(s.NetBytes, _bytes);
        Messages = Rate(s.NetMessages, _messages);
        DisplayBytes = Rate(s.DisplayBytes, _display);
        int listed = Math.Min(s.StreamCount, OverlaySnapshot.MaxStreams);
        Span<double> fps = stackalloc double[OverlaySnapshot.MaxStreams];
        for (int i = 0; i < listed; i++)
        {
            fps[i] = double.NaN;
            for (int j = 0; j < _streamCount; j++)
                if (_streamIds[j] == s.Streams[i].Id) { fps[i] = Rate(s.Streams[i].Shown, _streamShown[j]); break; }
        }
        for (int i = 0; i < listed; i++) { _streamIds[i] = s.Streams[i].Id; _streamShown[i] = s.Streams[i].Shown; _streamFps[i] = fps[i]; }
        _streamCount = listed;
        _presents = s.Presents; _bytes = s.NetBytes; _messages = s.NetMessages; _display = s.DisplayBytes;
        _at = nowMs;
        _sampled = true;

        double Rate(long now, long before) => Valid ? Math.Max(0, now - before) / seconds : double.NaN;
    }
}

/// <summary>
/// The overlay's lines in a fixed character grid: formatted in place (no strings), compared with what is shown so an
/// unchanged text is neither redrawn nor presented. Text past <see cref="MaxColumns"/> or <see cref="MaxLines"/> is cut.
/// </summary>
sealed class OverlayText
{
    public const int MaxLines = 18, MaxColumns = 80;

    readonly char[] _chars = new char[MaxLines * MaxColumns];
    readonly int[] _lengths = new int[MaxLines];
    readonly char[] _shown = new char[MaxLines * MaxColumns];
    readonly int[] _shownLengths = new int[MaxLines];
    int _shownCount = -1;
    int _line = -1;

    public int LineCount => _line + 1;

    /// <summary>The longest line's length.</summary>
    public int Columns
    {
        get
        {
            int max = 0;
            for (int i = 0; i < LineCount; i++) max = Math.Max(max, _lengths[i]);
            return max;
        }
    }

    public ReadOnlySpan<char> Line(int i) => _chars.AsSpan(i * MaxColumns, _lengths[i]);

    public void Clear() => _line = -1;

    /// <summary>Starts a new line (ignored past <see cref="MaxLines"/>; what follows goes to the last line).</summary>
    public OverlayText NewLine(ReadOnlySpan<char> label = default)
    {
        if (_line < MaxLines - 1) _lengths[++_line] = 0;
        return Add(label);
    }

    Span<char> Free => _line < 0 ? default : _chars.AsSpan(_line * MaxColumns + _lengths[_line], MaxColumns - _lengths[_line]);

    public OverlayText Add(ReadOnlySpan<char> s)
    {
        if (_line < 0) NewLine();
        var free = Free;
        int n = Math.Min(s.Length, free.Length);
        s[..n].CopyTo(free);
        _lengths[_line] += n;
        return this;
    }

    public OverlayText Add(char c) => Add(new ReadOnlySpan<char>(in c));

    public OverlayText Add(long v) => Format(v, default);

    /// <summary><paramref name="v"/> with <paramref name="format"/> ("D2"…), invariant culture.</summary>
    public OverlayText Add(long v, ReadOnlySpan<char> format) => Format(v, format);

    /// <summary><paramref name="v"/> with <paramref name="format"/> ("F1", "D2"…), invariant culture; "–" for NaN.</summary>
    public OverlayText Add(double v, ReadOnlySpan<char> format) => double.IsNaN(v) ? Add('–') : Format(v, format);

    OverlayText Format<T>(T v, ReadOnlySpan<char> format) where T : ISpanFormattable
    {
        if (_line < 0) NewLine();
        if (v.TryFormat(Free, out int n, format, CultureInfo.InvariantCulture)) _lengths[_line] += n;
        else Add('…');
        return this;
    }

    /// <summary>A byte count as B, KB, MB or GB (1024-based), one decimal from KB up.</summary>
    public OverlayText AddBytes(double bytes)
    {
        if (double.IsNaN(bytes)) return Add('–');
        if (bytes < 1024) return Add(bytes, "F0").Add(" B");
        if (bytes < 1024 * 1024) return Add(bytes / 1024, "F1").Add(" KB");
        if (bytes < 1024L * 1024 * 1024) return Add(bytes / (1024 * 1024), "F1").Add(" MB");
        return Add(bytes / (1024L * 1024 * 1024), "F2").Add(" GB");
    }

    public OverlayText AddSize(int w, int h) => Add(w).Add('x').Add(h);

    /// <summary>
    /// Compares the text with the one shown last and makes it the shown one. True when it differs (the overlay image must
    /// be redrawn and presented), false when it is the same.
    /// </summary>
    public bool Commit()
    {
        bool changed = _shownCount != LineCount;
        for (int i = 0; i < LineCount && !changed; i++)
            changed = _shownLengths[i] != _lengths[i] || !Line(i).SequenceEqual(_shown.AsSpan(i * MaxColumns, _lengths[i]));
        if (!changed) return false;
        _chars.AsSpan().CopyTo(_shown);
        _lengths.AsSpan().CopyTo(_shownLengths);
        _shownCount = LineCount;
        return true;
    }
}

/// <summary>Turns a snapshot and its rates into the overlay's lines. Pure: tested without a window.</summary>
static class OverlayFormat
{
    public static void Write(OverlaySnapshot s, OverlayRates r, OverlayText t)
    {
        t.Clear();
        var tod = s.TimeOfDay;
        t.NewLine().Add(Cut(s.Title, 32)).Add("  ").Add(tod.Hours, "D2").Add(':').Add(tod.Minutes, "D2").Add(':').Add(tod.Seconds, "D2");
        if (r.Valid) t.Add("  rates over ").Add(r.Seconds, r.Seconds < 10 ? "F1" : "F0").Add(" s");

        t.NewLine("conn  ").Add(s.State).Add(s.Tls ? ", TLS" : ", no TLS").Add(s.Proxy ? " via proxy" : "")
            .Add(", ").Add(s.Channels).Add(" ch, mouse ").Add(s.ClientMouse ? "client" : "server")
            .Add(s.Agent ? ", agent" : ", no agent");

        t.NewLine("disp  ");
        if (!s.HasSurface) t.Add("no guest display");
        else
        {
            t.AddSize(s.AreaW, s.AreaH);
            if (s.AreaW != s.SurfaceW || s.AreaH != s.SurfaceH)
            {
                t.Add(" of ").AddSize(s.SurfaceW, s.SurfaceH);
                if (s.AreaX != 0 || s.AreaY != 0) t.Add(" at ").Add(s.AreaX).Add(',').Add(s.AreaY);
                t.Add(s.AreaInferred ? " (inferred)" : " (monitor)");
            }
        }
        t.Add(", ").Add(s.OneToOne ? "1:1" : "fit").Add(' ').Add(s.Scale, "F2").Add('x');

        t.NewLine("pres  ").Add(s.Direct ? "direct " : "letterbox ").AddSize(s.BufferW, s.BufferH)
            .Add(", ").Add(r.Presents, "F1").Add("/s, ").Add(Cut(s.Adapter, 36)).Add(s.Software ? " (software)" : "");

        t.NewLine("net   ").AddBytes(r.Bytes).Add("/s, ").Add(r.Messages, "F0").Add(" msg/s, display ").AddBytes(r.DisplayBytes).Add("/s");

        t.NewLine("video ");
        if (s.StreamCount == 0) t.Add("no stream");
        else t.Add(s.StreamCount).Add(s.StreamCount == 1 ? " stream" : " streams");
        if (s.StreamCount > OverlaySnapshot.MaxStreams) t.Add(" (").Add(OverlaySnapshot.MaxStreams).Add(" listed)");
        t.Add(", pacing ").Add(Pacing(s.Pacing));
        int listed = Math.Min(s.StreamCount, OverlaySnapshot.MaxStreams);
        for (int i = 0; i < listed; i++)
        {
            var v = s.Streams[i];
            t.NewLine("  #").Add(v.Id).Add(' ').Add(Codec(v.Codec)).Add(' ').Add(Path(v.Path));
            if (v.Width > 0 && v.Height > 0) t.Add(' ').AddSize(v.Width, v.Height);
            t.Add(", ").Add(r.FpsOf(v.Id), "F1").Add(" fps, ").Add(v.Shown).Add(" shown, ").Add(v.Drops).Add(" dropped");
            if (v.Hidden > 0) t.Add(", ").Add(v.Hidden).Add(" hidden");
            if (v.Path == VideoDecodePath.Hardware && s.DecoderNames[i] is { } name) t.NewLine("    ").Add(Cut(name, 60));
        }

        t.NewLine("audio ");
        var a = s.Audio;
        if (!a.Playing) t.Add("idle");
        else
        {
            t.Add(a.Mode switch { AudioDataMode.Opus => "Opus", AudioDataMode.Celt051 => "CELT", _ => "PCM" })
                .Add(' ').Add(a.Rate / 1000.0, "0.#").Add(" kHz ").Add(a.Channels).Add(" ch");
        }
        if (a.LatencyMs > 0) t.Add(", server latency ").Add(a.LatencyMs).Add(" ms");

        t.NewLine("mem   ").AddBytes(s.PrivateBytes).Add(" private, GC heap ").AddBytes(s.GcHeapBytes);
        if (s.LastTrimReason is { } why) t.Add(", trimmed ").Add(Math.Max(0, s.NowMs - s.LastTrimMs) / 1000).Add(" s ago (").Add(why).Add(')');
    }

    static ReadOnlySpan<char> Cut(string s, int max) => s.Length <= max ? s : s.AsSpan(0, max);

    static string Codec(VideoCodecType c) => c switch
    {
        VideoCodecType.Mjpeg => "MJPEG",
        VideoCodecType.Vp8 => "VP8",
        VideoCodecType.H264 => "H.264",
        VideoCodecType.Vp9 => "VP9",
        VideoCodecType.H265 => "HEVC",
        _ => "?",
    };

    static string Path(VideoDecodePath p) => p switch
    {
        VideoDecodePath.Mjpeg => "libjpeg-turbo",
        VideoDecodePath.Hardware => "hardware (MF)",
        VideoDecodePath.Software => "software (FFmpeg)",
        _ => "not decoded",
    };

    static string Pacing(VideoPacing p) => p switch
    {
        VideoPacing.Spacing => "smooth bursts",
        VideoPacing.Clock => "guest clock",
        _ => "off",
    };
}

/// <summary>
/// The overlay of one presenter: its snapshot, rates and text, and the rule for when it refreshes. Used only on the
/// presenter thread. Energy (docs/decisions/0044): a refresh happens only on a wake the presenter has anyway, and it
/// presents only if the text changed. Counters become rates only here, so a static guest costs nothing and its overlay
/// keeps the last values, with the time they were taken.
/// </summary>
sealed class StatusOverlay(IOverlaySource source)
{
    public OverlaySnapshot Snapshot { get; } = new();
    public OverlayRates Rates { get; } = new();
    public OverlayText Text { get; } = new();

    /// <summary>
    /// Whether a presenter wake refreshes the overlay: always when <paramref name="forced"/> (it was just shown, a
    /// non-rate value changed, the layout changed), otherwise only when a guest picture is about to be presented anyway
    /// and the rates are due (at most once per <see cref="OverlayRates.IntervalMs"/>).
    /// </summary>
    public static bool ShouldRefresh(bool forced, bool guestPresenting, bool ratesDue) => forced || (guestPresenting && ratesDue);

    public bool WantsRefresh(long nowMs, bool forced, bool guestPresenting) => ShouldRefresh(forced, guestPresenting, Rates.Due(nowMs));

    long _lastBytes;
    bool _refreshed;

    /// <summary>
    /// The figures were moving at the last refresh, so another refresh in a second would show something new: bytes
    /// arrived since the refresh before (rates still falling towards 0 included), a video stream exists or audio is
    /// playing. The first refresh after the overlay is shown counts as moving. False once a refresh found nothing
    /// changing: that refresh already showed the final, zero rates.
    /// </summary>
    public bool Live { get; private set; }

    /// <summary>
    /// Takes a snapshot (the presenter has filled its own fields), samples the rates when due, formats the text and
    /// returns whether it differs from the text shown.
    /// </summary>
    public bool Refresh(long nowMs, TimeSpan timeOfDay)
    {
        var s = Snapshot;
        s.NowMs = nowMs;
        s.TimeOfDay = timeOfDay;
        source.Fill(s);
        if (Rates.Due(nowMs)) Rates.Sample(s, nowMs);
        Live = !_refreshed || s.NetBytes != _lastBytes || s.StreamCount > 0 || s.Audio.Playing;
        _lastBytes = s.NetBytes;
        _refreshed = true;
        OverlayFormat.Write(s, Rates, Text);
        return Text.Commit();
    }
}

/// <summary>A one-shot timer the overlay's refresh tick is armed on (a fake in tests).</summary>
interface IOverlayTimer
{
    /// <summary>Fires once after <paramref name="ms"/>, replacing any earlier arming.</summary>
    void Arm(int ms);
    void Disarm();
}

/// <summary>
/// The overlay's 1 s refresh tick (docs/decisions/0044, the human's decision of 2026-10-31): armed only while it would
/// show something new, so an idle guest costs no wakeups. The presenter calls <see cref="AfterRefresh"/> after every
/// overlay refresh, whatever caused it (a tick, a guest present, a session event, the overlay being shown), and
/// <see cref="Stop"/> when the overlay is hidden. A tick that fires while the window is minimized or occluded
/// refreshes nothing and so doesn't re-arm; the full redraw on restore re-arms it. Presenter thread only.
/// </summary>
sealed class OverlayTicker(IOverlayTimer timer)
{
    /// <summary>One second, plus a system timer tick so the rates (sampled at most once a second) are due.</summary>
    public const int TickMs = (int)OverlayRates.IntervalMs + 16;

    public bool Armed { get; private set; }

    /// <summary>After a refresh: tick again in a second while the figures move, otherwise stop.</summary>
    public void AfterRefresh(bool live)
    {
        if (live) { timer.Arm(TickMs); Armed = true; }
        else Stop();
    }

    /// <summary>The tick fired (it is one-shot): nothing is armed until the next refresh decides.</summary>
    public void Fired() => Armed = false;

    public void Stop()
    {
        if (!Armed) return;
        timer.Disarm();
        Armed = false;
    }
}

/// <summary><see cref="IOverlayTimer"/> on a thread-pool timer, made once per presenter; arming allocates nothing.</summary>
sealed class ThreadPoolOverlayTimer(Action fire) : IOverlayTimer, IDisposable
{
    readonly Timer _timer = new(static state => ((Action)state!)(), fire, Timeout.Infinite, Timeout.Infinite);
    public void Arm(int ms) => _timer.Change(ms, Timeout.Infinite);
    public void Disarm() => _timer.Change(Timeout.Infinite, Timeout.Infinite);
    public void Dispose() => _timer.Dispose();
}

/// <summary>Blends the overlay (premultiplied BGRA) over guest pixels on the CPU, for the direct path (no shader).</summary>
static class OverlayBlend
{
    /// <summary>
    /// <paramref name="output"/>[i] = <paramref name="overlay"/>[i] + <paramref name="guest"/>[i] × (1 − overlay alpha),
    /// per channel, rounded; opaque. All three spans have the same length.
    /// </summary>
    public static void Row(ReadOnlySpan<uint> overlay, ReadOnlySpan<uint> guest, Span<uint> output)
    {
        for (int i = 0; i < overlay.Length; i++)
        {
            uint o = overlay[i];
            uint a = o >> 24;
            if (a == 255) { output[i] = o; continue; }
            uint g = guest[i];
            if (a == 0) { output[i] = g | 0xFF000000; continue; }
            uint inv = 255 - a;
            uint rb = (g & 0x00FF00FF) * inv + 0x00800080;
            rb = ((rb + ((rb >> 8) & 0x00FF00FF)) >> 8) & 0x00FF00FF;
            uint ga = ((g >> 8) & 0x000000FF) * inv + 0x80;
            ga = ((ga + (ga >> 8)) >> 8) & 0xFF;
            output[i] = ((o & 0x00FFFFFF) + (rb | (ga << 8))) | 0xFF000000;
        }
    }
}
