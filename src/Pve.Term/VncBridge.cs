using System.Diagnostics;
using System.Net.Sockets;

namespace Pve.Term;

/// <summary>How a bridge session ended.</summary>
public enum VncBridgeOutcome
{
    /// <summary>The viewer was connected and the session ran until one side closed it.</summary>
    Ended,
    /// <summary>Proxmox or QEMU refused or failed the connection.</summary>
    ConnectFailed,
    /// <summary>No viewer connected in time, or the viewer could not be started or exited first.</summary>
    NoViewer,
}

/// <summary>What the bridge needs from its host (PveTerm, or tests).</summary>
public sealed class VncBridgeOptions
{
    /// <summary>Opens the upstream (normally <see cref="VncproxyConnector.ConnectAsync"/>); started at once, in
    /// parallel with the viewer.</summary>
    public required Func<CancellationToken, Task<VncUpstream>> Connect { get; init; }
    /// <summary>Starts the viewer for 127.0.0.1:port and returns its process, or null when nothing is started (the
    /// user connects a viewer by hand) or the process can't be watched.</summary>
    public Func<int, Process?>? StartViewer { get; init; }
    /// <summary>Status lines for the user (no secrets).</summary>
    public Action<string> Status { get; init; } = _ => { };
    /// <summary>How long the viewer has to connect and complete the handshake, through ServerInit (the startup
    /// deadline; it does not apply once the relay runs).</summary>
    public TimeSpan ViewerTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Idle time before the relay's keep-alive; <see cref="TimeSpan.Zero"/> turns it off.</summary>
    public TimeSpan KeepAlive { get; init; } = VncRelay.DefaultKeepAlive;
    /// <summary>Send QEMU a shared ClientInit whatever the viewer asked: an exclusive one would disconnect every other
    /// VNC client of the VM, such as a noVNC tab (QEMU's default share policy, decision 0043).</summary>
    public bool ForceShared { get; init; } = true;
    /// <summary>For tests: the local peer check (<see cref="LoopbackPeer.Check"/> by default).</summary>
    public Func<int, int, string?>? CheckPeer { get; init; }
    /// <summary>Called with the local port once the listener is up (tests, logs).</summary>
    public Action<int>? Listening { get; init; }
}

