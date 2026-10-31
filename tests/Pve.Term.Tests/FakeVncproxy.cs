using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Pve.Term.Tests;

/// <summary>
/// Test-only stand-in for pveproxy + <c>qm vncproxy</c> + QEMU's VNC server on loopback (docs/decisions/0043):
/// answers <c>POST .../vncproxy</c> (websocket=1), upgrades <c>GET .../vncwebsocket</c>, then speaks the server side
/// of RFB 3.8 as QEMU's ui/vnc.c does: security type 2 only, a 16-byte challenge, SecurityResult with QEMU's
/// "Authentication failed\0" reason, ClientInit, ServerInit. Afterwards every byte the client sends is recorded, and
/// a FramebufferUpdateRequest is answered with <see cref="Update"/>.
/// </summary>
sealed class FakeVncproxy : IAsyncDisposable
{
    public const string Token = "test@pve!bridge=0123-SECRET";
    public const string User = "test@pve!bridge";
    public const string Password = "Ab1!c`D@";
    public const string Ticket = Password + ":PVEVNC:6720A1B2::FAKE+ticket/with=chars";
    public const int VncPort = 5901;
    public const ushort Width = 1024, Height = 768;
    public const string Name = "QEMU (fake-vm)";
    /// <summary>A FramebufferUpdate with one 1x1 raw rectangle.</summary>
    public static readonly byte[] Update = [0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 0x11, 0x22, 0x33, 0x00];

    readonly TcpListener _listener;
    readonly TestPki _pki;
    readonly CancellationTokenSource _stop = new();
    readonly Task _accept;

    public int Port { get; }
    public BlockingCollection<string> Events { get; } = [];
    /// <summary>Bytes the "QEMU" received after ClientInit, in order.</summary>
    public MemoryStream Received { get; } = new();
    /// <summary>The VNC password QEMU checks; a different value makes authentication fail.</summary>
    public string ServerPassword { get; set; } = Password;
    /// <summary>Leave the password out of the vncproxy answer (older Proxmox): the ticket is then the password.</summary>
    public bool OmitPassword { get; set; }
    /// <summary>Replaces the whole server side of the RFB handshake (malformed-input tests).</summary>
    public Func<WebSocket, CancellationToken, Task>? Script { get; set; }
    /// <summary>The ClientInit shared flag QEMU got.</summary>
    public int SharedFlag = -1;
    public int WebSocketCount;
    public int UpdateRequests;
    public bool FragmentSeen;
    public int MaxFrame;

    public FakeVncproxy(TestPki pki)
    {
        _pki = pki;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync();
    }

    public Uri ApiUrl => new($"https://localhost:{Port}");

    public TermDescriptor Descriptor(string token = Token) => new()
    {
        ApiUrl = ApiUrl,
        Node = "testnode",
        Target = TermTarget.Qemu,
        Console = ConsoleKind.Vnc,
        Vmid = 100,
        ApiToken = token,
        CaPem = _pki.CaPem,
        Title = "fake vm",
    };

    public byte[] ReceivedBytes() { lock (Received) return Received.ToArray(); }

