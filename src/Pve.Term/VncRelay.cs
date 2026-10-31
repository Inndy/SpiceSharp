using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace Pve.Term;

/// <summary>
/// Follows the viewer-to-server RFB byte stream just far enough to know where each message ends, so the bridge's
/// keep-alive can be put between two messages (docs/decisions/0043). Lengths follow QEMU's protocol_client_msg
/// (ui/vnc.c), which is also the set QEMU accepts: any other message type makes QEMU drop the connection, and makes
/// this tracker give up (<see cref="Lost"/>), which only turns the keep-alive off. Nothing is changed or held back.
/// </summary>
public sealed class ClientMessageTracker
{
    readonly byte[] _head = new byte[8];
    int _headLength;
    long _remaining;

    /// <summary>Between two messages: a message can be inserted here.</summary>
    public bool AtBoundary => !Lost && _headLength == 0 && _remaining == 0;
    /// <summary>A message type the tracker does not know was seen; boundaries are unknown from then on.</summary>
    public bool Lost { get; private set; }
    /// <summary>The unknown message type that made the tracker give up, for the log.</summary>
    public int LostType { get; private set; } = -1;

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty && !Lost)
        {
            if (_remaining > 0)
            {
                int n = (int)Math.Min(_remaining, data.Length);
                _remaining -= n;
                data = data[n..];
                continue;
            }
            _head[_headLength++] = data[0];
            data = data[1..];
            long total = Length(_head.AsSpan(0, _headLength));
            if (total < 0) continue; // the header is not complete yet
            if (total == 0) { Lost = true; LostType = _head[0]; return; }
            _remaining = total - _headLength;
            _headLength = 0;
        }
    }

    /// <summary>The message's whole length given its first bytes; -1 when more header bytes are needed, 0 when the
    /// type is unknown. Header sizes never exceed 8 bytes.</summary>
    static long Length(ReadOnlySpan<byte> h)
    {
        switch (h[0])
        {
            case 0: return 20;                                   // SetPixelFormat
            case 2: return h.Length < 4 ? -1 : 4 + 4L * BinaryPrimitives.ReadUInt16BigEndian(h[2..]); // SetEncodings
            case 3: return 10;                                   // FramebufferUpdateRequest
            case 4: return 8;                                    // KeyEvent
            case 5: return 6;                                    // PointerEvent
            case 6:                                              // ClientCutText; QEMU takes abs() of an s32 (extended clipboard)
                if (h.Length < 8) return -1;
                return 8 + Math.Abs((long)BinaryPrimitives.ReadInt32BigEndian(h[4..]));
            case 250: return 4;                                  // xvp
            case 251: return h.Length < 8 ? -1 : 8 + 16L * h[6]; // SetDesktopSize
            case 255:                                            // QEMU client messages
                if (h.Length < 2) return -1;
                if (h[1] == 0) return 12;                        // extended key event
                if (h[1] != 1) return 0;
                if (h.Length < 4) return -1;                     // audio: u16 operation
                return BinaryPrimitives.ReadUInt16BigEndian(h[2..]) switch { 0 or 1 => 4, 2 => 10, _ => 0 };
            default: return 0;
        }
    }
}

/// <summary>
/// Relays a VNC session after ServerInit, byte for byte, between the local viewer's socket and Proxmox's WebSocket
/// (docs/decisions/0043). Each socket read becomes one unfragmented binary frame (pveproxy refuses fragments and
/// frames over 128 KiB); each frame's payload is written to the socket. There is no timer except the keep-alive:
/// <c>qm vncproxy</c> ends a session after 15 minutes without traffic, so after <see cref="KeepAlive"/> without any
/// traffic either way, the relay sends one incremental FramebufferUpdateRequest between two viewer messages. QEMU
/// answers it only when the screen changed. The data path allocates nothing per message.
/// </summary>
public sealed class VncRelay
{
    /// <summary>Default idle time before the keep-alive: well inside qm vncproxy's 15 minutes (qemu-server qm.pm).</summary>
    public static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromMinutes(10);
    /// <summary>Viewer bytes per frame; below pveproxy's 128 KiB limit.</summary>
    internal const int UpChunk = 32 * 1024;

    readonly WebSocket _ws;
    readonly Socket _local;
    readonly ReadOnlyMemory<byte> _pending;
    readonly byte[] _up = new byte[UpChunk];
    readonly byte[] _down = new byte[VncUpstream.BufferSize];
    readonly byte[] _keepAliveMessage = new byte[10];
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _end = new();
    long _lastTraffic = Stopwatch.GetTimestamp();
    string? _reason;

    public ClientMessageTracker Tracker { get; } = new();
    public TimeSpan KeepAlive { get; }
    /// <summary>Keep-alive requests sent (for tests and the log).</summary>
    public long KeepAlivesSent { get; private set; }
    public long BytesUp { get; private set; }
    public long BytesDown { get; private set; }
    public Action<string>? Log { get; init; }

