using System.Runtime.ExceptionServices;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.UI.WindowsAndMessaging;
using SpiceCanvas = Spice.Canvas.Canvas;

namespace SpiceViewer;

/// <summary>
/// Shows the guest framebuffer in a flip-model swap chain. Runs on its own thread and sleeps until something
/// changes: no render loop, no timer. Two paths, chosen per update by <see cref="Viewport"/>:
/// <list type="bullet">
/// <item><b>Direct</b> (normal): the swap chain is the guest's size and DXGI stretches it to the window. Changed
/// rectangles are copied from the CPU framebuffer into the back buffer and passed to Present1 as dirty rects, with a
/// COPY_BITS passed on as the scroll rect. No shader.</item>
/// <item><b>Letterbox</b> (maximized, snapped, full screen, 1:1 at another size): the swap chain is the client size; the
/// guest lives in a texture updated with the changed rectangles and one full-screen-triangle shader draws it into the
/// destination rectangle, with bars in the window background color.</item>
/// </list>
/// </summary>
/// <remarks>
/// The worker thread creates, uses and releases every D3D/DXGI object (device, context, swap chain, views, shaders,
/// the guest texture and the frame-latency handle). Other threads only set flags and wake it. <see cref="Shutdown"/>
/// never releases anything: it signals the worker, which cleans up in its own <c>finally</c>.
/// </remarks>
sealed unsafe class Presenter
{
    /// <summary>Posted to the window: wParam is <see cref="NotifyChanged"/> or <see cref="NotifyFailed"/>.</summary>
    public const uint WM_APP_PRESENTER = PInvoke.WM_APP + 3;
    /// <summary>The device is ready or was recreated (adapter/software profile may differ).</summary>
    public const nuint NotifyChanged = 1;
    /// <summary>The presenter hit an unrecoverable error and stopped presenting.</summary>
    public const nuint NotifyFailed = 2;

    const int MaxRects = 16;
    const uint BufferCount = 2;
    const uint SwapChainFlags = (uint)DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
    /// <summary>Letterbox color: the window background (0x202020).</summary>
    static readonly float[] BarColor = [0x20 / 255f, 0x20 / 255f, 0x20 / 255f, 1f];
    /// <summary>The full-screen triangle (clip space), clockwise: covers the viewport with one primitive.</summary>
    static readonly float[] Triangle = [-1f, 1f, 3f, 1f, -1f, -3f];
    static readonly HRESULT S_OK = (HRESULT)0;

    readonly HWND _hwnd;
    readonly SpiceCanvas _canvas;
    readonly Thread _thread;
    // Wait handles are never disposed: the display thread may still call Invalidate on a stopped presenter.
    readonly AutoResetEvent _wake = new(false);
    readonly ManualResetEvent _stopEvent = new(false);
    readonly ManualResetEvent _exited = new(false);
    readonly ManualResetEventSlim _ready = new(false);
    readonly object _lock = new();
    // Under _lock. _pending: everything changed since the last present (uploaded). _pendingPresent: the same minus a
    // scroll's destination (reported to Present1 as dirty).
    readonly List<SpiceRect> _pending = [];
    readonly List<SpiceRect> _pendingPresent = [];
    // Worker only: the last present's upload set (flip model: the back buffer is two presents old).
    readonly List<SpiceRect> _previous = [];
    Exception? _initError;
    bool _fullPending = true;
    volatile bool _stop;
    volatile bool _suspended;
    volatile bool _failed;
    volatile bool _isSoftware;

    // One COPY_BITS per present can become the DXGI scroll rect (under _lock).
    bool _scroll;            // a usable scroll is pending
    bool _scrollCompanion;   // the next Invalidate is the scroll's own destination
    bool _scrollBlocked;     // a second copy-bits (or an unsafe order) since the last present: plain dirty rects
    SpiceRect _scrollDest;
    int _scrollDx, _scrollDy;

    // Layout requested by the window (under _lock).
    int _clientW, _clientH;
    bool _oneToOne, _layoutSet;

    // Worker-thread-only state.
    ID3D11Device* _device;
    ID3D11DeviceContext* _context;
    IDXGISwapChain2* _swapChain;
    ID3D11Texture2D* _back;
    ID3D11RenderTargetView* _rtv;
    HANDLE _waitable;
    // Letterbox path: created on first use, released with the device.
    ID3D11VertexShader* _vs;
    ID3D11PixelShader* _ps;
    ID3D11InputLayout* _inputLayout;
    ID3D11Buffer* _vertices;
    ID3D11Buffer* _constants;
    ID3D11SamplerState* _pointSampler;
    ID3D11SamplerState* _linearSampler;
    // The guest copy the letterbox shader samples; exists only while the letterbox path is in use.
    ID3D11Texture2D* _guestTex;
    ID3D11ShaderResourceView* _guestSrv;
    int _texW, _texH;
    Viewport _vp;
    // The shown area of the guest surface at the last present (docs/decisions/0035): size and offset in the surface.
    int _guestW, _guestH, _guestX, _guestY;
    int _clearBuffersLeft;
    int _maxDim = Viewport.DefaultMaxDimension;
    bool _occluded;
    IDXGIFactory2* _factory;
    uint _occlusionCookie;
    bool _recoveredWithoutPresent;
    bool _loggedComposition;
    HRESULT _lastPresentError;

