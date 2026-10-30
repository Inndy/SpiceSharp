# Display-off and monitor-area review

Commit hashes in this record name work-branch commits kept under the tag `archive/display-off-wip`; they were squashed
into one commit on `main`.

Codex, 2026-10-30. Normal review of `9d3372a` against parent `44161f1`. Read AGENTS.md, Ubuntu findings L1/L2,
accepted decision 0035 and the engineering rules. This review uses source inspection and window-free tests;
it does not repeat the live guest check.

**Verdict: changes requested. Two P2 findings: M1 loses the selected monitor's identity when sending absolute
input; M2 leaves right/bottom edge pixels unreachable when downscaled. M2 is inherited arithmetic extracted
unchanged from the parent, included because coordinate edges were explicitly requested, not attributed to
the new crop implementation. Four added cases remain failing.**

## M1 - P2: Send absolute input to the monitor that is actually shown

Locations: new head selection/storage in `src/Spice.Canvas/Canvas.cs:148-153`; area-relative mapping in
`src/SpiceViewer/GuestView.cs:40-44` and `MainWindow.Input.cs:199-202`; actual send in
`src/Spice.Session/SessionInputs.cs:128`.

The new display policy selects the first nonempty head on surface 0, including a head whose monitor ID is
not zero. Its rectangle reaches the presenter/window, but its identity does not reach input: every
CMousePosition still contains display ID **0**. The position is now relative to the selected monitor, so
the packet can address a different monitor than the one the user sees. A click then uses that wrong pointer
location. This requires only one enabled head with a nonzero ID, not simultaneous presentation of multiple
monitors.

