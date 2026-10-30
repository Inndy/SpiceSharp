using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks.Sources;
using Microsoft.Win32.SafeHandles;
using PveTerm;
using Spice.Tests;

namespace Pve.Term.Tests;

// Synthetic loopback endpoints, in-memory operations and hidden pseudoconsoles only. No PVE credential file is read.
public partial class PveTermReviewTests
{
    // N6: both characters are accepted by --escape, so doubling must quote them too.
    [Theory]
    [InlineData('.')]
    [InlineData('?')]
    public void DoublingACustomEscapeSendsItInsteadOfExecutingACommand(char escape)
    {
        var filter = new EscapeFilter(escape);
        Span<char> sent = stackalloc char[2];
        Assert.Equal(EscapeCommand.None, filter.Feed(escape, sent, out int count));
        Assert.Equal(0, count);
        Assert.Equal(EscapeCommand.None, filter.Feed(escape, sent, out count));
        Assert.Equal(1, count);
        Assert.Equal(escape, sent[0]);
    }

    [Theory]
    [InlineData("\r\n~.", "\r\n")]
    [InlineData("~\n~.", "~\n")]
    [InlineData("~\e[200~\r~.\n~?\e[201~\r~.", "~\e[200~\r~.\n~?\e[201~\r")]
    [InlineData("~\e[A\e[3~\r~.", "~\e[A\e[3~\r")]
    [InlineData("~\ud83d\ude00\e[A\r~.", "~\ud83d\ude00\e[A\r")]
    public void EscapeEdgesKeepVtAndPasteBytesInOrder(string typed, string expected)
    {
        var filter = new EscapeFilter('~');
        var sent = new StringBuilder();
        Span<char> buffer = stackalloc char[2];
        var command = EscapeCommand.None;
        foreach (char c in typed)
        {
            command = filter.Feed(c, buffer, out int count);
            sent.Append(buffer[..count]);
            if (command == EscapeCommand.Quit) break;
        }
        Assert.Equal(expected, sent.ToString());
        Assert.Equal(EscapeCommand.Quit, command);
    }

