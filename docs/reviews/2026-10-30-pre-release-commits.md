# Review of the pre-release commits

**Commit names.** These were six local commits made before the repository was published; they were then squashed
into its initial commit, so their hashes no longer exist. This record and
[2026-10-30-native-codecs.md](2026-10-30-native-codecs.md) name them by topic:

| Name | Content |
|---|---|
| `v0` | the first complete viewer (the root of that history) |
| `review-fixes` | fixes for the previous Codex review round ([task12-regressions.md](task12-regressions.md)) |
| `principles` | [../design/principles.md](../design/principles.md) |
| `native-build` | x86-64-v3, trimmed exports with LTO, the LZ4 decoder |
| `viewer-options` | video pacing, fast JPEG decoding, LZ4 image compression |
| `native-codecs` | native review fixes N1 to N5, differential verification, codec speedups |

2026-10-30. Reviewed the five commits from `review-fixes` to `native-codecs`, each against its parent. `v0` itself is
excluded. Production code, existing tests, native binaries and golden images were not edited. Only this report
and the review index were written.

## Scope, results and validation

| Commit | Scope reviewed | Result |
|---|---|---|
| `review-fixes` | Writer wake/token wrap, canvas alpha and mask pooling, playback EOF, presenter occlusion, settings serialization and regression tests | R1 (P2); its uncommitted correction is correct by inspection |
| `principles` | Human engineering principles and their documentation links | No actionable finding |
| `native-build` | ISA/build/link/export changes, native LZ4 bridge, smoke checks, native verification and packaging/licensing changes | No actionable finding from source inspection |
| `viewer-options` | Pacing, timers/queues/pooling, stream reports, JPEG/LZ4 decoding, capabilities, settings/menu changes and tests | R2 (P1), R3 (P2); the uncommitted R2 correction restores the byte bounds but has follow-up W1 (P2) |
| `native-codecs` | Native bounds/allocation fixes, GLZ reuse, LZ/GLZ copy optimizations, QUIC optimizations, differential/fault-injection/benchmark harnesses and managed LZ caller | R4 (P1, mixed DLL versions), R5 (P2) |

Read the binding specification/principles, relevant decision records and existing review records. Compared the
stream-report behavior with the local `refs/spice-common` and `refs/spice-gtk` sources. The reference paths below
refer to those local checkouts, not a newly fetched upstream revision.

This is a source review. No builds, test suites, native differential/ASan runs, viewer/GPU checks, live guests,
network services or credentials were used. Failure sequences and byte counts below are derived from the code;
they are not measured runtime reproductions. Existing passing-test claims in other documents were not rerun.
In particular, this review does not verify ARM64 builds or the complete downloaded JPEG/Opus implementations.

The working tree became modified during review. The existing changes to `src/SpiceViewer/Presenter.cs` and
`src/Spice.Session/SessionDisplay.cs` were preserved and rechecked separately below. Additional native LZ/harness
changes appeared while the report was being written; the R4/R5 notes distinguish their apparent effect from the
committed defects. Those concurrent native/harness edits were not comprehensively reviewed or tested. Unless
marked **working tree**, locations describe the committed source at the named commit; these may differ from
current line numbers.

## Findings

### R2 — P1: Charge pacing buffers by capacity and bound the spare pool's bytes

- **Introducing commit:** `viewer-options`.
- **Location:** `src/Spice.Session/SessionDisplay.cs:354-364`, `:367-374`, `:463-483`.
- **Committed status:** present through `native-codecs`; an uncommitted correction exists.

`Enqueue` charges only the compressed payload length, although `RentFrameBuffer` rounds allocations to powers
of two and can lend an arbitrarily larger spare to a tiny frame. The spare pool has a 32-object limit but no byte
limit and deliberately retains larger arrays. Consequently the advertised 64 MiB queued-data limit does not
bound the allocated memory kept in either the queues or the pool. This is a resource-exhaustion path from server
stream data, before JPEG validity is checked.

**Failure scenario:** in clock mode, let a frame slightly larger than 32 MiB reach its deadline; its 64 MiB array
is returned to the pool even if JPEG decoding rejects the payload. A tiny frame on another stream, with a later
deadline, borrows that array but is charged only its tiny payload length. While that tiny frame is waiting,
another large frame can pass the payload-byte budget and allocate another 64 MiB array. Repeating this pattern
grows retained storage far beyond 64 MiB. Even a full pool of 32 such arrays alone represents 2 GiB; queued arrays
are additional storage. No actual memory exhaustion was attempted.

