using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;

namespace Pve.Term;

/// <summary>
/// A connected termproxy session over a WebSocket: frames input as <c>0:LEN:DATA</c>, resizes as <c>1:C:R:</c>,
/// and sends the <c>2</c> keep-alive only after <see cref="KeepAliveInterval"/> without any other message
/// (docs/decisions/0036). Sends are serialized; reads come from one reader. The data path does not allocate.
/// </summary>
public sealed class TermproxyChannel : ITermChannel
{
    /// <summary>Default: one ping after 4 idle minutes, under the 5-minute timeout the pve-xtermjs README states.</summary>
    public static readonly TimeSpan DefaultKeepAlive = TimeSpan.FromMinutes(4);
    /// <summary>pveproxy refuses client frames over 128 KiB (PVE/APIServer/AnyEvent.pm); stay well below.</summary>
    internal const int MaxChunk = 32 * 1024;

    readonly WebSocket _ws;
    readonly byte[] _send = new byte[MaxChunk + TermproxyProtocol.MaxDataHeader];
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly CancellationTokenSource _life = new();
    readonly Task _keepAlive;
    byte[]? _pending;
    int _pendingOffset;
    long _lastSend = Stopwatch.GetTimestamp();
    int _cols = -1, _rows = -1;
    volatile string? _closeReason;

    public TimeSpan KeepAliveInterval { get; }
    public string? CloseReason => _closeReason;
    /// <summary>Keep-alive pings sent so far (for tests and logs).</summary>
    public long PingsSent { get; private set; }

    /// <param name="ws">An open WebSocket on which the <c>OK</c> handshake already happened.</param>
    /// <param name="initialOutput">Terminal bytes that arrived in the same message as <c>OK</c>.</param>
    /// <param name="keepAlive">Idle time before a ping; <see cref="TimeSpan.Zero"/> disables pings.</param>
    internal TermproxyChannel(WebSocket ws, ReadOnlySpan<byte> initialOutput, TimeSpan keepAlive)
    {
        _ws = ws;
        if (!initialOutput.IsEmpty) _pending = initialOutput.ToArray();
        KeepAliveInterval = keepAlive;
        _keepAlive = keepAlive > TimeSpan.Zero ? KeepAliveLoopAsync() : Task.CompletedTask;
    }

