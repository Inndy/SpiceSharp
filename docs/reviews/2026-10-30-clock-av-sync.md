# Clock-mode A/V sync review

The change was reviewed in six rounds on a work branch, then squashed into one commit on `main`. Hashes other than
`80839e1` and `5c567c9` name those work-branch commits, kept under the local tag `archive/avsync-wip`.

Codex, 2026-10-30. Normal correctness/regression review of the uncommitted clock-mode audio change on base
`80839e1`, including untracked `PcmQueue.cs`, `AudioSyncTests.cs` and Proposed decision 0030. Read decisions 0025
and 0030, the spec's energy rules and the engineering principles. The human's choice to delay audio in `clock`
mode is accepted here; this review assesses its implementation.

**Verdict: changes requested. Three P2 correctness issues remain, reproduced by new window-free tests.**
No production code was edited. No commits, viewer tests, viewer launch, desktop/VM interaction, artificial CPU
load, or `/daybreak` were used.

## Findings

### A1 - P2: Read a coherent multimedia-clock base on the playback thread

Locations: `src/Spice.Session/SessionPlayback.cs:31-35`; shared writer/read implementation at
`src/Spice.Session/SpiceSession.cs:291-302`.

The new playback-channel reader calls `MultimediaTime` without synchronizing with the main-channel rebase.
`MultimediaTimeBase` publishes `_mmBase` and `_mmBaseTicks` separately. Reading `_mmSeeded` with `Volatile.Read`
does not make those two fields an atomic snapshot, particularly after the clock has already been seeded and
the reader can observe the previous `true`. A packet can therefore be scheduled against a clock that never
existed, causing unnecessary audio drops or inserted silence. The two channel loops can execute concurrently.

Confirmed with a controlled clock read and separate writer/reader tasks, without busy loops: initially the
guest base is 10000 at local time 1000; local time advances to 2000, then the main channel rebases to guest
11000. This is a **no-op rebase**: both complete bases give guest time 11000 and a packet stamped 11400 should
wait 400 ms. Between the two field assignments, the new audio reader instead returns **-600 ms**. That value
is fed directly to the queue, where it can trigger a substantial skip despite no actual clock discontinuity.

Publish/read the guest/local base pair coherently, including the rebase writer. Merely making each individual
field volatile is insufficient. The regression allows a correct reader to serialize behind the writer; it
does not require a lock-free implementation.

Failing regression: `AudioSyncReviewTests.PlaybackThreadNeverSeesAHybridGuestClockDuringRebase`.

### A2 - P2: Retiming must include audio already queued when the clock moves backwards

Locations: `src/Spice.Media/PcmQueue.cs:129-157`; `src/Spice.Session/SessionPlayback.cs:31-35`. Video rebase
handling is in `src/Spice.Session/SessionDisplay.cs:326-354`.

A positive scheduling correction appends silence **after** the audio already in the ring and before the new
packet. That aligns the new packet, but cannot retime earlier held packets. Video's queued deadlines, in
contrast, are all moved when the guest clock is rebased. A backwards clock step therefore leaves already
queued sound playing at its old time, ahead of the video, followed by the inserted gap. This affects the
clock/latency-change scenario explicitly covered by decision 0030.

Confirmed sequence at 1000 Hz stereo:

1. Packet A (10 ms) is stamped 1300 with the guest at 1000: the queue inserts 300 ms of silence.
2. Drain 250 ms. A is still 50 ms away, with 50 ms of silence and 10 ms of A left in the ring.
3. Rebase the guest from 1250 to 1050. Video A now belongs 250 ms from now.
4. Packet B, stamped 1310, correctly gets delay 260 ms. The queue reports a 200 ms correction.
5. Output is **50 ms silence, A, 200 ms silence, B**, rather than **250 ms silence, A, B**. A is heard
   **200 ms early**, even though B is aligned.

The session must propagate rebase semantics to the held audio, or retain enough packet timing information to
retime it. Blindly prepending every positive correction would not be a general fix: an actual gap between
packet timestamps belongs between packets. The current per-packet delay alone does not distinguish that gap
from a rebase of all existing deadlines. A rebase with no immediately following audio packet also currently
leaves held audio untouched.

Failing regression: `AudioSyncReviewTests.BackwardClockRebaseAlsoDelaysAudioAlreadyWaitingInTheQueue`.
It uses the actual session clock/delay and queue, not a duplicate scheduling algorithm; it does not play audio
or render video.

### A3 - P2: Publish device occupancy and dequeue as one coherent transfer

Locations: `src/SpiceViewer/Audio/AudioOutput.cs:284-295`; `src/Spice.Media/PcmQueue.cs:90-93`, `:125`.

`Fill` publishes `padding + frames + latencyFrames` before calling `GetBuffer` and `queue.Dequeue`. Until that
dequeue, those `frames` are still in `_count`. A network write in the gap adds `_count / channels` to the new
device estimate and counts the same audio twice. The individual queue operations are locked/atomic, but the
handoff is not. This can turn an in-tolerance packet into a spurious correction and discard audible samples.
Publishing after dequeuing without a shared transaction would instead create an undercount window.

Confirmed by replaying the production call ordering through the actual queue API, with no WASAPI calls:
90 ms of real audio are queued, device padding and stream latency are zero, and `Fill` plans to transfer 40 ms.
It publishes 40 device frames while all 90 are still queued. An intervening packet due in 89 ms has only a
1 ms genuine error, but `Write` sees 130 ms ahead and **drops 41 ms**. The test then performs the planned
dequeue; it has already lost part of the earlier packet. This is an ordering proof, not a measurement of how
often that interleaving occurs on a physical endpoint.

Make ring removal and the associated device-depth publication coherent to synchronized writers. A renderer
test seam should exercise the resulting transfer directly when that API is introduced.

Failing regression: `AudioSyncReviewTests.PublishingDeviceTransferBeforeDequeueMustNotDropInToleranceAudio`.
This test deliberately reproduces the existing `Fill` sequence; it does not invoke private `Fill` or constitute
a WASAPI integration test. When the handoff is refactored, it should call the new production transfer operation
instead of preserving the old split sequence.

## Rules, regressions and test coverage

- **Off/spacing:** no additional audio delay is selected in these modes. The extracted unsynchronized queue
  retains the old 200 ms bound, 20 ms prime and >100 ms-to-40 ms trim behavior. The existing mode-switch tests
  pass, including dropping the clock-mode hold on the next unsynchronized write. No separate off/spacing
  regression was confirmed.
- **Frames/samples, ring wrap and growth:** writes truncate incomplete input frames, correction lengths are
  converted to whole interleaved frames, and growth copies the wrapped queue before resetting its head. Under
  the renderer's accepted rate/channel limits, the arithmetic fits `int`. Source inspection confirms the
  backing-array growth ceiling is 2.2 seconds (2 seconds of delay plus the 200 ms packet allowance); the
  presentation-delay clamp is 2 seconds. Added tests pass at 8 kHz mono, 44.1/48 kHz stereo and 96 kHz/8 channels,
  checking silence placement, exact packet contents and the logical queue bound across sustained writes.
  The 8-channel case exercises the renderer/queue range; the current session decoder accepts only mono/stereo.