/// <summary>
/// The VNC bridge (docs/decisions/0043): a one-session RFB endpoint on 127.0.0.1 for the user's own VNC viewer,
/// joined to a VM's VNC display through Proxmox's <c>vncproxy</c>/<c>vncwebsocket</c>. The bridge authenticates to
/// QEMU with the session's one-time password, presents the viewer a None-security handshake (the viewer's process was
/// checked instead), forwards ServerInit, and then relays bytes unchanged.
/// </summary>
public static class VncBridge
{
    public static async Task<(VncBridgeOutcome Outcome, string Reason)> RunAsync(VncBridgeOptions o, CancellationToken ct = default)
    {
        using var listener = VncListener.Start(o.CheckPeer);
        int port = listener.Port;
        o.Listening?.Invoke(port);
        using var life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var upstream = o.Connect(life.Token);
        // Observed whatever happens below, so a failure after an early return is never unobserved.
        _ = upstream.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        try
        {
            Process? viewer;
            try { viewer = o.StartViewer?.Invoke(port); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
            {
                return (VncBridgeOutcome.NoViewer, $"the VNC viewer could not be started: {e.Message}");
            }
            o.Status($"Waiting for the VNC viewer on 127.0.0.1:{port}...");

            // One startup deadline (--wait) covers the accept, both handshakes, ServerInit and its forwarding, and the
            // report of a failure to the viewer; it ends when the relay starts, so it never cuts a running session
            // (review 2026-10-31 R2). Its expiry is told apart from the caller's cancellation through ct.
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
            startup.CancelAfter(o.ViewerTimeout);
            string waited = $"{o.ViewerTimeout.TotalSeconds:0} s";
            Socket socket;
            {
                var accept = listener.AcceptAsync(o.Status, startup.Token);
                // A viewer that fails at once (bad arguments, a refused option) ends the wait; one that exits with 0
                // may have handed the connection to a running instance, so the wait goes on.
                var failed = viewer is null ? null : WaitForFailureAsync(viewer, startup.Token);
                try
                {
                    if (failed is not null && await Task.WhenAny(accept, failed).ConfigureAwait(false) == failed && !accept.IsCompleted)
                    {
                        startup.Cancel();
                        _ = accept.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        return (VncBridgeOutcome.NoViewer, $"the VNC viewer exited with code {await failed.ConfigureAwait(false)} before it connected");
                    }
                    socket = await accept.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return (VncBridgeOutcome.NoViewer, $"no VNC viewer connected within {waited}");
                }
                finally { viewer?.Dispose(); }
            }

            using (socket)
            await using (var local = new NetworkStream(socket, ownsSocket: false))
            {
                VncUpstream up;
                int minor;
                try
                {
                    minor = await LocalRfbHandshake.BeginAsync(local, startup.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is RfbHandshakeException or IOException or EndOfStreamException)
                {
                    return (VncBridgeOutcome.NoViewer, "the VNC viewer did not complete the RFB handshake: " + e.Message);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return (VncBridgeOutcome.NoViewer, $"the VNC viewer did not complete the RFB handshake within {waited}");
                }
                try { up = await upstream.WaitAsync(startup.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is TermConnectException or TermDescriptorException or RfbHandshakeException)
                {
                    try { await LocalRfbHandshake.CompleteAsync(local, minor, e.Message, startup.Token).ConfigureAwait(false); }
                    catch (Exception x) when (x is RfbHandshakeException or IOException or OperationCanceledException) { }
                    return (VncBridgeOutcome.ConnectFailed, e.Message);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return (VncBridgeOutcome.ConnectFailed, $"the VNC console did not open within {waited}");
                }
                await using (up)
                {
                    bool shared;
                    byte[] serverInit;
                    string stage = "the VNC viewer did not send ClientInit";
                    try
                    {
                        shared = await LocalRfbHandshake.CompleteAsync(local, minor, null, startup.Token).ConfigureAwait(false);
                        if (!shared && o.ForceShared) o.Status("The viewer asked for exclusive access; the bridge asks QEMU for a shared session, so other consoles stay open.");
                        stage = "the VNC server did not send ServerInit";
                        serverInit = await up.InitAsync(shared || o.ForceShared, startup.Token).ConfigureAwait(false);
                        stage = "the VNC viewer did not take ServerInit";
                        await local.WriteAsync(serverInit, startup.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is RfbHandshakeException or IOException or EndOfStreamException)
                    {
                        return (VncBridgeOutcome.ConnectFailed, "the VNC handshake failed: " + e.Message);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return (stage.StartsWith("the VNC server", StringComparison.Ordinal) ? VncBridgeOutcome.ConnectFailed : VncBridgeOutcome.NoViewer,
                            $"{stage} within {waited}");
                    }
                    // The relay runs without the startup deadline; cancelling it also ends the viewer-exit watch.
                    startup.Cancel();
                    var (w, h) = RfbHandshake.ServerInitSize(serverInit);
                    o.Status($"Connected: RFB 3.{minor} viewer, {w}x{h} display.");
                    var relay = new VncRelay(up, socket, serverInit, o.KeepAlive) { Log = o.Status };
                    string reason = await relay.RunAsync(life.Token).ConfigureAwait(false);
                    return (VncBridgeOutcome.Ended, reason);
                }
            }
        }
        finally
        {
            life.Cancel();
            // An upstream that connected but was never used (or connects after an early return) is closed.
            _ = upstream.ContinueWith(static t => t.Result.DisposeAsync().AsTask(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
    }

    static async Task<int> WaitForFailureAsync(Process p, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                if (p.ExitCode != 0) return p.ExitCode;
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); // exited cleanly: not a failure
            }
        }
        catch (InvalidOperationException) { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); return 0; } // not ours to watch
    }
}
