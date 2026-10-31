using System.Buffers;
using System.Collections.Concurrent;
using Spice.Wire;

namespace Spice.Canvas;

/// <summary>
/// The software compositor: applies display-channel draw commands to in-memory surfaces, following
/// spice-common's canvas_base.c / sw_canvas.c semantics. Not thread-safe by itself: callers hold
/// <see cref="Sync"/> (the session does while applying; the presenter does while reading pixels).
/// </summary>
public sealed class Canvas : IDisposable
{
    /// <summary>Total surface pixel bytes a session may hold at once; a SURFACE_CREATE beyond it is rejected.</summary>
    public const long MaxSurfaceBytes = 512L << 20;
    /// <summary>
    /// Image-cache capacity in BGRA bytes. This is the pixmap_cache_size the session advertises in DISPLAY_INIT
    /// ((80 MiB) / 4 pixels): spice-server accounts the cache in descriptor pixels and invalidates entries before it
    /// would exceed that, so a well-behaved server never trips it. Keep the two in step.
    /// </summary>
    public const long ImageCacheBytes = 80L << 20;
    /// <summary>Cached images kept at once, bounding per-entry overhead for tiny images.</summary>
    public const int MaxCachedImages = 1 << 17;
    /// <summary>Largest width or height of a video stream destination.</summary>
    public const int MaxStreamSide = Surface.MaxSide;
    /// <summary>Per-command work ceilings (pixels touched plus bookkeeping) for strokes and text.</summary>
    const long MaxStrokeWork = 1 << 24, MaxGlyphWork = 1 << 26;
    /// <summary>Per-command ceiling on clip normalisation work (sum of active rectangles over all bands).</summary>
    const long MaxClipWork = 1 << 22;

    readonly Dictionary<uint, Surface> _surfaces = [];
    readonly Dictionary<ulong, DecodedImage> _cache = [];
    readonly List<DecodedImage> _temps = [];
    readonly ImageDecoder _decoder = new();
    long _surfaceBytes, _cacheBytes;

    public object Sync { get; } = new();
    public Surface? Primary { get; private set; }
    /// <summary>
    /// The part of <see cref="Primary"/> the guest shows as its monitor, in surface pixels: the first head on surface 0 of
    /// the last DISPLAY_MONITORS_CONFIG, clamped to the surface, or the whole surface when there is none (e.g. a qxl
    /// surface of 640x512 behind a 640x480 mode). A primary surface recreated at the same size keeps the last head.
    /// With <see cref="InferPaddedMonitor"/> and a Linux agent, a whole-surface area can be narrowed to the guest's own full-frame draws
    /// (a qxl KMS guest whose config spice-server reset to the surface). Empty while there is no primary surface. Read
    /// under <see cref="Sync"/>. See docs/decisions/0035.
    /// </summary>
    public SpiceRect PrimaryArea { get; private set; }
    /// <summary>
    /// Whether the guest's agent is a Linux one (it announces LF line endings; a Windows agent never does), set by the
    /// session. A monitor that no config names is inferred only for such a guest, and only while
    /// <see cref="InferPaddedMonitor"/> is on. Any thread.
    /// </summary>
    public bool GuestAgentIsLinux { get => Volatile.Read(ref _agentLinux); set => Volatile.Write(ref _agentLinux, value); }
    bool _agentLinux;
    /// <summary>
    /// The user's setting "Trim Linux guest padding" (on by default): whether a monitor that no config names may be
    /// inferred from the guest's draws, for a guest with <see cref="GuestAgentIsLinux"/>. Only a draw after the
    /// surface's MARK that starts at the origin, is drawn whole (no clip, or one clip rectangle equal to its box), holds
    /// every guest draw since the MARK and leaves fewer than <see cref="MaxInferredPadding"/> rows and columns of the
    /// surface uncovered narrows the area, and only while the area is the whole surface; a later draw outside it gives
    /// the whole surface back for the rest of that surface's life. Turning it off gives the configs' area back at once
    /// (and announces it); turning it on takes effect at the guest's next qualifying draw. Any thread.
    /// Finding L16 (docs/verification/2026-10-31-ubuntu-monitor-area.md, docs/decisions/0035).
    /// </summary>
    public bool InferPaddedMonitor
    {
        get => Volatile.Read(ref _inferPadded);
        set
        {
            lock (Sync)
            {
                if (Volatile.Read(ref _inferPadded) == value) return;
                Volatile.Write(ref _inferPadded, value);
                if (value) return;
                // Nothing inferred may stay: not the shown area, nor one kept for a recreated surface (whose configured
                // head, ID included, takes its place: review MI4).
                if (_keptHeadInferred)
                {
                    _hasKeptHead = _keptHasConfigHead;
                    _keptHead = _keptConfigHead;
                    _keptHeadInferred = false;
                }
                if (!_headInferred || Primary is not { } p) { _headInferred = false; return; }
                _headInferred = false;
                _hasHead = _hasConfigHead;
                _head = _configHead;
                UpdatePrimaryArea();
                Log.Info($"canvas: monitor area {PrimaryArea} of the {p.Width}x{p.Height} primary surface: trimming Linux guest padding was turned off");
                PrimaryChanged?.Invoke(p);
            }
        }
    }
    bool _inferPadded = true;
    bool InferenceEnabled => GuestAgentIsLinux && InferPaddedMonitor;
    /// <summary>
    /// Padding the qxl KMS driver's surfaces have shown (640x480 on 640x512, 1600x900 on 1600x960) stays below this.
    /// </summary>
    public const int MaxInferredPadding = 64;
    /// <summary>
    /// The monitor ID of the head behind <see cref="PrimaryArea"/>, or 0 when the whole surface is shown: the display ID
    /// absolute mouse positions carry, as spice-gtk sends its shown monitor's ID (a sole head can be #1 after the server
    /// drops an empty #0). Any thread.
    /// </summary>
    public uint PrimaryMonitorId => (uint)Volatile.Read(ref _monitorId);
    /// <summary>
    /// Whether <see cref="PrimaryArea"/> comes from a monitors config, rather than being the provisional whole surface
    /// shown until one arrives after a new primary surface (or, with <see cref="InferPaddedMonitor"/>, until the guest's
    /// draws narrow a whole-surface area). Read under <see cref="Sync"/>.
    /// </summary>
    public bool PrimaryAreaIsMonitor => Primary is { } p && _hasHead && !(InferenceEnabled && InferenceOpen(p));
    int _monitorId;
    // The head behind PrimaryArea, as sent (a lone head moved to the origin). When the primary surface goes, it is kept
    // with that surface's size until the next primary surface: one of the same size gets it back (a guest waking its
    // display sends no new config), one of another size doesn't (a Windows guest growing its mode sends none either,
    // and the old head would crop the new mode). See docs/decisions/0035.
    bool _hasHead;
    MonitorHead _head;
    bool _hasKeptHead;
    MonitorHead _keptHead;
    int _keptWidth, _keptHeight;
    // An inferred head (_head came from the guest's draws, not a config), what the configs said before it (given back
    // if the guest draws outside it), whether the primary surface has had its MARK (draws before it are spice-server's
    // image of the whole surface, not the guest's), and whether inference is over for this surface.
    bool _headInferred, _keptHeadInferred;
    bool _hasConfigHead, _keptHasConfigHead;
    MonitorHead _configHead, _keptConfigHead;
    bool _primaryMarked, _inferenceClosed, _configSeen;
    // How far right and down the guest has drawn on the primary surface since its MARK.
    int _drawnRight, _drawnBottom;

