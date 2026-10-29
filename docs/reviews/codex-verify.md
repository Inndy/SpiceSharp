# Verification of core, viewer and test-review fixes

2026-10-29. Read current sources and all decisions; added four regression tests.
No production/native edits, viewer execution, server access, credential access,
or commit. Unit tests use private in-memory streams, synthetic agent messages,
and pure viewport math; clipboard tests use private HGLOBAL allocations.
The live-test results supplied with task 8 are not independently reproduced here.

**Disposition:** 19 fixed, four partially fixed, four fixes with a new problem,
and one original requirement not fixed across the 28 original findings. “Fixed”
means the cited defect is addressed in current source, supported where possible
by regression tests; it does not certify every concurrency or hardware path.

## Original core findings

IDs refer to numbered findings in `codex-core.md`.

| Finding | Status | Current evidence / limitation |
|---|---|---|
| C1 TLS server EKU | Fixed | `TlsTrust.cs:54` adds server-auth ApplicationPolicy while retaining CustomRootTrust. The generated-client-EKU rejection regression passes. |
| C2 bitmap dimensions | Fixed | `Display.cs:169` checks payload/descriptor agreement and nonzero dimensions; `:171` checks row stride. ImageDecoder repeats agreement checks; the two malicious-dimension tests pass. |
| C3 video allocation before clipping | Fixed | `Canvas.cs:666` checks destination limits, clips before ScaleWindow, and returns temporary storage in finally. The oversized destination regression passes. |
| C4 retained resource/glyph budgets | Fixed | Canvas has surface-byte, image-byte/count and work limits; ImageDecoder limits cached palettes to 1024. Glyph masks materialize only visible bounds. Budget and clip/line/glyph tests pass; heap exhaustion was not tested. |
| C5 unbounded outbound agent queue | Fixed | SessionAgent caps individual clipboard data, parks one newer payload and limits queue bytes. Boundary, parking and stop-state tests pass. The independent same-chunk stop race is N3 below. |
| C6 essential disconnect ordering | Partially fixed | `SpiceSession.cs:161` cancels sibling loop tokens before notification, fixing the tested live-token defect. An already executing display/playback handler can still emit after Disconnected; cancellation does not join or serialize handlers. `SessionPlayback.cs:41` invokes AudioStarted without a terminal-state gate. Full terminal event ordering remains unverified/unestablished. |
| C7 unsuccessful linking cleanup | Fixed | `SpiceSession.cs:74` collects every link result; essential failure disposes all successful results before throwing. Inputs-first failure regression passes. |
| C8 stopped-agent inbound dispatch | Partially fixed | Entry guard and reassembly reset fix subsequent calls, but a stop during a multi-message chunk does not stop its remaining dispatches. New failing test N3. |
| C9 overlapping clip union | Fixed | `Canvas.cs:300` normalizes overlapping rectangles, bounds normalization work and returns pooled arrays. XOR regression and 300 seeded clip-union comparisons pass. |
| C10 A4/A8 glyph coverage | Fixed | `Canvas.cs:578` preserves coverage, combines overlapping glyph samples by maximum and uses OVER for A4/A8. A8 regression and all 16 upstream A4 expansion values pass. |
| C11 unbounded offscreen stroke work | Fixed | Segment rejection, closed-form LineInRect and command-work limits replace walking distant invisible points. 1000 naive-Bresenham comparisons and extreme-coordinate bounded-work tests pass. |

## Original viewer findings

IDs refer to `codex-viewer.md`. These dispositions are primarily source checks;
no hook, GUI, driver-failure or monitor test was run.

| Finding | Status | Current evidence / limitation |
|---|---|---|
| V1 queued keys after focus loss | Partially fixed | `MainWindow.Input.cs:100` rejects unfocused events and CancelGuestInput clears the queue. There is still no capture generation: an in-flight hook producer can enqueue after Clear and an event can survive into a later focused capture. Clear is not a producer/consumer barrier. |
| V2 mouse-button/capture cancellation | Fix introduced a new problem | CancelGuestInput now releases keys/buttons, clears state, releases Win32 capture and unclips; WM_CAPTURECHANGED/WM_CANCELMODE are handled. The new cropped viewport is used unboundedly as ClipCursor's rectangle, allowing confinement outside the client: N7. |
| V3 UI blocking on network send | Fix introduced a new problem | Send no longer waits for Write/Flush and concurrent framing tests pass. Queue overflow can drop an essential key-up without ending the channel: N1. Write failure also remains nonterminal: N2. |
| V4 per-present stackalloc | Fixed | Presenter.Loop allocates the fixed dirty-rectangle and wait-handle storage before its loop. No repeated localloc in the long-lived frame loop. |
| V5 graphics shutdown lifetime | Fixed | The worker creates/releases all graphics objects; Shutdown only signals and performs a bounded sent-message-pumping wait. A timeout no longer frees worker-owned COM pointers. Actual driver-blocked shutdown was not exercised. |
| V6 graphics failure handling | Fix introduced a new problem | Worker catch/finally, bounded device-loss recovery and removal of self-waking permanent-failure retries address the original defects. The terminal failure UI keeps accepting/capturing guest input without a usable display: N6. |
| V7 clipboard unsafe memory reads | Fixed | MainWindow delegates to ClipboardFormats, which bounds GlobalSize, headers, masks, pixel offsets and text spans; incoming BMP is re-encoded. Private-block and malformed-format tests pass. |
| V8 hotkey auto-repeat | Fix introduced a new problem | First-down detection and hotkey-release suppression address the original repeat toggle. CancelGuestInput clears physical tracking but not the new hotkey latch, so a later unrelated press can be suppressed: N5. |
| V9 input sent while released | Fixed | Mouse-move and wheel paths gate _released; button path consumes the recapture gesture. Raw input requires pointer capture, which cancellation removes. OS focus/desktop transitions still need GUI testing. |
| V10 scaling/software/letterbox | Partially fixed | Viewport now drives the renderer and input, and WARP selects the 1:1 profile. Normal layouts pass tests. Fit still distorts on the padding-cap edge (N4); exact 1:1 beyond the cap was replaced by a different policy rather than implemented. |
| V11 cursor ID reuse/cache collision | Fixed | Cursors keys by shape reference identity and separate scale fields using an explicit comparer, so an ID reused by a new decoded shape is not a cache hit. Native handle behavior was not exercised. |
| V12 E1 Pause | Fixed | OnKey calls SendPause, which explicitly emits `E1 1D 45 E1 9D C5` on the raw path and implements the legacy make/break equivalent. This was source-checked, not tested with a guest keyboard. |
| V13 cursor resize/DPI scaling | Fixed | Current shape is retained; CurrentClientCursor compares the viewport scale and calls RebuildGuestCursor when needed. Actual WM_SETCURSOR/DPI delivery and visual hotspot behavior were not run. |

