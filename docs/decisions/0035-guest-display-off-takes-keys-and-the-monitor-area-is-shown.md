# 0035. A guest without a display says so and takes keys; the viewer shows the guest's monitor, not its whole surface

- Status: Accepted
- Date: 2026-10-30
- Decided by: agent (spec silent), reviewed by Codex
  ([../reviews/2026-10-30-display-off-and-monitor-area.md](../reviews/2026-10-30-display-off-and-monitor-area.md)).
  Accepted by the human on 2026-10-30:
  - a brief "display off" message during a mode change is acceptable ("other RDP/VNC software behaves similarly");
  - the shown area comes from parsing DISPLAY_MONITORS_CONFIG ("L2: parse").

  Decided by the human on 2026-10-31 (finding L11): the `Waiting` text is neutral, so the ~0.1 s that every normal
  connect spends in `Waiting` before the first surface doesn't alarm; the `Off` text is unchanged.

- **Context:** two findings of the Ubuntu guest check ([2026-10-30-ubuntu.md](../verification/2026-10-30-ubuntu.md)).
  L1: with the guest's display powered down, spice-server has no primary surface and sends only SET_ACK and
  DISPLAY_MARK on the display channel; the viewer stayed at "Connecting to …" and forwarded no input (every input path
  required the Connected state, which only the first surface set), although keys on the inputs channel wake the guest.
  L2: the qxl KMS driver put the 640x480 mode on a 640x512 primary surface and DISPLAY_MONITORS_CONFIG named the
  640x480 monitor; the canvas ignored the message, so the viewer and the replay showed 32 black rows and the window
  took the surface's size. The spec's launch flow (step 5) says to size the window to "the guest resolution"; its
  presenter list (item 3) says to size the swap chain "to the guest's primary surface" (not a MUST rule). It is silent
  on a guest without a display and on monitors config in v0 (multiple monitors are post-v0).
- **Decision (L1, display off):**
  - `SpiceSession.DisplayState` is `Pending`, `Waiting`, `On` or `Off`, raised by `DisplayStateChanged`: `On` while
    a primary surface exists; `Off` when there is none after the server's initial display state (a DISPLAY_MARK) or
    after a primary surface existed (the guest blanked later); `Waiting` when `ConnectAsync` has linked every channel
    and neither has happened; `Pending` before that. `Waiting` covers a guest already blank at connect, which gets no
    DISPLAY_MARK at all (finding L7: SET_ACK and pings only). Nothing polls and nothing times out: the transitions
    are link completion, DISPLAY_MARK, SURFACE_CREATE and SURFACE_DESTROY.
  - The window has a `DisplayOff` state for both `Off` and `Waiting`. Before any frame it paints, with GDI instead of
    "Connecting to …", "Connected. The guest's display is off — press a key (Shift) to wake it." or "Connected —
    waiting for the guest's display… (press a key if the guest is asleep)." (neutral, finding L11: every normal
    connect is `Waiting` for about 0.1 s). Coming from a shown display, it lets go
    of the pointer and held buttons and uninstalls the keyboard hook, and the presenter keeps running and shows the
    message as an image (dark background, GDI text drawn into a client-sized BGRA buffer, presented through the same
    upload-and-Present1 path as a guest frame, so no shader). Releasing the presenter, the first version of this
    record, doesn't work: the live re-check (finding L5) showed DWM keeps compositing a released flip-model swap
    chain's last frame over the window's GDI painting, through resizes and minimize/restore, so the user saw a frozen
    desktop. The image is redrawn at the end of a resize (during a drag DXGI stretches it). The first new primary
    surface replaces it. The handler reads the session's current state when it runs, so a SURFACE_DESTROY and
    SURFACE_CREATE handled back to back (a mode change) show nothing; handled apart they show the message for those
    tens of milliseconds, which the human accepted.
  - Keys and the viewer's hotkeys reach the guest from the moment every channel is linked (`ConnectAsync` returned) and
    in `DisplayOff`, through WM_KEYDOWN/WM_KEYUP (the low-level hook stays reserved to a shown display, so Alt+Tab and
    the Win key stay with Windows meanwhile). Not while the presenter has failed (`DisplayFailed`) or after the session
    ended. The rules are pure functions in `src/SpiceViewer/GuestView.cs`, tested without a window.
  - The pointer (motion, buttons, wheel, server-mode capture) goes to the guest only while its display is shown. With
    no surface there is no coordinate space for client mode, server mode would need the pointer confined to a window
    showing nothing, and a click would land wherever the guest's invisible pointer is.
  - A session that ends while the display is off keeps its window with the reason and a Close button, as one that
    showed a frame does; only a session that never got as far as a display (or a "display off") gets the error dialog.
  - spice-cli `dump` says that the display is off instead of only "no primary surface".
