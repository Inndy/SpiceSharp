#!/usr/bin/env python3
"""Dev helper: fetch a fresh SPICE .vv file from a Proxmox VE API, or open a termproxy console in PveTerm.

Reads credentials from PVE_TOKEN_DONT_COMMIT.txt (PVE_URL, TOKEN_ID, TOKEN_SECRET lines) at the repo root, or in
the nearest parent directory that has it (a worktree checkout finds the main checkout's copy).
Usage: pve-vv.py <vmid> [out.vv | --config]   or   pve-vv.py --list
       pve-vv.py --term-probe [vmid ...]      read-only: version, console privileges, serial ports, TLS chain
       pve-vv.py --term <target> [options]    start PveTerm for a termproxy console: a Windows Terminal tab when
                                              one is found, else a new console (--wt PATH, --console, --here,
                                              --hidden; see term_main below)
Every API request verifies the server the way PveTerm does (docs/decisions/0033): against the CA in the PEM file
that an optional PVE_CA_FILE line names (relative to the token file's folder), else against the system trust store,
with the hostname checked in both cases. No request, and so no credential, goes to an unverified server.
"""
import json, os, ssl, sys, urllib.request, urllib.parse

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
cfg = {}
token_dir = root
while not os.path.exists(os.path.join(token_dir, "PVE_TOKEN_DONT_COMMIT.txt")) and os.path.dirname(token_dir) != token_dir:
    token_dir = os.path.dirname(token_dir)
with open(os.path.join(token_dir, "PVE_TOKEN_DONT_COMMIT.txt"), encoding="utf-8") as f:
    for line in f:
        if ":" in line:
            k, v = line.split(":", 1)
            cfg[k.strip()] = v.strip()
if cfg.get("PVE_CA_FILE"):
    cfg["PVE_CA_FILE"] = os.path.join(token_dir, cfg["PVE_CA_FILE"])

base = cfg["PVE_URL"].rstrip("/") + "/api2/json"
auth = {"Authorization": f"PVEAPIToken={cfg['TOKEN_ID']}={cfg['TOKEN_SECRET']}"}
ctx = ssl.create_default_context()
if cfg.get("PVE_CA_FILE"):
    ctx = ssl.create_default_context(cafile=cfg["PVE_CA_FILE"])  # pinned: only this CA, hostname still checked

def call(method, path, data=None):
    body = urllib.parse.urlencode(data).encode() if data is not None else None
    req = urllib.request.Request(base + path, data=body, method=method, headers=auth)
    with urllib.request.urlopen(req, context=ctx, timeout=15) as r:
        return json.load(r)["data"]

if sys.argv[1] == "--list":
    # Read-only inventory of SPICE-capable VMs (no secrets printed).
    for r in sorted(call("GET", "/cluster/resources?type=vm"), key=lambda r: r.get("vmid", 0)):
        if r.get("type") != "qemu":
            continue
        try:
            c = call("GET", f"/nodes/{r['node']}/qemu/{r['vmid']}/config")
        except Exception as e:
            print(f"{r['vmid']} {r.get('name')}: config not readable ({e})")
            continue
        print(f"{r['vmid']} {r.get('name')} status={r.get('status')} ostype={c.get('ostype')} vga={c.get('vga', '(default)')} "
              f"audio0={c.get('audio0', '-')} agent={c.get('agent', '-')} spice_enh={c.get('spice_enhancements', '-')}")
    sys.exit(0)

def node_of(vmid):
    return next(r["node"] for r in call("GET", "/cluster/resources?type=vm") if r.get("vmid") == vmid)

