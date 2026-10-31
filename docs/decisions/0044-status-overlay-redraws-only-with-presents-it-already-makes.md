# 0044. A toggleable status overlay that redraws only with presents the viewer already makes

- Status: Accepted
- Date: 2026-10-31
- Decided by: agent (spec silent; the human asked for "a toggleable text overlay with debug or status information"),
  reviewed by Codex ([../reviews/2026-10-31-status-overlay.md](../reviews/2026-10-31-status-overlay.md)). Decided by:
  the human (2026-10-31) on the two points that depart from the spec:
  - the letterbox path's overlay blend shader ("允許"); spec.md (Scaling) and 0017 updated;
  - a 1 s refresh tick, "only enable when needed" (the **Refresh tick** bullet below); spec.md (Energy-efficiency
    rules) updated.

  The windowed check is pending.

- **Context:**
  - The human asked whether the viewer can overlay toggleable debug or status text. The spec says nothing about one.
  - Its energy rules bind any such overlay: present only on change, no render loop, no timer-driven presents, no
    polling, no per-frame allocation ([spec.md](../design/spec.md), Energy-efficiency rules; [0009](0009-allocation-scope-of-the-hot-paths.md)).
  - The normal (direct) presentation path must not use shaders, and the letterbox path has one draw
    ([0017](0017-two-presentation-paths-direct-and-one-letterbox-shader.md)). The presenter recreates its device
    once on loss ([0018](0018-presenter-owns-its-graphics-objects-device-loss-recreates-on.md)).
  - Guest pixels must stay as the guest drew them: replays, PNG snapshots and goldens read the canvas.
- **Decision (toggle):**
  - Off by default. The window menu has **Show status overlay** (checked when shown), and **Ctrl+Alt+Shift+F12**
    toggles it.
  - The hotkey is checked in `CheckHotkeys` after the `.vv` hotkeys, so it works wherever they do: through the
    low-level keyboard hook while it captures, and through WM_KEYDOWN otherwise. A `.vv` hotkey with the same keys
    wins. Like toggle-fullscreen, it lets go of the modifiers already sent to the guest; F12 itself never reaches it.
  - Why these keys: they clash with no default `.vv` or virt-viewer hotkey (Shift+F11, Ctrl+Alt+R, Ctrl+Alt+Ins,
    Shift+F12, Shift+F8/F9). Windows and common applications don't use them. Linux switches consoles with
    Ctrl+Alt+Fn, but without Shift. A function key also produces no AltGr character, which Ctrl+Alt+letter can.
  - Known interaction: a `.vv` file whose release-cursor is modifier-only (Ctrl+Alt) fires that first, on the way to
    the overlay hotkey. The menu item avoids it.
  - Saved for all VMs as `statusOverlay` in settings.json, like the other window-menu settings (0024). A file without
    the key, or with a value other than true, reads as off.
- **Decision (content):** up to 18 lines of up to 80 characters, monospace (Consolas), in the top-left corner of the
  shown guest image:
  - the `.vv` file's own `title` (cut to 32 characters; "(untitled console)" when it has none, never the host the
    window caption falls back to, review SO3), the local time the figures were taken, and the span the rates cover;
  - `conn`: view state (connecting, connected, display off, display failed, disconnected), TLS, whether a proxy is
    used, linked channels, mouse mode (client or server), whether the agent is connected;
  - `disp`: the monitor area, the surface when it is larger and the area's offset, whether the area was inferred
    (0035) or named by a monitors config, fit or 1:1, and the client-pixels-per-guest-pixel scale;
  - `pres`: direct or letterbox, the buffer size, presents per second, the adapter name and whether it is a software
    adapter;
  - `net`: received bytes per second and messages per second, all channels, and the display channel's bytes per
    second;
  - `video`: the number of streams and the pacing mode. For each of the first four streams: its id, codec, decode
    path (libjpeg-turbo, hardware through Media Foundation, software through FFmpeg, or not decoded), size, shown frames
    per second, and lifetime frames shown, dropped and hidden. For a hardware stream, a second line names the MFT the
    probe found;
  - `audio`: playing or idle, codec, rate, channels, and the server's PLAYBACK_LATENCY;
  - `mem`: private bytes (K32GetProcessMemoryInfo), the GC heap (`GC.GetTotalMemory(false)`), and the last trim (0041)
    with its reason and age.
  - Left out:
    - the ticket, the password, the proxy's address and the host. The title is the `.vv` file's own, which the
      window caption shows too; without one the overlay says "(untitled console)".
    - A/V offset: no per-frame figure exists. Pacing `clock` lag is the server's lead (0025), which the viewer
      doesn't measure.
