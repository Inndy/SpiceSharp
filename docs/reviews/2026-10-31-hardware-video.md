# Hardware video review

Codex, 2026-10-31. Review of `bcceecdf22fabd1abc71aeb6cb1389fa58637cfb` against its parent on this branch.
Read AGENTS.md, the design spec and principles, and decisions 0037, 0018 and 0016. Production code and existing
test assertions were left unchanged.

**Verdict: changes requested. Three P2 findings; three new bug cases remain failing.** Six additional replay
cases pass. The change to `UnsupportedCodecDoesNotDecodeEvenAValidJpeg` is justified.

## N1 - P2: Validate and reserve the output before allocating GPU conversion targets

Locations: `src/SpiceViewer/Video/MfVideoDecoder.cs:315-345`, especially `EnsureProcessor` at `:319` before
`output.Begin` at `:341`; allocations at `:388-407`. Budget/size guard: `src/Spice.Session/SessionDisplay.cs:809-820`.

The destination comes from STREAM_CREATE or STREAM_DATA_SIZED. `Present` creates a video processor, BGRA render
target and CPU-readable staging texture, performs the blit and copy, and only then calls `VideoOutput.Begin`.
That last call is where the session rejects an impossible size or an exhausted 256 MiB decoded-frame budget.
Consequently, rejection cannot prevent the preceding GPU allocations or work. A budget-denied stream also retains
its conversion textures until resize/disposal. Multiple streams can therefore retain substantial conversion
memory even though the session refuses their output frames. This follows from the allocation order; no memory
exhaustion experiment was performed.

There is also a directly reproduced consequence: a valid H.264 access unit sent to a partly visible destination
of 16,385 by **one** pixel reaches D3D11's dimension checks before the session guard. The resulting HRESULT is
converted to `Failed`, disposing the decoder and marking the whole stream unsupported, instead of dropping the
oversized picture as the output contract specifies. This bounded test uses no large texture or framebuffer.

Validate/reserve the session output before creating or submitting conversion resources. Refused output must
leave the inter-frame decoder advanced, count a picture drop, and avoid allocating targets for that picture;
retained GPU resources also need a bounded lifetime when the frame budget rejects output.

Failing test: `HardwareVideoReviewTests.AnImpossibleDestinationDropsThePictureWithoutFailingTheDecoder`.

## N2 - P2: Balance each successful COM initialization on its calling thread

Locations: `src/SpiceViewer/Video/HardwareVideo.cs:49`, called by `Probe` at `:61`, `FindDecoder` at `:191`, and
`CreateSessionDevice` at `:273`. There is no corresponding `CoUninitialize` in this implementation.

`CoInitializeEx` returning S_FALSE is still a successful, counted initialization. Every decoder enumeration adds
another count on its calling thread, including enumeration that finds no matching MFT; stream creation repeats
this on the receive workers. Releasing transforms, activation objects and devices does not balance apartment
initialization. The caller cannot correctly finish its own COM lifetime while these extra counts remain, and
apartment cleanup is no longer paired with the operation that initialized it.

Confirmed on a dedicated thread with two enumerations of an absent synthetic subtype: the explicit COM
initialization depth rises from **1 to 3**. The test accounts for the runtime's pre-existing initialization,
restores every count used to measure the depth, and unwinds only the surplus counts before the thread exits.
It does not infer a leaked GPU object or a measured number of leaked bytes from this count.

Use a balanced COM lifetime on each owning thread, including S_FALSE and early-return paths. Do not uninitialize
after RPC_E_CHANGED_MODE, which did not acquire an initialization count. Any decoder work moved to pacing workers
also needs an explicit, consistent apartment-lifetime policy.

Failing test: `HardwareVideoReviewTests.DecoderEnumerationDoesNotLeaveComInitializationsOnItsCallingThread`.

## N3 - P2: Count a decoded hidden picture as proof that the replacement device works

Locations: `src/SpiceViewer/Video/MfVideoDecoder.cs:153-160`; recovery state in
`src/SpiceViewer/Video/HardwareVideo.cs:299-323`.

