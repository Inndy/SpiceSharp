# Live verification of clock-mode A/V sync (2026-10-30)

The WIP commits named below (`0251588`, `c003d29`) were squashed into one commit on `main`; they are kept under the
local tag `archive/avsync-wip`.

Host: Windows 11 IoT Enterprise LTSC 2024, AMD Radeon 8060S; default audio endpoint mixing at 44.1 kHz stereo float
(the viewer opens 48 kHz s16 with automatic conversion; it logs `device buffer 40 ms, stream latency 0 ms`). Guest:
`win7` on Proxmox through spiceproxy (1280x800, `streaming-video=all`, the only test VM that streams video). Build: WIP
commit `0251588` (decision [0030](../decisions/0030-clock-pacing-holds-audio-to-the-guest-clock.md), Proposed), the
portable zip from `pwsh tools/package.ps1` (Native AOT) for the viewer runs, and a Debug build of the same tree for
the probe. Each connection used a fresh `.vv`; the viewer ran with `--debug` and `resizeGuest: false`.

## Method

- **Guest signal.** A WinForms window in the guest's interactive session (scheduled task over WinRM) repaints a
  480x320 area about 21 times a second with fixed photo-like frames, which spice-server streams as MJPEG. Every
  2 s (3 s in the long run) the area turns white for about 200 ms, and a 1 kHz tone (120 ms unless stated) starts:
  - *continuous*: one long WAV with a tone every period, started once with `SoundPlayer.Play()`; the flashes follow a
    stopwatch started with it. The playback stream stays open, so this measures steady-state sync.
  - *per beep*: `SoundPlayer.Play()` of a short WAV (tone plus 30 ms) in the same timer tick as the flash; every beep
    is its own PLAYBACK_START ... PLAYBACK_STOP, as Windows UI sounds are.
  Where the guest makes the flash and the tone relative to each other is the guest's business; it shows up in the
  frames' and packets' timestamps and is subtracted (below).
- **Probe** (throwaway console program, not committed). It connects with `Spice.Session`, applies the pacing mode
  (and switches it at set times), and plays the audio through the viewer's own `AudioOutput` class, loaded by
  reflection from the viewer assembly and fed exactly as `MainWindow` feeds it (`Write(pcm, delay, gap)`; with
  `--oldaudio` it passes no delay, which is the pre-0030 behaviour of clock mode). It records:
  - every draw of the stream rectangle (`Invalidated`): time, guest clock (`MultimediaTime`), whether the area is
    white, and the drawn frame's timestamp (in `clock` mode the queue head seen after the previous draw, since a
    dequeued slot is cleared; in `off` the stream's last timestamp);
  - every PLAYBACK_DATA packet: arrival time, timestamp, the delay and gap the session computed, and the decoded PCM;
  - a WASAPI loopback capture of the default render endpoint, with the performance-counter time of each capture
    packet, so "heard" means the time the audio engine mixed the sample.
  Tone onsets are found offline (first 1 ms block above threshold that starts at least 10 ms of tone after at least
  60 ms of quiet) in the decoded PCM, laid out by timestamp, and in the loopback.
- **Per flash:** stamp offset `dS` = tone-onset stamp minus flash-frame stamp (set by the guest and server);
  heard-drawn = loopback onset minus flash draw on the client; **sync error = heard-drawn - dS** (0 is perfect; positive
  means the sound comes later than the timestamps say). "Due" is the packet's arrival time plus the delay the session
  gave it, i.e. when `PcmQueue` aimed for it to be heard.
- **Viewer runs** (the zip): the same guest signal, loopback recorded alongside, pacing switched from outside the
  viewer with `WM_SYSCOMMAND` and the window menu's item ids (check marks read back with `GetMenuState`), and the
  viewer's `--debug` log. The viewer exposes no per-frame timing, so its runs check audibility, gaps and skips and the
  log, not the sync error.

## Results

Steady state, continuous tone track, probe (ms; mean ± sd, range):