    [Fact]
    public async Task EscapePreservesSupplementaryUnicodeAndVtKeys()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        using var handoff = HandoffServer.Create();
        var serve = handoff.ServeAsync(fake.Descriptor().ToJson(), TimeSpan.FromSeconds(10));
        string exe = Path.Combine(AppContext.BaseDirectory, "PveTerm.exe");
        using var pc = PseudoConsole.Start($"\"{exe}\" console --pipe {handoff.PipeName} --nonce {handoff.Nonce} --keepalive 0", 80, 25);
        await serve;
        pc.WaitFor("fake-login: ");
        int mark = 0;
        pc.Type("~\U0001f600\e[A\r~."); // held tilde, a UTF-16 surrogate pair, Up, Enter, quit
        fake.ExpectInput(Encoding.UTF8.GetBytes("~\U0001f600\e[A\r"), ref mark);
        pc.WaitFor("Connection closed: closed from this terminal");
        Assert.Equal(0, pc.WaitForExit());
        Assert.Equal("~\U0001f600\e[A\r", Encoding.UTF8.GetString(fake.InputBytes()));
    }

    // The managed N3 cases below pass now; the helper must enforce the same lifetime while its child is alive.
    [Fact]
    public async Task HelperDeadlineCoversAConnectedClientWhileTheLaunchedCommandIsStillRunning()
    {
        const string script = """
            import ast, json, os, sys, subprocess, urllib.parse
            tree = ast.parse(open(sys.argv[1], encoding='utf-8').read())
            node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'term_main')
            root = os.path.dirname(os.path.dirname(sys.argv[1]))
            cfg = {'PVE_URL':'https://localhost:8006', 'TOKEN_ID':'test@pve!bridge', 'TOKEN_SECRET':'0123-SECRET'}
            class RunningCommand:
                def __init__(self, args, **kw):
                    print('LAUNCH ' + json.dumps({'pipe':args[3]}), flush=True)
                def wait(self):
                    sys.stdin.readline() # mock a command that remains alive; never launch a terminal
                    return 0
            subprocess.Popen = RunningCommand
            exec(compile(ast.Module(body=[node], type_ignores=[]), '<pve-vv term_main>', 'exec'))
            sys.exit(term_main(['node:testnode', '--here']))
            """;
        string helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/pve-vv.py"));
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
            string? line;
            do { line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            while (line is not null && !line.StartsWith("LAUNCH ", StringComparison.Ordinal));
            if (line is null) Assert.Fail(await process.StandardError.ReadToEndAsync());
            using var launch = JsonDocument.Parse(line![7..]);
            using var client = new NamedPipeClientStream(".", launch.RootElement.GetProperty("pipe").GetString()!,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);
            // Connect but never send the nonce. A 30-second whole-exchange budget must close this peer even
            // when child.wait() has not returned. Cancel the test read to leave no pending operation on failure.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(33));
            int read = -1;
            var error = await Record.ExceptionAsync(async () =>
            {
                try { read = await client.ReadAsync(new byte[1], deadline.Token); }
                catch (IOException) { read = 0; } // a broken pipe also demonstrates server closure
            });
            Assert.Null(error);
            Assert.Equal(0, read);
            Assert.False(process.HasExited); // the mock launched command is still alive
        }
        finally
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("exit");
                await process.StandardInput.FlushAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            }
        }
    }

    // Runs only in an explicitly created hidden pseudoconsole; never attaches to the test runner's console.
    internal static int InspectConsoleNul()
    {
        using var input = CreateFileW("CONIN$", 0xc0000000, 3, 0, 3, 0, 0);
        using var output = CreateFileW("CONOUT$", 0xc0000000, 3, 0, 3, 0, 0);
        GetConsoleMode(input, out uint saved);
        SetConsoleMode(input, (saved & ~7u) | 0x208u);
        try
        {
            byte[] ready = "REVIEW-READY\r\n"u8.ToArray();
            WriteFile(output, ready, (uint)ready.Length, out _, 0);
            var records = new KeyRecord[16];
            while (ReadConsoleInputW(input, records, (uint)records.Length, out uint count))
                for (int i = 0; i < count; i++)
                    if (records[i].EventType == 1 && records[i].Down != 0)
                    {
                        if (records[i].Char == 0)
                        {
                            byte[] found = "REVIEW-NUL-KEY\r\n"u8.ToArray();
                            WriteFile(output, found, (uint)found.Length, out _, 0);
                        }
                        if (records[i].Char == 'x')
                        {
                            byte[] done = "REVIEW-END\r\n"u8.ToArray();
                            WriteFile(output, done, (uint)done.Length, out _, 0);
                            return 0;
                        }
                    }
            return 1;
        }
        finally { SetConsoleMode(input, saved); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    internal struct KeyRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int Down;
        [FieldOffset(14)] public ushort Char;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(SafeFileHandle input, out uint mode);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(SafeFileHandle input, uint mode);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadConsoleInputW(SafeFileHandle input, [Out, MarshalUsing(CountElementName = nameof(length))] KeyRecord[] records, uint length, out uint count);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(SafeFileHandle output, byte[] bytes, uint length, out uint written, nint overlapped);

    [Fact]
    public void NulControlProvesTheConsoleDeliversAZeroCharacterKeyEvent()
    {
        string probe = Path.Combine(AppContext.BaseDirectory, "Pve.Term.Tests.exe");
        using var pc = PseudoConsole.Start($"\"{probe}\" --review-console-nul", 80, 25);
        pc.WaitFor("REVIEW-READY");
        pc.Type("\e[32;57;0;1;8;1_x"); // Win32-input-mode Ctrl+Space, then a marker; no host input injection.
        pc.WaitFor("REVIEW-END");
        Assert.Equal(0, pc.WaitForExit());
        Assert.Contains("REVIEW-NUL-KEY", pc.Output);
    }

    [Fact]
    public async Task ApiStageDeadlineIsReportedAsAConnectFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var descriptor = new TermDescriptor
        {
            ApiUrl = new Uri($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}"),
            Node = "testnode", Target = TermTarget.Node, ApiToken = FakeTermproxy.Token,
        };
        var connect = TermproxyConnector.ConnectAsync(descriptor, TimeSpan.Zero, CancellationToken.None);
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // Accept TCP but stall TLS: the internal 20-second deadline must cover the API stage too.
        var error = await Record.ExceptionAsync(() => connect.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.IsType<TermConnectException>(error);
        Assert.Contains("timed out", error.Message);
    }

    [Fact]
    public async Task HandoffServerDeadlineCoversAClientThatStopsReadingAfterItsNonce()
    {
        using var server = HandoffServer.Create();
        using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var serve = server.ServeAsync("{}"u8.ToArray(), TimeSpan.FromMilliseconds(150));
        try
        {
            await client.ConnectAsync(3000);
            await client.WriteAsync(Encoding.ASCII.GetBytes(server.Nonce + "\n"));
            // The tiny descriptor fits the pipe buffer. WaitForPipeDrain must not defeat the deadline.
            var error = await Record.ExceptionAsync(() => serve.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsType<HandoffException>(error);
            Assert.Contains("in time", error.Message);
        }
        finally
        {
            client.Dispose();
            try { await serve.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task HandoffClientDeadlineCoversAConnectedServerThatNeverWritesTheDescriptor()
    {
        string name = "pveterm-review-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var receive = Task.Run(() => HandoffClient.Receive(name, new string('a', 32), TimeSpan.FromMilliseconds(150)));
        try
        {
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(3));
            byte[] nonce = new byte[33];
            await server.ReadExactlyAsync(nonce).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            var error = await Record.ExceptionAsync(() => receive.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsType<HandoffException>(error);
        }
        finally
        {
            server.Dispose();
            try { await receive.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task ConsoleNulInputReachesTheGuestBeforeTheFollowingKey()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        using var handoff = HandoffServer.Create();
        var serve = handoff.ServeAsync(fake.Descriptor().ToJson(), TimeSpan.FromSeconds(10));
        string exe = Path.Combine(AppContext.BaseDirectory, "PveTerm.exe");
        using var pc = PseudoConsole.Start($"\"{exe}\" console --pipe {handoff.PipeName} --nonce {handoff.Nonce} --keepalive 0", 80, 25);
        await serve;
        pc.WaitFor("fake-login: ");
        pc.Type("\e[32;57;0;1;8;1_x"); // Windows Terminal's Win32-input-mode Ctrl+Space plus a marker.
        int mark = 0;
        fake.ExpectInput("x"u8.ToArray(), ref mark, timeoutMs: 3000);
        byte[] got = fake.InputBytes();
        pc.Type("exit\r");
        pc.WaitFor("Connection closed:");
        Assert.Equal(0, pc.WaitForExit());
        Assert.Equal(new byte[] { 0, (byte)'x' }, got);
    }

    // Reusable pending I/O, completed by a sleeping worker after the consumer actually registers its continuation.
    // This measures Bridge.Wait rather than allocations by a test TaskCompletionSource or socket fixture.
    sealed class PendingSend : IValueTaskSource, IDisposable
    {
        ManualResetValueTaskSourceCore<bool> _core;
        readonly AutoResetEvent _ready = new(false);
        readonly Thread _worker;
        volatile bool _stop;

        public PendingSend(int completionDelayMs = 0)
        {
            _worker = new Thread(() =>
            {
                while (true)
                {
                    _ready.WaitOne();
                    if (_stop) return;
                    if (completionDelayMs > 0) Thread.Sleep(completionDelayMs);
                    _core.SetResult(true);
                }
            }) { IsBackground = true };
            _worker.Start();
        }

        public ValueTask Next() { _core.Reset(); return new ValueTask(this, _core.Version); }
        public void GetResult(short token) => _core.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _core.OnCompleted(continuation, state, token, flags);
            _ready.Set();
        }
        public void Dispose() { _stop = true; _ready.Set(); _worker.Join(); _ready.Dispose(); }
    }

    [Fact]
    public void WaitingForPendingInputSendsDoesNotAllocatePerSend()
    {
        using var send = new PendingSend();
        void Work() { for (int i = 0; i < 32; i++) Bridge.Wait(send.Next()); }
        Work();
        AllocationWindow.Measure("pending terminal input sends", Work, prepare: Work);
    }

    [Fact]
    public void WaitingForSlowPendingInputSendsDoesNotAllocatePerSend()
    {
        // Deliberately pending I/O, not CPU load: exercise the blocking wait after any spin budget, which can
        // expose a late lazy lock allocation in a ManualResetEventSlim waiter.
        using var send = new PendingSend(completionDelayMs: 15);
        void Work() { for (int i = 0; i < 32; i++) Bridge.Wait(send.Next()); }
        Work();
        AllocationWindow.Measure("slow pending terminal input sends", Work, prepare: Work);
    }

    [Fact]
    public async Task TerminalHelpersAuthenticatedApiCallRejectsAnUntrustedServer()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        // Extract just ctx and call from the real helper. Never run its credential-file/configuration startup.
        const string script = """
            import ast, json, ssl, sys, urllib.request, urllib.parse
            tree = ast.parse(open(sys.argv[1], encoding='utf-8').read())
            nodes = [n for n in tree.body if (isinstance(n, ast.FunctionDef) and n.name == 'call') or
                     (isinstance(n, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'ctx' for t in n.targets))]
            base = 'https://localhost:' + sys.argv[2] + '/api2/json'
            auth = {'Authorization': 'PVEAPIToken=test@pve!bridge=0123-SECRET'}
            exec(compile(ast.Module(body=nodes, type_ignores=[]), '<pve-vv call>', 'exec'))
            try:
                call('POST', '/nodes/testnode/qemu/100/termproxy', {})
            except urllib.error.URLError:
                sys.exit(0)
            sys.exit(1)
            """;
        string helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/pve-vv.py"));
        var start = new ProcessStartInfo("python")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(helper);
        start.ArgumentList.Add(fake.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(fake.Requests); // TLS must fail before synthetic Authorization reaches HTTP.
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task HelperHereUsesNoNewConsoleAndAnExitedWrapperDoesNotAbandonTheHandoff()
    {
        // Execute only term_main, with fake config and a mocked launcher that exits before its client connects.
        // The real Python pipe ACL/nonce/write path still runs. No terminal command is actually started.
        const string script = """
            import ast, json, os, sys, subprocess, urllib.parse
            tree = ast.parse(open(sys.argv[1], encoding='utf-8').read())
            node = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'term_main')
            root = os.path.dirname(os.path.dirname(sys.argv[1]))
            cfg = {'PVE_URL':'https://localhost:8006', 'TOKEN_ID':'test@pve!bridge', 'TOKEN_SECRET':'0123-SECRET'}
            class Wrapper:
                def __init__(self, args, **kw):
                    print('LAUNCH ' + json.dumps({'pipe':args[3], 'nonce':args[5], 'flags':kw['creationflags']}), flush=True)
                def wait(self): return 0
            subprocess.Popen = Wrapper
            exec(compile(ast.Module(body=[node], type_ignores=[]), '<pve-vv term_main>', 'exec'))
            sys.exit(term_main(['node:testnode', '--here']))
            """;
        string helper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/pve-vv.py"));
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
            string? line;
            do { line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            while (line is not null && !line.StartsWith("LAUNCH ", StringComparison.Ordinal));
            if (line is null) Assert.Fail(await process.StandardError.ReadToEndAsync());
            Assert.NotNull(line);
            using var launch = JsonDocument.Parse(line[7..]);
            Assert.Equal(0, launch.RootElement.GetProperty("flags").GetInt32());
            // Give the wrapper-exit path time to run before its hypothetical terminal process connects.
            await Task.Delay(150);
            Assert.False(process.HasExited);
            byte[] bytes = await Task.Run(() => HandoffClient.Receive(
                launch.RootElement.GetProperty("pipe").GetString()!, launch.RootElement.GetProperty("nonce").GetString()!, TimeSpan.FromSeconds(3)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("testnode", TermDescriptor.Parse(bytes).Node);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("hand-off served", await process.StandardOutput.ReadToEndAsync());
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }
}
