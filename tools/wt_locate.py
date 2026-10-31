#!/usr/bin/env python3
"""Finds Windows Terminal's wt.exe the way Pve.Api.WindowsTerminalLocator does (docs/decisions/0042).

No process is started and no package is queried: file checks, one registry value (read only) and the reparse data
of the generic alias. tools/pve-vv.py --term uses it. Run it directly to see what this machine has
(`python tools/wt_locate.py`), or with --self-test to check the resolution order against made-up machines.

Automatic picks, in this order:
  1. the Windows Terminal set as the default terminal application (HKCU\\Console\\%%Startup DelegationTerminal);
  2. the generic alias %LOCALAPPDATA%\\Microsoft\\WindowsApps\\wt.exe, the package whose alias the user turned on;
  3. the stable package's own alias, then Preview's, then wt.exe on PATH.
"""
import os, stat, struct, sys

STABLE_FAMILY = "Microsoft.WindowsTerminal_8wekyb3d8bbwe"
PREVIEW_FAMILY = "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe"
# The terminal-host class IDs from the packages' AppxManifest.xml (stable 1.24, Preview 1.26).
STABLE_CLSID = "{E12CFF52-A866-4C77-9A90-F570A7AA2C6B}"
PREVIEW_CLSID = "{86633F1F-6454-40EC-89CE-DA4EBA977EE2}"
IO_REPARSE_TAG_APPEXECLINK = 0x8000001B
NAMES = {"stable": "Windows Terminal", "preview": "Windows Terminal Preview"}


def file_exists(path):
    """A file, as .NET's File.Exists: not a directory (review R2). The aliases are zero-length reparse points that
    os.stat can't follow on older Pythons, so lstat decides, and isdir rejects a link to a directory."""
    try:
        st = os.lstat(path)
    except (OSError, ValueError):
        return False
    return not stat.S_ISDIR(st.st_mode) and not os.path.isdir(path)


def default_terminal():
    """The DelegationTerminal class ID of the default terminal application, or None. Reads only."""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Console\%%Startup") as key:
            value, _ = winreg.QueryValueEx(key, "DelegationTerminal")
            return value if isinstance(value, str) else None
    except (OSError, ImportError):
        return None


def parse_alias_package(data):
    """The package family name in an App Execution Alias's reparse data, or None.

    Malformed data is refused, never clipped (review R4): the declared payload must fit in the buffer, be version 3
    (the only layout read: strings from byte 12), hold whole UTF-16 code units and terminate the family inside it.
    """
    if len(data) < 12:
        return None
    tag, length, _, version = struct.unpack_from("<IHHI", data)
    end = 8 + length
    if tag != IO_REPARSE_TAG_APPEXECLINK or version != 3 or end > len(data) or end <= 12 or (end - 12) % 2:
        return None
    text = bytes(data[12:end]).decode("utf-16-le", "replace")
    if "\0" not in text:
        return None
    return text.split("\0", 1)[0] or None