| Run | Pacing, audio path | Flashes | dS (guest side) | Heard - drawn | **Sync error** | Arrival → heard | Close-of-stream log |
|---|---|---|---|---|---|---|---|
| 40 s | `clock`, 0030 | 19 | 53.6 ± 13.0 | 52.1 ± 14.2 | **-1.5 ± 5.5** (-14.4 .. 8.9) | 419.4 ± 3.0 | underruns 0, dropped 0; 1 correction, 19152 frames (399 ms) of silence |
| 150 s | `clock`, 0030 | 50 | 66.6 ± 3.2 | 74.6 ± 7.1 | **+8.0 ± 7.6** (-7.4 .. 26.5) | 422.0 ± 3.2 | underruns 0, dropped 0; 1 correction, 18576 frames (387 ms) |
| 30 s | `clock`, audio unsynchronized (pre-0030) | 14 | 56.9 ± 12.5 | -328.8 ± 13.1 | **-385.6 ± 6.0** | 35.0 ± 2.7 | underruns 0, dropped 0 |
| 30 s | `off`, 0030 | 14 | 45.7 ± 13.0 | 69.4 ± 12.5 | +23.6 ± 2.5 | 31.4 ± 2.6 | underruns 0, dropped 0 |
| 30 s | `spacing`, 0030 | 14 | 56.9 ± 13.1 | 80.4 ± 12.8 | +23.5 ± 3.0 | 31.1 ± 3.1 | underruns 0, dropped 0 |
| 30 s | `off`, `AudioOutput` before 0030 | 14 | 55.5 ± 12.1 | 83.9 ± 12.4 | +28.4 ± 3.0 | 36.1 ± 2.9 | underruns 0, dropped 0 |

Other behaviour:

| What | How | Observed | Result |
|---|---|---|---|
| A/V sync in `clock` | rows above | sound within -14 .. +27 ms of where the timestamps put it (was -386 ms before 0030); 0 underruns, 0 dropped | pass |
| Where the residual comes from | `clock` 40 s / 150 s | video frames drawn 21.4 ± 5.8 / 17.0 ± 6.9 ms after their stamps (the 15.6 ms system tick, F1 of [2026-10-30-live-options.md](2026-10-30-live-options.md)); audio due within 3 ms of its stamp on average; loopback heard it 17.0 ± 6.0 / 25.6 ± 5.7 ms after due | the two lags mostly cancel (see F3) |
| Drift, guest clock vs host audio clock | 150 s `clock` | sync error grows 0.063 ms/s (about 9 ms over the run); no correction after the first | pass; a 40 ms drift step would come about every 10 min (inferred) |
| Audio latency in `off` / `spacing` | rows above | arrival → heard 31.4 / 31.1 ms with 0030, 36.1 ms with the old `AudioOutput`; 0 underruns, 0 dropped in every run | pass (unchanged within run-to-run spread) |
| Switch `off` → `clock` → `off` (probe) | continuous track, switches at 16 s and 32 s | `off`: sync error +21.8, arrival → heard 30.0; at the switch one correction of 19248 frames (401 ms) of silence, then +8.0 (1.7 .. 12.9); at the switch back one skip, `dropped samples 32736` (341 ms), then **+82.4** (78.3 .. 87.6), arrival → heard 90.1, until the stream closed; video `stream 40 ended after 920 frames, 7 dropped` | pass, with F2 |
| Switch `clock` → `off` → `clock` → `off` (viewer, menu) | continuous track, viewer started in `clock`, switches 14 s apart | tones 2000 ms apart except: `off`: tone cut to 41 ms, next interval 1652 ms (348 ms skip); `clock`: interval 2330 ms (330 ms gap); `off`: tone cut to 29 ms, interval 1672 ms (328 ms skip). Log: `Audio: stream closed; underruns 0, dropped samples 64924; clock pacing made 2 corrections: 34896 frames of silence inserted, 0 dropped` (727 ms = 397 + 330) and `display: stream 40 ended after 1175 frames, 16 dropped`; check marks followed each switch; the last mode was saved | pass (one gap or one skip per switch), with F2 |
| Short sounds in `clock` | per beep, probe and viewer | probe: 10 tones decoded, **0 heard**; viewer: 8 beeps, **0 heard**. Each beep: `Audio: playing ...` then about 145 ms later `Audio: stream closed; underruns 0, dropped samples 0; clock pacing made 1 corrections: ~19000 frames of silence inserted, 0 dropped` | **fail (F1)** |
| Short sounds in `off` | per beep, probe and viewer | probe 5 of 5 heard; viewer 8 of 8 heard, 87–98 ms of each 120 ms tone | pass (tail cut, F1) |
| 1 s tone | per beep, `ToneMs` 1000, probe | `clock`: 577–593 ms of each 998 ms tone heard; `off`: 970–979 ms | **fail (F1)** |
| PLAYBACK_LATENCY | every run | `playback: server latency N ms` (Info) with N = 4, 6, 8 or 10, about every 3.3 s, then 0, in 5 of 11 probe runs (in `clock`, `spacing` and `off`), while a video stream and audio were both active; none logged in the other probe runs or the 3 viewer runs | sent (F5) |
| Guest clock rebases (MULTI_MEDIA_TIME) | guest clock minus local time, every packet | stayed within one 15.6 ms tick in every run | none occurred; rebase path not exercised |

