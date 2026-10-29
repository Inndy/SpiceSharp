# 0019. Viewport beyond the texture limit

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** a texture side is limited (16384 at feature level 11, 8192 at 10, 4096 at 9_3). Both the guest-sized swap chain and the letterbox texture must fit, and so must a client-sized letterbox swap chain.
- **Decision:** two explicit limitations. (1) A guest larger than the limit shows only its top-left part up to the limit, on both paths; `GuestRect()` extends past the shown part at the same scale, so input mapping stays consistent. (2) A letterboxed client larger than the limit (a window spanning several monitors, or a large monitor at feature level 9_3) gets the largest swap chain with the client's aspect ratio, and DXGI stretches it back: the image stays uniformly scaled (within rounding), but 1:1 is then not pixel-exact. Fit stays uniform in every case (`ViewportTests`).
- **Reason:** both are rare, neither may produce an invalid buffer or a distortion, and exact 1:1 past the limit would need a second swap chain or child window, which v0 avoids.