**Required change:** account for the full capacity of live queued arrays and maintain a separate byte ceiling
for spare arrays, including every rent, return, eviction and clear. Retaining a bounded pool is appropriate;
limiting only its object count is insufficient.

**Working-tree recheck:** the current changes correctly add/subtract `buffer.Length` / `f.Data.Length` for the
queue and track `_pooledBytes` on rent, return, eviction and clear. They enforce separate 64 MiB live-queue and
64 MiB spare-pool limits by inspection. This means up to 128 MiB of retained queue-plus-pool storage, with a
temporary newly rented array possible during admission; it is not a 64 MiB combined/process-memory guarantee.
The original accounting defect is corrected, but admission of an oversized spare has the separate W1 problem.

### R4 — P1: Version the tightened LZ output layout before shrinking the managed destination

- **Introducing commit:** `native-codecs`.
- **Location:** `src/Spice.Native/Decoders.cs:68-78`, `:83`; native contract change at
  `native/spicecodec/codec_bridge.c:201-228`.
- **Committed status:** conditional on loading an older `spicecodec.dll`; concurrent uncommitted mitigation noted below.

The managed LZ decoder now allocates exactly `width * height * 4` scratch bytes and assumes tightly packed rows.
Earlier DLLs export the same `sc_lz_begin` / `sc_lz_decode` names but write padded palette rows. Neither native
loading nor these calls establish which output layout the loaded DLL implements. The intentional N4 layout
change is documented in decision 0029, but that does not make a mismatched DLL safe to call.

**Failure scenario:** run the new managed code with the DLL from `viewer-options`, using a fresh LZ decoder and a valid
9x1 PLT1 image. The managed scratch array has 36 bytes. The old native decoder writes 16 padded pixels, or 64
bytes, through its pinned pointer: a 28-byte overwrite beyond the managed array. With multiple rows, the changed
row-stride assumption also produces incorrect pixels. This can arise from replacing the codec DLL or from stale
native outputs: `native/NativeDlls.targets` copies existing DLLs without checking their source/layout version.
This finding does not claim a matched new viewer/new DLL pair has that overflow.

**Required change:** expose/probe a layout/ABI version or use a new tight-output entry point, and reject an
incompatible DLL before passing a tight buffer. If old DLL support is retained, use their padded allocation and
row stride explicitly. Keep the corrected tight-output implementation for compatible callers.

**Concurrent working-tree note:** the latest observed managed edit reserves rows rounded up to eight pixels and
passes an output capacity to an amended `sc_lz_decode` signature. The larger allocation avoids the specific
old-DLL overwrite above, but does not identify the DLL's row layout: the caller still copies with a tight source
stride. For example, a 9x2 PLT1 image from the old DLL has its second row at byte 64, while this caller reads it
at byte 36. The same exported name also now has different argument counts across versions. This is a mitigation
of the overwrite, not verified ABI/layout compatibility; versioned dispatch or safe rejection remains needed.

### R1 — P2: Invert the occlusion guard so visibility resumes pending rendering

- **Introducing commit:** `review-fixes`.
- **Location:** `src/SpiceViewer/Presenter.cs:323`; helper at `:821-830`.
- **Committed status:** present through `native-codecs`; corrected in the current working tree.

The committed loop uses `if (_occluded && !StillOccluded()) continue;`. `StillOccluded` returns true while the
window remains occluded and returns false after clearing `_occluded` when it becomes visible. The guard acts
on the opposite result from the intended one.

**Failure scenario:** after a real present returns `DXGI_STATUS_OCCLUDED`, subsequent guest updates still pass
the guard, upload and attempt real presents while hidden. When the occlusion-status event finally wakes the
worker for a now-visible window, the helper clears `_occluded`, but the negated guard consumes that wake and
skips the pending full frame. A static guest can remain stale until another update/layout wake arrives.

**Working-tree recheck:** `if (_occluded && StillOccluded()) continue;` is the correct correction by inspection:
hidden windows skip rendering; a successful visibility test falls through to rendering on the same wake. The
previous occluded present already set `_fullPending`, so that frame is available to render. This is verification
of the Boolean/control flow, not a live DXGI notification test. The existing headless occlusion test is skipped.