def alias_package(path):
    """Reads the reparse data of an alias at a full path; None for anything else or on any error."""
    import ctypes
    from ctypes import wintypes as wt
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.CreateFileW.restype = wt.HANDLE
    k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD, wt.DWORD, wt.HANDLE]
    k32.DeviceIoControl.argtypes = [wt.HANDLE, wt.DWORD, ctypes.c_void_p, wt.DWORD, ctypes.c_void_p, wt.DWORD,
                                    ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
    k32.CloseHandle.argtypes = [wt.HANDLE]
    # No access rights; share everything; OPEN_EXISTING; FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS.
    h = k32.CreateFileW(path, 0, 7, None, 3, 0x00200000 | 0x02000000, None)
    if h in (None, wt.HANDLE(-1).value):
        return None
    try:
        buf = ctypes.create_string_buffer(16 * 1024)  # MAXIMUM_REPARSE_DATA_BUFFER_SIZE
        got = wt.DWORD()
        if not k32.DeviceIoControl(h, 0x000900A8, None, 0, buf, len(buf), ctypes.byref(got), None):  # FSCTL_GET_REPARSE_POINT
            return None
        return parse_alias_package(buf.raw[:got.value])
    finally:
        k32.CloseHandle(h)


def _flavor_of_family(family):
    return {STABLE_FAMILY.lower(): "stable", PREVIEW_FAMILY.lower(): "preview"}.get((family or "").lower())


def _flavor_of_clsid(clsid):
    return {STABLE_CLSID.lower(): "stable", PREVIEW_CLSID.lower(): "preview"}.get((clsid or "").strip().lower())


def _full(path):
    return bool(os.path.splitdrive(path)[0]) and os.path.isabs(path)


def scan(exists=file_exists, getenv=os.environ.get, default=default_terminal, alias_pkg=alias_package):
    """What is installed: {"stable", "preview", "alias", "path"} -> (exe, flavor, name) or None, and "default"."""
    found = {"stable": None, "preview": None, "alias": None, "path": None,
             "default": _flavor_of_clsid(default())}
    local = (getenv("LOCALAPPDATA") or "").strip()
    if local and _full(local):
        apps = os.path.join(local, "Microsoft", "WindowsApps")
        for flavor, family in (("stable", STABLE_FAMILY), ("preview", PREVIEW_FAMILY)):
            p = os.path.join(apps, family, "wt.exe")
            if exists(p):
                found[flavor] = (p, flavor, NAMES[flavor])
        p = os.path.join(apps, "wt.exe")
        if exists(p):
            flavor = _flavor_of_family(alias_pkg(p))
            found["alias"] = (p, flavor, NAMES[flavor] if flavor else "Windows Terminal (wt.exe alias)")
    # Compared after normalizing separators and dot segments, which needs no working folder for a full path, so
    # "WindowsApps\." or "C:/.../WindowsApps" is recognized as the alias (review R1).
    alias = os.path.normcase(os.path.normpath(found["alias"][0])) if found["alias"] else None
    for entry in (getenv("PATH") or "").split(";"):
        d = entry.strip().strip('"')
        if not d or not _full(d):
            continue  # a relative entry would depend on the working folder
        p = os.path.normpath(os.path.join(d, "wt.exe"))
        if os.path.normcase(p) == alias:
            continue  # the WindowsApps folder on PATH: already counted, and not an unknown wt.exe
        if exists(p):
            found["path"] = (p, None, "wt.exe on PATH")
            break
    return found


def preferred(found):
    """What Automatic opens, or None (then a new console)."""
    d = found["default"]
    if d and found[d]:
        return found[d]
    return found["alias"] or found["stable"] or found["preview"] or found["path"]


def for_choice(found, choice):
    """auto, wt (the stable package, else a wt.exe that isn't Preview) or wt-preview."""
    alias = found["alias"]
    if choice == "wt":
        return found["stable"] or (alias if alias and alias[1] != "preview" else None) or found["path"]
    if choice == "wt-preview":
        return found["preview"] or (alias if alias and alias[1] == "preview" else None)
    return preferred(found)


def _self_test():
    local = r"C:\Users\u\AppData\Local"
    apps = local + r"\Microsoft\WindowsApps"
    stable, preview, alias = (apps + r"\Microsoft.WindowsTerminal_8wekyb3d8bbwe\wt.exe",
                              apps + r"\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\wt.exe", apps + r"\wt.exe")
    portable = r"D:\tools\terminal\wt.exe"
    path_var = r'C:\Windows\system32;relative\bin;"D:\tools\terminal";;' + apps

    def machine(files, family=None, clsid=None, local_dir=local):
        files = {f.lower() for f in files}
        return scan(lambda p: p.lower() in files, {"LOCALAPPDATA": local_dir, "PATH": path_var}.get,
                    lambda: clsid, lambda p: family)

    cases = [
        (machine([stable, alias], STABLE_FAMILY), "auto", alias),
        (machine([preview, alias], PREVIEW_FAMILY), "auto", alias),
        (machine([preview, alias], PREVIEW_FAMILY), "wt", None),
        (machine([stable, preview, alias], PREVIEW_FAMILY, "{00000000-0000-0000-0000-000000000000}"), "auto", alias),
        (machine([stable, preview, alias], PREVIEW_FAMILY, STABLE_CLSID), "auto", stable),
        (machine([stable, preview, alias], STABLE_FAMILY, PREVIEW_CLSID.lower()), "auto", preview),
        (machine([stable, alias], STABLE_FAMILY, PREVIEW_CLSID), "auto", alias),
        (machine([stable, preview, alias], PREVIEW_FAMILY), "wt", stable),
        (machine([stable, preview, alias], STABLE_FAMILY), "wt-preview", preview),
        (machine([stable, preview]), "auto", stable),
        (machine([alias]), "auto", alias),
        (machine([alias]), "wt", alias),
        (machine([alias]), "wt-preview", None),
        (machine([portable]), "auto", portable),
        (machine([portable], local_dir=None), "wt", portable),
        (machine([portable]), "wt-preview", None),
        (machine([r"relative\bin\wt.exe"]), "auto", None),
        (machine([stable], local_dir=r"relative\Local"), "auto", None),
        (machine([]), "auto", None),
    ]
    failed = 0
    for i, (found, choice, want) in enumerate(cases):
        got = for_choice(found, choice)
        if (got[0] if got else None) != want:
            failed += 1
            print(f"case {i} ({choice}): want {want}, got {got}")
    blob = "\0".join([PREVIEW_FAMILY, PREVIEW_FAMILY + "!App", r"C:\Program Files\WindowsApps\x\wt.exe", "0", ""]).encode("utf-16-le")
    data = struct.pack("<IHHI", IO_REPARSE_TAG_APPEXECLINK, 4 + len(blob), 0, 3) + blob
    for want, d in ((PREVIEW_FAMILY, data), (None, data[:11]), (None, b"\x0c" + data[1:])):
        if parse_alias_package(d) != want:
            failed += 1
            print(f"reparse data: want {want}, got {parse_alias_package(d)}")
    print(f"{len(cases) + 3 - failed} passed, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        sys.exit(_self_test())
    found = scan()
    for k in ("stable", "preview", "alias", "path"):
        print(f"{k}: {found[k][0] + ' (' + found[k][2] + ')' if found[k] else '-'}")
    print(f"default terminal: {found['default'] or 'not a Windows Terminal'}")
    p = preferred(found)
    print(f"automatic: {p[2] + ' ' + p[0] if p else 'a new console'}")