- **Underrun/device reopen:** added tests pass for synchronized and unsynchronized re-priming, whole-frame
  output and clearing the stale device-depth estimate on `Unprime`. `Dequeue` also zero-fills if a concurrent
  correction reduced the queue after `ReadyFrames`; this prevents negative counts/stale samples, but does not
  solve the occupancy race A3.
- **Energy/native binding:** no new timer, polling loop or native import was introduced. Existing WASAPI event
  waits and stop/join lifecycle remain; no new `DllImport` occurs. `PcmQueue.Write` can allocate during bounded
  ring growth, then reuses it; existing warmed queue/session allocation tests pass. Those tests do **not**
  cover the entire renderer: the debug correction log at `AudioOutput.cs:82-84` formats strings on the audio
  write path whenever a correction occurs with debug enabled. Thus the tests do not establish unconditional
  end-to-end allocation freedom, including the requested diagnostic mode. No exception to the binding energy
  rule is assumed by this review.
- **What the original tests prove:** all 21 `AudioSyncTests` cases pass. They establish single-threaded queue
  arithmetic at 1000 Hz, chosen mode transitions and warmed allocations in the queue/session. The session
  rebase test checks the next delay value, without a queue; it therefore misses A2. The interleaving test only
  changes the queue between readiness and dequeue, without a device-occupancy transfer, so it misses A3.
  The new separate-channel test exposes A1; it pauses only at a clock-read boundary, with bounded event waits.
- **Remaining runtime assumptions:** the window-free tests cannot establish perceptual lip sync, physical
  device latency or how rare corrections are. Microsoft's documentation defines
  [GetCurrentPadding](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getcurrentpadding)
  as queued endpoint frames, and
  [GetStreamLatency](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getstreamlatency)
  as a fixed maximum for the stream. These do not verify the design's treatment of the latter as an exact
  additional physical delay. The upstream SPICE behavior described as remembered in Proposed 0030 was not
  independently validated in this review. Keep live lip-sync and latency claims provisional.

## Validation

All commands ran **inside this worktree**, using its copied native DLLs. Only the projects allowed by the brief
were run; no solution-wide or viewer build/test was performed.

| Command | Result |
|---|---|
| `dotnet test tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore --filter FullyQualifiedName~AudioSyncTests` | 21 passed |
| `dotnet test tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore --filter FullyQualifiedName~AudioSyncReviewTests` | 6 passed, 3 intentionally failing regressions |
| `dotnet test tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` | 154 passed, 3 failed, 0 skipped; all failures are A1-A3 |
| `dotnet test tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | 24 passed |
| `dotnet test tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | 13 passed |

Added only `tests/Spice.Replay.Tests/AudioSyncReviewTests.cs`, this report and its review-index row. The three
regressions remain failing until the implementation is fixed. No golden images or implementer's existing
uncommitted production/design/test changes were modified.

## Re-review

Codex, 2026-10-30. Reviewed WIP `0251588a6a2ae7070c346ed5d8ff369adf3f9f01` against main `5c567c9`, in
the same worktree. The worktree was clean at the start. **Changes requested: A1 and A3 are fixed; A2's original
backward-rebase case is fixed, but the gap/rebase split remains incomplete and introduces a new gap-accumulation
regression.** Two new window-free tests reproduce these remaining problems.

### A1-A3 fix verification

| Finding | Verdict | Evidence |
|---|---|---|
| A1: coherent guest-clock base | **Fixed.** | `SpiceSession.cs:288-311` constructs an immutable `MmClockBase` completely before `Volatile.Write` publishes its reference. `MultimediaTimeAt` takes one volatile read into a local and reads both values from that object. No reader combines fields from separate bases. The original controlled rebase race and the new whole-base test pass. |
| A2: retime previously held audio | **Partially fixed.** | `PcmQueue.Write` now prepends the non-gap portion of a positive correction. The original 200 ms backward-rebase regression passes unchanged. However, limiting the gap to the net positive error loses forward retiming, and ignored small gaps are later mistaken for a rebase. See B1 and B2. |
| A3: atomic queue-to-device handoff | **Fixed. Assertion preserved.** | `Transfer` removes queue samples and updates `_deviceFrames` inside the same `_lock` that synchronized `Write` holds. `AudioOutput.Fill` calls it once after `GetBuffer`, with padding plus all newly submitted frames plus stream latency. It no longer publishes those frames separately before removing them from the ring. The adapted regression and the added transfer bookkeeping test pass. |

The A3 test at `tests/Spice.Replay.Tests/AudioSyncReviewTests.cs:120-137` was adapted in the way the original
review requested: its network write runs before the indivisible transfer, and it calls the new production
`q.Transfer(...)`. All three original protections remain:

```csharp
Assert.Equal(0, correction);
Assert.Equal(0, q.Statistics.SyncDroppedFrames);
Assert.All(transferred, sample => Assert.Equal(111, sample));
```

No assertion was weakened to accept a correction or discarded audio. The separate
`TransferMovesAudioIntoTheDeviceCountInOneStep` test also checks the post-transfer state (60 queued frames,
45 device frames including latency) and zero correction for the next correctly timed packet. The lock
excludes a synchronized writer from the middle of the transfer; these remain queue-level tests, not execution
of the WASAPI renderer. The adapted test's inline comment referring to the old split publication is stale but
does not affect its assertions.

### B1 - P2: A stamp gap can conceal a forward clock rebase of held audio

Location: `src/Spice.Media/PcmQueue.cs:132-143`; gap supplied by `src/Spice.Session/SessionPlayback.cs:82-85`.

The code splits only a **positive net error**, and clips `gapMs` to that error. A source gap and a forward clock
step have opposite signs in the net error. When they partially cancel, the earlier audio still needs to move
forward, even though the new packet needs a net positive amount of silence. The current split treats all of
that net correction as a gap and leaves the old audio on its previous deadline.

Confirmed using the actual `OnPlaybackMessage` -> `AudioData` -> `PcmQueue.Write` path with 10 ms stereo PCM
packets at 48 kHz:

1. With guest clock 1000, A stamped 1300 is held for 300 ms.
2. The guest clock rebases forward to 1100. A now belongs at +200 ms.
3. B is stamped 1510: the session correctly supplies a 200 ms source gap after A's end stamp 1310, and a
   410 ms presentation delay for B.
4. The queue's 310 ms plus the new net 100 ms silence puts B correctly at +410 ms, but leaves A at **+300 ms**,
   **100 ms late** against its rebased deadline. It shortens the source gap instead of retiming A.

The same cancellation can hide the necessary retime inside the 40 ms deadband, so only changing the positive
split branch is insufficient. Separate the held timeline's clock correction from the inter-packet source gap
before applying the tolerance and arranging output, while retaining the existing overall capacity bound.

