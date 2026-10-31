"""Offline decision 0042 regressions: python -I -B tests/Pve.Api.Tests/windows_terminal_self_test.py.

No terminal, API, registry or credential access. Import wt_locate without its entrypoint; extract only the
option-selection and command/wait portions of term_main, never the helper's credential-reading module body.
"""
import ast
import importlib.util
import os
from pathlib import Path
import re
import struct
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
LOCATOR = ROOT / "tools" / "wt_locate.py"
HELPER = ROOT / "tools" / "pve-vv.py"
spec = importlib.util.spec_from_file_location("review_wt_locate", LOCATOR)
locate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(locate)


def function_part(start, stop, namespace, returning=None, initial=None):
    tree = ast.parse(HELPER.read_text(encoding="utf-8"))
    fn = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "term_main")
    body = fn.body
    fn.body = body[start(body):stop(body)]
    if initial:
        fn.body = ast.parse(initial).body + fn.body
    if returning:
        fn.body += ast.parse("return " + returning).body
    module = ast.fix_missing_locations(ast.Module(body=[fn], type_ignores=[]))
    exec(compile(module, str(HELPER), "exec"), namespace)
    return namespace["term_main"]


def assigns(node, name):
    return isinstance(node, ast.Assign) and any(
        isinstance(t, ast.Name) and t.id == name for t in node.targets)


def options(argv, found=None):
    # Stop before target parsing, config access, API calls and every Win32 call.
    fn = function_part(lambda b: 1,
                       lambda b: next(i for i, n in enumerate(b)
                                      if isinstance(n, ast.Assign) and isinstance(n.targets[0], ast.Tuple)),
                       {"os": os, "__file__": str(HELPER)}, "(host, opts, command)")
    fake_locator = SimpleNamespace(scan=lambda: {}, preferred=lambda _: found)
    fake_spec = SimpleNamespace(loader=SimpleNamespace(exec_module=lambda _: None))
    with mock.patch.object(importlib.util, "spec_from_file_location", return_value=fake_spec), \
            mock.patch.object(importlib.util, "module_from_spec", return_value=fake_locator):
        return fn(argv)


def build(host, opts=None, command=None, exe=r"C:\apps\PveTerm.exe"):
    events = []
    captured = {}

    class Child:
        def wait(self):
            events.append("wrapper exited")
            return 0

    def popen(args, **kwargs):
        captured.update(args=args, **kwargs)
        return Child()

    def join(**kwargs):
        events.append(("join", kwargs))

    namespace = dict(os=os, root=str(ROOT), opts=opts or {
        "exe": exe, "hidden": False, "here": False, "prefetch": False},
        __review_command=command, command_given=command is not None, host=host,
        kind="node", spec="testnode", d={"node": 'test;"node'},
        name="pveterm-synthetic", nonce="a" * 32,
        subprocess=SimpleNamespace(Popen=popen),
        sys=SimpleNamespace(stdout=None, stderr=None),
        server=SimpleNamespace(join=join), served={"why": "served"}, print=lambda *a, **kw: None)
    fn = function_part(lambda b: next(i for i, n in enumerate(b) if assigns(n, "exe")),
                       len, namespace, initial="command = __review_command")
    code = fn([])
    return captured, events, code


