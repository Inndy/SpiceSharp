# 0043. VNC consoles: a local bridge to the user's own VNC viewer

- Status: Accepted (direction by the human, 2026-10-31); the details are the agent's (spec silent) and are listed
  under "Open questions" where they need the human
- Date: 2026-10-31
- Decided by: the human (2026-10-31): "SPICE 自己做主要原因是現有的 SPICE client 不好用或效率不好，但 VNC 有很成熟的
  client … 我們支援常見的 VNC client 讓 user 自己安裝吧". We wrote our own SPICE client because the existing ones are
  poor; VNC has mature clients, so we bridge to the ones users install. This replaces the built-in RFB client of
  [0034](0034-v1-viewer-adds-vnc-and-stays-a-separate-process.md) (amended there); a built-in client is deferred.

- **Context:**
  - VMs without a SPICE display (`vga: std`, `virtio` without SPICE, `vmware`, `cirrus`) have only Proxmox's VNC
    console, which is RFB over `vncproxy` plus `vncwebsocket`. The v1 launcher
    ([0038](0038-v1-pve-launcher-winui3-unpackaged-self-contained-aot.md)) needs a way to open it.
  - VNC viewers can't speak Proxmox's API, its WebSocket or its one-time ticket, so something local must. That is
    the same shape as the terminal bridge of [0036](0036-v1-terminal-bridge-to-the-users-terminal-first.md):
    connection data over the hand-off pipe, the API's TLS policy of [0033](0033-pve-api-tls-pins-a-given-ca-else-uses-system-trust.md),
    and the user's own program in front.