`Drain` obtains decoded samples for frames that are hidden, late or superseded, but `Decode` returns before
`OnPicture` whenever `output` is null. A replacement device can successfully decode many such pictures while
`_proven` remains false and `_recreated` remains true. Another device loss then permanently sets `_disabled`,
applying the "replacement failed before decoding a picture" rule to a device that already decoded pictures.
This contradicts decision 0037's recovery rule and the comment on `OnDeviceLost`; clip coverage should not decide
whether hardware decoding remains available for later streams.

The regression creates a separate headless factory, puts it into the documented replacement-device state,
feeds three valid H.264 frames under an empty clip, then directly signals loss through `OnDeviceLost`. Hardware
becomes disabled. No actual device removal, desktop operation or driver reset occurs. The same synthetic clip
produces pictures in the existing shown-frame tests. Device proof should be recorded when a decoded picture is
obtained, independently of whether the session requests conversion/readback.

Failing test: `HardwareVideoReviewTests.HiddenDecodedPicturesProveARecreatedDeviceBeforeAnotherLoss`.

## Protocol review

Read the existing local spice-protocol clone at `499cc8326a672e9e5747efc017319b19e1594b42`, spice-gtk at
`4086377fb5379a849a7444a6b32686ae14856255`, and the local spice-common `spice.proto`. No spice-server clone was
present alongside those references, so server conclusions below use decision 0037's citations to
`adeb620a04c43620416c5067b8bbfd1e4cf5eef5`; they were not independently re-fetched. No network access was used.

- **105 and capabilities:** `Messages.cs:176-198`, `Link.cs:25-26` and `Display.cs:15` agree with the protocol
  headers and spice.proto: byte count followed by byte codec values; codec IDs 1-5; MULTI_CODEC 8, MJPEG 9,
  VP8 10, H.264 11, preference 12, VP9 13, H.265 14. The normal hardware factory offers only H.264/VP9/H.265;
  MJPEG remains present and VP8 is absent. Preference filtering preserves order, removes duplicates, and requires
  the server capability. It is sent after DISPLAY_INIT, and the connection-time/property-change handoff retains
  the existing serialized channel-send behavior. Wire and session capture assertions pass.
- **STREAM_CREATE:** the parsed codec selects a stream's own decoder; the payload is not sniffed as JPEG.
  Unadvertised/uncreatable codecs become unsupported. This agrees with spice-gtk `channel-display.c:1387-1442`.
  The encoded dimensions are hints, while decoded type changes reconfigure output. Bottom-up handling agrees
  with spice-gtk `:1493-1502`, and clipping remains in the canvas.
- **Invalid stream report:** `SessionDisplay.cs:397-407` emits stream ID, zero unique ID/times/frame count,
  UINT32_MAX drops and zero delays, gated on STREAM_REPORT. This matches `report_invalid_stream` at
  `channel-display.c:1333-1352`. Decoder failure disables ordinary reporting and ignores subsequent frames.
  The cited server handler recognizes the sentinel before normal report-window handling and falls back to draws;
  no live-server fallback was tested.
- **Input framing/order:** the local GStreamer client specifies H.264/H.265 byte streams and plain VP9
  (`channel-display-priv.h:176-188`). It ignores empty frames and drops late compressed input only for MJPEG
  (`channel-display-gst.c:869-891`). The implementation submits each access unit without introducing length-prefix
  conversion. The cited server emits one compressed access unit/frame per message and disables VP9 reordering.
  Session drop paths feed dependency frames in receive order: late arrivals first drain older queued input,
  overflow/superseding/timeline changes feed discarded heads, and hidden frames decode without output. The six
  added `HardwareVideoFeedingReviewTests` cases cover overflow, superseding, timeline replacement, mode change,
  destroy-time tail flushing and failure during discarded-frame feeding. All pass. Existing tests cover late
  arrivals, hidden input, unsupported creation and exact outgoing report bytes.