- **Decision (L2, monitor area):**
  - `Canvas.PrimaryArea` is the part of the primary surface shown: the first head with a non-zero size on surface 0 of
    the last DISPLAY_MONITORS_CONFIG, intersected with the surface. As in spice-gtk, a message with a single head shows
    it from the surface's origin whatever its x/y; with several heads the first one on surface 0 keeps its offset and
    the others are ignored (multiple monitors are post-v0). Without a config, with no head on surface 0, or with a head
    entirely outside the surface, the whole surface is shown (spice-gtk shows nothing in the last case; a picture is
    more useful). A malformed message (count beyond 64 or beyond the payload) counts as a canvas error and keeps the
    current area. A changed area raises `PrimaryChanged` like a new surface.
  - Across a new primary surface, the head is kept only if the new surface has the size of the one it was shown on.
    The first version forgot it on every SURFACE_CREATE, assuming a config always follows; the live re-check (finding
    L6) showed a guest waking its display sends SURFACE_CREATE, a full draw and MARK but no MONITORS_CONFIG, so the
    60 padding rows of a 1600x960 surface behind a 1600x900 mode came back. spice-gtk keeps its monitor list across
    surfaces entirely, but the same re-check showed a Windows guest growing its mode sends SURFACE_DESTROY and
    SURFACE_CREATE without a config, where the old, smaller head would crop the new mode; a surface of another size
    therefore starts whole until its config arrives (a Windows shrink sends the new config before the destroy, on the
    old surface, so it is paired with the old size and dropped too). Limit: a session that starts with the guest
    already blank has no head to keep; when the guest wakes (SURFACE_CREATE 1600x960, draws, MARK, no config) the
    whole surface is shown, padding included, until a config arrives. Nothing in that capture names the 1600x900 mode
    (main, cursor and display channels were checked; only the later draws' extents hint at it), and guessing the mode
    from draw extents was not done. spice-gtk shows nothing in that case.
  - Everything the user sees or points at follows the area: the presenter's swap chain, guest texture and uploads (read
    at the area's offset in the surface), window sizing in 1:1 and Fit (including the WM_SIZING aspect lock),
    resize-follows-window (a request matching the monitor is not repeated), client-mode mouse positions (relative to
    the area, as spice-gtk sends them), server-mode cursor positions (surface coordinates minus the area's offset, as
    spice-gtk draws them), the disconnect overlay's text, and PNG snapshots (`Canvas.SnapshotPrimaryPng`, so spice-cli
    `dump` and the replay goldens). The presenter list's "size the swap chain to the guest's primary surface" is read
    as the guest resolution of launch step 5, which is the monitor.
  - Absolute mouse positions carry the shown head's monitor ID as their display ID (`Canvas.PrimaryMonitorId`, 0 when
    the whole surface is shown), as spice-gtk's `get_display_id` does: spice-server drops empty heads but keeps the
    others' IDs, so a sole enabled head can be #1 (review M1). The agent's resize request still names monitor 0 only;
    resizing a guest whose shown head isn't #0 is left to multi-monitor work.
  - Client pixels map onto the monitor end to end when the image is shrunk: (size - 1) maps to (guest - 1), as in
    spice-gtk's `transform_input`, so the last row and column (hidden panels, hot corners) stay reachable (review M2,
    a defect older than this record). Enlarged or 1:1, each client pixel still maps to the guest pixel under it.
  - A first frame that arrives before its monitors config shows the whole surface; the next notification then still
    counts as the first frame for window sizing, so the window takes the monitor's size (or keeps a remembered size)
    even when the guest follows the window.
  - Parsing (`DisplayParse.TryPrimaryHead`) reads in place and allocates nothing; the canvas logs only when the area
    changes. Recorded `session.json` sizes and `ReplayMatchesRecordedStats` keep the surface size.
- **Reason:** L1: a guest that is only asleep must not look like a hung connection, and the one safe way to wake it from
  the viewer is a key. Keys before the first surface are harmless (they would reach a shown guest a moment later
  anyway), while clicks at an unseen position are not. Keeping the presenter and presenting the message costs one
  client-sized buffer per message or resize while the display is off, and no device recreation when it comes back.
  L2: the monitor is what the guest calls its
  screen, what spice-gtk and remote-viewer show, and what the guest agent resizes; the rows below it are driver
  padding. The Windows fixtures' monitors equal their surfaces, so their goldens don't change; the `ubuntu-rich`
  candidate becomes 640x480. Costs: a mode change whose destroy and create arrive in separate reads can flash the
  "display off" message; a single head placed away from the origin is shown from the origin (as in spice-gtk).
  Findings L5–L7 (2026-10-30 re-check) changed this record: the presenter now shows the message (L5), the head is kept
  across a same-size surface (L6) and `Waiting` replaces "Connecting" once linked (L7); see
  [Re-check: L5–L7 fixes](../verification/2026-10-30-ubuntu.md#re-check-l5l7-fixes-decision-0035-2026-10-30-2335--2026-10-31-0008)
  for the live check. After it, any presenter failure stops keys in every state, the display-off state included
  (review M3), and the message painted before the first frame is laid out again on a resize (L10).
