using System.Buffers;
using System.Security.Cryptography;
using System.Threading.Tasks.Sources;
using Spice.Wire;

namespace Spice.Transport;

/// <summary>Receives one server message. The payload is only valid during the call.</summary>
public delegate void MessageHandler(ushort type, ReadOnlyMemory<byte> payload);

/// <summary>
/// One linked SPICE channel: framing, the ack window, ping/pong, and serialized sends. Messages are delivered
/// synchronously on the receive loop, in order, from one reusable buffer.
/// </summary>
public sealed class SpiceChannel : IAsyncDisposable, IValueTaskSource
{
    public const int MaxMessageSize = 64 << 20;
    /// <summary>Outgoing bytes held for a peer that isn't reading before further sends are dropped.</summary>
    const int MaxQueuedBytes = 8 << 20;

    readonly Stream _stream;
    readonly object _sendLock = new();
    // Double-buffered send queue: callers append to _fill, the writer drains _drain.
    byte[] _fill = new byte[4096], _drain = new byte[4096];
    int _fillLen;
    // The writer loop runs for the channel's lifetime and parks on _wake (a reusable IValueTaskSource) while the
    // queue is empty, so a send that wakes it allocates nothing. _parked: the writer waits and nobody woke it yet.
    ManualResetValueTaskSourceCore<bool> _wake = new() { RunContinuationsAsynchronously = true };
    bool _parked;
    ulong _serial = 1;
    uint _ackWindow, _sinceAck;
    int _disposed;

    public ChannelId Id { get; }
    public bool MiniHeader { get; }
    public CapabilitySet ServerCommonCaps { get; }
    public CapabilitySet ServerChannelCaps { get; }
    public bool IsClosed => _disposed != 0;

    // Written only by the receive loop; read from any thread (64-bit reads are atomic on the supported platforms).
    long _bytesReceived, _messagesReceived;
    /// <summary>Bytes received after the link (headers included). Diagnostics; any thread.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);
    /// <summary>Messages received after the link, the common ones included. Diagnostics; any thread.</summary>
    public long MessagesReceived => Volatile.Read(ref _messagesReceived);

    SpiceChannel(Stream stream, ChannelId id, LinkReply reply)
    {
        _stream = stream;
        Id = id;
        ServerCommonCaps = reply.CommonCaps;
        ServerChannelCaps = reply.ChannelCaps;
        MiniHeader = reply.CommonCaps.Has(CommonCap.MiniHeader);
        _ = WriteLoopAsync(); // parks at once: the queue is empty
    }

    public static CapabilitySet ClientCommonCaps() =>
        new(CommonCap.ProtocolAuthSelection, CommonCap.AuthSpice, CommonCap.MiniHeader);

    /// <summary>Runs the link handshake and ticket authentication on an open stream.</summary>
    public static async Task<SpiceChannel> LinkAsync(Stream stream, uint connectionId, ChannelId id, CapabilitySet channelCaps,
                                                    string? password, CancellationToken ct)
    {
        try
        {
            await stream.WriteAsync(Link.EncodeLinkMess(connectionId, id, ClientCommonCaps(), channelCaps), ct);
            var header = new byte[Link.HeaderSize];
            await ReadExact(stream, header, ct);
            int size = Link.ParseReplyHeader(header);
            var body = new byte[size];
            await ReadExact(stream, body, ct);
            var reply = LinkReply.Parse(body);
            if (reply.Error != LinkError.Ok) throw LinkFailure(reply.Error, id);

            if (reply.CommonCaps.Has(CommonCap.ProtocolAuthSelection))
            {
                var mech = new byte[4];
                ClientMsg.U32(mech, CommonCap.AuthSpice);
                await stream.WriteAsync(mech, ct);
            }
            await stream.WriteAsync(EncryptTicket(password ?? "", reply.PublicKey), ct);

            var result = new byte[4];
            await ReadExact(stream, result, ct);
            var err = (LinkError)BitConverter.ToUInt32(result);
            if (err != LinkError.Ok) throw LinkFailure(err, id);
            return new SpiceChannel(stream, id, reply);
        }
        catch (WireException e)
        {
            throw new SpiceConnectException(ConnectFailure.Protocol, $"The server's reply on the {id} channel is not valid SPICE ({e.Message}).", e);
        }
        catch (EndOfStreamException e)
        {
            throw new SpiceConnectException(ConnectFailure.Closed, $"The server closed the {id} channel during the handshake.", e);
        }
    }

