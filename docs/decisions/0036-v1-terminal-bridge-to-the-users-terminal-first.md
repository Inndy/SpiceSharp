# 0036. v1 terminal: bridge to the user's terminal first

- Status: Accepted
- Date: 2026-10-30
- Decided by: the human (2026-10-30): v1 ships the bridge first, and a built-in VT (libghostty-vt) comes later
  behind the same seam; PuTTY support is dropped; the bridge stays byte-transparent apart from termproxy's client
  framing (no hold-back of split UTF-8). The human (2026-10-30) also approved an ssh-style escape (`~.`) on the
  input side, the bridge's only interpretation of input. The details below are the agent's (spec silent) and are
  listed under "Open questions" where they need the human.

- **Context:**
  - The v1 PVE client opens Proxmox text consoles: the node shell, a VM's serial console and a container console.
    Proxmox serves them through `termproxy` behind its API, not as VNC.
  - [../design/principles.md](../design/principles.md) ("Transparency and flexibility") asks that users can keep
    the terminal they already use. The spec's process model ([0034](0034-v1-viewer-adds-vnc-and-stays-a-separate-process.md))
    keeps consoles in separate processes, with connection data never on disk or a command line.
  - The PVE API's TLS rule is [0033](0033-pve-api-tls-pins-a-given-ca-else-uses-system-trust.md).

