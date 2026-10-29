# SpiceViewer review against CLAUDE_DRAFT.md

Reviewed 2026-10-29. Source review only: no production/native files changed, no
GUI executable started, no server contacted, no credentials read, and no commit.
No tests were added or run: the findings below concern UI-message ordering,
unmanaged resource ownership and hardware paths which a simulated GUI would not
validate. Failure scenarios are deductions from the cited code, not observations
from a running viewer.

## 1. high — Queued hook events can re-press keys after focus-loss cleanup

**Location:** `src/SpiceViewer/KeyboardHook.cs:58`, `:90` through `:100`;
`src/SpiceViewer/MainWindow.Input.cs:31` through `:42`, `:95` through `:108`.

The hook checks the foreground window when it enqueues an event, but Drain
forwards every queued event without checking focus or a capture generation.
OnFocus(false) releases guest keys and clears local modifiers, yet OnKey accepts
ordinary keys whenever the session is connected, even when _focused is false.
A key-down queued just before a synchronous focus-loss notification can therefore
be drained after cleanup and sent to the guest again. Its physical key-up may
go to the new foreground application, leaving the guest key pressed. Pending
modifier downs can also repopulate _modKeys after it was cleared.

**Suggested fix:** invalidate the hook queue on every capture/focus transition,
tag events with a capture generation, and reject stale events on the UI thread.
Gate ordinary guest input on current focus/capture ownership. Do not replay old
downs after releasing the guest's held state.

## 2. high — Mouse-button state is not released on release hotkey or capture loss

**Location:** `src/SpiceViewer/MainWindow.Input.cs:64` through `:70`, `:199`
through `:207`, `:276`; `src/SpiceViewer/MainWindow.cs:169` through `:247`.

ReleaseInput releases keys but never calls ReleaseAllButtons. ReleasePointer
only unregisters raw input/unclips in server mode; it never calls ReleaseCapture,
and returns immediately in client mode. The window has no WM_CAPTURECHANGED or
WM_CANCELMODE handling. During a guest drag, releasing input, opening a modal
menu, or losing capture to another window can thus leave the guest button down
when Windows routes the eventual mouse-up elsewhere. Focus loss happens to call
ReleaseAllButtons, but capture loss need not change keyboard focus. Disconnect
also goes through UninstallCapture without a button/key release attempt.

**Suggested fix:** centralize input cancellation: release guest buttons/keys
where the channel is still usable, clear local held state, ReleaseCapture,
unregister raw input, and ClipCursor(NULL). Call it from release, capture-cancel,
focus loss, disconnect and shutdown paths. Handle normal ReleaseCapture's own
WM_CAPTURECHANGED idempotently. See [WM_CAPTURECHANGED](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-capturechanged).

## 3. high — Network writes on the UI thread can disable the release hotkey

**Location:** `src/SpiceViewer/KeyboardHook.cs:58`;
`src/SpiceViewer/MainWindow.Input.cs:108`, `:247`, `:275`;
downstream `src/Spice.Transport/SpiceChannel.cs:202`.

Hook draining, normal window keys and mouse handlers call session send methods
synchronously. SpiceChannel.Send holds its send lock while doing Stream.Write
and Flush without a write deadline. If the peer stops reading and buffers fill,
the UI thread can block inside an ordinary key/mouse event. The hook thread
continues swallowing system shortcuts and enqueuing events, but the UI cannot
process the queued release hotkey, focus-loss cleanup or Close. The unbounded
hook/UI queues also retain more data while stalled. Keeping the hook callback
itself short does not prevent this failure.

**Suggested fix:** use a bounded, ordered outbound-input queue and a network
writer independent of the UI thread, with failure/cancellation handling. Keep a
local capture-release path responsive even when sending release packets cannot
complete; do not block local unhook/unclip behind network I/O.

## 4. high — Per-present stackalloc accumulates for the lifetime of the render thread

**Location:** `src/SpiceViewer/Presenter.cs:170` through `:175`, `:234`.