The old unsupported-codec JPEG test formerly expected one frame and one drop. Decision 0037 explicitly changes
that policy to unsupported-at-creation. The edited assertion still proves no JPEG decoder, no frame allocation
and no pixel change; it adds Unsupported and checks both counters remain zero. An independent existing test
checks the invalid-stream report bytes. This is a justified expectation change, not a removed codec check.

## MF/D3D11, energy, AOT and settings checks

- **Raw ABI:** compared the emitted CsWin32 0.3.367 bindings and installed Windows SDK 10.0.26100.0
  `mftransform.h`/`mfobjects.h`. IMFTransform slots 14/23/24/25 and IMFAttributes slots 7/8/10/15, pointer widths,
  Stdcall signatures and HRESULT return handling match. NEED_MORE_INPUT, NOTACCEPTING, absent attributes and
  exhausted type enumeration take nonthrowing control paths. `GetDeviceRemovedReason` is a generated throwing
  wrapper at slot 39, so `Fail`'s catch does observe a removed-device HRESULT; it is not accidentally ignoring a
  returned HRESULT.
- **Object references:** normal decoder construction holds independent device/manager references; factory
  temporaries are released. Output samples/events, media types, texture/buffer queries and cached input views have
  matching releases on the normal/drain/error paths inspected. Views retain their textures, so cached texture
  addresses cannot be recycled underneath the cache. The cache has a 32-view bound. The factory intentionally
  retains its process-lifetime device/manager; that is separate from the unbalanced thread initialization in N2.
  This source audit and headless tests do not prove absence of all driver-side leaks or allocation-failure leaks.
- **Conversion/readback:** NV12/P010 output selection, array-slice input views, render-target/staging flags,
  progressive source/destination/target rectangles, source aperture, matrix/range configuration and processor
  invalidation on format/size changes are coherent. Row copies use mapped RowPitch and copy only width times four
  bytes, with bottom-up reversal before the canvas's clip. Existing real H.264 tests pass for scaling, sized output,
  clipping and row reversal. N1 is the resource-validation ordering defect. VP9/HEVC, P010, coded-size changes,
  unusual driver pitch/apertures and HDR color handling were not independently exercised here.
- **Threading/recovery:** the separate multithread-protected decode device avoids taking the presenter's device
  lock under `_streamLock`. Factory device references are acquired under its lock, which is released before
  decoder construction and stream-lock acquisition. Decode/disposal are serialized by the stream lock. No new
  reverse presenter/UI lock dependency was found. Lost-device notifications compare device identity, release the
  factory's references and defer recreation to a new stream; N3 is the proof-bookkeeping defect. The separate
  device and sync-MFT-only policy remain provisional choices for the human under 0037; this review does not
  approve those spec departures. No physical device-loss test was performed.
- **Energy/interop:** the new decode path has no idle timer or polling loop. Output draining is bounded work
  triggered by input; Map blocks for submitted GPU work. The five-second probe deadline is connection work.
  Warm shown/hidden H.264 allocation checks and existing session allocation checks pass. They measure managed
  allocations, not MF/driver native allocations or static-desktop power. New production bindings use CsWin32 with
  `allowMarshaling: false`; raw calls use unmanaged function pointers. No new DllImport was found. The viewer
  builds with warnings treated as errors. No Native AOT publish or arm64 execution was performed.
- **Setting/menu:** `videoCodec` has the documented default, lenient parsing, HEVC alias and source-generated
  JSON persistence. Settings/order tests pass. Static inspection of menu IDs, radio checks, command dispatch,
  hardware availability gating, probe-completion update, initial session preference and About text found no
  confirmed defect. Returning to server order is explicitly deferred until reconnect and logged. The default's
  host-CPU tradeoff is disclosed in 0037 and shipped instructions. No menu was opened on the desktop.

## Validation and scope

| Check | Result |
|---|---|
| Existing Video suite before additions | 27 passed, 2 skipped (VP9/HEVC decoder unavailable) |
| Video suite with review regressions | 27 passed, 3 failed (N1-N3), 2 skipped |
| Replay suite with six new feeding cases | 346 passed |
| Wire suite | 27 passed |
| `dotnet build src/SpiceViewer/SpiceViewer.csproj --no-restore -warnaserror` | Passed, zero warnings/errors |
| `git diff --check` | Clean |