    /// <summary>The area is the whole surface and the guest's draws may still narrow it.</summary>
    bool InferenceOpen(Surface p) => !_inferenceClosed && !_headInferred && PrimaryArea == p.Bounds;

    /// <summary>Before the primary surface goes: remembers its head and size for the next primary surface.</summary>
    void KeepHead()
    {
        if (Primary is not { } p) return; // nothing shown: keep what an earlier destroy kept
        _hasKeptHead = _hasHead;
        _keptHead = _head;
        _keptHeadInferred = _hasHead && _headInferred;
        // The configs' head behind an inferred one, so a recreated surface can still fall back to it (review MI4).
        _keptHasConfigHead = _hasConfigHead;
        _keptConfigHead = _configHead;
        _keptWidth = p.Width; _keptHeight = p.Height;
    }
    public Dictionary<string, int> Stats { get; } = [];
    /// <summary>Count of commands that could not be applied (bad data or unsupported features).</summary>
    public int Errors { get; private set; }

    /// <summary>
    /// Raised (under <see cref="Sync"/>) when a primary surface is created or destroyed, or when its shown area
    /// (<see cref="PrimaryArea"/>) changes.
    /// </summary>
    public event Action<Surface?>? PrimaryChanged;
    /// <summary>Raised (under <see cref="Sync"/>) for each primary-surface rectangle a command changed.</summary>
    public event Action<SpiceRect>? Invalidated;
    /// <summary>
    /// Raised (under <see cref="Sync"/>) when a COPY_BITS on the primary surface moved one whole rectangle: <c>dest</c>
    /// now holds the pixels that were at <c>dest</c> offset by (<c>dx</c>, <c>dy</c>), all inside the surface.
    /// <see cref="Invalidated"/> follows for the same rectangle. Lets a presenter pass the move on as a scroll.
    /// </summary>
    public event Action<SpiceRect, int, int>? CopiedBits;

    public void Dispose()
    {
        ClearCache();
        _decoder.Dispose();
    }

    /// <summary>Fast JPEG decoding for JPEG and JPEG_ALPHA images (<see cref="ImageDecoder.FastJpeg"/>). Any thread.</summary>
    public bool FastJpeg { get => _decoder.FastJpeg; set => _decoder.FastJpeg = value; }

    /// <summary>
    /// Frees the GLZ window's reuse pool; the images later GLZ images may reference stay. Any thread (the window
    /// serializes with decoding). Returns the bytes freed.
    /// </summary>
    public long TrimGlzPool() => _decoder.TrimGlzPool();

    void Count(string key) => Stats[key] = Stats.GetValueOrDefault(key) + 1;

    // Statistics keys built once, so counting a draw or an ignored message never formats a string.
    static readonly string[] s_imageKeys = Enumerable.Range(0, 256).Select(i => $"image_{(ImageType)i}").ToArray();
    static readonly ConcurrentDictionary<ushort, string> s_ignoredKeys = new();

    void Error(string message)
    {
        Errors++;
        Count("error");
        Log.Warn($"canvas: {message}");
    }

    /// <summary>Applies one display message. Malformed or unsupported commands are logged and skipped.</summary>
    public void Apply(ushort type, ReadOnlyMemory<byte> payload)
    {
        lock (Sync)
        {
            try { ApplyCore(type, payload); }
            catch (Exception e) when (e is CanvasException or WireException)
            {
                Error($"message {type}: {e.Message}");
            }
            finally
            {
                foreach (var t in _temps) if (!t.IsCached) t.Release();
                _temps.Clear();
            }
        }
    }

