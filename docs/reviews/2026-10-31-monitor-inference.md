# Linux monitor inference review

Codex, 2026-10-31. Reviewed `43b3837` and `a4a693e` against their main baseline, including the dated L16
note in decision 0035, `docs/verification/2026-10-31-ubuntu-monitor-area.md`, settings and menu wiring.
Read AGENTS.md, the design spec, engineering principles, and settings decisions 0024/0028. The worktree
was clean at the start. Production code, decisions, fixtures and golden images remain unchanged.

**Verdict: changes requested. Four P2 findings, reproduced by ten enabled failing Replay tests.**

## Findings

### MI1 - P2: streamed video bypasses both containment and withdrawal

**Locations:** the only inference observation is
[Canvas.cs:670](../../src/Spice.Canvas/Canvas.cs#L670); the separate stream compositor writes pixels at
[Canvas.cs:961](../../src/Spice.Canvas/Canvas.cs#L961) without observing their extents. Real stream frames
reach it through [SessionDisplay.cs:901](../../src/Spice.Session/SessionDisplay.cs#L901).

After MARK, a video frame can draw into the bottom strip, then an ordinary origin draw can still infer
a shorter monitor that excludes those pixels: the frame never updates `_drawnRight`/`_drawnBottom`.
Conversely, video drawing outside an already inferred area never withdraws it. The guest can continue
updating real screen pixels that remain cropped indefinitely, including after the inferred head is
kept across a same-size surface recreation. This violates both safeguards in decision 0035: containment
of every post-MARK guest draw, and restoration when a later draw reaches outside the inferred area.

**Regressions:** `MI1_VideoOutsideACandidateBeforeInferenceMustCountAsAGuestDraw` and both variants of
`MI1_VideoOutsideAnInferredAreaMustWithdrawItIncludingAfterRecreate` in
[MonitorInferenceReviewTests.cs](../../tests/Spice.Replay.Tests/MonitorInferenceReviewTests.cs).
On a 128x128 surface, a real CPU frame writes white pixels in rows 112-127. The tests verify those
pixels before checking the area. The first test then crops to 128x96 incorrectly; the other two retain
that crop instead of restoring 128x128. All three fail at the area assertion with zero canvas errors.

**Suggested fix:** include visible primary-surface video writes in post-MARK draw history and withdrawal,
using their actual surface/clip intersection. This does not require treating a video rectangle as a
candidate monitor. Preserve pre-MARK exclusion and ignore frame portions hidden by their clips; the
passing `VideoBeforeMarkAndVideoClippedInsideTheAreaDoNotWithdrawInference` pins those boundaries.

### MI2 - P2: normalization replaces the required original clip test

**Locations:** [Canvas.cs:670](../../src/Spice.Canvas/Canvas.cs#L670) passes `rects.Length == 1` after
[ClipRects](../../src/Spice.Canvas/Canvas.cs#L539) intersects and normalizes the clip; the eligibility
check is [Canvas.cs:385](../../src/Spice.Canvas/Canvas.cs#L385).

Decision 0035 requires no clip, or exactly one original clip rectangle equal to the draw box. The code
instead accepts any clip whose final intersection/union becomes that box. A single larger clip, a
multi-rectangle clip with a rectangle outside the box, and overlapping rectangles normalized into one
all pass. These forms occur on ordinary clipped rendering as well as on a whole-frame update; they
discard a stated discriminator for recognizing the qxl frame and broaden the false-positive crop.

**Regression:** all three variants of `MI2_OnlyTheOriginalSingleClipEqualToTheBoxMayQualify` verify a
successful fill, zero errors, and that these original clips leave the 128x128 area unchanged. Each
instead infers 128x96. A later exact one-rectangle frame is still expected to qualify after the fix.

**Suggested fix:** decide candidate eligibility from `c.Base.Clip.Type`, its original count and original
rectangle equality. Continue using normalized visible rectangles for containment and withdrawal.
The documented risk of a qualifying Linux application draw above a panel is accepted by the human;
this finding concerns the additional clip forms that the accepted rule excludes.

### MI3 - P2: rejecting one candidate permanently disables valid later candidates

**Location:** [Canvas.cs:388](../../src/Spice.Canvas/Canvas.cs#L388).

When an origin candidate does not contain an earlier draw, `_inferenceClosed` is set even though no
area was inferred or withdrawn. A later, larger frame that contains every draw and leaves valid padding
can never qualify on this surface. For example, on a 128x128 allocation, draw to row 112, draw a 128x96
origin rectangle, then draw the actual 128x112 frame. The first candidate must be rejected; the second
meets all four recorded rules, but the whole 128x128 allocation remains visible indefinitely.

Decision 0035 ends inference for the surface when a draw disproves an *already inferred* area. It does
not end inference merely because an unaccepted candidate is too small. The permanent closure also makes
the documented setting-on/next-qualifying-draw behavior unable to trim this surface later.

**Regression:** `MI3_RejectingASmallerCandidateMustNotPreventALaterFrameContainingAllDraws` expects
128x112 after that sequence and receives 128x128, with zero errors.

**Suggested fix:** reject this candidate while retaining the observed maxima for later candidates.
Keep permanent closure for an actual withdrawal. If the history proves every allowable candidate
impossible, closure can be based on that fact rather than this candidate's dimensions.

### MI4 - P2: a retained inferred head loses its configured fallback identity

**Locations:** [KeepHead](../../src/Spice.Canvas/Canvas.cs#L129) keeps only the active inferred head;
[Canvas.cs:230](../../src/Spice.Canvas/Canvas.cs#L230) reconstructs `_hasConfigHead` as false on recreate.
The setting-off path also discards that saved head at
[Canvas.cs:76](../../src/Spice.Canvas/Canvas.cs#L76). Fallback publication is at
[Canvas.cs:350](../../src/Spice.Canvas/Canvas.cs#L350).

Start with a whole-surface MONITORS_CONFIG for enabled head #1, infer a shorter area, then destroy and
recreate the primary at the same size without another config (the motivating wake order). The shown
inferred head correctly retains ID 1, but its configured fallback is gone. Turning trimming off or
drawing outside after MARK restores the whole surface with ID 0. Turning trimming off while blank has
the same result at the next create. It also loses the configured area's confirmed-monitor status.

The geometry alone looks correct, hiding the regression: `SessionInputs.MousePosition` consumes
`PrimaryMonitorId` as its wire display ID. Input now addresses head #0 instead of the configured #1.
Decision 0035 already requires retaining nonzero head identity across same-size recreation (review M1).
The new inferred-head bookkeeping must retain enough configured state to restore that identity too.

**Regression:** all three variants of `MI4_KeptInferenceMustRetainTheConfiguredFallbackMonitorId`
verify the original inferred ID is 1, then exercise the three transitions above. Each restores the
expected whole rectangle but publishes ID 0 instead of 1. The tests fail at the ID assertion; the
following confirmed-monitor assertion is retained for the fix.

**Suggested fix:** keep the configured fallback together with the inferred head and its surface size.
Disabling inference while blank should discard the inferred crop while preserving the compatible
configured fallback. A different-size primary should continue dropping incompatible kept state.

## Other reviewed behavior

- **MARK and origin:** a MARK with no primary arms nothing; a new primary clears marking/history and
  must receive its own MARK. Pre-MARK server images are excluded. Candidate origin must be exactly
  (0,0), and surface-clipped boxes cannot qualify unless their original box equals their visible box.
  Repeated MARKs on one surface do not erase observed history. These observations do not establish
  server ordering beyond the supplied verification record.
- **Padding:** the implementation allows zero padding on either axis, requires at least one padded
  axis, and caps each independently at 63. Added controls pass for (0,1), (1,0), (63,63), (0,0), (0,64)
  and (64,0). This is consistent with the recorded full-width, bottom-padding examples; requiring
  positive padding on both axes would exclude the motivating capture.
- **Ordinary draws:** normalized visible extents are accumulated even while the setting is off or
  before the agent has been identified, so enabling it later cannot ignore those earlier post-MARK
  ordinary draws. A later outside ordinary draw restores the configured area and closes inference.
  Smaller configured monitor areas win; whole-surface configs preserve an inference. Malformed configs
  are parsed before state changes. Existing tests cover those paths. MI1 concerns the missing video path.
- **Surface lifetime:** same-size recreation keeps the active inferred geometry, while different-size
  recreation drops it and resets history/closure. The added different-size/own-MARK control passes.
  MI4 concerns the configured fallback lost alongside this otherwise correct active-head retention.
- **False positives:** LF line endings identify an agent convention, not the graphics driver or actual
  monitor size. A Linux guest with a whole-surface real monitor and a qualifying origin draw ending
  within 63 pixels of an edge can be cropped until it redraws the excluded area. This is explicitly
  accepted in the dated 0035 note and is not raised again as an implementation defect. MI1 can prevent
  the promised recovery; MI2 widens the qualifying forms beyond the stated rule.

## Agent gate and threading

The gate uses the existing line-ending capability positions: LF bit 8 must be present and CRLF bit 9
absent ([SessionAgent.cs:21](../../src/Spice.Session/SessionAgent.cs#L21),
[SessionAgent.cs:262](../../src/Spice.Session/SessionAgent.cs#L262)). Monitor-config capability alone is
insufficient. Existing tests pass for the recorded `0x38de7` announcement, Windows-style CRLF, and no
line-ending capability. Added controls pass for an announcement containing both LF and CRLF as well.
This review did not independently fetch upstream protocol/agent sources; no internet was used.

Linux identity intentionally persists within one session across agent disconnect/reannouncement:
`OnAgentDisconnected` clears capabilities and the running/reassembly state, while retaining that known
guest identity. An agent process restart or desktop logout does not imply a different guest OS. The
new control exercises the real disconnect handler and subsequent capability dispatch, and verifies a
fresh session starts unknown. It does not exercise network reconnection. The viewer creates a new
`SpiceSession` in `StartSession`; no new cross-session identity cache was introduced.

`GuestAgentIsLinux` is published with volatile reads/writes. `Apply`, `BlitFrame`, and the UI's
`InferPaddedMonitor` setter serialize geometry and inference-state changes through `Canvas.Sync`.
The off setter clears a current inference synchronously, restores its current fallback and raises
`PrimaryChanged` under that lock. The UI subscriber posts work using the existing nonblocking queue;
the UI handler rereads geometry under the same lock. Presenter reading also holds `Canvas.Sync` before
its own pending-rectangle lock. No new opposing lock order or torn area/setting mutation was found.

`TurningOffWaitsForTheDisplayLockAndAnnouncesUnderThatLock` holds the display lock while a worker
requests off, verifies that request cannot complete or mutate the area until release, then checks the
whole area and that its callback held the lock. This passes. The current surface's closed-inference
state survives an off/on toggle, preserving permanent withdrawal. Live menu behavior, native window
sizing and the timing of first-sizing notifications remain source-reviewed, without a windowed check.

## Energy and settings

No new timer, polling path, background loop or per-frame logging was found. `NoteGuestDraw` normally
uses scalar comparisons and value structs; formatted logs/events occur on inference/withdrawal, and
the existing test confirms zero managed allocation after inference. Primary lifecycle/config logs are
event-driven, identical repeated configs are silent, and both new switches use existing event-driven
settings saves. These are source and allocation-test observations, not a measurement of idle GPU/CPU.

The new converter reads JSON false and case-insensitive trimmed string "false" as off, everything else
as on; missing keys preserve the true initializer. `reader.Skip()` consumes compound values without
losing subsequent fields. The generated settings context emits `trimGuestPadding` as a JSON boolean.
The existing ten `TrimGuestPaddingSettingsTests` in the window-free Video project pass, covering old
files, defaults, both round trips, null, a number, an array and the remaining fields. No settings
parsing defect was confirmed. No user settings file was read or written by these tests.

## Validation

All commands were offline (`--no-restore`); the available local assets compiled successfully.

| Check | Result |
|---|---|
| Existing Replay `MonitorInferenceTests` before additions | 20 passed |
| Added Replay `MonitorInferenceReviewTests` | 10 passed, 10 intentionally failed |
| Full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` | 421 passed, 10 intentionally failed, 0 skipped; only MI1-MI4 fail |
| `tests/Spice.Video.Tests/Spice.Video.Tests.csproj --no-restore --filter FullyQualifiedName~TrimGuestPaddingSettingsTests` | 10 passed; settings tests only, no decoder/device tests |
| `git diff --check` | Clean |

The video regressions feed decoded CPU buffers directly to the real `BlitFrame` compositor; they do not
claim a codec, network, GPU or live-guest reproduction. New tests use small synthetic messages/buffers,
and the concurrency control uses bounded synchronization. The existing Replay suite also passes its
recorded-fixture and allocation tests. No new recording or golden was added or approved.

Only this review, its index row and `MonitorInferenceReviewTests.cs` were added for the review commit.
The ten bug cases remain enabled and failing until the implementer fixes them. No production edits,
Viewer test project, viewer process/window, VM, token file, external network or internet access was used.

## Response

Claude, 2026-10-31. All four findings are accepted and fixed in `src/Spice.Canvas/Canvas.cs`; Codex's tests are
unchanged and all ten regressions now pass.

- **MI1 (video):** `BlitFrame` reports a frame drawn on the primary surface after its MARK to the inference as a guest
  draw, with its visible (surface- and clip-limited) bounds. It counts for containment and withdraws an inferred area
  it reaches outside, but is never a candidate. Pre-MARK video and video clipped inside the area still change nothing.
- **MI2 (clip):** candidate eligibility is now decided from the original clip, before normalisation: no clip, or a
  clip of type rects with exactly one rectangle equal to the box (`WholeFrameClip`). The normalised visible rectangles
  still drive containment and withdrawal. The live capture's frame (one clip rectangle equal to its box) still
  qualifies.
- **MI3 (premature closure):** a candidate that doesn't hold every earlier post-MARK draw is only rejected; the drawn
  extents stay, so a later, larger frame that holds them can still qualify. Inference closes only on an actual
  withdrawal, as decision 0035 says.
- **MI4 (fallback ID):** the configured head behind an inferred one is kept with it across a same-size surface
  (`_keptHasConfigHead`/`_keptConfigHead`) and restored as the fallback on recreate. Withdrawal, the setting turned off
  while shown, and the setting turned off while blank (the kept inferred head is replaced by the kept configured head
  before the next create) all give back head #1 and its confirmed-monitor status. A different-size surface still
  drops everything kept.

Decision 0035's "Inferred monitor" bullet now states the original-clip rule, that video counts for containment and
withdrawal, that a rejected candidate doesn't end inference, and that the configured head is kept behind an inferred
one.

Validation (window-free, no viewer window): build warnings-clean; Replay 431 passed (all of
`MonitorInferenceTests` and `MonitorInferenceReviewTests`). In the first three full Replay runs after the rebuild,
`PixelBufferTests.WarmRentAndReturnAllocateNothing` failed (it passes alone, and passed in the next seven full runs
without any change); it measures the process-wide pixel-buffer pool, which this change doesn't touch, so it is noted
here rather than treated as a regression of these fixes.
