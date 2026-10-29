# Live verification of the viewer options (2026-10-30)

Host: Windows 11 IoT Enterprise LTSC 2024, AMD Radeon 8060S, 3840x2160. Guests on Proxmox through spiceproxy, as in
[2026-10-29-live.md](2026-10-29-live.md): `win7` (1280x800 at the start, the only VM with `streaming-video=all`) and
`win81` (1280x960, 150% DPI inside the guest). Build: the portable zip from `pwsh tools/package.ps1` (Native AOT,
`SpiceViewer-0.1.0-win-x64.zip`), built from `80839e1`; the viewer and library sources are unchanged through
`5c567c9`. Each run used a fresh `.vv` and a copy of the zip whose `data\settings.json` set `videoPacing`, `fastJpeg`
and `imageCompression`; `--debug` was on for the log.

## Method

- Guest load: a WinForms window painting one of eight fixed photo-like 480x320 bitmaps (seeded, so identical in every
  run) about 21 times a second, started in the guest's interactive session through a scheduled task over WinRM. A
  "still" variant painted the first bitmap once and stayed open. On win7 the animation becomes a 480x320 MJPEG stream;
  win81 gets it as image draws.
- Window actions came from outside the viewer: `ShowWindow` (minimize, restore), `SetWindowPos` (resize), and
  `WM_SYSCOMMAND` with the window menu's item ids, which is what a menu click sends. `GetMenuState` read the check
  marks back.
- Presents: in-box ETW (`logman` on Microsoft-Windows-DXGI, `tracerpt`), DXGI `Present` events of the viewer
  process counted between timestamped phase marks.
- Protocol-level numbers came from a throwaway console program on `Spice.Session` (not committed). It applies the same
  session options as the viewer, records the time of every `Invalidated` of the stream's 480x320 rectangle (each
  drawn frame) and prints `Canvas.Stats` (image types) and a canvas snapshot.
- Audio: system sounds played with `Media.SoundPlayer` in the guest's interactive session. With `--debug` the
  viewer logs each playback stream's underruns and dropped samples when it closes.

## Results

| Option / behaviour | Guest | What was done | Observed | Result |
|---|---|---|---|---|
| `imageCompression: auto` | win81 | probe, animation | 265 GLZ images, 1 LZ (the first screen) | pass (server default kept) |
| `imageCompression: lz4` | win81 | probe, animation | `asked the server to prefer LZ4 image compression`; 266 `image_Lz4`, 1 LZ (the first screen, sent before the request), 0 canvas errors | pass |
| LZ4 output | win81 | probe snapshot of the still window, once with `auto` (11 GLZ images) and once with `lz4` (8 LZ4 images) | the two 1280x960 snapshots are pixel-identical | pass |
| LZ4 | win7 | probe, `lz4` + fast JPEG | 32 `image_Lz4`, none other, 0 errors; the MJPEG stream ran alongside (151 frames, 0 dropped) | pass |
| LZ4 in the viewer | win81, win7 | viewer with `lz4` | Info line at connect, display correct (screenshots), no warnings | pass |
| LZ4 menu round trip | win81 | menu "Server default", then "LZ4" | logs `the server keeps the requested image compression until the next connection`, then asks for LZ4 again; radio marks follow; `lz4` saved | pass |
| Video pacing `off` (default) | win7 | viewer, 20 s animation | stream 407 frames, 0 dropped; 0 presents in the 8 s after it ended | pass |
| | win7 | probe | 301 frames drawn of 301; draw interval 46.9 ms, sd 0.9 ms (44–55 ms); first frame drawn 11 ms after STREAM_CREATE | pass |
| Video pacing `spacing` | win7 | probe | 300 frames, 0 dropped; draw interval sd 2.5 ms (frames arrive evenly on this LAN, so nothing is held) | pass |
| | win7 | viewer started with `spacing`, minimize/restore and resize during the stream | 535 frames, 0 dropped; after the resize the stream was re-created (127 + 219 frames, 0 dropped) | pass |
| Video pacing `clock` | win7 | probe, twice | 301 frames received, 0 counted as dropped; first frame drawn **423 ms** after STREAM_CREATE (the guest clock lead, previously unmeasured in 0025); 293 frames reached the screen (see F2); draw intervals 31–63 ms, sd 7.9 ms (see F1) | pass, with F1 |
| Pacing switched while streaming | win7 | viewer started with `clock` + fast JPEG + LZ4, then menu: Off, Smooth bursts, Follow guest clock, fast JPEG off (about 4.6 s apart) | each switch moved the radio mark; presents 19–21/s in every phase; switching from `clock` to `off` dropped the 8 queued frames (log: 663 frames, 8 dropped); switching to `clock` froze the picture for 447 ms (the lead) and then played on; the last mode and `fastJpeg: false` were saved | pass (as designed in 0025) |
| `fastJpeg: true` | win7 | viewer and probe | menu item checked at start from settings.json; MJPEG frames decoded with 0 drops; toggled off at run time and saved | pass (image difference not measured live) |
| Minimize / restore, image draws | win81 | animation, minimize 8.6 s, restore | 0 presents while minimized (last present 19 ms before the minimize, next 2 ms after the restore); restored window shows the current frame | pass |
| Minimize while the guest changes, restore when idle | win81 | animation 8 s, minimized for its end, restored with the guest idle | 0 presents while minimized; 3 presents on restore, then none; the restored window shows the desktop without the closed animation window | pass |
| Minimize / restore during a video stream | win7 | `spacing`, minimized 8.6 s mid-stream | 0 presents while minimized; stream kept decoding (535 frames, 0 dropped); restore repainted at once | pass |
| Window resize | win81 | `SetWindowPos` to 1040x800 and 1640x1100 | guest followed: 1016x744, then 1616x1044 (width rounded down to a multiple of 8); image correct | pass |
| Window resize during a stream | win7 | resize to 1100x750 while streaming, back later | guest 1072x694, stream ended and was re-created, 0 dropped; back to 1280x800 on the direct path | pass |
| Audio | win81 | 3 × three system sounds (9 playback streams) | `playing 2 ch 48000 Hz s16, device buffer 40 ms`; underruns 1, 0, 0, then 0 in the other six; 0 dropped samples | pass (see F4) |
| Audio | win7 | four system sounds back to back (4 streams) | 0 underruns, 0 dropped samples | pass |
| Idle | both | after the animation ended | 0–1 presents per 4–5 s | pass |