### R3 — P2: Keep deferred playback drops out of a later stream-report arrival window

- **Introducing commit:** `viewer-options`.
- **Location:** `src/Spice.Session/SessionDisplay.cs:277-281`, `:395-399`, `:529-534`.
- **Status:** unresolved by the current occlusion/pool changes.

`OnStreamData` counts received frames and sends/resets reports immediately, but `PumpStreams` later increments
the same `s.Drops` when an already reported queued frame is superseded. The next arrival window therefore
reports losses from previous windows, potentially with more drops than received frames. The wire definition
calls `num_drops` a subset of the reported frames dropped for late arrival
(`refs/spice-common/spice.proto:1007-1020`).

**Exact failure sequence:** use clock pacing, a guest clock of 1000 at local time 0, a valid MJPEG stream and an
active report with `MaxWindow = 1`.

1. Receive four frames at local time 0 with stamps 1400, 1433, 1466 and 1499. All have positive arrival margin,
   so they queue for local deadlines 400, 433, 466 and 499. Each arrival immediately sends a report containing
   `num_frames = 1`, `num_drops = 0`, then resets both counters.
2. The pacing callback is delayed until local time 500. All four frames are due. `PumpStreams` discards the first
   three as superseded and draws the fourth. Its three `s.Drops++` operations leave `Frames = 0`, `Drops = 3`.
3. At local time 500 receive another frame stamped 1532. The guest clock is now 1500, so this frame is early and
   queues for 532. The one-frame report is sent immediately with `start = end = 1532`, `num_frames = 1`,
   `num_drops = 3`, although all three drops belong to the earlier, already reported frames.

Report reactivation while older frames remain queued can likewise charge old playback drops to the new report
ID. These inconsistent counters can mislead server adaptive-streaming decisions; no particular server bitrate
or arithmetic failure was tested or asserted.

**What spice-gtk does:** `display_handle_stream_data` computes the frame's arrival margin and, when adaptive
streaming is enabled, calls `display_update_stream_report` once per received frame with `margin_report`
(`refs/spice-gtk/src/channel-display.c:1773-1820`). The report function increments `report_num_frames` and
increments `report_num_drops` only for a negative arrival margin. It sends on window size, elapsed timeout or
three consecutive arrival-late frames, then resets those report counters (`:1533-1586`). Later MJPEG playback
lateness calls `stream_dropped_frame_on_playback` (`refs/spice-gtk/src/channel-display-mjpeg.c:178-194`), which
increments the separate `num_drops_on_playback` statistic (`refs/spice-gtk/src/channel-display.c:1485-1489`),
not `report_num_drops`. Thus playback loss does not retroactively modify an arrival report window.

**Required change:** separate lifetime/playback/decode/supersession drop statistics from arrival-window report
counters. Follow the wire contract and reference distinction rather than adding delayed losses to the next
report. Add a deterministic report-output regression for the sequence above; the existing pacing tests inspect
internal drop totals but do not cover these deferred drops across serialized reports.

### R5 — P2: Retain reusable palette scratch above 4 MiB on the warmed LZ path

- **Introducing commit:** `native-codecs`.
- **Location:** `native/spicecodec/codec_bridge.c:209-214`, `:226-228`; threshold at `:117`.
- **Committed status:** present; concurrent uncommitted viewer-path correction noted below.

The N4 fix allocates native scratch for padded palette rows but frees it after every successful decode when its
capacity exceeds 4 MiB. Repeating an identical valid image therefore performs `realloc(NULL, need)` and `free`
on every display draw even after warm-up. This reintroduces the native hot-path allocation that the binding
energy rules and decision 0009 prohibit. The allocation harness exercises only a 9x5 padded palette image, so
its warmed small-image check does not cover this branch.

**Failure scenario:** repeatedly decode uncached 1366x768 PLT1 images. The packed stride is 171 bytes, or 1368
padded output pixels per row. Native scratch needs `1368 * 768 * 4 = 4,202,496` bytes, exceeding the 4,194,304-byte
retention threshold. Every successful decode frees the scratch and the next allocates it again. This requires
neither malformed input nor extreme image dimensions.

