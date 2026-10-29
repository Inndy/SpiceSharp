# 0018. Presenter owns its graphics objects; device loss recreates once

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** shutdown released D3D objects from the UI thread after a 2 s `Join` while the worker could still be inside `Present1`, which may itself wait on the UI thread; worker exceptions were unhandled; a failing `Present1` re-signalled itself in a loop (review findings 5 and 6).
- **Decision:** the presenter thread creates, uses and releases the device, context, swap chain, views and latency handle, and releases them in its own `finally`. `Shutdown` only signals; it waits at most 2 s while dispatching sent messages only (`PeekMessage(PM_QS_SENDMESSAGE)`), and never releases anything. The worker has a top-level exception boundary. `DXGI_ERROR_DEVICE_REMOVED/RESET/HUNG/DRIVER_INTERNAL_ERROR` (from `Present1` or thrown by wrappers) recreate the device and swap chain once and redraw in full; if recreation fails, or the new device is lost before it presents anything, or any other unexpected error occurs, the worker posts `WM_APP_PRESENTER` (`NotifyFailed`) and stops, and the window shows a status message. The window then leaves `Connected` for `DisplayFailed`, releases everything held in the guest and uninstalls the keyboard hook, so no input reaches a guest the user can't see (verification finding N6); the next guest display change tries a new presenter. Other `Present1` failures mark a full redraw for the next guest change without waking the worker.
- **Reason:** no use-after-release regardless of timing, no UI/DXGI deadlock, and no busy retry against a dead device.
