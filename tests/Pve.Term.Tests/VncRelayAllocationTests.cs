using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Xunit.Abstractions;

namespace Pve.Term.Tests;

/// <summary>
/// Allocation measurements in this project. Not run in parallel with other tests: their GCs disturb a per-thread
/// window (<see cref="Spice.Tests.AllocationWindow"/> discards a window a GC ran in), as in Spice.Replay.Tests'
/// "Allocation profiling" collection.
/// </summary>
[CollectionDefinition("Allocation profiling", DisableParallelization = true)]
public sealed class AllocationProfilingCollection;

/// <summary>
/// The relay's data path must not allocate per message (AGENTS.md energy rules, docs/decisions/0043). The relay's pumps
/// run on thread-pool and I/O threads, so a per-thread counter can't see them, and the process-wide counter also sees
/// whatever any other test left running in the test host (servers, sockets, timers). So the measurement runs in a
/// process of its own: <c>Pve.Term.Tests.exe --relay-allocation</c> (<see cref="Probe"/>), in which nothing else runs.
/// </summary>
[Collection("Allocation profiling")]
public class VncRelayAllocationTests(ITestOutputHelper log)
{
    /// <summary>Allowance for runtime bookkeeping that can land in the window (timer queue, thread pool); anything per
    /// message would be at least tens of kilobytes over 2,000 round trips.</summary>
    const long MaxBytes = 1024;

    [Fact]
    public async Task RelayingMessagesDoesNotAllocate()
    {
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Pve.Term.Tests.exe"), "--relay-allocation")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string output;
        try
        {
            output = await p.StandardOutput.ReadToEndAsync(limit.Token);
            await p.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill();
            throw new Xunit.Sdk.XunitException("the relay allocation process did not finish within 60 s");
        }
        log.WriteLine(output);
        string errors = await stderr;
        Assert.True(p.ExitCode == 0, $"the relay allocation process exited with {p.ExitCode}: {errors}");
        string? best = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("best ", StringComparison.Ordinal));
        Assert.NotNull(best);
        long bytes = long.Parse(best["best ".Length..], CultureInfo.InvariantCulture);
        Assert.True(bytes <= MaxBytes, $"relay allocated {bytes} B for 2,000 round trips");
    }

    /// <summary>Run by <c>Pve.Term.Tests.exe --relay-allocation</c>: relays 2,000 viewer-to-server-to-viewer round trips
    /// after a warm-up and prints each attempt's process-wide allocation, then <c>best N</c>. Exit code 0 when the
    /// session also ended as expected.</summary>
    internal static int Run()
    {
        long best = RunAsync(Console.WriteLine).GetAwaiter().GetResult();
        Console.WriteLine($"best {best.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    static async Task<long> RunAsync(Action<string> log)
    {
        // Upstream: a WebSocket pair over loopback TCP (no TLS: the relay sees only the WebSocket).
        using var wsListener = new TcpListener(IPAddress.Loopback, 0);
        wsListener.Start();
        using var upClient = new TcpClient();
        await upClient.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)wsListener.LocalEndpoint).Port);
        using var upServer = await wsListener.AcceptTcpClientAsync();
        upClient.NoDelay = upServer.NoDelay = true;
        using var clientWs = WebSocket.CreateFromStream(upClient.GetStream(), new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
        using var serverWs = WebSocket.CreateFromStream(upServer.GetStream(), new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        var echo = EchoAsync(serverWs);

        // Local: the viewer's socket pair.
        using var localListener = new TcpListener(IPAddress.Loopback, 0);
        localListener.Start();
        using var viewer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        viewer.Connect(IPAddress.Loopback, ((IPEndPoint)localListener.LocalEndpoint).Port);
        var relaySide = localListener.AcceptSocket();
        relaySide.NoDelay = true;

        var init = FakeVncproxy.ServerInit(640, 480, "x");
        var relay = new VncRelay(new VncUpstream(clientWs), relaySide, init, TimeSpan.Zero);
        var run = relay.RunAsync();

        var message = new byte[] { 3, 1, 0, 0, 0, 0, 2, 128, 1, 224 }; // FramebufferUpdateRequest, echoed back
        var back = new byte[message.Length];
        void Round()
        {
            viewer.Send(message);
            int have = 0;
            while (have < back.Length) have += viewer.Receive(back, have, back.Length - have, SocketFlags.None);
        }
        for (int i = 0; i < 500; i++) Round(); // warm up: pooled buffers, boxed state machines, socket event args

        long best = long.MaxValue;
        for (int attempt = 0; attempt < 5 && best > 0; attempt++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < 2_000; i++) Round();
            long bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
            log($"attempt {attempt}: {bytes} B for 2,000 round trips");
            best = Math.Min(best, bytes);
        }

        viewer.Shutdown(SocketShutdown.Both);
        viewer.Close();
        string reason = await run.WaitAsync(TimeSpan.FromSeconds(10));
        if (reason != "the VNC viewer closed the connection") throw new InvalidOperationException("relay ended: " + reason);
        if (relay.BytesUp < 2500L * message.Length) throw new InvalidOperationException($"relay forwarded only {relay.BytesUp} B");
        await echo.WaitAsync(TimeSpan.FromSeconds(10));
        return best;
    }

    static async Task EchoAsync(WebSocket ws)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buf.AsMemory(), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return;
                await ws.SendAsync(buf.AsMemory(0, r.Count), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException) { }
    }
}
