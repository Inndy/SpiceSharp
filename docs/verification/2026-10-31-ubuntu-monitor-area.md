# Linux guest: stale strip below the monitor after a display wake (2026-10-31)

Follow-up to [2026-10-30-ubuntu.md](2026-10-30-ubuntu.md) (L6 and its recorded limit). The human saw it again at 13:42
with `main`: on the `ubuntu` guest (GNOME on Wayland, qxl KMS), a window with a 1600x960 client area at 1:1 showed
about 60 rows of stale content below GNOME's screen (old dock icons, a purple bar). Their `viewer.log` had
`guest display 1600x960 — first frame 261 ms after start`, then `agent: requested guest resolution …` lines with no
surface change after them (L8: GNOME ignores them), and no line about a monitors config or a monitor area. An earlier
session in the same log connected to a blank guest (first frame after 2862 ms).

Method: headless only. Every connection used a fresh `.vv` from `tools/pve-vv.py`, deleted after use, and
`tools/spicerec` (each one disconnected the human's open viewer, which they had allowed). The display, main and inputs
recordings were decoded with a scratch script outside the repository; the recordings stay outside it (not needed as a
fixture: the tests rebuild their message order). The guest was driven over SSH as the logged-in user (D-Bus only, no
sudo, no settings changed). No viewer window was opened.

## What the server sent

Times are milliseconds from the start of each recording. Draws are listed with their box.

**A: connect while the display is on (13:49, 6 s).** Agent capabilities `0x38de7` (includes `GUEST_LINEEND_LF`) at
224 ms, then:

```
 284.8  SURFACE_CREATE id=0 1600x960 primary
 289.6  DRAW_COPY (0,0)-(1600,960)                            spice-server's image of the whole surface
 315.3  MONITORS_CONFIG count=1 max=4: head #0 surface 0 1600x960 at (0,0)
 316.0  MARK
```

No guest draw followed in the 6 s (an idle desktop). The snapshot of the surface shows the desktop over rows 0–899 and,
in rows 900–959, part of an old dock and a purple bar: the human's strip.

**B: connect while the display is blank (13:51–13:52, 12 s), woken over D-Bus (`PowerSaveMode` 3 → 0) at 7 s.**
SET_ACK and pings only until the wake, then:

```
7033.7  SURFACE_CREATE id=0 1600x960 primary
7040.5  DRAW_COPY (0,0)-(1600,960)                            spice-server's image of the whole surface
7065.6  MARK
7065.7  DRAW_COPY (0,0)-(1600,900), clip: one rectangle (0,0)-(1600,900)   the guest's first frame
```

No MONITORS_CONFIG at all, as in L6.

**C: connect while the display is on (14:06, 75 s), the guest idle behind its screen shield.** The same as A (the
config again names the whole 1600x960 surface), and the guest's next frame came 8.2 s in: two
`DRAW_COPY (0,0)-(1600,900)`.

On the main channel, in all three: INIT, NAME, UUID, CHANNELS_LIST and the agent's ANNOUNCE_CAPABILITIES; no agent
message names a resolution (spice-vdagent sends the client nothing about its monitors).

## Cause (L16)

Not the viewer dropping or resetting the config: the server never names the 1600x900 monitor.

- spice-server, `dev_create_primary_surface` (0.15.2): whenever the guest creates a primary surface it replaces its
  stored monitors config with one head the size of the surface (`display_channel_set_monitors_config_to_primary`)
  and, with a driver that sends configs itself, expects the guest to send a new one.
- The Linux qxl KMS driver (`qxl_crtc_update_monitors_config`) sends a config only when its own cached head changes.
  On a display wake the mode is the same: the crtc enable finds no primary surface yet ("no primary surface, skip"),
  the plane update creates the surface, and the flush finds the cached 1600x900 head unchanged, so nothing is sent.
  (DPMS off doesn't change head 0's cache either: "inactive head 0, skip".)
- From then on, every connect gets spice-server's stand-in head of 1600x960 (A and C), and a session that was
  connected through the wake gets no config at all (B). This lasts until the guest really changes its mode.
- The viewer followed decision 0035: a whole-surface head shows the whole surface, and as the area didn't change
  nothing was logged. spice-gtk follows the server's head the same way (source reading, not checked live), so
  remote-viewer would show the strip too.

The 1600x960 surface is taller than GNOME's 1600x900 framebuffer, which sits in its top rows (640x480 sat on 640x512
the same way, L2). The driver draws every frame as one copy of the whole framebuffer (`qxl_primary_atomic_update` → `qxl_draw_dirty_fb` with the
framebuffer's rectangle), so the guest's own draws are always (0,0)-(1600,900): the mode is visible there and nowhere
else.

## Fix

Decision 0035 is amended (dated note and the "Inferred monitor" bullet). The human decided on 2026-10-31 to keep the
inference as a setting ("推算但做成設定"): window menu, Scaling → **Trim Linux guest padding**, saved for all VMs as
`trimGuestPadding`, on by default (a file without the key reads as on). Off, decision 0035's original rule applies.

- When the guest agent announces LF line endings (a Linux agent), `Canvas.GuestAgentIsLinux` is set; with the setting
  on (`Canvas.InferPaddedMonitor`), a whole-surface area is then narrowed to the first guest draw after the surface's MARK that starts at (0,0), is drawn whole (no clip,
  or one clip rectangle equal to its box), leaves 1–63 rows and columns uncovered and holds every guest draw since the
  MARK. A later draw outside it gives the whole surface back for the rest of that surface's life. A config naming a
  smaller monitor wins; a whole-surface config doesn't undo an inferred area. The inferred area is kept across a
  same-size surface (a wake while connected), like a configured one.