**Required change:** reuse sufficiently sized scratch on this path within a deliberate memory budget, or implement
the tight-output conversion without per-image heap allocation. Decision 0029 mentions the repeated-allocation
cost, but does not record explicit human approval to depart from the hot-path MUST rule. Extend native allocation
coverage beyond the threshold; managed GC counters alone cannot verify this.

**Concurrent working-tree note:** the latest observed native edit decodes into a padded caller buffer and
compacts rows in place when the supplied capacity permits it; the managed caller now reserves that padding.
Together these avoid native scratch allocation for the matched viewer/DLL scenario above by inspection.
Tight-capacity C callers still take the threshold/free path. This note is not a build, allocation-test or
differential-verification result for the in-progress edits.

## Additional finding in the uncommitted pool correction

### W1 — P2: Do not drop a small frame solely because a spare buffer is too large

- **Commit:** none; current uncommitted correction to R2, based on `native-codecs`.
- **Location:** working-tree `src/Spice.Session/SessionDisplay.cs:358-362`, `:463-474`.
- **Verdict on the fix:** correct byte accounting and byte ceilings, but incomplete frame admission.

`RentFrameBuffer` still selects any sufficiently large spare before `Enqueue` tests the capacity budget. It does
not limit the rental to capacity that can fit alongside other streams. A cache choice can therefore cause drops
even when the actual incoming frame has ample space.

**Failure scenario:** a 64 MiB spare remains after an earlier large frame was drained. Stream A then queues a
tiny early frame, borrowing that spare and consuming the entire live-queue capacity budget despite needing
only a few hundred payload bytes. Stream B receives another tiny early frame and allocates a 4 KiB buffer.
Since `64 MiB + 4 KiB > MaxQueuedBytes`, B's empty queue cannot drop anything to make room, so B's frame is
dropped. Two appropriately sized 4 KiB buffers would fit easily. Repeated B arrivals can repeat this avoidable
drop while A remains queued. This can happen in clock mode even after the earlier large frame was rejected as
an invalid JPEG: its compressed array is still returned to the pool.

**Required change:** restrict spare selection to a capacity that can actually be admitted, or retry with an
appropriately sized buffer before evicting queued frames or dropping the incoming frame. Preserve the new
capacity accounting and both byte ceilings. Cover an oversized-spare/two-stream sequence in addition to simply
asserting that the byte counters stay under their caps.

## Fix verification

2026-10-30, subsequent review of the uncommitted fixes on top of `native-codecs`. This section supersedes the earlier
working-tree status notes for R1-R5 and W1; the historical findings above remain intact.

Reviewed the changed presenter, pacing/report accounting, managed/native LZ ABI and in-place output path,
alongside the changes to ADRs 0025/0029, `native/README.md`, `native/VENDORED.md` and `spice_codec.h`.
Only regression tests and this appended section were edited during verification. The existing production,
native/harness and documentation changes were preserved; nothing was committed.

### Verdicts

| Finding | Verification result |
|---|---|
| R1 | **Correct by inspection.** `src/SpiceViewer/Presenter.cs:323` now continues only while `StillOccluded()` is true. A visibility notification that clears occlusion falls through to the pending full render on the same wake. No live DXGI/window test was run. |
| R2 | **Fixed for the reported byte-bound scenarios.** `src/Spice.Session/SessionDisplay.cs:358-379` charges/debits array capacity; `:515-536` bounds spare storage by both count and 64 MiB. Tests sum actual owned arrays independently of the counters, exercise rejection at the live-queue limit and pool eviction at the spare-byte limit, and verify clearing. The two limits are separate, allowing 128 MiB retained in total, plus a transient rental during admission. |
| W1 | **Fixed.** `src/Spice.Session/SessionDisplay.cs:492-511` limits spare reuse to at most one size class above the required buffer and considers the queue budget. A retained 64 MiB spare no longer gets lent to the two tiny frames in the original two-stream scenario; both frames are accepted and drawn. |
| R3 | **The cross-window/ID contamination is fixed.** Queued frames retain their arrival `Window`; `CountDrop` and `NextReportWindow` at `src/Spice.Session/SessionDisplay.cs:401-411` route drops from a closed window only to `EarlierDrops`. The exact five-report sequence and reactivation with queued frames pass against serialized channel output. Lifetime frame/drop totals are preserved across report sends. |
| R4 | **The unsafe mixed-layout call is guarded.** `sc_abi_version()` declares version 2 (`native/spicecodec/codec_bridge.c:114`); `NativeCodecs.AbiMatches` checks the loaded export and treats its absence as version 1 (`src/Spice.Native/Decoders.cs:276-281`). `LzDecoder` refuses a mismatched version before LZ begin/decode (`:73`), and passes capacity to the version-2 entry point (`:81`). Tests verify the actual rebuilt DLL's version/public loader result and safe rejection of a module without the export. |
| R5 | **Fixed for the matched viewer/DLL path by inspection.** `src/Spice.Native/Decoders.cs:70` reserves `round_up(width, 8) * height * 4`, enough for every supported palette row. `native/spicecodec/codec_bridge.c:212-235` therefore selects caller-buffer decoding and ascending `memmove` compaction, bypassing native scratch allocation even above 4 MiB. The existing tight-buffer C ABI guard regression also passes. Native allocation counts on the large-image path were not independently measured this round. |