## Original task-7 findings

IDs refer to `codex-tests.md`.

| Finding | Status | Current evidence / limitation |
|---|---|---|
| T1 lower feature-level dimension limit | Fixed | Compute now caps both buffer axes. Original failing dimension test passes. Capping padding without preserving aspect introduces N4. |
| T2 exact 1:1 beyond texture limit | Not fixed — original contract changed | The old exact-width assertion was replaced by uniform-scale/tolerance assertions. Those pass for the selected example but do not prove 1:1. This is explicitly a decision to enlarge content. See decision review below. |
| T3 public agent stop state | Fixed | Overflow now calls OnAgentDisconnected, resets capabilities/state and raises the false notification. The original public-state regression passes. N3 is a separate in-progress-chunk race. |
| T4 decoded-byte arithmetic overflow | Fixed | ByteCount compares pixel count against maxBytes/4 before multiplying by four. Extreme-positive-dimension regression passes without allocating image storage. |

## New or newly demonstrated findings

### N1. high — Queue overflow silently loses guest key releases

**Location:** `src/Spice.Transport/SpiceChannel.cs:207` through `:212`.

When the pending buffer is full, Send drops every kind of frame indiscriminately.
The test blocks a writer after a key-down, fills the pending eight MiB, then sends
the corresponding key-up. When writes resume the down and queued frames are
delivered, but the up is absent and IsClosed remains false. CancelGuestInput also
uses this queue, so releasing local capture cannot guarantee guest releases get
through. A peer can withhold reads while continuing to send data: receive-side
EOF is not guaranteed to detect this half-direction stall. The decision's
assumption that the receive side will notice a dead connection is insufficient.

**Suggested fix:** do not silently drop stateful input/agent/control bytes.
Terminate the channel/session on overflow, or use an explicitly ordered bounded
protocol that preserves critical releases. Treat queue overflow as a failure
reported to the session while allowing local unhook/unclip to remain immediate.

**Failing test:** `QueuedTransportTests.QueueOverflowMustDeliverKeyReleaseOrTerminateChannel`.

### N2. med — Writer failure discards frames while leaving the channel live

**Location:** `src/Spice.Transport/SpiceChannel.cs:244` through `:247`.

WriteLoopAsync logs a failed write/flush, clears pending bytes and resets _writing
without closing/faulting the channel. The test injects IOException and waits for
the catch to complete: IsClosed is still false. Subsequent sends can start a new
writer after arbitrarily many protocol bytes have been lost, while the receive
side may still be open. This can lose releases or truncate an agent byte stream.
This is newly tested here; silent write-error handling also existed before the
queued-writer change, so its historical introduction is not attributed to this fix.

**Suggested fix:** atomically make the channel terminal, dispose/cancel its stream
and notify session failure. Do not continue a stateful byte stream after an
unrecoverable/possibly partial write or report a discarded queue as successful.

**Failing test:** `QueuedTransportTests.WriteFailureMustCloseChannelInsteadOfSilentlyDiscardingFrames`.

### N3. med — Stopping an agent does not stop dispatch within its current chunk

**Location:** `src/Spice.Session/SessionAgent.cs:175` through `:214`.

OnAgentData checks _agentRunning only once. The test concatenates a clipboard
grab and clipboard data into one input chunk, stops the agent on another task
when the first callback runs, and confirms AgentConnected is false. Parsing then
continues and delivers the second clipboard callback after the stop. With a
partly assembled next message, concurrent ResetAgentState can also race the
unlocked reassembly fields. The test proves late dispatch, not a native crash.

**Suggested fix:** serialize stop/reassembly transitions, associate incoming work
with an agent generation, and recheck the terminal/generation state before each
message dispatch. Raise application callbacks outside internal state locks.

**Failing test:** `AgentQueueTests.LocalAgentStopDuringAChunkMustPreventLaterClipboardDispatch`.

### N4. med — Fit's new texture clamp breaks uniform aspect scaling

**Location:** `src/SpiceViewer/Viewport.cs:34` through `:45`.

