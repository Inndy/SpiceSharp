#!/usr/bin/env python3
"""Headless RFB probe for the PveTerm VNC bridge (docs/decisions/0043). No window, and it sends the guest no input.

Usage: vnc-probe.py PORT [--hold SECONDS]
Connects to 127.0.0.1:PORT, does the handshake the bridge presents (RFB 3.8, security None, shared ClientInit),
prints ServerInit, asks for the encodings a TigerVNC-like viewer asks for, requests one full update and prints
every message and rectangle header until that update is complete. With --hold it then stays idle for SECONDS and
asks for another full update, to show the session survived the idle time. Typical use, as the bridge's viewer:
    python tools/pve-vv.py --vnc <vmid> --here -- python -I tools/vnc-probe.py {port}
"""
import socket, struct, sys, time

ENCODINGS = [16, 0, -223, -308, -239, -314, -258, -261, -257, 0xC0A1E5CE - (1 << 32)]  # ZRLE, Raw, pseudo-encodings
NAMES = {0: "Raw", 1: "CopyRect", 5: "Hextile", 6: "Zlib", 7: "Tight", 16: "ZRLE", -223: "DesktopSize",
         -308: "ExtendedDesktopSize", -239: "Cursor", -314: "AlphaCursor", -258: "QEMUExtendedKeyEvent",
         -261: "LEDState", -257: "PointerTypeChange", 0xC0A1E5CE - (1 << 32): "ExtendedClipboard"}


def recv(s, n):
    b = b""
    while len(b) < n:
        c = s.recv(n - len(b))
        if not c:
            raise EOFError(f"connection closed after {len(b)} of {n} bytes")
        b += c
    return b


def main():
    port = int(sys.argv[1])
    hold = int(sys.argv[sys.argv.index("--hold") + 1]) if "--hold" in sys.argv else 0
    s = socket.create_connection(("127.0.0.1", port), timeout=60)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    t0 = time.monotonic()
    print("probe: server version", recv(s, 12))
    s.sendall(b"RFB 003.008\n")
    n = recv(s, 1)[0]
    types = list(recv(s, n))
    print("probe: security types", types)
    s.sendall(bytes([1]))
    result = struct.unpack(">I", recv(s, 4))[0]
    if result:
        raise SystemExit("probe: refused: " + recv(s, struct.unpack(">I", recv(s, 4))[0]).decode("latin-1"))
    s.sendall(bytes([1]))  # shared
    w, h, bpp, depth, be, tc, rmax, gmax, bmax, rs, gs, bs = struct.unpack(">HHBBBBHHHBBB3x", recv(s, 20))
    name = recv(s, struct.unpack(">I", recv(s, 4))[0]).decode("utf-8", "replace")
    print(f"probe: ServerInit {w}x{h} bpp={bpp} depth={depth} big-endian={be} true-colour={tc} max={rmax}/{gmax}/{bmax} "
          f"shift={rs}/{gs}/{bs} name={name!r} ({time.monotonic() - t0:.2f} s after connect)")
    s.sendall(struct.pack(">BxH", 2, len(ENCODINGS)) + b"".join(struct.pack(">i", e) for e in ENCODINGS))
    s.sendall(struct.pack(">BBHHHH", 3, 0, 0, 0, w, h))
    full_updates = 0
    while True:
        mtype = recv(s, 1)[0]
        if mtype == 0:
            count = struct.unpack(">xH", recv(s, 3))[0]
            pixels = False
            for _ in range(count):
                x, y, rw, rh, enc = struct.unpack(">HHHHi", recv(s, 12))
                extra = ""
                if enc == 16:
                    ln = struct.unpack(">I", recv(s, 4))[0]
                    recv(s, ln)
                    extra = f" {ln} compressed bytes"
                    pixels = True
                elif enc == 0:
                    recv(s, rw * rh * bpp // 8)
                    pixels = True
                elif enc == -239:
                    recv(s, rw * rh * bpp // 8 + (rw + 7) // 8 * rh)
                elif enc == -314:
                    inner = struct.unpack(">i", recv(s, 4))[0]
                    if inner != 0:
                        raise SystemExit(f"probe: alpha cursor with encoding {inner}, not handled")
                    recv(s, rw * rh * 4)
                elif enc == -308:
                    screens = recv(s, 4)[0]
                    recv(s, 16 * screens)
                    extra = f" {screens} screen(s), reason {y}"
                elif enc == -261:
                    extra = f" leds={recv(s, 1)[0]:#x}"
                elif enc == -257:
                    extra = " absolute" if x else " relative"
                elif enc in (-223, -258):
                    pass
                else:
                    raise SystemExit(f"probe: encoding {enc} not handled; stopping")
                print(f"probe:   rect {x},{y} {rw}x{rh} {NAMES.get(enc, enc)}{extra}")
                if enc == -223 or enc == -308:
                    w, h = rw, rh
            print(f"probe: FramebufferUpdate with {count} rectangle(s) ({time.monotonic() - t0:.2f} s)")
            if pixels:
                full_updates += 1
                if full_updates == 1 and hold:
                    print(f"probe: idle for {hold} s", flush=True)
                    s.settimeout(hold + 120)
                    time.sleep(hold)
                    s.sendall(struct.pack(">BBHHHH", 3, 0, 0, 0, w, h))
                    continue
                print("probe: done")
                return
        elif mtype == 2:
            print("probe: Bell")
        elif mtype == 3:
            ln = struct.unpack(">xxxi", recv(s, 7))[0]
            recv(s, abs(ln))
            print(f"probe: ServerCutText length {ln}")
        elif mtype == 255:
            sub = recv(s, 1)[0]
            raise SystemExit(f"probe: QEMU server message {sub}, not handled")
        else:
            raise SystemExit(f"probe: message type {mtype}, not handled")


if __name__ == "__main__":
    main()
