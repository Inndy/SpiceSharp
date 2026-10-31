using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace Pve.Term.Tests;

/// <summary>Offline regressions for the 2026-10-31 bridge review. All endpoints are loopback; no programs start.</summary>
public class VncBridgeReviewTests
{
    [Fact]
    public void PeerLookupMustMatchTheRemoteLoopbackAddressToo()
    {
        // There is no connection to 127.0.0.1:serverPort. The same port on another loopback address
        // must not supply a PID for the bridge's 127.0.0.1 listener.
        using var listener = new TcpListener(IPAddress.Parse("127.0.0.2"), 0);
        listener.Start();
        int serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(IPAddress.Parse("127.0.0.2"), serverPort);
        using var accepted = listener.AcceptSocket();
        int clientPort = ((IPEndPoint)client.LocalEndPoint!).Port;
        Assert.Equal(0u, LoopbackPeer.FindOwner(clientPort, serverPort));
        Assert.NotNull(LoopbackPeer.Check(clientPort, serverPort));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("client-init")]
    [InlineData("server-init")]
    public async Task ViewerDeadlineMustCoverTheWholeHandshake(string stall)
    {
        using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();
        using var upstreamClient = new TcpClient();
        await upstreamClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)upstreamListener.LocalEndpoint).Port);
        using var upstreamServer = await upstreamListener.AcceptTcpClientAsync();
        using var clientWs = WebSocket.CreateFromStream(upstreamClient.GetStream(), false, null, Timeout.InfiniteTimeSpan);
        using var serverWs = WebSocket.CreateFromStream(upstreamServer.GetStream(), true, null, Timeout.InfiniteTimeSpan);
        // The upstream security phase is already complete. No TLS, ticket, or fake API is needed to test this deadline.
        var upstream = new VncUpstream(clientWs);
        using var stop = new CancellationTokenSource();
        var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = VncBridge.RunAsync(new VncBridgeOptions
        {
            Connect = _ => Task.FromResult(upstream),
            Listening = p => port.SetResult(p),
            ViewerTimeout = TimeSpan.FromSeconds(1),
            KeepAlive = TimeSpan.Zero,
        }, stop.Token);
        using var viewer = new TcpClient();
        try
        {
            await viewer.ConnectAsync(IPAddress.Loopback, await port.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            var stream = viewer.GetStream();
            var version = new byte[12];
            await stream.ReadExactlyAsync(version).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RfbHandshake.Version38.ToArray(), version);
            if (stall != "version")
            {
                await stream.WriteAsync(RfbHandshake.Version38.ToArray());
                var types = new byte[2];
                await stream.ReadExactlyAsync(types).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(new byte[] { 1, 1 }, types);
                await stream.WriteAsync(new byte[] { 1 });
                var result = new byte[4];
                await stream.ReadExactlyAsync(result).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(new byte[4], result);
                if (stall == "server-init")
                {
                    await stream.WriteAsync(new byte[] { 1 });
                    var init = new byte[1];
                    await serverWs.ReceiveAsync(init.AsMemory(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(1, init[0]);
                }
            }
            var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.True(completed == run, $"--wait expired, but the bridge is still waiting for {stall}");
            Assert.NotEqual(VncBridgeOutcome.Ended, (await run).Outcome);
        }
        finally
        {
            stop.Cancel();
            viewer.Dispose();
            serverWs.Abort();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is OperationCanceledException or IOException or RfbHandshakeException) { }
        }
    }

    [Fact]
    public async Task VncproxyResponseMustHaveABoundedBodyBeforeJsonParsing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string json = "{\"data\":{\"user\":\"test\",\"ticket\":\"synthetic\",\"port\":5900,\"padding\":\""
            + new string('x', 2 * 1024 * 1024) + "\"}}";
        var body = Encoding.ASCII.GetBytes(json);
        var serve = ServeAsync();
        try
        {
            // Directly exercise the HTTP body reader without TLS or a persisted certificate key.
            // This uses the same SendAsync/ReadAsByteArrayAsync/ParseSession path as HTTPS in production.
            var descriptor = new TermDescriptor
            {
                ApiUrl = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"),
                Node = "test", Target = TermTarget.Qemu, Console = ConsoleKind.Vnc,
                Vmid = 100, ApiToken = "test@pve!test=synthetic",
            };
            await Assert.ThrowsAsync<TermConnectException>(() =>
                VncproxyConnector.RequestSessionAsync(descriptor, new PveTls(null), stop.Token));
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serve; }
            catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
        }

        async Task ServeAsync()
        {
            using var tcp = await listener.AcceptTcpClientAsync(stop.Token);
            var stream = tcp.GetStream();
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(one, stop.Token);
                header.Append((char)one[0]);
                Assert.True(header.Length <= 8192);
            }
            await stream.ReadExactlyAsync(new byte[11], stop.Token); // websocket=1
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
            await stream.WriteAsync(body, stop.Token);
        }
    }
}