This is a valid server configuration: spice-server removes zero-size heads while preserving each remaining
head's original monitor ID. An enabled head 1 can therefore be the sole head in the message after head 0 is
disabled. [spice-server marshall_monitors_config](https://sources.debian.org/src/spice/0.15.2-1/server/dcc-send.cpp)

spice-gtk pairs monitor-relative x/y with the selected monitor's ID through `get_display_id`, rather than
hard-coding zero. Its inputs channel places that ID into the outgoing position message.
[widget source](https://raw.githubusercontent.com/daynix/spice-gtk/master/src/spice-widget.c),
[inputs-channel source](https://raw.githubusercontent.com/daynix/spice-gtk/master/src/channel-inputs.c)

Reproduced through the real canvas parser, the linked GuestView helper and the real session/channel serializer:
a sole 640x480 head **1** on a 640x512 surface is shown correctly; mapping the last pixel of a 1280x960 client
produces (639,479), but the serialized 11-byte mouse payload ends in **display ID 0**, not 1. Head 0 is the
passing control. The channel writes only to a MemoryStream; no host input API is involved.

Carry the chosen monitor ID together with its area into absolute input, including coalesced pending motion.
Adding the crop offset to x/y is not a substitute for addressing the right monitor. The old hard-coded ID
predates the commit, but the newly supported first-head selection exposes this mismatch.

Failing test: `DisplayReviewTests.AbsoluteMousePositionAddressesTheMonitorThatIsActuallyShown(monitorId: 1)`.

## M2 - P2: Preserve reachability of the downscaled monitor's last pixel

Location: `src/SpiceViewer/GuestView.cs:43-44`, used by `MainWindow.Input.cs:202`.

`floor(clientX * guestWidth / clientWidth)` maps the largest in-image client coordinate, `clientWidth-1`,
short of `guestWidth-1` whenever the guest is downscaled sufficiently. Clamping does not fix that: the result
is already inside the allowed range. On the direct Fit path there is no in-client letterbox bar beyond the
image to move into, so the guest's extreme edge cannot be reached normally. This affects edge-triggered UI,
including hidden panels/taskbars and hot corners.

| Guest area | Client image | Last client pixel maps to | Required reachable corner |
|---|---|---|---|
| 640x480 | 320x240 | (638,478) | (639,479) |
| 1920x1080 | 960x540 | (1918,1078) | (1919,1079) |
| 3840x2160 | 640x360 | (3834,2154) | (3839,2159) |

All three cases fail against the linked production helper. They use ordinary window/image sizes, without
overflow coordinates or synthetic input. Existing mapping tests cover upscaling and 1:1, which mask this error.
Use an endpoint-preserving input transform, with explicit handling of one-pixel/empty dimensions and existing
out-of-image clamping. spice-gtk's `transform_input` explicitly handles this endpoint problem.
[widget source](https://raw.githubusercontent.com/daynix/spice-gtk/master/src/spice-widget.c)

**Provenance:** this formula is identical to the parent's inline ToGuest implementation. It is a real existing
input defect found during the requested edge review, not a regression caused by cropping or the extraction.

Failing tests: the three `DisplayReviewTests.DownscaledMonitorEdgesRemainReachable` cases.

## Wire and geometry checks

- The parser reads the four-byte count/max_allowed header and seven-u32 head records in place. It validates
  the full declared payload length before returning an early matching head, rejects counts above 64, and
  preserves the current area on malformed messages. All 24 Wire cases pass, including the literal recorded
  Ubuntu message and count/truncation checks. Server field population and the recorded message support the
  layout; direct retrieval of upstream spice.proto was unavailable through the browsing tool, so this was not
  an independent audit of the protocol generator's source text.
- The single-head origin rule matches the inspected spice-gtk non-EGL path. Multiple heads retain their
  selected head's surface offset. Cursor drawing subtracts that offset, and snapshots read at that offset
  with the original surface stride. The explicit fallback to the full surface for an entirely outside head
  differs from spice-gtk but is recorded in 0035.
  [widget source](https://raw.githubusercontent.com/daynix/spice-gtk/master/src/spice-widget.c)
- AreaOf uses wide arithmetic for head end coordinates and clamps before converting back to surface-sized
  ints. The changed presenter translates dirty, previous-frame and scroll rectangles to area coordinates,
  clips uploads to the source, reads pixels from the area's top-left, and forces a full upload on origin/size
  changes. Scrolls whose source or destination crosses the crop fall back to dirty updates. No new invalid
  memory access or scroll-coordinate defect was confirmed by source inspection; D3D execution was excluded.
- PNG offset/crop tests pass. Recorded surface dimensions/statistics retain the full surface; snapshots use
  the monitor area. Existing Windows goldens and the available Ubuntu golden comparison pass in the final run.

## Display-state ordering and input safety

The added event control verifies that DisplayStateChanged runs under Canvas.Sync, matches the current
published state and primary-surface presence, and reports Off/On/Off/On through MARK/create/destroy/recreate.
A monitor-area change does not fabricate an Off transition. State publication is volatile; window handlers
read current session state instead of applying a captured stale Off value after a mode change.

Window source review shows that DisplayOff releases pointer capture and held buttons, detaches the presenter
reference, shuts its worker down, and uninstalls the hook. Keys remain on the WM-key path. A later primary
notification can create a new presenter; handlers ignore disconnected windows. Presenter failure continues
to block input. The shutdown path does not hold Canvas.Sync while joining, avoiding that obvious lock inversion.
This is source evidence, not a native-window race test.

The unlinked-input control sends Shift to an unlinked in-memory session and confirms that no held guest key
is recorded; KeyEvent returns before touching its key set if `_inputs` is absent. GuestView blocks pointer
input in DisplayOff and blocks Connecting keys until linked. There is no host input injection in any test.

No timer/polling was added for display-state detection. State/area logging is transition work. Repeated
unchanged monitor parsing/canvas application measures **0 managed allocation bytes** in the repository's
guarded, warmed allocation window. GuestView uses value arithmetic/tuples; no new hot-path allocation was
found in the changed mapping or area-translation code. Live idle wakeups and GPU allocation were not measured.

## Minor qualifications and validation limits

- GuestView permits Connected/DisplayOff keys even when its `linked` argument is false; only Connecting uses
  that flag. The session's actual null-inputs check prevents sends before attachment, and ConnectAsync links
  all requested channels before attaching their loops. The pure helper's comment is broader than its truth
  table, but no unsafe pre-link send was reproduced, so this is not a separate finding.
- Initial window sizing can observe the provisional full-surface area before MONITORS_CONFIG arrives. With
  GuestFollowsWindow enabled, later area changes intentionally do not force a window resize. The exact startup
  sizing/resize-request interaction should be included in the live follow-up; the in-memory tests establish
  the final crop, not native window geometry or absence of a transient letterbox.
- The first full run had one unmodified raw-allocation test failure: 5,688 B in
  `MonitorAreaTests.ApplyingAnUnchangedMonitorsConfigAllocatesNothing`. Its isolated repeat and the final full
  run pass, as does the added AllocationWindow-based control. The first sample's cause was not captured; it
  is not presented as a confirmed production allocation defect. No forced GC, load or relaxed limit was used.

## Validation and scope

| Window-free check in this worktree | Result |
|---|---|
| First existing Replay run | **291 passed, 1 failed**; raw-allocation sample described above |
| Isolated repeat of that existing allocation test | **1 passed** |
| Added `DisplayReviewTests` | **4 passed, 4 failed**; failures are M1 and the three M2 cases |
| Final full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` | **296 passed, 4 failed, 0 skipped**, 300 total; only the new regressions fail |
| `tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | **24 passed** |
| `tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | **24 passed** |
| `git diff --check` | Clean |

Added this review, its index row and `DisplayReviewTests.cs`; all remain uncommitted and the four bug cases
remain failing. No production code, decisions or existing assertions were edited by this review. Concurrent
changes to DesktopReplayTests, the fixture README and Ubuntu golden appeared during the task and were left
untouched; the final run includes their golden comparison. This review did not approve or replace a golden.

No Viewer test project or window was run or created. No VM, desktop interaction, host input injection, host
audio-device access, artificial CPU stress, commit or `/daybreak` was used. Session messages and output packets
are in memory; native presenter, input hook, pointer confinement and live wake behavior remain unvalidated.

## Re-review

Codex, 2026-10-30. Reviewed `fbbfe2c` against its parent, including the 0035 update and both ancillary changes.
The worktree was clean at the start. **Verdict: approve. M1 and M2 are fixed; no new P1/P2 finding was confirmed.**
The first-sizing change has a minor qualification below. The documented monitor-0-only agent resize remains a
limitation, rather than being claimed fixed by this review.

### M1: complete and correct

`Canvas.PrimaryMonitorId` is published with volatile reads/writes, including when the ID changes without changing
the area (`Canvas.cs:210-215`). Creating or clearing the primary resets it to zero. The uint/int storage round trip
preserves the ID; `SessionInputs.cs:106-116` caps it to the wire's u8 range rather than truncating it. MousePosition
saves the ID alongside the pending coordinates under the existing input lock; the send path uses that saved ID.
It introduces no Canvas/input lock inversion.

The original in-memory serializer cases for heads 0 and 1 now pass without changing their assertions. Added
`CoalescedPositionKeepsItsCoordinatesAndMonitorTogetherAcrossAnIdOnlyChange` fills the eight-motion window, saves
a pending position, changes the monitor ID without changing the rectangle, then delivers the real ACK handler.
Both variants pass: an unreplaced pending position retains head 1; a newer coalesced position replaces both its
coordinates and ID with head 2; a subsequent fresh send uses current head 3. The channel output is inspected as
real mini-header packets, without network or host input. The added cap controls verify IDs 255, 256 and uint.MaxValue
serialize as 255 and that destruction clears the published ID. These out-of-range controls test the stated cap,
not a claim that a real server normally supplies that many heads.

### M2: complete and correct under the revised policy

`GuestView.Axis` uses `(guest-1)/(size-1)` only for a downscaled axis, handles a one-pixel image without division by
zero, and clamps outside-image input. The original three failing right/bottom edge cases now pass unchanged.
Each axis chooses independently, so mixed shrink/enlarge dimensions are handled correctly.

The difference from spice-gtk for enlarged images is justified: retaining `floor(client*guest/size)` preserves the
guest pixel under each enlarged block, while mapping endpoints in that regime would move some block pixels into
the preceding guest pixel. The added controls pin this distinction: client offsets 1 and 3 at 2x scale map to
guest pixels 0 and 1, respectively. Controls also cover 1:1, mixed-axis scaling, a nonzero image origin, letterbox
clamping and the specified first-pixel choice for a one-pixel image. This policy change does not weaken the original
edge assertions. The original review test methods and their assertions were retained; only the test sink's readiness
threshold was parameterized to support multiple queued packets, and new tests were appended.

### Ancillary changes and remaining minor qualifications

- **Initial sizing:** source inspection confirms the reported surface-then-smaller-monitor case now follows the
  first-sizing branch even with GuestFollowsWindow enabled. It retains the remembered-placement rule and does not
  center the window twice. If both notifications are already queued, handlers read the latest canvas state under
  its lock. Native geometry was not exercised. A small remaining edge case: an initial config equal to the whole
  surface emits no area-change notification, leaving `_firstSizingProvisional` set. A later changed-size primary
  notification can therefore use the first-sizing rule once, even after initial configuration completed. With
  guest-follows-window this can cause a one-off resize; this is a low-severity source qualification, not a new P2.
- **Allocation test:** the implementer's test now warms the same loop it measures and still requires exactly zero
  bytes; it does not relax the limit or force collection. It and the unchanged guarded AllocationWindow control pass.
  The original 5,688-byte sample's cause was not recorded, so the new warm-up is not proof of the claimed JIT cause.
- **Energy and scope:** the added production operations are field publication, saved scalar input state and value
  arithmetic. No timer, idle polling or new hot-path allocation was found. Agent resize targeting monitor 0 remains
  explicitly documented in 0035; nonzero-head resize is not covered by the input-ID fix.

### Re-review validation

| Window-free check | Result |
|---|---|
| Existing Replay suite before added controls | **306 passed, 0 failed** |
| Full Replay suite including 12 added cases | **318 passed, 0 failed, 0 skipped** |
| Wire suite | **24 passed** |
| Native suite | **24 passed** |
| `git diff --check` | Clean |

Only this appended review and `DisplayReviewTests.cs` were edited; they remain uncommitted. No production code,
decisions, existing golden images or review index row needed changing. No confirmed new bug requires a failing test.
No Viewer test project, window, VM, host input/audio device, artificial CPU stress, commit or `/daybreak` was used.
Native window sizing, cursor/presenter execution and live guest behavior remain outside this validation.

## Second round (L5-L7)

Codex, 2026-10-30. Normal review of `57b43b1` against parent `8a360c0`, in the requested worktree and branch.
Read AGENTS.md, decision 0035, Ubuntu verification findings L5-L9 and the earlier review first; also checked the
engineering principles, spec energy rules and presenter-failure decision 0018. The worktree was clean at the start.

**Verdict: changes requested. One new P2 finding, M3. Two regression tests and one passing-control assertion have
been added, but remain uncompiled and unexecuted because offline test builds stop at restore-asset errors.**

### M3 - P2: A presenter failure while the guest is blank must stop key forwarding

Locations: `src/SpiceViewer/MainWindow.Session.cs:111-128` now keeps the presenter in `DisplayOff`;
`src/SpiceViewer/MainWindow.cs:319-332` still changes only `Connected` to `DisplayFailed` on `NotifyFailed`;
`src/SpiceViewer/GuestView.cs:24-28` and `MainWindow.Input.cs:114-136` continue accepting keys in `DisplayOff`.

After a picture has been shown, let the guest blank, then let the retained presenter suffer a terminal graphics
failure while uploading/presenting the message or processing a resize/restore. Its worker reports `NotifyFailed`.
The window detaches the presenter and releases existing guest input, but the `Connected`-only condition leaves
the window in **DisplayOff**. A subsequent ordinary WM-key down/up is still forwarded. The failure status asks the
user to close and reopen the console, yet typing can wake and operate that guest after its presenter has failed.
This violates the explicit input rule in 0035 and the failure policy in 0018. Releasing held input once does not
block the next press.

The failure handler predates this commit; the newly retained presenter makes this a reachable state during
DisplayOff. Include that active state in the transition to DisplayFailed, while preserving Disconnected, and keep
the next primary-surface notification as the recovery path. No production fix was made.

Added intended-failing tests in `DisplayReviewTests.cs`:

- `PresenterFailureWhileDisplayOffMustEnterDisplayFailed`: the real notification handler must leave DisplayOff.
- `PresenterFailureWhileDisplayOffMustStopForwardingKeys`: a fresh A down/up must not be consumed/sent afterwards.

`PresenterFailureWhileConnectedStillEntersDisplayFailed` is the control for the existing working branch. The harness
loads the actual viewer assembly, constructs only managed window state, and bypasses the presenter's constructor.
It supplies an already-failed worker state and synchronization handles, then calls the production handler and key
path. It never calls Create, StartSession, Run, OnDestroy, a hook installer or a D3D constructor. The HWND_MESSAGE
pseudo-handle keeps user32 queries/invalidation away from real windows; no host input is injected. These tests
require an offline viewer build in the same configuration before the Replay test run; there is no new project
reference or package. They are Windows-specific and skip on other operating systems. Their behavior has not been
executed or confirmed in this review.

### State ordering and the retained presenter

- **Pending/Waiting/Off/On:** `_linked`, `_displayKnown` and state recomputation are serialized by Canvas.Sync.
  A primary surface takes precedence over both flags; a MARK without one makes Off sticky across link completion;
  link completion without either makes Waiting. A later create/destroy yields On/Off. Completing links cannot
  overwrite an already-observed On or Off with Waiting. The window reads the current state when a posted handler
  runs and ignores display-state notifications after disconnect or display failure. No new timer implements Waiting.
- **Image ownership and guest precedence:** the placeholder is published atomically and its pixel array is not
  modified after publication. RenderOnce selects the guest surface first while holding Canvas.Sync through uploads;
  an old message cannot replace an existing guest surface in that selection. SetPlaceholder forces a full update,
  clears pending scroll state, and wakes the existing worker. A newly created primary invalidates its full surface,
  and the UI clears the placeholder on OnPrimaryChanged. CPU arrays are pinned for the upload. No new concurrent
  COM release or canvas-lock/shutdown join was found in these changes.
- **Resize, minimize/restore and full screen:** RenderMessage redraws at resize completion and ordinary WM_SIZE;
  it defers while iconic. SetSuspended keeps a retained presenter from presenting while minimized and requests a
  full redraw on restore. Full-screen geometry changes use the WM_SIZE path. A message after a shown frame is sized
  to the client, so ordinary layouts use the direct upload path. The separate layout/image publications can cause
  an intermediate old-size frame before the full message update; this is source ordering, not a measured artifact.
- **Disconnect:** when no primary remains, the new message replaces the display-off text with the disconnect reason;
  when a primary exists, the overlay is drawn into it. The window blocks further input and ignores queued primary
  callbacks after Disconnected. Native Close-button composition, cross-channel disconnect timing, actual DWM output,
  device recovery and full-screen transitions were not exercised. These observations do not establish a live L5 pass.

### Same-size heads and server behavior

KeepHead pairs the last shown head, including its ID and normalized origin, with the surface's dimensions before
clearing Primary. Recreate consumes it only when both dimensions match. An intervening differently-sized primary
drops it, so returning later to the original size cannot resurrect the older crop. Repeated same-size blank/wake
cycles retain it again, and a later valid config on the new surface takes precedence. A non-primary surface does
not consume the saved head. The changed tests cover the observed Ubuntu wake and Windows grow/shrink orders.

The inspected spice-server 0.15.2 source supports the need for an independent monitor policy: `send_create_surface`
queues the surface and optional image, without unconditionally queuing a monitors config; monitor updates have a
separate push path. [display-channel.cpp](https://sources.debian.org/src/spice/0.15.2-1/server/display-channel.cpp)
Its `dcc_start` queues the normal initial surface, image, monitors config and MARK only when surface 0 exists,
supporting Waiting for a blank connection. [dcc.cpp](https://sources.debian.org/src/spice/0.15.2-1/server/dcc.cpp)

The same-size rule is a documented client policy supported by the supplied live observations, not a protocol
guarantee that equal allocations always mean equal monitor modes. No broader guarantee was inferred. The lack of
mode information at blank connect, full-surface head after a wake, ignored config without a primary, and
Windows shrink config being paired with the old size remain the recorded policy/limits, not additional findings.
L8's guest resize refusal and L9's missing playback channel are not attributed to this commit.

### Energy and validation limits

No new idle timer, polling loop or self-waking presenter retry was found. The worker still sleeps on its event;
the message causes a present when published and after a relevant redraw, rather than periodically. The new BGRA
array, placeholder record, DIB and fonts are created for message transitions/resizes, outside the ordinary guest
draw/present hot path. RenderOnce selects existing arrays and scalar metadata; no new per-frame managed allocation
was identified by source inspection. Idle presents, managed allocation counts, GPU work and live message visibility
were not measured.

| Check | Result |
|---|---|
| `dotnet build src/SpiceViewer/SpiceViewer.csproj --no-restore -v minimal` before the failed restore | Passed, 0 warnings, 0 errors; build only |
| Focused Replay test command with implicit restore | Blocked by NU1301/socket access; no tests ran |
| Requested network retry | Denied by the human; no further network restore requested |
| Focused Replay test command with `--no-restore`, before and after dropping the temporary viewer reference | Stops at NU1301 recorded in restore assets; no compilation/test results |
| Added regression tests | Source added, uncompiled/unexecuted; expected failures described above, not claimed reproduced |
| `git diff --check` | Clean |

The temporary ProjectReference was removed. Only this appended section and `DisplayReviewTests.cs` remain changed.
The failed implicit restore touched generated build assets; no attempt was made to repair them by accessing NuGet.
No production source, existing assertions, decisions, golden images or review index was changed. No Viewer test
project, visible window, VM, host input/audio interaction, commit or `/daybreak` was run. L5-L7 still need their
separate live re-check; this review does not turn source inspection or a successful build into that evidence.
