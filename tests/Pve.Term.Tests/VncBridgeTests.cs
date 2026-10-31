using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace Pve.Term.Tests;

/// <summary>The VNC bridge end to end against loopback fakes: a fake pveproxy/QEMU upstream and a fake viewer
/// (docs/decisions/0043).</summary>
public class VncBridgeTests
{
    static readonly TestPki Pki = TestPki.Shared;

    sealed class Run
    {
        public required Task<(VncBridgeOutcome Outcome, string Reason)> Task { get; init; }
        public required TaskCompletionSource<int> Port { get; init; }
        public List<string> Status { get; } = [];
    }

    static Run Start(FakeVncproxy fake, TimeSpan? viewerTimeout = null, TimeSpan? keepAlive = null, bool forceShared = true,
        Func<int, int, string?>? checkPeer = null, Func<CancellationToken, Task<VncUpstream>>? connect = null)
    {
        var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = new List<string>();
        var options = new VncBridgeOptions
        {
            Connect = connect ?? (ct => VncproxyConnector.ConnectAsync(fake.Descriptor(), ct)),
            Status = s => { lock (status) status.Add(s); },
            ViewerTimeout = viewerTimeout ?? TimeSpan.FromSeconds(20),
            KeepAlive = keepAlive ?? TimeSpan.Zero,
            ForceShared = forceShared,
            CheckPeer = checkPeer,
            Listening = p => port.SetResult(p),
        };
        var run = new Run { Task = System.Threading.Tasks.Task.Run(() => VncBridge.RunAsync(options)), Port = port };
        run.Status.AddRange(status);
        return run;
    }