Failing test: `AudioSyncRereviewTests.ForwardClockStepAndStampGapKeepBothPacketsOnTheirDeadlines` in
`tests/Spice.Replay.Tests/AudioSyncRereviewTests.cs`. B's marker is verified at +410 ms; A's first frame is
**14400**, rather than the required **9600**. This is a remaining A2 variant, not a failure of A1's publication.

### B2 - P2: Unapplied small gaps accumulate and wrongly delay earlier audio

Location: `src/Spice.Media/PcmQueue.cs:135-142`, `:168-172`; the session supplies only the latest gap at
`src/Spice.Session/SessionPlayback.cs:82`.

A gap within the tolerance is omitted from output and is not remembered. When multiple such gaps accumulate
past 40 ms, `error` includes all of them, but `gapMs` describes only the latest. The remainder becomes `before`
and is prepended to **all earlier audio**, as if the clock had rebased. Repeating this pushes already held
packets beyond the allowed tolerance even though their deadlines and the guest clock never changed. This
regression comes from the new prepending behavior; the old append-only correction did not move A in this case.

Confirmed with a network burst of five 10 ms, 48 kHz stereo packets stamped **1300, 1340, 1380, 1420, 1460**,
against an unchanged guest clock of 1000. There is a real 30 ms pause between consecutive packets. The second
and fourth writes leave a 30 ms error alone. The third and fifth see 60 ms accumulated error, classify only
30 ms as a gap, and each prepend another 30 ms before A. A consequently begins at **+360 ms**, not +300 ms:
**17280 frames**, outside the permitted +260..+340 ms interval (**12480..16320 frames**). The newest packet is
aligned, masking the displacement of the earlier ones.

Retain the pending source-gap contribution or preserve packet timing so it cannot later be reclassified as a
clock shift. Tolerance of the newest packet's net error alone does not preserve deadlines of earlier audio.

Failing test: `AudioSyncRereviewTests.RepeatedSmallStampGapsDoNotRebaseEarlierHeldAudio`, in the same new test
file. Its assertion explicitly allows the configured 40 ms tolerance; it does not require sample-perfect sync.

### Other fix-path checks

- **Publication:** the clock object's reference does not escape for mutation, and no new locking cycle is
  introduced. Playback and video both use the coherent base reader; the seeded check cannot combine two base
  fields. The base allocation happens on the main-channel control message, not on audio packets.
- **Transfer:** queue removal and device accounting share `_lock`; no callback, native call or lifecycle lock
  is taken inside it. Empty/starving transfers still publish the submitted device frames, including silence;
  partial queue contents are zero-filled. Direct occupancy publication in `Fill` is restricted to paths that
  remove no audio. No additional transfer/deadlock defect was confirmed.
- **Ring operations:** prepending wraps within the allocated ring; capacity clamps leave room for the combined
  prepend, between-packet silence and new packet. Existing wrap, real-rate, cap, underrun and off/spacing tests
  pass. No new frame/sample or array-bound defect was confirmed.
- **Gap calculation:** decoded `samples` are per channel; using the playback frequency to compute the previous
  packet's end stamp is consistent with that contract. The new wraparound test passes for contiguous packets
  across the u32 timestamp wrap. Rounding to integer milliseconds was not the cause of B1/B2: both use exact
  10 ms packet lengths at 48 kHz.
- **Allocation/energy:** per-correction string formatting has been removed from `AudioOutput.Write`; counters
  are logged only at stream closure. Warmed packet/queue/transfer allocation checks pass, including repeated
  corrections. Their shared `AllocationWindow` helper still rejects nonzero allocations in an undisturbed
  sample; replacing the explicit assertions at those call sites did not remove the zero-byte requirement.
  No timer, polling loop, or new native import was introduced by these fixes.
- **Rebase without a following packet:** revised Proposed 0030 explicitly defers queue retiming until the next
  packet and acknowledges that device-held audio cannot be retimed. This remains a documented limitation;
  passing the original rebase test does not establish immediate main-thread retiming or physical lip sync.

### Re-review validation

All tests ran in this worktree, with its existing native DLLs. No production files, existing regression
assertions, golden images, or other worktrees were edited. No viewer/UI/VM access, viewer test project,
artificial CPU load, commits, or `/daybreak` were used. Live testing in the other worktree is outside this
review's evidence.

