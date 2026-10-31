# PveTerm

Opens a Proxmox VE text console (a node shell, a VM's serial console or a container console) in the console window
it runs in, so the user's own terminal shows it. The bridge is byte-transparent: the guest's bytes go to the console
unchanged, and keys go back wrapped in termproxy's `0:LEN:DATA` messages. The design, protocol facts and open
questions are in [decision 0036](../../docs/decisions/0036-v1-terminal-bridge-to-the-users-terminal-first.md).

A launcher starts it and hands it the connection data: the v1 PVE launcher (`src/PveLauncher`, decision
[0038](../../docs/decisions/0038-v1-pve-launcher-winui3-unpackaged-self-contained-aot.md)), with which it ships in the
zip, or `tools/pve-vv.py --term` for development. `Pve.Api.TerminalLauncher` is the launcher's side.

## Command line

```text
PveTerm console --pipe NAME --nonce HEX [--keepalive SECONDS] [--escape CHAR|none]
```

- The launcher creates the named pipe `\\.\pipe\NAME` and passes NAME and a one-time HEX nonce. The connection data
  (API URL, target, API token or login cookie, optional pinned CA) travels only through that pipe. Neither the
  command line nor the disk ever holds a secret. `Pve.Term.HandoffServer` is the launcher side.
- `--keepalive` sets the idle time before a termproxy ping: default 240 s, `0` turns it off.
- `--escape` sets the escape character (default `~`, see below); `none` turns the escape off. `.` and `?` are
  refused, since they are the escape's commands.
- Exit codes: 0 the session ended, 1 the connection failed, 2 bad arguments, 3 the hand-off failed, 4 no usable
  console.

## Starting it in a terminal

A launcher fills in NAME and HEX:

| Terminal | Command |
|---|---|
| Windows Terminal | `"%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe" -w 0 new-tab --title "VM 100 serial0 on pve1" -- "C:\path\PveTerm.exe" console --pipe NAME --nonce HEX` |
| conhost | `conhost.exe "C:\path\PveTerm.exe" console --pipe NAME --nonce HEX` |
| The default terminal | start `PveTerm.exe console ...` with `CREATE_NEW_CONSOLE`; Windows 11 opens it in the default terminal application |

Windows Terminal reads `;` in its arguments as a command separator. PveTerm's arguments never contain one, and the
title has it removed.

The launcher and `tools/pve-vv.py --term` pick Windows Terminal by themselves when it is installed
([decision 0042](../../docs/decisions/0042-terminal-choice-detects-windows-terminal-automatic-by-default.md)), and
start the wt.exe they found by its full path:

1. the Windows Terminal set as Windows' default terminal application (Settings, System, For developers, Terminal);
2. otherwise the `wt.exe` app execution alias, which runs the package turned on under Settings, Apps, Advanced app
   settings, App execution aliases;
3. otherwise Windows Terminal, then Windows Terminal Preview (each package's own alias), then `wt.exe` on PATH.

Without any of them PveTerm opens in a new console. `python tools/wt_locate.py` prints what this machine has.

## Behaviour

- Raw keyboard input in VT mode: Ctrl+C goes to the guest as a byte, and so does Ctrl+Space (NUL).
- Escapes, as in ssh, typed at the start of a line (first in the session or right after Enter):

  | Keys | Does |
  |---|---|
  | `~.` | quits PveTerm; the guest's shell keeps running |
  | `~~` | sends one `~` |
  | `~?` | shows the escapes |

  A `~` followed by any other key sends both. Nothing times out: a `~` at the start of a line waits for the next
  key. A plain paste counts as typing, so pasting text that contains Enter then `~.` quits, as it does in ssh. A
  bracketed paste (when the guest's shell or editor turned it on) is never read as an escape. Closing the window or
  tab also disconnects.
- Resizing the window sends the new size. A serial console can't pass the size to the guest: run
  `stty rows R cols C` (or xterm's `resize`) there.
- The console's modes, code pages and title are put back when PveTerm exits.
- A VM's serial port takes one client at a time. While another console (for example the web UI's) has it open,
  PveTerm connects but shows nothing until that one closes.

## VNC bridge

```text
PveTerm vnc --pipe NAME --nonce HEX [--viewer auto|tigervnc|tightvnc|ultravnc|realvnc|none]
            [--keepalive SECONDS] [--wait SECONDS] [-- COMMAND [ARG...]]
```

Opens a VM's VNC display in the user's own VNC viewer
([decision 0043](../../docs/decisions/0043-vnc-consoles-bridge-to-the-users-vnc-viewer.md)).

- The descriptor is the same as for `console`, with `"console": "vnc"` and a qemu target.
- PveTerm requests `vncproxy`, opens `vncwebsocket` and authenticates to QEMU with the session's one-time password.
- It then listens on a one-session port on 127.0.0.1 and starts the viewer at `127.0.0.1::PORT`.
- Only a process of the same user, at the same or a higher integrity level, may connect. The viewer sees security
  type None.
- After the handshake, bytes are relayed unchanged. The only exception: after `--keepalive` idle seconds (default
  600, `0` turns it off), PveTerm sends one incremental update request between two viewer messages, because
  Proxmox ends a VNC session after 15 idle minutes.
- `--viewer` picks a detected viewer; `none` prints the address instead.
- `-- COMMAND` runs any program, with `{host}`, `{port}`, `{hostport}` and `{title}` filled in.
- Status lines go to standard error (the launcher reads them).
- Exit codes: 0 the session ended, 1 the connection failed, 2 bad arguments, 3 the hand-off failed, 4 no viewer
  connected.

For development: `python tools/pve-vv.py --vnc VMID [--viewer V] [--here|--hidden] [-- COMMAND]`.
`tools/vnc-probe.py PORT` is a headless test viewer: it prints ServerInit and the first update, and sends the guest
no input. For example: `python tools/pve-vv.py --vnc VMID --here -- python -I tools/vnc-probe.py {port}`.

## Development

```sh
python tools/pve-vv.py --term-probe 100              # read-only: privileges, serial ports, TLS
python tools/pve-vv.py --term qemu:100:serial0       # a Windows Terminal tab if one is found, else a new console
python tools/pve-vv.py --term qemu:100:serial0 --console               # always a new console window
python tools/pve-vv.py --term qemu:100:serial0 --wt "D:\tools\wt.exe"  # this wt.exe
python tools/pve-vv.py --term qemu:100:serial0 --here                  # in this console (a multiplexer pane)
python tools/pve-vv.py --term qemu:100:serial0 -- wt.exe -w new new-tab -- "{exe}" console --pipe "{pipe}" --nonce "{nonce}"
python tools/wt_locate.py                            # which Windows Terminal the helper and the launcher pick
```

The helper verifies the API's certificate for its own requests the way PveTerm does: against the system roots, or
against a CA PEM named by a `PVE_CA_FILE: path` line in `PVE_TOKEN_DONT_COMMIT.txt` (relative to that file's folder),
which it also hands to PveTerm to pin. Don't use the CA from a `.vv` file or a `spiceproxy` answer for this: that is
the cluster's SPICE CA, not necessarily the API certificate's issuer.

The window-free tests in `tests/Pve.Term.Tests` run PveTerm under a pseudoconsole against a loopback fake termproxy.
For a live check without a window:

```sh
python tools/pve-vv.py --term qemu:100:serial0 --hidden -- \
  tests/Pve.Term.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pve.Term.Tests.exe --conpty-run 100 30 script.txt -- \
  "{exe}" console --pipe "{pipe}" --nonce "{nonce}"
```

`script.txt` holds lines such as `expect TEXT`, `type TEXT` (with `\r`, `\e` and `\xHH` escapes), `resize CxR`,
`sleep MS` and `close` (see `tests/Pve.Term.Tests/Probe.cs`).
