# 0034. In v1 the viewer also opens Proxmox VNC consoles and stays a separate process

- Status: Accepted
- Date: 2026-10-30
- Decided by: the human (2026-10-30: "ok" to keeping the viewer a separate process with the spec unchanged;
  "let's support noVNC in viewer (in v1)")

- **Context:**
  - The v1 PVE client will open consoles for VMs and containers. Some VMs have no SPICE display, and Proxmox's
    noVNC console is RFB over WebSocket (`vncproxy` plus `vncwebsocket`).
  - The spec has the launcher start the viewer as a separate process, through `--vv-stdin`, and never embed it.
- **Decision:**
  - The spec's process model stays: the v1 client starts `SpiceViewer.exe` and passes connection data on stdin,
    never on disk or the command line.
  - In v1 the viewer also speaks RFB over Proxmox's VNC WebSocket, with a native C# client. noVNC's JavaScript is
    not embedded.
  - Scope is the RFB dialect Proxmox actually serves, not general VNC (human, 2026-10-30: "support VNC
    dialect/extensions that PVE is using"). That means:
    - QEMU's VNC server, reached through `vncproxy` and `vncwebsocket` with the ticket as the password;
    - the encodings and pseudo-encodings it offers, for example QEMU's extended key events;
    - the dialect of any other Proxmox console endpoint the client opens.
  - The exact list is taken from QEMU's and Proxmox's sources when the client is designed, and recorded there.
    Other VNC servers' extensions and security types are out of scope.
  - The VNC client reuses the viewer's presenter, input and keyboard hook. It sits behind the same transport and
    protocol seams as SPICE.
  - The viewer stays usable standalone.
- **Reason:**
  - Keeping the separate process isolates crashes, keeps the viewer plain Win32 and Native AOT, and keeps the stdin
    hand-off as an open interface any launcher can use (flexibility, see
    [../design/principles.md](../design/principles.md)).
  - The viewer core stays in libraries, so in-process hosting remains possible later. That would need a spec change
    by the human.
  - Cost: each console is its own window, not a tab in the client.
