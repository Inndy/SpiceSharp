# Audio detach and reattach review

Codex, 2026-10-30. Normal correctness review of `d2315c6` against parent `71abeaf`. Read AGENTS.md, accepted
decisions 0030/0031, the energy rules and engineering principles before reviewing the queue, renderer,
notifications and test adaptations. Live G5 is context from the verification record, not a live result from
this review.

`d2315c6`, `403c931` and `2f87365` name work-branch commits, kept under the tag `archive/audio-detach-wip`;
they were squashed into one commit on `main`.

**Verdict: changes requested. One P2 finding: a quick reattach discards the remaining virtual device lead,
which can put held drain audio outside the accepted clock budget. Two rate/channel cases reproduce H1.
The stream/device lifetime separation, detached waits and the two requested old-test adaptations otherwise
look correct within the validation below.**

## H1 - P2: Preserve the unplayed virtual device lead when attaching an empty endpoint

Locations: `src/Spice.Media/PcmQueue.cs:187-198`, especially `_deviceFrames = 0` at `:198`, following
`ExpireDetached` at `:193`; remaining-device calculation at `:544-546`.

`ExpireDetached` correctly plays the old device's held frames before taking anything from the ring. If the
replacement opens before those held frames would finish, it leaves their remaining depth in `_deviceFrames`.
`Attach` then sets that depth to zero without preserving the corresponding interval before the ring. The
new endpoint really is empty, so the remaining virtual interval must be represented in the playback schedule,
rather than simply removed. Otherwise every remaining packet moves early by that interval. A later ordinary
write can correct the error, but a draining sound has no later packet to do so.

Concrete 10 ms PCM packets on a manual clock, reproduced at mono 44.1 kHz and stereo 48 kHz:

1. Queue A, stamp 1300 against clock 1000: its deadline is +300 ms. Transfer 40 ms of the leading hold to
   the device, recording 40 ms of device depth. A is now +260 ms into the ring, after that device-held interval.
2. Rebase the guest clock backwards 30 ms and receive contiguous B, stamp 1310: A/B's deadlines become
   +330/+340 ms. The 30 ms held-clock shift is allowed by the accepted gate, so A/B remain scheduled at
   +300/+310 ms, each **30 ms early**, within the budget.
3. End the stream, detach for a default change, then attach after **10 ms**. The virtual device still has
   **30 ms** left; none of the ring is expired. Nevertheless Attach clears the device depth and leaves the
   ring unchanged. A/B now start at **+260/+270 ms after reattach**, against deadlines **+320/+330 ms**:
   **60 ms early**, exceeding the 40 ms allowance.

There is no simulated device-depth staleness or timer rounding here. The inputs and deadlines are in range;
the only prior error is the explicitly allowed 30 ms clock shift. In the 48 kHz test A's first frame is
**12480**, below the allowed **13440..17280** interval. The mono test likewise gets **11466**, below
**12348..15876**. Tests assert deadline ranges, not an exact newest-packet position.

This matters for a default switch while the last sound is draining, a path explicitly supported by 0031.
The existing timing test reattaches at 100 ms, after all 40 ms of old device depth has elapsed, so it cannot
exercise the residual-depth case. Preserve that remaining lead in the audible schedule when replacing it
with an empty physical device; do not falsely count it as samples already in the new hardware. The remedy
must also respect continued-stream protected samples and the queue's physical bounds.

This is an unresolved alignment weakness, not a claim that this commit first introduced all early playback
on default changes: the parent's Unprime path also cleared device depth, and the live record already noted
early drains after changes. The new virtual-device model explicitly promises stamp alignment on reattach,
so that inherited weakness remains relevant to its contract; the same loss can occur in failure recovery
when old held depth, including stream latency, exceeds the retry interval.

Failing regression: `AudioDetachReviewTests.FastReattachDuringADrainKeepsEveryHeldPacketInsideTheClockBudget`,
with `(rate=44100, channels=1)` and `(rate=48000, channels=2)`. Both remain failing for the implementer.

