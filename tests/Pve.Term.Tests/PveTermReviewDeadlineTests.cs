using System.Diagnostics;
using System.Text.Json;

namespace Pve.Term.Tests;

public partial class PveTermReviewTests
{
    // N7: force the valid scheduling order where the one-shot deadline runs before the serving thread starts.
    // Only term_main is extracted; real local pipe APIs run with synthetic config and no launched command.
    [Fact]
    public async Task HelperDeadlineCannotBeMissedBeforeConnectStarts()
    {
        const string script = """
            import ast, ctypes, json, os, sys, subprocess, threading, urllib.parse
            from ctypes import wintypes as wt
            with open(sys.argv[1], encoding='utf-8') as f:
                tree = ast.parse(f.read())
            node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'term_main')
            root = os.path.dirname(os.path.dirname(sys.argv[1]))
            cfg = {'PVE_URL':'https://localhost:8006', 'TOKEN_ID':'test@pve!bridge', 'TOKEN_SECRET':'synthetic'}
            expired, done = threading.Event(), threading.Event()
            real_timer, real_start = threading.Timer, threading.Thread.start
            def timer(interval, callback):
                def fire():
                    try: callback()
                    finally: expired.set()
                # Shorten only the test's wait; the retained connected-client case exercises the real 30 s.
                return real_timer(0.05, fire)
            def start(thread):
                if getattr(thread._target, '__name__', '') == 'serve':
                    if not expired.wait(3): raise RuntimeError('timer did not fire')
                    target = thread._target
                    def run():
                        try: target()
                        finally: done.set()
                    thread._target = run
                return real_start(thread)
            threading.Timer, threading.Thread.start = timer, start
            class RunningCommand:
                def __init__(self, args, **kw): self.name = args[3]
                def wait(self):
                    ended = done.wait(0.3)
                    # Connect and close only for cleanup, so a missed connect/nonce read cannot leak a worker.
                    k32 = ctypes.WinDLL('kernel32', use_last_error=True)
                    k32.CreateFileW.argtypes = [wt.LPCWSTR,wt.DWORD,wt.DWORD,ctypes.c_void_p,wt.DWORD,wt.DWORD,wt.HANDLE]
                    k32.CreateFileW.restype = wt.HANDLE
                    k32.CloseHandle.argtypes = [wt.HANDLE]
                    handle = k32.CreateFileW('\\\\.\\pipe\\' + self.name, 0xc0000000, 0, None, 3, 0, None)
                    connected = handle not in (None, wt.HANDLE(-1).value)
                    if connected: k32.CloseHandle(handle)
                    clean = done.wait(3)
                    print('RESULT ' + json.dumps({'expired':expired.is_set(), 'ended':ended,
                          'connected':connected, 'clean':clean}), flush=True)
                    return 0
            subprocess.Popen = RunningCommand
            exec(compile(ast.Module(body=[node], type_ignores=[]), '<pve-vv deadline ordering>', 'exec'))
            sys.exit(term_main(['node:testnode', '--here']))
            """;
        string helper = Path.Combine(RepositoryPaths.Root, "tools", "pve-vv.py");
        var start = new ProcessStartInfo("python")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(helper);
        using var process = Process.Start(start)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.ExitCode == 0, await stderr);
            string line = Assert.Single((await stdout).Split('\n'), l => l.StartsWith("RESULT ", StringComparison.Ordinal));
            using var result = JsonDocument.Parse(line[7..]);
            Assert.True(result.RootElement.GetProperty("expired").GetBoolean());
            Assert.True(result.RootElement.GetProperty("clean").GetBoolean());
            Assert.True(result.RootElement.GetProperty("ended").GetBoolean(),
                "The deadline already fired, but the helper entered a blocking connect and remained alive.");
            Assert.False(result.RootElement.GetProperty("connected").GetBoolean(),
                "A new client could connect after the whole-exchange deadline expired.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }
}
