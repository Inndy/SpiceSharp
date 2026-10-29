#!/usr/bin/env python3
"""Dev helper: fetch a fresh SPICE .vv file from a Proxmox VE API.

Reads credentials from PVE_TOKEN_DONT_COMMIT.txt (PVE_URL, TOKEN_ID, TOKEN_SECRET lines)
at the repo root. Usage: pve-vv.py <vmid> [out.vv | --config]   or   pve-vv.py --list
The API's self-signed certificate is not verified (dev use only).
"""
import json, os, ssl, sys, urllib.request, urllib.parse

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
cfg = {}
with open(os.path.join(root, "PVE_TOKEN_DONT_COMMIT.txt"), encoding="utf-8") as f:
    for line in f:
        if ":" in line:
            k, v = line.split(":", 1)
            cfg[k.strip()] = v.strip()

base = cfg["PVE_URL"].rstrip("/") + "/api2/json"
auth = {"Authorization": f"PVEAPIToken={cfg['TOKEN_ID']}={cfg['TOKEN_SECRET']}"}
ctx = ssl._create_unverified_context()

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