    /// <param name="serverInit">The ServerInit the viewer received; its size fills the keep-alive request.</param>
    public VncRelay(VncUpstream upstream, Socket local, ReadOnlySpan<byte> serverInit, TimeSpan keepAlive)
    {
        _ws = upstream.WebSocket;
        _pending = upstream.Pending;
        _local = local;
        KeepAlive = keepAlive;
        var (w, h) = RfbHandshake.ServerInitSize(serverInit);
        _keepAliveMessage[0] = 3; // FramebufferUpdateRequest
        _keepAliveMessage[1] = 1; // incremental: QEMU only notes that an update is wanted (framebuffer_update_request)
        BinaryPrimitives.WriteUInt16BigEndian(_keepAliveMessage.AsSpan(6), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(_keepAliveMessage.AsSpan(8), (ushort)h);
    }

    /// <summary>Runs until either side ends; returns why, in words for the user. Both sides are closed then.</summary>
    public async Task<string> RunAsync(CancellationToken ct = default)
    {
        using var reg = ct.Register(static s => ((VncRelay)s!).Finish("cancelled"), this);
        var down = DownAsync();
        var up = UpAsync();
        var keep = KeepAlive > TimeSpan.Zero ? KeepAliveAsync() : Task.CompletedTask;
        await Task.WhenAll(down, up).ConfigureAwait(false);
        _end.Cancel();
        try { await keep.ConfigureAwait(false); } catch (OperationCanceledException) { }
        return Volatile.Read(ref _reason) ?? "the session ended";
    }

    void Finish(string reason)
    {
        if (Interlocked.CompareExchange(ref _reason, reason, null) is not null) return;
        try { _local.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        _local.Close(0);
        _ws.Abort();
        try { _end.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Server to viewer.</summary>
    async Task DownAsync()
    {
        try
        {
            for (int sent = 0; sent < _pending.Length;)
                sent += await _local.SendAsync(_pending[sent..], SocketFlags.None).ConfigureAwait(false);
            while (Volatile.Read(ref _reason) is null)
            {
                var r = await _ws.ReceiveAsync(_down.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    Finish(string.IsNullOrEmpty(_ws.CloseStatusDescription) ? "the VNC server closed the connection"
                        : "the VNC server closed the connection: " + _ws.CloseStatusDescription);
                    return;
                }
                if (r.Count == 0) continue;
                // Inline rather than a helper: a helper's async state machine is a heap object per call in Debug.
                for (int sent = 0; sent < r.Count;)
                    sent += await _local.SendAsync(_down.AsMemory(sent, r.Count - sent), SocketFlags.None).ConfigureAwait(false);
                BytesDown += r.Count;
                Volatile.Write(ref _lastTraffic, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception e) when (e is WebSocketException or SocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // pveproxy drops the TCP connection without a close frame when the VNC server goes (qm vncproxy exits).
            Finish(e is WebSocketException ? "the VNC server closed the connection" : "the VNC viewer's connection was lost");
        }
    }

    /// <summary>Viewer to server.</summary>
    async Task UpAsync()
    {
        try
        {
            while (Volatile.Read(ref _reason) is null)
            {
                int n = await _local.ReceiveAsync(_up.AsMemory(), SocketFlags.None).ConfigureAwait(false);
                if (n == 0) { Finish("the VNC viewer closed the connection"); return; }
                await _sendLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    bool wasLost = Tracker.Lost;
                    Tracker.Feed(_up.AsSpan(0, n));
                    if (!wasLost && Tracker.Lost)
                        Log?.Invoke($"the viewer sent message type {Tracker.LostType}, which the bridge does not know; keep-alive off");
                    await _ws.SendAsync(_up.AsMemory(0, n), WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false);
                }
                finally { _sendLock.Release(); }
                BytesUp += n;
                Volatile.Write(ref _lastTraffic, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception e) when (e is WebSocketException or SocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            Finish(e is WebSocketException ? "the VNC server closed the connection" : "the VNC viewer's connection was lost");
        }
    }

    /// <summary>One wake-up per idle interval at most: it sleeps until the interval after the last traffic.</summary>
    async Task KeepAliveAsync()
    {
        var ct = _end.Token;
        while (Volatile.Read(ref _reason) is null)
        {
            var idle = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastTraffic));
            if (idle < KeepAlive)
            {
                await Task.Delay(KeepAlive - idle, ct).ConfigureAwait(false);
                continue;
            }
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _reason) is not null) return;
                if (Tracker.AtBoundary)
                {
                    await _ws.SendAsync(_keepAliveMessage, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    KeepAlivesSent++;
                }
                else if (Tracker.Lost) return; // boundaries unknown: inserting a message could corrupt the stream
            }
            catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException) { return; }
            finally { _sendLock.Release(); }
            Volatile.Write(ref _lastTraffic, Stopwatch.GetTimestamp());
        }
    }
}
