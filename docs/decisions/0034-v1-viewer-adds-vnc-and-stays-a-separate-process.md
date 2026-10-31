# 0034. In v1 the viewer also opens Proxmox VNC consoles and stays a separate process

- Status: Accepted; the VNC part amended 2026-10-31 (below)
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

- **Amended 2026-10-31 (the human):** "SPICE 自己做主要原因是現有的 SPICE client 不好用或效率不好，但 VNC 有很成熟的
  client … 我們支援常見的 VNC client 讓 user 自己安裝吧" (we wrote our own SPICE client because the existing ones are
  poor; VNC has mature clients, so support the common ones and let users install their own).
  - This replaces "the viewer also speaks RFB natively". VNC consoles now go through a local bridge
    (`PveTerm vnc`) to the user's own VNC viewer, as designed in
    [0043](0043-vnc-consoles-bridge-to-the-users-vnc-viewer.md). The scope stays the dialect Proxmox serves.
  - The built-in RFB client is **deferred, not done**. Nothing of it was merged, and the viewer's SPICE paths are
    unchanged.
  - The rest of this record stands: the viewer remains a separate process that takes its connection data on stdin.