R3's scope is worth distinguishing from complete spice-gtk equivalence: the revised ADR retains drops of
current-window frames in that window's report, including current-window decode/playback losses. Spice-gtk's
arrival report counts negative arrival margins and keeps playback drops in a separate statistic, as described
above. The new tests verify that deferred losses never contaminate an already advanced report window/ID; they
do not establish identical adaptive-streaming policy. The documented tight-capacity C fallback likewise still
has scratch allocation above 4 MiB; the viewer supplies the larger buffer and avoids that branch.

### Added regression tests

`tests/Spice.Replay.Tests/StreamPacingReviewRegressionTests.cs` reuses the existing manual clock and stream
messages. `StreamPacingTests.cs` changed only to make that test class partial. The report sink captures the real
`SpiceChannel` writer output, parses miniheaders and checks all eight report fields; no real network or timer
advancement is involved.

| Test | Evidence |
|---|---|
| `DeferredDropsDoNotLeakIntoTheNextSerializedReportWindow` | Four early frames stamped 1400/1433/1466/1499, one wake at local 500, then stamp 1532 produce five serialized one-frame reports, each with zero reported drops; the three deferred losses remain in lifetime totals. This is the exact R3 sequence above. |
| `ReactivationKeepsOldQueuedDropsOutOfTheNewReportId` | Queued frames from report ID 11 are superseded after activation of ID 22. The only serialized report has ID 22 and no inherited loss. |
| `QueueByteCapCountsRoundedCapacityAcrossStreamsAndRejectsOverflow` | Five streams receive payloads just over 8 MiB. Four 16 MiB buffers fill the queue budget; the fifth is rejected. Actual array capacities match the counters and stay within the limit; destroy-all clears both queues and spares. |
| `ReturningLargerBuffersEvictsSparesToKeepThePoolWithinItsByteCap` | Four returned 16 MiB arrays fill the spare budget. Returning a subsequent 32 MiB array evicts smaller spares instead of retaining 96 MiB. Destroying the last stream releases the remaining storage. |
| `OversizedSpareDoesNotCrowdOutTwoSmallEarlyFramesOnDifferentStreams` | A payload just over 32 MiB seeds a 64 MiB spare through the real enqueue/deferred-rejection path. Two subsequent tiny, valid JPEG frames on different streams both queue in small buffers and both draw. |

`tests/Spice.Native.Tests/CodecAbiRegressionTests.cs` adds:

- `RebuiltCodecDeclaresTheExpectedAbiAndPassesThePublicLoaderProbe`: loads the real rebuilt codec, calls its
  `sc_abi_version` export, checks version 2 and exercises `NativeCodecs.SpiceCodecError` / `TryLoad`.
- `MissingVersionExportIsRejectedAsLegacyAbiWithoutCallingAnyDecoder`: passes a real system-module handle
  without `sc_abi_version` to the probe and verifies rejection with the version-1/needs-2 diagnostic. It neither
  replaces the codec DLL nor mutates the process-wide cached probe. A present-but-wrong version export was not
  separately synthesized; its rejection branch was inspected.

### Executed validation and limits

- Focused pacing tests: **27 passed**, including the five new pacing/report/pool regressions and the existing
  pacing allocation tests.