- **Decision (energy):** apart from the refresh tick below, the overlay never wakes the presenter on its own.
  - **Redraw points.** The presenter refreshes the overlay only on a wake it has anyway, before it takes the canvas
    lock (`Presenter.PrepareOverlay`). A refresh happens (`StatusOverlay.ShouldRefresh`):
    - when forced: the overlay was just shown; a full redraw was asked for (resize, restore, activation, a new
      surface, the display-off and disconnect images); the layout's path, buffer or scale changed; or the window or
      session reported a non-rate change. That last one is `Presenter.InvalidateOverlay`, called on the session's new
      `StatusChanged` (stream create/destroy/destroy-all, decoder path chosen, playback start/stop/latency, a memory
      trim), on a mouse mode or agent change, and on a video pacing change.
    - otherwise only when guest changes are pending, so the wake will present anyway, and at least
      `OverlayRates.IntervalMs` (1 s) has passed since the last rate sample.
  - **Presents.** The text is formatted into a fixed character grid and compared with the text shown
    (`OverlayText.Commit`). The image is redrawn only when the text or the font size changed. An overlay-only change
    presents just the overlay's rectangle (and the one it left), with dirty rects.
  - **Rates.** Rates are counter deltas over the time since the previous sample, taken only at a refresh. While the
    guest is static nothing refreshes. The overlay then keeps its last figures, and its first line shows when they
    were taken and the span the rates cover, so after a still period the next rates are long-span averages. The user
    README says so.
  - **Refresh tick (the human, 2026-10-31).** A 1 s one-shot thread-pool timer (`OverlayTicker`, armed for 1016 ms
    so the rates are due) refreshes the overlay while its figures move.
    - **Arming.** After every overlay refresh, whatever caused it, the ticker re-arms if `StatusOverlay.Live`, and
      otherwise stops. Live means one of:
      - bytes arrived since the refresh before (this covers rates still falling to 0);
      - a video stream exists;
      - audio is playing;
      - it is the first refresh after the overlay was shown.
    - **Stopping.** The first tick that finds none of these shows the final, zero figures once and arms nothing.
    - **Waking again.** A stopped tick is re-armed only by a refresh from an existing path: a guest present with due
      rates, a session or window event, the overlay being shown, a full redraw on restore. Data that neither presents
      nor raises an event (an input ack, a ping) doesn't re-arm it. Nothing polls.
    - **Minimized or occluded.** The worker skips the wake, so nothing refreshes and nothing re-arms. Restoring
      redraws in full, which re-arms it.
    - **Hidden.** Hiding the overlay stops the ticker.
    - **What a tick presents.** A tick is a forced refresh. It presents only if the text changed, and then only the
      overlay rectangle. Overlay-only presents are counted apart (`Presenter.OverlayPresentCount`) and left out of
      the presents per second shown.
    - **Effect.** An idle guest with the overlay shown reaches zero wakeups within about 2 s: at most two ticks
      after the last data. During video, ticks and present-driven refreshes take turns; each re-arms for a second
      later, so there is still one refresh a second.
    - **Tests.** Fake timer and simulated clock (`StatusOverlayTickTests`).
  - **Cost when hidden.** None in the presenter. The session keeps two counters per channel, incremented by the
    receive loop, and one per stream.
  - **Cost when shown.** During continuous video, one GDI text redraw per second. On the direct path, a CPU blend of
    the overlay rectangle in the presents whose uploads touch it, and in the two presents after it changes. On the
    letterbox path, one more draw per present.
  - **Resizes (review SO1).** An aspect-locked resize on the direct path presents nothing: DXGI stretches the
    unchanged buffer. The overlay follows that rule. On such a layout-only wake it keeps its image, font size and
    place, even when the new window size calls for another font size. The new size is applied at the next present
    the viewer makes anyway, or at the next overlay change. Meanwhile DXGI stretches the overlay with the guest.
    A layout change that alters the path, buffer or scale presents in full anyway, and the overlay is redrawn then.
