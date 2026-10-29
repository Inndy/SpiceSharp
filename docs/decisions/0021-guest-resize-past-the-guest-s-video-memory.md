# 0021. Guest resize past the guest's video memory

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** with "Resize guest with window" (default on, used when the agent supports monitors config), a maximized window on a 4K host asked the Windows 8.1 guest for 3840x2080. With QEMU's default QXL video memory (16 MB, below 3840x2080x4) the guest kept 1024x768 and sent no reply.
- **Decision:** the viewer keeps showing the current guest resolution, letterboxed and uniformly scaled, and doesn't retry or guess a smaller mode. Sizes that fit are followed (1472x894 on Windows 8.1 and 1272x844 on Windows 7 in live tests; the guest rounds the width down to a multiple of 8, so the letterbox path covers the few pixels left over).
- **Reason:** the guest driver decides which modes it accepts; raising `vga: qxl,memory=...` on the VM is the fix for large monitors.
