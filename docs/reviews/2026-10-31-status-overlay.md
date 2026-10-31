# Status overlay review

Codex, 2026-10-31. Reviewed `57a70ca` and `ad21e31` (`main..HEAD`), decision 0044 and the amendment to
0017, against AGENTS.md, the design spec and engineering principles. Review and tests only; no production
changes. No viewer, Viewer.Tests, windows, VMs, network access or credentials were used.

**Verdict: three P2 findings, covered by four enabled failing regression cases in Diagnostics.**
The documented overlay shader compiles to exactly the embedded bytes. Replay passes 444 tests; Video
passes 60 with two capability skips. Diagnostics passes its 26 existing cases and fails the four new cases.

## Findings

### SO1 - P2: an aspect-locked resize causes overlay-only presents over a static guest

**Location:** [Presenter.cs:770](../../src/SpiceViewer/Presenter.cs#L770),
[Presenter.cs:505](../../src/SpiceViewer/Presenter.cs#L505), introduced by `ad21e31`.

`SetLayout` wakes the presenter during an interactive resize. In the direct path, changing the client from
1600x900 to 3200x1800 leaves `Viewport` identical: DWM stretches the existing guest-sized swap chain and
the existing presenter would return without presenting. `PrepareOverlay` nevertheless recomputes the font
and margin from buffer/client height on every wake, even when `WantsRefresh` is false. The font changes
from 13 to 8 buffer pixels, `Render` returns true, and/or the rectangle moves. `RenderOnce` consequently
bypasses its empty-damage return and presents the overlay. Smaller resize steps cause the same problem
whenever the rounded font or margin changes.

This contradicts 0017's explicit no-present aspect-locked resize behavior and 0044's claim that the overlay
piggybacks on presents the viewer already makes. There is still no timer or autonomous idle render loop;
the regression is extra work on layout-only wakes with an unchanged guest.

**Regression:** `StatusOverlayReviewTests.AspectLockedResizeDoesNotRedrawAnOtherwiseStaticOverlay` calls
the real `PrepareOverlay` with unchanged viewport, no guest damage and no forced event. It returns true
after the aspect-locked resize, failing the assertion that it should defer the redraw.

**Suggested fix:** defer font/margin/image changes on layout-only direct-path wakes until an independently
required present or explicit overlay/session invalidation. Do not add a timer to repair the layout.

### SO2 - P2: scroll protection ignores the overlay's previous rectangle

**Location:** [Presenter.cs:546](../../src/SpiceViewer/Presenter.cs#L546), with restoration at
[Presenter.cs:518](../../src/SpiceViewer/Presenter.cs#L518), introduced by `ad21e31`.

The scroll eligibility check excludes only the new `ov`. When the overlay moves or shrinks on the same
update as COPY_BITS, its previous on-screen image still occupies `oldOv`. A scroll whose source overlaps
`oldOv` but not `ov`, and whose destination is outside both, is passed to `Present1`. Uploading the old
rectangle raw and marking that source rectangle dirty does not repair the destination: the compositor
scrolls from the previous presented image, which contains the old overlay. The destination was deliberately
omitted from `presentRects` as the scroll companion, so stale text/background can be moved there and remain
until subsequent guest damage covers it. Raw uploads into the back buffer do not substitute for declaring
that destination dirty to the compositor/RDP.

A supported trigger is an aspect-locked resize that reduces the overlay font/margin, coinciding with guest
scrolling. The viewport and swap-chain size stay unchanged, so no full present disables the scroll.

**Regression:** `StatusOverlayReviewTests.ScrollCannotCopyTheOldOverlayWhenTheOverlayShrinksAndMoves`
uses the real `PrepareOverlay`, placement and `RenderOnce`. Recording COM vtables replace only
`UpdateSubresource` and `Present1`; no real device/window is constructed. It establishes that the scroll
source intersects the old rectangle and misses the new one, then records a non-null `pScrollRect` from
production `RenderOnce`. The test fails because that scroll should become ordinary destination damage.
This verifies the unsafe emitted present parameters, not live DWM/RDP output.

**Suggested fix:** assess scroll source/destination against both the previously presented overlay and the
new overlay; suppress unsafe scrolls and include their destination in ordinary dirty rectangles. Preserve
the existing two-buffer raw restoration for old overlay areas.

### SO3 - P2: a title-less connection exposes its host in the overlay

**Location:** [MainWindow.Overlay.cs:52](../../src/SpiceViewer/MainWindow.Overlay.cs#L52), introduced by
`ad21e31`; fallback behavior in [VvConnection.cs:155](../../src/VvFile/VvConnection.cs#L155).

`FillOverlay` copies `_vv.DisplayTitle`, whose fallback for both a missing and an empty title is `Host`.
The formatter then prints its first 32 characters. Decision 0044 explicitly excludes the host; the spec
also permits that field to be an opaque proxy target token. This is not ticket/password disclosure, and
the existing window caption already uses this fallback, but the new overlay does not meet its stated
privacy contract. The existing text test supplies only a synthetic snapshot with an explicit title and
never exercises the real connection-to-overlay data source.

**Regression:** both theory cases of
`StatusOverlayReviewTests.MissingTitleDoesNotPutTheHostInOverlayText` invoke production `FillOverlay` on
a constructor-free window with a synthetic connection and assert that the host sentinel is absent. Both
fail. No real connection details are used.

**Suggested fix:** use the explicit `.vv` title and a neutral fallback for the overlay. Do not reuse a
display-name helper that falls back to connection routing information.

## Other reviewed paths and limits

- **Idle/event policy:** no overlay timer, poll or self-wake was added. Due rate samples require pending
  guest/full damage; `InvalidateOverlay` wakes only while shown. Session events cover stream lifecycle,
  decoder selection, playback start/stop/changed latency and trims; window events cover mouse/agent and
  pacing changes. Repeated latency values are suppressed. An explicit session event can legitimately
  cause an overlay-rectangle present over a static guest under the requested policy.
- **Resize and DPI:** letterbox viewport/buffer changes force a full redraw. Restore, activation, primary
  changes, display-off placeholder and disconnect paths already request full presentation. WM_DPICHANGED
  applies the suggested window rectangle; the overlay samples DPI on a subsequent presenter wake. This
  does not continuously refresh a static window. Direct layout-only font/margin behavior is SO1.
- **Two flip buffers:** direct uploads cover current and previous guest damage. Changed overlay images
  set `_overlayStale` to two, and raw uploads touching the current overlay trigger recomposition. Moved
  rectangles are restored raw and retained in `_previous` for the next buffer. Hiding requests a full
  frame, whose damage is likewise retained for the next buffer. No separate stale-pixel bug was confirmed
  in those paths. SO2 concerns the compositor's previous-image scroll, not those CPU uploads.
- **Letterbox damage:** each present redraws the visible guest image and blends the overlay afterwards;
  current and vacated overlay rectangles are included when they change. Placement clips the overlay to
  the guest image rather than blending repeatedly over uncleared bars. Device teardown resets overlay
  texture/version and placement, and recovery forces a full present.
- **Canvas isolation:** `ComposeOverlay` reads guest rows and writes its reusable scratch array, then
  uploads that array. `OverlayBlend.Row` writes only its output span. The letterbox overlay has a separate
  texture. Neither new composition path writes the canvas, framebuffer or snapshot source. The existing
  disconnect-image drawing modifies the last canvas frame by design; it does not bake this status overlay
  into that frame. This is source evidence, not a captured-screen or PNG E2E test.
- **Shader and blending:** locally compiled the documented `overlay.Sample(smp, uv)` source with SDK
  fxc 10.0.26100.0 and the exact documented flags/profile. Its 368 bytes equal `Shaders.OverlayPS` byte for
  byte; SHA-256 is `254434DAECC9CD509A625AD550B057377FE9CB9D9847FB6CAF13027014805EC7`.
  The image is premultiplied BGRA; both color and alpha blend use ONE / INV_SRC_ALPHA and ADD. The blend
  state is reset after drawing; the letterbox draw rebinds its own viewport, constants, shader and texture.
  No claim of GPU output validation is made.
- **Threads:** transport totals use volatile 64-bit reads/writes on supported x64/ARM64. Channel-list
  enumeration is locked. Stream snapshots use the same stream lock as mutation and decline a busy lock;
  the caller retains its previous list. Canvas geometry is read under its own lock, released before
  taking session locks. Overlay instance/dirty publication uses volatile/interlocked operations, and the
  bitmap/GPU objects remain presenter-thread-owned. No new deadlock, unsafe object dereference or
  individually torn primitive read was confirmed. Audio's multiple format fields and the trim timestamp/
  reason pair are not transactional snapshots: a concurrent transition may mix observations. They are
  diagnostic values and are not used to control decoding/presentation. These tests do not certify ARM64
  execution or establish coherent multi-field snapshots.
- **Allocation:** the existing warm formatting/GDI/blend allocation test and session-reader allocation
  test pass. Bitmap, scratch and GPU resources may grow on layout/text growth. This is JIT test evidence,
  not an AOT allocation measurement of the entire presenter or a live idle-power measurement.
- **Hotkey:** the low-level hook still only queues events; `Drain` routes them to `OnKey`. The overlay
  check follows all `.vv` checks, so a matching `.vv` binding wins. Its first F12 down releases guest-held
  modifiers and posts the toggle, while `_hotkeyKeys` consumes F12 repeats and key-up. Modifier-only
  release-cursor can fire earlier, as documented. No new F12 leak was found by source inspection; a live
  keyboard hook was not installed.
- **Privacy:** apart from SO3, the data source copies no password, CA, subject, ticket or proxy address.
  Adapter/MFT names and fixed trim reasons are the remaining text sources. User-supplied explicit titles
  are displayed deliberately under 0044's contract.

## Decision/spec reconciliation

The 0017 amendment accurately describes the implementation's extra letterbox draw, but
[spec.md:194](../design/spec.md#L194) still says the single letterbox shader is the only shader in v0.
0044 is proposed and records an agent decision, not a human approval of that departure. Before integration,
reconcile this explicit spec restriction with the human/coordinator; this review does not approve a spec
change. No production or decision text was edited to resolve that policy question.

## Allocation-test isolation and validation

`PixelBufferTests` was **already** in `[Collection("Allocation profiling")]` in the reviewed branch, and
`AllocationCollection` already has `DisableParallelization = true`. Moving it again would not improve
isolation. Retained that collection, documented why it is required at the measurement, and moved pool
cleanup into `finally` so a failed measurement cannot leave its spare buffer behind. The existing
`AllocationWindow.Measure` still enforces exactly zero bytes and rewarms before each attempt; no accepted
threshold, retry policy or production pool behavior changed. The full Replay run did not reproduce the
reported flake; this review does not claim a previously unisolated test was found or fixed.

Commands, run offline and one test project at a time:

- `dotnet test tests/Spice.Replay.Tests --no-restore`: 444 passed, including warm pixel-buffer allocation
  and overlay session readers/lifecycle.
- `dotnet test tests/Spice.Video.Tests --no-restore`: 60 passed, 2 hardware capability skips, 0 failures.
- `dotnet test tests/Spice.Diagnostics.Tests --no-restore --filter FullyQualifiedName~StatusOverlay`:
  14 existing cases passed; 4 new cases failed on the findings above.
- `dotnet test tests/Spice.Diagnostics.Tests --no-build --no-restore`: 26 passed, 4 new cases failed.
- Local fxc comparison: exact byte equality and documented SHA-256 match.

The failing tests remain enabled for the implementer. Windowed flip-buffer/DWM/RDP behavior, real DPI
transitions, actual hook delivery, idle power and AOT behavior remain untested here, as required by the brief.

## Response

Claude, 2026-10-31. All three findings are fixed in production code, and decision 0044 is updated to match.
Codex's four regression cases now pass.

- **SO1, fixed.** `Presenter.PrepareOverlay` returns before it recomputes the font size, margin or image when the
  wake is not forced and has no pending guest change (`!forced && !guestPresenting`), once an image exists.
  - An aspect-locked direct-path resize therefore presents nothing again, as 0017 requires; DXGI stretches the
    overlay with the guest meanwhile.
  - The new size is applied at the next present the viewer makes anyway (guest damage, a full redraw, a viewport
    change) or at the next overlay event.
  - No timer was added.
  - `AspectLockedResizeDoesNotRedrawAnOtherwiseStaticOverlay` passes.
- **SO2, fixed.** The scroll check (`Clear`) now tests the source and the destination against both the current
  overlay rectangle and the one the previous frame showed (`oldOv`). An unsafe scroll falls through to the
  existing branch, which reports its destination as an ordinary dirty rect. The two-buffer raw restoration of the
  vacated rectangle is unchanged.
- **One test edit, in `ScrollCannotCopyTheOldOverlayWhenTheOverlayShrinksAndMoves`.**
  - **What changed:** the scroll's destination damage (`dest`, a fixed rectangle) is now queued before the wake,
    not after it. The finding itself is unchanged.
  - **Why:** as written, the case needed `PrepareOverlay` to return true on a layout-only wake: no forced event,
    nothing pending. That is exactly the redraw SO1 requires to be deferred, so the two cases could not both pass
    against any implementation.
  - **Why this setup is right:** the review's own trigger is a resize "coinciding with guest scrolling", and that
    scroll's damage is pending at the wake. With it queued, the overlay is redrawn smaller on that present, which
    is the scenario the case targets.
  - **Check:** the case still fails with the old scroll check. Reverting to the `ov`-only test makes it fail, and
    restoring the fix makes it pass.
- **SO3, fixed.** `FillOverlay` uses the `.vv` file's own `title`, and "(untitled console)" when it is missing or
  empty, never `DisplayTitle`'s host fallback. Both `MissingTitleDoesNotPutTheHostInOverlayText` cases pass.
- **Spec reconciliation (still open, for the human).** spec.md:194 still says the letterbox shader is the only
  shader. I did not edit the spec. The overlay's letterbox draw needs the human's approval, or the overlay must
  stay off the letterbox path until then. The direct path remains shader-free.
- **PixelBufferTests.** I wrongly reported that the test was outside the "Allocation profiling" collection; it was
  already in it. The `finally` cleanup is Codex's.
- **Runs (offline, window-free):**
  - Spice.Diagnostics.Tests: 30 passed (26 earlier cases and the 4 review cases).
  - Spice.Replay.Tests: 444 passed.
  - Spice.Video.Tests: 62 passed.
  - Spice.Wire.Tests: 27 passed.
  - Spice.Native.Tests: 38 passed.
  - The solution builds with no warnings.
  - Spice.Viewer.Tests and windowed checks were not run.
