# Stream-tail and hidden-frame review

Codex, 2026-10-30. Normal correctness review of the uncommitted stream-tail fix against main `1f4b8bc`, including the stream-tail
change, agent-reply wording, updated ADR 0025 and changed tests. Read the live-options record; did not repeat
or incorporate another worktree's ongoing live check.

**Verdict: no P1/P2 issue confirmed. The hidden-frame optimization and destroy-time draw are correct for the
documented policy. The two existing test expectation changes are justified. Minor qualifications below do
not warrant failing tests.**

## Server ordering: plausible and supported by source

Checked upstream spice-server **0.15.2** source as distributed by Debian, rather than relying on remembered
behavior. This is evidence of a plausible implementation path, not a message capture or identification of
the live host's server build.

- `streams_update_visible_region` excludes an opaque drawable's region from each affected stream's visible
  region and clip, then queues a clip update. `current_add` performs this update before the covering drawable
  is added to the client pipe. Full coverage can therefore produce an empty STREAM_CLIP before the desktop
  draw. [display-channel.cpp](https://sources.debian.org/src/spice/0.15.2-1/server/display-channel.cpp)
- The timeout constant is one second.
  [video-stream.h](https://sources.debian.org/src/spice/0.15.2-1/server/video-stream.h)
- `video_stream_timeout` gracefully detaches an expired stream, then stops it. Graceful detach sends an
  empty clip and, where needed, an ordinary drawable or surface-image repair; stop queues STREAM_DESTROY.
  Thus a stream can remain alive after an earlier covering draw. Also, a normal timeout can make a
  destroy-time flush invisible because its clip was already emptied.
  [video-stream.cpp](https://sources.debian.org/src/spice/0.15.2-1/server/video-stream.cpp)

This supports the live F2 explanation: the old implementation could decode a queued tail and blit zero
rectangles after coverage changed. The observed correct desktop should stay correct; those hidden frames
should not be forced back onto it. The commit saves decoding and explains their absence in the log. The
specific live sequence remains unconfirmed at the message level, as the updated record correctly says.

## Destroy-time drawing and the changed assertions

`SessionDisplay.cs:225-247` removes the stream under `_streamLock`, discards older queued frames through
the existing counted-drop path, and draws only the newest with its saved destination and current clip.
`DrawHead` returns the encoded buffer in a finally block; the outer finally releases the decoded frame,
ends the stream and updates the shared wake. A concurrent pacing wake cannot draw it again.

Drawing now rather than after STREAM_DESTROY is consistent with the chosen policy: subsequent channel
draws run afterwards and remain authoritative. Earlier desktop draws are protected by the clip updates
described above. New tests verify both an entirely covered stream and a one-column-visible stream with
the desktop draw **before** destroy; neither redraws the excluded desktop pixels. The implementation's
test separately checks a draw **after** destroy and absence of any later wake-driven overwrite.

This is a client policy choice, not a protocol requirement to reconstruct missing video at destroy. The
upstream graceful-detach path can already supply a lossless repair, and an empty clip makes the flush a
no-op. ADR 0025 explicitly records the cost of drawing a still-visible newest frame before its clock deadline.
I found no plausible new stale-overdraw case with the server's current-clip ordering.

The two changed `StreamPacingTests` expectations are appropriate:

- `RemovingTheStreamDiscardsQueuedFramesAndCancelsTheWake`: single destroy now adds one immediate draw;
  destroy-all and ID recreation retain their previous discard behavior. Both immediate and post-advance
  draw counts are asserted, and queued bytes/wake cancellation are still checked.
- `OtherStreamsKeepTheirWakeWhenOneIsDestroyed`: destroy adds one draw from the removed stream, so the
  other stream's later draw moves the final total from three to four. Its exact +28 ms wake remains asserted.

These assertions implement the documented behavior without weakening cancellation or independent-stream
checks. No earlier tests or expectations were changed during this review.

## Visibility, counting and allocation

`Canvas.IsFrameVisible` (`Canvas.cs:684`) tests the intersection of destination, surface bounds and **any**
clip rectangle. `TypeNone` means unbounded clip; a zero-rectangle rect clip is empty. This matches whether
`BlitFrame`'s clipping would produce any rectangle. Overlapping clips need no normalization for this boolean
question. One visible pixel is sufficient, including a destination crossing the surface boundary or a later
visible rectangle after an off-surface one. New pixel-level tests exercise these cases with sized frames.

The early return at `SessionDisplay.cs:583` increments Hidden without opening the JPEG decoder. It does not
increment Drops; Frames still counts receipt, preserving arrival-window reporting. Unsupported codecs keep
their existing drop behavior. Skipping an independent MJPEG image does not invalidate later frames.

The visibility predicate uses value-type rectangles, dictionary lookup and the existing canvas lock, with
no new buffer/delegate allocation or timer. Existing warmed visible-frame allocation tests pass in Off,
Spacing and Clock. A new warmed hidden-path test measures **0 managed bytes for 1,000 frames**, checks the
Hidden delta and confirms no decoder was created. This is an allocation check, not a decode-speed benchmark.
Destroy logging is control-path work; decode buffers and the queue pool retain their existing reuse/cleanup.

The agent-reply change matches the official protocol: result 1 is success and 2 is error. The new Debug
success and Info failure wording avoids presenting a successful resize as an error; both tests pass.
[SPICE agent protocol](https://spice.pages.freedesktop.org/spice-space/agent-protocol.html)

## Minor notes

- **Counter taxonomy:** Hidden counts frames that reach the visibility check. Late-arrival and superseded
  frames still take earlier drop paths. By inspection, three queued empty-clip frames serviced in one late
  wake become two superseded drops and one hidden frame; destroy has the same newest-only classification.
  This limits the universal wording about hidden frames, but does not corrupt pixels. No consequential
  server rate-control regression was established, so this is a minor accounting/documentation note, not P2.
- The log says "hidden by its clip" even when the surface is absent or the destination is wholly off-surface;
  ADR 0025 describes the broader condition. The removal test's old "DiscardsQueuedFrames" name also now
  understates its single-destroy branch. Neither warrants a failing test.

## Validation and scope

| Window-free check in this worktree | Result |
|---|---|
| Existing Replay suite before review additions | **214 passed**, 0 failed, 0 skipped |
| Six added cases in `StreamTailReviewTests.cs` | **6 passed**: three surface/clip intersections, two earlier-desktop/destroy orderings, one hidden-path allocation check |
| Full `tests/Spice.Replay.Tests/Spice.Replay.Tests.csproj --no-restore` after additions | **220 passed**, 0 failed, 0 skipped |
| `tests/Spice.Native.Tests/Spice.Native.Tests.csproj --no-restore` | **24 passed** |
| `tests/Spice.Wire.Tests/Spice.Wire.Tests.csproj --no-restore` | **13 passed** |
| `git diff --check` | Clean |

Added this record, its index row and the six passing review cases. No confirmed P1/P2 bug required a failing
test. No production code, ADR, earlier assertions, verification record or golden images were edited. All
review changes are uncommitted. No viewer test project, viewer/UI/VM access, artificial CPU load, commits
or `/daybreak` were used. Tests exercise in-memory protocol messages and a manual clock; there is no new
claim about live presentation, message capture, physical timing or the other worker's re-check.
