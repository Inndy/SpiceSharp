# 0031. The audio stream outlives its output device

- Status: Accepted (design approved by the human, 2026-10-30); re-checked live on win7 ([../verification/2026-10-30-clock-av-sync.md](../verification/2026-10-30-clock-av-sync.md), "Re-check: audio detach")
- Date: 2026-10-30
- Decided by: the human ("go", 2026-10-30, on the design: the stream lives from PLAYBACK_START to PLAYBACK_STOP, the
  device attaches and detaches, a reopen fails only if the device never delivered, backoff then event-only waits);
  the details below (the virtual device, the limits, the notification dedupe) by the agent. The approved design
  ended a run of failures at a device's first buffer; that let a device failing right after it reopen every 50 ms
  forever (energy rules), so a run now ends after 1 s of playing (agent, at the coordinator's review)

- **Context:** until now the output device owned the stream. When WASAPI failed, or the reopen limit was reached (3
  reopens unless the stream had been open for 5 s), `AudioOutput` closed the `PcmQueue` and the SPICE playback stream
  ended until the guest's next PLAYBACK_START. Live (verification
  [2026-10-30-clock-av-sync.md](../verification/2026-10-30-clock-av-sync.md), G5) two default-device changes 0.7 s
  apart ended the stream and discarded its held audio, because each change caused two reopens. A long stream (music)
  stayed silent for the rest of its life after a give-up, and 0030 had to accept E4: a START continuing while a
  terminal device failure was in progress lost its stream. Energy rules (spec): no timers or polling while idle, no
  hot-path allocation.