| Check | Result |
|---|---|
| Existing `AudioSyncTests` and `AudioSyncReviewTests` (`--filter FullyQualifiedName~AudioSync`, before adding new cases) | **35 passed**, including all original A1-A3 regressions |
| New `AudioSyncRereviewTests` | **1 passed, 2 failed**: the stamp-wrap control passes; B1 and B2 remain failing |
| Full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` | **166 passed, 2 failed, 0 skipped**; only the two new regressions fail |
| `tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | **24 passed** |
| `tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | **13 passed** |

The re-review section and `AudioSyncRereviewTests.cs` are left uncommitted. The new tests exercise production
session decoding, gap propagation and queue ordering with marked PCM; they do not use an imitation scheduler
or claim to measure actual device presentation. Physical latency and the other worker's live A/V observations
remain separate validation.

## Second re-review

Codex, 2026-10-30. Compared `22b09d2ffe302b758131c81b5b149a053aa0c9eb` against `0251588`. The worktree
was clean at the start. **B1 and B2 are fixed against their unchanged regressions, but changes are still
requested: the stamp-lead implementation introduces two confirmed timing regressions (C1/C2).** The deliberate
small-gap policy is evaluated separately below; passing its characterization tests is not approval of the
departure from the brief.

### B1/B2 verification

- **B1: fixed.** Clock shift is now calculated after subtracting the pending source gap. The forward 100 ms
  rebase is no longer hidden by the 200 ms gap: the original marked-PCM test finds A at +200 ms and B at
  +410 ms, with the full gap between them. The old test was not weakened.
- **B2: fixed.** Gaps left unapplied are remembered in `_stampLead` and no longer become silence before earlier
  audio. The five-packet/30 ms-gap burst passes the original first-packet deadline assertion unchanged. The
  implementation's additional accumulated-gap test also passes.
- **A1/A3 remain fixed.** Clock publication and `Transfer` locking are unchanged in this commit, and their
  regressions still pass. The A3 zero-correction, zero-drop and transferred-sample assertions remain intact.

All **41** pre-existing audio-sync tests passed before adding the new tests. These include the unchanged
`AudioSyncRereviewTests.cs` from the previous round and the new signed-jitter/overlap tests in the commit.

### Jitter reasoning and the deliberate small-pause policy

**Assessment: agree with filtering signed timestamp jitter; agree with the 40 ms pause allowance only with
an explicitly accepted timing budget. It is not equivalent to the original brief.**

The implementer's reasoning about *asymmetric* exact gap handling is sound: bounded timestamp jitter creates
positive and negative inter-packet differences. Inserting every positive difference while ignoring negative
ones rectifies noise into growing delay. Trimming the corresponding PCM for every negative difference avoids
that bias but repeatedly discards real samples. Keeping a small signed lead pending avoids both reactions.
A new actual session-to-queue test with absolute timestamps alternating by +/-3 ms at 48 kHz passes with
**no gap insertion, no packet trimming and no clock correction after the initial hold**.

That supports a jitter filter, but does **not** establish that its band must be 40 ms. Bounded +/-3 ms absolute
stamp jitter produces adjacent differences up to 6 ms; a 40 ms gap band is a separate quality/correction-rate
choice. The test does not establish actual guest stamp noise, or prove that a smaller separate band would
cause repeated corrections. Furthermore, integer packet-duration bias is not symmetric jitter; C2 shows the
current integrator responds badly to it even with the 40 ms band.

For a genuine isolated 30 ms pause, the new policy omits that pause and plays the following contiguous audio
**30 ms early indefinitely**, until another event changes the phase. The passing characterization test verifies
A at +300 ms, B at +310 ms rather than its +340 ms stamp, and C at +320 ms rather than +350 ms. Thus the
deviation is not just a slightly different silence boundary: it shifts later audio as well. I consider that
defensible for a best-effort low-correction mode **if shortening real pauses and that source-phase error are
explicitly accepted**. It is not exact pause reproduction, and no perceptual or live lip-sync result is claimed.

There is also an important combined-budget consequence: the clock-shift deadband and pending-gap allowance
are independent. With a 30 ms backwards rebase plus a 40 ms source gap, neither is corrected. A passing
characterization test requests B at **+380 ms** and finds it at **+310 ms: 70 ms early**, with no new correction.
At the two 40 ms boundaries the arithmetic permits **80 ms** of combined error before device-estimate error
is considered. Therefore I would not accept the description "at most 40 ms early overall". Either accept and
document this larger combined budget, or constrain the combined error/use a separate smaller jitter allowance.
The human's requested departure remains a decision to approve, not something this review silently adopts.

### C1 - P2: A long source pause leaves expired stamp lead that drops resumed audio

Locations: `src/Spice.Media/PcmQueue.cs:163-164`, `:175-183`, `:197-200`.

The logical clock-shift calculation is clamped to the ring's 2.2-second capacity **before** it gives up expired
source-gap debt. That capacity limit should bound storage/sample operations, but it also limits how much old
lead can be reconciled in one write. A long pause can leave seconds of stale lead attached to a packet whose
actual delay is only 300 ms. The next correctly timed packet then causes a large drop of valid resumed audio.

Confirmed through the actual session/queue path at 48 kHz stereo, without a STOP/START between packets:

1. Play/drain A stamped 1300, then advance the manual clock by five seconds with the queue empty.
2. Receive R1 stamped 6300. Its source gap is 4990 ms, but its delay is **300 ms**, well inside the normal cap.
3. The real expired component is 4690 ms. Clamping the negative shift to 2200 ms, then laying out 300 ms of
   silence, leaves **2490 ms** of stamp lead pending.
4. Drain 10 ms and receive contiguous R2 stamped 6310, still due in 300 ms. The stale lead causes the queue to
   drop **14400 frames (300 ms)**, including R1, instead of preserving the correctly scheduled resumed packet.

This is not a request to buffer the five-second pause or extend the 2-second presentation limit. The pause
already elapsed, and both resumed packet deadlines are ordinary 300 ms deadlines. Reconcile expired logical
gap/time debt independently of the bound on physical samples that may be inserted or removed.

Failing test: `AudioSyncSecondReviewTests.ResumingAfterFiveSecondSourcePauseDoesNotDropTheResumedPacket`.
The measured result is `(14400 dropped frames, R1 absent)`, versus `(0, R1 present)`.

### C2 - P2: Per-packet millisecond truncation becomes accumulating phantom gaps

Locations: `src/Spice.Session/SessionPlayback.cs:82-83`; accumulated by
`src/Spice.Media/PcmQueue.cs:163`, `:175-208`.

The previous end stamp is calculated as `packetStamp + floor(packetFrames * 1000 / rate)`. For valid PCM
blocks whose duration is a fractional millisecond, that differs systematically from the next absolute stamp
rounded to milliseconds. It creates one-sided +1 ms "gaps" in genuinely contiguous audio. Previously these
small values were harmless unless a real net scheduling error accumulated. The new stamp-lead integrator
adds them all, mistakes the result for a clock shift, drops old audio and inserts artificial gap silence.

Confirmed with **200 contiguous 256-frame raw stereo packets at 44100 Hz** (about 5.805 ms per packet).
Packet stamps are `1400 + floor(i * 256 * 1000 / 44100)`; the manual guest clock advances by the same rounded
elapsed time. Every packet's reported delay is exactly **400 ms**, an ideal device consumes exactly 256 frames
per step, and there are no source pauses or rebases. Nevertheless, the queue reports **5412 frames dropped,
5412 frames of gap silence inserted, and 4 clock corrections**, rather than zero drops/gaps and the initial
hold only. The three extra correction steps are about 40.9 ms each. The band postpones the bias; it does not
cancel it.

Preserve fractional timing/quantization state when inferring source gaps, so rounding each packet's duration
does not create a persistent positive source-gap signal. Supporting integral 10 ms packets is insufficient:
the raw PCM path accepts these frame counts, and the previous real-rate tests used integral-ms packet lengths.

Failing test: `AudioSyncSecondReviewTests.ContiguousFractionalMillisecondPacketsDoNotAccumulatePhantomGaps`.
It measures production decoding, session gap propagation and queue accounting, not a duplicate scheduler.

### Other changes and validation

The new statistics value is a readonly record struct, and gap/correction counters remain under the existing
queue lock. Close-of-stream logging is still outside the packet path. No change to `MmClockBase` publication,
`Transfer` ownership, native imports or timer/wake behavior was found in this diff. Existing wrap, overlap,
mode-switch, cap and allocation tests pass. A1-A3 and B1-B2 regression assertions were not edited in this round.

Added `tests/Spice.Replay.Tests/AudioSyncSecondReviewTests.cs`: two failing bug regressions and three passing
characterizations (bounded +/-3 ms jitter, an isolated 30 ms pause, and the combined 70 ms early case). The
characterization assertions intentionally describe the proposed policy; they do not turn its departure into
an approved requirement. All new scenarios are deterministic, window-free and use no actual device or sleep
to simulate the pause; the clock is advanced directly.

| Check, in this worktree | Result |
|---|---|
| Existing `--filter FullyQualifiedName~AudioSync`, before adding new tests | **41 passed**, including B1/B2 unchanged |
| New `AudioSyncSecondReviewTests` | **3 passed, 2 failed** (C1/C2) |
| Full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` | **174 passed, 2 failed, 0 skipped**; only C1/C2 fail |
| `tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | **24 passed** |
| `tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | **13 passed** |