    static SpiceConnectException LinkFailure(LinkError err, ChannelId id) => err switch
    {
        LinkError.PermissionDenied => new(ConnectFailure.TicketRejected, "This console link has expired. In Proxmox, choose Console → SPICE again."),
        LinkError.NeedSecured => new(ConnectFailure.Protocol, $"The server requires TLS for the {id} channel."),
        LinkError.VersionMismatch => new(ConnectFailure.Protocol, "The server uses an incompatible SPICE protocol version."),
        LinkError.ChannelNotAvailable => new(ConnectFailure.Protocol, $"The server has no {id} channel."),
        _ => new(ConnectFailure.Protocol, $"The server refused the {id} channel ({err})."),
    };

    /// <summary>RSA-OAEP (SHA-1) of the NUL-terminated ticket with the server's SubjectPublicKeyInfo key.</summary>
    public static byte[] EncryptTicket(string password, byte[] spki)
    {
        var pw = System.Text.Encoding.UTF8.GetBytes(password);
        if (pw.Length > Link.MaxPasswordLength)
            throw new SpiceConnectException(ConnectFailure.Protocol, "The ticket in the connection file is too long.");
        var plain = new byte[pw.Length + 1];
        pw.CopyTo(plain, 0);
        using var rsa = RSA.Create();
        try { rsa.ImportSubjectPublicKeyInfo(spki, out _); }
        catch (CryptographicException e) { throw new SpiceConnectException(ConnectFailure.Protocol, "The server sent an invalid public key.", e); }
        var cipher = rsa.Encrypt(plain, RSAEncryptionPadding.OaepSHA1);
        CryptographicOperations.ZeroMemory(plain);
        CryptographicOperations.ZeroMemory(pw);
        return cipher;
    }

    static async Task ReadExact(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        await s.ReadExactlyAsync(buf, ct);
    }