    void ApplyCore(ushort type, ReadOnlyMemory<byte> payload)
    {
        switch (type)
        {
            case DisplayMsg.SurfaceCreate:
            {
                var sc = SurfaceCreate.Parse(payload);
                long bytes = (long)sc.Width * sc.Height * 4;
                _surfaces.TryGetValue(sc.SurfaceId, out var old);
                long remaining = _surfaceBytes - (old is null ? 0 : (long)old.Stride * old.Height);
                if (remaining + bytes > MaxSurfaceBytes)
                    throw new CanvasException($"surface {sc.SurfaceId} ({sc.Width}x{sc.Height}) exceeds the {MaxSurfaceBytes >> 20} MiB surface budget");
                var s = new Surface(sc.SurfaceId, (int)sc.Width, (int)sc.Height, sc.Format, sc.IsPrimary);
                _surfaces[sc.SurfaceId] = s;
                _surfaceBytes = remaining + bytes;
                Count("surface_create");
                if (s.IsPrimary)
                {
                    KeepHead();
                    Primary = s;
                    // A primary surface recreated at the size the last head was shown on (a guest waking its display
                    // sends SURFACE_CREATE and draws, but no MONITORS_CONFIG) gets that head back; any other size starts
                    // from the whole surface until its config arrives (docs/decisions/0035).
                    bool sameSize = _keptWidth == s.Width && _keptHeight == s.Height;
                    _hasHead = _hasKeptHead && sameSize;
                    _head = _keptHead;
                    _headInferred = _hasHead && _keptHeadInferred;
                    // The configured head (behind an inferred one, or the shown one) comes back with it, ID included.
                    _hasConfigHead = _headInferred ? _keptHasConfigHead : _hasHead;
                    _configHead = _headInferred ? _keptConfigHead : _head;
                    // An inferred head comes back only while inference is still allowed; otherwise its configured one.
                    if (_headInferred && !InferenceEnabled)
                    {
                        _headInferred = false;
                        _hasHead = _hasConfigHead;
                        _head = _configHead;
                    }
                    _hasKeptHead = false;
                    _configSeen = false;
                    _primaryMarked = _inferenceClosed = false;
                    _drawnRight = _drawnBottom = 0;
                    PrimaryArea = _hasHead ? AreaOf(_head, s.Width, s.Height) : s.Bounds;
                    Volatile.Write(ref _monitorId, _hasHead ? (int)_head.Id : 0);
                    // Logged for every new primary surface (connect, mode change, wake), so the log tells where the
                    // shown area came from.
                    Log.Info(_hasHead
                        ? $"canvas: monitor area {PrimaryArea} kept{(_headInferred ? " (inferred from draws)" : "")} for the recreated {s.Width}x{s.Height} primary surface"
                        : $"canvas: monitor area {PrimaryArea}, the whole new {s.Width}x{s.Height} primary surface until a monitors config names the monitor");
                    PrimaryChanged?.Invoke(s);
                    Invalidated?.Invoke(s.Bounds);
                }
                else if (old is not null && old == Primary) ClearPrimary();
                return;
            }
            case DisplayMsg.SurfaceDestroy:
            {
                uint id = new SpiceReader(payload).U32();
                if (_surfaces.Remove(id, out var s))
                {
                    _surfaceBytes -= (long)s.Stride * s.Height;
                    if (s == Primary) ClearPrimary();
                }
                return;
            }
            case DisplayMsg.MonitorsConfig:
            {
                // Parsed before anything changes: a malformed message keeps the current area.
                bool has = DisplayParse.TryPrimaryHead(payload, out var head, out int count);
                // spice-gtk shows a lone head from the surface's origin, whatever its x/y; its position only places it
                // among other heads on the same surface.
                var h = count == 1 ? head with { X = 0, Y = 0 } : head;
                // The same config again (spice-server repeats it) is applied silently.
                bool repeat = _configSeen && has == _hasConfigHead && (!has || h == _configHead);
                _configSeen = true;
                _hasConfigHead = has;
                _configHead = h;
                if (Primary is not { } p)
                {
                    // Nothing to show it on; a new primary surface starts from its kept head.
                    _head = h; _hasHead = has;
                    return;
                }
                // A head naming the whole surface is what spice-server substitutes when a guest creates its primary
                // surface without sending a new config (a qxl KMS guest waking its display, finding L16): it doesn't
                // undo an area inferred from the guest's own draws.
                if (_headInferred && (!has || AreaOf(h, p.Width, p.Height) == p.Bounds))
                {
                    if (!repeat)
                        Log.Info($"canvas: monitors config {DescribeHead(has, h, count)} names the whole {p.Width}x{p.Height} surface; keeping the monitor area {PrimaryArea} inferred from the guest's draws");
                    return;
                }
                _head = h;
                _hasHead = has;
                _headInferred = false;
                bool changed = UpdatePrimaryArea();
                if (changed || !repeat)
                    Log.Info($"canvas: monitor area {PrimaryArea} of the {p.Width}x{p.Height} primary surface{(changed ? "" : ", unchanged")}, from monitors config {DescribeHead(has, h, count)}" +
                        (InferenceEnabled && InferenceOpen(p) ? "; it names the whole surface, so the guest's first full-frame draw may narrow it" : ""));
                if (changed) PrimaryChanged?.Invoke(p);
                return;
            }
            case DisplayMsg.Mark:
                // Draws before the MARK are spice-server's image of the whole surface; the guest's own draws follow it.
                if (Primary is not null) _primaryMarked = true;
                return;
            case DisplayMsg.Mode:
                return;
            case DisplayMsg.Reset:
                ClearCache();
                _decoder.ClearPalettes();
                _decoder.ClearGlzWindow();
                return;
            case DisplayMsg.InvalAllPixmaps:
                ClearCache();
                return;
            case DisplayMsg.InvalList:
                foreach (var (_, id) in DisplayParse.InvalList(payload)) RemoveCached(id);
                return;
            case DisplayMsg.InvalPalette:
                _decoder.RemovePalette(new SpiceReader(payload).U64());
                return;
            case DisplayMsg.InvalAllPalettes:
                _decoder.ClearPalettes();
                return;
            case DisplayMsg.CopyBits or DisplayMsg.DrawFill or DisplayMsg.DrawOpaque or DisplayMsg.DrawCopy or DisplayMsg.DrawBlend
                or DisplayMsg.DrawBlackness or DisplayMsg.DrawWhiteness or DisplayMsg.DrawInvers or DisplayMsg.DrawRop3
                or DisplayMsg.DrawStroke or DisplayMsg.DrawText or DisplayMsg.DrawTransparent or DisplayMsg.DrawAlphaBlend:
                Draw(DrawCommand.Parse(type, payload));
                return;
            default:
                Count(s_ignoredKeys.GetOrAdd(type, static t => $"ignored_{t}"));
                return;
        }
    }

    void ClearPrimary()
    {
        KeepHead();
        Log.Info($"canvas: monitor area cleared: the {Primary!.Width}x{Primary.Height} primary surface is gone");
        Primary = null;
        _hasHead = false;
        _headInferred = false;
        PrimaryArea = default;
        Volatile.Write(ref _monitorId, 0);
        PrimaryChanged?.Invoke(null);
    }

    /// <summary>
    /// Recomputes <see cref="PrimaryArea"/> from the current head. Returns whether it changed; the caller logs and then
    /// announces it like a new surface (<see cref="PrimaryChanged"/>).
    /// </summary>
    bool UpdatePrimaryArea()
    {
        if (Primary is not { } p) return false;
        var area = _hasHead ? AreaOf(_head, p.Width, p.Height) : p.Bounds;
        // The ID can change without the area (head #1 replacing head #0 at the same size).
        Volatile.Write(ref _monitorId, _hasHead ? (int)_head.Id : 0);
        if (area == PrimaryArea) return false;
        PrimaryArea = area;
        return true;
    }

    /// <summary>No clip, or an original clip of exactly one rectangle equal to the box (before any normalisation).</summary>
    static bool WholeFrameClip(in DisplayBase b) =>
        b.Clip.Type == SpiceClip.TypeNone || (b.Clip.Type == SpiceClip.TypeRects && b.Clip.Count == 1 && b.Clip[0] == b.Box);

    static string DescribeHead(bool has, in MonitorHead h, int count) =>
        has ? $"head #{h.Id} {h.Width}x{h.Height} at ({h.X},{h.Y}) of {count}" : $"with no head on surface 0 ({count} heads)";

