# Verification records

Live tests against real Proxmox guests, one file per session named `YYYY-MM-DD-<topic>.md`. Each record states the
host, the guests (by OS: `win7`, `win81`), the build tested, what was run, and the measured result. Automated tests
are not repeated here; link them instead.

| Record | Covers |
|---|---|
| [2026-10-29-live.md](2026-10-29-live.md) | M3 presentation (ETW present modes), M5 agent (clipboard, resize, mouse modes), M7 audio and MJPEG, settings and install round trip, allocation profiling |
| [2026-10-30-gstreamer-encoders.md](2026-10-30-gstreamer-encoders.md) | Whether the node's spice-server encodes H.264, VP8 and VP9 through GStreamer (`spice-cli streams`) |
| [2026-10-30-live-options.md](2026-10-30-live-options.md) | The viewer options on the AOT zip: LZ4 images, video pacing modes and run-time switches, fast JPEG, minimize/restore presents, resize, audio underruns |
| [2026-10-30-clock-av-sync.md](2026-10-30-clock-av-sync.md) | Clock-mode A/V sync (decision 0030) on win7: measured sync error with a flash-and-tone guest signal, off/spacing audio latency, live pacing switches, short sounds lost at PLAYBACK_STOP, PLAYBACK_LATENCY; re-check after fixes: drain at STOP, back-to-back sounds, switch depth, steady sync, default-device change; re-check of the audio detach (decision 0031): default-device changes and a device failure during playback, role notifications |
