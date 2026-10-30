# 0030. Clock pacing holds audio to the guest clock by packet timestamps

- Status: Accepted (by the human, 2026-10-30, with the timing budget and the render worker exit limitation (E4) below;
  E4 superseded by [0031](0031-the-audio-stream-outlives-its-output-device.md))
- Date: 2026-10-30
- Decided by: the human ("A/V sync 做，clock 模式延遲音訊", 2026-10-30: do A/V sync, by delaying audio in `clock`
  mode); the mechanism by the agent (spec silent), revised after Codex's review
  ([../reviews/2026-10-30-clock-av-sync.md](../reviews/2026-10-30-clock-av-sync.md), findings A1-A3, B1-B2, C1-C2,
  D1, E1-E5) and the live test ([../verification/2026-10-30-clock-av-sync.md](../verification/2026-10-30-clock-av-sync.md),
  F1-F5)

- **Context:** 0025's `clock` mode holds each video frame until the guest's multimedia clock (MAIN_INIT and
  MAIN_MULTI_MEDIA_TIME, extrapolated) reaches its timestamp, but audio played on arrival and PLAYBACK_LATENCY was
  ignored, so in `clock` mode video ran behind its sound by the server's clock lead (about 400 ms). spice-server stamps
  audio packets and video frames with its own mm-time when it produces them, and gives the client a clock that lags
  behind it by its latency (`MM_TIME_DELTA`, raised later from stream reports, announced by a new MULTI_MEDIA_TIME).
  PLAYBACK_LATENCY carries a target audio buffer depth. (spice-server and spice-gtk are not in refs/; their behaviour
  here is from their source as remembered and has not been checked against them. Live on win7 the server sent
  PLAYBACK_LATENCY of 4-10 ms about every 3.3 s while streaming video, then 0, and never rebased the clock.)
  - spice-gtk makes audio the master clock: the audio backend keeps about PLAYBACK_LATENCY of audio buffered (a default
    before the first one), the playback channel periodically asks the backend how much it still holds, and sets the
    session's mm-time to the last packet's stamp minus that delay. Video frames are then scheduled against that clock,
    and a large jump in it resets the streams.