    /// <summary>
    /// A guest draw on the primary surface, after its MARK (<see cref="InferPaddedMonitor"/>, finding L16). The qxl KMS
    /// driver draws every frame as one copy of its whole framebuffer, the mode, onto a primary surface that can be
    /// taller (1600x900 on 1600x960); when no config names that mode, the first such draw that holds every guest draw so
    /// far becomes the area. A draw outside an inferred area gives the configs' area back for the rest of the surface's
    /// life. Value arithmetic only: this runs for every draw.
    /// </summary>
    void NoteGuestDraw(Surface p, in SpiceRect box, in SpiceRect visible, bool candidate)
    {
        if (visible.Right > _drawnRight) _drawnRight = visible.Right;
        if (visible.Bottom > _drawnBottom) _drawnBottom = visible.Bottom;
        if (_inferenceClosed || !InferenceEnabled) return;
        if (_headInferred)
        {
            var a = PrimaryArea;
            if (visible.Left >= a.Left && visible.Top >= a.Top && visible.Right <= a.Right && visible.Bottom <= a.Bottom) return;
            _inferenceClosed = true;
            _headInferred = false;
            _hasHead = _hasConfigHead;
            _head = _configHead;
            UpdatePrimaryArea();
            Log.Info($"canvas: monitor area {PrimaryArea} of the {p.Width}x{p.Height} primary surface: the guest drew {visible}, outside the area inferred from its draws, so none is inferred again for this surface");
            PrimaryChanged?.Invoke(p);
            return;
        }
        // Only a draw command can be a candidate: the qxl frame comes with no clip or with an original clip of exactly
        // one rectangle, the box itself (as captured live; review MI2). Video only counts as a draw.
        if (!candidate || PrimaryArea != p.Bounds || box != visible || box.Left != 0 || box.Top != 0) return;
        int padW = p.Width - box.Right, padH = p.Height - box.Bottom;
        if (padW < 0 || padH < 0 || padW >= MaxInferredPadding || padH >= MaxInferredPadding || (padW | padH) == 0) return;
        // An earlier draw lies outside this candidate: reject it, but a later, larger frame may still qualify (MI3).
        if (_drawnRight > box.Right || _drawnBottom > box.Bottom) return;
        _head = new MonitorHead(_hasConfigHead ? _configHead.Id : 0, 0, (uint)box.Right, (uint)box.Bottom, 0, 0, 0);
        _hasHead = true;
        _headInferred = true;
        UpdatePrimaryArea();
        Log.Info($"canvas: monitor area {PrimaryArea} of the {p.Width}x{p.Height} primary surface, inferred from the guest's full-frame draw: no monitors config names a smaller monitor (spice-server names the whole surface after a guest recreates it)");
        PrimaryChanged?.Invoke(p);
    }

    /// <summary>
    /// A head's rectangle clamped to a <paramref name="width"/>×<paramref name="height"/> surface (spice-gtk intersects
    /// the same way). A head entirely outside the surface leaves the whole surface shown.
    /// </summary>
    public static SpiceRect AreaOf(in MonitorHead head, int width, int height)
    {
        long l = Math.Min(head.X, (uint)width), t = Math.Min(head.Y, (uint)height);
        long r = Math.Min((long)head.X + head.Width, width), b = Math.Min((long)head.Y + head.Height, height);
        var area = new SpiceRect((int)l, (int)t, (int)r, (int)b);
        return area.IsEmpty ? new SpiceRect(0, 0, width, height) : area;
    }

    void ClearCache()
    {
        foreach (var img in _cache.Values) img.Release();
        _cache.Clear();
        _cacheBytes = 0;
    }

    void RemoveCached(ulong id)
    {
        if (!_cache.Remove(id, out var img)) return;
        _cacheBytes -= (long)img.Stride * img.Height;
        img.Release();
    }

    Surface GetSurface(uint id) => _surfaces.TryGetValue(id, out var s) ? s : throw new CanvasException($"no surface {id}");

    /// <summary>Resolves an image to pixels: cache lookups, surface-as-image, or decoding (then caching if asked).</summary>
    ImageView Resolve(in SpiceImage? maybe, uint drawingSurface)
    {
        if (maybe is not SpiceImage image) throw new CanvasException("missing source image");
        Count(s_imageKeys[(byte)image.Type]);
        switch (image.Type)
        {
            case ImageType.FromCache or ImageType.FromCacheLossless:
                return _cache.TryGetValue(image.Descriptor.Id, out var cached)
                    ? cached.View
                    : throw new CanvasException($"image {image.Descriptor.Id:x} not in cache");
            case ImageType.Surface:
            {
                var s = GetSurface(image.SurfaceId);
                if (s.Id != drawingSurface) return s.View;
                // Reading and writing the same surface: snapshot it first.
                var copy = DecodedImage.SnapshotOf(s);
                _temps.Add(copy);
                return copy.View;
            }
        }
        var img = _decoder.Decode(image);
        if ((image.Descriptor.Flags & (ImageFlags.CacheMe | ImageFlags.CacheReplaceMe)) != 0)
        {
            RemoveCached(image.Descriptor.Id);
            long bytes = (long)img.Stride * img.Height;
            if (_cache.Count >= MaxCachedImages || _cacheBytes + bytes > ImageCacheBytes)
            {
                // The server promised to stay within the advertised cache; refuse rather than evict silently.
                // The image is still drawn; later FROM_CACHE references to it fail visibly.
                Error($"image {image.Descriptor.Id:x} not cached: cache budget exceeded ({_cache.Count} images, {_cacheBytes} bytes)");
                _temps.Add(img);
                return img.View;
            }
            img.IsCached = true;
            _cache[image.Descriptor.Id] = img;
            _cacheBytes += bytes;
        }
        else _temps.Add(img);
        return img.View;
    }

    PixelSource BrushSource(in SpiceBrush brush, uint surface) => brush.Type switch
    {
        // canvas_base.c's draw_brush paints black for BRUSH_TYPE_NONE.
        BrushType.None => PixelSource.Solid(0),
        BrushType.Solid => PixelSource.Solid(brush.Color),
        _ => PixelSource.Pattern(Resolve(brush.Pattern, surface), brush.PatternPos.X, brush.PatternPos.Y),
    };