Run contains an effectively unbounded loop, and each partial present executes
stackalloc RECT[rects.Count] inside that loop. The allocation's stack storage is
not reclaimed after the iteration: it lasts until Run returns. Ordinary long
sessions with repeated dirty updates can therefore exhaust the thread stack and
terminate the process with StackOverflowException. The threshold depends on the
stack reserve and number of rectangles; it was not measured or triggered.

**Suggested fix:** allocate a fixed RECT[MaxRects] stack span once outside the
loop, or use reusable unmanaged/pinned storage. See Microsoft's
[CA2014 rule](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca2014).

## 5. high — Shutdown releases graphics objects even if the render thread is still running

**Location:** `src/SpiceViewer/Presenter.cs:264` through `:273`;
`src/SpiceViewer/MainWindow.cs:512`.

Dispose runs from WM_DESTROY, blocks the message-pump thread in Join(2000), ignores
the join result, and then releases/nulls COM pointers and closes the latency
handle. Present1 can itself wait for the message-pump thread. A driver stall or
that cross-thread wait can exceed the timeout; the worker then resumes against
released graphics objects/closed handles, or reads a now-null pointer. This is a
native lifetime race, not a safe bounded shutdown. The stop flag does not protect
calls already executing after its last check.

**Suggested fix:** let the worker own and release its graphics resources in a
finally block, use a stop-aware wait, and arrange shutdown without blocking the
UI message pump needed by DXGI. Never release resources until worker termination
is established. Microsoft documents the [Present/message-pump dependency](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-present).

## 6. high — Graphics failures can terminate the process or retry a dead device indefinitely

**Location:** `src/SpiceViewer/Presenter.cs:170`, `:204` through `:215`, `:250`.

The background Run method has no exception boundary. CreateSwapChainForHwnd,
ResizeBuffers and GetBuffer use generated CsWin32 wrappers that throw on failure;
these failures occur after the constructor returned, outside OnPrimaryChanged's
presenter-construction catch. A device loss or resize/allocation failure can
therefore escape the worker and terminate the process. Where Present1 instead
returns a failing HRESULT, the code signals its own wake event and immediately
retries the same device without recovery. That can repeatedly render/log despite
no new guest updates, violating the energy rule and never restoring display.

**Suggested fix:** add a top-level worker failure/finally boundary; distinguish
device removal/reset from transient failures, recreate device/swap-chain state
when appropriate, or report a terminal error to the UI and sleep/stop. Do not
self-signal an unbounded retry loop on a permanently invalid device.

**Binding check:** inspected generated wrappers: ResizeBuffers/IAudioClient
methods throw through ThrowOnFailure, while Present1 preserves its HRESULT.
The report does not mistake omitted explicit HRESULT checks on throwing wrappers
for unchecked-return bugs.

## 7. high — Clipboard conversion performs unbounded reads from HGLOBAL memory

**Location:** `src/SpiceViewer/MainWindow.Session.cs:275`, `:320`, `:350`
through `:373`.

DibToPng dereferences a BITMAPINFOHEADER and computes an arbitrary pixel pointer
from biSize/biClrUsed without checking GlobalSize or validating that headers,
masks and all rows fit the allocation. Even a tiny DIB can claim 1x1 RGB32 and
an enormous header offset, causing an unmanaged read outside its allocation.
The text path similarly constructs new string(p) without finding a terminating
NUL within the clipboard block. This can crash the process or expose adjacent
readable process data in clipboard data sent to the guest.

There is also a guest-controlled entry point: ImageBmp data is accepted by
stripping 14 bytes and publishing the remainder as CF_DIB without validating its
header/pixel storage. A subsequent guest clipboard request reaches DibToPng.
Whether Windows rewrites or rejects a particular malformed clipboard block was
not exercised; the converter's missing memory bounds are directly visible.