The first overlapping test builds conflicted on shared intermediate files; subsequent builds/tests were run
sequentially. Those build races are not product findings. The COM regression was corrected to measure changes
relative to the runtime's existing apartment count, rather than assuming a fresh .NET thread has depth zero.

Only this review, its index entry and the two new test files are included. No production fixes, old assertion
changes, golden changes, VM access, credential-file reads, viewer launches or Spice.Viewer.Tests runs were made.
Tests use synthetic protocol input and the existing runtime-generated clip; this is not traffic-capture or
live-guest verification. The failing cases remain enabled for the implementer.

## Response

Claude, 2026-10-31. All three findings were fixed in production code, and none of Codex's tests were changed: each
one fails for the reason it describes, so none is wrong. Decision 0037 records the three rules.

- **N1:** `MfVideoDecoder.Present` now calls `VideoOutput.Begin` first. If the session refuses the size (beyond
  16384, which is both the canvas and the D3D11 texture limit, or over the frame budget), or the size is over the
  D3D11 limit, the picture is returned as `Dropped` before any texture is queried or any conversion resource is made
  or used. The decoder has already consumed the access unit, so it stays in step. `ReleaseProcessor` also frees the
  processor, targets and input views of an earlier size, so a stream the budget refuses holds no conversion memory
  until it is resized again.
- **N2:** `InitCom` is replaced by `HardwareVideo.ComScope`. This is a `ref struct` that calls `CoInitializeEx(MTA)`
  and, on dispose, calls `CoUninitialize` on the same thread when the call succeeded (S_OK or S_FALSE). It does not
  uninitialize after RPC_E_CHANGED_MODE. `Probe` and `FindDecoder` use it. `CreateSessionDevice` no longer
  initializes COM, because `D3D11CreateDevice` and `MFCreateDXGIDeviceManager` need no apartment. The decoders are
  free-threaded MF objects used under the stream lock and rely on the process MTA (decision 0037, "COM apartment").
- **N3:** `MfVideoDecoder.Decode` now reports `OnPicture` as soon as `Drain` yields a decoded sample, before it
  checks whether output was requested. Hidden, late and superseded pictures therefore prove a recreated device.

Validation:

| Check | Result |
|---|---|
| `dotnet build SpiceViewer.sln --no-incremental` | 0 warnings, 0 errors |
| Spice.Video.Tests | 30 passed, 2 skipped (VP9/HEVC decoder unavailable), including the three N1-N3 regressions |
| Spice.Replay.Tests | 346 passed |
| Spice.Wire.Tests | 27 passed |
| Spice.Native.Tests | 24 passed |
| Pve.Term.Tests | 115 passed |

Spice.Viewer.Tests was not run.

## Follow-up review

Codex, 2026-10-31. Reviewed `9c81fd60896531ad771aec5f8e6cb4bfa412ec0e` and
`566640e0026fb473ad6bcabebcaf98ca5f07a9a1` against `4779ed3`. Read AGENTS.md, the relevant design spec and
principles, decisions 0018/0037, and the previous review and fix notes above. Production code and existing test
assertions are unchanged. **Changes requested: five P2 findings, with eleven new failing test cases.**

### N4 - P2: Release the idle current device even when the final close belongs to an older device

Locations: `src/SpiceViewer/Video/HardwareVideo.cs:267,328-334,350-356`.

`_decoders` counts every generation, but `OnDecoderClosed` also requires the final decoder's device to equal the
current device. After loss, old decoders still hold their own references and can outlive replacement decoders.
Create two decoders on A, notify loss, create a decoder on B, close B's decoder, then close A's decoders. The count
reaches zero on A's final close, the pointer check fails, and the factory retains B's device and manager with no
decoder left. This defeats the idle-memory purpose of this commit and decision 0037's last-decoder rule.