- While inference is still possible the area counts as provisional, so the window's first sizing follows the
  narrowed area: at 1:1 the window shrinks from 1600x960 to 1600x900 when the guest draws.
- The canvas now logs every new primary surface (`canvas: monitor area (0,0 1600x960), the whole new 1600x960 primary
  surface until a monitors config names the monitor`), each config that changes the area or is the first for its
  surface (`…, unchanged, from monitors config head #0 1600x960 at (0,0) of 1; it names the whole surface, so the
  guest's first full-frame draw may narrow it`), the inference (`canvas: monitor area (0,0 1600x900) of the 1600x960
  primary surface, inferred from the guest's full-frame draw: …`), a withdrawal and the clear when the surface goes.
  Repeated identical configs log nothing, and nothing logs per frame. The session logs once that the agent turned
  inference on.
- Windows guests and guests without an agent are unchanged.
- Turning the setting off while connected gives the configs' area back at once (logged: `… trimming Linux guest
  padding was turned off`) and forgets an inferred area kept for a blank guest; turning it on narrows the area at the
  guest's next qualifying draw. `TrimGuestPaddingSettingsTests` covers the default, a file without the key, the
  round-trip and lenient values; `MonitorInferenceTests` covers the setting off, the toggle both ways and the kept area.

Replaying the three recordings through `SpiceSession` (a local, uncommitted test): A ends at 1600x960 (no guest draw
yet, the expected limit), B at 1600x900 from the guest's first frame 30 ms after the wake, C at 1600x900 from its frame
at 8.2 s. Zero canvas errors in each. `MonitorInferenceTests` rebuilds these orders without the recordings: the A and
C order with and without the guest's frame, the B order and a blank/wake while connected, the Windows case (no
agent: no inference, a maximized window above a taskbar isn't cropped), a draw outside the inferred area, padding of
64 rows or columns, a draw not from the origin, a clipped draw, configs before and after, the log lines, zero
allocation per draw after the inference, the agent capability gate, and `ubuntu-rich` (its 640x480 config still
wins). No golden image changed.

Limit: until the guest draws, the strip shows. On a connect to an awake, idle desktop (A) that lasts until the next
change on the guest's screen (a clock tick, a hover, a key). A guest without spice-vdagent keeps the strip.

## Results

| Check | Result | Notes |
|---|---|---|
| What the server sends, display on at connect | captured (A, C) | one head = the whole 1600x960 surface, before the MARK; no other source names 1600x900 |
| What the server sends, display blank at connect, then woken | captured (B) | SURFACE_CREATE, whole-surface image, MARK, the guest's (0,0)-(1600,900) frame; no config |
| Root cause | found (L16) | spice-server resets its config on every primary surface; the qxl KMS driver doesn't resend an unchanged head |
| Replay of A, B, C with the fix | pass | 1600x960 (no guest frame yet), 1600x900, 1600x900; zero errors |
| Window-free tests | pass | see the commit; Replay, Native, Wire, Video, Diagnostics, Pve.Api, Pve.Term |
| Windowed check: the strip goes, the window takes 1600x900 at 1:1, the log names the area | **pending** | needs the human's re-test |

## Pending: the human's re-test

With the build from this change and `ubuntu` logged in at 1600x900:

1. Connect while the guest's desktop is shown and nothing moves. Expected: the strip at first (1600x960), and in
   `viewer.log` `canvas: a Linux guest agent: …`, `canvas: monitor area (0,0 1600x960), the whole new 1600x960 primary
   surface …` and `… from monitors config head #0 1600x960 at (0,0) of 1; it names the whole surface …`. Hover over
   the dock or wait for the clock's next minute: the strip disappears, at 1:1 the window becomes 1600x900, and the log
   has `canvas: monitor area (0,0 1600x900) of the 1600x960 primary surface, inferred from the guest's full-frame
   draw`. Mouse positions at the bottom edge reach the dock.
2. Let the guest blank (or lock and wait), connect, press Shift. Expected: no strip from the first frame on.
3. With the window connected, let it blank and wake it. Expected: `… kept (inferred from draws) for the recreated
   1600x960 primary surface`, no strip.
4. A Windows guest: nothing changes (no `inferred` line in the log).
5. The window menu's Scaling → "Trim Linux guest padding" is checked by default. Unchecking it while the 1600x900
   area is shown brings the 1600x960 surface (and the strip) back at once; checking it again trims at the guest's next
   redraw. The choice survives a restart (`trimGuestPadding` in settings.json).

## Guest and host state

- Guest (`ubuntu`): woken once over D-Bus (`PowerSaveMode` set to 0, the equivalent of a key press); afterwards it
  idled into its screen shield by itself (screensaver active, display on). No settings changed, no sudo used.
- Host: no viewer window was opened; every `.vv` file was deleted; the recordings and snapshots stay in the agent's
  scratch folder, outside the repository.
