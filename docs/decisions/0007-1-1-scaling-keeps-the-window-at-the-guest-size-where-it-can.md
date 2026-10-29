# 0007. 1:1 scaling keeps the window at the guest size where it can

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** the draft offers "1:1" scaling but does not say what happens when the window and the guest differ in size.
- **Decision:** choosing 1:1 sizes the window's client area to the guest (clamped to the monitor's work area), and an interactive drag (`WM_SIZING`) keeps the client at the guest size, unless "Resize guest with window" is on and the agent supports it, in which case the window resizes freely and the guest follows. Maximize, snap, full screen and the work-area clamp can still produce a client of another size; then the guest is drawn unscaled through the letterbox path, centered on an axis where the client is larger and cropped right/bottom where it is smaller. There is no scrolling in v0. The software profile behaves the same way, since it is always 1:1.
- **Reason:** 1:1 means unscaled pixels; when the window can't match the guest, bars or a crop are the honest results, and cropping keeps the top-left (where most UI starts) visible.