Only this appended section and the new test file are left uncommitted. No production code, existing tests,
golden images or other worktrees were edited; no viewer/UI/VM access, artificial CPU load, commits or
`/daybreak` were used. The other worktree's live test remains separate evidence. The two confirmed bugs stay
exposed by failing tests until implementation fixes land.

## Third re-review

Codex, 2026-10-30. Compared `4e90a204424e89345bf1e04b9a6b3561e90fa2c7` with `788bb33`. The worktree
was clean at the start. **C1/C2 are fixed, the protected tests are byte-unchanged, and the two authorized policy
expectation changes are legitimate. Changes are still requested: the new combined-budget gate does not bound
the error of all previously held audio (D1).** Two new failing cases reproduce this one remaining issue.

### Test preservation and authorized changes

Compared the raw Git blob bytes at both revisions, not just test names or assertions:

| Protected content | Result |
|---|---|
| Entire `AudioSyncReviewTests.cs` (A1-A3 and original rate/underrun tests) | Byte-identical; SHA-256 `8a8f92441d76655089603bde6485bc8fbc90260ee21180e056e86688237ec95a` |
| Entire `AudioSyncRereviewTests.cs` (B1/B2 and wrap test) | Byte-identical; SHA-256 `b721df06daadb9eee83a18919c288b7d5e801faded1212517497226d9532066d` |
| Prefix of `AudioSyncSecondReviewTests.cs`, including its harness, C1/C2 bug tests and bounded-jitter test | Byte-identical; SHA-256 `29c1c1910d2a828cecae79381cd7d3150ea424110eb1de7317a24a93aae9fc5b` |
| All bytes in that file outside the two authorized characterization method bodies | Identical after masking only those two bodies |

The two changed bodies retain their fixtures and concrete PCM-position/counter assertions. Their comments
explain the policy change, and the revised results follow from it:

- **`ThirtyMillisecondPause...`:** 30 ms exceeds `GapJitterMs=10`, so the pause is inserted. A remains at
  +300 ms, B moves from the old +310 ms expectation to its actual stamp at **+340 ms**, C moves to **+350 ms**,
  and `GapSilenceFrames` must equal **30 ms**. This requires the newly intended pause, rather than relaxing
  the test to accept arbitrary output.
- **`GapAndClockDeadbands...`:** the 40 ms source pause is now laid out in full. The remaining 30 ms clock
  shift is within the 40 ms tolerance, so B starts at **+350 ms** against a +380 ms deadline: **30 ms early**,
  replacing the former 70 ms result. The test checks the exact first-B frame, exact remaining error, the
  unchanged initial-only correction count, and **40 ms** of gap silence. This is consistent with the new
  policy. It does not weaken C1/C2 or the earlier rebase protections.

The implementation-owned `AudioSyncTests` also changes its small-gap accumulation fixture from 20 ms to 4 ms
gaps to exercise the new 10 ms band, and adds a positive pending-gap/clock-shift test. These changes do not
alter the review-authored regression files above. Test names/comments that still mention the old early-phase
behavior are historical identifiers; the updated assertions and explanatory comments are clear.

### C1/C2 verification and requested edge checks

- **C1: fixed.** `PcmQueue.cs:179` no longer clamps logical shift before reconciling expired source-gap lead.
  The unchanged five-second-pause test preserves the resumed packet and reports zero unintended drops.
  Added five-second and sixty-second cases also preserve both resumed packets and stay within the queue's
  configured count bound. No multi-second buffer is needed to reconcile an elapsed pause.
- **C2: fixed.** `SessionPlayback.cs:91-104` carries the exact end phase as whole milliseconds plus a normalized
  remainder. It moves that phase toward incoming stamps by whole milliseconds, then adds the full decoded
  frame duration. A sub-ms timestamp-rounding difference is no longer repeatedly integrated as a +1 ms source
  gap. The unchanged 200 x 256-frame/44.1 kHz regression passes with no phantom gap silence or drops.
- **Remainder and wraparound:** added tests use varying blocks of 1, 127, 256, 441 and 512 frames at 11025,
  44100 and 88200 Hz, beginning just before u32 stamp wrap. Every contiguous packet reports zero gap, the
  delay stays 400 ms, no source/clock correction follows the initial hold, and the stamps actually cross the
  wrap. Signed subtraction and unchecked u32 updates behave correctly for these normal-sized differences.
  A playback restart from 44.1 to 48 kHz also resets the fractional phase correctly.
- **Logical versus physical bounds:** positive insertion is limited before its `int` conversion; negative
  physical drops are bounded by the actual queued/current-packet frame counts. Reconciliation of logical
  lead itself is not limited to the ring capacity. `EnsureCapacity` still caps backing storage at 2.2 seconds;
  the new long-pause tests check the queue count at runtime. No new integer/array-bound defect was confirmed
  under the renderer's accepted formats.
- **Existing protections:** all 47 pre-existing audio-sync cases pass, including A1-A3, B1-B2, C1-C2 and the
  authorized characterization updates. `Transfer` locking, coherent `MmClockBase` publication, and the
  allocation/idle-wake behavior were not changed by this commit. Warm allocation tests pass.

The 10 ms signed gap band addresses the previous 40+40 stacking examples and preserves the bounded +/-3 ms
jitter test. The remembered QEMU timer argument is still not a guest measurement; the timing-budget choice
remains marked open in Proposed 0030. More importantly, the current-packet calculation is not sufficient to
establish the stated 40 ms budget for every packet still held across a rebase, as the following cases show.

### D1 - P2: A current packet's signed gap can hide excessive error in earlier held audio

Location: `src/Spice.Media/PcmQueue.cs:179-190`, particularly the `shift + pending` gate at `:185` and `:190`.

The combined error describes the **newest** packet's pending-gap phase. Earlier packets in the ring can have
different phases. Opposite-sign jitter on the new packet can keep its combined error within 40 ms and suppress
a clock correction that older held audio needs. Video's earlier queued deadlines still move with the new
guest base. Thus the claim that synchronized audio stays within 40 ms of its rebased stamp does not hold for
all audio already waiting in the ring.

Two deterministic 48 kHz, 10 ms PCM cases use the actual session rebase, stamp/gap tracking and queue:

1. **A 50 ms step hidden by a -10 ms overlap.** A stamped 1300 is queued at +300 ms with guest clock 1000.
   Rebase the guest to 950: A now belongs at +350 ms. B stamped 1300 contributes a -10 ms gap. The queue sees
   `shift=50`, `pending=-10`, combined error **40**, and makes no correction. B starts at +310 ms, exactly
   40 ms early, but A stays at **+300 ms: 50 ms early**, outside the budget. A's first frame is **14400**;
   the accepted +310..+390 ms interval would start at **14880**.