    public string Expect(string prefix, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0 || !Events.TryTake(out var e, (int)left)) throw new TimeoutException($"no '{prefix}' event");
            if (e.StartsWith(prefix, StringComparison.Ordinal)) return e;
        }
    }

    async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            await using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _pki.Server }, _stop.Token);
            var (method, target, headers) = await ReadHeadAsync(ssl);
            bool authed = headers.TryGetValue("authorization", out var auth) && auth == "PVEAPIToken=" + Token;
            if (method == "POST" && target.EndsWith("/vncproxy", StringComparison.Ordinal))
            {
                int length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
                var body = new byte[length];
                await ssl.ReadExactlyAsync(body);
                Events.Add("post " + target + " body=" + Encoding.ASCII.GetString(body));
                if (!authed)
                {
                    await WriteAsync(ssl, "HTTP/1.1 403 Permission check failed (/vms/100, VM.Console)\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }
                string pw = OmitPassword ? "" : $"\"password\":\"{Password.Replace("\\", "\\\\")}\",";
                string json = $"{{\"data\":{{\"cert\":\"-----BEGIN CERTIFICATE-----\",{pw}\"port\":\"{VncPort}\",\"ticket\":\"{Ticket}\",\"upid\":\"UPID:testnode:1:2:3:vncproxy:100:{User}:\",\"user\":\"{User}\"}}}}";
                await WriteAsync(ssl, $"HTTP/1.1 200 OK\r\nContent-Type: application/json;charset=UTF-8\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n{json}");
                return;
            }
            if (method == "GET" && target.Contains("/vncwebsocket?", StringComparison.Ordinal))
            {
                var query = target[(target.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                if (!authed || query.GetValueOrDefault("vncticket") != Ticket || query.GetValueOrDefault("port") != VncPort.ToString())
                {
                    await WriteAsync(ssl, "HTTP/1.1 401 permission denied - invalid vnc ticket\r\nContent-Length: 0\r\n\r\n");
                    return;
                }
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                Events.Add("ws " + target[..target.IndexOf('?')] + " protocol=" + headers.GetValueOrDefault("sec-websocket-protocol"));
                await WriteAsync(ssl, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: upgrade\r\nSec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: binary\r\n\r\n");
                Interlocked.Increment(ref WebSocketCount);
                using var ws = WebSocket.CreateFromStream(ssl, new WebSocketCreationOptions { IsServer = true, SubProtocol = "binary", KeepAliveInterval = TimeSpan.Zero });
                if (Script is { } script) await script(ws, _stop.Token);
                else await QemuAsync(ws);
                return;
            }
            await WriteAsync(ssl, "HTTP/1.1 501 not implemented\r\nContent-Length: 0\r\n\r\n");
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException
            or System.Security.Authentication.AuthenticationException or EndOfStreamException)
        {
            Events.Add("conn-error " + e.GetType().Name);
        }
    }

    /// <summary>A byte-stream view of the WebSocket for the fake server; records frame shapes.</summary>
    public sealed class WsReader(FakeVncproxy? owner, WebSocket ws, CancellationToken ct)
    {
        readonly byte[] _buf = new byte[256 * 1024];
        int _start, _end;

        public async Task ReadExactAsync(Memory<byte> dst)
        {
            while (!dst.IsEmpty)
            {
                if (_start == _end)
                {
                    var r = await ws.ReceiveAsync(_buf.AsMemory(), ct);
                    if (r.MessageType == WebSocketMessageType.Close) throw new EndOfStreamException("client closed");
                    if (!r.EndOfMessage && owner is not null) owner.FragmentSeen = true;
                    if (owner is not null) owner.MaxFrame = Math.Max(owner.MaxFrame, r.Count);
                    _start = 0; _end = r.Count;
                    continue;
                }
                int n = Math.Min(dst.Length, _end - _start);
                _buf.AsMemory(_start, n).CopyTo(dst);
                _start += n;
                dst = dst[n..];
            }
        }

        public Task SendAsync(byte[] data) => ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
    }

    async Task QemuAsync(WebSocket ws)
    {
        var io = new WsReader(this, ws, _stop.Token);
        await io.SendAsync("RFB 003.008\n"u8.ToArray());
        var version = new byte[12];
        await io.ReadExactAsync(version);
        Events.Add("version " + Encoding.ASCII.GetString(version).TrimEnd('\n'));
        await io.SendAsync([1, 2]); // QEMU: count 1, VNC authentication
        var choice = new byte[1];
        await io.ReadExactAsync(choice);
        if (choice[0] != 2) { Events.Add("bad-choice"); return; }
        var challenge = RandomNumberGenerator.GetBytes(16);
        await io.SendAsync(challenge);
        var response = new byte[16];
        await io.ReadExactAsync(response);
        if (!response.AsSpan().SequenceEqual(ExpectedResponse(challenge, ServerPassword)))
        {
            Events.Add("auth-failed");
            // ui/vnc.c: SecurityResult 1, then (3.8) u32 sizeof("Authentication failed") = 22 and the text with its NUL.
            await io.SendAsync([0, 0, 0, 1, 0, 0, 0, 22, .. "Authentication failed\0"u8]);
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, _stop.Token);
            return;
        }
        Events.Add("auth-ok");
        await io.SendAsync([0, 0, 0, 0]);
        var init = new byte[1];
        await io.ReadExactAsync(init);
        SharedFlag = init[0];
        Events.Add("client-init " + init[0]);
        await io.SendAsync(ServerInit(Width, Height, Name));
        var one = new byte[1];
        while (true)
        {
            await io.ReadExactAsync(one);
            lock (Received) Received.WriteByte(one[0]);
            if (one[0] == 3)
            {
                var rest = new byte[9];
                await io.ReadExactAsync(rest);
                lock (Received) Received.Write(rest);
                Interlocked.Increment(ref UpdateRequests);
                Events.Add("fbur " + rest[0]);
                await io.SendAsync(Update);
            }
        }
    }

    /// <summary>ServerInit as QEMU's protocol_client_init writes it: size, 32 bpp depth 24 true colour, shifts 16/8/0.</summary>
    public static byte[] ServerInit(ushort w, ushort h, string name)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var b = new byte[24 + n.Length];
        BinaryPrimitives.WriteUInt16BigEndian(b, w);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), h);
        b[4] = 32; b[5] = 24; b[6] = 0; b[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(12), 255);
        b[14] = 16; b[15] = 8; b[16] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)n.Length);
        n.CopyTo(b, 24);
        return b;
    }

    /// <summary>The server's side of VNC authentication, written apart from the bridge's (a table-free bit reversal,
    /// as QEMU's vnc_munge_des_rfb_key does it).</summary>
    public static byte[] ExpectedResponse(byte[] challenge, string password)
    {
        var key = new byte[8];
        for (int i = 0; i < 8 && i < password.Length; i++)
        {
            int c = password[i], r = 0;
            for (int bit = 0; bit < 8; bit++) if ((c & (1 << bit)) != 0) r |= 0x80 >> bit;
            key[i] = (byte)r;
        }
        using var des = DES.Create();
        des.Key = key;
        return des.EncryptEcb(challenge, PaddingMode.None);
    }

    static async Task<(string Method, string Target, Dictionary<string, string> Headers)> ReadHeadAsync(Stream s)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 4 || !(head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await s.ReadAsync(one) == 0) throw new IOException("eof in request head");
            head.Add(one[0]);
            if (head.Count > 16384) throw new IOException("request head too large");
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var parts = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            int c = l.IndexOf(':');
            if (c > 0) headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim();
        }
        return (parts[0], parts[1], headers);
    }

    static Task WriteAsync(Stream s, string text) => s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _accept; } catch { }
        _stop.Dispose();
    }
}