## Findings

- **F1: clock pacing draws on the 15.6 ms system tick, which adds jitter.** Steps: `videoPacing: clock`, a steady
  ~21 fps MJPEG stream on win7, record the draw time of each frame. Expected: draws spaced as the frames' timestamps
  (about 47 ms, as `off` draws them: sd 0.9 ms). Actual: intervals cluster at 31, 47 and 62 ms (2, 3 and 4 × 15.6 ms),
  sd 7.9 ms. Interval histogram (4 ms bins, 289 intervals): 28–32: 1, 32–36: 30, 36–40: 32, 40–44: 17, 44–48: 103,
  48–52: 25, 52–56: 29, 56–60: 34, 60–64: 19. Likely cause: `SystemPacingClock` uses `Environment.TickCount64` and a
  thread-pool `Timer`, both at the default system timer resolution. `spacing` would likely show the same when it
  holds frames; on this LAN it never did. A higher timer resolution costs energy (principles), so this is for the human to weigh, not a fix to
  make here.
- **F2: in `clock` mode the last ~0.4 s of a stream never reaches the screen and isn't counted as dropped.** In both
  probe runs 301 frames arrived, the log said 0 dropped, and 293 were drawn: the missing 8 are the frames still queued
  (the 423 ms lead) when the guest window closed. The stream itself ended 1 s later (spice-server's stream timeout),
  so STREAM_DESTROY didn't discard them. The likely path is the documented one in 0025: a queued frame is drawn with
  the stream's current clip, and the server had clipped the stream area once the desktop was redrawn over it, so the
  blit covered nothing. Not confirmed at the message level. The picture was right, so this looks like intended
  behaviour; it is noted because the "dropped" count in the log doesn't show it.
- **F3: stale doc comment.** `src/Spice.Session/VideoPacing.cs` still calls `Spacing` the "Default"; the default is
  `Off` (`SessionDisplay._videoPacing`, `ViewerSettings.VideoPacing`, decision 0025).
- **F4: one audio underrun.** The first playback stream after connecting to win81 had 1 underrun; the eight streams
  after it and all four on win7 had none. Not reproduced, so not filed as a bug.
- Not a bug, but easy to misread: the `--debug` line `agent: reply type 2 error 1` after each resize is a successful
  monitors-config reply (`VD_AGENT_SUCCESS` = 1).

## Not covered

- What fast JPEG does to the picture live: the guest's MJPEG frames vary with the server's quality control, and the
  viewer exposes no decode timing. The difference is measured offline in decision 0028.
- Audio/video sync and `clock` mode with real video content (the animation has no sound).
- `spacing` holding bursty frames: the LAN delivered frames evenly, so no frame was held or dropped as late.
- Presentation-model details (flip and independent flip); see the 2026-10-29 record.