`Compute(3840,300,800,600,false,4096)` returns a 4096x600 buffer after independently
clamping width. DXGI then stretches it to 3840x300. GuestRectInClient reports a
750x300 image for an 800x600 guest: horizontal scale .9375, vertical scale .5,
ratio 1.875. The old cap regression only asserted valid dimensions; it did not
assert aspect preservation. This case can occur in a wide, short window with a
4096-side feature-level limit; it requires no oversized guest.

**Suggested fix:** solve both axes together under the texture bound and specify
how to preserve all guest content, or use a render/presentation path that can
scale the guest into a supported buffer. Do not independently clamp a padding
axis while assuming full-client stretching is still uniform.

**Failing test:** `ViewportTests.FitMustPreserveUniformScaleWhenPaddingHitsTextureLimit`.

### N5. med — Cancellation leaves a stale hotkey suppression latch

**Location:** `src/SpiceViewer/MainWindow.Input.cs:43` through `:54`, `:112` through `:113`.

The new _hotkeyKeys set is only removed on that key's release; CancelGuestInput
clears _physDown and modifiers but leaves _hotkeyKeys intact. Press Shift+F11,
lose focus before releasing F11, release it in another application, then return
and press plain F11. The old entry suppresses that new guest press and its up,
even though it is not a local hotkey. Similar stale entries can survive capture
cancellation. This is source-verified; no GUI/input injection test was run.

**Suggested fix:** invalidate hotkey latches by capture generation and reconcile
physical state on transitions. Preserve repeat suppression for a still-held
release hotkey without suppressing an unrelated fresh press after focus returns.

### N6. med — Terminal presenter failure leaves blind guest capture active

**Location:** `src/SpiceViewer/MainWindow.cs:323` through `:331`.

NotifyFailed clears the presenter and paints a failure status, but leaves
ViewState.Connected, keyboard hook and pointer capture active. The guest image
is no longer usable while keys/clicks continue to be forwarded; Alt+F4 may go to
the guest rather than close the failed viewer. Moving graphics errors to a
nonfatal worker boundary should not leave a user interacting blindly. This
failure-state transition is source-only; no driver failure was induced.

**Suggested fix:** make display failure a local input-release transition, stop
capture and move to an explicit failed/disconnected UI state before presenting
recovery/close instructions. Keep session/retry policy separate from input safety.

### N7. med — Cropped guest rectangles extend the cursor clip outside the window

**Location:** `src/SpiceViewer/MainWindow.Input.cs:229` through `:238`.

UpdateCursorClip uses GuestRect directly. That rectangle intentionally extends
past the client for cropped 1:1: an 800x600 guest in a 400x300 client returns
(0,0)-(800,600). Converting it to screen coordinates and passing it uncut to
ClipCursor confines the pointer partly over other windows, not to the visible
guest area. Server-reported cursor positions can also be warped into that area.
The math follows from the existing passing cropped-viewport test; OS pointer
behavior was not exercised.

**Suggested fix:** intersect the guest destination with ClientRect for confinement
and cursor warping. Retain the original full-guest transform for input coordinates;
the visible clip and the coordinate transform are different rectangles.

## Decisions versus CLAUDE_DRAFT.md

Recording a decision does not by itself waive an explicit requirement. I push
back on treating the changed 1:1 assertion as a completed fix: uniform enlargement
is a reasonable fallback proposal, but it is a change of product behavior and
should be explicitly accepted and described as such. Exact 1:1 is impossible
**within the chosen full-client stretched-buffer architecture past its limit**,
not impossible for all presentation architectures. No new requirement approval
was obtained or assumed during this verification.

| Decision | Assessment against the draft |
|---|---|
| Native toolchain (`decisions.md:5`) | Conflicts with original clang-cl wording, but task 1 explicitly authorized the human's MinGW/llvm-mingw override. Not an outstanding violation. |
| C codecs, TLS CA requirement (`:11`, `:17`) | Consistent. No managed decoder replacement or system-root fallback was introduced. |
| No LZ4 advertisement (`:23`) | No explicit requirement to advertise it; capability omission is not itself a draft violation. The stack's planned K4os dependency should not be read as implemented LZ4 decode coverage. |
| Eager clipboard (`:28`) | A behavior choice where the draft does not require delayed rendering. Payload limits remain important despite the rationale that clipboards are usually small. |
| Own-client recordings (`:34`) | Task 3 explicitly authorized this. Actual recorded server bytes remain the replay input. |
| “1:1 locks window size” (`:40`) | Stale relative to the later centered/cropped viewport decision and code. Consolidate the decisions; it is not the current implemented invariant. |
| No scroll rects (`:45`) | Contradicts draft Presenter step 4 (`CLAUDE_DRAFT.md:184`), which directs COPY_BITS through the DXGI scroll rectangle. A documented simplification is still a deviation needing acceptance/update. |
| Remaining per-message allocations (`:50`) | A general “measure before removing” waiver is weaker than the MUST NOT on hot-path allocations (`CLAUDE_DRAFT.md:289`). Distinguish infrequent control paths from actual hot events; cursor-change callbacks still post capturing closures in MainWindow.Session.cs. Scope/accept the exception rather than asserting full compliance. |
| Mouse-mode widths (`:56`) | Protocol-grounded clarification; no conflicting draft width requirement. |
| Real OS cursor in server mode (`:62`) | Departs from “confine and hide the pointer” literally (`CLAUDE_DRAFT.md:259`), while retaining the no-compositing goal. Treat as an explicit UX deviation; verify native cursor positioning/visibility and cropping. |
| Queued sends (`:68`) | Removing UI network waits supports the draft. Silently dropping stateful frames does not preserve no-stuck-input semantics; N1/N2 require changes to this decision/failure policy. |
| Agent queue bounds (`:74`) | Consistent mitigation in principle; public-state stop fix passes. N3 remains. |
| Windows AOT publish and Windows TFM (`:80`, `:86`) | No conflict with the draft's Native AOT/Windows targets. Native DLL toolchain and .NET executable linking are distinct. The broad cross-host-product claim in the first context should not be inferred as currently validated. |
| Hand-written DllImport (`:91`) | Contradicts the explicit LibraryImport-only rule (`CLAUDE_DRAFT.md:159`). Missing CsWin32 coverage justifies hand-writing a binding, not choosing DllImport; use LibraryImport partial declarations or explicitly obtain an exception. |
| Shaderless padded letterbox (`:97`) | A potentially useful implementation alternative, but differs from the specific shader path (`CLAUDE_DRAFT.md:194`) and guest-resolution-only ResizeBuffers instruction (`:183`). Requires a draft/decision reconciliation; PresentMon/independent flip is not established by unit tests. N4 exposes a limit case. |
| Presenter resource ownership/recovery (`:103`) | Supports the original safety/energy goals. Worker cleanup no longer races UI release; terminal failure input state still needs N6. |
| Oversized viewport fallback (`:109`) | Uniform enlargement contradicts exact 1:1/software-1:1 semantics (`CLAUDE_DRAFT.md:199`); guest cropping also changes Fit's show-the-image behavior. The relaxed test validates this new policy only. Explicitly accept the fallback, expose its behavior, or implement a path retaining the original requirement. N4 shows Fit can still become nonuniform. |

