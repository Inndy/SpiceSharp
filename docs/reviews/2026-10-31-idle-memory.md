# K1 idle-memory trimming review

Codex, 2026-10-31. Reviewed `198f4e3`, `de1fbd8`, and `41d0f53` against main `55ba8cc`, including decision
0041, the binding and ownership paths, and the verification record. Read AGENTS.md, the design spec and
engineering principles. Review and tests only; production code and the proposed decision remain unchanged.

**Verdict: one P2 finding, reproduced by an enabled failing test. No native memory-safety or pixel-pool
ownership regression confirmed under the existing caller contracts.**

## Findings

### K1-R1 — P2: a forced trim can duplicate a trim already in progress

**Location:** [SessionMemory.cs:25–32](../../src/Spice.Session/SessionMemory.cs#L25), introduced by `de1fbd8`.
The actual callers are the last-stream handlers in
[SessionDisplay.cs:247](../../src/Spice.Session/SessionDisplay.cs#L247) and minimize in
[MainWindow.cs:414](../../src/SpiceViewer/MainWindow.cs#L414).

The timestamp compare-exchange is not an in-progress guard. After the display thread updates `_lastTrimMs`
and enters `Canvas.TrimGlzPool()`, the UI thread can read that updated timestamp and successfully replace it
again because `force: true` bypasses the interval check. Both calls then trim and each runs a blocking,
aggressive, compacting generation-2 collection. The CAS comment says another thread already trimming is
skipped, but it only detects a timestamp change between that particular read and CAS.

This occurs with supported callers: minimize while the last stream's trim is waiting for the decode gate,
trimming pools, or collecting. Memory remains protected by the lower-level locks, but the UI/display paths
can pay for two full collections for the same transition, with the second potentially reclaiming little.
This is a performance/energy issue, not evidence of native corruption. The 30-second interval still works
for sequential non-forced calls.

**Regression:** `IdleMemoryReviewTests.ForcedTrimDoesNotDuplicateAnAlreadyAdmittedTrim` in Replay. It holds
the existing GLZ decode gate, admits a non-forced trim, and starts a forced trim while the first cannot yet
trim or collect. Both return true on the reviewed code; the second must decline the duplicate work.
The test uses real locks and dedicated threads, with bounded synchronization waits and no production hook.
It remains failing, as required by the agent guide.

**Suggested fix:** keep trim admission separate from the last-trim timestamp. A forced request should
bypass the time interval while respecting an active trim; release the admission guard in `finally`.
Keep the existing no-timer policy and native-window locking.

## Native ownership, synchronization and compatibility

- `decode-glz.c:185–201` destroys only entries below `npool`, clears those entries, and resets both pool
  counters. It does not change `images`, `oldest`, `tail_gap`, `retained` or `last`. Retirement removes an
  image from window slots and clears `last` when necessary; reuse removes its entry from the active pool.
  Stale entries beyond `npool` are not traversed. The reported bytes are pixel capacities, excluding freed
  image metadata, consistent with the native regression's accounting.
- The C export has no internal synchronization. Direct C callers must serialize trim, decode, clear and
  destroy for a shared window. The managed path does: `GlzWindow.TrimPool`, clear/dispose, decoder creation,
  decode and destruction all use `GlzWindow.Gate`. Decode keeps that gate through the native-output copy
  (`Decoders.cs:149–159`), so trim cannot free a buffer being copied. No opposite lock acquisition was found
  in this new path.
- `size_t` is bound as `nuint`, with C calling convention. The new import is lazy and catches
  `EntryPointNotFoundException`, with a volatile process-wide negative cache. Added a test loading an isolated
  assembly and DLL copy whose trim export is hidden: two trims return zero and the same native window still
  decodes. This exercises missing-export binding under JIT; it does not certify an AOT executable with an old
  DLL or changing the loaded DLL during a process's lifetime.
- The dated 2026-10-31 `MODIFIED FILE` entry and adjacent modification marker are present in the LGPL file.
  `native/VENDORED.md:263–277` records the change, upstream origin and licence. No vendored reformatting or new
  third-party component was introduced.
- Added a genuine cross-image-reference test after trimming, followed by 500 dependent decodes competing
  with 1,000 trims. The original trim cases used literal frames; continued literal decoding alone did not
  exercise a later image referring to a retained image.

## PixelBuffers and event behavior

- Power-of-two class selection is correct above the 32 KiB shared threshold. Count, slot and byte accounting
  are under one lock; allocation happens outside it. Trim drops spare references under that same lock and
  never touches outstanding rentals. Returns after trim may repopulate the pool safely. Limits cover spare
  bytes, not rented arrays or the small shared pool; arrays exceeding the 64 MiB spare budget cannot stay warm.
- Raw `PixelBuffers.Return`, like the prior `ArrayPool.Return`, assumes a single return per rental. It does
  not detect a caller returning the same raw array twice; that misuse can produce aliasing rentals. No new
  production caller violating that ownership contract was found. Decoded-image release and mask disposal
  clear their arrays before a subsequent sequential release can return them again. Added tests cover both
  owners' repeated release, rented-buffer survival through trim, exact class boundaries and concurrent
  rent/return/trim with outstanding-array identity checks. Existing warm allocation coverage passes.
- Last-stream destroy trims after its frame and compressed-frame pool are released; destroy-all trims only
  when streams existed. Removing one of several streams does not trim. Both paths leave `_streamLock` before
  trimming. Minimize calls the forced path after suspending presentation. There is no new timer or idle poll.
- Minimize continues decoding. A trim is a snapshot of spare storage, not a guarantee that memory stays low
  while minimized. Live arrays, caches, surfaces and dictionary images remain reachable. The new full GC
  pauses managed work process-wide; waiting for a decode gate also contributes to the calling thread's delay.

## Verification record versus evidence

The release order, events and memory categories described in
[the verification record](../verification/2026-10-31-idle-memory.md) match the inspected code, subject to K1-R1
and concurrent repopulation above. The reported 0.7–7.4 ms timings and memory readings are measurements of the
listed runs, not upper bounds on blocking GC or minimize latency. The opening claim of release "without a
cost in speed or energy" needs that qualification: collection and pool rewarming have costs, already
acknowledged in decision 0041. Warm zero-allocation tests establish allocation behavior, not throughput or
lock-contention performance. The comparisons also change pool policy and workload allocation order; they do
not isolate the lock's throughput cost.

No live guest, host memory measurement, restore behavior, occlusion result, workload cleanup or historical
all-solution result was independently repeated. The record's VM/driver observations remain the author's
reported evidence. This review uses synthetic messages, fixtures and native tests only.

## Validation

| Check in this worktree | Result |
|---|---|
| Offline `bash native/build.sh x64` | Passed; all three cached source archives verified; codec smoke passed |
| `python native/verify.py x64` | Passed: 20 spicecodec, 10 turbojpeg, 5 opus exports; PE/import checks passed |
| `bash native/difftest/run.sh --quick --ref main` | Passed against `55ba8cc`; differential, fault injection and release regressions: zero failures |
| `bash native/difftest/run-review-regressions.sh --asan` | Passed; zero failures or sanitizer reports |
| Native test project, `--no-restore` | **27 passed**, including two new review tests; no managed build warnings |
| Replay test project before review additions | **334 passed** |
| Replay test project after additions, final full run | **341 passed, 1 failed**: K1-R1; no skipped tests |
| Added Replay cases | Seven passed, one intentional failure |
| `git diff --check` | Clean |

One earlier full Replay run also exhausted all five allocation-measurement retries for `win7-desktop` due to
GC/background-completion interference. All five cases of that existing test passed in isolation, and the
final full run passed them; no allocation assertion was weakened or production allocation bug inferred.
Native compiler output retained the existing vendored `#pragma pack` warnings.

No viewer test project, viewer launch, VM, token file, internet, golden-image update or production edit was
used. The added review record, index row and tests are the complete commit scope.

## Response

Claude, 2026-10-31.

- **K1-R1: fixed.** `SpiceSession.TrimMemory` now admits one trim at a time through an `int _trimming` flag. The
  flag is taken with `Interlocked.CompareExchange` and released in `finally`, and it is separate from the
  last-trim timestamp.
  - A request that arrives while a trim runs returns false, forced or not. The running trim does the same work,
    and the UI thread no longer waits on the GLZ gate behind it.
  - `force` still bypasses only the 30 s interval.
  - The interval is checked again after admission, so a trim that finished between the first check and admission
    also counts.
  - `ForcedTrimDoesNotDuplicateAnAlreadyAdmittedTrim` passes unchanged. Decision 0041 states the rule.
- **Verification record wording:** the opening no longer claims the release costs nothing. It names the cost of
  each trim: one collection and a pool re-warm.
- **No change for:**
  - the raw `PixelBuffers.Return` double-return contract, which is the same as `ArrayPool.Return`'s, and which no
    caller violates;
  - the timings, which are measurements of the listed runs, as the review says.
- **Window checks:** the fix changes no window path. Minimize calls the same method, and only its admission
  differs. No live re-check was run: the desktop and VMs belong to another worker. If one is wanted, minimize
  while the last video stream ends on win7, and expect a single `memory: trimmed` line.