    public bool IsSoftware => _isSoftware;
    public string AdapterName { get; private set; } = "";
    /// <summary>Texture size limit of the current device's feature level.</summary>
    public int MaxDimension => Volatile.Read(ref _maxDim);
    /// <summary>The worker stopped after an unrecoverable error.</summary>
    public bool HasFailed => _failed;
    /// <summary>Debug switch: present the whole frame every time (Present1 bookkeeping check).</summary>
    public bool FullFrames { get; set; } = Environment.GetEnvironmentVariable("SPICEVIEWER_FULL_FRAMES") == "1";
    public long PresentCount;
    /// <summary>Presents that carried a scroll rect (diagnostics).</summary>
    public long ScrollPresentCount;

    /// <summary>Starts the worker and waits for it to create the device; throws if that fails.</summary>
    public Presenter(HWND hwnd, SpiceCanvas canvas)
    {
        _hwnd = hwnd;
        _canvas = canvas;
        _thread = new Thread(Run) { Name = "presenter", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        // Device creation does not involve the window, so this wait cannot deadlock with the message pump.
        if (!_ready.Wait(10_000))
        {
            Shutdown(0);
            throw new TimeoutException("Creating the Direct3D device timed out.");
        }
        if (_initError is not null) ExceptionDispatchInfo.Throw(_initError);
    }

    /// <summary>Marks a guest rectangle dirty. Cheap; called on the display thread.</summary>
    public void Invalidate(SpiceRect r)
    {
        lock (_lock)
        {
            if (!_fullPending && !r.IsEmpty)
            {
                AddRect(_pending, r);
                bool isScrollDest = false;
                if (_scrollCompanion)
                {
                    _scrollCompanion = false;
                    if (r == _scrollDest) isScrollDest = true; // in the back buffer, but not reported as dirty
                    else DropScroll();
                }
                if (!isScrollDest) AddRect(_pendingPresent, r);
            }
        }
        _wake.Set();
    }

    /// <summary>
    /// A COPY_BITS moved <paramref name="dest"/> from <paramref name="dest"/> offset by (dx, dy); its
    /// <see cref="Invalidate"/> follows. Called on the display thread under the canvas lock.
    /// </summary>
    /// <remarks>
    /// Present1's scroll rect tells the compositor to fill the destination from the previous frame's source area; the
    /// back buffer still gets the destination uploaded like any dirty rect, so it is complete either way. That is only
    /// right while the previous frame's source pixels are still what the guest copied, so the scroll is used only when
    /// nothing changed inside the source since the last present before the copy, and only once per present.
    /// </remarks>
    public void CopyBits(SpiceRect dest, int dx, int dy)
    {
        lock (_lock)
        {
            if (_fullPending || dest.IsEmpty) return;
            if (_scroll || _scrollBlocked) { DropScroll(); return; }
            var source = dest.Offset(dx, dy);
            foreach (var r in _pendingPresent)
                if (!r.Intersect(source).IsEmpty) { _scrollBlocked = true; return; }
            _scroll = true;
            _scrollCompanion = true;
            _scrollDest = dest;
            _scrollDx = dx;
            _scrollDy = dy;
        }
    }

    /// <summary>Under _lock: no scroll for this present; its destination becomes an ordinary dirty rect.</summary>
    void DropScroll()
    {
        if (_scroll) AddRect(_pendingPresent, _scrollDest);
        _scroll = _scrollCompanion = false;
        _scrollBlocked = true;
    }

    /// <summary>A CPU image the presenter shows while the canvas has no primary surface (docs/decisions/0035).</summary>
    public sealed record Placeholder(byte[] Pixels, int Width, int Height);

    Placeholder? _placeholder;

    /// <summary>
    /// Shows <paramref name="image"/> (top-down BGRA, Width × 4 bytes per row, never changed afterwards) whenever the
    /// canvas has no primary surface, through the same upload-and-Present1 path as a guest frame; null shows nothing new.
    /// A primary surface always takes precedence. Releasing the swap chain instead doesn't work: DWM keeps compositing a
    /// flip-model swap chain's last frame over the window's GDI painting (finding L5).
    /// </summary>
    public void SetPlaceholder(Placeholder? image)
    {
        Volatile.Write(ref _placeholder, image);
        InvalidateAll();
    }

    /// <summary>Everything must be re-presented (resize, restore, disconnect overlay).</summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _fullPending = true;
            _pending.Clear();
            _pendingPresent.Clear();
            _scroll = _scrollCompanion = _scrollBlocked = false;
        }
        _wake.Set();
    }

    /// <summary>
    /// The window's client size (physical pixels) and scaling mode. The worker presents again only if the resulting
    /// <see cref="Viewport"/> differs; an aspect-locked resize leaves the buffer alone and DXGI just stretches it.
    /// </summary>
    public void SetLayout(int clientW, int clientH, bool oneToOne)
    {
        if (clientW <= 0 || clientH <= 0) return;
        lock (_lock)
        {
            if (_layoutSet && clientW == _clientW && clientH == _clientH && oneToOne == _oneToOne) return;
            _clientW = clientW; _clientH = clientH; _oneToOne = oneToOne; _layoutSet = true;
        }
        _wake.Set();
    }

    /// <summary>Minimized: keep the guest state current but present nothing.</summary>
    public void SetSuspended(bool suspended)
    {
        bool was = _suspended;
        _suspended = suspended;
        // Only coming back from minimized needs a full frame. Every other WM_SIZE calls this too, and a resize that
        // leaves the viewport alone must not re-upload anything (DXGI stretches the buffer).
        if (was && !suspended) InvalidateAll();
    }

    /// <summary>
    /// Stops the worker. Releases nothing itself: the worker does that. Waits up to <paramref name="timeoutMs"/>
    /// for it while dispatching only sent messages, since Present may wait on this (the window's) thread.
    /// If the wait times out the worker still finishes and cleans up on its own.
    /// </summary>
    public void Shutdown(int timeoutMs)
    {
        _stop = true;
        _stopEvent.Set();
        _wake.Set();
        if (timeoutMs <= 0 || Thread.CurrentThread == _thread) return;
        var exited = (HANDLE)_exited.SafeWaitHandle.DangerousGetHandle();
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            // Dispatch pending cross-thread SendMessage calls only; posted messages stay queued.
            PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE | PEEK_MESSAGE_REMOVE_TYPE.PM_QS_SENDMESSAGE);
            long left = deadline - Environment.TickCount64;
            if (left <= 0) break;
            var r = PInvoke.MsgWaitForMultipleObjectsEx(1, &exited, (uint)left, QUEUE_STATUS_FLAGS.QS_SENDMESSAGE, MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_NONE);
            if (r == WAIT_EVENT.WAIT_OBJECT_0) return;
            if (r != (WAIT_EVENT)((uint)WAIT_EVENT.WAIT_OBJECT_0 + 1)) break; // timeout or failure
        }
        Log.Warn("presenter: worker did not stop in time; it will release its objects when it does");
    }

    static void AddRect(List<SpiceRect> list, SpiceRect r)
    {
        if (r.IsEmpty) return;
        for (int i = 0; i < list.Count; i++)
        {
            var u = list[i];
            // Merge overlapping or touching rectangles.
            if (r.Left <= u.Right && u.Left <= r.Right && r.Top <= u.Bottom && u.Top <= r.Bottom)
            {
                list.RemoveAt(i);
                AddRect(list, u.Union(r));
                return;
            }
        }
        if (list.Count < MaxRects) { list.Add(r); return; }
        var all = r;
        foreach (var x in list) all = all.Union(x);
        list.Clear();
        list.Add(all);
    }

    // ---------------- worker ----------------

    void Run()
    {
        try
        {
            try { CreateDevice(); }
            catch (Exception e) { _initError = e; return; }
            finally { _ready.Set(); }
            Notify(NotifyChanged);
            Loop();
        }
        catch (Exception e)
        {
            Log.Error($"presenter: worker failed: {e}");
            Fail();
        }
        finally
        {
            try { ReleaseAll(); }
            catch (Exception e) { Log.Error($"presenter: release failed: {e}"); }
            _exited.Set();
        }
    }

    void Loop()
    {
        long lastPresent = 0;
        var rects = new List<SpiceRect>(MaxRects);
        var presentRects = new List<SpiceRect>(MaxRects);
        // AddRect keeps every list at MaxRects or fewer (+1: a scroll's destination or its placeholder).
        // Allocated once, outside the loop.
        var dirty = stackalloc RECT[MaxRects + 1];
        var waits = stackalloc HANDLE[2];
        bool slotHeld = false;
        while (true)
        {
            _wake.WaitOne();
            if (_stop) return;
            if (_suspended) continue;
            // Occluded: keep the guest state (and the pending full frame), but upload and present nothing until a
            // test present says the window is visible again. The occlusion-status event wakes us when that changes.
            if (_occluded && StillOccluded()) continue;

            // Software profile: at most 30 presents per second (only matters under continuous updates).
            if (_isSoftware)
            {
                long wait = 34 - (Environment.TickCount64 - lastPresent); // 34 ms: never above 30 presents/s
                if (wait > 0) _stopEvent.WaitOne((int)wait);
            }

            // The latency waitable counts free frame slots. A wake that ends up presenting nothing (layout-only
            // change, cropped-away rects) keeps its slot, so the next real update does not stall on the timeout.
            if (_waitable != HANDLE.Null && !slotHeld)
            {
                waits[0] = _waitable;
                waits[1] = (HANDLE)_stopEvent.SafeWaitHandle.DangerousGetHandle();
                slotHeld = PInvoke.WaitForMultipleObjects(2, waits, false, 1000) == WAIT_EVENT.WAIT_OBJECT_0;
            }
            if (_stop) return;

            // At most one recovery per update: a device lost again before anything was presented is terminal.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                HRESULT hr;
                try
                {
                    hr = RenderOnce(rects, presentRects, dirty, out bool presented);
                    if (presented) { lastPresent = Environment.TickCount64; slotHeld = false; }
                }
                catch (Exception e) when (IsDeviceLost((HRESULT)e.HResult)) { hr = (HRESULT)e.HResult; }
                if (!IsDeviceLost(hr)) break;
                if (_stop) return;
                slotHeld = false; // the new swap chain has its own waitable
                if (!Recover(hr)) { Fail(); return; }
            }
        }
    }

    /// <summary>Uploads and presents pending changes. Returns Present1's result (S_OK if nothing was presented).</summary>
    HRESULT RenderOnce(List<SpiceRect> rects, List<SpiceRect> presentRects, RECT* dirty, out bool presented)
    {
        presented = false;
        bool full, scroll;
        int dirtyCount = 0;
        RECT scrollRect = default;
        System.Drawing.Point scrollOffset = default;
        lock (_canvas.Sync)
        {
            // Only the guest's monitor area of the surface is shown (docs/decisions/0035). Below, every rectangle is in
            // area coordinates; Upload reads the source at the area's offset. Without a primary surface, the
            // placeholder (a message image) is the source, whole.
            byte[] pixels;
            int stride, gw, gh, ax, ay;
            if (_canvas.Primary is { } surface)
            {
                var area = _canvas.PrimaryArea;
                pixels = surface.Pixels; stride = surface.Stride;
                gw = area.Width; gh = area.Height; ax = area.Left; ay = area.Top;
            }
            else if (Volatile.Read(ref _placeholder) is { } placeholder)
            {
                pixels = placeholder.Pixels; stride = placeholder.Width * 4;
                gw = placeholder.Width; gh = placeholder.Height; ax = ay = 0;
            }
            else return S_OK;
            if (gw <= 0 || gh <= 0) return S_OK;
            int cw, ch;
            bool oneToOne;
            SpiceRect scrollDest;
            int scrollDx, scrollDy;
            lock (_lock)
            {
                full = _fullPending || FullFrames;
                rects.Clear();
                foreach (var r in _pending) rects.Add(r.Offset(-ax, -ay));
                presentRects.Clear();
                foreach (var r in _pendingPresent) presentRects.Add(r.Offset(-ax, -ay));
                _pending.Clear();
                _pendingPresent.Clear();
                _fullPending = false;
                scroll = _scroll;
                scrollDest = _scrollDest.Offset(-ax, -ay); scrollDx = _scrollDx; scrollDy = _scrollDy;
                _scroll = _scrollCompanion = _scrollBlocked = false;
                cw = _clientW; ch = _clientH; oneToOne = _oneToOne;
                if (!_layoutSet)
                {
                    PInvoke.GetClientRect(_hwnd, out var rc);
                    cw = rc.Width; ch = rc.Height;
                }
            }
            // Software profile: 1:1 only (no stretch on the CPU rasterizer).
            var vp = Viewport.Compute(cw, ch, gw, gh, oneToOne || _isSoftware, _maxDim);

            if (_swapChain is null) CreateSwapChain(vp.BufferWidth, vp.BufferHeight);
            else if (vp.BufferWidth != _vp.BufferWidth || vp.BufferHeight != _vp.BufferHeight)
            {
                // Direct path: only when the guest resolution changes. Letterbox path: the client size changed.
                ReleaseTargets();
                _context->ClearState();
                _swapChain->ResizeBuffers(BufferCount, (uint)vp.BufferWidth, (uint)vp.BufferHeight, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, SwapChainFlags);
            }
            if (vp != _vp || gw != _guestW || gh != _guestH || ax != _guestX || ay != _guestY)
            {
                if (vp.Direct != _vp.Direct || _vp.BufferWidth == 0)
                    Log.Info($"presenter: {(vp.Direct ? "direct" : "letterbox")} path, buffer {vp.BufferWidth}x{vp.BufferHeight}, guest {gw}x{gh}, client {cw}x{ch}");
                else
                    Log.Debug($"presenter: layout {vp}");
                _vp = vp; _guestW = gw; _guestH = gh; _guestX = ax; _guestY = ay;
                _clearBuffersLeft = (int)BufferCount; // letterbox: both flip buffers need the new bars
                full = true;
            }
            if (vp.Direct) ReleaseGuestTexture(); // only the letterbox path samples it; it goes stale meanwhile
            else if (EnsureGuestTexture(vp.SourceWidth, vp.SourceHeight)) full = true;
            if (full)
            {
                rects.Clear(); rects.Add(new SpiceRect(0, 0, gw, gh));
                presentRects.Clear();
                scroll = false;
                _previous.Clear();
            }
            if (rects.Count == 0) return S_OK;

            // The part of the guest that is shown (all of it unless it exceeds the texture limit).
            var source = new SpiceRect(0, 0, vp.SourceWidth, vp.SourceHeight);
            if (vp.Direct)
            {
                EnsureBackBuffer();
                fixed (byte* px = pixels)
                {
                    byte* origin = px + (long)ay * stride + ax * 4;
                    // Flip model with two buffers: this back buffer last held the frame from two presents ago, so it
                    // needs this update's rects and the previous update's. A scroll's destination is among them, so
                    // the buffer is complete whether or not the compositor applies the scroll itself.
                    foreach (var r in rects) Upload((ID3D11Resource*)_back, origin, stride, r, source);
                    foreach (var r in _previous) Upload((ID3D11Resource*)_back, origin, stride, r, source);
                }
                if (!full)
                {
                    foreach (var g in presentRects)
                    {
                        var r = g.Intersect(source);
                        if (!r.IsEmpty) dirty[dirtyCount++] = new RECT(r.Left, r.Top, r.Right, r.Bottom);
                    }
                    if (scroll)
                    {
                        var from = scrollDest.Offset(scrollDx, scrollDy);
                        if (scrollDest.Intersect(source) == scrollDest && from.Intersect(source) == from)
                        {
                            scrollRect = new RECT(scrollDest.Left, scrollDest.Top, scrollDest.Right, scrollDest.Bottom);
                            scrollOffset = new System.Drawing.Point(-scrollDx, -scrollDy); // source to destination
                            // Zero dirty rects would mean "the whole frame changed"; one pixel of the (complete)
                            // destination keeps the present partial.
                            if (dirtyCount == 0) dirty[dirtyCount++] = new RECT(scrollDest.Left, scrollDest.Top, scrollDest.Left + 1, scrollDest.Top + 1);
                        }
                        else
                        {
                            scroll = false;
                            var r = scrollDest.Intersect(source);
                            if (!r.IsEmpty) dirty[dirtyCount++] = new RECT(r.Left, r.Top, r.Right, r.Bottom);
                        }
                    }
                    if (dirtyCount == 0) return S_OK; // only cropped-away pixels changed
                }
            }
            else
            {
                scroll = false; // the bars and scaling make the guest's scroll meaningless here
                fixed (byte* px = pixels)
                {
                    byte* origin = px + (long)ay * stride + ax * 4;
                    foreach (var r in rects) Upload((ID3D11Resource*)_guestTex, origin, stride, r, source);
                }
                var visible = DrawLetterbox(vp, full);
                if (!full)
                {
                    foreach (var g in rects)
                    {
                        var r = ToBuffer(vp, g.Intersect(source), visible);
                        if (r.right > r.left && r.bottom > r.top) dirty[dirtyCount++] = r;
                    }
                    if (dirtyCount == 0) return S_OK;
                }
            }
        }

        var p = default(DXGI_PRESENT_PARAMETERS);
        if (!full)
        {
            p.DirtyRectsCount = (uint)dirtyCount;
            p.pDirtyRects = dirty;
            if (scroll) { p.pScrollRect = &scrollRect; p.pScrollOffset = &scrollOffset; }
        }
        HRESULT hr = _swapChain->Present1(1, 0, &p);
        Interlocked.Increment(ref PresentCount);
        presented = true;

        bool occluded = hr == HRESULT.DXGI_STATUS_OCCLUDED;
        if (occluded != _occluded) { _occluded = occluded; Log.Debug($"presenter: occluded={occluded}"); }
        if (occluded || hr.Failed)
        {
            // Nothing reached the screen: present everything on the next change. No self-wake: a failing
            // device must not turn into a busy loop. Device loss is handled by the caller.
            _previous.Clear();
            _clearBuffersLeft = (int)BufferCount; // unknown which buffer got the bars; repaint both
            lock (_lock) _fullPending = true;
            if (hr.Failed && !IsDeviceLost(hr) && hr != _lastPresentError) Log.Error($"presenter: Present1 failed {hr}");
            if (hr.Failed) _lastPresentError = hr;
            return hr;
        }
        _lastPresentError = S_OK;
        _recoveredWithoutPresent = false;
        if (scroll && !full) ScrollPresentCount++;
        _previous.Clear();
        _previous.AddRange(rects);
        if (!_loggedComposition) { _loggedComposition = true; LogHardwareComposition(); }
        return hr;
    }

    /// <summary>
    /// Copies guest rectangle <paramref name="r"/> (area coordinates, clipped to <paramref name="source"/>) into
    /// <paramref name="target"/> at the same position. <paramref name="pixels"/> points at the area's top-left pixel.
    /// </summary>
    void Upload(ID3D11Resource* target, byte* pixels, int stride, SpiceRect r, SpiceRect source)
    {
        r = r.Intersect(source);
        if (r.IsEmpty) return;
        var box = new D3D11_BOX { left = (uint)r.Left, top = (uint)r.Top, right = (uint)r.Right, bottom = (uint)r.Bottom, front = 0, back = 1 };
        _context->UpdateSubresource(target, 0, &box, pixels + (long)r.Top * stride + r.Left * 4, (uint)stride, 0);
    }

    /// <summary>
    /// A guest rectangle in letterbox buffer pixels, within <paramref name="visible"/>. Scaled output is linearly
    /// filtered: a texel influences every output pixel whose bilinear footprint reaches it, i.e. up to one source
    /// texel beyond the changed rect on each side at any scale (plus a pixel for rounding). 1:1 point sampling is exact.
    /// </summary>
    static RECT ToBuffer(in Viewport vp, SpiceRect g, RECT visible)
    {
        if (g.IsEmpty) return default;
        int texels = vp.Unscaled ? 0 : 1, pad = vp.Unscaled ? 0 : 1;
        int l = vp.X + (int)Math.Floor((double)(g.Left - texels) * vp.Width / vp.SourceWidth) - pad;
        int t = vp.Y + (int)Math.Floor((double)(g.Top - texels) * vp.Height / vp.SourceHeight) - pad;
        int r = vp.X + (int)Math.Ceiling((double)(g.Right + texels) * vp.Width / vp.SourceWidth) + pad;
        int b = vp.Y + (int)Math.Ceiling((double)(g.Bottom + texels) * vp.Height / vp.SourceHeight) + pad;
        return new RECT(Math.Max(l, visible.left), Math.Max(t, visible.top), Math.Min(r, visible.right), Math.Min(b, visible.bottom));
    }

    /// <summary>
    /// Letterbox path: clears the bars (once per flip buffer after a layout change) and draws the whole guest texture
    /// into the destination rectangle, so the back buffer is complete regardless of its age. Returns the drawn area.
    /// </summary>
    RECT DrawLetterbox(in Viewport vp, bool layoutChanged)
    {
        EnsureBackBuffer();
        if (_rtv is null)
        {
            ID3D11RenderTargetView* rtv;
            _device->CreateRenderTargetView((ID3D11Resource*)_back, null, &rtv);
            _rtv = rtv;
        }
        if (_clearBuffersLeft > 0)
        {
            fixed (float* c = BarColor) _context->ClearRenderTargetView(_rtv, c);
            _clearBuffersLeft--;
        }
        // The destination may extend past the buffer (cropped 1:1): draw only the visible part and map it to the
        // matching part of the texture, so the viewport always lies inside the render target (feature level 9).
        var visible = new RECT(Math.Max(vp.X, 0), Math.Max(vp.Y, 0),
            Math.Min(vp.X + vp.Width, vp.BufferWidth), Math.Min(vp.Y + vp.Height, vp.BufferHeight));
        if (visible.right <= visible.left || visible.bottom <= visible.top) return default;
        if (layoutChanged)
        {
            var uv = stackalloc float[4];
            uv[0] = (float)(visible.left - vp.X) / vp.Width;
            uv[1] = (float)(visible.top - vp.Y) / vp.Height;
            uv[2] = (float)(visible.right - visible.left) / vp.Width;
            uv[3] = (float)(visible.bottom - visible.top) / vp.Height;
            _context->UpdateSubresource((ID3D11Resource*)_constants, 0, null, uv, 0, 0);
        }

        var rtvs = _rtv;
        _context->OMSetRenderTargets(1, &rtvs, null);
        var viewport = new D3D11_VIEWPORT
        {
            TopLeftX = visible.left, TopLeftY = visible.top,
            Width = visible.right - visible.left, Height = visible.bottom - visible.top,
            MinDepth = 0, MaxDepth = 1,
        };
        _context->RSSetViewports(1, &viewport);
        _context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        _context->IASetInputLayout(_inputLayout);
        var vb = _vertices;
        uint stride = 8, offset = 0;
        _context->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        _context->VSSetShader(_vs, null, 0);
        var cb = _constants;
        _context->VSSetConstantBuffers(0, 1, &cb);
        _context->PSSetShader(_ps, null, 0);
        var srv = _guestSrv;
        _context->PSSetShaderResources(0, 1, &srv);
        // 1:1 stays pixel-exact; scaled output is filtered.
        var sampler = vp.Unscaled ? _pointSampler : _linearSampler;
        _context->PSSetSamplers(0, 1, &sampler);
        _context->Draw(3, 0);
        return visible;
    }

    void EnsureBackBuffer()
    {
        if (_back is not null) return;
        ID3D11Texture2D* back;
        var iid = typeof(ID3D11Texture2D).GUID;
        _swapChain->GetBuffer(0, &iid, (void**)&back);
        _back = back;
    }

    /// <summary>(Re)creates the letterbox guest texture for a w×h source. True if it was (re)created and needs a full upload.</summary>
    bool EnsureGuestTexture(int w, int h)
    {
        if (_guestTex is not null && _texW == w && _texH == h) return false;
        ReleaseGuestTexture();
        EnsureLetterboxPipeline();
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* tex;
        _device->CreateTexture2D(&desc, null, &tex);
        _guestTex = tex;
        ID3D11ShaderResourceView* srv;
        _device->CreateShaderResourceView((ID3D11Resource*)tex, null, &srv);
        _guestSrv = srv;
        _texW = w; _texH = h;
        return true;
    }

    void ReleaseGuestTexture()
    {
        if (_guestSrv is not null) { _guestSrv->Release(); _guestSrv = null; }
        if (_guestTex is not null) { _guestTex->Release(); _guestTex = null; }
        _texW = _texH = 0;
    }

    /// <summary>Shaders, input layout, vertex and constant buffers and samplers of the letterbox draw (once per device).</summary>
    void EnsureLetterboxPipeline()
    {
        if (_vs is not null) return;
        var vsCode = Shaders.LetterboxVS;
        var psCode = Shaders.LetterboxPS;
        fixed (byte* vsc = vsCode)
        fixed (byte* psc = psCode)
        fixed (byte* semantic = "POSITION\0"u8)
        fixed (float* tri = Triangle)
        {
            ID3D11VertexShader* vs;
            _device->CreateVertexShader(vsc, (nuint)vsCode.Length, null, &vs);
            _vs = vs;
            ID3D11PixelShader* ps;
            _device->CreatePixelShader(psc, (nuint)psCode.Length, null, &ps);
            _ps = ps;

            var element = new D3D11_INPUT_ELEMENT_DESC
            {
                SemanticName = new PCSTR(semantic),
                SemanticIndex = 0,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                InputSlot = 0,
                AlignedByteOffset = 0,
                InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_VERTEX_DATA,
                InstanceDataStepRate = 0,
            };
            ID3D11InputLayout* layout;
            _device->CreateInputLayout(&element, 1, vsc, (nuint)vsCode.Length, &layout);
            _inputLayout = layout;

            var vbDesc = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)(Triangle.Length * sizeof(float)),
                Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER,
            };
            var init = new D3D11_SUBRESOURCE_DATA { pSysMem = tri };
            ID3D11Buffer* vb;
            _device->CreateBuffer(&vbDesc, &init, &vb);
            _vertices = vb;
        }

        var cbDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = 16,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
        };
        ID3D11Buffer* cbuf;
        _device->CreateBuffer(&cbDesc, null, &cbuf);
        _constants = cbuf;

        _pointSampler = CreateSampler(D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_POINT);
        _linearSampler = CreateSampler(D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR);
    }

    ID3D11SamplerState* CreateSampler(D3D11_FILTER filter)
    {
        var desc = new D3D11_SAMPLER_DESC
        {
            Filter = filter,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            MaxAnisotropy = 1,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        };
        ID3D11SamplerState* s;
        _device->CreateSamplerState(&desc, &s);
        return s;
    }

    static bool IsDeviceLost(HRESULT hr) =>
        hr == HRESULT.DXGI_ERROR_DEVICE_REMOVED || hr == HRESULT.DXGI_ERROR_DEVICE_RESET
        || hr == HRESULT.DXGI_ERROR_DEVICE_HUNG || hr == HRESULT.DXGI_ERROR_DRIVER_INTERNAL_ERROR;

    /// <summary>Recreates device and swap chain once after a device loss; false if that is not possible.</summary>
    bool Recover(HRESULT hr)
    {
        string reason = "";
        if (_device is not null)
        {
            try { _device->GetDeviceRemovedReason(); }
            catch (Exception e) { reason = $" (reason {(HRESULT)e.HResult})"; }
        }
        if (_recoveredWithoutPresent)
        {
            Log.Error($"presenter: device lost again before a frame was presented ({hr}){reason}; giving up");
            return false;
        }
        Log.Warn($"presenter: device lost ({hr}){reason}; recreating device and swap chain");
        ReleaseAll();
        try { CreateDevice(); }
        catch (Exception e)
        {
            Log.Error($"presenter: device recreation failed: {e.Message}");
            return false;
        }
        _recoveredWithoutPresent = true;
        _previous.Clear();
        lock (_lock)
        {
            _fullPending = true;
            _pending.Clear();
            _pendingPresent.Clear();
            _scroll = _scrollCompanion = _scrollBlocked = false;
        }
        Notify(NotifyChanged); // the adapter (and so the software profile) may have changed
        return true;
    }

    void Fail()
    {
        _failed = true;
        Notify(NotifyFailed);
    }

    void Notify(nuint code)
    {
        if (!_stop) PInvoke.PostMessage(_hwnd, WM_APP_PRESENTER, code, 0);
    }

    void CreateDevice()
    {
        var levels = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_9_3 };
        var flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_SINGLETHREADED;
        ID3D11Device* dev = null;
        ID3D11DeviceContext* ctx = null;
        D3D_FEATURE_LEVEL level = 0;
        // Debug switch: exercise the software (WARP) profile on a machine with a GPU.
        bool forceWarp = Environment.GetEnvironmentVariable("SPICEVIEWER_FORCE_WARP") == "1";
        HRESULT hr = forceWarp ? (HRESULT)unchecked((int)0x80004005) /* E_FAIL */
            : PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null, flags, levels, 4,
                PInvoke.D3D11_SDK_VERSION, &dev, &level, &ctx);
        if (hr.Failed)
        {
            Log.Info($"presenter: hardware device {(forceWarp ? "skipped (SPICEVIEWER_FORCE_WARP)" : $"failed ({hr})")}, using WARP");
            hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, HMODULE.Null, flags, levels, 4,
                PInvoke.D3D11_SDK_VERSION, &dev, &level, &ctx);
        }
        hr.ThrowOnFailure();
        _device = dev;
        _context = ctx;
        Volatile.Write(ref _maxDim, level >= D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 ? 16384 : level >= D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0 ? 8192 : 4096);

        // Software adapter? (WARP, Microsoft Basic Render Driver, or RDP's software path)
        _device->QueryInterface<IDXGIDevice>(out var dxgiDevice).ThrowOnFailure();
        try
        {
            IDXGIAdapter* adapter;
            dxgiDevice->GetAdapter(&adapter);
            if (adapter->QueryInterface<IDXGIAdapter1>(out var adapter1).Succeeded)
            {
                var desc = adapter1->GetDesc1();
                AdapterName = desc.Description.ToString().TrimEnd('\0');
                _isSoftware = forceWarp || ((uint)desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || desc.VendorId == 0x1414;
                adapter1->Release();
            }
            adapter->Release();
        }
        finally { dxgiDevice->Release(); }
        Log.Info($"presenter: adapter '{AdapterName}' software={_isSoftware} level={level}");
    }

    /// <summary>
    /// While occluded: true if the window is still hidden (or the test failed; the next real present reports that).
    /// Clears <see cref="_occluded"/> once it's visible.
    /// </summary>
    bool StillOccluded()
    {
        if (_swapChain is null) { _occluded = false; return false; }
        var p = default(DXGI_PRESENT_PARAMETERS);
        if (_swapChain->Present1(0, DXGI_PRESENT.DXGI_PRESENT_TEST, &p) == HRESULT.DXGI_STATUS_OCCLUDED) return true;
        _occluded = false;
        Log.Debug("presenter: occluded=False");
        return false;
    }

    void CreateSwapChain(int width, int height)
    {
        IDXGIFactory2* factory;
        Guid iid = typeof(IDXGIFactory2).GUID;
        PInvoke.CreateDXGIFactory2(0, &iid, (void**)&factory).ThrowOnFailure();
        try
        {
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = BufferCount,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
                Flags = (DXGI_SWAP_CHAIN_FLAG)SwapChainFlags,
            };
            IDXGISwapChain1* sc1;
            factory->CreateSwapChainForHwnd((Windows.Win32.System.Com.IUnknown*)_device, _hwnd, &desc, null, null, &sc1);
            // We handle Alt+Enter ourselves (and forward it to the guest).
            factory->MakeWindowAssociation(_hwnd, DXGI_MWA_FLAGS.DXGI_MWA_NO_ALT_ENTER | DXGI_MWA_FLAGS.DXGI_MWA_NO_WINDOW_CHANGES);
            var qi = sc1->QueryInterface<IDXGISwapChain2>(out var sc2);
            sc1->Release();
            qi.ThrowOnFailure();
            _swapChain = sc2;
            // Occlusion changes signal the worker's wake event; kept until the swap chain goes (ReleaseAll).
            try
            {
                uint cookie;
                factory->RegisterOcclusionStatusEvent((HANDLE)_wake.SafeWaitHandle.DangerousGetHandle(), &cookie);
                _factory = factory; _occlusionCookie = cookie;
                factory = null;
            }
            catch (Exception e) { Log.Debug($"presenter: no occlusion notifications ({(HRESULT)e.HResult})"); }
        }
        finally { if (factory is not null) factory->Release(); }
        _swapChain->SetMaximumFrameLatency(1);
        _waitable = _swapChain->GetFrameLatencyWaitableObject();
    }

    /// <summary>
    /// Logs whether the output can scale and compose in hardware (docs/design/spec.md, Scaling): the direct path relies
    /// on DXGI stretching the guest-sized buffer. Once per device, after the first present places the window.
    /// </summary>
    void LogHardwareComposition()
    {
        IDXGIOutput* output = null;
        try
        {
            _swapChain->GetContainingOutput(&output);
            if (output->QueryInterface<IDXGIOutput6>(out var output6).Succeeded)
            {
                uint support;
                output6->CheckHardwareCompositionSupport(&support);
                output6->Release();
                Log.Info($"presenter: hardware composition support 0x{support:x} (1 fullscreen, 2 windowed, 4 cursor stretched)");
            }
        }
        catch (Exception e) { Log.Debug($"presenter: hardware composition support unknown ({(HRESULT)e.HResult})"); }
        finally { if (output is not null) output->Release(); }
    }

    void ReleaseTargets()
    {
        if (_rtv is not null) { _rtv->Release(); _rtv = null; }
        if (_back is not null) { _back->Release(); _back = null; }
    }

    static void Release<T>(ref T* p) where T : unmanaged
    {
        if (p is null) return;
        ((Windows.Win32.System.Com.IUnknown*)p)->Release();
        p = null;
    }

    /// <summary>Worker thread only. Leaves the presenter ready for <see cref="CreateDevice"/>.</summary>
    void ReleaseAll()
    {
        ReleaseTargets();
        ReleaseGuestTexture();
        Release(ref _linearSampler);
        Release(ref _pointSampler);
        Release(ref _constants);
        Release(ref _vertices);
        Release(ref _inputLayout);
        Release(ref _ps);
        Release(ref _vs);
        // Unbind and flush so the swap chain is destroyed now, not deferred (a new one may be made for this HWND).
        if (_context is not null) { _context->ClearState(); _context->Flush(); }
        if (_swapChain is not null) { _swapChain->Release(); _swapChain = null; }
        if (_factory is not null) { _factory->UnregisterOcclusionStatus(_occlusionCookie); Release(ref _factory); }
        _occluded = false;
        if (_waitable != HANDLE.Null) { PInvoke.CloseHandle(_waitable); _waitable = HANDLE.Null; }
        if (_context is not null) { _context->Release(); _context = null; }
        if (_device is not null) { _device->Release(); _device = null; }
        _vp = default;
        _guestW = _guestH = _guestX = _guestY = 0;
        _loggedComposition = false;
    }
}