Confirmed with actual headless devices/MFTs: the final count is zero and the current device remains non-null.
Loss notification was synthetic; neither device was physically removed. Track ownership by generation or make
the zero-count release target the idle current device, while retaining the existing creation reservation under
`_lock`. No additional native-memory measurement is inferred from this pointer-lifetime test.

Failing test: `HardwareVideoReviewTests.LastOldGenerationDecoderClosingReleasesAnIdleReplacementDevice`.

### N5 - P2: Successful hidden decoding must prevent session-wide codec avoidance after a later loss

Locations: `src/Spice.Session/SessionDisplay.cs:428,492-502,831,855-857`;
`src/SpiceViewer/Video/MfVideoDecoder.cs:158-164`.

The new `VideoStream.Pictured` becomes true only for a picture written to `VideoOutput`. Hidden, late and
superseded pictures return `NoPicture` by the decoder contract; an already-decoded picture refused by the output
budget returns `Dropped`. Thus a functioning decoder can remain "before its first picture" in the session for
its entire life. A later device loss calls `FailStream`, permanently avoids the codec and prevents the next
stream from exercising the factory's allowed device recreation. This reintroduces the visibility-dependent
recovery problem of N3 at the session layer, despite the factory's proof flag now being correct.

The regression decodes three real synthetic H.264 pictures under an empty clip and checks the decoder's
`_pictureSeen` and factory's `_proven`. It then signals the first loss and sets the decoder's failure flag to
simulate the next call failing. The factory remains enabled, but a subsequent H.264 STREAM_CREATE is unsupported
because the session avoided H.264. No driver reset or real removal occurs. Expose/propagate successful decoding
independently of presentation, or distinguish device-loss recovery from an incompatible format; do not interpret
every null-output `NoPicture` as success, since that result can also mean the decoder is buffering input.

Failing test: `HardwareVideoReviewTests.HiddenDecodedPicturesAllowANewStreamAfterAFirstDeviceLoss`.

### N6 - P2: Recheck avoidance when installing a decoder created outside the stream lock

Locations: `src/Spice.Session/SessionDisplay.cs:279-301,389-390,476-485`.

The only avoidance check is before `factory.Create`. While construction runs outside `_streamLock`, the pacing
wake or a UI pacing change can fail another stream's queued input and avoid this codec. Construction then
finishes and installs its decoder using the stale `unsupported` value. The new stream receives no invalid-stream
report and continues using a codec already avoided for the connection. A server without the preference capability
has no preference resend to stop it; even with that capability correctness depends on the server's timing.

The deterministic regression pauses the second factory creation, fails queued input on the first stream by
changing pacing, and resumes creation. Captured bytes contain the MJPEG preference and the correct invalid report
for the already-open stream, but omit the required report for the new stream. Recheck under `_streamLock` before
installation; dispose the newly created decoder and report that stream unsupported if avoidance changed.

Failing test: `VideoDecoderSessionTests.CodecAvoidedWhileADecoderIsBeingCreatedIsRecheckedBeforeInstallingIt`.

### N7 - P2: Serialize avoidance filtering with preference enqueueing

Locations: `src/Spice.Session/SessionDisplay.cs:202-206,220-241,476-485`.

Atomic bit updates and channel-send serialization do not serialize the filtering decision. A UI preference send
can filter H.264 while it is allowed, pause, then resume after a stream-thread failure has avoided H.264 and sent
MJPEG. Its stale span still includes H.264; the final avoidance read only appends MJPEG. The resulting last
request is `[H264, Mjpeg]`, restoring the avoided codec to first place on the server. New H.264 streams are
refused locally, so playback can remain ordinary images instead of the intended MJPEG fallback.

The test uses a paused read-only preference enumerator to control a preemption point in the actual sender. The
real setter passes an array; ordinary thread preemption between its filter and Send gives the same interleaving.
Captured messages end with `[H264, Mjpeg]` after the MJPEG resend and invalid report. The regression also allows
a fix to serialize the whole operation, checking the last preference rather than requiring one report order.
Make filtering plus enqueueing coherent with avoidance and preference changes; serializing only `display.Send`
or rereading the avoidance bits before appending MJPEG is insufficient.

