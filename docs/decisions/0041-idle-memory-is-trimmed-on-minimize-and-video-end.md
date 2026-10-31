# 0041. Idle memory is trimmed when the window is minimized and when the last video stream ends

- Status: Proposed
- Date: 2026-10-31
- Decided by: agent (spec silent), not yet reviewed

- **Context:**
  - Finding K1 of [2026-10-30-v0-dod.md](../verification/2026-10-30-v0-dod.md): after a busy period the idle viewer
    kept 100–180 MB more private bytes than at connect, and never gave them back.
  - [principles.md](../design/principles.md) §2 asks to release what an idle session doesn't use. Its §1 (energy)
    ranks higher: no timers, no polling.
  - The spec says nothing about when caches and pools may be emptied.
  - Measurements are in [2026-10-31-idle-memory.md](../verification/2026-10-31-idle-memory.md). The growth was:
    - the GLZ window's reuse pool (up to 32 MiB of retired images);
    - spare decode buffers in `ArrayPool<byte>.Shared`, 15–71 MB. The shared pool trims itself only during gen-2
      collections spread over minutes, and an idle viewer allocates nothing, so it never collects;
    - the GLZ window's own images, about 32 MB. The protocol requires them;
    - the GPU driver's allocations, 25–40 MB on the AMD device and none under WARP.
- **Decision:**
  - **Owned pool.** Decode and draw buffers larger than 32 KiB come from `Spice.Canvas.PixelBuffers`, not
    `ArrayPool<byte>.Shared`. These are decoded images (stream frames included), coverage masks and decode scratch.
    - The pool keeps power-of-two arrays: at most 16 per size class and 64 MiB in all.
    - `Trim()` drops every spare array.
    - Requests of 32 KiB or less still use the shared pool.
  - **GLZ pool trim.** spicecodec.dll exports `sc_glz_window_trim`, which frees the GLZ window's reuse pool. The
    window's own images stay, because later GLZ images may reference them.
    - The export is optional. A version-2 DLL without it still loads, and the viewer then trims nothing there.
  - **`SpiceSession.TrimMemory(reason, force)`.** The steps, in order:
    1. trim the GLZ pool;
    2. trim `PixelBuffers`;
    3. run one `GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)`. This frees the dropped
       arrays and returns the GC's free space to the system.
    4. log one line with the bytes released and the time taken.
  - **When it runs, on existing events only:**
    - the window is minimized (`force: true`);
    - the last video stream ends (STREAM_DESTROY of the last stream, or STREAM_DESTROY_ALL while streams existed).
      This is rate-limited: a trim that isn't forced is skipped within 30 s of the previous one, so streams that come
      and go don't each pay for a collection and a re-warm. The check reads `Environment.TickCount64` at the event.
      No timer is involved.
    - A request that arrives while another trim is running is skipped, forced or not, so two blocking collections
      never run back to back (review K1-R1). The running trim does the same work.
  - **What is never released:** GLZ window images, the image cache, palettes, surfaces and the stream frame of a
    live stream.
- **Reason:**
  - Measured on the AMD host:
    - the K1 workload on win81 dropped from 161 MB to 128 MB on minimize;
    - a mixed workload dropped from 208 MB to 160 MB (the old build held 264 MB, minimized or not);
    - on win7, the MJPEG run dropped back to the connect level when the video ended.
  - The owned pool also reuses arrays better than the shared pool. Before any trim, a busy period leaves 34 MB of GC
    heap committed instead of 81 MB.
  - A trim takes 0.7–7 ms, on the UI thread at minimize or on the display thread after a video. The only costs:
    - the next busy period allocates its pools again, as after connect;
    - the GC runs at a moment we choose.
  - The pool's lock adds no allocation. The allocation tests stay at 0 B, and a new test checks warm rent and return.
- **Rejected:**
  - **GC settings.** Concurrent GC off, `GCgen0size` 4 MB and `GCConserveMemory` were tried. They changed the
    post-busy GC commit within the run-to-run noise (32–44 MB) and left the large native part alone. The GC heap is
    live pooled arrays, not gen-0 garbage, because the hot paths don't allocate.
  - **Trimming on a timer or after an idle delay:** forbidden by the energy rules.
  - **Occlusion as a trigger:** a windowed flip-model present did not report `DXGI_STATUS_OCCLUDED` when another
    window covered the viewer, so the trigger could not be verified.
  - **`IDXGIDevice3::Trim` on minimize:** private bytes unchanged.
  - **`HeapCompact`:** private bytes unchanged.
  - **Playback stop and surface destroy as triggers:**
    - playback stop: closing the WASAPI stream already frees the audio memory, and an audio event says nothing about
      the display;
    - surface destroy: it comes with resolution changes, when the display is busy.
  - **A smaller GLZ dictionary** (the client advertises 8M pixels, 32 MiB): it trades compression for memory. The
    protocol requires the window's images, so this is the human's call.
- **Not addressed:** a session that stays visible after a busy period, without a video ending, keeps:
  - its pools: at most 32 MiB of GLZ pool and 64 MiB of spare pixel buffers;
  - the driver's memory.
  No existing event says the guest went idle.

## Answers (the human, 2026-10-31, through agentdesk)

- Keep the trim (GLZ window, pooled pixel buffers) but drop the forced `GC.Collect`: the GC decides when to
  collect. The record moves to Accepted when that change lands.
