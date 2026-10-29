# 0025. Video pacing is a setting; by default frames are drawn on arrival

- Status: Accepted (the human, 2026-10-30: default `off`, for the best performance and the lowest latency; the
  user can switch to a mode that follows the guest clock)
- Date: 2026-10-30
- Decided by: agent, from Codex's review finding that MJPEG frames were drawn on arrival; the three modes as a
  setting by the human (2026-10-30)

- **Context:** the spec (Video and audio) says "display frames against the guest's multimedia clock, as SpiceSee
  does". Frames were decoded and drawn the moment they arrived, so a burst overwrote frames before any present and
  wasted decodes. The references differ on frames that arrive *early*:
  - SpiceSee (`StreamPlayer.swift`) has no jitter buffer: a frame more than 80 ms behind the clock is dropped before
    decode, and every other frame is decoded and shown at once. Before the clock is seeded nothing counts as late.
  - spice-gtk (`channel-display-mjpeg.c`) queues frames and draws each at its mm-time with a one-shot timer, drops
    frames already behind the clock (0 ms tolerance), and drops the queue when timestamps go backwards.

  spice-server gives the client a clock about 400 ms (`MM_TIME_DELTA` in reds.c, more once it raises the latency)
  behind the time it stamps frames with, and tells the client to buffer audio by the same amount. (spice-server is
  not in refs/; this is from its source as remembered and has not been measured live. A live check would log each
  frame's stamp minus `MultimediaTime` on arrival.) Our audio plays on arrival and
  ignores PLAYBACK_LATENCY, so waiting for each frame's absolute mm-time would show all video, including streamed
  window drags, about 400 ms late and that far behind its sound.
- **Decision** (`src/Spice.Session/SessionDisplay.cs`): `SpiceSession.VideoPacing` selects one of three modes; it
  can change while a session runs. The viewer's global `videoPacing` setting (`spacing`, `clock`, `off`; anything
  else reads as `off`) and the window menu's "Video pacing" submenu set it (see 0024).
  - **`off` (default)**: every frame is decoded and drawn on arrival: no queue, no wake, no drops (the behaviour
    before pacing). The human chose it as the default (2026-10-30) for the best performance and lowest latency.
  - **`spacing`**, as described in the rest of this list.
  - **`clock`**: spice-gtk's pacing. Each frame waits until the guest clock reaches its timestamp (at most 2 s, against
    a clock far off the stamps); a frame already behind the clock on arrival is dropped before decode (0 ms
    tolerance, as spice-gtk drops a late MJPEG frame). A new clock base from MAIN_MULTI_MEDIA_TIME reschedules the
    queued frames. Until our audio honours PLAYBACK_LATENCY this puts video behind its sound by the server's clock lead
    (the unverified ~400 ms above), and adds that delay to everything streamed.
  - **Switching** reschedules queued frames at once under the new mode, keeping deadlines non-decreasing: `off` makes
    them all due (the newest is drawn, older ones are dropped as superseded), `clock` moves them to their timestamps,
    `spacing` caps them at 50 ms from now. If nothing remains queued the wake is cancelled.
  - Late (spacing): a frame more than 80 ms behind the guest clock on arrival is dropped before decode and counted in the
    stream report's drops (SpiceSee's threshold and drop point).
  - Early (spacing): a frame is held only to restore its spacing from the previous frame of its stream:
    deadline = clamp(previous deadline + timestamp step − 2 ms, arrival, arrival + 50 ms). A frame that comes after a
    normal gap, or after the first frame of a stream, draws on arrival as in SpiceSee. Frames that come in a burst are
    spread out as stamped, for at most 50 ms; the 2 ms per frame lets delay added by a burst drain in about a second.
    A timestamp step backwards or over 1 s starts a new timeline: queued frames are dropped (counted) as in spice-gtk.
  - A wait of 4 ms or less draws at once. When the next queued frame is also due, the earlier one is dropped
    (counted) without decoding, so a late wake decodes only the newest frame.
  - Without a seeded clock (no MAIN_INIT yet) every frame draws on arrival, as SpiceSee assumes "on time".
  - Bounds: 32 queued frames per stream (room for clock mode's lead at 30 fps; the oldest is dropped, counted) and
    64 MiB of queued buffers across streams, counted by capacity. Queued bytes are copied out of the reused receive
    buffer into a per-session pool of power-of-two buffers (at most 32 and 64 MiB kept, released when the last stream
    ends); a spare is reused only if at most one size larger than needed and within the queue budget, so a large
    spare never crowds out small frames. Steady streaming allocates nothing in any mode.
  - Stream reports count each frame, and each drop, in the report window the frame arrived in. A queued frame dropped
    after its window was sent goes only into the lifetime total in the log, as spice-gtk keeps playback drops out of
    its arrival reports (review R3, 2026-10-30).
  - One wake for the session: a one-shot timer armed for the earliest queued deadline and cancelled when the queues
    empty, so an idle session never wakes. Stream destroy, destroy-all, re-create of an id and session end (disconnect
    or dispose, via the session's cancellation token) drop the queued frames and cancel the wake; a wake already in
    flight then does nothing.
  - Threads: stream state is guarded by one lock, taken before `Canvas.Sync` and never after it. A deferred frame is
    decoded on the timer's thread-pool thread and drawn with `Canvas.BlitFrame` exactly as an immediate one, so it
    raises `Invalidated` under `Canvas.Sync` from that thread.
  - Ordering: draw commands are applied as they arrive, even while an earlier stream frame waits, and that frame is
    drawn over them when it comes due (spice-gtk accepts the same). It is drawn at the destination it arrived with and
    the stream's current clip, so a STREAM_CLIP sent for a window raised over the video keeps the frame off it, and
    STREAM_DESTROY (sent before the server redraws the area) discards it.
  - Tests replace the clock (`SpiceSession.PacingClock`, internal) with a manual one; replays that never advance time
    stay deterministic, and sessions without MAIN_INIT draw on arrival as before.
- **Reason:** `off` has the lowest latency and does the least work per frame, which the principles rank first; a
  burst may overwrite a frame before it is presented, wasting its decode. `spacing`
  removes that bunching for at most 50 ms of delay. `clock` is there for A/V sync: once audio honours
  PLAYBACK_LATENCY it should give lip sync; until then it trades bunched frames for a fixed lag behind the sound. Costs: jitter longer than 50 ms is only partly smoothed; frames are not synchronized with audio
  beyond both being shown on arrival; the stream report still sends 0 for last_frame_delay and audio_delay.