class WindowsTerminalReviewTests(unittest.TestCase):
    # R1: Windows recognizes each spelling as the same alias.
    def test_alternate_alias_path_is_not_a_portable_stable_fallback(self):
        local = r"C:\Users\u\AppData\Local"
        apps = os.path.join(local, "Microsoft", "WindowsApps")
        alias = os.path.join(apps, "wt.exe")
        preview = os.path.join(apps, locate.PREVIEW_FAMILY, "wt.exe")
        equivalent = os.path.join(apps, ".")
        found = locate.scan(
            exists=lambda p: os.path.normcase(os.path.abspath(p)) in
                {os.path.normcase(alias), os.path.normcase(preview)},
            getenv={"LOCALAPPDATA": local, "PATH": equivalent}.get,
            default=lambda: None, alias_pkg=lambda _: locate.PREVIEW_FAMILY)
        self.assertIsNone(locate.for_choice(found, "wt"))

    # R2: real temporary filesystem entries, but neither executable is ever started.
    def test_a_directory_named_wt_exe_does_not_shadow_the_next_path_file(self):
        with tempfile.TemporaryDirectory(prefix="wt-review-") as temp:
            first, second = Path(temp) / "first", Path(temp) / "second"
            (first / "wt.exe").mkdir(parents=True)
            second.mkdir()
            executable = second / "wt.exe"
            executable.touch()
            found = locate.scan(getenv={"PATH": f"{first};{second}"}.get,
                                default=lambda: None, alias_pkg=lambda _: None)
            self.assertEqual(str(executable), locate.preferred(found)[0])

    def test_explicit_wt_must_not_accept_a_directory(self):
        with tempfile.TemporaryDirectory(prefix="wt-review-") as temp:
            with self.assertRaises(SystemExit):
                options(["node:testnode", "--wt", temp])

    @staticmethod
    def alias_data(terminated=True):
        blob = (locate.PREVIEW_FAMILY + ("\0" if terminated else "")).encode("utf-16-le")
        return struct.pack("<IHHI", locate.IO_REPARSE_TAG_APPEXECLINK, 4 + len(blob), 0, 3) + blob

    # R4: both parsers should reject malformed lengths; Python also needs the family terminator.
    def test_reparse_payload_must_fit_inside_the_returned_buffer(self):
        data = bytearray(self.alias_data())
        struct.pack_into("<H", data, 4, 65535)
        self.assertIsNone(locate.parse_alias_package(data))

    def test_reparse_utf16_payload_cannot_have_an_odd_byte_length(self):
        data = bytearray(self.alias_data() + b"A")
        struct.pack_into("<H", data, 4, len(data) - 8)
        self.assertIsNone(locate.parse_alias_package(data))

    def test_reparse_family_must_be_terminated(self):
        self.assertIsNone(locate.parse_alias_package(self.alias_data(terminated=False)))

    # R3: argv quoting is distinct from WT's own unescaped semicolon separators.
    def test_a_bridge_path_with_a_semicolon_is_one_terminal_command(self):
        captured, _, _ = build((r"D:\portable\wt.exe", "portable"), exe=r"C:\apps\semi;colon\PveTerm.exe")
        self.assertFalse(any(re.search(r"(?<!\\);", arg) for arg in captured["args"]), captured["args"])

    def test_alias_resolution_order_self_test(self):
        self.assertEqual(0, locate._self_test())

    def test_default_uses_the_found_full_path_without_a_shell(self):
        found = (r"D:\portable\wt.exe", None, "portable")
        host, opts, command = options(["node:testnode"], found)
        self.assertEqual((found[0], found[2]), host)
        captured, _, _ = build(host, opts=opts, command=command)
        self.assertEqual(found[0], captured["args"][0])
        self.assertFalse(captured.get("shell", False))
        self.assertEqual(0, captured["creationflags"])
        title = captured["args"][captured["args"].index("--title") + 1]
        self.assertNotIn(";", title)
        self.assertNotIn('"', title)

    def test_explicit_wt_and_console_options(self):
        with tempfile.TemporaryDirectory(prefix="wt-review-") as temp:
            exe = Path(temp) / "wt.exe"
            exe.touch()
            host, _, _ = options(["node:testnode", "--wt", str(exe)])
            self.assertEqual(str(exe), host[0])
        host, opts, _ = options(["node:testnode", "--console"], ("unused.exe", None, "unused"))
        self.assertIsNone(host)
        self.assertTrue(opts["console"])
        for conflicting in (["--wt", "unused.exe", "--console"], ["--console", "--here"],
                            ["--wt", "unused.exe", "--", "command"]):
            with self.subTest(conflicting=conflicting), self.assertRaises(SystemExit):
                options(["node:testnode"] + conflicting)

    def test_here_hidden_and_custom_command_skip_detection(self):
        for args in (["--here"], ["--hidden"], ["--", "custom", "{pipe}"]):
            with self.subTest(args=args):
                host, _, _ = options(["node:testnode"] + args, ("unused.exe", None, "unused"))
                self.assertIsNone(host)

    def test_wrapper_exit_is_followed_by_the_handoff_join(self):
        captured, events, code = build((r"D:\portable\wt.exe", "portable"))
        self.assertEqual(["wrapper exited", ("join", {"timeout": 30})], events)
        self.assertEqual(0, code)
        self.assertEqual(["console", "--pipe", "pveterm-synthetic", "--nonce", "a" * 32], captured["args"][-5:])

    def test_every_truncated_reparse_prefix_can_be_parsed(self):
        data = self.alias_data()
        for length in range(len(data)):
            locate.parse_alias_package(data[:length])


if __name__ == "__main__":
    unittest.main(verbosity=2)