    [Fact]
    public async Task ViewerGetsQemusServerInitAndBytesFlowBothWays()
    {
        await using var fake = new FakeVncproxy(Pki);
        var run = Start(fake);
        using var viewer = await FakeViewer.ConnectAsync(await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var init = await viewer.HandshakeAsync(minor: 8, shared: false).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(FakeVncproxy.ServerInit(FakeVncproxy.Width, FakeVncproxy.Height, FakeVncproxy.Name), init);
        Assert.StartsWith("post /api2/json/nodes/testnode/qemu/100/vncproxy body=websocket=1", fake.Expect("post"));
        Assert.Equal("ws /api2/json/nodes/testnode/qemu/100/vncwebsocket protocol=binary", fake.Expect("ws "));
        Assert.Equal("version RFB 003.008", fake.Expect("version"));
        fake.Expect("auth-ok");
        // The viewer asked for exclusive access; QEMU got a shared ClientInit (allow-exclusive would drop other clients).
        Assert.Equal("client-init 1", fake.Expect("client-init"));

        // Viewer to server: a key event and a pointer event in one write, then an update request.
        byte[] key = [4, 1, 0, 0, 0, 0, 0, 0x61], pointer = [5, 0, 0, 10, 0, 20];
        byte[] fbur = [3, 1, 0, 0, 0, 0, 4, 0, 3, 0];
        await viewer.Stream.WriteAsync((byte[])[.. key, .. pointer, .. fbur]);
        Assert.Equal("fbur 1", fake.Expect("fbur"));
        var update = new byte[FakeVncproxy.Update.Length];
        await viewer.Stream.ReadExactlyAsync(update).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(FakeVncproxy.Update, update);
        Assert.Equal((byte[])[.. key, .. pointer, .. fbur], fake.ReceivedBytes());

        viewer.Tcp.Client.Shutdown(SocketShutdown.Both);
        var (outcome, reason) = await run.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(VncBridgeOutcome.Ended, outcome);
        Assert.Equal("the VNC viewer closed the connection", reason);
        Assert.False(fake.FragmentSeen);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task OlderViewerVersionsAreAnsweredInTheirOwn(int minor)
    {
        await using var fake = new FakeVncproxy(Pki);
        var run = Start(fake, forceShared: false);
        using var viewer = await FakeViewer.ConnectAsync(await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var init = await viewer.HandshakeAsync(minor, shared: true).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(FakeVncproxy.Name, Encoding.UTF8.GetString(init.AsSpan(24)));
        Assert.Equal("client-init 1", fake.Expect("client-init"));
        viewer.Dispose();
        Assert.Equal(VncBridgeOutcome.Ended, (await run.Task.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
    }

    [Fact]
    public async Task WithoutForceSharedTheViewersFlagIsPassedOn()
    {
        await using var fake = new FakeVncproxy(Pki);
        var run = Start(fake, forceShared: false);
        using var viewer = await FakeViewer.ConnectAsync(await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await viewer.HandshakeAsync(8, shared: false).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("client-init 0", fake.Expect("client-init"));
    }

    [Fact]
    public async Task OldProxmoxWithoutPasswordUsesTheTicket()
    {
        await using var fake = new FakeVncproxy(Pki) { OmitPassword = true };
        var run = Start(fake);
        using var viewer = await FakeViewer.ConnectAsync(await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await viewer.HandshakeAsync().WaitAsync(TimeSpan.FromSeconds(15));
        fake.Expect("auth-ok");
    }

    [Fact]
    public async Task ARefusedPasswordReachesTheViewerAsTheReason()
    {
        await using var fake = new FakeVncproxy(Pki) { ServerPassword = "replaced" };
        var run = Start(fake);
        using var viewer = await FakeViewer.ConnectAsync(await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var e = await Assert.ThrowsAsync<IOException>(() => viewer.HandshakeAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("Authentication failed", e.Message);
        Assert.DoesNotContain("\0", e.Message);
        var (outcome, reason) = await run.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(VncBridgeOutcome.ConnectFailed, outcome);
        Assert.Contains("refused the console password (Authentication failed)", reason);
        Assert.DoesNotContain(FakeVncproxy.Password, reason);
    }

    [Fact]
    public async Task AnApiRefusalReachesAThreeThreeViewerToo()
    {
        await using var fake = new FakeVncproxy(Pki);
        var run = Start(fake, connect: ct => VncproxyConnector.ConnectAsync(fake.Descriptor("wrong@pve!x=y"), ct));
        using var viewer = await FakeViewer.ConnectAsync(await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var e = await Assert.ThrowsAsync<IOException>(() => viewer.HandshakeAsync(minor: 3).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("403 Permission check failed", e.Message);
        Assert.Equal(VncBridgeOutcome.ConnectFailed, (await run.Task.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
    }

    [Fact]
    public async Task OnlyOneViewerIsServed()
    {
        await using var fake = new FakeVncproxy(Pki);
        var run = Start(fake);
        int port = await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var first = await FakeViewer.ConnectAsync(port);
        await first.HandshakeAsync().WaitAsync(TimeSpan.FromSeconds(15));
        // The listener closed when the first viewer was taken.
        using var second = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => second.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ARefusedPeerDoesNotTakeTheSession()
    {
        await using var fake = new FakeVncproxy(Pki);
        int calls = 0;
        var run = Start(fake, checkPeer: (_, _) => Interlocked.Increment(ref calls) == 1 ? "process 4242 runs as another user" : null);
        int port = await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var intruder = await FakeViewer.ConnectAsync(port))
        {
            var buf = new byte[12];
            // Closed (reset) without a byte of RFB.
            int n;
            try { n = await intruder.Stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (IOException) { n = 0; }
            Assert.Equal(0, n);
        }
        using var viewer = await FakeViewer.ConnectAsync(port);
        await viewer.HandshakeAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, fake.WebSocketCount);
    }

    [Fact]
    public async Task NoViewerInTimeEndsTheBridge()
    {
        await using var fake = new FakeVncproxy(Pki);
        var run = Start(fake, viewerTimeout: TimeSpan.FromMilliseconds(300));
        var (outcome, reason) = await run.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(VncBridgeOutcome.NoViewer, outcome);
        Assert.Contains("no VNC viewer connected", reason);
    }

    [Fact]
    public async Task AViewerThatFailsAtOnceEndsTheWait()
    {
        await using var fake = new FakeVncproxy(Pki);
        var port = new TaskCompletionSource<int>();
        var options = new VncBridgeOptions
        {
            Connect = ct => VncproxyConnector.ConnectAsync(fake.Descriptor(), ct),
            StartViewer = _ => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 3") { CreateNoWindow = true, UseShellExecute = false }),
            KeepAlive = TimeSpan.Zero,
            Listening = p => port.SetResult(p),
        };
        var (outcome, reason) = await VncBridge.RunAsync(options).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(VncBridgeOutcome.NoViewer, outcome);
        Assert.Contains("exited with code 3", reason);
    }

    [Fact]
    public async Task ServerClosingEndsTheSessionAndClosesTheViewer()
    {
        await using var fake = new FakeVncproxy(Pki)
        {
            Script = async (ws, ct) =>
            {
                var io = new FakeVncproxy.WsReader(null, ws, ct);
                await QemuHandshakeAsync(ws, io);
                // qm vncproxy exiting: pveproxy drops the connection without a close frame.
                ws.Abort();
            },
        };
        var run = Start(fake);
        using var viewer = await FakeViewer.ConnectAsync(await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await viewer.HandshakeAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var (outcome, reason) = await run.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(VncBridgeOutcome.Ended, outcome);
        Assert.Equal("the VNC server closed the connection", reason);
        Assert.Equal(0, await viewer.Stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>The fake QEMU's handshake with the right password, for scripted servers.</summary>
    static async Task QemuHandshakeAsync(WebSocket ws, FakeVncproxy.WsReader io, byte[]? serverInit = null)
    {
        await io.SendAsync("RFB 003.008\n"u8.ToArray());
        await io.ReadExactAsync(new byte[12]);
        await io.SendAsync([1, 2]);
        await io.ReadExactAsync(new byte[1]);
        var challenge = new byte[16];
        await io.SendAsync(challenge);
        var response = new byte[16];
        await io.ReadExactAsync(response);
        Assert.Equal(FakeVncproxy.ExpectedResponse(challenge, FakeVncproxy.Password), response);
        await io.SendAsync([0, 0, 0, 0]);
        await io.ReadExactAsync(new byte[1]);
        await io.SendAsync(serverInit ?? FakeVncproxy.ServerInit(640, 480, "x"));
    }

    [Theory]
    [InlineData("garbage-not-rfb", "does not speak RFB")]
    [InlineData("count0", "refused the connection: no way")]
    [InlineData("types", "none of which the bridge speaks")]
    [InlineData("longreason", "over-long failure reason")]
    [InlineData("hugeinit", "framebuffer")]
    [InlineData("longname", "name is too long")]
    [InlineData("badformat", "invalid pixel format")]
    [InlineData("close", "closed during the handshake")]
    public async Task MalformedServersAreRefused(string kind, string expected)
    {
        await using var fake = new FakeVncproxy(Pki)
        {
            Script = async (ws, ct) =>
            {
                var io = new FakeVncproxy.WsReader(null, ws, ct);
                switch (kind)
                {
                    case "garbage-not-rfb": await io.SendAsync("HTTP/1.1 200\n"u8.ToArray()[..12]); break;
                    case "close": await io.SendAsync("RFB 003.008\n"u8.ToArray()); await io.ReadExactAsync(new byte[12]); ws.Abort(); return;
                    default:
                        await io.SendAsync("RFB 003.008\n"u8.ToArray());
                        await io.ReadExactAsync(new byte[12]);
                        break;
                }
                switch (kind)
                {
                    case "count0": await io.SendAsync([0, 0, 0, 0, 6, .. "no way"u8]); break;
                    case "types": await io.SendAsync([2, 19, 30]); break;
                    case "longreason": await io.SendAsync([0, 0x7F, 0xFF, 0xFF, 0xFF]); break;
                    case "hugeinit" or "longname" or "badformat":
                        await io.SendAsync([1, 2]);
                        await io.ReadExactAsync(new byte[1]);
                        var challenge = new byte[16];
                        await io.SendAsync(challenge);
                        await io.ReadExactAsync(new byte[16]);
                        await io.SendAsync([0, 0, 0, 0]);
                        await io.ReadExactAsync(new byte[1]);
                        var init = FakeVncproxy.ServerInit(640, 480, "x");
                        if (kind == "hugeinit") BinaryPrimitives.WriteUInt16BigEndian(init, 60000);
                        if (kind == "longname") BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), 1 << 30);
                        if (kind == "badformat") init[4] = 24;
                        await io.SendAsync(init);
                        break;
                }
                try { await io.ReadExactAsync(new byte[1]); } catch (Exception e) when (e is EndOfStreamException or WebSocketException or OperationCanceledException) { }
            },
        };
        var run = Start(fake);
        using var viewer = await FakeViewer.ConnectAsync(await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAnyAsync<Exception>(() => viewer.HandshakeAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        var (outcome, reason) = await run.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(VncBridgeOutcome.ConnectFailed, outcome);
        Assert.Contains(expected, reason);
    }

    [Fact]
    public async Task KeepAliveGoesBetweenMessagesOnlyWhenIdle()
    {
        await using var fake = new FakeVncproxy(Pki);
        var run = Start(fake, keepAlive: TimeSpan.FromMilliseconds(400));
        using var viewer = await FakeViewer.ConnectAsync(await run.Port.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await viewer.HandshakeAsync().WaitAsync(TimeSpan.FromSeconds(15));
        // Half a KeyEvent: the stream is inside a message, so nothing may be inserted.
        await viewer.Stream.WriteAsync(new byte[] { 4, 1, 0, 0 });
        await Task.Delay(1200);
        Assert.Equal(0, Volatile.Read(ref fake.UpdateRequests));
        // The rest of the KeyEvent: now idle at a boundary, the keep-alive request follows.
        await viewer.Stream.WriteAsync(new byte[] { 0, 0, 0, 0x61 });
        Assert.Equal("fbur 1", fake.Expect("fbur", 10000));
        var got = fake.ReceivedBytes();
        Assert.Equal(new byte[] { 4, 1, 0, 0, 0, 0, 0, 0x61, 3, 1, 0, 0, 0, 0, 4, 0, 3, 0 }, got[..18]);
    }

    [Fact]
    public void LoopbackPeerFindsThisProcessAndAcceptsIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, serverPort);
        using var accepted = listener.AcceptSocket();
        int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        Assert.Equal((uint)Environment.ProcessId, LoopbackPeer.FindOwner(clientPort, serverPort));
        Assert.Null(LoopbackPeer.Check(clientPort, serverPort));
        Assert.Equal(0u, LoopbackPeer.FindOwner(clientPort, serverPort + 1 == 65536 ? 1 : serverPort + 1));
        Assert.NotNull(LoopbackPeer.Check(1, 2));
    }

    [Fact]
    public void ProcessCheckRefusesAnotherUsersProcess()
    {
        // The System process (PID 4) runs as SYSTEM: never this user.
        Assert.NotNull(LoopbackPeer.CheckProcess(4));
        Assert.Null(LoopbackPeer.CheckProcess((uint)Environment.ProcessId));
    }
}