Failing test: `VideoDecoderSessionTests.APreferenceFilteredBeforeAvoidanceCannotRestoreTheAvoidedCodecAfterTheResend`.

### N8 - P2: Bound the SPS reader to its NAL and validate the prefix used to select a format

Locations: `src/Spice.Session/VideoBitstream.cs:70,80-89,106-107`.

The SPS reader receives the rest of the access unit, including later NALs. For
`00 00 01 67 F4 00 00 01 68 FF`, the SPS ends immediately after profile 244, yet the parser consumes the next
NAL's start code/header as its missing SPS fields and returns a format. Bounds checks against the access unit
therefore do not establish bounds against the SPS. Find the next Annex-B prefix and restrict the reader to that
NAL; a truncated SPS must not borrow syntax from another NAL.

There are related prefix-validation gaps against H.264 7.3.1, 7.3.2.1.1 and 7.4.2.1.1:

- `00 00 03 AC` is accepted as an escape even though an emulation-prevention byte's following byte must be in
  0..3. The existing `EmulationPreventionBytesAreSkipped` positive test contains exactly this invalid sequence.
- The implementation claims to read through `bit_depth_chroma_minus8`, but reads only luma depth. Both a missing
  chroma-depth code and chroma depth minus eight equal to 7 are accepted as 8-bit High/4:2:0.
- `forbidden_zero_bit=1`, nonzero `reserved_zero_2bits`, and SPS ID 32 are accepted. These are fields in the
  inspected prefix, rather than validation of unused later SPS syntax.

All seven malformed table entries currently return true. A format match disables further format checks and a
refusal avoids the codec for the session, so invented or incomplete fields should not be treated as a declared
format. Validate the fields actually consumed, including both bit depths, and reject malformed escapes. This
does not require a full SPS validator or a scaling-list decoder. These are format-classification defects; the
review found no managed-memory escape, integer overflow or infinite loop and makes no native-exploit claim.

Failing tests: seven cases of
`VideoBitstreamFollowupReviewTests.MalformedFieldsUsedForTheFormatDoNotDeclareAFormat`.

### Parser, protocol and lifetime checks without additional findings

- **H.264 bounds/overflow:** the span scanner advances on every match, accepts three/four-byte Annex-B prefixes,
  and handles a start code at EOF. `Bit` checks the span before indexing and records overrun. `Ue` caps the
  leading-zero run at 16; its maximum computed value is 131070, safely within `int`, as is adding eight. Its
  comment's "values past 2^16" is imprecise, but there is no arithmetic overflow. The cap is adequate for the
  much smaller valid fields here; their individual ranges still need checking (N8).
- **High profiles/scaling lists:** profile/chroma/separate-plane/luma-depth field order agrees with the SPS prefix
  in 7.3.2.1.1. Both depth fields precede `qpprime_y_zero_transform_bypass_flag` and the scaling-matrix flag;
  scaling lists cannot shift any field this reader needs. Four added complete synthetic SPS cases cover High and
  High 4:4:4 with/without scaling matrices, including `delta_scale=-8` causing `nextScale=0`. They pass. Parsing
  or skipping the scaling lists would add unnecessary work for this prefix-only use.
- **VP9:** the 6.2 uncompressed-header prefix has marker `10`, then profile low bit, then high bit; their mapping
  is correct and all four profile tests pass. This is a profile reader, not a complete frame validator: it does
  not validate profile 3's reserved bit, the sync code or the remaining frame. Exact 10/12-bit depth and the
  subsampling of profiles 1/3 are not decoded. The returned 10-bit/4:4:4 values are placeholders; the current
  hardware policy uses only `Profile == 0`, so this does not admit unsupported profiles. Do not later use those
  placeholders as exact colour metadata without parsing `color_config`.
- **Garbage/allocation:** 6,208 deterministic short random buffers, alternating with forced SPS headers, and
  long-zero/truncated Exp-Golomb cases pass without exceptions; successful H.264 reads stay in the advertised
  chroma/depth ranges. The existing zero-managed-allocation parser and warm session/hardware tests pass. This is
  bounded regression coverage, not exhaustive fuzzing or native-decoder hardening proof.