## Findings

- **F1: in `clock` mode, PLAYBACK_STOP throws away the audio still held back, so the last ~0.4 s of every sound is
  never played and sounds shorter than that are silent.** Steps: `videoPacing: clock`, win7; in the guest play a short
  sound (here a 120 ms tone, as Windows UI sounds are short), or a 1 s tone. Expected: the whole sound, about 0.4 s
  late. Actual: nothing heard for the short sound (probe 0 of 10, viewer 0 of 8 in loopback); 577–593 ms of the 1 s
  tone. spice-server sends PLAYBACK_STOP about 145 ms after START for the short sound, while the queue still holds it
  behind ~0.4 s of inserted silence; the log line says `dropped samples 0`, so the loss isn't counted anywhere. Likely
  path: `PlaybackMsg.Stop` raises `AudioStopped`, `MainWindow` calls `AudioOutput.Stop`, and `StopCore` closes the
  `PcmQueue`, which discards what it holds. The same path costs `off` mode the last 20–33 ms of each sound (87–98 ms of
  a 120 ms tone heard, 970–979 ms of 1 s), which is pre-existing and small. Decision 0030 lists the costs of `clock`
  mode but not this one; the matching video loss at the end of a stream is F2 of the options record.
- **F2: after switching from `clock` to an unsynchronized mode, audio stays about 60 ms later than in a fresh `off`
  stream until playback restarts.** Probe, continuous track: sync error +21.8 ms before the `clock` phase, +82.4 ms
  after it (arrival to heard 30 → 90 ms); the viewer's `off` → `clock` gap was 330 ms, about 70 ms short of the
  397 ms lead, which fits the same extra queue. Likely cause: the switch trims the queue to 40 ms (`TrimToMs`), and
  nothing brings it lower afterwards because the trim only starts above 100 ms; a new stream starts from the 20 ms
  prime. Decision 0030 describes the trim but not the lasting latency.
- **F3: the sync is good partly because two lags cancel.** Video in `clock` mode is drawn 17–21 ms after its stamp
  (the system tick, live-options F1). Audio is aimed at its stamp (within 3 ms on average), but loopback hears it 17–26 ms
  after that; likely because the endpoint reports a stream latency of 0 ms, so the queue counts only the device
  padding and not the engine's own buffering (not confirmed). On a host whose
  endpoint has more unreported latency, or with a finer timer for video, the residual would grow; it is within the
  40 ms tolerance here.
- **F4: drift corrections are rare.** The guest clock and this host's audio clock drift about 0.065 ms/s; over 150 s
  no correction followed the first one. At this rate the 40 ms tolerance is reached after about 10 minutes (inferred,
  not observed).
- **F5: the server sends PLAYBACK_LATENCY while streaming video.** Values were 4–10 ms, sent about every 3.3 s and
  ending with 0, in runs of all three modes; in other runs none was logged (only changes are logged, and a first 0 is
  not). This contradicts the remembered behaviour in decision 0030 ("sends PLAYBACK_LATENCY otherwise", i.e. when not
  streaming), though not its conclusion: values this small could not have driven a ~400 ms depth. No MULTI_MEDIA_TIME
  rebase happened in any run, so the session never moved the clock under held audio.
- **F6 (logging):** there is no per-packet "clock pacing inserted ..." line; corrections appear only in the
  close-of-stream `Audio: stream closed` line, as 0030 says. The `stream latency 0 ms` part of `Audio: playing` comes from
  `IAudioClient::GetStreamLatency`.
- Not a viewer issue: loopback picked up two short decaying pulse trains during viewer runs, one of them before the
  viewer had opened any audio stream; they were excluded from the analysis.

## Not covered

- Present-to-screen and speaker timing: "drawn" is the canvas update the viewer presents on its next paint, and
  "heard" is the audio engine's mix time. Neither the DWM/scanout delay nor the endpoint's hardware latency was
  measured; both add to their side.
