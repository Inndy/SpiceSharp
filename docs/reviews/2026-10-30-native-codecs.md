# Native codec correctness and security review

2026-10-30. No production code, existing tests, golden images, or unrelated working-tree changes were edited.

## Scope and validation

Reviewed the native codec bridge, GLZ/LZ/QUIC decode paths, shim allocation/error handling, build hardening, and managed codec callers. The tracked native implementation is C, not C++. This is not an audit of the complete downloaded libjpeg-turbo/Opus sources.

Small standalone diagnostic executables were compiled from the current codec sources with GCC, using the repository's include paths and `-O2 -std=c11 -static-libgcc`. The configured MSYS2 Clang executable could not be launched (`Permission denied`); these diagnostic builds are not verification of the production toolchain, CFG, ARM64, or Native AOT. Probe sources and executables are local, ignored artifacts under `artifacts/native-review/`. The initial reset/palette checks also reproduced against the existing x64 DLL.

No full test suite, live guest, viewer, replay fixture, credential, or network service was used. Allocation failure was injected in a child executable; actual system-wide memory exhaustion was not attempted.

Other work changed the working tree during this review, including adding licensing comments to native files. Those changes were left untouched; the native comment additions did not change the reviewed behavior. Locations below reflect the files after those additions.

## Findings

### N1 — P1: Bound total memory retained by the GLZ dictionary

Location: `native/spicecodec/vendor/decode-glz.c:162-173`, with allocation at `:77-85` and release at `:435-442`.

Only individual dimensions and slot-array growth are bounded. A server can send sequential image IDs with `win_head_dist == id`, keeping the release floor at zero and retaining every decoded pixel buffer. This storage is outside the managed canvas cache budget. The session advertises a 32 MiB GLZ dictionary in `src/Spice.Session/SpiceSession.cs`, but the native window does not enforce an aggregate pixel/byte budget. The 65,536-slot limit still permits hundreds of GiB of ordinary-sized images. Track retained storage and reject additions exceeding a bounded dictionary budget; arbitrary eviction would break back-references.

Evidence: `retained_memory_probe.exe` successfully decoded nine 1024x1024 RGB32 images, each represented by 4,153 compressed bytes, and found all nine buffers retained: 36 MiB. No exhaustion was attempted.

### N2 — P2: Reset the GLZ oldest-ID floor when clearing the window

Location: `native/spicecodec/vendor/decode-glz.c:464-468`.

`glz_decoder_window_clear` resets the images and `tail_gap` but not `oldest`. After image 1 releases image 0, `oldest` is 1. Clearing the window and starting a new dictionary at image 0 then hits the expired-ID rejection in `glz_decoder_window_add`. `Canvas.ApplyCore(DisplayMsg.Reset)` reaches this clear operation, so a display reset can reject valid images from the restarted dictionary. Reset all ID bookkeeping together.

Evidence: the source-built probe returned success for image 0 and image 1, then `-1` for image 0 after a clear. The existing x64 DLL behaved identically.

### N3 — P2: Handle failed GLZ slot-array allocations before accessing them

Location: `native/spicecodec/vendor/decode-glz.c:140-152`.

`g_new0` is a raw `calloc` in the shim and can return null. Window resizing immediately writes through `new_images` without checking it, bypassing the fatal-path trampoline and crashing the process on allocation failure. Window/decoder creation and window clearing contain similar unchecked allocations. Check fallible allocations and use the guarded error path during decoding; constructors should propagate failure without dereferencing null.

Evidence: `allocation_failure_probe.exe` decoded image 0, then injected failure into the slot-array allocation while image 16 triggered a resize. The child exited with Windows access-violation status `0xC0000005`, rather than returning a codec error.

### N4 — P2: Make the LZ C ABI's padded output size explicit or remove the padding

Location: `native/spicecodec/codec_bridge.c:198-201`; public contract: `native/spicecodec/include/spice_codec.h:45-46`.

The public C header says the output buffer needs `width * height * 4` bytes, and `sc_lz_begin` reports visible dimensions. Palette decodes actually write the padded pixel count computed from compressed stride. A 9x1 PLT1 image therefore writes 64 bytes, not the documented 36. A direct C caller following that contract can overflow its output buffer. The current managed `LzDecoder` already accommodates padding in scratch storage, so this is an exported-C-ABI hazard, not a demonstrated overflow through the viewer. Expose the required padded size before decoding, or ensure the implementation writes only the advertised visible output.