if sys.argv[1] == "--term-probe":
    # Read-only: what a termproxy console could open with this token. Prints no secrets.
    v = call("GET", "/version")
    print(f"version {v.get('version')} release {v.get('release')} repoid {v.get('repoid')}")
    perms = call("GET", "/access/permissions")
    wanted = ("Sys.Console", "Sys.Audit", "VM.Console", "VM.Audit", "VM.Config.HWType")
    for path in sorted(perms):
        privs = [p for p in wanted if perms[path].get(p)]
        if privs:
            print(f"perm {path}: {' '.join(privs)}")
    for n in call("GET", "/nodes"):
        print(f"node {n['node']} status={n.get('status')}")
    for a in sys.argv[2:]:
        vmid = int(a)
        node = node_of(vmid)
        c = call("GET", f"/nodes/{node}/qemu/{vmid}/config")
        st = call("GET", f"/nodes/{node}/qemu/{vmid}/status/current")
        serials = {k: c[k] for k in ("serial0", "serial1", "serial2", "serial3") if k in c}
        print(f"vm {vmid} on {node}: status={st.get('status')} ostype={c.get('ostype')} vga={c.get('vga', '(default)')} serial={serials or '-'}")
        # Recent console sessions this token can see (a termproxy session is a "vncproxy" task), running or not.
        for t in call("GET", f"/nodes/{node}/tasks?vmid={vmid}&typefilter=vncproxy&limit=5"):
            print(f"  task {t.get('type')} start={t.get('starttime')} end={t.get('endtime', '-')} status={t.get('status', 'running')}")
    import socket
    u = urllib.parse.urlparse(cfg["PVE_URL"])
    # Only to list the certificates the server sends: a bare handshake, no request and no credential on it.
    dump = ssl._create_unverified_context()
    with socket.create_connection((u.hostname, u.port or 8006), timeout=10) as sock:
        with dump.wrap_socket(sock, server_hostname=u.hostname) as tls:
            chain = tls.get_unverified_chain() if hasattr(tls, "get_unverified_chain") else []
            print(f"tls {tls.version()}: server sent {len(chain)} certificate(s)")
            for der in chain:
                pem = ssl.DER_cert_to_PEM_cert(bytes(der))
                print("  cert sha256", __import__("hashlib").sha256(bytes(der)).hexdigest()[:16], "pem bytes", len(pem))
    try:
        with socket.create_connection((u.hostname, u.port or 8006), timeout=10) as sock:
            with ctx.wrap_socket(sock, server_hostname=u.hostname):
                print(f"{'pinned CA' if cfg.get('PVE_CA_FILE') else 'system trust'} + hostname: OK")
    except ssl.SSLError as e:
        print(f"{'pinned CA' if cfg.get('PVE_CA_FILE') else 'system trust'} + hostname: FAILED ({e.reason})")
    sys.exit(0)