## Queue state, expiry and race checks

- **Pending versus Detached:** Reset starts Pending, preserving an initial short sound while its first device
  opens. An unsuccessful open calls Detach; subsequent retries are idempotent and do not reset its expiry origin.
  Attach expires elapsed detached time; initial Attach does not pretend the first open was already playing.
  This matches 0031's explicit initial-open policy.
- **Lazy expiry:** elapsed frames are computed from total milliseconds since detach, then reduced by what was
  already accounted for. Missing audio advances the virtual cursor too, so newly arriving audio is not charged
  for an already elapsed underrun. The added 44.1/48 kHz controls expire exactly 100 ms across 99 intervening
  writes, without per-write fractional rounding accumulating. Existing long-outage and continuous-write cases pass.
- **Drain/START:** TryEndDetachedDrain and TryContinue share the same queue lock and active/draining state.
  The new parallel control has exactly one winner. If completion wins, Start must join and reset before accepting
  new PCM; if continuation wins, the worker remains available to attach. BeginDrain refuses a detached stream,
  so STOP closes/joins rather than leaving a drain parked forever. Default-change drains reopen immediately;
  failure during a drain ends it before entering a detached wait, as the decision specifies.
- **Bounds and locking:** synchronized storage keeps its existing 2.2 s cap. Detached off-mode writes retain
  the existing ring bound and trim above 100 ms to 40 ms; expiry/discard update the protected prefix in samples.
  Transfer, epoch checks and physical device-depth publication remain under one lock. No new lifecycle-lock/Join
  inversion was found; Stop/Dispose closes the queue before joining and frees wait handles after the worker exits.

## Renderer recovery and energy checks

Device failures now detach and retry instead of ending an active stream. A normal default change bypasses
failure counting and reopens immediately. Endpoint IDs are compared in place, with both native strings freed;
same-ID notifications do not recreate the client. Resetting notification events before querying the current
default makes the ensuing attempt answer earlier notifications, while a later notification remains signalled.
Existing repeated-switch/deduplication tests pass without exhausting a retry counter.

A run is cleared only after successful buffer fills spanning `healthyMs` from the first buffer event; a single
buffer does not clear it. Existing tests cover devices failing immediately after their first buffer and devices
that keep delivering. The stream's first failure starts the run clock; subsequent short-lived opens do not reset
it. Default changes alone are not failures. This addresses the failure-counting mechanism behind G5, subject
to an actual live re-check.

Backoff is bounded by the configured maximum; no-device (`E_NOTFOUND`) goes straight to the event-only wait.
Once in that state, `WaitAny` has `Timeout.Infinite` and listens only to stop, default-change and device-change
events. Same-format START sets the latter event while detached. Existing tests verify no additional open
attempts during a quiet event-only interval and immediate interruption by stop or device events. Writes expire
audio on their existing network activity and do not wake the renderer. This is source/test evidence of no
scheduled retry wake in the parked state, not a host power trace.

Added `StartWinningDuringAnEndpointFailureKeepsItsAudioOnTheSameWorker`: the real linked renderer is paused
inside a failing fake GetBuffer call; STOP/START wins continuation, then the endpoint error is released.
Recovery keeps the same worker and the new PCM marker. This covers an actual endpoint-error path as well as
the implementation's new default-change/open-failure fixture.

## Notification callbacks and allocation

The Replay test project now links the **unchanged production** `DeviceNotifications.cs` instead of its empty
test stub. Enumeration, endpoint registration and audio calls remain fake. Tests invoke the notification
vtable directly with test-owned events; they do not register with Windows audio or touch a host device.

- Console-role render default changes signal only the default event and increment the atomic count; capture
  or another role is ignored. Added and active-device callbacks signal the device event; disabled does not.
- A warmed batch of 1,000 default/added/active callback sets, including consuming their events, measures
  **0 managed allocation bytes**. State and GUID values are structs; normal callbacks do not build strings.