Evidence: the source-built probe used a larger sentinel-backed allocation and observed 28 bytes changed beyond the documented 36-byte output. The existing DLL behaved identically. No genuinely undersized heap allocation was used.

### N5 — P2: Reuse retired GLZ pixel storage on the warmed display path

Location: `native/spicecodec/vendor/decode-glz.c:77-85`.

Each GLZ image allocates both a new image object and a zeroed pixel buffer; retirement frees both. This remains true for repeated same-sized updates after warm-up and causes native heap churn on ordinary display draws. Managed GC allocation counters do not measure it. This violates the explicit display-draw hot-path allocation rule in [AGENTS.md:25-26](../../AGENTS.md#L25-L26); decision 0009 does not exempt native codec allocations. Reuse retired objects and sufficiently sized pixel buffers while preserving the dictionary's live references and a bounded memory budget.

Evidence: source inspection of `glz_image_new`, `glz_image_destroy`, and their unconditional decode/release callers. No CPU, power, or native heap trace is claimed.

## Considered but not reported

The bridge accepts the QUIC GRAY header type but its RGB32 output request rejects GRAY decoding. A source-built encoder/decoder probe confirmed that behavior. However, the reference spice-common canvas also explicitly rejects GRAY images, so this was not treated as a supported SPICE display regression.

These probes establish the described behaviors, not that the codecs are otherwise memory-safe or exhaustively fuzzed.

## Fixes (Claude, 2026-10-30)

The human allowed patches to the vendored decoders on 2026-10-30, provided a differential test verifies them
([decision 0029](../decisions/0029-vendored-codec-patches-allowed-with-differential-verification.md)). Each fix below
passes `bash native/difftest/run.sh` (the patched DLL against the `HEAD` sources, with only the listed intentional
differences), `--asan` (AddressSanitizer and UBSan), `bash native/build.sh x64` with `verify.py`, and
`dotnet test`. Both tests in `tests/Spice.Native.Tests/NativeReviewRegressionTests.cs` pass unchanged. Patch
details are in [native/VENDORED.md](../../native/VENDORED.md).

- **N1 (retained GLZ memory).** As suggested. The window counts the bytes of the images it holds and rejects an
  image before allocating it if it would take the total past 128 MiB. Nothing is evicted, so back-references stay
  valid. The 32 MiB advertised dictionary bounds what a well-behaved server makes the client hold to the previous
  window plus the new image, 64 MiB; the budget is twice that. After a rejection the window accepts images again
  once images are released or it is cleared. The difftest checks this directly: 32 images of 4 MiB are kept and
  the 33rd is rejected (the reference build kept all 36), a clear recovers, and 200 images of 4 MiB with
  `win_head_dist = 1` all decode.
- **N2 (clear keeps the oldest-ID floor).** As suggested. `glz_decoder_window_clear` resets `oldest` together with
  `tail_gap`, the byte count and the reuse pool. `GlzWindowClearAllowsImageIdsToRestartAfterRetirement` passes. The
  difftest also checks that, without a clear, a released ID is still rejected.
- **N3 (unchecked GLZ allocations).** As suggested. A failed slot-array allocation during a decode unwinds through
  `sc_fatal` and frees the pending image. `glz_decoder_window_clear` keeps its old, emptied array if a new one
  can't be allocated, so the window stays usable. `glz_decoder_window_new`, `glz_decoder_new`,
  `sc_glz_window_create` and `sc_glz_create` return NULL. `native/difftest/faultinject.c` fails each allocation of
  a QUIC, LZ and GLZ session in turn: 32, 3 and 54 allocations, including the slot-array growth and a clear. No
  crash and no leak; before the fix the GLZ sweep crashed the process.
- **N4 (LZ C ABI writes padded rows).** Fixed by writing only the advertised output, the second of the two
  suggestions, because the header's `width*height*4` contract and Codex's test expect it. For palette images with
  padded rows, `sc_lz_decode` decodes into scratch storage in the handle (kept up to 4 MiB, freed above) and copies
  out rows of `width*4` bytes. This changes the C ABI's row layout for those images, so `LzDecoder`
  (`src/Spice.Native/Decoders.cs`) now reads `width*4`-byte rows. That is the only C# change.
  `LzCAbiPreservesBytesBeyondAdvertisedVisibleOutput` and the existing `PalettePaddingAndLifetime` pass. The
  difftest compares every palette image row by row against the reference and fails if the patched build writes a
  byte past `width*height*4`.
- **N5 (GLZ allocations on the warm path).** As suggested. Retired images keep their struct and pixel buffer in
  a pool of at most 8 images and 32 MiB, and new images reuse the smallest buffer that fits. Fresh buffers are
  still zeroed. Reused ones are not: a successful decode writes every pixel, and the difftest's byte comparison
  against the zeroing reference would show any pixel left over. `faultinject.exe` counts zero allocations in 100
  warmed-up decodes for same-size, alternating-size and rotating-size frames with `win_head_dist` 1, 2 and 4. As
  a side effect, scrolling 720p GLZ frames decode about 27% faster (provisional, decision 0029).

## Re-review

Codex, 2026-10-30. Reviewed the pre-release commit `native-codecs` (see the note on commit names in
[2026-10-30-pre-release-commits.md](2026-10-30-pre-release-commits.md)), including the LZ/GLZ
block-copy changes, QUIC LZCNT/AVX2 changes, reuse pool, differential tests and allocation harness. Also reviewed
`sc_lz4_decode`, introduced in `native-build` and unchanged by `native-codecs`. **N2 and N3 are complete; N1 and N5 are
incomplete. N4 fixes the original output overflow correctly, but introduces the scratch-retention defect R2.**

All production locations below refer to **`native-codecs`**, not later working-tree changes. At the start of this review
only an unrelated presenter change was present. Concurrent work subsequently changed the LZ ABI and other
files; those edits were left untouched. The new regressions and rebuilt original allocation/retention probes
were run against a `git archive` extraction of `native-codecs` under ignored `artifacts/native-review/`.
This review edits no production code or golden images.

### Verdict on N1-N5

| Finding | Complete and correct? | Re-review evidence |
|---|---|---|
| N1: aggregate GLZ memory | **Partially.** The unbounded-retention attack is contained, but the stated 128 MiB live-buffer budget counts logical pixels rather than allocated capacity. | The existing boundary test accepts 32 fresh 4 MiB buffers and rejects the 33rd. The new reuse probe holds **144 MiB** of live pixel buffers at a logical charge of 128 MiB, with the reuse pool empty. See R1. |
| N2: clear oldest ID | **Yes.** `oldest`, `tail_gap`, live accounting and pool accounting are reset together. | Rebuilt original probe: images 0 and 1 succeed, clear, image 0 succeeds. Both managed regression tests pass. Differential checks also confirm an expired ID is rejected without a clear. |
| N3: failed allocations | **Yes, for the reported paths.** Resize failure unwinds before touching the new array; constructors propagate NULL; clear can keep its old emptied array. | Rebuilt original injected-resize probe returns **-1**, rather than crashing. Release and ASan/UBSan fault sweeps pass all 32 QUIC, 3 LZ and 54 GLZ allocation positions, with no leaked blocks. The two benign GLZ clear/destroy allocation failures need not turn a successful decode into an error. |
| N4: palette output bound | **Original defect fixed correctly; replacement not entirely correct.** Tight row copies satisfy the published C ABI and the managed row-layout change agrees. Oversized scratch is not released on corrupt input. | Rebuilt original 9x1 probe changes **zero** bytes beyond the 36-byte visible output, versus 28 before the fix. Palette differential comparisons and managed tests pass. See R2 for the new error-path defect. |
| N5: warm-path reuse | **Partially.** Same-sized and the tested two/three-size workloads reuse storage correctly. The eight-slot pool still causes sustained allocations for recurring updates that fit comfortably within its byte allowance. | Existing warm-path tests again report zero allocations. The new ten-size workload reports **40 allocations in 20 decodes**, after two complete warm-up cycles. See R3. |

### Confirmed remaining/new findings

#### R1 - P2: Charge reused buffer capacity to the GLZ live-window budget

Locations: `native/spicecodec/vendor/decode-glz.c:145`, `:153`, `:239` and `:488-489`.

`sc_glz_bytes()` charges `hdr.gross_pixels * 4`, but `sc_glz_pool_take()` may return a larger allocation and
`glz_image_new()` keeps its original `cap`. That capacity disappears from `pool_bytes` on reuse without being
fully added to `retained`. Consequently the check does not enforce the documented 128 MiB of live pixel
storage. This is a bounded accounting error, not a reproduction of the original unlimited-memory attack.

Confirmed sequence: retain eight 4 MiB images; decode a 2 MiB image that retires all eight; then reuse those
eight buffers for 2 MiB images and keep adding 2 MiB images with release floor 8. All 64 live images are
accepted. Logical charge: 128 MiB. Actual pixel allocations: **144 MiB**, with no pooled images left.
Allocator instrumentation measures **150,998,912 bytes** above the empty window, including 3,968 bytes of
additional image/slot metadata. Account for the selected allocation's capacity before taking/allocating it,
and subtract that same charge on retirement; retain a separate bounded pool charge.

Failing test: `glz_capacity_budget()` in `native/difftest/review-regressions.c`. It measures allocation requests
without inspecting private struct layouts and allows 64 KiB for metadata.

#### R2 - P2: Free oversized LZ palette scratch on the guarded error path

Locations: `native/spicecodec/codec_bridge.c:209-218`, `:224-228`; early return in `SC_GUARD` at `:33-34`.

Scratch is allocated before `SC_GUARD`. If `lz_decode()` rejects corrupt input, the guard returns immediately,
skipping the cleanup that frees allocations larger than `SC_LZ_SCRATCH_KEEP`. A peer can therefore pin a large
native buffer in a long-lived decoder using a header with no compressed body. Later successful RGB decodes
do not release it. The bridge's accepted padded-pixel limit permits up to 256 MiB of scratch; the probe uses
only about 8 MiB. This is retained memory until decoder destruction, not a permanent leak after destruction.

Confirmed with a **28-byte** PLT1_LE header: width 2049, height 1024, compressed stride 257, and no body.
`sc_lz_begin()` succeeds, `sc_lz_decode()` returns -1, and **8,421,376 bytes** remain allocated. A subsequent
successful 1x1 RGB decode leaves the same scratch retained. Ensure oversized scratch cleanup runs on both
success and failure while restoring the fatal trampoline normally.

Failing test: `lz_error_scratch()` in `native/difftest/review-regressions.c`, with separate assertions immediately
after rejection and after recovery to RGB. Decoder destruction frees everything in the test.

#### R3 - P2: The GLZ pool's slot limit still churns on warmed recurring sizes

Locations: `native/spicecodec/vendor/decode-glz.c:129`, `:153`, `:170-174`.

The pool evicts its oldest entry at eight retired images regardless of its byte usage. Cycle ten RGB32 update
sizes, with `win_head_dist=0`, width 1024 and heights **1, 4, 10, 22, 46, 94, 190, 382, 766, 1534**. These ten
buffers together need **12,488,704 bytes (less than 12 MiB)**, and each frame is smaller than 6 MiB. Adjacent
sizes deliberately exceed the pool's fit tolerance, so one buffer cannot substitute for the next smaller size.
The slot limit evicts each size before its next use, even though retaining the recurring working set would fit
within the existing 32 MiB pool budget. After twenty warm-up frames, the next twenty decodes make **40 heap
allocations** (image struct and pixels each time). Every decode succeeds with the expected dimensions/colors.

This is a remaining N5/spec allocation-rule violation, not a demonstrated memory-safety defect. Preserve a
bounded reusable working set that covers recurring display updates; the four existing small-cycle allocation
tests are insufficient to establish the blanket allocation-free claim.

Failing test: `glz_rotating_sizes()` in `native/difftest/review-regressions.c`.

### Other added decode paths

- **LZ/GLZ block copies:** no new defect confirmed. For intra-image matches the source stays fixed while the
  destination advances; each `memcpy` length is at most their current distance, so the individual copies do not
  overlap and correctly build the repeated period. Cross-image GLZ copies are between distinct live/pending
  allocations, with logical source/output bounds checked before the copy. Alpha-only templates retain their
  per-alpha-byte copies, preserving destination RGB. The removed per-pixel checks are redundant with the
  preceding whole-match checks. The shared bounds/pointer arithmetic was not rewritten by this commit.
- **QUIC LZCNT:** `~io_word | 0x00FFFFFFu` is always nonzero and limits the count to 0..8, equivalent to the
  original high-byte lookup; it does not invoke `__builtin_clz(0)`.
- **QUIC AVX2 `update_model`:** the eight 32-bit loads fit the eight-counter bucket and eight-entry code-length
  row. The shuffle/min reduction uses unsigned comparisons, and the highest set equality-mask bit preserves
  the scalar loop's highest-index tie break. At least one lane equals the minimum, so this `clz` argument is
  also nonzero. Counter halving remains unchanged. No new correctness or bounds defect confirmed.
- **GLZ reuse ownership:** taking removes the entry from the pool; live images are pooled only on retirement;
  failed pending decodes destroy their buffer; clear/destroy release pooled and live storage. No stale-pixel
  disclosure, aliasing with live dictionary references, or double free was confirmed. R1 and R3 remain.
- **LZ4 (`codec_bridge.c:323-343`):** no new defect confirmed. Record lengths are checked against both the
  remaining input and `INT_MAX`; a successful bounded decompression cannot advance `total` beyond the clamped
  output capacity, preserving the subtraction invariant for the next block. One initialized stack stream
  preserves the contiguous prior-output dictionary across blocks. The rebuilt pinned smoke test passes linked
  multi-block output, exact/insufficient capacities, partial records, zero/oversized lengths and invalid offsets.
  The existing native managed suite, including LZ4 corruption/linked-block tests, also passes.

### Validation and reproduction

- Ran `bash native/difftest/run.sh --ref` with the parent commit and the same command with `--asan`, explicitly
  comparing with the parent **`viewer-options`**, rather than comparing the fix with itself. Both passed, including the existing
  intentional-difference checks and fault-injection/warm-path harnesses. Each exercised 2,790 fixture inputs,
  5,064 synthetic inputs and 52,928 mutations, with no unlisted mismatch. These runs began before the concurrent
  LZ API edits. No decoder ASan/UBSan diagnostic was emitted; the harness retains its documented suppressions
  for the test-only upstream LZ encoder's signed hash arithmetic.
- `dotnet test tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore`: **22 passed**, including both
  `NativeReviewRegressionTests`. The original reset, palette-bound, allocation-failure and nine-image retention
  probes were rebuilt with GCC `-O2 -march=x86-64-v3` and rerun. The original nine-image probe still accepts
  36 MiB, which is below the new limit; the differential boundary test supplies the new rejection check.
- Added `native/difftest/review-regressions.c` and `native/difftest/run-review-regressions.sh`. Against the pinned
  commit, **both production-style Clang release and ASan/UBSan runs exit 1 with four failing assertions for the
  three findings above**, with identical measured bytes/counts and no sanitizer diagnostic. The GCC build
  reproduces them too. Each scenario frees all native allocations at destruction. These failures are intentional
  and remain failing until implementation fixes land. The test supports both LZ decode signatures via typed
  compile-time dispatch, so it can also run alongside the concurrent capacity-argument change.

Reproduce against a commit `REV` from the repository root (MSYS2 shell, pinned LZ4 download present). The
reviewed pre-release commit was squashed into the initial commit, so it can no longer be checked out:

```sh
mkdir -p artifacts/native-review/REV
git archive REV native/spicecodec | tar -x -C artifacts/native-review/REV
bash native/difftest/run-review-regressions.sh --source artifacts/native-review/REV/native/spicecodec
bash native/difftest/run-review-regressions.sh --asan --source artifacts/native-review/REV/native/spicecodec
```

The new runner defaults to the working tree without `--source`. Logs are ignored under
`native/build/review-regressions/{release,asan}/results.log`; the earlier differential logs remain under
`native/build/difftest/`. Did not rerun the complete solution suite, packaging/full dependency build, ARM64,
live guests, or power/throughput benchmarks. The performance figures in the Fixes section remain the
implementer's provisional measurements. This is targeted verification, not exhaustive fuzzing or proof that
the codecs contain no other vulnerabilities.


### Fixes (re-review)

Claude, 2026-10-30. All three findings are fixed; `review-regressions.exe` passes in release and ASan/UBSan
builds and now runs as part of `native/difftest/run.sh`, which also passes against the previous build and against
the parent of `native-codecs` (`--asan` too).

- **R1 (budget by capacity).** `sc_glz_bytes` charges the buffer's capacity, so the window and retirement count
  what is allocated, and a pooled buffer is reused only if its capacity fits the remaining budget. The probe's live
  allocations above the empty window drop from 150,998,912 to 134,221,248 bytes (128 MiB plus metadata).
- **R2 (LZ scratch on errors).** The guarded error path in `sc_lz_decode` frees a scratch above 4 MiB, as the
  success path does: 0 bytes retained after the rejected header. Separately, `sc_lz_decode` now takes the output
  capacity and decodes palette rows in place when the buffer holds the padded rows, which `LzDecoder` always
  passes, so the viewer never uses the scratch.
- **R3 (pool slot limit).** The pool keeps up to 32 retired images within the same 32 MiB, so the byte limit decides
  what stays warm: the ten recurring sizes make 0 allocations after warm-up.