def term_main(argv):
    """--term TARGET [--prefetch] [--wt PATH | --console | --hidden | --here] [--exe PATH] [-- COMMAND ...]

    TARGET is qemu:VMID[:serialN], lxc:VMID or node:NAME. Hands PveTerm its connection descriptor through a
    named pipe that only this user (at this integrity level) can open, the same way a launcher does
    (docs/decisions/0036). The token stays inside this process and the pipe; nothing secret is printed, put on a
    command line or written to disk.
    Where PveTerm runs, as the launcher's "Automatic" choice does (docs/decisions/0042, tools/wt_locate.py): a new
    tab in Windows Terminal (`wt.exe -w 0 new-tab --title ... -- PveTerm console --pipe ... --nonce ...`), the one
    set as the default terminal application, else the one behind the wt.exe alias, else stable, Preview or wt.exe on
    PATH; without any, a new console window. --wt PATH uses that wt.exe. --console forces a new console window.
    --here runs PveTerm in this console (e.g. a terminal multiplexer pane). --hidden starts COMMAND without any
    window (for the pseudoconsole harness in tests/Pve.Term.Tests, which shows nothing either). COMMAND replaces
    all of these; its arguments may use {pipe}, {nonce} and {exe}. --prefetch requests the termproxy session here
    instead of in PveTerm. The descriptor carries the PVE_CA_FILE CA when one is configured (PveTerm then pins it),
    else none (system trust). The spiceproxy answer's CA is the cluster CA for SPICE, not necessarily the API's
    issuer.
    """
    import ctypes, hmac, importlib.util, secrets, subprocess, threading
    from ctypes import wintypes as wt
    opts = {"prefetch": False, "hidden": False, "here": False, "console": False, "wt": None, "exe": None}
    command = None
    rest = list(argv)
    target = rest.pop(0)
    while rest:
        a = rest.pop(0)
        if a == "--prefetch": opts["prefetch"] = True
        elif a == "--hidden": opts["hidden"] = True
        elif a == "--here": opts["here"] = True
        elif a == "--console": opts["console"] = True
        elif a == "--wt": opts["wt"] = rest.pop(0)
        elif a == "--exe": opts["exe"] = rest.pop(0)
        elif a == "--": command = rest; break
        else: raise SystemExit(f"unknown option {a}")
    if sum(bool(opts[k]) for k in ("hidden", "here", "console", "wt")) > 1:
        raise SystemExit("--wt, --console, --hidden and --here exclude each other")
    command_given = command is not None
    if command_given and (opts["console"] or opts["wt"]):
        raise SystemExit("--wt and --console choose the default command; they don't apply to -- COMMAND")
    host = None  # (wt.exe, name) when PveTerm opens in a Windows Terminal tab
    if not (command_given or opts["console"] or opts["here"] or opts["hidden"]):
        if opts["wt"]:
            # Checked before the pipe is set up; a directory is no program (review R2). lexists keeps a zero-length
            # App Execution Alias acceptable.
            if not os.path.lexists(opts["wt"]) or os.path.isdir(opts["wt"]):
                raise SystemExit(f"--wt: {opts['wt']} is not a file")
            host = (os.path.abspath(opts["wt"]), "the wt.exe given with --wt")
        else:
            # Loaded by path, so it works under `python -I` too (no script folder on sys.path).
            loc = importlib.util.spec_from_file_location("wt_locate", os.path.join(os.path.dirname(os.path.abspath(__file__)), "wt_locate.py"))
            wt_locate = importlib.util.module_from_spec(loc)
            loc.loader.exec_module(wt_locate)
            found = wt_locate.preferred(wt_locate.scan())
            if found:
                host = (found[0], found[2])
    kind, _, spec = target.partition(":")
    u = urllib.parse.urlparse(cfg["PVE_URL"])
    d = {"v": 1, "apiUrl": f"https://{u.hostname}:{u.port or 8006}", "target": kind,
         "apiToken": f"{cfg['TOKEN_ID']}={cfg['TOKEN_SECRET']}"}
    if kind == "node":
        d["node"] = spec
        path = f"/nodes/{spec}"
    elif kind in ("qemu", "lxc"):
        vmid, _, serial = spec.partition(":")
        d["vmid"] = int(vmid)
        d["node"] = node_of(int(vmid))
        if serial: d["serial"] = serial
        path = f"/nodes/{d['node']}/{kind}/{vmid}"
    else:
        raise SystemExit("target must be qemu:VMID[:serialN], lxc:VMID or node:NAME")
    if cfg.get("PVE_CA_FILE"):
        with open(cfg["PVE_CA_FILE"], encoding="ascii") as f:
            d["caPem"] = f.read()
    if opts["prefetch"]:
        s = call("POST", path + "/termproxy", {"serial": d["serial"]} if "serial" in d else {})
        d.update(termUser=s["user"], termTicket=s["ticket"], termPort=int(s["port"]))
    payload = json.dumps(d).encode()

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    adv = ctypes.WinDLL("advapi32", use_last_error=True)
    k32.GetCurrentProcess.restype = wt.HANDLE
    k32.CreateNamedPipeW.restype = wt.HANDLE
    k32.CreateNamedPipeW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD, wt.DWORD, ctypes.c_void_p]
    k32.ConnectNamedPipe.argtypes = [wt.HANDLE, ctypes.c_void_p]
    k32.ReadFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
    k32.WriteFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
    k32.DisconnectNamedPipe.argtypes = [wt.HANDLE]
    k32.CancelIoEx.argtypes = [wt.HANDLE, ctypes.c_void_p]
    k32.CreateEventW.restype = wt.HANDLE
    k32.CreateEventW.argtypes = [ctypes.c_void_p, wt.BOOL, wt.BOOL, wt.LPCWSTR]
    k32.SetEvent.argtypes = [wt.HANDLE]
    k32.ResetEvent.argtypes = [wt.HANDLE]
    k32.WaitForMultipleObjects.argtypes = [wt.DWORD, ctypes.POINTER(wt.HANDLE), wt.BOOL, wt.DWORD]
    k32.WaitForMultipleObjects.restype = wt.DWORD
    k32.GetOverlappedResult.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(wt.DWORD), wt.BOOL]
    k32.CloseHandle.argtypes = [wt.HANDLE]
    k32.LocalFree.argtypes = [ctypes.c_void_p]
    adv.OpenProcessToken.argtypes = [wt.HANDLE, wt.DWORD, ctypes.POINTER(wt.HANDLE)]
    adv.GetTokenInformation.argtypes = [wt.HANDLE, ctypes.c_int, ctypes.c_void_p, wt.DWORD, ctypes.POINTER(wt.DWORD)]
    adv.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(wt.LPWSTR)]
    adv.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [wt.LPCWSTR, wt.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p]

    def token_sid(info_class):  # 1 = TokenUser, 25 = TokenIntegrityLevel; both start with SID_AND_ATTRIBUTES
        tok = wt.HANDLE()
        if not adv.OpenProcessToken(k32.GetCurrentProcess(), 8, ctypes.byref(tok)): raise ctypes.WinError(ctypes.get_last_error())
        size = wt.DWORD()
        adv.GetTokenInformation(tok, info_class, None, 0, ctypes.byref(size))
        buf = ctypes.create_string_buffer(size.value)
        if not adv.GetTokenInformation(tok, info_class, buf, size, ctypes.byref(size)): raise ctypes.WinError(ctypes.get_last_error())
        text = wt.LPWSTR()
        adv.ConvertSidToStringSidW(ctypes.c_void_p.from_buffer(buf).value, ctypes.byref(text))
        value = text.value
        k32.LocalFree(text)
        k32.CloseHandle(tok)
        return value

    class SA(ctypes.Structure):
        _fields_ = [("nLength", wt.DWORD), ("lpSecurityDescriptor", ctypes.c_void_p), ("bInheritHandle", wt.BOOL)]
    sd = ctypes.c_void_p()
    sddl = f"D:P(A;;GA;;;{token_sid(1)})S:(ML;;NWNR;;;{token_sid(25)})"
    if not adv.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, ctypes.byref(sd), None):
        raise ctypes.WinError(ctypes.get_last_error())
    name = "pveterm-" + secrets.token_hex(16)
    nonce = secrets.token_hex(16)
    sa = SA(ctypes.sizeof(SA), sd, False)
    # PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED; PIPE_REJECT_REMOTE_CLIENTS; one instance.
    pipe = k32.CreateNamedPipeW("\\\\.\\pipe\\" + name, 0x3 | 0x00080000 | 0x40000000, 0x8, 1, 65536, 4096, 0, ctypes.byref(sa))
    k32.LocalFree(sd)
    if pipe in (None, wt.HANDLE(-1).value):
        raise ctypes.WinError(ctypes.get_last_error())
    served = {"ok": False, "why": "no client"}

    # One 30-second deadline for the whole exchange (connect, nonce, descriptor, close), as in Pve.Term's
    # HandoffServer, whether or not the launched command is still running. Every pipe step is overlapped and waits
    # for either its own completion or the `expired` event. That event is a latch: once the one-shot timer sets it,
    # it stays set, so a step started after expiry returns at once instead of missing a signal (no lock, no race).
    # A timed-out step is cancelled with CancelIoEx and waited out before its buffers go; then the pipe is closed.
    class OVERLAPPED(ctypes.Structure):
        _fields_ = [("Internal", ctypes.c_void_p), ("InternalHigh", ctypes.c_void_p), ("Offset", wt.DWORD),
                    ("OffsetHigh", wt.DWORD), ("hEvent", wt.HANDLE)]
    expired = k32.CreateEventW(None, True, False, None)  # manual reset: never closed, the timer may still set it
    io_done = k32.CreateEventW(None, True, False, None)
    if not expired or not io_done:
        raise ctypes.WinError(ctypes.get_last_error())
    deadline = threading.Timer(30, lambda: k32.SetEvent(expired))
    deadline.daemon = True

    class Late(Exception): pass

    def step(start):
        """Runs one overlapped pipe call; returns (ok, bytes, error). Raises Late when the deadline has passed."""
        ov = OVERLAPPED(hEvent=io_done)
        got = wt.DWORD()
        k32.ResetEvent(io_done)
        if not start(ctypes.byref(ov)):
            error = ctypes.get_last_error()
            if error == 535: return True, 0, 0  # ERROR_PIPE_CONNECTED: the client came before ConnectNamedPipe
            if error != 997: return False, 0, error  # anything but ERROR_IO_PENDING failed at once
        handles = (wt.HANDLE * 2)(io_done, expired)
        if k32.WaitForMultipleObjects(2, handles, False, 0xFFFFFFFF) != 0:  # WAIT_OBJECT_0 + 1: expired
            k32.CancelIoEx(pipe, ctypes.byref(ov))
            k32.GetOverlappedResult(pipe, ctypes.byref(ov), ctypes.byref(got), True)  # wait for the cancel to land
            raise Late()
        ok = k32.GetOverlappedResult(pipe, ctypes.byref(ov), ctypes.byref(got), False)
        return bool(ok), got.value, 0 if ok else ctypes.get_last_error()

    def serve():
        stage = "no client"
        try:
            ok, _, error = step(lambda ov: k32.ConnectNamedPipe(pipe, ov))
            if not ok:
                served["why"] = f"connect failed ({error})"
                return
            stage = "nonce"
            line = b""
            buf = ctypes.create_string_buffer(130)
            while b"\n" not in line and len(line) < 130:
                ok, n, _ = step(lambda ov: k32.ReadFile(pipe, buf, 130 - len(line), None, ov))
                if not ok or n == 0: break
                line += buf.raw[:n]
            if not hmac.compare_digest(line.split(b"\n")[0], nonce.encode()):
                served["why"] = "wrong nonce"
                return
            stage = "descriptor"
            data = ctypes.create_string_buffer(payload, len(payload))
            ok, n, _ = step(lambda ov: k32.WriteFile(pipe, data, len(payload), None, ov))
            if not ok or n != len(payload):
                served["why"] = "write failed"
                return
            # PveTerm closes its end once it holds the whole descriptor; wait for that (a broken pipe) instead of an
            # uncancellable FlushFileBuffers.
            stage = "close"
            one = ctypes.create_string_buffer(1)
            while True:
                ok, n, _ = step(lambda ov: k32.ReadFile(pipe, one, 1, None, ov))
                if not ok or n == 0: break
            served.update(ok=True, why="served")
        except Late:
            served["why"] = {"no client": "no client in time", "nonce": "nonce not sent in time",
                             "descriptor": "descriptor not collected in time", "close": "descriptor not collected in time"}[stage]
        finally:
            deadline.cancel()
            k32.DisconnectNamedPipe(pipe)
            k32.CloseHandle(pipe)
            k32.CloseHandle(io_done)
    server = threading.Thread(target=serve, daemon=True)
    deadline.start()
    server.start()

    exe = opts["exe"] or os.path.join(root, "src", "PveTerm", "bin", "Debug", "net10.0-windows10.0.17763.0", "PveTerm.exe")
    if command is None:
        command = ["{exe}", "console", "--pipe", "{pipe}", "--nonce", "{nonce}"]
        if host:
            # As Pve.Api.TerminalLauncher: the descriptor's display name as the tab title, without the ';' that
            # Windows Terminal reads as a command separator, or quotes.
            title = {"node": f"{d['node']} shell", "lxc": f"CT {d.get('vmid')} on {d['node']}"}.get(
                kind, f"VM {d.get('vmid')} {d.get('serial') or 'serial'} on {d['node']}")
            title = "".join(c for c in title if c.isprintable() and c not in ';"')[:80] or "PveTerm"
            # The bridge path keeps its ';' but escaped as "\;", a literal ';' to Windows Terminal (review R3).
            command = [host[0], "-w", "0", "new-tab", "--title", title, "--", exe.replace(";", "\\;")] + command[1:]
    command = [c.replace("{pipe}", name).replace("{nonce}", nonce).replace("{exe}", exe) for c in command]
    # CREATE_NO_WINDOW : this console (--here, e.g. a terminal multiplexer pane) : nothing for wt.exe (a GUI
    # program) : CREATE_NEW_CONSOLE
    flags = 0x08000000 if opts["hidden"] else 0 if opts["here"] or host else 0x00000010
    where = (f"in {host[1]} ({host[0]})" if host else "in this console" if opts["here"]
             else "without a window" if opts["hidden"] else "by COMMAND" if command_given else "in a new console")
    print(f"term: {kind} {spec} via the API host; session requested by {'this helper' if opts['prefetch'] else 'PveTerm'}; "
          f"CA {'pinned from PVE_CA_FILE' if 'caPem' in d else 'system trust'}; {where}", flush=True)
    # Hidden runs keep this helper's stdio, so a harness can report; a new console window has its own.
    child = subprocess.Popen(command, creationflags=flags, stdout=sys.stdout if opts["hidden"] else None,
                             stderr=sys.stderr if opts["hidden"] else None)
    code = child.wait()
    # A launcher such as wt.exe exits before PveTerm, which it starts, has picked up the descriptor.
    server.join(timeout=30)
    print(f"term: hand-off {served['why']}; command exited with {code}", flush=True)
    return code