- **Reports/already-open streams:** refusal calls the existing `FailStream`, disables ordinary reports, discards
  queued input and disposes the decoder. The race test activates normal reports first and confirms the failed
  existing stream gets exactly the zero-unique-ID, zero-frame, UINT32_MAX-drop invalid report. This agrees with
  the local spice-gtk `report_invalid_stream` (lines 1333-1352). Failure does not manufacture an ordinary report
  from the old report window. The missing report in N6 is for the new in-flight stream.
- **Connection state:** a new SpiceSession starts with no avoidance bits; a passing regression shares the factory
  between two sequential sessions and confirms H.264 creation/preference works in the second. The viewer creates
  a new session object in `MainWindow.Session.cs:23`. Reusing an already-connected/disposed SpiceSession is not
  a supported reconnect mechanism. Factory recovery flags are process-instance state, separate from the session's
  avoidance mask; this change adds no reset for them.
- **Creation/release safety:** on one generation, `_decoders++` and temporary AddRefs happen under the same
  factory lock as last-close release. A decoder being constructed therefore reserves the device before the old
  last decoder can close. Successful construction holds independent references; caught constructor failures
  dispose once, and repeated sequential Dispose calls are guarded. Loss drops factory references, while live
  decoders/construction temporaries retain theirs; old loss/picture notifications compare identity. No
  use-after-release was found in these paths. N4 is the cross-generation idle-retention defect. Concurrent
  Dispose calls on a single decoder are outside the session's serialized decoder contract.

Standards comparison above covers the named H.264 syntax/semantics and VP9 prefix. No normative specification
file was present in this checkout or its local reference clones, and none was fetched under the no-internet
constraint; this is not a fresh verification against a downloaded edition. The local spice-gtk reference was
read directly. No server source was fetched or executed.

### Decision 0037 and verification-document accuracy

0037 correctly keeps the separate-device, synchronous-MFT-only and preference-default choices provisional; this
review does not approve those human decisions. Its format gate and explicit MJPEG fallback describe the intended
normal path. Its unconditional last-decoder release and first-decoded-picture recovery claims need the fixes in
N4/N5, and the no-avoided-codec preference guarantee needs N6/N7. The "SPS as far as the bit depth" wording should
cover both depth fields and distinguish prefix reading from full bitstream validation (N8).

The live document clearly distinguishes the three builds, synthetic hardware decode from live MJPEG playback,
unavailable VP9/HEVC, untested clipping, and guest cleanup. Its Run B log is internally consistent with one
format refusal, an MJPEG preference, an invalid report, device release and later MJPEG streams. It supports the
normal-path fallback claim; it does not exercise concurrent creation, hidden-picture loss or cross-generation
release. The live screenshot, timestamped logs, CPU/GPU counters and cleanup were not independently rechecked in
this offline review. The historical Spice.Viewer.Tests result is the author's evidence, not a test run here.

Documentation corrections still needed:

- At verification line 14, `e44d536a??e64c` is not a SHA-256 digest. Supply the complete artifact hash or label it
  explicitly as unavailable/abbreviated; it currently cannot identify the tested zip reproducibly.
- Lines 21, 132-136 and decision 0037 generalize from the tested encoder configuration to all hardware or untested
  guests. Phrase those as the capabilities probed here and expectations from the cited pipeline. VP9 profile 1,
  another guest's negotiated encoder format, and lack of every possible hardware decoder were not measured here.
- The 338 MB versus 294/300 MB private-byte figures suggest the device-retention cost but do not isolate 45 MB
  of driver memory: B1 Automatic also differs in playback history (three plays). Retain the useful measurements
  and qualify the attribution. The normal-path idle figures do not establish release after the loss ordering in N4.

### Follow-up validation

All builds/tests used restored local assets (`--no-restore`) and ran sequentially. Only the two window-free test
projects were executed. The new regressions intentionally remain enabled and failing until production fixes land.