    /// <summary>
    /// A QMask bitmap as coverage. Mask pixel for surface (x,y) is (x - box.left + pos.x, y - box.top + pos.y).
    /// Only the part inside <paramref name="visible"/> is materialised; pixels outside the mask are uncovered.
    /// </summary>
    CoverageMask? ResolveMask(in SpiceQMask mask, SpiceRect box, SpiceRect visible, uint surface)
    {
        if (mask.Bitmap is not SpiceImage bmp) return null;
        bool invert = (mask.Flags & SpiceQMask.FlagInvers) != 0;
        long ox = (long)box.Left - mask.Pos.X, oy = (long)box.Top - mask.Pos.Y;
        if (bmp.Type == ImageType.Bitmap && bmp.BitmapFormat is BitmapFormat.A1Le or BitmapFormat.A1Be)
        {
            // 1-bit masks carry no palette (canvas_get_bitmap_mask): bit set = covered.
            int w = (int)bmp.BitmapWidth, h = (int)bmp.BitmapHeight;
            long stride = bmp.Stride;
            if (w <= 0 || h <= 0 || stride < (w + 7) / 8 || bmp.Data.Length < stride * h) throw new CanvasException("mask bitmap too short");
            var m = VisibleMask(ox, oy, w, h, visible);
            if (m.Width == 0) return m;
            bool topDown = (bmp.BitmapFlags & BitmapFlags.TopDown) != 0, be = bmp.BitmapFormat == BitmapFormat.A1Be;
            var data = bmp.Data.Span;
            for (int y = 0; y < m.Height; y++)
            {
                int my = (int)(m.OriginY + y - oy);
                var row = data.Slice((int)((topDown ? my : h - 1 - my) * stride), (int)stride);
                var cov = m.Coverage.AsSpan(y * m.Width, m.Width);
                for (int x = 0; x < m.Width; x++)
                {
                    int mx = (int)(m.OriginX + x - ox);
                    int bit = be ? (row[mx >> 3] >> (7 - (mx & 7))) & 1 : (row[mx >> 3] >> (mx & 7)) & 1;
                    cov[x] = (bit != 0) != invert ? (byte)255 : (byte)0;
                }
            }
            return m;
        }
        var img = Resolve(bmp, surface);
        var cm = VisibleMask(ox, oy, img.Width, img.Height, visible);
        for (int y = 0; y < cm.Height; y++)
        {
            var row = img.Row((int)(cm.OriginY + y - oy));
            var cov = cm.Coverage.AsSpan(y * cm.Width, cm.Width);
            int sx = (int)(cm.OriginX - ox);
            for (int x = 0; x < cm.Width; x++) cov[x] = ((row[sx + x] & 0xFFFFFF) != 0) != invert ? (byte)255 : (byte)0;
        }
        return cm;
    }

    /// <summary>An empty mask covering the intersection of a w×h mask at (ox, oy) with <paramref name="visible"/>.</summary>
    static CoverageMask VisibleMask(long ox, long oy, int w, int h, SpiceRect visible)
    {
        long l = Math.Max(ox, visible.Left), t = Math.Max(oy, visible.Top);
        long r = Math.Min(ox + w, visible.Right), b = Math.Min(oy + h, visible.Bottom);
        return l < r && t < b ? CoverageMask.Rent((int)(r - l), (int)(b - t), (int)l, (int)t) : CoverageMask.Rent(0, 0, 0, 0);
    }

    static SpiceRect Bounds(ReadOnlySpan<SpiceRect> rects)
    {
        var u = default(SpiceRect);
        foreach (var r in rects) u = u.Union(r);
        return u;
    }

