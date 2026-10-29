# 0011. Server mouse mode cursor is configurable

- Status: Accepted
- Date: 2026-10-29
- Decided by: the human

- **Context:** in server mouse mode the pointer is captured (clipped, raw input), and the guest reports its cursor position on the cursor channel. The draft says "confine and hide the pointer"; a guest with a hardware (cursor-channel) cursor then shows no cursor at all.
- **Decision:** the system-menu item "Show guest cursor in server mouse mode" (global setting `serverCursor`, default `guest`) chooses. On: while captured, the real Windows pointer shows the guest's cursor shape and is moved with `SetCursorPos` to the guest-reported position; guests that draw their own cursor into the framebuffer get no second pointer. Off (`hidden`): the draft's behaviour, the pointer is confined (same clip) and hidden, and guest cursor moves are not applied (no posts, no `SetCursorPos`). Client mouse mode is unaffected either way. We always ask for client mode when the server offers it, also when it becomes available later.
- **Reason:** the OS draws the pointer as a hardware overlay, so no compositing in the presenter; raw-input deltas don't depend on the pointer position, so there is no feedback loop. The human chose "make it configurable" (2026-10-29), which settles the earlier deviation from the draft.
