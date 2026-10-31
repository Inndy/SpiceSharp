"""Inspect the generated PE files using LLVM tools on PATH. No third-party modules."""
import pathlib
import re
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parent
out = root / 'out' / ('win-' + (sys.argv[1] if len(sys.argv) > 1 else 'x64'))
header = (root / 'spicecodec/include/spice_codec.h').read_text()


def def_exports(path):
    """Names listed under EXPORTS in a module-definition file."""
    names, active = set(), False
    for line in path.read_text().splitlines():
        line = line.split(';', 1)[0].strip()
        if line == 'EXPORTS':
            active = True
        elif line and active:
            names.add(line.split()[0])
    return names


# Each DLL must export exactly its list: spicecodec the SC_API functions, the others their .def file.
expected = {
    'spicecodec': set(re.findall(r'SC_CALL (sc_\w+)\(', header)),
    'turbojpeg': def_exports(root / 'turbojpeg.def'),
    'opus': def_exports(root / 'opus.def'),
}
for name in ('spicecodec', 'turbojpeg', 'opus'):
    dll = out / (name + '.dll')
    machine = {'win-x64': 'IMAGE_FILE_MACHINE_AMD64', 'win-arm64': 'IMAGE_FILE_MACHINE_ARM64'}[out.name]
    headers = subprocess.check_output(['llvm-readobj', '--file-headers', str(dll)], text=True)
    assert f'Machine: {machine} ' in headers, (dll, 'wrong architecture')
    pe = subprocess.check_output(['llvm-objdump', '-p', str(dll)], text=True)
    imports = re.findall(r'DLL Name: (\S+)', pe)
    # libavutil's random seed (BCryptGenRandom) stays linked into spicecodec.dll; bcrypt.dll is part of Windows.
    system = {'kernel32.dll', 'bcrypt.dll'} if name == 'spicecodec' else {'kernel32.dll'}
    assert imports and all(x.lower() in system or x.lower().startswith('api-ms-win-crt-') for x in imports), imports
    for flag in ('DYNAMIC_BASE', 'HIGH_ENTROPY_VA', 'NX_COMPAT', 'GUARD_CF'):
        assert flag in pe, (dll, flag)
    cfg = subprocess.check_output(['llvm-readobj', '--coff-load-config', str(dll)], text=True)
    assert 'CF_INSTRUMENTED' in cfg and 'CF_FUNCTION_TABLE_PRESENT' in cfg
    exports_text = subprocess.check_output(['llvm-readobj', '--coff-exports', str(dll)], text=True)
    exports = set(re.findall(r'^  Name: (\S+)', exports_text, re.M))
    want = expected[name]
    assert want and exports == want, (dll, 'unexpected', exports - want, 'missing', want - exports)
    (out / (name + '.exports.txt')).write_text('\n'.join(sorted(exports)) + '\n')
    print(f'{dll.name}: {len(exports)} exports; system/UCRT imports and PE protections verified')