- **Decision** (`src/Spice.Media/PcmQueue.cs`, `src/SpiceViewer/Audio/AudioOutput.cs`,
  `src/SpiceViewer/Audio/DeviceNotifications.cs`):
  - **Stream and device are separate.** The stream (the queue and the render thread) lives from PLAYBACK_START to
    PLAYBACK_STOP, or to the end of its drain under clock pacing. It accepts writes and keeps its timeline with or
    without a device. The render thread attaches to the default render device (console role) and on a failure or a
    default change detaches (`PcmQueue.Detach`); the stream goes on. Only a failure before any device can be tried
    (`CoInitializeEx`, the device enumerator) still ends the stream, as before.
  - **While detached, a virtual device plays in real time.** It first plays what the real device still held
    (`DeviceQueuedFrames` at the detach), then the queue from its head. Nothing wakes for it: what it would have
    played expires lazily, on each `Write` and on `Attach`, from the system tick (`Environment.TickCount64`), so a
    reattached device plays from "now". Under clock pacing the held audio therefore stays on its stamps (a write while
    detached sees the queue as a playing device would leave it, so it needs no correction), and where the virtual
    device ran dry the timeline slips as after an underrun and the next packet corrects it. Unsynchronized audio is
    also cut to 40 ms once it exceeds 100 ms while detached (the trim `ReadyFrames` applies on reattach), so it never
    grows past that. The first device of a stream is not "detached" while it opens (a short sound keeps its start);
    a failure there detaches. Reopening after a default change goes through detach and attach too, so its open time
    expires instead of making the audio late. If the new device opens before the virtual device has finished the old
    device's audio, the rest becomes silence ahead of the held synchronized audio (the new device starts empty), so
    that audio keeps its stamps even in a drain, which has no later packet to correct it (Codex review
    [2026-10-30-audio-detach.md](../reviews/2026-10-30-audio-detach.md), H1: it played up to 60 ms early). With
    nothing held nothing is inserted, so an empty drain still ends in the pre-roll (E3); unsynchronized audio just
    plays from now.
  - **Reattach policy.** A default change reattaches at once and is not a failure. A notification whose default is
    the device in use is ignored (see the double reopen below). Every other detach is a failure: an open that failed,
    a device that never asked for a buffer after starting (2 s without a buffer event), or a device that failed
    later. A run of failures ends only when a device has kept asking for buffers for 1 s after its first one, judged
    from timestamps on its buffer events (no timer; the tick is read on buffer events only while a run is counted).
    So a device that fails soon after each first buffer goes through the same backoff and then the event-only wait,
    instead of reopening every 50 ms forever. After a failure the thread waits
    50 ms, doubling to at most 2 s, for 10 s from the first failure of the run (the last wait is cut to end at 10 s,
    and the attempt made then is the last); after that, or at once if there is no
    render device at all (`E_NOTFOUND`), it waits only for events, with no timeout: stop, a default change, a device
    added or becoming active (`IMMNotificationClient::OnDeviceAdded`, `OnDeviceStateChanged` to active, which now set
    an event), or a same-format PLAYBACK_START. Stop or a notification ends any wait at once. While detached and past
    the budget the thread makes no wakeups.
  - **STOP and START while detached.** `BeginDrain` refuses while detached, so PLAYBACK_STOP ends the stream at once
    (nothing could be heard). A drain that loses its device ends when the render thread would wait for a new one
    (`TryEndDetachedDrain`, atomic with `TryContinue`); a default change during a drain still reopens and finishes the
    drain on the new device. A same-format PLAYBACK_START while detached continues the stream (`TryContinue`, as for
    a drain: the held audio keeps its schedule, the new stream starts a new timeline after it) and asks the render
    thread to try the device again; another format restarts. This supersedes 0030's E4 limitation: a START that
    continues while the device fails keeps its stream.
  - **Logging** on transitions only: the first failure of a run (`Warn`, with the error; not when a drain then ends,
    as nothing goes on without output), entering the event-only
    wait (`Warn`, once per run), a default change (`Info`), recovery (`Info`, "output back after N ms", once the
    device has played for 1 s) and each successful open (`Info`, "playing ...", so at most one per backoff step);
    never per retry. The stream-close line (Debug) adds how often and how
    long the stream was without a device and the frames that expired meanwhile, and the console-role default-change
    notifications received and how many named the device in use.
  - **Double reopen (G5).** One callback sets the auto-reset event once and wakes one reopen, so the two reopens per
    change seen live mean two console-role notifications arrived per change. The test helper set the console and the
    multimedia roles separately (`IPolicyConfig::SetDefaultEndpoint` twice); Windows keeps those roles linked, and
    may announce the console role for each call. That is not confirmed. Either way the render thread now clears the
    event before it asks for the default device, and on a notification compares the default's endpoint ID with the
    device in use (`IMMDevice::GetId`; the strings are compared in place and freed with `CoTaskMemFree`): a repeat is
    counted and ignored.
  - Unchanged from 0030: the timing budget, `Transfer` under one lock, the queue epoch, protected samples after a
    continued stream, the drain and E3 (a drain that ends in a reopened device's pre-roll starts no client). 0030's
    reopen limit (`MaxReopens`, the 5 s window) is gone.
- **Reason:** the guest's stream and the host's device have independent lives; tying them made every device hiccup
  cost the rest of a stream, and made recovery depend on the guest starting a new one. A virtual device keeps the
  queue's clock-pacing arithmetic unchanged (it sees the same queue and device depth a playing device would), costs a
  tick read per write while detached, and needs no timer. Not counting default changes, and ending a run of failures
  once a device has played for a second, keeps device switches and long-lived devices from exhausting retries, while
  the bounded backoff and the event-only wait keep a missing or flapping device from costing power. Costs: a detached
  render thread stays alive (blocked, no wakeups) until the stream ends; a device that fails every few seconds after
  playing for more than a second is reopened each time (after 50 ms), as a working device; a device that comes back without any notification (e.g. released by an exclusive-mode application) is retried only
  by the next default change, device event or PLAYBACK_START once the 10 s budget is spent; the expiry uses the
  system tick (about 16 ms steps) and 0030's device-depth estimate, so a reattached synchronized stream starts within
  the timing budget rather than exactly on it. Not yet checked live.
