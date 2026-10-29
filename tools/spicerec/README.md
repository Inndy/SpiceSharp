# SPICE recorder

```powershell
dotnet build tools/spicerec
python tools/pve-vv.py <vmid> record.vv
dotnet tools/spicerec/bin/Debug/net10.0/spicerec.dll record.vv tests/fixtures/win7-desktop 8
python tools/spicerec/scrub.py tests/fixtures/win7-desktop <guest name>
```

**Scrub before committing.** The main channel carries the VM's name (SPICE_MSG_MAIN_NAME) and UUID
(SPICE_MSG_MAIN_UUID). `scrub.py` replaces the name with a same-length placeholder and zeroes the UUID in place, so
record framing and replays are unchanged; it fails if the name is still present. `session.json` holds no title, host
or guest name. Name fixtures after the guest OS (`win7-*`, `win81-*`), never after a VM ID or hostname.

Usage: `spicerec <file.vv> <outdir> [seconds] [--script <file>]` (default 8 seconds after linking, or after the script finishes).
Use a fresh ticket immediately before connecting. The .vv loader honors
delete-this-file=1. Build first so an expiring ticket is not spent compiling.
Choose a new output directory: existing channel recordings are never overwritten.
This is a separate executable; spice-cli was not changed.

`--script tests/fixtures/scripts/windows-rich.txt` runs after the first primary
surface appears and all input channels are linked. Scripts are parsed and
validated before the ticket is loaded. Blank lines and `#` comments are ignored.
Supported commands:

```text
wait 1500
key ctrl+esc
key esc
key win+r
type notepad
key enter
move 400 300
click left
dclick left
drag 100 100 600 400
```

Keys use XT set-1 scancodes, with E0 extended prefixes. Named modifiers include
win, ctrl, alt and shift; enter, esc, tab, space, backspace, delete, arrows,
home/end and F1-F12 are supported. Type emits printable ASCII with a US keyboard
layout (the guest must use that layout and compatible IME state). Combos release
keys in reverse order, and cleanup releases all keys/buttons even after failure.
Clicks have fixed press and inter-click intervals; drags use 20 fixed steps.
Absolute mouse commands fail explicitly unless the session is in client mouse
mode. Timing controls live interaction only; offline replay ignores timestamps.

The supplied script opens/dismisses Start, launches Paint, attempts a keyboard
window move, closes Paint, opens the standard Windows wallpaper JPEG, and closes
its viewer. It creates no image/text document. It leaves the desktop visible;
Windows may retain normal application/run-history changes. It is not an
assertion-based automation framework: a missing app/file or unexpected guest
dialog must be checked in the recording/replay. Both rich captures have LZ,
GLZ and QUIC traffic; neither contains CopyBits with these guest display setups.

Each channel's plaintext stream is tapped after TLS. Files are named
`<type>-<id>.spicerec`, e.g. `display-0.spicerec`; the main opener's `main` label
is normalized to `main:0`. Each file starts with the seven ASCII bytes
`SPREC1\n`, followed by records:

| Field | Encoding |
|---|---|
| Direction | u8: 0 = server to client, 1 = client to server |
| Elapsed time | u64 little endian, microseconds since the shared session start, measured with Stopwatch |
| Payload length | u32 little endian |
| Payload | Exactly length bytes, no additional framing |

These records describe completed Stream reads/writes, so record boundaries are
independent of SPICE message boundaries. Each tap copies the completed buffer
to a queue; a background consumer writes buffered files. There is no disk flush,
await, or timer on each receive/send path. This adds a buffer copy and allocation;
timing overhead has not been benchmarked. Disposal drains the writer queue and
surfaces disk failures instead of silently producing successful metadata.

`session.json` contains an explicit allowlist: title, host, guest name, channel
labels, primary size, elapsed duration, canvas stats and error count. It never
serializes the VvConnection or its password. Client-to-server channel bytes
contain the RSA-encrypted, single-use ticket following the link exchange (128
bytes with these servers). They contain no plaintext ticket. Recordings include
all channel traffic, including guest display, cursor and agent messages; avoid
capturing sensitive guest activity. No .vv, API credential, or plaintext
password is part of the fixtures.

Replay: `dotnet test tests/Spice.Replay.Tests`. ReplayOpener ignores elapsed times
and outbound writes, supplies inbound bytes in original channel order, and
keeps exhausted channels open until session cancellation. Display completion is
signalled on its next read, after the final complete message handler ran. The
30-second test timeout is a failure watchdog, not a scheduling input.

Tests compare decoded PNG pixel arrays, dimensions and zero canvas errors. A
missing golden writes `golden.actual.png` and fails; tests never overwrite a
golden. Initial candidates were visually checked and copied per task 3; see
tests/fixtures/README.md for review status and observed codec coverage.