- A guest clock rebase (MULTI_MEDIA_TIME) during playback, underrun recovery and device changes in `clock` mode: none
  happened live (covered by `AudioSyncTests` / `AudioSyncReviewTests`).
- Real content (a film with speech): only a synthetic flash and tone. win81 (no video streaming), where `clock` mode
  delays all sound without anything to line up with.
- Drift beyond 150 s.

## Re-check after fixes

Same host, guest, signal and method as above, on WIP commit `c003d29` (end-of-stream drain, re-prime on switching
pacing off, full gap layout, queue epoch; decision 0030 as revised). The viewer runs used the AOT zip from
`pwsh tools/package.ps1` at that commit; the probe drives `AudioOutput` as the revised `MainWindow` does
(PLAYBACK_STOP → `EndOfStream`). Additions to the method:
- *Back-to-back*: the per-beep signal with a period of about 300 ms (120 ms tone), so each PLAYBACK_START arrives
  115–180 ms after the previous STOP, while that sound is still held back (~0.4 s).
- Tone lengths and holes come from the loopback: a tone heard in two pieces less than 30 ms apart counts as one tone
  with a hole.
- *Default device change*: a scratch helper switched the host's default render endpoint (console and multimedia roles,
  through the undocumented `IPolicyConfig`) to another active endpoint and back during playback, then restored the
  original default for all three roles and checked it (identical to before). Loopback records the original device
  only, so audio is heard there only while it is the default.

### Results

| What | Run | Observed | Result |
|---|---|---|---|
| F1: short sounds, `clock` | per beep, 10 × 120 ms, probe | **10 of 10 heard, 117–119 ms each** (decoded 117–118); `Audio: stream closed; underruns 0, dropped samples 0, discarded on close 0; clock pacing made 1 corrections (19584 frames of silence inserted, 0 dropped), stamp gaps 0 frames of silence, 0 cut`; the device closes 404–445 ms after PLAYBACK_STOP (the drain) | pass (was 0 of 10) |
| | per beep, 8 × 120 ms, viewer | 8 of 8 heard, 117–119 ms; 8 streams, each `discarded on close 0` | pass (was 0 of 8) |
| F1: short sounds, `off` | per beep, probe | 10 of 10 heard, 87–98 ms of each 118 ms tone; `discarded on close 960` (10 ms) per stream | unchanged (the 20–30 ms tail cut in `off` remains, see F1 above) |
| F1: 1 s tones | per beep, `clock` / `off`, probe | `clock`: 999–1001 ms heard of 997–998 decoded, `discarded on close 0`; `off`: 970–979 ms | pass (was 577–593 ms) |
| Sync of per-beep sounds, `clock` | per beep, probe | sync error **+11.0 ± 12.2** (-8.6 .. +30.6); loopback heard 17–40 ms after due | within the 40 ms budget, looser than continuous (G2) |
| Back-to-back, `clock` | ~300 ms period, probe, 34 and 54 sounds | **all heard**, one device stream for all of them (`Audio: playing` once, one close: `stamp gaps 232560 frames of silence, 0 cut`), `discarded on close 0`; but **2 of 34 and 6 of 54 tones had a 12–21 ms hole** about 40–50 ms in; heard spacing minus stamp spacing sd 12.1 / 10.7 ms (-24 .. +32) | pass, with G1 |
| | ~300 ms period, viewer, 40 sounds heard | one device stream; 3 tones split or shortened (18 + 11 + 90 ms, 89 + 30 ms, 108 ms) | pass, with G1 |
| Back-to-back, `off` | ~300 ms period, probe, 34 sounds | all heard, no holes, 87–98 ms each, spacing error sd 4.7 ms; one device open/close per sound | reference |
| F2: depth after `clock` → `off` | probe, continuous, `off` → `clock` → `off` at 16 s / 32 s | arrival → heard: **37.2 ± 2.7** ms fresh `off`; 415.4 in `clock` (sync error -1.4, -10.0 .. 6.8); **56.8 ± 2.5** ms after switching back (sync error +29.5 → +49.1); one skip, `dropped samples 34368` (358 ms); no holes | improved: +19.6 ms over a fresh stream (was +60); decision 0030 expected at most ~10 ms (G3) |
| Switches in the viewer | menu, `clock` → `off` → `clock` → `off`, 14 s apart | tone intervals 2000 ms except a 379 ms skip, a **365 ms** gap (was 330) and a 364 ms skip; `dropped samples 71356, discarded on close 960; clock pacing made 2 corrections (36672 frames of silence inserted, 0 dropped)`; `display: stream 40 ended after 1175 frames, 16 dropped` | pass |
| Steady sync, `clock` | continuous, 40 s, probe | sync error **-3.7 ± 5.0** (-14.4 .. +5.3), arrival → heard 418.7, heard - due 14.7 ± 3.2; 1 correction | pass (was -1.5 ± 5.5) |
| | continuous, 150 s, probe | sync error **+5.0 ± 13.2** (-24.9 .. +23.5); 2 corrections: the first, then at ~122 s `1488 dropped` (31 ms): drift 0.061 ms/s reached the 30 ms gate, moving the error from about +22 to about -19 ms (mean +11.2 before, -16.8 after) | pass (was +8.0 ± 7.6 with no drift correction; G4) |
| Default device change | viewer, 1 s tones, one change per stream, timed into the drain (after STOP) | each change logs `Audio: default output device changed; reopening` twice (one per role) and the stream reopens; the drain completes on the new device about 60–70 ms early, `discarded on close 0`, no warning; after a change back to the recorded device the rest of the held tone (88–100 ms) is heard there | pass |
| | viewer, changes 0.7 s apart | the second change within one stream ends it: `Warn Audio: output closed; the next playback start reopens the default device`, `discarded on close 24988` (260 ms) and `32860` (342 ms); the next sound reopens normally | G5 |