| Check | Result |
|---|---|
| Targeted existing Replay video tests before additions | 30 passed |
| Targeted Replay video tests with additions | 40 passed, 9 failed |
| Full Spice.Replay.Tests | 368 passed, 9 failed (seven N8 cases, N6, N7) |
| Final parser-table/garbage/scaling/profile recheck | 9 passed, 7 failed (N8) |
| Full Spice.Video.Tests | 33 passed, 2 failed (N4, N5), 2 skipped (VP9/HEVC unavailable) |
| Earlier N1-N3 hardware regressions | Passed in the Video run |
| `git diff --check` | Clean |

No Spice.Viewer.Tests, viewer/window, VM, Proxmox operation, secret file, network access, package build or physical
device-loss test was used. No production, decision, verification or golden-image file was edited.

## Response (follow-up)

Claude, 2026-10-31. All five findings are fixed in production code. None of Codex's eleven new cases was changed;
each one failed for the reason it gives. One of my own tests was wrong: the positive case in
`VideoBitstreamTests.EmulationPreventionBytesAreSkipped` used `00 00 03 AC`, the malformed escape N8 names. I
replaced it with `TheLargerBitDepthIsTheFormatsDepth`. A positive escape case can't be built inside this prefix: the
byte after `03` is at most 3, so `seq_parameter_set_id` would start with six zeros, which puts it past 31.

- **N4:** `HardwareVideo.OnDecoderClosed` no longer takes a device. When the open-decoder count reaches zero,
  whichever generation the last decoder belonged to, the current device and manager are released (still under
  `_lock`, with creation's reservation unchanged).
- **N5:** a new `IVideoDecoder.HasDecoded` (default false; `MfVideoDecoder` returns `_pictureSeen`) reports any
  decoded picture, shown or not. `FailStream` avoids the codec only if the stream has shown no picture and its decoder
  has decoded none. A null-output `NoPicture` is still not taken as success; only the decoder's own flag counts.
- **N6:** `STREAM_CREATE` checks avoidance again under `_streamLock` before installing a decoder made outside it. If
  the codec was avoided meanwhile, the decoder is disposed and the stream is reported unsupported. Avoidance only
  happens under `_streamLock`, so this check is enough.
- **N7:** a `_preferenceLock` now covers each preference's filtering and enqueueing (`SendPreferredVideoCodecs`, the
  `PreferredVideoCodecs` setter), and also avoidance's bit update and resend. Lock order is `_streamLock`, then
  `_preferenceLock`. The avoidance bits are written only under the new lock.
- **N8:** the SPS reader is bounded to its NAL, up to the next `00 00 00`, `00 00 01` or `00 00 02`. It rejects:
  - forbidden_zero_bit set;
  - nonzero reserved_zero_2bits;
  - an SPS ID over 31 or a chroma_format_idc over 3;
  - a byte over 3 after an emulation-prevention byte.

  It now reads `bit_depth_chroma_minus8` too. Both depths must be 0..6, and the reported depth is the larger one.
  The VP9 placeholders are documented as such.
- **Documentation:**
  - Decision 0037 describes the bounded prefix check, `HasDecoded`, the recheck and the serialization. It also
    describes release regardless of device generation, and qualifies the 4:4:4 claims as the profiles probed here.
  - The verification note now gives the full zip SHA-256. It qualifies the hardware, VP9 and other-guest
    generalizations, and the B1 memory attribution.
  - `tools/package/README.txt` no longer says graphics cards in general can't decode 4:4:4.

Validation, run sequentially and window-free:

| Check | Result |
|---|---|
| `dotnet build SpiceViewer.sln --no-incremental` | 0 warnings, 0 errors |
| Spice.Replay.Tests | 377 passed (three runs) |
| Spice.Video.Tests | 35 passed, 2 skipped (VP9/HEVC decoder unavailable) |
| Spice.Wire.Tests | 27 passed |
| Spice.Native.Tests | 24 passed |
| Pve.Term.Tests | 115 passed |