2. **Even a sub-40 ms shift can violate an older packet's budget.** Queue A stamped 1300 and B stamped 1313.
   B's +3 ms jitter remains pending, so it starts at +310 ms. Rebase the clock backwards 39 ms to 961, then
   receive C stamped 1317 (absolute -3 ms jitter, gap -6 ms). Current pending lead is -3 ms, so the gate sees
   **39-3=36 ms** and leaves everything alone. C is 36 ms early, but B's new deadline is +352 ms and it still
   starts at **+310 ms: 42 ms early**. This case uses only +/-3 ms stamp jitter; checking `abs(shift)>40`
   separately would not fix it either.

These are within-range, non-late packet deadlines, with no WASAPI latency estimate or timer quantization in
the test. The assertions allow the full configured 40 ms tolerance; they do not demand sample-perfect sync.
Preserve enough information about earlier held phases, or use a correction criterion conservative enough
to cover them. A latest-packet error alone cannot prove the bound for the whole held timeline.

Failing regressions, in the new `tests/Spice.Replay.Tests/AudioSyncThirdReviewTests.cs`:

- `NegativeStampJitterCannotHideAnOutOfBudgetRebaseForHeldAudio`
- `EarlierPositiveJitterStillSharesTheBudgetAfterAClockRebase`

### Third re-review validation