- **Decision (rendering):**
  - **Image.** GDI draws the text, white with grayscale antialiasing, into a DIB section kept by the presenter thread
    (`OverlayBitmap`). The DIB is converted in place into premultiplied BGRA over a black background of alpha 0xB8.
  - **Size.** The font is 13 px and the margin 8 px at 96 DPI, scaled by the window's DPI and by buffer pixels per
    client pixel, so the text has the same size on screen on both paths, whatever the guest's scale.
  - **Direct path.** It stays shader-free. After the guest uploads, the presenter blends the overlay over the canvas
    pixels of its rectangle on the CPU (`OverlayBlend.Row`) into a reusable scratch buffer, then uploads that box into
    the back buffer. It does this whenever guest pixels under the overlay were uploaded raw (this update's or the
    previous one's rects, flip model) and in each of the two flip buffers after the image changes or moves. A
    rectangle the overlay left is uploaded raw, in this buffer and the next.
  - **Scroll rects.** A COPY_BITS scroll whose source or destination meets the overlay becomes plain dirty rects:
    where the overlay is now, and where the previous frame showed it when it moved or shrank in this update (review
    SO2). Otherwise the compositor or RDP would move overlay pixels with the guest's.
  - **Letterbox path.** The whole destination is redrawn every update. The overlay is one more draw after it: the
    letterbox vertex shader with its own constant buffer; a new pixel shader `OverlayPS` that samples with alpha
    (fxc `ps_4_0_level_9_3`, embedded like the letterbox shaders, source and hash in `Shaders.cs`); the point sampler;
    and a blend state of ONE / INV_SRC_ALPHA. The overlay texture is uploaded only when the image's version changes.
  - **Placement.** Inside the shown guest image (not the bars, which are cleared only after a layout change and
    would accumulate the blend), cut to it.
  - **Device loss.** The GPU objects go with the device. The GDI image survives, and the texture is recreated and
    re-uploaded on the full redraw after recovery. WARP needs nothing special: feature level 9_3 shaders and blending.
  - **Guest pixels.** The canvas, its PNG snapshots and the goldens never contain the overlay. Turning the overlay off
    asks for a full present, which removes it.
  - **Other states.** It shows over everything the presenter shows: the guest picture, the display-off image and the
    dimmed disconnected frame. Before the first surface the window paints its status with GDI and has no presenter,
    so the overlay isn't shown there.
- **Decision (allocation and threads):**
  - Formatting writes into spans (`ISpanFormattable.TryFormat`, invariant culture). The snapshot, rates, text,
    stream array, DIB and blend scratch are reused, and the DIB and scratch only grow. A warm refresh, redraw and
    blend allocate 0 B (test).
  - The session's readers allocate nothing (test):
    - `ReadVideoStreams` takes the stream lock with `TryEnter`. While a frame is being decoded it returns -1, and the
      overlay keeps the streams it showed;
    - `ReadTraffic` takes the channel-list lock;
    - `Audio` and `LastTrim` read fields.
  - The window's figures are read on the presenter thread without locks: single fields, where a refresh racing a
    change shows the old or the new value. The canvas lock is taken briefly and never while holding another session
    lock.
- **Reason:**
  - It piggybacks on presents that happen anyway, so a static guest costs nothing. That is principles §1, and the
    spec's present-only-on-change rule holds.
  - GDI into a DIB is the smallest addition to a presenter that already renders the display-off message with GDI.
    DirectWrite would add a dependency and Direct2D interop for a dozen lines of ASCII.
  - Compositing in the swap chain keeps the overlay in the same frame as the guest. Full screen keeps independent
    flip, the overlay works through RDP and WARP like the guest picture, and no second window has to follow the main
    one.
  - The CPU blend keeps the direct path shader-free, as the spec requires.
  - Measured once on the development machine (JIT Release build, scalar code; provisional until re-measured in a
    clean environment):
    - the blend takes 0.16 ms for a 400×250 rectangle, paid only by presents whose uploads touch it;
    - a GDI redraw of 12 lines (458×190 px) takes 0.26 ms, at most once a second.
- **Rejected:**
  - **A layered or DirectComposition window over the client.** It doesn't wake presents either, but:
    - it is a second HWND to keep placed through moves, full screen, minimize and DPI changes;
    - it breaks independent flip in full screen while shown;
    - its updates would need their own scheduling, separate from the frames they describe.
  - **A shader draw on the direct path.** It would put a shader on the path the spec keeps shader-free (a MUST-level
    rule), for a cost the CPU blend doesn't have.
  - **A free-running refresh timer.** It would keep waking an idle viewer while the overlay is shown. The human
    chose the conditional tick above instead.
  - **Updating the rates on every present.** The text would flicker at the frame rate and be redrawn 60 times a
    second, for no information.
- **Tests:**
  - `tests/Spice.Diagnostics.Tests/StatusOverlayTests.cs` (window-free; a GDI memory DC only) covers:
    - every line's text and that no secret is in it;
    - cutting long titles and lines;
    - the refresh truth table, including that a static guest never refreshes;
    - that an unchanged text isn't redrawn and a non-rate change is;
    - rate spans, and fps for new streams;
    - premultiplied blending;
    - the image's alpha and premultiplication;
    - 0 B for warm refresh, redraw and blend.
  - `tests/Spice.Diagnostics.Tests/StatusOverlayTickTests.cs` covers the refresh tick with a fake timer and a
    simulated clock:
    - an idle guest has no wakeups after the first tick, within 2 s;
    - data keeps it ticking, and it stops one tick after the data ends, showing zero rates;
    - a stream or playback keeps it ticking;
    - a stopped tick is re-armed only by a refresh from an existing path;
    - an unchanged tick presents nothing, and hiding the overlay disarms it.
  - `tests/Spice.Diagnostics.Tests/StatusOverlayReviewTests.cs` (Codex) covers the review findings SO1–SO3.
  - `tests/Spice.Replay.Tests/StatusOverlaySessionTests.cs` covers the stream figures through a stream's life,
    `StatusChanged` on stream end and not per frame, and 0 B for the session readers.
  - `tests/Spice.Video.Tests/StatusOverlaySettingsTests.cs` covers the setting's default, round trip and lenient
    reading.
  - The presenter's compositing needs a window and is part of the pending windowed check.