**Suggested fix:** obtain GlobalSize before any dereference, require the complete
header and supported compression/header variants, and use checked bounds for all
offsets/row sizes. Bound UTF-16 scans by the block length. Validate incoming BMP
before publishing it, check GlobalAlloc/GlobalLock results, and unlock/free in
finally blocks on every failure path. Do not blindly add external bit masks to
V4/V5 headers that already contain them.

## 8. med — Auto-repeat retriggers local release/fullscreen hotkeys

**Location:** `src/SpiceViewer/MainWindow.Input.cs:97`, `:145` through `:163`.

CheckHotkeys runs on every down event before _localDown is updated, with no
first-down or key-up latch. Holding a release hotkey can toggle _released on
successive repeats; after the first release, normal WM_KEYDOWN repeats can invoke
the same check and recapture input. Holding the fullscreen shortcut repeatedly
queues fullscreen transitions and creates/destroys the strip. An autorepeat must
not undo the user's attempt to escape guest capture.

**Suggested fix:** maintain physical press/repeat state before hotkey dispatch,
fire once per press, and consume the matching release appropriately. Reset the
latch safely on focus/capture cancellation.

## 9. med — Released input still forwards client pointer motion and wheel events

**Location:** `src/SpiceViewer/MainWindow.Input.cs:236` through `:244`, `:281`
through `:289`.

These paths only test session/state, not _released or focus. After ReleaseInput,
moving over the client still changes the guest mouse position in client mode,
and a wheel gesture still scrolls the guest without the recapture click promised
by the window title. This can change guest content while the user believes input
has been released. It is independent of the held-button cancellation defect.

**Suggested fix:** share a capture/ownership predicate across all input paths;
while released, accept only explicit local recapture gestures and keep motion,
wheel and raw-input traffic out of the guest.

## 10. med — The 1:1/software profile and letterboxing are not implemented by the presenter

**Location:** `src/SpiceViewer/MainWindow.cs:325`, `:473` through `:474`;
`src/SpiceViewer/Presenter.cs:104`, `:181` through `:185`.

_scaling only changes interactive sizing and an initial SizeWindowToGuest call;
the presenter always stretches the guest-size swap chain to the whole client
area. Selecting 1:1 then maximizing/fullscreening still stretches pixels. Fit mode
also stretches mismatched aspect ratios instead of the draft's letterboxing.
On WARP, IsSoftware only limits frame rate: it never forces the required 1:1
profile. The corresponding menu settings therefore do not describe actual
render behavior in these states.

**Suggested fix:** pass scaling/profile state to the presenter, implement a
destination viewport with letterboxing or 1:1 placement, and use exactly that
viewport for ToGuest/cursor positioning. Enforce the software profile rather
than exposing a setting that still stretches.

## 11. med — Native cursor cache returns stale shapes when guest IDs are reused

**Location:** `src/SpiceViewer/Cursors.cs:17` through `:21`;
`src/SpiceViewer/MainWindow.Session.cs:77` through `:88`.

The native cache treats unique ID plus scale as an immutable shape identity.
Guest cursor cache invalidation/reset is not propagated to this cache, and
uncached cursor updates also enter it. A new CursorShape with the same Unique
after invalidation (or a noncached ID reused for multiple shapes) therefore gets
the old HCURSOR, showing the wrong pointer/hotspot. Additionally, XOR-packing
arbitrary 64-bit IDs and scale bits is not a collision-free composite key.

**Suggested fix:** include a shape generation/content identity and explicit scale
fields in the key, propagate invalidation/reset, and do not reuse native handles
for a different shape solely because the numeric guest ID matches.

## 12. med — Pause is encoded as Ctrl/NumLock rather than an E1 Pause sequence

**Location:** `src/SpiceViewer/MainWindow.Input.cs:101` through `:104`;
downstream `src/Spice.Session/SessionInputs.cs:51` through `:65`.