- **Decision:**
  - **Shape.** A subcommand of the terminal bridge, `PveTerm vnc --pipe NAME --nonce HEX`, not a new exe. The
    library parts (`VncproxyConnector`, `VncUpstream`, `VncListener`/`LoopbackPeer`, `LocalRfbHandshake`,
    `VncRelay`, `VncBridge`, `VncViewers`) live in `src/Pve.Term`, window-free and tested headless.
    - Why a subcommand: it shares the hand-off, the descriptor, the TLS policy and the WebSocket code with
      `PveTerm console`, and one AOT runtime instead of two in the zip (principles: binary size). The cost is that
      a "Term" binary also bridges VNC; the usage text says so.
    - The launcher starts it without a window (`CreateNoWindow`) and reads its standard error. The dev helper
      starts it in a console, where its status lines show.
  - **Descriptor.** The hand-off descriptor of 0036 gains one field: `"console": "vnc"` (absent or `"term"` is a
    text console). A VNC descriptor must target `qemu`, without `serial` and without a session: the bridge always
    requests `vncproxy` itself, because the session must be used within seconds (below). Each subcommand refuses
    the other's descriptors. The viewer choice is on the command line (`--viewer`, or `-- COMMAND`), since it is
    not secret; secrets stay in the pipe.
  - **Upstream protocol** (read from the sources at the end):

    | Item | Fact |
    |---|---|
    | Session | `POST /nodes/{node}/qemu/{vmid}/vncproxy` with `websocket=1`, privilege `VM.Console` on `/vms/{vmid}`. Returns `user`, `ticket`, `password`, `cert`, `port` (5900..5999) and `upid` (qemu-server `API2/Qemu.pm` 2993-3205). |
    | Password | 8 random characters, `!`..`` ` `` (pve-common `Ticket.pm` 209, 244-246). The ticket is `"<password>:PVEVNC:…"` (`Qemu.pm` 3078-3082). The bridge uses `password`, else the ticket, whose first 8 characters are the same; QEMU reads 8 bytes. PVE's noVNC does the same: `password ?? ticket` (novnc-pve patch 0020). |
    | Timing | The worker waits 10 s for the connection (`Qemu.pm` 3084, 3166-3173). `qm vncproxy` then sets QEMU's VNC password with `expire_password +30` (`CLI/qm.pm` 363-366): authentication must follow within 30 s. pveproxy accepts the `vncticket` from 20 s before to 40 s after its creation (pve-access-control `AccessControl.pm` 553, 566-575). The bridge therefore connects upstream at once, in parallel with starting the viewer, inside one 20 s budget. |
    | Socket | `GET /nodes/{node}/qemu/{vmid}/vncwebsocket?port=P&vncticket=T` with the API credentials, subprotocol `binary`, to the node that answered the POST (no `proxyto`). pveproxy relays raw RFB bytes to `localhost:P`; client frames must be masked, unfragmented and at most 128 KiB; server frames are binary, at most 128 KiB (pve-http-server `AnyEvent.pm` 536-732, 1018-1031). |
    | QEMU's server | `-vnc unix:/var/run/qemu-server/<vmid>.vnc,password=on`, no TLS credentials (qemu-server `QemuServer.pm` 3400-3401), for every display type except `serialN` and `none`, qxl included (3394-3405). QEMU's own WebSocket (`ui/vnc-ws.c`) is not used. |
    | Handshake | QEMU sends `RFB 003.008`; accepts 3.3, 3.7 and 3.8 (3.4/3.5 as 3.3) (`ui/vnc.c` 2936-2952, 3391). It offers exactly VNC authentication (type 2) under PVE (3734-3742, 2974-2976): a 16-byte challenge, DES-ECB with the password's first 8 bytes bit-reversed as the key (2779-2834), SecurityResult 0, or 1 plus the reason `"Authentication failed\0"` with its NUL counted (2767-2777). No handshake timeout (no timer in `vnc.c` but refresh and statistics). |
    | Sharing | PVE sets no `share=`, so QEMU's policy is allow-exclusive: a ClientInit with shared=0 **disconnects every other VNC client** of the VM, such as a noVNC tab (`vnc.c` 2667, 2688-2706, 4186-4188). |
    | Idle | `qm vncproxy` exits after 15 minutes with no data in either direction (`CLI/qm.pm` 75-101). QEMU sends nothing on a static screen while an incremental update request is pending (`vnc.c` 1146-1153). Confirmed live (below). |
    | SPICE | VNC and SPICE are separate servers with separate passwords (`Qemu.pm` 3425-3426); a VNC session does not touch a SPICE session. A second `vncproxy` call replaces the VNC password, which affects only clients still authenticating (`vnc.c` 2801-2815). |

  - **The bridge's handshake.**
    - Upstream (`VncUpstream`): RFB 3.8, VNC authentication with the session password (None if that is all a
      server offers), SecurityResult; then the ClientInit and ServerInit. ServerInit is checked before it is
      forwarded: 1..16384 pixels per side, 8/16/32 bpp with a depth no larger, a name of at most 4096 bytes. A
      refusal or reason over 4096 bytes is refused. Any failure is an error, never a partial relay.
    - Downstream (`LocalRfbHandshake`): the bridge says `RFB 003.008` and answers a 3.3, 3.7 or 3.8 viewer in its
      own version, offering only security type None. The local SecurityResult waits for the upstream: an upstream
      failure reaches a 3.8 viewer as SecurityResult 1 with the reason, and a 3.3 viewer as type 0 with the
      reason (3.7 has no way for None; the connection closes). Then the viewer's ClientInit, QEMU's ServerInit
      forwarded unchanged, and from there a byte-for-byte relay.
    - **Shared is forced**: the bridge sends QEMU shared=1 whatever the viewer asked (TigerVNC's default is
      exclusive), so opening a VNC console never closes someone's noVNC tab. Logged when it overrides the
      viewer. Open question 2.
  - **Local security.**
    - The listener binds `127.0.0.1` on a port the system picks, with `SO_EXCLUSIVEADDRUSE`. It serves one session:
      the listener closes once a viewer is taken.
    - Every connection's process is identified (`GetExtendedTcpTable`, `TCP_TABLE_OWNER_PID_CONNECTIONS`) before a
      byte is sent. The row must match the whole connection: established, 127.0.0.1 at both ends, and both ports.
      More than one owner counts as none. The process is then opened, and the table is read again while that handle
      is held; the handle keeps the PID from being reused. Its token is read through the same handle (review
      2026-10-31 R1). The process must run as this user, at this process's integrity level or higher. That is the
      hand-off pipe's rule (0036: owner-only DACL, no-read/no-write-up label). Other connections are closed at
      once and logged, and the wait goes on, so another user can't end the session by connecting first. A viewer
      running elevated whose token we can't read is refused too.
    - No VNC password on the local side. A one-time password would protect only against processes of the same
      user, which are not a boundary on Windows (0036). Viewers also take passwords badly: UltraVNC and TightVNC
      only in plain text on the command line or in an obfuscated `.vnc` file; TigerVNC through `VNC_PASSWORD` or
      a DES-obfuscated `-PasswordFile`. So there is no `{passwordfile}` placeholder. Open question 1.
    - The viewer must finish the handshake within the wait (`--wait`, default 60 s). A viewer that exits non-zero
      first ends the wait; one that exits 0 may have handed over to a running instance, so the wait goes on.
  - **Relay** (`VncRelay`). Each viewer read (up to 32 KiB) becomes one binary frame; each upstream frame's payload
    is written to the socket. Bytes that arrived with ServerInit go first. Either side ending closes both, and the
    reason goes to the log.
    - The data path allocates nothing per message: fixed buffers, `ValueTask` socket and WebSocket calls, and one
      boxed state machine per pump. An allocation test guards it.
  - **Keep-alive** (the relay's only interpretation of the stream):
    - It is needed because of `qm vncproxy`'s 15-minute idle exit. WebSocket pings don't help, since pveproxy
      answers them itself.
    - After `--keepalive` seconds (default 600) with no traffic either way, the bridge sends QEMU one incremental
      FramebufferUpdateRequest (`03 01 0000 0000 W H`). QEMU only notes that an update is wanted
      (`framebuffer_update_request`, `vnc.c` 2042-2057) and sends one when the screen next changes. Viewers
      handle a FramebufferUpdate at any time.
    - The request is put only between two viewer messages. `ClientMessageTracker` follows message lengths exactly
      as QEMU's `protocol_client_msg` does (`vnc.c` 2379-2660): types 0, 2, 3, 4, 5, 6 (with QEMU's `abs()` of an
      extended-clipboard length), 250, 251 and 255/0, 255/1.
    - An unknown type (which QEMU itself would answer by dropping the client) turns the keep-alive off, never the
      relay.
    - A session in use never gets one. An idle one wakes once every 10 minutes: the only timer besides the startup
      deadlines. `--keepalive 0` turns it off.
  - **Viewers** (`VncViewers`, sources at the end). Detection uses file checks and uninstall-key reads only, like
    the Windows Terminal detection of [0042](0042-terminal-choice-detects-windows-terminal-automatic-by-default.md).
    Every viewer gets `127.0.0.1::PORT` (`::` is a literal port in all four).

    | Viewer | Found at | Extra arguments | QEMU extended key event (-258) | Notes |
    |---|---|---|---|---|
    | TigerVNC (GPL) | `TigerVNC_is1` InstallLocation, `%ProgramFiles%\TigerVNC\vncviewer.exe` | none | **yes**: Windows scancodes (`KeyboardWin32.cxx`) | accepts None silently; default shared=0 (forced to 1 by the bridge) |
    | TightVNC 2.x (GPL) | `%ProgramFiles%\TightVNC\tvnviewer.exe` | none | no (keysyms) | accepts None silently; 4.x is closed freeware, unverified |
    | UltraVNC (GPL) | `Ultravnc2_is1` InstallLocation, `%ProgramFiles%\uvnc bvba\UltraVNC\` (installer script) and `%ProgramFiles%\uvnc\UltraVNC\` (winget) | `-AllowUntrustedServers` (else it asks whether a server without authentication can be trusted) | no (keysyms) | |
    | RealVNC Viewer 7 (proprietary) | `%ProgramFiles%\RealVNC\VNC Viewer\vncviewer.exe`, `%ProgramFiles%\VNC Viewer\vncviewer.exe` (paths unverified) | `-WarnUnencrypted=FALSE` (documented) | unverified | RealVNC Connect 8 needs a RealVNC account and a paid plan for third-party servers: unsupported |

    - **Automatic** picks the first found in that order. TigerVNC comes first because only it sends scancodes
      through QEMU's extended key events, so layouts and keys without a keysym behave as in the SPICE viewer. The
      user README recommends it.
    - A **custom command** (`-- COMMAND ARG...`) takes the placeholders `{host}`, `{port}`, `{hostport}`
      (`127.0.0.1::PORT`) and `{title}`.
    - **None** starts nothing and prints the address.
  - **Launcher.** "Open VNC console" is an action for every qemu VM.
    - The default action reads the VM's config (`GET .../qemu/{vmid}/config`, `VM.Audit`) and follows
      qemu-server's own rule:
      - SPICE when the `vga` type is `qxl`, `qxl2`..`qxl4` or starts with `virtio`. That is the `spice` flag of the
        status API (`API2/Qemu.pm` 3533-3537; `vga_conf_has_spice`, `QemuServer.pm` 2935-2943, 3475).
      - The serial terminal for `serialN`.
      - VNC otherwise: `std`, `cirrus`, `vmware`, or no `vga` (which is `std` on x86, `QemuServer.pm` 3077-3085).
      - `vga: none` has no display, so the default action opens the terminal.
      - A config that can't be read falls back to SPICE, the earlier default.
    - The VNC viewer is a launcher setting, like the terminal: Automatic, a detected viewer, None, or a custom
      command.
  - **Dev helper.** `tools/pve-vv.py --vnc VMID [--viewer V] [--here|--hidden] [-- COMMAND]`, with the same pipe
    hand-off as `--term`. `tools/vnc-probe.py` is a headless RFB client for checks: no window, no input to the
    guest.
  - **Exit codes** (`PveTerm vnc`): 0 the session ran and ended, 1 Proxmox or QEMU refused or failed, 2 bad
    arguments, 3 the hand-off failed, 4 no viewer (not found, didn't start, exited, or didn't connect in time).

- **Reason:**
  - The human's direction. Mature viewers already have scaling, clipboard, key handling and their own settings,
    and users keep the one they know (principles: flexibility).
  - A byte-transparent relay is the least code that can be wrong: the bridge parses only the handshake and
    message lengths, and never the framebuffer.
  - Process identity is checked the way the pipe's ACL checks it. It works with every viewer without a secret on a
    command line or on disk.
  - Costs:
    - Each console is a separate viewer window with the viewer's own behaviour, so system shortcuts and scaling
      follow the viewer, not ours.
    - Only TigerVNC sends scancodes.
    - The keep-alive inserts a message into the viewer's stream; it is one documented message, at a message
      boundary only.

- **Open questions (human):**
  1. The local side has no password; the viewer's process (user and integrity level) is checked instead. Add a
     one-time VNC password for viewers that can take one safely (TigerVNC through `VNC_PASSWORD`), as defence in
     depth against same-user processes?
  2. The bridge always asks QEMU for a shared session, so a viewer can't close other consoles. Keep it, or pass
     the viewer's exclusive flag on (TigerVNC's `-Shared` setting would then matter)?
  3. Keep-alive every 10 idle minutes (an injected incremental update request), or none, accepting that Proxmox
     ends an idle VNC session after 15 minutes?
  4. RealVNC Viewer: its install path and whether 7.x accepts a None server silently are unverified (closed source,
     help pages only). Keep it in the automatic list, or offer it only as an explicit choice?
  5. Should the launcher also offer VNC for VMs that do have SPICE (as now, through "Open VNC console"), or hide
     it for them?

- **Sources** (read 2026-10-31):
  - QEMU `4075aa42` (`ui/vnc.c`, `ui/vnc.h`, `ui/vnc-jobs.c`, `ui/vnc-clipboard.c`); qemu-server `6a9adaac`
    (`src/PVE/API2/Qemu.pm`, `src/PVE/QemuServer.pm`, `src/PVE/CLI/qm.pm`); pve-http-server `5c9e4ea7`
    (`src/PVE/APIServer/AnyEvent.pm`); pve-access-control `dff26b45` (`src/PVE/AccessControl.pm`); pve-common
    `b9b41d52` (`src/PVE/Ticket.pm`, `src/PVE/Tools.pm`); pve-manager `73ac838f`; novnc-pve `9711393c` (patches
    0001, 0013, 0020); noVNC `e8b5024d` (`core/rfb.js`), all at git.proxmox.com, GitHub or GitLab.
  - Viewers: TigerVNC `ed951a41` (`release/tigervnc.iss.in`, `vncviewer/parameters.cxx`, `CConn.cxx`,
    `KeyboardWin32.cxx`, `common/rfb/CConnection.cxx`, `SecurityClient.cxx`, `common/network/TcpSocket.cxx`);
    UltraVNC `7bd294a8` (`UltraVNC_installer_x64.iss`, `vncviewer/VNCOptions.cpp`, `ClientConnection.cpp`,
    `vncviewer.cpp`); TightVNC 2.8.90 GPL source (`tvnviewer/ViewerCmdLine.cpp`, `viewer-core/RemoteViewerCore.cpp`,
    `wix-installer/components.wxs`); winget-pkgs manifests `TigerVNC.TigerVNC` 1.16.2, `uvncbvba.UltraVNC`
    1.8.3.1, `GlavSoft.TightVNC` 2.8.89, `RealVNC.VNCViewer` 7.15.1, `RealVNC.VNCConnect.Viewer` 8.5.3; RealVNC
    help-center articles 360002249797 (address syntax), 360002254618 (parameters: `WarnUnencrypted`, `Shared`),
    36060594862365 (Connect 8 and third-party servers).
  - Live: [../verification/2026-10-31-vnc.md](../verification/2026-10-31-vnc.md).

## Answers (the human, 2026-10-31, through agentdesk)

1. No local password: the peer check (same user, same or higher integrity level) stays the only local check.
2. Shared is always forced. On the human's question whether noVNC can take a session exclusively: noVNC's
   `core/rfb.js` sends shared=1 by default and PVE's noVNC doesn't change it, but QEMU runs allow-exclusive under
   PVE, so any client sending shared=0 (TigerVNC's default) would close the noVNC tab. Forcing shared=1 prevents
   that.
3. The 10-minute keep-alive stays.
4. RealVNC stays in the automatic list; the order is by how usable and robust each viewer is ("按照 VNC client
   好用與穩健性排序").
5. "Open VNC console" stays for every qemu VM, SPICE or not.