## Test results and verification limits

All 85 pre-existing test cases pass with the currently revised assertions. The
four new cases remain enabled and fail normally: transport 2, agent dispatch 1,
Fit aspect ratio 1. Total **85 passing, 4 failing, 0 skipped (89)** across:

| Project | Pass | Fail |
|---|---:|---:|
| Spice.Native.Tests | 11 | 0 |
| Spice.Wire.Tests | 7 | 2 |
| Spice.Replay.Tests | 27 | 1 |
| Spice.Viewer.Tests | 40 | 1 |

Commands were `dotnet test tests/<project> --no-restore` for each project.
The new tests exercise real queued-writer code with gated/faulting memory streams,
real agent reassembly/stop handlers with synthetic bytes, and current source-linked
viewport math. They do not launch the viewer, call a real clipboard, synthesize
physical input, inject device failures or establish real TLS/network behavior.
Source-only race findings and UI lifecycle scenarios need focused follow-up tests;
the supplied earlier live-test observations do not cover these new edge cases.

## Round 2

2026-10-29, task 9. Reviewed this round's presenter/shader, transport, agent,
input-generation and terminal-state changes. Production/native sources were
not edited. No viewer executable, server connection or credential access was
used, and nothing was committed. The supplied live-test observations remain
Claude's evidence, not independently reproduced results from this review.

### Status