- **Decision:**
  - **Shape.** `src/Pve.Term` (library) holds the transport, the connection descriptor, the hand-off pipe and the
    TLS policy. `src/PveTerm` (`PveTerm.exe`, Native AOT console program) runs one session in the console it was
    started in: Windows Terminal, or conhost on Windows 10 22H2. The seam is `ITermChannel` (read output bytes, send
    input bytes, resize, close reason). A front end (`IFrontEnd`) sits on the other side: the console today, and later
    a built-in VT window. The launcher, the bridge and a later VT all use the same transport.
  - **termproxy protocol**, read from the sources listed at the end:

    | Item | Fact |
    |---|---|
    | Session | `POST /api2/json/nodes/{node}/termproxy`, `.../qemu/{vmid}/termproxy` (optional `serial`, `serial0`..`serial3`) or `.../lxc/{vmid}/termproxy` returns `user`, `ticket`, `port` and `upid`. Node shell: `Sys.Console` on `/nodes/{node}`; VM and container: `VM.Console` on `/vms/{vmid}`. A node shell for anyone but `root@pam` is `/bin/login`. |
    | Socket | `GET /api2/json/<same path>/vncwebsocket?port=P&vncticket=T` with the API credentials (API token header or `PVEAuthCookie`). `port` is limited to 5900..5999. The request must reach the node that answered the POST: the endpoints have no `proxyto`, and termproxy listens on that node's localhost. Subprotocol `binary`. |
    | Timing | termproxy waits 10 s for the connection and 10 s more for the ticket line, then exits. So by default PveTerm requests the session itself instead of receiving a ticket from the launcher. |
    | Login | The first line is `user:ticket\n`, where `user` is the `user` the POST returned (an API token's id for token logins). termproxy checks it with `/access/vncticket` and answers `OK`. Any bytes after `OK` are already terminal output. A refused ticket makes termproxy exit, which drops the socket. |
    | Client to server | `0:LEN:DATA` with LEN the byte length; `1:COLS:ROWS:` (termproxy casts both to u16 and sets the pty size); `2` (ping). Everything else is skipped. A message may span frames: termproxy parses a byte stream. |
    | Server to client | The pty's raw bytes, unframed, in binary frames of at most 128 KiB. |
    | pveproxy | Refuses fragmented client frames, frames over 128 KiB and unmasked frames. It answers WebSocket pings and ignores pongs. When termproxy exits it drops the TCP connection without a close frame. |
    | Survey corrections | No `Referer: ...xtermjs=1` check exists in these sources; the termproxy endpoints are text-only. Token-authenticated serial termproxy works on the test node's PVE 9.2 (live, see below). |

  - **Byte transparency (human).** Output goes to the console exactly as received, one `WriteFile` per WebSocket
    read, with the output code page set to UTF-8. Nothing is parsed, held back, reordered or rewritten. Requests
    such as DA/DSR, title changes and OSC 52 reach the user's terminal, which answers or acts on them.
    Input is what the console delivers in VT input mode (`ENABLE_VIRTUAL_TERMINAL_INPUT`): keys arrive already
    encoded as VT sequences. The console API delivers UTF-16, so the bridge converts it to UTF-8 and wraps it in
    `0:LEN:`. That framing is the only protocol processing; the input escape below is the only interpretation of
    input. The bridge adds its own bytes in three places: status lines (`[PveTerm] Connecting...`, the `~?` help and
    `Connection closed: <reason>`), the console title it sets, and the reset sequence on exit (open question 2).
    - Split UTF-8 needs no buffering on the targets. A sequence split across two `WriteFile` calls renders whole on
      Windows 11 build 26100.9711 (in-box conhost 10.0.26100.9549, the host behind a pseudoconsole). The test
      `Utf8SplitAcrossWritesRendersWhole` echoes every byte in its own frame. Windows Terminal brings its own
      OpenConsole, and Windows 10 22H2 uses the modern console host; neither is a legacy conhost (human, 2026-10-30).
  - **Console mode.** The bridge opens `CONIN$`/`CONOUT$`, so redirected standard handles don't matter.
    - Input mode: line input, echo and processed input are cleared, so Ctrl+C reaches the guest as 0x03. VT input
      and window input are set; other bits, such as Quick Edit, are left alone.
    - Output mode: VT processing plus `DISABLE_NEWLINE_AUTO_RETURN`. Both code pages are set to 65001.
    - Input is read with blocking `ReadConsoleInputW` on its own thread. Characters come from key-down events, plus
      Alt key-up events for Alt+numpad entry. A key-down with a zero character is sent as NUL only for Ctrl+Space and
      Ctrl+2 (Ctrl+@ on US layouts) without Alt, as xterm types them; Windows Terminal's win32-input-mode delivers
      them that way. Other zero-character events (Shift, Ctrl, lock keys) send nothing.
    - `WINDOW_BUFFER_SIZE_EVENT` becomes `1:COLS:ROWS:` with the window's size; repeats are not sent. The initial
      size is sent once the session is open.
    - Restore on exit: modes, code pages and title (also an empty one) come back on every exit, including from the
      `CTRL_CLOSE/LOGOFF/SHUTDOWN` handler. `CONIN$` is not closed, because closing it blocks while the input thread
      sits in `ReadConsoleInputW`.
    - Exit codes: 0 when the session ended after it opened, 1 when the connection failed, 2 for bad arguments,
      3 when the hand-off failed, 4 when the console is missing or has no VT support.
  - **Input escape (human, 2026-10-30; details follow ssh).** The only way to leave a session from the keyboard,
    since raw mode sends Ctrl+C to the guest.
    - The escape character (default `~`) is special only at the start of a line: first in the session, or right after
      Enter (`\r`, or `\n`). There it is held until the next key: `.` quits the bridge (the console is restored and
      the WebSocket closed; the guest's shell keeps running), `?` prints a help line locally, the escape character
      sends one of itself, and any other key sends the held escape character and then that key, in order. After `~?`
      the line start remains, as in ssh, so `~?~.` quits; after anything else the next line start is the next Enter.
    - There is no timeout and no timer: a held `~` waits for the next key.
    - A paste without bracketed paste is just typing to the bridge, so a pasted `"\r~."` quits, as in ssh. Text between
      `ESC[200~` and `ESC[201~` (bracketed paste, which the terminal sends once the guest asked for it) is never
      taken as an escape; after such a paste the escape works again from the next Enter.
    - `--escape none` turns it off; `--escape C` picks another visible character. `.` and `?` are refused with a
      usage error, because doubling them would collide with the quit and help commands; the filter itself also
      checks doubling first, so a doubled escape character always sends one of itself. The startup line says
      `(~. to quit, ~? for help)` with the chosen character.
    - Output is never inspected. The filter is a per-character state machine with no allocation on the input path.
  - **Hand-off (agent).**
    - The launcher creates `\\.\pipe\pveterm-<128-bit hex>`:
      - one instance, `FILE_FLAG_FIRST_PIPE_INSTANCE`, `PIPE_REJECT_REMOTE_CLIENTS`;
      - security descriptor `D:P(A;;GA;;;<user SID>)S:(ML;;NWNR;;;<own integrity SID>)`: a protected DACL for the
        current user only, plus a label that keeps processes at a lower integrity level from reading or writing it.
    - It starts `PveTerm.exe console --pipe NAME --nonce HEX` (32 hex digits, single use). The bridge connects at
      SECURITY_IDENTIFICATION impersonation level and checks that the server process's token user is its own user.
      It sends the nonce; the launcher compares it in constant time and writes the descriptor once. The bridge reads
      until it holds one whole JSON value (or the pipe closes), then closes its end; the launcher waits for that
      instead of `WaitForPipeDrain`, which can't be cancelled.
    - One deadline covers each side's whole exchange (connect, nonce, descriptor, close). A wrong nonce, a timeout,
      or the client process exiting ends the hand-off for good. The dev helper's Python server keeps the same
      30-second whole-exchange deadline while its launched command still runs. Every pipe step is overlapped and
      waits for its own completion or an "expired" manual-reset event, which a one-shot timer sets. The event
      stays set, so a step that starts after expiry ends at once rather than missing the signal; the step is then
      cancelled (`CancelIoEx`, waited out) and the pipe closed. Like PveTerm's launcher side, it waits for the bridge
      to close the pipe instead of an uncancellable `FlushFileBuffers`.
    - The descriptor is JSON, version 1, read with `Utf8JsonReader` (no reflection). It carries:
      - the API URL (https, host and port only), the node, the target, the vmid and serial, and a title;
      - an API token, or a login cookie plus CSRF token;
      - optionally a termproxy session, and optionally a CA PEM.
    - Every field is validated, including header-safe characters, the safe-ID node name and the 5900..5999 port.
      Error messages and `ToString` never include a secret.
    - The name and nonce on the command line grant nothing without access to the pipe. Secrets never reach a
      command line or the disk. Same-user processes are not a boundary on Windows (they can read the launcher's
      memory), so the protection is against other users, lower integrity levels and the network.
  - **TLS ([0033](0033-pve-api-tls-pins-a-given-ca-else-uses-system-trust.md)).** A given CA is pinned: the chain
    must build to it alone (`CustomRootTrust`, no downloads, server-auth EKU). Otherwise the system roots decide.
    The certificate must name the host in both modes (agent's reading; open question 5). The POST and the WebSocket
    use the same policy, and no request is sent over a connection that failed it. The dev helper
    (`tools/pve-vv.py`) applies the same policy to its own API calls: an optional `PVE_CA_FILE` in its token file is
    pinned (and passed on in the descriptor), else the system roots decide, with the hostname checked. The CA in a
    `spiceproxy` answer is the cluster CA for SPICE, not the API certificate's issuer, so it is never used for the API.
  - **Keep-alive.** The pve-xtermjs README says "we have a timeout of 5 minutes", and xterm.js pings every 30 s.
    Neither termproxy (`main.rs`: the poll loop has no timer once the session is open) nor pveproxy (`websocket_proxy`
    sets both handles' timeout to 0) enforces an idle timeout in the current sources. Live, with pings off, a session
    sat 335 s with no traffic either way and still worked
    ([2026-10-30-pveterm.md](../verification/2026-10-30-pveterm.md)). Proxies and NAT on the way may still time
    out. Policy: PveTerm sends `2` only after 240 s without any other send, so a session in use never
    pings, and an idle one wakes once every 4 minutes. `--keepalive SECONDS` changes the interval and 0 turns it off.
    `ClientWebSocket`'s default 30 s unsolicited pong is turned off (`KeepAliveInterval = 0`). These are the only
    timers: one 20 s connect deadline and the keep-alive.
  - **Energy and allocation.** The bridge uses blocking console reads and asynchronous socket I/O; nothing polls.
    - Sends and reads have synchronous fast paths: when the WebSocket completes at once, no async state machine is
      created; otherwise the slow paths use pooled builders.
    - Allocation tests measure 0 B for framing and for 2,000 rounds of keystroke, 3 KB paste and resize sends, and
      for 5,000 output reads through a real `ManagedWebSocket`. The input thread waits for a send that is still
      pending on a reused per-thread event with a cached continuation (no `AsTask`), also 0 B; the input escape filter
      is 0 B too.
  - **Resize on serial consoles.** termproxy sets the size of the host-side pty that `qm terminal` runs in. A serial
    line has no way to carry window size to the guest, so the guest's `stty size` stays as it was (live check). Users
    run `stty rows R cols C` or xterm's `resize` in the guest. A node shell and a container console run in
    termproxy's pty, so for them the resize reaches the program (from the source; not checked live, see open
    question 6).
  - **Ships:** not in the v0 zip yet, because there is no launcher. The dev helper (`tools/pve-vv.py --term`) plays
    the launcher's part. Usage is in [../../src/PveTerm/README.md](../../src/PveTerm/README.md).
    Amended 2026-10-31: the v1 launcher ([0038](0038-v1-pve-launcher-winui3-unpackaged-self-contained-aot.md)) now
    exists, and `tools/package.ps1` puts `PveTerm.exe` in the zip with it (`-NoLauncher` leaves both out). That is
    the "with the v1 launcher" answer to open question 1, pending the human's confirmation.
  - **Deferred:**
    - the built-in VT (libghostty-vt, [0032](0032-zig-is-allowed-as-a-build-only-tool.md)) behind `ITermChannel`;
    - mintty, and a user command template for other terminals;
    - PuTTY (dropped by the human);
    - any OSC 52 policy: in bridge mode it is the user's terminal's setting.

- **Reason:**
  - Bridging is a few hundred lines on top of the transport the built-in VT needs anyway.
  - Users get their own terminal's fidelity (IME, fonts, selection, scrollback) at once.
  - Keeping the bridge byte-transparent leaves behaviour with the terminal, which users can inspect and configure.
  - Requesting the session in the bridge avoids termproxy's 10 s window when a terminal starts slowly. The cost is
    that the descriptor carries the API credential, which the WebSocket upgrade needs in any case.
  - Costs:
    - Fidelity depends on the user's console host, which this project can't pin.
    - Resize can't reach a guest through a serial line.

- **Open questions (human):**
  1. Ship `PveTerm.exe` in the v0 zip now, with only the dev helper as a launcher, or with the v1 launcher?
  2. On exit the bridge writes `ESC[0m ESC[?25h ESC[?1049l` and turns off mouse, focus and bracketed-paste reporting,
     application cursor keys and keypad mode. That keeps a console the user returns to (cmd, PowerShell) usable
     after a guest program left those modes on. Keep it, or drop it for strict transparency?
  3. Keep the 240 s ping (the documented contract, and a guard against middleboxes), although PVE 9.2 kept a
     335 s idle session open without it? Dropping it would leave the bridge with no idle timer at all.
  4. OSC 52 from the guest reaches the user's terminal, and Windows Terminal writes the clipboard on it by default.
     Acceptable for the bridge?
  5. With a pinned CA, the bridge still requires the certificate to name the host. Is that the intended reading of
     0033?
  6. Container consoles with an API token, and a node shell, were not checked live: the test token has no
     `Sys.Console`, and no container is in scope.

- **Sources** (read 2026-10-30):
  - pve-xtermjs at commit 83a17e9c: `README` (protocol, 5-minute timeout), `xterm.js/src/main.js` (POST, socket URL,
    `binary` subprotocol, `user:ticket\n`, `OK`, `0:`/`1:`/`2`, 30 s ping) and `termproxy/src/main.rs` (ticket line,
    `/access/vncticket` check, `process_queue`, 10 s timeouts, no idle timer). https://git.proxmox.com/?p=pve-xtermjs.git
  - qemu-server `src/PVE/API2/Qemu.pm` (`termproxy`, `vncwebsocket`), pve-manager `PVE/API2/Nodes.pm` (`termproxy`,
    `vncwebsocket`, `get_shell_command`), pve-container `src/PVE/API2/LXC.pm`, and pve-http-server
    `src/PVE/APIServer/AnyEvent.pm` (`websocket_proxy`, frame limits, timeouts). All read at HEAD on git.proxmox.com.
  - Console API: `SetConsoleMode` (`ENABLE_VIRTUAL_TERMINAL_INPUT`, `DISABLE_NEWLINE_AUTO_RETURN`),
    `ReadConsoleInputW`, `CreatePseudoConsole` (Microsoft Learn).

## Answers (the human, 2026-10-31, through agentdesk)

- Open questions 1–4 and 6: "accept all", the current behaviour stands (PveTerm ships with the launcher; the exit
  reset sequence; the 240 s ping; OSC 52 passes through; container and node-shell consoles stay unverified).
- Open question 5 is reopened for discussion: the human's view is "已經 pin CA 可接受憑證與 host 不符合" (with a
  pinned CA, a certificate that doesn't name the host is acceptable). Tracked in agentdesk; the host check stays until
  that is settled.
- Serial terminal size (agentdesk D-6e78ed251399): both a `~r` escape that sends `stty rows R cols C` to the guest on
  demand, and an opt-in `--auto-stty` that sends it on every resize. Off by default, so the bridge stays transparent.
