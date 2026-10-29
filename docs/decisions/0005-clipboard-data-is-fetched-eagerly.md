# 0005. Clipboard data is fetched eagerly

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** Windows supports delayed rendering (`WM_RENDERFORMAT`), which would block the UI thread on a network round trip.
- **Decision:** when the guest grabs the clipboard, the viewer requests the data at once and places it on the Windows clipboard when it arrives.
- **Reason:** no UI-thread blocking; guest clipboard payloads are small in practice. Agent messages are capped at 64 MB (`VdAgent.MaxMessage`).