if sys.argv[1] == "--term":
    sys.exit(term_main(sys.argv[2:]))

if sys.argv[1] == "--isos":
    # ISO images on every storage that holds them (read-only).
    for n in call("GET", "/nodes"):
        stores = call("GET", f"/nodes/{n['node']}/storage?content=iso")
        print(f"{n['node']}: iso storages {[s['storage'] for s in stores]}")
        for s in stores:
            try:
                for v in call("GET", f"/nodes/{n['node']}/storage/{s['storage']}/content?content=iso"):
                    print(f"{n['node']} {v['volid']} {v.get('size', 0) // (1 << 20)} MiB")
            except Exception as e:
                print(f"{n['node']} {s['storage']}: {e}")
    sys.exit(0)

if sys.argv[1] in ("--enable-media", "--attach-iso", "--reboot"):
    # Changes for the test VMs the user authorized (audio + video streaming, guest-tools ISO, reboot).
    vmid = int(sys.argv[2])
    node = node_of(vmid)
    if sys.argv[1] == "--enable-media":
        # Separate calls: spice_enhancements needs root on Proxmox, audio0 only VM.Config.HWType.
        for key, value in (("audio0", "device=ich9-intel-hda,driver=spice"), ("spice_enhancements", "videostreaming=all")):
            try:
                call("PUT", f"/nodes/{node}/qemu/{vmid}/config", {key: value})
                print(f"{vmid}: set {key}")
            except Exception as e:
                print(f"{vmid}: {key} not set: {e}")
    elif sys.argv[1] == "--attach-iso":
        call("PUT", f"/nodes/{node}/qemu/{vmid}/config", {"ide2": f"{sys.argv[3]},media=cdrom"})
    else:
        print(call("POST", f"/nodes/{node}/qemu/{vmid}/status/reboot", {}))
    print(f"{sys.argv[1]} done for {vmid} on {node}")
    sys.exit(0)

vmid = int(sys.argv[1])
out = sys.argv[2] if len(sys.argv) > 2 else f"{vmid}.vv"
node = node_of(vmid)
if out == "--config":
    # Display/input-related VM settings only (no secrets).
    c = call("GET", f"/nodes/{node}/qemu/{vmid}/config")
    for k in ("ostype", "vga", "tablet", "agent", "usb0", "usb1", "audio0", "machine"):
        print(f"{k}={c.get(k, '(default)')}")
    sys.exit(0)
proxy = urllib.parse.urlparse(cfg["PVE_URL"]).hostname
d = call("POST", f"/nodes/{node}/qemu/{vmid}/spiceproxy", {"proxy": proxy})
with open(out, "w", encoding="utf-8", newline="\n") as f:
    f.write("[virt-viewer]\n")
    for k, v in d.items():
        f.write(f"{k}={v}\n")
print(f"wrote {out} (node {node}, vmid {vmid}); keys: {', '.join(d)}")
