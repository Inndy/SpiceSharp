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
| [2026-10-30-v0-dod.md](2026-10-30-v0-dod.md) | v0 DoD checks that need no human: idle acceptance test after connect and after audio/MJPEG (M3, M7), expired-ticket dialog and startup/latency targets (M4), Win key, Alt+Tab, release hotkey and focus loss through the keyboard hook (M6), forced-WARP software profile (M8) |
| [2026-10-30-ubuntu.md](2026-10-30-ubuntu.md) | First Linux guest (`ubuntu`, GNOME): the `ubuntu-rich` replay capture (LZ and GLZ only, no QUIC), connect and display; blank-display and monitors-config findings; the rest of the live check cut short; re-checks of the fixes (L1–L15) |
| [2026-10-30-arm64.md](2026-10-30-arm64.md) | The win-arm64 release zip run by the human on a Windows ARM64 machine against win81 |
| [2026-10-30-pveterm.md](2026-10-30-pveterm.md) | PveTerm (decision 0036) on the `ubuntu` serial console through termproxy: hand-off, token-authenticated session, input and output, byte transparency, resize on a serial line, detach, idle without keep-alive |
| [2026-10-31-idle-memory.md](2026-10-31-idle-memory.md) | K1: where an idle session's memory sits after a busy period (GC heap, ArrayPool, GLZ window and reuse pool, GPU driver, WARP comparison), GC settings A/B, and the trims of decision 0041 on win81 and win7 |