### Findings

- **G1: back-to-back sounds in `clock` mode sometimes get a 12–21 ms hole.** Steps: `clock`, the guest plays a short
  sound every ~300 ms, so each START arrives while the previous sound is still held. Expected: every tone continuous,
  placed at its stamp. Actual: 2 of 34, 6 of 54 (probe) and 3 of 40 (viewer) tones are heard in two pieces with a
  12–21 ms silence about 40–50 ms in; tone spacing is off its stamp spacing by sd 11–12 ms (-24 .. +32 ms), against
  4.7 ms in `off`. The hole is about one 15.6 ms tick. Likely cause (not confirmed): while the previous stream's
  samples are queued, each packet of the continuing stream is placed at its own deadline (0030, E2), and the delay
  is computed from the guest clock extrapolated with `TickCount64`, which steps by 15.6 ms; a step between two packets
  of one tone then becomes inserted silence. Nothing is lost (`0 cut`, `discarded on close 0`).
- **G2: a sound that opens the device is placed less precisely than continuous audio.** Per-beep sync error
  +11.0 ± 12.2 ms (to +30.6), against -3.7 ± 5.0 for a continuous stream; loopback hears the first samples 17–40 ms
  after due (14.7 ± 3.2 continuous). Likely the opening device's start-up is not in the depth the first correction
  sees. Within the 40 ms budget.
- **G3: the clock → off depth is better but not yet at a fresh stream's.** +19.6 ms over a fresh `off` stream (was
  +60), where 0030 expects up to ~10 ms. The viewer's matching off → clock gap grew from 330 to 365 ms, consistent
  with ~35 ms of extra depth before it. One run each.
- **G4: drift corrections now happen within minutes.** With the 30 ms gate (0030 step 4) and this host's 0.061 ms/s,
  the first drift correction came after ~2 minutes (one 31 ms drop, as designed), instead of the ~10 minutes estimated
  for a 40 ms gate. The sync error jumped from about +22 to about -19 ms. On this host the queue's aim sits ~17 ms
  ahead of where loopback hears it (F3), so after the correction the error sits near the opposite edge.
- **G5: two device changes within one stream end it.** Each default change notifies twice (console and multimedia
  roles), so it uses 2 of the 3 reopens `AudioOutput` allows before the counter resets (a stream open for more than
  5 s). A second change during the same stream ends it, and with `clock` pacing the held audio is discarded (counted
  in `discarded on close`). A single change, including one during the drain, is handled. The reopen limit predates
  0030; changes this close together were forced by the test.

### Not covered

- A guest clock rebase (MULTI_MEDIA_TIME) during playback: none occurred again.
- Underrun recovery in `clock` mode; a device change in the pre-roll of a reopened device exactly at the end of a drain
  (0030 E3) was not hit deliberately (the changes landed 0.2 s into drains).
- Present-to-screen and speaker timing, real content, win81, drift beyond 150 s: as above.