    /// <summary>
    /// The draw box clipped to the surface and to the clip list, as non-overlapping rectangles (upstream clips with a
    /// pixman region, so each covered pixel is drawn once). Results live in <paramref name="stack"/> or, when they
    /// don't fit, in <paramref name="rented"/> (from <see cref="ArrayPool{T}"/>; the caller returns it).
    /// </summary>
    static int ClipRects(in DisplayBase b, Surface s, Span<SpiceRect> stack, out SpiceRect[]? rented)
    {
        rented = null;
        var box = b.Box.Intersect(s.Bounds);
        if (box.IsEmpty) return 0;
        if (b.Clip.Type == SpiceClip.TypeNone) { stack[0] = box; return 1; }
        int n = b.Clip.Count;
        var into = stack;
        if (n > into.Length) { rented = ArrayPool<SpiceRect>.Shared.Rent(n); into = rented; }
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            var r = b.Clip[i].Intersect(box);
            if (!r.IsEmpty) into[k++] = r;
        }
        if (k <= 1 || (k <= 32 && Disjoint(into[..k]))) return k;
        return Normalize(ref rented, into[..k]);
    }

    static bool Disjoint(ReadOnlySpan<SpiceRect> rects)
    {
        for (int i = 1; i < rects.Length; i++)
            for (int j = 0; j < i; j++)
                if (!rects[i].Intersect(rects[j]).IsEmpty) return false;
        return true;
    }

    /// <summary>
    /// Rewrites overlapping rectangles as their union in y-x bands (pixman's region form). Output goes to a pooled
    /// array assigned to <paramref name="rented"/> (any previous one is returned to the pool). Rarely used: servers
    /// send region-derived, already disjoint clip lists.
    /// </summary>
    static int Normalize(ref SpiceRect[]? rented, Span<SpiceRect> input)
    {
        int k = input.Length;
        var pool = ArrayPool<SpiceRect>.Shared;
        var rects = pool.Rent(k);
        var ys = ArrayPool<int>.Shared.Rent(2 * k);
        var active = ArrayPool<int>.Shared.Rent(k);
        var spans = ArrayPool<(int L, int R)>.Shared.Rent(k);
        var output = pool.Rent(Math.Max(16, k));
        try
        {
            input.CopyTo(rects);
            var sorted = rects.AsSpan(0, k);
            sorted.Sort(static (p, q) => p.Top.CompareTo(q.Top));
            for (int i = 0; i < k; i++) { ys[2 * i] = sorted[i].Top; ys[2 * i + 1] = sorted[i].Bottom; }
            var edges = ys.AsSpan(0, 2 * k);
            edges.Sort();
            int m = 0;
            for (int i = 0; i < edges.Length; i++) if (m == 0 || edges[m - 1] != edges[i]) edges[m++] = edges[i];

            int next = 0, activeCount = 0, count = 0, prevStart = 0, prevCount = 0;
            long work = 0;
            for (int e = 0; e + 1 < m; e++)
            {
                int y0 = edges[e], y1 = edges[e + 1];
                // Drop rects that ended, add those that start here.
                int keep = 0;
                for (int i = 0; i < activeCount; i++) if (sorted[active[i]].Bottom > y0) active[keep++] = active[i];
                activeCount = keep;
                while (next < k && sorted[next].Top <= y0) active[activeCount++] = next++;
                work += activeCount + 1;
                if (work > MaxClipWork) throw new CanvasException($"clip list of {k} rectangles is too complex");
                if (activeCount == 0) { prevCount = 0; continue; }

                for (int i = 0; i < activeCount; i++) spans[i] = (sorted[active[i]].Left, sorted[active[i]].Right);
                var band = spans.AsSpan(0, activeCount);
                band.Sort(static (p, q) => p.L.CompareTo(q.L));
                int merged = 0;
                for (int i = 0; i < band.Length; i++)
                {
                    if (merged > 0 && band[i].L <= band[merged - 1].R) band[merged - 1].R = Math.Max(band[merged - 1].R, band[i].R);
                    else band[merged++] = band[i];
                }
                // Extend the previous band's rects when this band continues them exactly.
                bool same = prevCount == merged && output[prevStart].Bottom == y0;
                for (int i = 0; same && i < merged; i++) same = output[prevStart + i].Left == band[i].L && output[prevStart + i].Right == band[i].R;
                if (same)
                {
                    for (int i = 0; i < merged; i++) output[prevStart + i] = output[prevStart + i] with { Bottom = y1 };
                    continue;
                }
                if (count + merged > output.Length)
                {
                    var bigger = pool.Rent(Math.Max(output.Length * 2, count + merged));
                    output.AsSpan(0, count).CopyTo(bigger);
                    pool.Return(output);
                    output = bigger;
                }
                prevStart = count; prevCount = merged;
                for (int i = 0; i < merged; i++) output[count++] = new SpiceRect(band[i].L, y0, band[i].R, y1);
            }
            if (rented is not null) pool.Return(rented);
            rented = output;
            output = null!;
            return count;
        }
        finally
        {
            pool.Return(rects);
            ArrayPool<int>.Shared.Return(ys);
            ArrayPool<int>.Shared.Return(active);
            ArrayPool<(int, int)>.Shared.Return(spans);
            if (output is not null) pool.Return(output);
        }
    }

    void Draw(in DrawCommand c)
    {
        var s = GetSurface(c.Base.SurfaceId);
        Span<SpiceRect> stack = stackalloc SpiceRect[16];
        int n = ClipRects(c.Base, s, stack, out var rented);
        try { Draw(c, s, rented is null ? stack[..n] : rented.AsSpan(0, n)); }
        finally { if (rented is not null) ArrayPool<SpiceRect>.Shared.Return(rented); }
    }

    void Draw(in DrawCommand c, Surface s, ReadOnlySpan<SpiceRect> rects)
    {
        Count(c.Kind switch
        {
            DisplayMsg.CopyBits => "copy_bits", DisplayMsg.DrawFill => "fill", DisplayMsg.DrawOpaque => "opaque",
            DisplayMsg.DrawCopy => "copy", DisplayMsg.DrawBlend => "blend", DisplayMsg.DrawBlackness => "blackness",
            DisplayMsg.DrawWhiteness => "whiteness", DisplayMsg.DrawInvers => "invers", DisplayMsg.DrawRop3 => "rop3",
            DisplayMsg.DrawStroke => "stroke", DisplayMsg.DrawText => "text", DisplayMsg.DrawTransparent => "transparent",
            _ => "alpha_blend",
        });
        if (rects.IsEmpty) return;
        var box = c.Base.Box;
        var visible = Bounds(rects);
        if (_primaryMarked && ReferenceEquals(s, Primary)) NoteGuestDraw(s, box, visible, WholeFrameClip(c.Base));

        switch (c.Kind)
        {
            case DisplayMsg.CopyBits:
            {
                int dx = c.SourcePos.X - box.Left, dy = c.SourcePos.Y - box.Top;
                if (rects.Length == 1)
                {
                    Raster.CopyBits(s, rects[0], dx, dy);
                    // Rects are clipped to the surface; report a scroll only when the source is inside it too.
                    var source = rects[0].Offset(dx, dy);
                    if (s.IsPrimary && source.Intersect(s.Bounds) == source) CopiedBits?.Invoke(rects[0], dx, dy);
                }
                else
                {
                    // Several clip rects: copy from a snapshot so one rect's write never feeds another's read.
                    var snap = DecodedImage.SnapshotOf(s);
                    _temps.Add(snap);
                    foreach (var r in rects) Raster.Copy(s, r, snap.View, dx, dy);
                }
                break;
            }
            case DisplayMsg.DrawFill:
            {
                var src = BrushSource(c.Brush, s.Id);
                using var mask = ResolveMask(c.Mask, box, visible, s.Id);
                foreach (var r in rects) Raster.Draw(s, r, src, c.RopDescriptor, mask);
                break;
            }
            case DisplayMsg.DrawCopy or DisplayMsg.DrawBlend:
            {
                var (img, dx, dy) = SourceFor(c, s, visible);
                using var mask = ResolveMask(c.Mask, box, visible, s.Id);
                var src = PixelSource.FromImage(img, dx, dy);
                foreach (var r in rects) Raster.Draw(s, r, src, c.RopDescriptor, mask);
                break;
            }
            case DisplayMsg.DrawOpaque:
            {
                var (img, dx, dy) = SourceFor(c, s, visible);
                var brush = BrushSource(c.Brush, s.Id);
                using var mask = ResolveMask(c.Mask, box, visible, s.Id);
                // canvas_base.c combines (brush, src): INVERS_SRC inverts the bitmap, INVERS_BRUSH the brush.
                ushort rop = c.RopDescriptor;
                ushort brushRop = (ushort)(rop & ~(Ropd.InversSrc | Ropd.InversDest));
                if ((rop & Ropd.InversSrc) != 0) brushRop |= Ropd.InversDest;
                var srcPx = PixelSource.FromImage(img, dx, dy);
                foreach (var r0 in rects)
                {
                    var r = r0;
                    for (int y = r.Top; y < r.Bottom; y++)
                    {
                        var row = s.Row(y);
                        for (int x = r.Left; x < r.Right; x++)
                        {
                            if (mask is not null && !mask.Covers(x, y)) continue;
                            if (!srcPx.TrySample(x, y, out uint sp) || !brush.TrySample(x, y, out uint bp)) continue;
                            row[x] = Raster.RopCombine(sp, bp, brushRop) | 0xFF000000;
                        }
                    }
                }
                break;
            }
            case DisplayMsg.DrawBlackness or DisplayMsg.DrawWhiteness or DisplayMsg.DrawInvers:
            {
                using var mask = ResolveMask(c.Mask, box, visible, s.Id);
                ushort rop = c.Kind == DisplayMsg.DrawInvers ? Ropd.OpInvers : Ropd.OpPut;
                var src = PixelSource.Solid(c.Kind == DisplayMsg.DrawWhiteness ? 0xFFFFFFu : 0u);
                foreach (var r in rects) Raster.Draw(s, r, src, rop, mask);
                break;
            }
            case DisplayMsg.DrawRop3:
            {
                var (img, dx, dy) = SourceFor(c, s, visible);
                var brush = BrushSource(c.Brush, s.Id);
                using var mask = ResolveMask(c.Mask, box, visible, s.Id);
                foreach (var r in rects) Raster.DrawRop3(s, r, img, dx, dy, brush, c.Rop3, mask);
                break;
            }
            case DisplayMsg.DrawTransparent:
            {
                var (img, dx, dy) = SourceFor(c, s, visible, forceNearest: true);
                foreach (var r in rects) Raster.DrawTransparent(s, r, img, dx, dy, c.TrueColor);
                break;
            }
            case DisplayMsg.DrawAlphaBlend:
            {
                var (img, dx, dy) = SourceFor(c, s, visible);
                bool srcAlpha = img.HasAlpha || (c.AlphaFlags & 2) != 0;
                foreach (var r in rects) Raster.AlphaBlend(s, r, img, dx, dy, c.Alpha, srcAlpha);
                break;
            }
            case DisplayMsg.DrawText:
                DrawText(c, s, rects, visible);
                break;
            case DisplayMsg.DrawStroke:
                DrawStroke(c, s, rects, visible);
                break;
        }

        if (s.IsPrimary)
            foreach (var r in rects) Invalidated?.Invoke(r);
    }

    /// <summary>
    /// The source image for a copy-like command. When src_area differs in size from the box it is scaled to the box,
    /// but only the part inside <paramref name="visible"/> (the clipped draw) is computed.
    /// </summary>
    (ImageView Img, int Dx, int Dy) SourceFor(in DrawCommand c, Surface s, SpiceRect visible, bool forceNearest = false)
    {
        var img = Resolve(c.Source, s.Id);
        var box = c.Base.Box;
        var area = c.SourceArea;
        if (area.Width == box.Width && area.Height == box.Height)
            return (img, area.Left - box.Left, area.Top - box.Top);
        if (box.Width <= 0 || box.Height <= 0) throw new CanvasException("bad scaled box");
        Count("scaled");
        var win = visible.Intersect(box);
        if (win.IsEmpty) throw new CanvasException("scaled draw outside its box");
        var scaled = Raster.ScaleWindow(img, area, box.Width, box.Height, win.Offset(-box.Left, -box.Top),
            forceNearest || c.ScaleMode == 1, flipY: false);
        _temps.Add(scaled);
        return (scaled.View, -win.Left, -win.Top);
    }

    /// <summary>
    /// DRAW_TEXT: back area with back brush (PUT), then the glyph string as one mask (canvas_get_str_mask: glyphs are
    /// OR-ed for A1, max-ed for A4/A8) through the fore brush. A1 keeps fore_mode as a binary mask; A4/A8 keep their
    /// coverage and composite OVER like sw_canvas.c. The mask covers only the visible part of the string.
    /// </summary>
    void DrawText(in DrawCommand c, Surface s, ReadOnlySpan<SpiceRect> rects, SpiceRect visible)
    {
        if (!c.BackArea.IsEmpty && c.BackBrush.Type != BrushType.None)
        {
            var back = BrushSource(c.BackBrush, s.Id);
            foreach (var r in rects) Raster.Draw(s, r.Intersect(c.BackArea), back, Ropd.OpPut, null);
        }
        if (c.Brush.Type == BrushType.None) return;
        var fore = BrushSource(c.Brush, s.Id);
        var str = new SpiceReader(c.Vector);
        ushort count = str.U16();
        byte flags = str.U8();
        int bpp = (flags & 4) != 0 ? 8 : (flags & 2) != 0 ? 4 : 1;
        var glyphs = str;

        // Pass 1: the visible part of the union of glyph boxes.
        var area = default(SpiceRect);
        for (int g = 0; g < count; g++)
            if (NextGlyph(ref str, bpp, out var gb, out _, out _) && !gb.IsEmpty) area = area.Union(gb.Intersect(visible));
        if (area.IsEmpty) return;

        // Pass 2: accumulate coverage of the visible glyph pixels only.
        using var mask = CoverageMask.Rent(area.Width, area.Height, area.Left, area.Top);
        long work = 0;
        for (int g = 0; g < count; g++)
        {
            if (!NextGlyph(ref glyphs, bpp, out var gb, out var data, out int rowBytes)) continue;
            var v = gb.Intersect(area);
            if (v.IsEmpty) continue;
            work += (long)v.Width * v.Height;
            if (work > MaxGlyphWork) throw new CanvasException("text too expensive");
            int h = gb.Height;
            for (int y = v.Top; y < v.Bottom; y++)
            {
                // canvas_put_glyph_bits reads glyph rows bottom-up (RASTER_TOP_DOWN is unimplemented upstream).
                var row = data.Slice((h - 1 - (y - gb.Top)) * rowBytes, rowBytes);
                var cov = mask.Coverage.AsSpan((y - area.Top) * area.Width, area.Width);
                for (int x = v.Left; x < v.Right; x++)
                {
                    int gx = x - gb.Left;
                    int value = bpp switch
                    {
                        1 => ((row[gx >> 3] >> (7 - (gx & 7))) & 1) * 255,
                        // canvas_put_glyph_bits: high nibble first, each kept as nibble << 4.
                        4 => (gx & 1) == 0 ? row[gx >> 1] & 0xF0 : (row[gx >> 1] << 4) & 0xF0,
                        _ => row[gx],
                    };
                    int i = x - area.Left;
                    if (value > cov[i]) cov[i] = (byte)value;
                }
            }
        }
        foreach (var r in rects)
        {
            var rr = r.Intersect(area);
            if (rr.IsEmpty) continue;
            if (bpp == 1) Raster.Draw(s, rr, fore, c.ForeMode, mask);
            else Raster.DrawOver(s, rr, fore, mask);
        }
    }

    /// <summary>Reads one SpiceRasterGlyph; false when it is empty or its position is out of range.</summary>
    static bool NextGlyph(ref SpiceReader str, int bpp, out SpiceRect box, out ReadOnlySpan<byte> data, out int rowBytes)
    {
        int rx = str.I32(), ry = str.I32(), gx = str.I32(), gy = str.I32();
        int w = str.U16(), h = str.U16();
        rowBytes = (w * bpp + 7) / 8;
        long size = (long)rowBytes * h;
        if (size > int.MaxValue) throw new CanvasException("glyph too large");
        data = str.Bytes((int)size).Span;
        box = default;
        if (w == 0 || h == 0) return false;
        long left = (long)rx + gx, top = (long)ry + gy;
        if (left < int.MinValue / 2 || left > int.MaxValue / 2 || top < int.MinValue / 2 || top > int.MaxValue / 2) return false;
        box = new SpiceRect((int)left, (int)top, (int)left + w, (int)top + h);
        return true;
    }

    /// <summary>
    /// DRAW_STROKE: 1-pixel solid lines along the path (dash styles are not drawn). Each segment is clipped to each
    /// clip rectangle before rasterising, so offscreen geometry costs nothing; total work per command is capped.
    /// </summary>
    void DrawStroke(in DrawCommand c, Surface s, ReadOnlySpan<SpiceRect> rects, SpiceRect visible)
    {
        var src = BrushSource(c.Brush, s.Id);
        var path = new SpiceReader(c.Vector);
        uint segments = path.U32();
        if (segments > 1 << 16) throw new CanvasException("too many path segments");
        int lastX = 0, lastY = 0, startX = 0, startY = 0;
        bool have = false;
        ushort rop = c.ForeMode;
        long work = 0;
        for (uint i = 0; i < segments; i++)
        {
            byte flags = path.U8();
            uint count = path.U32();
            if (count > 1 << 16) throw new CanvasException("too many path points");
            for (uint p = 0; p < count; p++)
            {
                // FIXED28_4 rounded to the nearest pixel (fix_to_int).
                int x = (path.I32() + 8) >> 4, y = (path.I32() + 8) >> 4;
                bool begin = p == 0 && ((flags & 1) != 0 || !have);
                if (begin) { startX = x; startY = y; }
                else work += Segment(s, lastX, lastY, x, y, rects, visible, src, rop);
                lastX = x; lastY = y; have = true;
                if (work > MaxStrokeWork) throw new CanvasException("stroke too expensive");
            }
            if ((flags & 8) != 0 && have) work += Segment(s, lastX, lastY, startX, startY, rects, visible, src, rop);
        }
    }

    static long Segment(Surface s, int x0, int y0, int x1, int y1, ReadOnlySpan<SpiceRect> rects, SpiceRect visible, in PixelSource src, ushort rop)
    {
        var seg = new SpiceRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1) + 1, Math.Max(y0, y1) + 1);
        if (seg.Intersect(visible).IsEmpty) return 1;
        long work = 1;
        // Clip rects are disjoint, so every visible pixel is plotted exactly once.
        foreach (var r in rects)
            work += seg.Intersect(r).IsEmpty ? 1 : Raster.LineInRect(s, x0, y0, x1, y1, r, src, rop);
        return work;
    }

    /// <summary>
    /// Whether <see cref="BlitFrame"/> would draw any pixel: some of <paramref name="dest"/> lies on the surface and
    /// inside <paramref name="clip"/>. Lets a stream skip decoding a frame its clip hides entirely.
    /// </summary>
    public bool IsFrameVisible(uint surfaceId, SpiceRect dest, SpiceClip clip)
    {
        if (dest.IsEmpty) return false;
        lock (Sync)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var s)) return false;
            var box = dest.Intersect(s.Bounds);
            if (box.IsEmpty) return false;
            if (clip.Type == SpiceClip.TypeNone) return true;
            for (int i = 0; i < clip.Count; i++)
                if (!clip[i].Intersect(box).IsEmpty) return true;
            return false;
        }
    }

    /// <summary>
    /// Copies a decoded video frame into a surface at <paramref name="dest"/>, scaling if needed. Only the visible
    /// (surface- and clip-limited) part of the destination is scaled. Throws <see cref="CanvasException"/> for an
    /// impossible destination.
    /// </summary>
    public void BlitFrame(uint surfaceId, SpiceRect dest, SpiceClip clip, ImageView frame, bool topDown)
    {
        if (dest.Width > MaxStreamSide || dest.Height > MaxStreamSide)
            throw new CanvasException($"stream destination {dest} exceeds {MaxStreamSide} pixels");
        if (dest.IsEmpty || frame.IsEmpty || frame.Width <= 0 || frame.Height <= 0) return;
        lock (Sync)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var s)) return;
            var b = new DisplayBase { SurfaceId = surfaceId, Box = dest, Clip = clip };
            Span<SpiceRect> stack = stackalloc SpiceRect[16];
            int n = ClipRects(b, s, stack, out var rented);
            DecodedImage? temp = null;
            try
            {
                var rects = rented is null ? stack[..n] : rented.AsSpan(0, n);
                if (n == 0) return;
                if (topDown && frame.Width == dest.Width && frame.Height == dest.Height)
                    foreach (var r in rects) Raster.Copy(s, r, frame, -dest.Left, -dest.Top);
                else
                {
                    var win = Bounds(rects);
                    temp = Raster.ScaleWindow(frame, new SpiceRect(0, 0, frame.Width, frame.Height), dest.Width, dest.Height,
                        win.Offset(-dest.Left, -dest.Top), nearest: false, flipY: !topDown);
                    foreach (var r in rects) Raster.Copy(s, r, temp.View, -win.Left, -win.Top);
                }
                // Video on the primary surface after its MARK is a guest draw too: it counts for containment and
                // withdraws an inferred area it reaches outside (review MI1), but is never a candidate monitor.
                if (_primaryMarked && ReferenceEquals(s, Primary)) NoteGuestDraw(s, dest, Bounds(rects), candidate: false);
                if (s.IsPrimary) foreach (var r in rects) Invalidated?.Invoke(r);
            }
            finally
            {
                temp?.Release();
                if (rented is not null) ArrayPool<SpiceRect>.Shared.Return(rented);
            }
        }
    }

    /// <summary>A PNG of the primary surface's shown area, <see cref="PrimaryArea"/> (for tests and spice-cli dump).</summary>
    public byte[]? SnapshotPrimaryPng()
    {
        lock (Sync)
        {
            if (Primary is not { } p) return null;
            var a = PrimaryArea;
            return Png.Encode(p.Pixels, a.Width, a.Height, p.Stride, a.Top * p.Stride + a.Left * 4);
        }
    }
}
