# PveTerm

Opens a Proxmox VE text console (a node shell, a VM's serial console or a container console) in the console window
it runs in, so the user's own terminal shows it. The bridge is byte-transparent: the guest's bytes go to the console
unchanged, and keys go back wrapped in termproxy's `0:LEN:DATA` messages. The design, protocol facts and open
questions are in [decision 0036](../../docs/decisions/0036-v1-terminal-bridge-to-the-users-terminal-first.md).

PveTerm is not in the v0 zip yet. A launcher (the v1 client, or `tools/pve-vv.py --term` for development) starts it
and hands it the connection data.

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
| Windows Terminal | `wt.exe -w new new-tab --title "VM 100 serial0" -- "C:\path\PveTerm.exe" console --pipe NAME --nonce HEX` |
| conhost | `conhost.exe "C:\path\PveTerm.exe" console --pipe NAME --nonce HEX` |
| The default terminal | start `PveTerm.exe console ...` with `CREATE_NEW_CONSOLE`; Windows 11 opens it in the default terminal application |

Windows Terminal reads `;` in its arguments as a command separator. PveTerm's arguments never contain one.

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

## Development

```sh
python tools/pve-vv.py --term-probe 100              # read-only: privileges, serial ports, TLS
python tools/pve-vv.py --term qemu:100:serial0       # opens PveTerm in a new console window
python tools/pve-vv.py --term qemu:100:serial0 -- wt.exe -w new new-tab -- "{exe}" console --pipe "{pipe}" --nonce "{nonce}"
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