| Check, inside this worktree | Result |
|---|---|
| Existing `--filter FullyQualifiedName~AudioSync`, before adding new cases | **47 passed** |
| New third-review cases | **6 passed, 2 failed**; failures are the two D1 examples |
| Full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` | **183 passed, 2 failed, 0 skipped**; only the new D1 cases fail |
| `tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | **24 passed** |
| `tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | **13 passed** |

Added only this section and `AudioSyncThirdReviewTests.cs`, left uncommitted. No production code, earlier
review tests, authorized policy expectations or golden images were edited. No viewer/UI/VM access, viewer
test project, artificial CPU load, commits or `/daybreak` were used. The re-review does not incorporate the
other worktree's live results or claim physical lip-sync validation. The new D1 tests remain failing until
the held-audio budget issue is fixed.

## Fourth re-review

Codex, 2026-10-30. Reviewed `72eded6` against `5fd8ccf`, including the D1 gate, F1 drain/continuation,
F2 mode switch, decision 0030 changes and the existing live evidence in
[the verification record](../verification/2026-10-30-clock-av-sync.md). This review ran only window-free tests;
it did not repeat or interfere with live testing.

**Verdict: changes requested. The implementer is right about my two over-constrained D1 assertions. After
correcting those assertions, both original cases pass. Five further P2 cases remain reproducible: a residual
held-packet budget violation, retiming the previous stream's tail, ignoring pre-roll drain completion,
continuing onto a retiring worker, and consuming fresh unprimed audio during a mode switch.**

### D1: original cases fixed; test-owner correction

The two exact newest-packet positions in my third-review tests were wrong. In-order playback cannot keep a
nonempty older packet at a later position while pinning the packet immediately after it at its former position:

- In `NegativeStampJitterCannotHideAnOutOfBudgetRebaseForHeldAudio`, A's allowed first frame starts at +310 ms.
  Keeping the full 10 ms A packet requires B no earlier than +320 ms, conflicting with my exact B +310 ms
  assertion. The implementation's **A +350 / B +360 ms** is valid: their rebased deadlines are both +350 ms,
  so the errors are 0 and +10 ms.
- In `EarlierPositiveJitterStillSharesTheBudgetAfterAClockRebase`, B's allowed range begins at +312 ms.
  Keeping its full 10 ms requires C no earlier than +322 ms, conflicting with my exact C +320 ms assertion.
  The implementation's **A +339 / B +349 / C +359 ms** is valid against deadlines +339 / +352 / +356 ms:
  errors 0 / -3 / +3 ms.

As authorized, changed only these two method bodies in `AudioSyncThirdReviewTests.cs`: replaced the exact
B/C pins with the configured +/-40 ms deadline ranges, retained the original older-packet range assertions,
and added the missing A deadline range in the second case. The comments explain the correction. These tests
still reject the original D1 outputs (A +300 against +350, and B +310 against +352); they now accept a valid
whole-timeline correction. The other review-authored tests are unchanged. The commit itself changes none of
the earlier review-authored test files.

The new held-audio gate (`PcmQueue.cs:267-268`) fixes these original cases: 50 and 39 ms shifts exceed the
30 ms held threshold. It leaves the queue-empty rule intact. Its general proof nevertheless assumes that
every retained packet has at most 10 ms of residual source phase; the partial-layout branch can violate that
assumption, as E1 shows.

### E1 - P2: A partially laid source gap defeats the new held-packet budget

Locations: `src/Spice.Media/PcmQueue.cs:261-268`, `:300-304`.

Source-gap insertion is capped by the current packet's target, so a real gap can remain partly pending with
more than `GapJitterMs` of phase. That packet stays in the ring with that wider error. A subsequent 30 ms
clock shift then passes the new gate even though this older packet is outside the promised 40 ms budget.

Deterministic 10 ms packets, with all delays inside the supported scheduling interval:

| Step | Clock/stamp | Queue result |
|---|---|---|
| A | clock 1000, stamp 1300, delay 300 | A at +300 ms |
| B after a real 60 ms pause | clock 1030, stamp 1370, delay 340, gap +60 | shift -30 is allowed; only 30 ms of the gap is laid; B at +340, remaining lead 30 ms |
| C, contiguous with B, after a rebase | clock 970, stamp 1380, delay 410 | shift +30 is allowed; remaining gap is laid before C; A/B/C at +300/+340/+380 ms |

Final deadlines are **+330/+400/+410 ms**. A and C are 30 ms early, but **B is 60 ms early**. The new test
checks every packet against its deadline range; there is no exact-position pin on the newest packet. It
fails on B (actual 340, accepted 360..440). The 30 ms gate is sufficient only when the retained phase envelope
really is bounded by 10 ms. Preserve that invariant across partial layout, or account for the wider envelope.

Failing test: `AudioSyncFourthReviewTests.PartiallyLaidSourceGapCannotInvalidateTheHeldPacketBudgetAfterAnotherRebase`.

### E2 - P2: The second packet after continuation moves the previous stream's tail

Locations: `src/Spice.Media/PcmQueue.cs:252-268`.

`TryContinue` marks only the first new packet as a fresh timeline. If that packet's deadline precedes the
old held tail, it is appended after the tail, as the new policy explicitly permits. However, the next new
packet resumes ordinary shift calculation against the combined queue; the deliberately late first packet
then looks like a clock error and causes the old tail to move.

Reproduction: old A is due at +300 ms and lasts 10 ms. STOP then same-format START; new B is due at +100 ms
and is correctly appended at +310 ms. The next contiguous C is due at +110 ms. Its shift is -210 ms, so the
implementation drops 210 ms of the old leading hold and moves **A to +90 ms**, 210 ms before A's unchanged
deadline. No guest-clock rebase occurred. The regression constrains only the old A deadline (+260..+340);
it leaves the late/new-stream policy open. A stream boundary must continue protecting the old schedule
beyond the first new packet, until that tail is no longer held.

Failing test: `AudioSyncFourthReviewTests.SecondPacketOfContinuedStreamCannotRetimeThePreviousStreamsTail`.

### E3 - P2: Pre-roll ignores drain completion and starts a client for a closed queue

Locations: `src/SpiceViewer/Audio/AudioOutput.cs:251-252`, `:298`; compare the correct handling at `:266`.

If the default device changes while the last drained samples are in the old endpoint, the software queue is
already empty but still draining. The replacement endpoint has zero padding. Its pre-roll `Fill` therefore
calls `TryFinishDrain`, closes the queue and returns true. `Play` ignores that result and starts the new
client anyway. Later fills see a closed, non-draining queue and resume padding silence; the worker no longer
finishes naturally after STOP. This also keeps endpoint events active after the stream has ended.

The test executes the real renderer with a fake endpoint: drive the queued tail into the old device, signal
device replacement, and observe whether the worker retires before a second client starts. Expected
`(retired=true, starts=1)`; actual **`(false, 2)`**. Honor the pre-roll result before calling `Start`.

Failing test: `AudioOutputLifecycleReviewTests.ReopenedEmptyDrainRetiresBeforeStartingAnotherClient`.

### E4 - P2: Same-format START can hand off to a worker that has already chosen to exit

Locations: `src/SpiceViewer/Audio/AudioOutput.cs:64`, `:175`, `:198-200`.

Normal `TryFinishDrain` versus `TryContinue` is atomic, but abnormal render retirement does not use that
handoff. After an unrecoverable endpoint failure, `Play` has released its client and the render loop has
chosen to exit. Until its later `Finish` closes the queue, the queue remains active and draining. A network
START in this interval successfully calls `TryContinue`, returns without opening a new worker/device, and
accepts new PCM. The old worker then closes that newly continued queue in `Finish`; the new sound is lost.

The actual renderer regression injects an unrecoverable padding-read failure and pauses at its existing
"output closed" log, before `Finish`. A same-format START and write complete; releasing the worker's exit
then leaves the accepted queue empty. The test also permits START to wait/join a retiring worker, rather
than requiring it to return during the pause. Worker retirement needs an ownership/state handoff for all
exit paths, not just normal empty-drain completion. Checking `Thread.IsAlive` is insufficient here. Taking
the lifecycle lock in `Finish` without restructuring would deadlock with `StopCore`, which holds that lock
while joining the worker.

Failing test: `AudioOutputLifecycleReviewTests.StartDuringFailedWorkerRetirementCannotContinueOntoTheDyingWorker`.

### E5 - P2: An in-flight clock-mode fill consumes newly unprimed off-mode audio

Locations: `src/Spice.Media/PcmQueue.cs:222-223`; `src/SpiceViewer/Audio/AudioOutput.cs:302-313`.

The sequential F2 fix correctly flushes the synchronized hold and clears `_primed`. However, a render fill
can already have selected 40 ms using `ReadyFrames` and be in `GetBuffer` when the network write switches
to unsynchronized mode. That write flushes the old hold and appends a fresh 10 ms packet, which should wait
for the 20 ms prime. The outstanding `Transfer` uses its stale 40 ms selection, consumes the fresh packet
without rechecking readiness, and fills the other 30 ms with silence. The switch therefore bypasses its
new prime and publishes a 40 ms device queue containing only 10 ms of fresh audio.

The real-renderer test pauses the fake `GetBuffer` after selection, performs the off-mode write, then
releases the fill. Expected fresh queued samples/nonzero submitted PCM: `(960, false)`; actual **`(0, true)`**.
It permits an implementation to serialize the switch behind the outstanding fill; that order also passes
because the selected old 40 ms is leading silence. Invalidate or serialize stale readiness across the mode
transition. This is a confirmed transaction race, not a claim to have reproduced the live record's measured
steady latency on hardware.

Failing test: `AudioOutputLifecycleReviewTests.SwitchToOffDuringGetBufferMustNotConsumeUnprimedNewAudio`.

### Drain, restart and disposal checks

- `TryContinue` and normal `TryFinishDrain` share the queue lock: the competing calls have exactly one winner.
  The losing continuation follows the existing restart/join path; a newly reset queue is not closed by an old
  normal-drain worker. This atomicity does not cover E4's abnormal exit.
- The real-renderer passing control confirms same-format continuation keeps the existing thread/client;
  different-format START joins the old worker, reopens with the new format, and logs the discarded queue.
- Immediate Stop/Dispose closes the queue and joins the render worker instead of waiting for media to drain.
  Repeated close (including `Finish`) counts a held sample only once. Disposed instances ignore later
  Start/EndOfStream/Write. `DiscardedSamples` counts queued samples, including queued silence; it does not
  claim to count the hardware buffer, which has already left the queue.
- Source inspection confirms disconnect still calls immediate `Stop`, rather than `EndOfStream`, with the
  existing ended-session guards around START. No additional normal-path lifecycle lock inversion was found.
- Sequential F2 flush/re-prime and the existing allocation/idle tests pass. No new timer or polling loop was
  introduced; E3 nevertheless prevents natural worker retirement in its replacement-device case.

### Fourth re-review validation

The Replay project links the unchanged `AudioOutput.cs` source into its window-free test assembly, using
test-only COM-shaped stand-ins in `AudioOutputFakeBackend.cs`. This executes the production managed
Start/Stop/Dispose/render-loop/Fill ordering with controlled endpoint results and event boundaries. It does
not reference or build the viewer project, call real COM/audio APIs, open a window/device, or exercise the
real device-notification callback ABI. Tests restore their process-local log hooks and join their workers.
Queue-only cases use the public `PcmQueue` API. The backend is not evidence about hardware latency or actual
endpoint notification behavior.

| Check, inside this worktree | Result |
|---|---|
| Existing audio-sync tests after the authorized D1 assertion correction, before new tests | **60 passed** |
| New fourth-review queue tests | **3 passed, 2 failed** (E1/E2) |
| New actual-renderer lifecycle tests with fake endpoints | **2 passed, 3 failed** (E3/E4/E5) |
| Full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` | **195 passed, 5 failed, 0 skipped**, 200 total; only the five new regressions fail |
| `tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | **24 passed** |
| `tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | **13 passed** |

The live record establishes the original F1/F2 symptoms on its tested build; it did not exercise clock
rebases or device changes, and its results do not validate this commit's fixes. No new live validation is
claimed here. Added this section, the test-only backend/lifecycle/queue files and Replay source-link entry,
and corrected the two authorized D1 test bodies. All changes are uncommitted; production code and golden
images are untouched. No viewer/UI/VM access, viewer test project, artificial CPU load, commits or
`/daybreak` were used. The five new regressions remain failing for the implementer.

## Fifth re-review

