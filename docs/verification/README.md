# Verification records

Live tests against real Proxmox guests, one file per session named `YYYY-MM-DD-<topic>.md`. Each record states the
host, the guests (by OS: `win7`, `win81`), the build tested, what was run, and the measured result. Automated tests
are not repeated here; link them instead.

| Record | Covers |
|---|---|
| [2026-10-29-live.md](2026-10-29-live.md) | M3 presentation (ETW present modes), M5 agent (clipboard, resize, mouse modes), M7 audio and MJPEG, settings and install round trip, allocation profiling |
| [2026-10-30-gstreamer-encoders.md](2026-10-30-gstreamer-encoders.md) | Whether the node's spice-server encodes H.264, VP8 and VP9 through GStreamer (`spice-cli streams`) |