    /// <summary>Sends the <c>user:ticket</c> line and waits for <c>OK</c>; returns the open channel.</summary>
    internal static async Task<TermproxyChannel> HandshakeAsync(WebSocket ws, string user, string ticket, TimeSpan keepAlive, CancellationToken ct)
    {
        await ws.SendAsync(TermproxyProtocol.AuthLine(user, ticket), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        var buf = new byte[4096];
        int have = 0;
        while (have < 2)
        {
            ValueWebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf.AsMemory(have), ct).ConfigureAwait(false); }
            catch (WebSocketException) { r = new(0, WebSocketMessageType.Close, true); }
            if (r.MessageType == WebSocketMessageType.Close)
                throw new TermConnectException("termproxy closed the connection before accepting the ticket (it expired, or the token lacks the console privilege).");
            have += r.Count;
            if (have == buf.Length) break;
        }
        if (!buf.AsSpan(0, 2).SequenceEqual(TermproxyProtocol.Ok))
            throw new TermConnectException("termproxy did not answer OK to the ticket.");
        return new TermproxyChannel(ws, buf.AsSpan(2, have - 2), keepAlive);
    }

    // Each operation has a synchronous fast path that creates no async state machine when the WebSocket completes at
    // once (the common case for sends, and for reads while output is streaming), and a pooled async slow path.

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pending is { } p)
        {
            int n = Math.Min(buffer.Length, p.Length - _pendingOffset);
            p.AsSpan(_pendingOffset, n).CopyTo(buffer.Span);
            _pendingOffset += n;
            if (_pendingOffset == p.Length) _pending = null;
            return new(n);
        }
        if (_closeReason is not null) return new(0);
        ValueTask<ValueWebSocketReceiveResult> receive;
        try { receive = _ws.ReceiveAsync(buffer, cancellationToken); }
        catch (Exception e) when (IsTransportError(e)) { return new(Lost(e)); }
        if (receive.IsCompletedSuccessfully)
        {
            int n = Received(receive.Result);
            if (n >= 0) return new(n);
            return ReadSlowAsync(default, false, buffer, cancellationToken); // an empty frame: read on
        }
        return ReadSlowAsync(receive, true, buffer, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    async ValueTask<int> ReadSlowAsync(ValueTask<ValueWebSocketReceiveResult> pending, bool hasPending, Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            if (_closeReason is not null) return 0;
            ValueWebSocketReceiveResult r;
            try { r = hasPending ? await pending.ConfigureAwait(false) : await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false); }
            catch (Exception e) when (IsTransportError(e)) { return Lost(e); }
            hasPending = false;
            int n = Received(r);
            if (n >= 0) return n;
        }
    }

    /// <summary>The byte count, 0 at the end of the session, or -1 for an empty frame.</summary>
    int Received(ValueWebSocketReceiveResult r)
    {
        if (r.MessageType == WebSocketMessageType.Close)
        {
            _closeReason ??= string.IsNullOrEmpty(_ws.CloseStatusDescription)
                ? $"the server closed the session ({_ws.CloseStatus})" : "the server closed the session: " + _ws.CloseStatusDescription;
            return 0;
        }
        return r.Count > 0 ? r.Count : -1;
    }

    static bool IsTransportError(Exception e) => e is WebSocketException or IOException or ObjectDisposedException;

    int Lost(Exception e)
    {
        // pveproxy drops the TCP connection without a close frame when termproxy exits (client_do_disconnect).
        _closeReason ??= e is WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely }
            ? "the server closed the connection" : "connection lost: " + e.Message;
        return 0;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (input.IsEmpty) return default;
        if (input.Length <= MaxChunk && _sendLock.Wait(0))
        {
            if (_closeReason is not null) { _sendLock.Release(); return default; }
            return SendAndRelease(TermproxyProtocol.WriteData(_send, input.Span), cancellationToken);
        }
        return SendSlowAsync(input, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    async ValueTask SendSlowAsync(ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        while (!input.IsEmpty)
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            if (_closeReason is not null) { _sendLock.Release(); return; }
            var chunk = input.Length > MaxChunk ? input[..MaxChunk] : input;
            await SendAndRelease(TermproxyProtocol.WriteData(_send, chunk.Span), ct).ConfigureAwait(false);
            input = input[chunk.Length..];
        }
    }

    public ValueTask ResizeAsync(int cols, int rows, CancellationToken cancellationToken = default)
        => _sendLock.Wait(0) ? ResizeAndRelease(cols, rows, cancellationToken) : ResizeSlowAsync(cols, rows, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    async ValueTask ResizeSlowAsync(int cols, int rows, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        await ResizeAndRelease(cols, rows, ct).ConfigureAwait(false);
    }

    ValueTask ResizeAndRelease(int cols, int rows, CancellationToken ct)
    {
        if (_closeReason is not null || cols == _cols && rows == _rows) { _sendLock.Release(); return default; }
        _cols = cols;
        _rows = rows;
        return SendAndRelease(TermproxyProtocol.WriteResize(_send, cols, rows), ct);
    }

    /// <summary>Sends <c>_send[..length]</c> as one unfragmented frame (pveproxy refuses fragments). The caller holds
    /// the send lock; it is released when the send has finished.</summary>
    ValueTask SendAndRelease(int length, CancellationToken ct)
    {
        ValueTask send;
        try { send = _ws.SendAsync(_send.AsMemory(0, length), WebSocketMessageType.Binary, true, ct); }
        catch (Exception e) when (IsTransportError(e))
        {
            Lost(e);
            _sendLock.Release();
            return default;
        }
        if (!send.IsCompletedSuccessfully) return AwaitSendAndReleaseAsync(send);
        send.GetAwaiter().GetResult();
        Volatile.Write(ref _lastSend, Stopwatch.GetTimestamp());
        _sendLock.Release();
        return default;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    async ValueTask AwaitSendAndReleaseAsync(ValueTask send)
    {
        try
        {
            await send.ConfigureAwait(false);
            Volatile.Write(ref _lastSend, Stopwatch.GetTimestamp());
        }
        catch (Exception e) when (IsTransportError(e)) { Lost(e); }
        finally { _sendLock.Release(); }
    }

    /// <summary>One wake-up per idle interval at most: it sleeps until the interval after the last send.</summary>
    async Task KeepAliveLoopAsync()
    {
        var ct = _life.Token;
        try
        {
            while (_closeReason is null)
            {
                var idle = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSend));
                if (idle < KeepAliveInterval)
                {
                    await Task.Delay(KeepAliveInterval - idle, ct).ConfigureAwait(false);
                    continue;
                }
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                if (_closeReason is not null) { _sendLock.Release(); return; }
                TermproxyProtocol.Ping.CopyTo(_send);
                await SendAndRelease(1, ct).ConfigureAwait(false);
                PingsSent++;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        try { await _keepAlive.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, t.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        _ws.Dispose();
        _life.Dispose();
    }
}