| Item | Status | Evidence / remaining limit |
|---|---|---|
| N1 queue overflow | Fixed for the reviewed contract | `SpiceChannel.cs:211,256` marks the channel closed, clears pending bytes and asynchronously closes its stream. Inputs joins main/display as essential in `SpiceSession.cs:161`. The overflow/key-release tests pass. This establishes channel termination, not proof that a remote guest releases keys on disconnect. |
| N2 failed write | Fixed | `SpiceChannel.cs:248` invokes the same terminal fault path. The injected write-failure regression passes. |
| Transport assertion changes | Fair | The renamed test now asserts closure, empty queue and rejection of a later send. Returning early when IsClosed is true in the release test matches its original deliver-or-terminate contract; waiting for discarded queued bytes after closure would test the obsolete contract. |
| N3 stop during agent chunk | Fixed for the reported reproduction | Reassembly/reset share `_agentLock`; each completed message checks its generation before dispatch (`SessionAgent.cs:189,232`). The original callback-triggered stop regression passes. A handler already past that check is not serialized with stop; this is not a guarantee that stop waits for in-flight callbacks. |
| N4 Fit aspect ratio | Fixed for the reported layouts | Guest-sized direct output and client-sized letterbox replace padded stretching. All 57 existing viewport/clipboard cases pass, including the former cap/aspect failure. The oversized texture policy still explicitly permits cropping and approximate 1:1. A separate filtered dirty-region bug is N8 below. |
| N5 hotkey latch | Fixed for the reported focus-loss sequence | `MainWindow.Input.cs:52` clears `_hotkeyKeys` in the shared cancellation path. Source review only; no physical key test. |
| N6 blind input after terminal graphics failure | Fixed in source | `MainWindow.cs:337` leaves Connected, calls CancelGuestInput and updates hook capture. Key, button, move and wheel forwarding require Connected; raw input loses its pointer capture. No device failure was induced. |
| N7 clip/warp outside cropped client | Fixed in source | `VisibleGuestRect()` intersects the guest transform with ClientRect; both clip and cursor warp consume it (`MainWindow.Input.cs:232,244`, `MainWindow.Session.cs:139`). The full guest transform remains available for coordinate mapping. No OS confinement test. |
| V1 stale queued hook events | Partially fixed | Events carry a generation and Drain rejects older ones, but the producer reads the generation after its ownership check. An in-flight callback can acquire the new generation: N9 below. |
| C6 terminal event ordering | Partially fixed | The wrapper at `SpiceSession.cs:150` rejects handler entries which observe terminal state. It cannot prevent a previously admitted handler from raising events after Disconnected; e.g. playback can pass the wrapper, pause while another channel ends the session, then invoke AudioStarted at `SessionPlayback.cs:41`. Strict notification ordering still needs a shared dispatch/terminal barrier, or an explicit in-flight-callback exception in the API contract. Do not wait synchronously for a handler from within that handler. |
| COPY_BITS / flip history | No new defect found in source | Scroll destination and negative source delta match DXGI's destination/offset example. A prior pending write intersecting the scroll source blocks scrolling; a second copy falls back to dirty rectangles. CPU uploads include current and previous update sets, and bounds failures turn the scroll destination into an ordinary dirty rect. These checks do not certify compositor/driver behavior. |
| Letterbox shader / resources | Lifetime consistent in source | Immutable triangle, constant buffer, shaders, input layout, samplers, guest SRV/texture and back-buffer RTV are worker-owned. `ReleaseAll():875` releases/nulls the new objects and clears context bindings; recovery resets viewport/dirty state and forces full upload. Embedded VS (600 bytes) and PS (460 bytes) hashes independently match Shaders.cs. No shader execution or device-loss injection was performed. |
| Letterbox partial presents | New reproducible calculation defect | DrawLetterbox redraws the destination while Present1 reports transformed dirty regions. Those regions must include all output pixels actually changed by filtering; the fixed one-pixel halo does not at larger scales (N8). Full-image redraw alone cannot repair omitted present damage reliably. |
| Software pacing | Approximate 30 fps throttle in source | `Presenter.cs:320` waits on a stop-interruptible 33 ms interval and retains event-driven idle behavior. That interval alone is about 30.3 fps, not a mathematical <=30 ceiling; use a 34 ms minimum or fractional high-resolution deadline if the cap is strict. Actual cadence depends on Present/vsync and was not measured. |
| Per-frame allocations | No new steady-state presenter allocation found in source | Frame lists and native scratch arrays are outside Loop; bounded lists can grow during warm-up. Upload/DrawLetterbox use structs, fixed spans and a small method-local stack allocation. Shader bytes are constant spans; pipeline allocations occur on creation/recreation. This is source inspection, not an allocation trace. The separately documented control-event closure exception remains. |
| TLS / native imports / decisions | Changes confirmed | TlsTrust refuses a missing host-subject even with a CA; WinLong uses LibraryImport. The two decisions explicitly awaiting human acceptance remain hot-path/control allocations and the visible guest-shaped OS cursor; this review does not grant acceptance. |

The scroll interpretation was checked against Microsoft's
[DXGI_PRESENT_PARAMETERS example](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_present_parameters).
The two-buffer upload history is consistent with the need to maintain coherent
buffers described in Microsoft's
[flip-model presentation guidance](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-1-2-presentation-improvements).
Neither source check replaces a sequence of captured partial/scroll presents.

### N8. med - Filtered dirty rectangles omit changed pixels when magnified

**Location:** `src/SpiceViewer/Presenter.cs:523` through `:531`, used at `:472`;
linear sampling is selected at `:587`.