/// <summary>A minimal RFB viewer for tests: speaks the client side of the handshake the bridge presents.</summary>
sealed class FakeViewer : IDisposable
{
    public TcpClient Tcp { get; } = new();
    public NetworkStream Stream => Tcp.GetStream();

    public static async Task<FakeViewer> ConnectAsync(int port)
    {
        var v = new FakeViewer();
        await v.Tcp.ConnectAsync(IPAddress.Loopback, port);
        v.Tcp.NoDelay = true;
        return v;
    }

    /// <summary>Does the 3.x handshake with security None; returns the ServerInit bytes.</summary>
    public async Task<byte[]> HandshakeAsync(int minor = 8, bool shared = false)
    {
        var s = Stream;
        var version = new byte[12];
        await s.ReadExactlyAsync(version);
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(version));
        await s.WriteAsync(Encoding.ASCII.GetBytes($"RFB 003.{minor:000}\n"));
        if (minor == 3)
        {
            var type = new byte[4];
            await s.ReadExactlyAsync(type);
            if (BinaryPrimitives.ReadUInt32BigEndian(type) == 0) throw new IOException("refused: " + await ReasonAsync());
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(type));
        }
        else
        {
            var types = new byte[2];
            await s.ReadExactlyAsync(types);
            Assert.Equal(new byte[] { 1, 1 }, types);
            await s.WriteAsync(new byte[] { 1 });
            if (minor == 8)
            {
                var result = new byte[4];
                await s.ReadExactlyAsync(result);
                if (BinaryPrimitives.ReadUInt32BigEndian(result) != 0) throw new IOException("refused: " + await ReasonAsync());
            }
        }
        await s.WriteAsync(new byte[] { shared ? (byte)1 : (byte)0 });
        var head = new byte[24];
        await s.ReadExactlyAsync(head);
        var name = new byte[BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(20))];
        await s.ReadExactlyAsync(name);
        return [.. head, .. name];
    }

    async Task<string> ReasonAsync()
    {
        var len = new byte[4];
        await Stream.ReadExactlyAsync(len);
        var text = new byte[BinaryPrimitives.ReadUInt32BigEndian(len)];
        await Stream.ReadExactlyAsync(text);
        return Encoding.ASCII.GetString(text);
    }

    public void Dispose() => Tcp.Dispose();
}
