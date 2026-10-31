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

  Decided by the human on 2026-10-31 (finding L14): the disconnect overlay gets a second line with the channel detail.
  The first line, the reason ("The server closed the connection."), is unchanged; under it, smaller and dimmer, comes
  the detail that `Describe` returns and the error dialog already shows (for a server close, which channel ended
  first: "The server closed the inputs:0 channel."). The detail is left out when there is none or it repeats the
  reason. It is drawn on the dimmed frame, on the message image when the display was off, and, before any frame, as a
  dimmer line under the GDI-painted status.

  Decided by the human on 2026-10-31 (finding L16,
  [2026-10-31-ubuntu-monitor-area.md](../verification/2026-10-31-ubuntu-monitor-area.md)): "推算但做成設定" (infer, but
  make it a setting). For a guest whose agent announces LF line endings, the canvas infers the monitor from the
  guest's full-frame draws when no config names a smaller one (the "Inferred monitor" bullet below), behind the
  window-menu setting **Scaling → Trim Linux guest padding**, saved for all VMs (`trimGuestPadding` in settings.json,
  as decisions 0024 and 0028 keep the other window-menu settings), **on by default**; a file without the key reads as
  on. Off, the behaviour is this record's original rule, apart from the log lines. This reverses the earlier
  "guessing the mode from draw extents was not done": the live capture showed that after a qxl KMS guest recreates
  its surface, spice-server itself names the whole padded surface as the monitor, on every later connect, so a config
  can't be relied on. Known cost, accepted with the setting: a Linux guest whose real monitor is the whole surface can
  be cropped by up to 63 rows or columns until it draws there (see the bullet). The canvas also logs every new primary
  surface, every config that changes the area or is the first for its surface, an inference, its withdrawal and every
  clear. The windowed re-test is still pending.

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
    from draw extents was not done. spice-gtk shows nothing in that case. (Superseded on 2026-10-31 for a Linux agent:
    see "Inferred monitor" below.)
  - **Inferred monitor (2026-10-31, finding L16; a setting, on by default, by the human's decision).** The qxl KMS
    driver sends its monitors config only when its own cached head changes, and spice-server replaces its stored config
    with one head the size of the surface whenever the guest creates a primary surface. After a display wake (same mode,
    new surface) the guest sends nothing, so every later connect gets a 1600x960 head for the 1600x960 surface behind
    the 1600x900 mode, and spice-gtk would show the padding too. The guest's own draws do name the mode: the driver
    draws each frame as one copy of its whole framebuffer, (0,0)-(1600,900). So, when `Canvas.GuestAgentIsLinux` (set by
    the session when the guest agent announces LF line endings: spice-vdagent on Linux; never a Windows agent, never
    without an agent) and `Canvas.InferPaddedMonitor` (the setting "Trim Linux guest padding", on by default) are both
    true, the canvas narrows a whole-surface area to the first guest draw that (a) comes after the surface's MARK
    (before it, spice-server sends its image of the whole surface), (b) starts at (0,0) and is drawn whole (no clip, or
    an original clip of exactly one rectangle equal to its box, judged before clip normalisation), (c) leaves 1 to 63
    rows and columns uncovered (`MaxInferredPadding`; the observed padding is 32 and 60 rows) and (d) holds every guest
    draw since the MARK, video frames included (only draw commands can be candidates). A candidate that fails (d) is
    only rejected; a later, larger one may still qualify. A later draw or video frame outside the inferred area gives
    the configs' area back and ends inference for that surface. A config naming a smaller monitor replaces an inferred
    area; one naming the whole surface doesn't (it is what spice-server substitutes). An inferred area is kept across a
    same-size surface like a configured one, together with the configured head behind it (its ID included), which is
    what comes back if the inference is withdrawn or turned off. Until the guest draws, the whole surface is shown: a
    connect to an idle, awake guest shows the padding until its next frame (any change on its screen: a clock tick, a
    hover, a key). While inference can still narrow the area, `PrimaryAreaIsMonitor` is false, so the window's first
    sizing follows the narrowed area. Turning the setting off while connected gives the configs' area back at once (and
    forgets an inferred area kept for a blank guest); turning it on narrows the area at the guest's next qualifying
    draw. Costs: a Linux guest whose real monitor is the whole surface and whose first full-width draw from the origin
    stops within 63 rows of the bottom (a maximized window above a bottom panel, with a config equal to the surface) is
    cropped until something draws below it; the check is a few integer comparisons per draw and allocates nothing.
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
  - Parsing (`DisplayParse.TryPrimaryHead`) reads in place and allocates nothing; the canvas logs when the area is set
    for a new primary surface, changed (by a config, an inference or its withdrawal) or cleared, and when a config
    first arrives for a surface even if the area stays the same; a repeated identical config is silent. Recorded `session.json` sizes and `ReplayMatchesRecordedStats` keep the surface size.
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
  (review M3), and the message painted before the first frame is laid out again on a resize (L10). That message
  wraps, centred within a margin, so a narrow window shows all of it (L13); the disconnect detail is the L14 note above.