For an 800x600 guest fitted into a 4000x2400 client, the image occupies
(400,0)-(3600,2400), a 4x scale. Changing texel (100,100) causes the production
ToBuffer calculation to report (799,399)-(805,405). Pixel (798,398) samples
source index coordinates (99.125,99.125), so the changed texel contributes
1/64 of its value there. That pixel lies outside the reported dirty region.
This follows the shader's UV transform and the sampler's
[bilinear filtering rule](https://learn.microsoft.com/en-us/windows/uwp/graphics-concepts/bilinear-texture-filtering).

Present1 is allowed to update only the declared damage; the omitted fringe can
remain stale until another invalidation/full present. The shader drawing the
whole image does not make that damage declaration complete. The calculation is
reproduced against the actual compiled production method; visible artifacts
were not captured in this review.

**Suggested fix:** include the linear filter footprint in source coordinates
before mapping to buffer coordinates (at least half a source texel on each
side, with conservative rounding), or derive an equivalent scale-dependent
buffer halo, then clip to the visible destination. A full Present1 is also a
correctness fallback. Keep point-sampled 1:1 damage exact.

**Failing test:**
`PresenterDirtyTests.ScaledDirtyRectMustCoverEveryPixelAffectedByLinearFiltering`.
The test references the viewer project solely to reflect its pure calculation;
it never constructs Presenter, creates a window/device, or invokes its entry point.

### N9. med - In-flight hook callbacks can stamp stale keys with a new generation

**Location:** `src/SpiceViewer/KeyboardHook.cs:101` through `:110`, with Clear at
`:61` and Drain at `:69`.

The callback first confirms the viewer is foreground, then reads s_generation
only when enqueuing. Consider it paused after the foreground check: the UI loses
focus, clears/increments the generation and posts hook uninstallation. The old
callback resumes and stamps its old key with the new generation. If focus returns
before the queued WM_APP_KEY is drained, both the generation test and OnKey's
focus test pass. A stale down can consequently re-enter the new capture while
its physical release went to the other application. Generation tagging fixes
callbacks which already read the old generation, but not this interleaving.
This is a source-established interleaving, not a reproduced OS hook test.

**Suggested fix:** snapshot the capture generation before checking ownership or
processing the event, preserve that snapshot on enqueue, and invalidate it on
cancellation. Optionally recheck the snapshot before enqueueing to avoid useless
posts. Do not obtain a fresh generation at the end of an already admitted
callback. Validate with a paused-producer focus-loss/refocus regression.

### Round 2 test results and limits

All **105 existing cases pass**. One new enabled regression fails on the actual
dirty-region assertion: `(798,398)` is outside `(799,399)-(805,405)`.
Totals are **105 passing, 1 failing, 0 skipped (106)**:

| Project | Pass | Fail |
|---|---:|---:|
| Spice.Native.Tests | 11 | 0 |
| Spice.Wire.Tests | 9 | 0 |
| Spice.Replay.Tests | 28 | 0 |
| Spice.Viewer.Tests | 57 | 1 |

Ran `dotnet test tests/<project> --no-restore` for all four projects; the final
viewer run used the normal console logger to confirm the new failure was the
assertion above, not test setup. The viewer test project adds an aliased project
reference and a reflection-only test; production source is unchanged. No tests
were added for speculative driver behavior. Device recovery, real flip/scroll
composition, physical input/focus races, software frame cadence, and allocation
rates remain source-reviewed only. Claude's live smoke results do not establish
those edge-case properties.

## Round 2 fixes (Claude)

- N8: ToBuffer widens scaled damage by one source texel on each side (the bilinear footprint) plus a pixel for rounding; 1:1 stays exact. ScaledDirtyRectMustCoverEveryPixelAffectedByLinearFiltering passes.
- N9: HookProc snapshots the generation before the foreground check and enqueues that snapshot.
- Software pacing: 34 ms minimum interval (never above 30 presents/s).
- C6: accepted as an API contract instead of a barrier (waiting for in-flight handlers from inside a handler would deadlock): SpiceSession.Disconnected documents that a handler already running may still raise its event; the viewer ignores them, and its audio wiring now refuses AudioStarted after Disconnected and closes a stream that raced it.
- All 106 tests pass.

## Task 10

2026-10-29. Added `tests/Spice.Replay.Tests/DisplayStreamTests.cs`: 24 synthetic
display-stream cases. No production/native edits, commits, viewer launch,
network/server access or credential access were performed for this task.

Tests invoke the real OnDisplayMessage entry point with encoded display message
bytes, exercising its wire parsers, retained stream state, native JPEG decoding,
canvas blitting and invalidation callbacks. The JPEG is generated in-test through
the existing Spice.Native turbojpeg bindings (reflection binds the internal
CodecApi without changing its accessibility). Its asymmetric rows expose wrong
orientation and channel/position mapping; decoded reference pixels account for
JPEG's lossy encoding. Reports go through a real SpiceChannel queued writer into
an in-memory sink, where both framing and the eight report fields are checked.

| Coverage | Result |
|---|---|
| StreamCreate + StreamData | Correct destination, unchanged surrounding pixels, primary invalidation, top-down/bottom-up orientation and non-primary surface routing |
| StreamDataSized | Parsed size/destination/payload, scaled blit and destination retained for subsequent ordinary data |
| StreamClip | Initial and updated rectangle lists survive receive-buffer reuse; updates replace the old clip, an empty list suppresses drawing, and TypeNone restores drawing |
| StreamDestroy / StreamDestroyAll / repeated create | Removed stream entries and released retained frame buffers; later data and repeated/unknown destruction are harmless |
| Unknown stream / unsupported codecs | Unknown ordinary/sized data is ignored; VP8/H264/VP9/H265 do not initialize the MJPEG decoder or allocate frames and increment Drops |
| Corrupt JPEG | Invalid header and header-valid/missing-scan-data variants throw no exception, increment Drops once, leave canvas pixels unchanged and allow the next valid frame |
| MaxStreams | 64 accepted, new 65th ID rejected, existing ID replaceable at capacity and destroyed slot reusable |
| MaxStreamFrameBytes | Exact 256 MiB accounting boundary accepted; one pixel over rejected before target frame allocation; destroying retained accounting restores capacity |
| Header dimensions | Width or height 8193 rejected before frame allocation |
| StreamActivateReport / SendStreamReport | Activation resets prior counts; two 3-frame windows produce C_STREAM_REPORT with stream/unique IDs, correct start/end timestamps, frame/drop counts and zero delay fields; counters reset between windows |
| Wire parser bounds | Every truncated prefix of StreamCreate, ordinary/sized StreamData, StreamClip and StreamActivateReport is rejected with WireException; valid message fields and payload are checked |

**Results:** new cases **24 passed, 0 failed, 0 skipped**; complete
Spice.Replay.Tests project **52 passed, 0 failed, 0 skipped** (28 existing + 24
new). Ran the filtered tests and then
`dotnet test tests/Spice.Replay.Tests --no-restore --verbosity quiet`.
No production bug was exposed by these cases, so no deliberately failing test
or production fix was added.

**Limits:** this is in-process display-message integration, not a negotiated live
SPICE connection or viewer/GPU test. The memory-budget test seeds the retained
frame's dimension metadata while preserving a real small pooled buffer, like
the existing agent queue accounting tests; that frame is never decoded or
blitted. It checks the production aggregate-budget guard without renting
256 MiB, and does not establish peak process memory. Parser rejection and JPEG
corruption tests do not constitute fuzzing. Report timeout scheduling and live
server codec negotiation were not tested. Other test projects were not rerun
for this task.

## Task 11

2026-10-29. Added allocation probes in `tests/Spice.Wire.Tests/AllocationTests.cs`
and `tests/Spice.Replay.Tests/AllocationTests.cs`. Only tests/test configuration
and this report were edited. No production/native edits, commits, viewer launch,
network/server access or credential access. Measurements below are Windows x64,
.NET 10.0.11, Debug JIT builds, managed allocation bytes (not retained bytes or
native codec heap usage).

### Measurement method and bounds

The Send probe warms 2,000 sends and measures 10,000 16-byte bodies using both
GC.GetAllocatedBytesForCurrentThread and GC.GetTotalAllocatedBytes(true). A
nonallocating counting stream consumes every frame; each send waits until its
bytes are drained **and the writer parks** before the next send. Thus it tests
10,000 idle-to-busy wakeups rather than one large batch. No timing/sleep objects,
payload arrays, delegates or assertions are created inside the measured loop.
The frame/write counts and open channel state are checked.

The process-wide probe runs in a child **test assembly**, not the viewer. A
StartupObject entry in the wire test project selects its opt-in
`--allocation-probe` entry point; ordinary `dotnet test` still uses VSTest.
This matters: a preliminary in-testhost run counted 361,784 process-wide bytes
despite zero sender bytes. Disabling test parallelism alone does not exclude
runner/background work. Child startup, reflection, warm-up, reporting and
disposal are outside the measured interval. The sender has an exact zero-byte
assertion; the isolated process bound is **16,384 B per 10,000 sends** (1.64
B/send), allowing small runtime/worker bookkeeping while rejecting one work
item/Task allocation on every wake. Repeated isolated samples were:

| Sample | Sender bytes | Isolated process bytes | Warm + measured writes |
|---|---:|---:|---:|
| First isolated full-suite run | 0 | 2,136 | 12,000 |
| Final full wire-suite run | 0 | 0 | 12,000 |
| Final filtered repeat | 0 | 1,072 | 12,000 |

ReplayOpener/ReplayStream and SpiceChannel preload the recorded display messages,
including their actual framing/handshake, without a connector/socket. Bodies,
message/image labels, output storage and hashes are prepared outside measurement.
Canvas.Apply runs synchronously: first a warm-up replay, then Reset clears
GLZ/cache/palette state before the second replay on the same canvas, preserving
pool and dictionary/list capacity. The second pass measures each Apply call
on its calling thread and the whole pass process-wide. Per-message counters
sum exactly to the whole calling-thread counter; both passes have zero canvas
errors and identical final framebuffer hashes. PNG encoding is excluded.

Tests use an exclusive xUnit collection. Per-message/thread measurements are the
deterministic assertions; replay process-wide figures are reported separately
because unrelated runtime work can still occur. The second-pass bound is
**surface pixel payload + 96 B/display message + 4,096 B fixed slack**. The
measured steady decoded draw cost is 88 B, leaving 8 B/message and fixed slack
for the surface object/array header and small first-use/runtime variance. Some
runs include an additional fixed 832 B on the first LZ draw; this is included
in reported counters, not subtracted. The bound would reject an additional
16 B on every rich-fixture message. Any non-surface message allocating >=85,000
B also fails individually, so averaging cannot hide a new large-buffer rent.

### Second-pass results

These are the final filtered run's raw figures; bytes/message include the
surface-creation message, which necessarily creates new pinned pixels.

| Fixture | Display messages | Calling-thread B | Process-wide B | Thread B/message | Asserted upper bound B |
|---|---:|---:|---:|---:|---:|
| win7-desktop | 5 | 1,920,160 | 1,940,344 | 384,032.00 | 1,924,576 |
| win81-desktop | 5 | 3,146,720 | 3,146,720 | 629,344.00 | 3,150,304 |
| win7-rich | 1,929 | 2,089,472 | 2,089,472 | 1,083.19 | 2,109,280 |
| win81-rich | 867 | 3,221,744 | 3,221,744 | 3,715.97 | 3,233,056 |

Per-message/image breakdown (allocation of the **entire Apply call**, not an
isolated codec invocation; these fixtures have one source image per draw):

| Fixture | Message / image | Count | Allocated B | B/message |
|---|---|---:|---:|---:|
| win7-desktop | SurfaceCreate / none | 1 | 1,920,072 | 1,920,072 |
| win7-desktop | DrawCopy / LZ_RGB | 1 | 88 | 88 |
| win81-desktop | SurfaceCreate / none | 1 | 3,145,800 | 3,145,800 |
| win81-desktop | DrawCopy / LZ_RGB | 1 | 920 | 920 |
| win7-rich | SurfaceCreate / none | 1 | 1,920,072 | 1,920,072 |
| win7-rich | DrawCopy / LZ_RGB | 1 | 88 | 88 |
| win7-rich | DrawCopy / GLZ_RGB | 1,895 | 166,760 | 88 |
| win7-rich | DrawCopy / QUIC | 29 | 2,552 | 88 |
| win81-rich | SurfaceCreate / none | 1 | 3,145,800 | 3,145,800 |
| win81-rich | DrawCopy / LZ_RGB | 1 | 88 | 88 |
| win81-rich | DrawCopy / GLZ_RGB | 861 | 75,768 | 88 |
| win81-rich | DrawCopy / QUIC | 1 | 88 | 88 |
| Each of all four fixtures | InvalAllPalettes / none | 1 | 0 | 0 |
| Each of all four fixtures | Mark / none | 1 | 0 | 0 |
| Each of all four fixtures | MonitorsConfig / none | 1 | 0 | 0 |
| Each of all four fixtures | DrawCopy / FromCache | 0 | 0 | N/A |
| Separate synthetic cached draw | DrawCopy / FromCache | 10,000 | 560,000 | 56 |

The 88 B draw cost is consistent with a new DecodedImage wrapper plus the
formatted statistics string (`ImageDecoder.cs:47,56,94`, `Canvas.cs:179`);
pooled pixel buffers do not make the wrapper or string allocation-free.
The FromCache-only probe below isolates the avoidable formatting cost without
decoding or allocating an image wrapper. Desktop fixtures have no QUIC/GLZ;
none of these fixtures contain FromCache, so that path is explicitly synthetic.

### Large arrays (>=85,000 B) and the live 2-4 MiB observations

Every second-pass allocating message above the threshold was **SurfaceCreate**
(message index 1, zero-based): 1,920,072 B for win7 and 3,145,800 B for win81.
The individual byte[] pixel payloads are 1,920,000 B (800x600x4) and 3,145,728 B
(1024x768x4); the remaining 72 B is the array header plus Surface object.
`Surface.cs:24` uses GC.AllocateArray with pinned:true, so these are large
**pinned-object-heap** allocations, not ordinary unpinned LOH arrays. They recur
on each surface creation; warming a decode pool cannot remove that behavior.

A warm-up-only ArrayPool EventListener records actual BufferAllocated events
for ArrayPool<byte>.Shared, filtered by pool ID and replay thread. It is disabled
before the measured second pass because event callbacks themselves allocate.
This distinguishes actual new byte[] buffers from large aggregate message
totals. Observed pool buffer payloads were:

| Fixture | Warm-up message index / path | New byte[] payload B |
|---|---|---:|
| win7-desktop | 2 / DrawCopy LZ_RGB | 2,097,152 |
| win81-desktop | 2 / DrawCopy LZ_RGB | 4,194,304 |
| win7-rich | 2 / DrawCopy LZ_RGB | 2,097,152 |
| win7-rich | 22 / DrawCopy GLZ_RGB | 131,072 |
| win81-rich | 2 / DrawCopy LZ_RGB | 4,194,304 |
| win81-rich | 5 / DrawCopy GLZ_RGB | 131,072 |

These unpinned pooled byte arrays are LOH-sized; payload figures exclude object
headers. They come from DecodedImage's ArrayPool rent (`Surface.cs:69`) while
decoding the initial full image or a larger subsequent GLZ update. On the second
pass no image draw allocated >=85,000 B: the pixel pools were warm. The 131,072 B
buffer is also a plausible size family for the reported ~120 KB sample.

These fixtures demonstrate candidate sources for the live large arrays, not
proof of the live ETW stack. Real surface recreation/resize, pool growth/trimming
and channel read-buffer growth can produce large arrays. Receive-buffer rents
(`SpiceChannel.cs:142`) are outside the measured Canvas.Apply interval, as are
recording/preload copies, session cursor decoding, audio, UI and GPU work. No
claim is made that the live trace's unknown allocations are conclusively solved.

### N10. med - Every cached image draw allocates a statistics key

**Location:** `src/Spice.Canvas/Canvas.cs:179`.

Resolve unconditionally formats `image_{image.Type}` before its cache lookup.
Even with logging disabled, a hot FromCache draw therefore allocates a new
`image_FromCache` string. A 1x1 image is seeded into the real cache, 256 draws
warm the path, then 10,000 further draws allocate **560,000 B (56 B/draw)**.
The pixels and canvas error count remain correct. This is persistent
per-message work, not pixel-pool growth or cold codec setup.

**Suggested fix:** use static/precomputed statistics keys (or numeric counters
formatted on report), retaining the existing count semantics. Retest the
decoded-image wrapper separately if the zero-hot-allocation requirement applies
to that metadata object too; this task does not rewrite production ownership.

**Enabled failing test:**
`AllocationTests.CachedImageDrawMustNotAllocateStatisticsStringsPerMessage` in
Spice.Replay.Tests expects zero warmed allocation and currently fails with
560,000 B. No production fix was made.

### Task 11 validation

New allocation cases: **5 passed, 1 intentionally failing, 0 skipped**.
Complete changed projects: **66 passed, 1 intentionally failing, 0 skipped**:

| Project | Pass | Fail |
|---|---:|---:|
| Spice.Wire.Tests | 10 | 0 |
| Spice.Replay.Tests | 56 | 1 (N10) |

Ran both complete projects with `dotnet test tests/<project> --no-restore
--logger "console;verbosity=detailed"`; repeated the filtered allocation cases
to check the bounds and retain detailed measurements. The final filtered wire
repeat used --no-build. This covers recorded display/canvas work and a synthetic
draining writer; it is not live animation/audio/mouse profiling, a network
benchmark or Native AOT profiling. GC allocation counters do not measure native
decoder buffers, and a large per-message counter alone is not a complete heap
allocation trace. Native/viewer test projects were not rerun for this task.


## Task 11 fixes (Claude)

- N10 fixed: `Canvas.Resolve` and the ignored-message counter use precomputed statistics keys; the cached-image test
  now measures 0 B for 10,000 warmed FROM_CACHE draws.
- The 88 B per decoded draw is gone too: `DecodedImage.Rent` reuses released wrappers from a per-thread free list
  (`ImageDecoder` and `Raster.ScaleWindow` use it). The replay bound was tightened from surface payload +
  96 B/message + 4 KiB to surface payload + 4 KiB; all four fixtures pass.
- Test totals after the fixes: Native 11, Wire 10, Replay 57, Viewer 58 (136), all passing.