- **Decision** (`src/Spice.Media/PcmQueue.cs`, `src/Spice.Session/SessionPlayback.cs`,
  `src/SpiceViewer/Audio/AudioOutput.cs`): in `clock` mode, audio is scheduled by its packet timestamps against the same
  guest clock the video waits for; the clock stays driven by the main channel.
  - The session gives every PLAYBACK_DATA packet (pcm, delay, gap) with `AudioData`. Delay: in `clock` mode with a
    seeded clock, its stamp minus the guest clock now (ms, may be negative); otherwise `PcmQueue.Unsynced`. Gap: how
    far its stamp is past the end of the previous packet's (stamp plus length), 0 for the first packet after
    PLAYBACK_START and when unsynchronized. The end is tracked exactly (whole ms plus a remainder in 1/rate ms) and
    follows each stamp by whole ms only, so the sub-ms rounding of whole-ms stamps on packets of fractional length (256
    frames at 44.1 kHz = 5.805 ms) is never reported as a gap (second re-review C2). The mode is read per packet, so a
    change in the menu applies from the next packet.
  - The guest clock base (guest time and local time of the last MAIN_INIT or MULTI_MEDIA_TIME) is one immutable
    object published with a volatile write, and every reader (stream pacing, playback) extrapolates from one read of
    it, so the playback thread never combines one base's guest time with another's local time (review A1). It
    allocates once per base, a control message.
  - The renderer's ring moved into `PcmQueue` (testable, no WASAPI). An unsynchronized write behaves as before: 200 ms
    ring, 20 ms prime after an underrun, more than 100 ms queued trimmed to 40 ms.
  - The queue keeps the held timeline explicitly (review A2, re-review B1 and B2): a *stamp lead*, the source gaps
    (sum of the packets' gaps) not yet laid out as silence, so the queued audio's stamps end that far before the next
    packet's. Per synchronized write, with the delay clamped to 40 ms .. 2 s and "queued" being the ring plus what
    the device still holds (its padding plus everything the last fill gave it, plus `IAudioClient::GetStreamLatency`):
    1. The packet's gap is added to the stamp lead.
    2. *Clock shift* = delay − queued − stamp lead: how far the whole held timeline is off the guest clock (a new
       clock base, drift, an underrun's silence, entering clock pacing). This is logical time and unbounded: a 5 s
       source pause is 5 s of lead, reconciled in full; only the samples inserted or removed are bounded by the ring
       (second re-review C1).
    3. The stamp lead within ±`GapJitterMs` (10 ms) stays pending; it is the packet's offset from its stamp until
       later gaps move it out of that band. Stamps jitter around contiguous audio, and laying out each +3 ms but not
       each −3 ms would drift the timeline into repeated corrections; a pending lead is part of the timeline, so it is
       never later mistaken for a clock shift.
    4. The whole shift is corrected, as queued video frames are moved on a rebase, when it could put any packet out of
       the 40 ms budget (`ToleranceMs`). With audio held, each held packet may keep its own pending lead within
       ±10 ms, so the shift is corrected beyond 40 − 10 = 30 ms (third re-review D1: the newest packet's phase alone
       can hide an older one's error). With nothing held, only the new packet counts: shift plus its pending lead
       beyond 40 ms. Early: silence before all held audio; late: dropping the oldest held audio, and with nothing
       held, giving up stamp lead (an expired pause), then cutting the start of the packet. Audio already in the
       device stays.
    5. A stamp lead beyond +10 ms is laid out exactly and in full as silence between the held audio and the packet;
       beyond −10 ms (stamps overlapping) the start of the packet is cut. Laying out all of it, rather than capping it
       at the packet's deadline, keeps every held packet's own phase within ±10 ms, which step 4's 30 ms gate relies
       on (fourth re-review E1); a shift left uncorrected (within the gate) then applies to this packet as to the rest
       of the held audio.

    Unsynchronized writes reset the stamp lead. While synchronized the ring is not trimmed. A rebase is applied with
    the next packet (10 ms later while playing), not on its own: a separate retime from the main thread could race the
    packet's delay.
  - **Timing budget (accepted by the human, 2026-10-30).** Every synchronized sample still in the queue is at most 40 ms
    off its stamp-derived time: the uncorrected shift (at most 30 ms while audio is held) plus its packet's pending
    lead (at most 10 ms); after a correction, at most 10 ms. Real source pauses of 10 ms or less are not laid out on
    their own: following audio plays up to 10 ms early until later gaps add past 10 ms. Larger pauses are laid out
    exactly. The 10 ms jitter allowance is one period of QEMU's audio timer (its default), which completes the frames
    spice-server stamps, so a late tick moves one stamp by less than a period; this is remembered, not measured on our
    guests. Outside the budget: the device-depth estimate (stale by up to one device period, about 10 ms), whole-ms
    stamps and the guest clock's extrapolation from `TickCount64` (about 16 ms steps), and packets late or beyond 2 s,
    which are clamped.
  - **Device latency (live F3).** The win7 test host's endpoint reported `GetStreamLatency` 0, and loopback mixed
    audio 17-26 ms after the queue aimed for; video in `clock` mode was drawn 17-21 ms after its stamp. Both sides lag
    the guest clock by the `TickCount64` extrapolation (up to one 15.6 ms tick), video adds its draw and audio the
    engine's period, so the two mostly cancel (measured sync error -14 .. +27 ms). We don't add an estimate such as
    `GetDevicePeriod` when the stream latency is 0: it would move audio ~10 ms ahead of video on that host while the
    clock's tick lag remains. Revisit both together if the guest clock gets a finer time source.
  - **End of stream (live F1).** PLAYBACK_STOP in `clock` mode no longer discards the audio held back (short sounds
    were never heard; a 1 s tone lost its last ~0.4 s). The viewer calls `AudioOutput.EndOfStream`: if the queue is
    synchronized (`PcmQueue.BeginDrain`), the held audio plays out at its schedule, no silence is padded, the last
    samples need no prime cushion, and the render thread closes the device itself on the first buffer event that finds
    the queue and the device empty (`TryFinishDrain`): no timer, and the stream was open anyway. Unsynchronized
    streams still close at once, as before. A PLAYBACK_START meanwhile in the same format continues on the open device
    (`TryContinue`, atomic with the drain's end): the held audio keeps its schedule, and as long as any of the previous
    stream's samples are still in the queue (counted at the continuation), each new packet is placed at its own
    deadline after the held audio, or right after it if that is already past, with no correction that could move the
    previous stream (fourth re-review E2); after that the new stream is corrected as usual. Another format, or a drain
    already finished, restarts the device. A drain that ends in the pre-roll of a reopened device (default device
    changed) starts no client (E3). A disconnect still stops at once; samples a close discards are counted
    (`DiscardedSamples`, in the close-of-stream log).
  - **Render worker exit (E4).** Every way the render thread leaves (stop, end of a drain, an unrecoverable device
    failure, an open failure) closes the queue under the queue's lock before the worker retires: `StopCore` and
    `TryFinishDrain` close it themselves, the failure paths close it before their terminal log. A same-format START
    after that fails `TryContinue` and restarts, joining the old worker, instead of continuing onto it. A START that
    continues while a terminal failure is in progress (between the failing WASAPI call and that close, which includes
    logging and releasing the client, so it has no fixed bound) loses that stream, as the playing stream does; the next
    START reopens the device. Accepted as a limitation (review, fifth round). No lock is taken by the
    worker that `StopCore` holds while joining it. *Superseded by [0031](0031-the-audio-stream-outlives-its-output-device.md):
    a device failure no longer ends the stream, so such a START keeps it; the rule that every exit closes the queue
    first still holds.*
  - The render thread hands audio to the device with `PcmQueue.Transfer`, which removes it from the ring and records
    the device's new depth under one lock, so a concurrent synchronized write sees each frame once (review A3). A fill
    that takes nothing from the queue publishes the device depth alone. Switching pacing off flushes the hold and bumps
    a queue epoch; a `Transfer` whose `ReadyFrames` choice predates the switch takes nothing (writes silence), so it
    can't consume the new, still-priming audio (E5).
  - Network jitter needs no correction: a packet that arrives late finds the queue drained by the same amount, so the
    error doesn't change. Corrections happen when playback starts or the mode switches to `clock` (one gap of about
    the clock lead), when the server rebases the clock (a new latency), after an underrun's silence, and for drift
    between the device clock and the guest clock (one 40 ms step each time it accumulates).
  - A late packet (delay under 40 ms, e.g. network delay longer than the clock lead) plays with a 40 ms cushion, as
    unsynchronized audio does; the matching video frames are dropped as late by 0025.
  - Switching away from `clock` turns the next write unsynchronized: the held audio is dropped (counted, one skip) and
    the queue primes 20 ms again, so playback settles near the depth of a fresh `off` stream (live F2: trimming to
    40 ms left it about 60 ms later until the stream restarted). The device may still hold up to 40 ms of the held
    audio while the new audio primes, so the new depth can be up to about 10 ms more than a fresh stream's 30 ms
    (not measured). Switching to `clock` inserts the gap on the next packet.
  - PLAYBACK_LATENCY is parsed, logged and exposed as `SpiceSession.PlaybackLatencyMs`, but does not set the depth.
  - The ring grows (once per larger delay, up to 2.2 s of audio: about 420 KB at 48 kHz stereo) when a synchronized
    delay needs it, and returns to 200 ms at the next playback start. Steady synchronized playback, corrections
    included, allocates nothing: corrections are counted and logged (Debug) only when the stream closes (tests in
    `tests/Spice.Replay.Tests/AudioSync*Tests.cs`); no timer or wake is added.
- **Reason:** stamps against the clock the video already uses line sound and video up by construction (both are
  presented when the guest clock reaches their stamps) and absorb network delay without knowing it. spice-gtk's way
  would change the video side: the clock would depend on whether audio plays, jump when it starts or stops, and need the
  stream resets spice-gtk has. PLAYBACK_LATENCY alone can't drive the depth: live, while streaming video, the server
  sent values of 4-10 ms (live F5, contradicting the remembered "sent only when not streaming"), far from the ~400 ms
  lag the video waits out, which the clock base carries. Costs: in `clock` mode all sound, not just sound with video,
  is about 0.4 s late, and a short sound keeps the device open until it has played; corrections are audible as a
  short gap or skip; the stream report still sends 0 for audio_delay; `GetStreamLatency` is documented as the
  stream's maximum latency, so counting it as delay is an approximation. Live on win7 (verification record above):
  sync error -1.5 ± 5.5 and +8.0 ± 7.6 ms in steady state, one correction per stream, drift about 0.065 ms/s; the
  end-of-stream and switch fixes (F1, F2) need a live re-check.