    /// <summary>
    /// Reads messages until the connection closes or <paramref name="ct"/> fires. Common messages (SET_ACK, PING,
    /// NOTIFY) are handled here; everything else goes to <paramref name="handler"/>.
    /// </summary>
    public async Task RunAsync(MessageHandler handler, CancellationToken ct)
    {
        var header = new byte[DataHeader.FullSize];
        int headerSize = MiniHeader ? DataHeader.MiniSize : DataHeader.FullSize;
        byte[] payload = ArrayPool<byte>.Shared.Rent(64 << 10);
        try
        {
            while (true)
            {
                await _stream.ReadExactlyAsync(header.AsMemory(0, headerSize), ct);
                var h = DataHeader.Parse(header, MiniHeader);
                if (h.Size > MaxMessageSize) throw new WireException($"{Id}: message size {h.Size} exceeds limit");
                if (payload.Length < h.Size)
                {
                    ArrayPool<byte>.Shared.Return(payload);
                    payload = ArrayPool<byte>.Shared.Rent((int)h.Size);
                }
                var body = payload.AsMemory(0, (int)h.Size);
                await _stream.ReadExactlyAsync(body, ct);
                Volatile.Write(ref _bytesReceived, _bytesReceived + headerSize + h.Size);
                Volatile.Write(ref _messagesReceived, _messagesReceived + 1);

                // Every server message counts toward the ack window, including the ones handled here.
                if (_ackWindow > 0 && ++_sinceAck >= _ackWindow)
                {
                    _sinceAck = 0;
                    Send(CommonMsg.CAck, []);
                }
                if (HandleCommon(h.Type, body)) continue;
                handler(h.Type, body);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    bool HandleCommon(ushort type, ReadOnlyMemory<byte> body)
    {
        Span<byte> tmp = stackalloc byte[16];
        switch (type)
        {
            case CommonMsg.SetAck:
            {
                var r = new SpiceReader(body);
                uint generation = r.U32();
                _ackWindow = r.U32();
                _sinceAck = 0;
                Send(CommonMsg.CAckSync, tmp[..ClientMsg.AckSync(tmp, generation)]);
                return true;
            }
            case CommonMsg.Ping:
            {
                var r = new SpiceReader(body);
                uint id = r.U32();
                ulong ts = r.U64();
                Send(CommonMsg.CPong, tmp[..ClientMsg.Pong(tmp, id, ts)]);
                return true;
            }
            case CommonMsg.Notify:
            {
                var n = Notify.Parse(body);
                Log.Info($"{Id}: server notify (severity {n.Severity}): {n.Message}");
                return true;
            }
            case CommonMsg.Disconnecting:
                Log.Info($"{Id}: server is disconnecting");
                return true;
            case CommonMsg.WaitForChannels or CommonMsg.Migrate or CommonMsg.MigrateData or CommonMsg.List:
                Log.Debug($"{Id}: ignoring common message {type}");
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Queues one message and returns at once; the writer loop writes the queue in order on a pool thread.
    /// Thread-safe, frames are never interleaved, and a peer that stops reading can never block the caller (the UI
    /// thread sends input).
    /// </summary>
    public void Send(ushort type, ReadOnlySpan<byte> body)
    {
        if (_disposed != 0) return;
        lock (_sendLock)
        {
            int need = DataHeader.FullSize + body.Length;
            if (_fillLen + need > MaxQueuedBytes)
            {
                // The peer has stopped reading. Dropping just this message would break the protocol stream
                // (a lost key-up sticks a key in the guest), so the channel ends instead.
                FaultLocked($"{_fillLen} bytes unsent; the server stopped reading");
                return;
            }
            if (_fill.Length - _fillLen < need) Array.Resize(ref _fill, Math.Max(_fillLen + need, _fill.Length * 2));
            int h = DataHeader.Write(_fill.AsSpan(_fillLen), MiniHeader, _serial++, type, body.Length);
            body.CopyTo(_fill.AsSpan(_fillLen + h));
            _fillLen += h + body.Length;
            if (!_parked) return;
            _parked = false;
        }
        // Continuations run asynchronously, so the writer resumes on a pool thread, never inline on the caller.
        _wake.SetResult(true);
    }

    /// <summary>Writes queued frames for the channel's lifetime, parking on <see cref="_wake"/> while the queue is empty.</summary>
    async Task WriteLoopAsync()
    {
        try
        {
            while (true)
            {
                byte[] buf;
                int len;
                bool park;
                short token = 0;
                lock (_sendLock)
                {
                    if (_disposed != 0) return;
                    // Version is a wrapping short: any value, negative included, is a valid token.
                    park = _fillLen == 0;
                    if (park)
                    {
                        _wake.Reset();
                        _parked = true;
                        token = _wake.Version;
                    }
                    // Swap buffers: producers keep appending to the other one while this one is written.
                    buf = _fill; len = _fillLen;
                    if (!park) { _fill = _drain; _fillLen = 0; _drain = buf; }
                }
                if (park) { await new ValueTask(this, token); continue; }
                await _stream.WriteAsync(buf.AsMemory(0, len));
                await _stream.FlushAsync();
            }
        }
        catch (Exception e)
        {
            // Part of a frame may have been written: nothing sent after this could be parsed. End the channel.
            lock (_sendLock) FaultLocked($"send failed: {e.Message}");
        }
    }

    /// <summary>Lets a parked writer loop see that the channel ended. Caller holds _sendLock.</summary>
    void WakeWriterLocked()
    {
        if (!_parked) return;
        _parked = false;
        _wake.SetResult(true);
    }

    /// <summary>
    /// Ends the channel after an unrecoverable send problem: no further sends, and closing the stream makes the
    /// receive loop fail, which the session treats like any other lost channel. Caller holds _sendLock.
    /// </summary>
    void FaultLocked(string reason)
    {
        _fillLen = 0;
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        WakeWriterLocked();
        Log.Warn($"{Id}: {reason}; closing the channel");
        _ = Task.Run(async () => { try { await _stream.DisposeAsync(); } catch { } });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_sendLock) WakeWriterLocked();
        try { await _stream.DisposeAsync(); } catch { }
    }

    void IValueTaskSource.GetResult(short token) => _wake.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _wake.GetStatus(token);
    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _wake.OnCompleted(continuation, state, token, flags);
}
