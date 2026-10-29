#!/usr/bin/env python3
"""Removes the VM's identity from a spicerec fixture directory before it is committed.

Usage: scrub.py <fixture dir> <guest name>

In every *.spicerec file, the server-to-client stream is reassembled across records and
- each occurrence of <guest name> (from SPICE_MSG_MAIN_NAME) is replaced by a placeholder of the same length,
- in main-*.spicerec, the 16-byte payload of SPICE_MSG_MAIN_UUID (mini header: type 114, size 16) is zeroed.
Lengths never change, so record framing and replay stay valid. Prints what it changed; exits 1 if the name
is still present afterwards.
"""
import struct
import sys
from pathlib import Path

MAGIC = b"SPREC1\n"
MAIN_UUID_HEADER = struct.pack("<HI", 114, 16)


def records(data):
    """Yields (payload file offset, length, direction)."""
    pos = len(MAGIC)
    while pos < len(data):
        direction, _elapsed, length = struct.unpack_from("<BQI", data, pos)
        pos += 13
        yield pos, length, direction
        pos += length


def scrub(path, name, placeholder):
    data = bytearray(path.read_bytes())
    if not data.startswith(MAGIC):
        raise SystemExit(f"{path}: not a spicerec file")
    # Server-to-client stream, with a map from stream offset to file offset.
    stream = bytearray()
    where = []
    for off, length, direction in records(data):
        if direction == 0:
            stream += data[off:off + length]
            where.extend(range(off, off + length))
    changed = 0

    def patch(start, replacement):
        nonlocal changed
        for i, b in enumerate(replacement):
            data[where[start + i]] = b
        changed += 1

    i = stream.find(name)
    while i >= 0:
        patch(i, placeholder)
        i = stream.find(name, i + len(name))
    # MAIN_UUID is sent once, early, on the main channel only; don't pattern-match other channels' binary data.
    i = stream.find(MAIN_UUID_HEADER) if path.name.startswith("main-") else -1
    if i >= 0:
        patch(i + len(MAIN_UUID_HEADER), bytes(16))
    if changed:
        path.write_bytes(data)
        print(f"{path}: {changed} replacement(s)")
    return name not in data


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    folder, name = Path(sys.argv[1]), sys.argv[2].encode()
    placeholder = (b"guest" * (len(name) // 5 + 1))[:len(name)]
    ok = all([scrub(p, name, placeholder) for p in sorted(folder.glob("*.spicerec"))])
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