- Focused native ABI/previous native-review regressions: **4 passed**, including the two new ABI tests.
- Full `Spice.Replay.Tests` project: **127 passed, 0 failed, 0 skipped**.
- Full `Spice.Native.Tests` project: **24 passed, 0 failed, 0 skipped**.
- `git diff --check`: passed.

The focused commands used `dotnet test <project> --no-restore --filter ...`; the complete project runs used
`dotnet test <project> --no-build --no-restore`. No regression test is left failing from this verification.
The user reported a native rebuild, `difftest --ref HEAD` and a green complete solution test run; those particular
commands were not independently repeated here. No new native build/differential/ASan run, native heap trace,
full solution run, AOT publish, ARM64 verification, viewer/GPU test or live guest check is claimed.

## Desktop Presenter test follow-up

2026-10-30, at the human's request. Removed the two empty unconditional skips in
`tests/Spice.Viewer.Tests/SettingsConcurrencyTests.cs`. Added `PresenterDesktopTests.cs`, executing the real
production Presenter on a test-owned Win32 window using its existing forced-WARP debug option. Reflection
crosses the internal type boundary only to call public members; no private state or worker handles are used.

On the interactive desktop with Microsoft Basic Render Driver/WARP, the executable tests for repeated
`SetSuspended(false)` and minimized suspension/restore passed. They check that public `PresentCount` stays
unchanged during repeated false calls or suspended guest changes and that restore presents a pending frame
without another guest update. Startup layout/full-frame wakes are allowed to settle before those assertions;
the tests pump their own window messages and clean up their window and process-wide debug settings.

The sandbox uses an isolated background desktop, where the first present was already occluded even before
minimization. The conditional test attribute now detects that the test desktop differs from the input desktop
and skips with that reason; it also checks window-station visibility, input-desktop access and WARP availability.
Actual interactive runs were performed outside that isolated desktop. Neither test is unconditionally skipped.

The attempted R1 probe minimized the real window without calling `SetSuspended`. The minimized present returned
without producing the public debug-log occlusion transition; a subsequent guest update still advanced
`PresentCount`. Thus minimization did not establish R1's precondition on that interactive WARP flip-model path.
This agrees with Microsoft's [DXGI status documentation](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-status),
which says flip-model swap chains do not report `DXGI_STATUS_OCCLUDED`. The probe was removed because assuming
that status would make a platform-dependent failure, not a valid regression assertion. This does not change
the source-level finding about the inverted guard, and the suspension tests do not claim R1 coverage.

**Minimal production seam requested, not implemented:** an internal test constructor overload accepting a
`Present1` adapter (both normal and `DXGI_PRESENT_TEST` calls) and an optional caller-owned wake event. A test
can then script an occluded real present, an occluded test present, and a visible test present, and signal the
visibility wake without dirtying the guest framebuffer. Add a public monotonic `UploadCount` diagnostic,
incremented only when a nonempty upload is actually submitted, so tests can assert no hidden GPU uploads rather
than inferring that from `PresentCount`. Default production construction must retain the current native calls
and event ownership. No swap-chain mode change or rendering abstraction beyond these hooks is needed.

Only tests/test configuration and review documentation were edited for this follow-up. GPU upload counts,
R1's visibility-event branch, hardware-device behavior and Native AOT remain unverified by these tests.

Final validation: viewer test project built with **0 warnings and 0 errors**. The complete viewer test project
ran on the interactive desktop with **98 passed, 0 failed, 0 skipped**, including both new WARP window tests.
`git diff --check` passed. No failing assumption-based occlusion probe or empty placeholder remains in the suite.

## Implementer's notes

Claude, 2026-10-30.

- **R1's test seam is not added.** Microsoft's [DXGI_STATUS](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-status)
  reference says flip-model swap chains never return `DXGI_STATUS_OCCLUDED`, and the presenter uses one, which
  matches what the WARP probe saw. The corrected guard stays because the spec's MUST rule names that status (spec
  "Presenter" step 6 and the energy rules); whether to drop the unreachable branch is a spec question for the human.
  Minimized windows stop presenting through `SetSuspended`, which the two desktop tests now cover.
- **The native allocation regressions** (`native/difftest/review-regressions.c`) run as part of
  `native/difftest/run.sh`. The re-review's three findings there are fixed; see the native review's
  "Fixes (re-review)" section.