OnKey requests SendCombo(0xE11D, 0x45), but SendKey/ClientMsg.KeyCode only recognize
the E0 prefix. The E1 bits are discarded and the guest gets ordinary Ctrl and
NumLock make/break codes, not the Pause sequence. This can toggle guest NumLock
or invoke the wrong Ctrl combination. The normal extended-key support does not
cover E1.

**Suggested fix:** represent Pause as its actual multi-byte XT sequence on the
raw-scancode path and implement the negotiated legacy equivalent explicitly.
Do not express E1 input through an API that only supports E0-prefixed keys.

## 13. med — Resizing/DPI changes do not rebuild the current scaled cursor

**Location:** `src/SpiceViewer/MainWindow.Session.cs:83` through `:86`;
`src/SpiceViewer/MainWindow.cs:201` through `:209`, `:345` through `:354`.

The HCURSOR's size/hotspot is computed using client-to-guest scale only when a
new guest cursor event arrives. Resize, maximize and DPI-driven client-size
changes do not rebuild that cursor. With a static guest cursor shape, the
framebuffer scales immediately while the cursor and hotspot remain at the old
scale indefinitely. A scale-aware cache does not help when it is never queried
again after the scale changes.

**Suggested fix:** retain the current shape and refresh its native cursor after
viewport/client/guest-size/DPI changes, using the same transform as rendering
and input mapping. Reapply the active cursor after rebuilding it.

## Checks without additional confirmed findings

- PerMonitorV2 is declared in app.manifest; WM_DPICHANGED applies the suggested
  rectangle and rebuilds the two fonts. Actual monitor transitions were not run.
- WM_INPUT is deliberately passed to DefWindowProc for foreground raw-input
  cleanup. X-button messages return TRUE; paint uses BeginPaint/EndPaint.
- Normal presenter waiting is event-driven. The guest-resize timer kills itself
  when it fires, and no ordinary idle-period polling timer was found. Audio's
  two-second timeout is only in an active playback worker, not after Stop.
- Invalidate takes the pending lock briefly; the render path takes Canvas.Sync
  then the pending lock, matching the canvas invalidation call order. Previous
  dirty rectangles are uploaded to repair the alternate flip buffer, and a full
  update clears prior history. No separate proven dirty-rectangle math defect
  is asserted without rendering/PresentMon validation.
- The occlusion branch marks a full redraw but does not gate later uploads and
  presents. Actual notification behavior is unverified: Microsoft notes that
  flip-model swap chains generally do not return DXGI_STATUS_OCCLUDED. Visibility
  handling should be tested, not inferred from this branch's presence.
- Cursor color/mask bitmaps are normally deleted after CreateIconIndirect;
  cached HCURSORs are destroyed on Clear/Dispose. No observed OS handle-leak
  measurement is claimed. Creation-failure paths still need native fault testing.
- Successful clipboard opens in the ordinary request/publish paths have
  CloseClipboard in finally blocks. This does not resolve finding 7's unsafe
  memory accesses or prove behavior under allocation/clipboard contention.
- Audio owns its COM pointers on its worker, uses an unmanaged ref-counted
  notification object, unregisters before releasing it, and joins the worker
  before disposing wait handles. Generated IAudioClient wrappers throw on
  HRESULT failures, so omitted explicit ThrowOnFailure calls are not themselves
  bugs. The ring-buffer Write/Fill path uses reusable storage. No additional
  proven WASAPI lifetime defect is asserted from source inspection alone.

## Explicitly unverified

No GUI, audio harness, injected input, server session, or physical device test was
run. In particular this review does **not** verify: AltGr's actual synthetic Ctrl
scan/flags on this keyboard layout; host behavior for Win+L/Ctrl+Alt+Del and
secure-desktop transitions; hook timeout/removal behavior; OS capture release
after process crashes; FancyZones/monitor/DPI transitions; WARP/driver/device-loss
recovery; real flip-buffer/dirty-rect presentation, occlusion and power usage;
audible PCM playback, endpoint event cadence or default-device switching. The
findings should guide focused human-observed tests after the corresponding fixes.