- Late default/added/active calls after disposal of the test events return zero. Signal catches the disposed
  event exceptions, so they do not escape through the unmanaged-callable boundary. This exceptional teardown
  path may allocate exception objects; the allocation claim concerns the normal callback path.
- Source review of Query/AddRef/Release and Create's failure cleanup found no new reference/handle ownership
  defect for valid COM callers. The test IID and registration are stand-ins, so these checks do not establish
  the real CsWin32 interface identity, OS registration or driver callback lifetime on a live endpoint.

Existing warm queue/renderer allocation guards pass, including detached writes in both pacing modes. The
new clock delegate is created/reused at queue construction, not per packet. Retry exceptions and transition
logs are control-path work. No new timer or per-write allocation was found.

## The two old-test changes

**Approve both as test-owner adaptations to 0031; neither warrants restoring the old expectation.**

- `ReopenedEmptyDrainRetiresBeforeStartingAnotherClient`: setting the fake default to a different endpoint
  before signalling is necessary now that same-ID notifications are deliberately ignored. It retains the
  original E3 assertion: the empty drain retires before another client starts. It still passes.
- `StartDuringADeviceFailureContinuesTheStreamOnTheSameWorker`: replacing the obsolete E4 requirement to
  abandon/join a dying worker is correct because ordinary device failure no longer retires the stream.
  The new fixture pauses a detached default-change drain, continues it, fails one subsequent open, and verifies
  the same live worker, accepted audio and reattach. That is a legitimate policy change, not a weakened
  assertion permitting lost audio. The added endpoint-error control above covers the different failure seam;
  the parallel queue test covers completion winning instead of START. No old assertions were edited in this review.

## Minor qualifications

- The 10 s retry budget is checked before a timed wait, not again before the following open. A final backoff
  can cross the deadline by up to the configured 2 s maximum and permit one extra attempt, plus its open duration.
  After its failure the thread parks indefinitely. This is a bounded policy-precision note, not an unbounded
  polling defect; no failing test was added for it.
- Count/Statistics do not force expiry and Close counts the still-stored queue as discarded. Detached off-mode
  trimming is lazy on Write/ReadyFrames, so a pre-existing burst above 100 ms can remain stored until another
  operation. The backing ring remains bounded; these are accounting/wording qualifications, not additional P2s.

## Validation and scope