Codex, 2026-10-30. Reviewed `c003d29` against `ecd1f05`, including the revised ADR 0030 and the renderer source
linked into the window-free Replay tests. **Verdict: E1-E5 are fixed for their reported cases; no new P1/P2
issue confirmed. The remaining E4 terminal-device-failure race is acceptable as a documented limitation,
not a claim of atomic START/failure recovery.** No live-test result is inferred from these checks.

### E1-E5 verification

| Finding | Result and evidence |
|---|---|
| **E1: partially laid source gap** | **Fixed.** `PcmQueue.cs:315-319` lays out the entire positive lead beyond the jitter band, rather than capping it at the current deadline. The unchanged regression now plays A/B/C at +300/+370/+380 ms against +330/+400/+410 deadlines: each is 30 ms early. B no longer retains the extra 30 ms phase that defeated the held-audio gate. The earlier long-pause, fractional-stamp, wrap and D1 cases also pass. |
| **E2: second continued packet retimes old tail** | **Fixed.** `TryContinue` snapshots the queued sample count (`PcmQueue.cs:174`); all writes stay in the fresh-timeline branch while any of those samples remain (`:265`). Both dequeue and discard subtract samples from the protected prefix (`:437`, `:455`); close/reset clear it. The unchanged regression preserves old A at +300 ms. New mono 44.1 kHz and stereo 48 kHz controls leave exactly one old frame, verify it survives another write, consume it, then verify correction resumes for the new stream. |
| **E3: pre-roll ignores drain completion** | **Fixed.** `AudioOutput.cs:265` returns `Exit.Stop` before client Start when pre-roll finishes the drain. The unchanged real-renderer/fake-endpoint regression now retires the worker with one client start, rather than starting a second client on a closed queue. |
| **E4: START accepted after worker retirement decision** | **Fixed at queue retirement, with the residual discussed below.** Fatal/retry-exhausted failure, outer open/setup failure and COM-initialization failure close the queue before their terminal log/cleanup (`AudioOutput.cs:143`, `:176`, `:188`). The unchanged regression paused at the terminal "output closed" log now makes START join/reopen; the old worker cannot close the newly reset queue. |
| **E5: stale fill consumes freshly unprimed off-mode audio** | **Fixed.** Switching off bumps the epoch under the queue lock (`PcmQueue.cs:236`). A stale Transfer clears its destination and takes zero samples (`:407-411`), leaving the new packet queued. The unchanged renderer regression passes. New 44.1/48 kHz controls verify that rejection does not strand later audio: after the second packet completes the 20 ms prime, a new ReadyFrames/Transfer plays both packets in order, returns the actual sample count and clears the unused destination. |

All six earlier review-authored regression files, including the corrected third-review tests and the
fourth-review queue/lifecycle tests, are byte-unchanged across `ecd1f05..c003d29`. They were not edited by
this re-review. The implementation adds two tests without weakening earlier assertions.

### E4 residual: acceptable failure semantics, with a documentation qualification

The handoff is linearized by `queue.Close`, not by the instant WASAPI fails. A same-format START that wins
`TryContinue` immediately before that close can still return successfully and then have its stream discarded.
This is not fully eliminated by the change, and a passing terminal-log test does not prove otherwise.

I do **not** classify that residual as P1/P2 for this change. It requires a simultaneous START/continuation and
a **terminal** endpoint/open failure (or exhausted recovery), where the existing active stream would also be
lost. Recoverable default-device/resource invalidation still follows the reopen path. After retirement, a
subsequent START reliably joins the old worker and opens again; there is no new permanent state corruption,
deadlock or loss of a later START. Retrying an active stream after terminal failure would be a broader recovery
policy than the current "next START reopens" behavior. Accepting this documented failure boundary is reasonable
without adding a worker-state handshake solely for this race.

**Lower-severity qualification:** the residual is not guaranteed to be sub-ms. `Play` logs its WASAPI error
and runs client Stop/COM releases before returning to the terminal `Retire` call (`AudioOutput.cs:283-298`).
Those operations, logging sinks and thread scheduling have no sub-ms bound. ADR 0030's "before anything else"
wording therefore describes the terminal render-loop retirement, not the earliest recognition of failure.
Describe it as START concurrent with an in-progress terminal failure; do not promise a measured duration.
No failing test was added for this accepted limitation.

### Additional source and regression checks

- **Epoch ownership:** the production renderer has one consumer and one outstanding ReadyFrames/Transfer pair.
  Both epoch accesses are locked; a successful fresh readiness check refreshes the epoch. The early return while
  priming cannot consume fresh audio because the chosen span is empty. Reset is reached only after joining the
  old worker, so it cannot accidentally validate an old worker's outstanding fill. No epoch object or hot-path
  allocation was added.
- **Protected samples:** the counter uses interleaved samples consistently with Count, dequeue and discard.
  While it is positive, the fresh branch suppresses prepended clock correction, so no new uncounted prefix is
  placed ahead of the protected samples. Switching off discards the whole queue and clears the count through
  the same discard path. Repeated continuation protects the entire then-held prefix. Device-held audio is
  already outside the ring and cannot be moved by later ring corrections.
- **Exit coverage and locks:** normal drain closes through TryFinishDrain; explicit Stop/disconnect/Dispose
  closes through StopCore before signalling and joining; failure exits retire as listed above; Finish closes
  again idempotently. Retire takes only the queue lock, avoiding the lifecycle-lock/Join deadlock. No new exit
  path leaving an open queue after worker retirement was found. Existing continuation, format restart, dispose
  and discard-counting controls all pass.
- **Transfer return value:** the renderer uses the returned **sample** count for the silent flag, tail clearing
  and gain (`AudioOutput.cs:327-334`). New fake-endpoint tests change the queue after a full 40 ms selection:
  with 10 ms actually available, all stereo samples receive half gain and the other 30 ms is zero; with none
  available, the full buffer is marked silent. Both publish the full submitted device depth. This checks partial
  and zero-take behavior beyond the epoch-mismatch case, without calling a real audio API.

### Fifth re-review validation

| Window-free check in this worktree | Result |
|---|---|
| Existing full Replay suite before new checks | **202 passed, 0 failed, 0 skipped**, including all five former E regressions |
| Added fifth-review controls | **6 passed**: two rates/formats each for epoch recovery and protected-prefix release, plus two renderer partial/empty-take cases |
| Full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` after additions | **208 passed, 0 failed, 0 skipped** |
| `tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | **24 passed** |
| `tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | **13 passed** |
| `git diff --check` | Clean |

Added `AudioSyncFifthReviewTests.cs`, `AudioOutputFifthReviewTests.cs`, a released-flags observation in the
test-only fake backend, and this section. All remain uncommitted. No new failing regression was warranted;
no production code, ADR, earlier assertions or golden images were edited. Tests use the existing linked
renderer/fake COM backend, not the viewer project or a real endpoint. No viewer/UI/VM access, artificial CPU
load, commits or `/daybreak` were used. Native callback ABI, physical device behavior and the other worktree's
live re-check are outside this review's evidence.