| Window-free check inside this worktree | Result |
|---|---|
| Existing full Replay suite before review additions | **236 passed**, 0 failed, 0 skipped |
| Added `AudioDetachReviewTests` | **6 passed, 2 failed**; the failures are the two H1 format cases |
| Full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` after additions | **242 passed, 2 failed, 0 skipped**, 244 total; only H1 fails |
| `tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | **24 passed** |
| `tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | **13 passed** |
| `git diff --check` | Clean |

Added this review, its index row and `AudioDetachReviewTests.cs`. The Replay source-link entry and fake backend
were adjusted only to test the real notification callbacks. All changes are uncommitted. Production code,
decisions, old review assertions, verification records and golden images are untouched. No viewer test project,
viewer/UI/VM access, host audio-device interaction, artificial CPU stress, commits or `/daybreak` were used.
The new tests use manual time, fake audio endpoints and test-owned kernel events; this does not validate live
speaker latency, device switching, driver behavior or physical idle power.

## Re-review

Codex, 2026-10-30. Reviewed `2f87365` against its parent `403c931`, which includes the original review and
regressions. **Verdict: approve. H1 is fixed, the final backoff is cut to the remaining budget, and no new
P1/P2 issue was confirmed.** The previously failing H1 cases and all earlier review cases pass unchanged.

### H1 verification and regression boundaries

`PcmQueue.Attach` now converts the unplayed virtual device remainder into actual silence before held
synchronized PCM, then clears the new physical device depth. Queue plus device lead is therefore preserved
across an ordinary reattach instead of losing that interval. In the original H1 fixtures, 30 ms of remaining
lead is restored: A/B play at **+290/+300 ms after reattach**, against **+320/+330 ms** deadlines. Both retain
their original 30 ms early phase, within the 40 ms budget, at mono 44.1 kHz and stereo 48 kHz. Neither test's
assertions were changed.

Added seven passing cases in `AudioDetachReviewTests.cs`:

- **Protected-prefix growth, two formats:** continue a detached stream, restore 30 ms ahead of its protected
  old tail, then consume the old pre-insertion sample count. The remaining old PCM must still keep its position
  through another new-stream write. After its final frame is consumed, normal new-stream correction resumes.
  This verifies that the added prefix is protected for exactly as long as the previous stream's tail.
- **Near-cap insertion, two depths:** with a maximum-delay packet and a nearly full ring, a 40 ms old device
  depth restores all 30 ms remaining after expiry; a 100 ms depth exhausts available room. Queue size stays at
  or below **2.2 seconds**, the entire PCM payload survives in order, and all preceding added data is silence.
  `EnsureCapacity` is capped before insertion, and insertion is limited to actual room. This checks storage
  safety and payload preservation, not an unlimited timing guarantee when the physical cap clips the lead.
- **Empty drain and off mode:** an empty synchronized drain gets no synthetic prefix and can finish in
  pre-roll; unsynchronized PCM gets no extra hold. The unchanged real-renderer
  `ReopenedEmptyDrainRetiresBeforeStartingAnotherClient` also passes, preserving E3.
- **Warm reattach allocation:** 1,000 fast detach/attach cycles each restore a 30 ms prefix, wrapping through
  the ring while continuing writes. The measured window allocates **0 managed bytes**; queue depth stays
  310 ms and no correction beyond the initial hold appears. Initial capacity growth remains bounded and is
  not a per-packet allocation.
- **Final backoff budget:** a fake device fails every open, with a 60 s retry interval but a 50 ms test budget.
  The worker reaches the infinite event-only wait promptly and performs no further attempts during a quiet
  interval. The test does not pin an exact attempt count: coarse TickCount64 steps can require additional
  short waits near the deadline. The production budget is still 10 s and the maximum backoff remains 2 s.

All new queue mutations remain under the existing lock. The added prefix increases the protected count only
when there already is a protected prefix, and only by samples actually inserted. Prepending cannot overrun
the ring after the room clamp; channel/sample arithmetic remains consistent. Attach is executed by the single
render worker after the old client has been released, so it does not introduce an older in-flight Transfer
crossing a new attach. No new lifecycle-lock/Join or epoch race was found.

### Backoff fixture and counter-only qualifications

The implementer's sole existing-test change raises `retryBudgetMs` in its own
`StopOrADeviceEventEndsABackoffWaitAtOnce` fixture to 600 s. That keeps its deliberately long 60 s wait from
being shortened by the new budget clamp. The stop/device-event interruption assertions remain unchanged;
the test still interrupts the wait instead of sitting it out. This is a valid fixture adaptation. The new
short-budget test independently checks the clamping behavior.

I agree with retaining the counter semantics rather than expiring inside Close. ExpiredFrames records lazy
playback advancement on Write/Attach; DiscardedSamples records the samples actually removed by Close. Making
Close advance virtual playback would reclassify samples according to the tick at shutdown. Count/Statistics
remaining observational is also reasonable. The original notes were qualifications, not required fixes or
additional findings; they do not block approval. No change to the accepted lazy-expiry design is requested.

### Re-review validation

| Window-free check inside this worktree | Result |
|---|---|
| Existing full Replay suite before additions | **244 passed**, including both unchanged H1 cases and E3 |
| Seven added re-review cases | **7 passed** |
| Full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` after additions | **251 passed, 0 failed, 0 skipped** |
| `tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | **24 passed** |
| `tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | **13 passed** |
| `git diff --check` | Clean |

Only this section and additional methods in `AudioDetachReviewTests.cs` were edited, uncommitted. Production,
decisions and existing test bodies/assertions were not changed. No new failing test is warranted. No Viewer
test project, UI/VM/host audio-device interaction, artificial CPU stress, commits or `/daybreak` were used.
The result is source and window-free regression verification, not a new live latency or device-switch result.
