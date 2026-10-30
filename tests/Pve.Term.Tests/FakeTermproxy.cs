using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Pve.Term.Tests;

/// <summary>
/// Test-only stand-in for pveproxy + termproxy on loopback: answers <c>POST .../termproxy</c>, upgrades
/// <c>GET .../vncwebsocket</c> (subprotocol binary), checks the <c>user:ticket</c> line, answers <c>OK</c>, then
/// parses client messages exactly like termproxy's main.rs and echoes input back as terminal output. Typing
/// <c>exit</c> + Return drops the connection without a close frame, as pveproxy does when termproxy exits.
/// </summary>
sealed class FakeTermproxy : IAsyncDisposable
{
    public const string Token = "test@pve!bridge=0123-SECRET";
    public const string User = "test@pve!bridge";
    public const string Ticket = "PVEVNC:6720A1B2::FAKE+ticket/with=chars";
    public const int TermPort = 5901;
    public static readonly byte[] Banner = "fake-login: "u8.ToArray();

    readonly TcpListener _listener;
    readonly TestPki _pki;
    readonly CancellationTokenSource _stop = new();
    readonly Task _accept;

    public int Port { get; }
    public BlockingCollection<string> Events { get; } = [];
    public ConcurrentQueue<string> Requests { get; } = [];
    /// <summary>Bytes the "pty" received, in order.</summary>
    public MemoryStream Input { get; } = new();
    public bool SplitOk { get; set; }
    public bool RefuseApi { get; set; }
    public bool PortAsString { get; set; }
    /// <summary>Echo each input byte in its own frame, so the client sees UTF-8 split across reads.</summary>
    public bool EchoBytewise { get; set; }
    public bool FragmentSeen { get; private set; }
    public int WebSocketCount;

    public FakeTermproxy(TestPki pki)
    {
        _pki = pki;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync();
    }

    public Uri ApiUrl => new($"https://localhost:{Port}");

    public TermDescriptor Descriptor(TermTarget target = TermTarget.Qemu, string? caPem = "", bool withSession = false, string token = Token) => new()
    {
        ApiUrl = ApiUrl,
        Node = "testnode",
        Target = target,
        Vmid = target == TermTarget.Node ? null : 100,
        Serial = target == TermTarget.Qemu ? "serial0" : null,
        ApiToken = token,
        CaPem = caPem == "" ? _pki.CaPem : caPem,
        Title = "fake console",
        TermUser = withSession ? User : null,
        TermTicket = withSession ? Ticket : null,
        TermPort = withSession ? TermPort : null,
    };

    /// <summary>Waits for an event that starts with <paramref name="prefix"/>, skipping others.</summary>
    public string Expect(string prefix, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0 || !Events.TryTake(out var e, (int)left))
                throw new TimeoutException($"no '{prefix}' event; input so far: {Escape(InputBytes())}");
            if (e.StartsWith(prefix, StringComparison.Ordinal)) return e;
        }
    }

    public byte[] InputBytes() { lock (Input) return Input.ToArray(); }

    /// <summary>Waits until the pty input since <paramref name="mark"/> contains <paramref name="expected"/> (input
    /// may arrive split over several messages); moves the mark past it.</summary>
    public void ExpectInput(byte[] expected, ref int mark, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var have = InputBytes();
            int i = have.AsSpan(Math.Min(mark, have.Length)).IndexOf(expected);
            if (i >= 0) { mark += i + expected.Length; return; }
            long left = deadline - Environment.TickCount64;
            if (left <= 0) throw new TimeoutException($"pty input lacks {Escape(expected)}; got {Escape(have)}");
            Expect("data", (int)left);
        }
    }

    public static string Escape(byte[] b) => string.Concat(b.Select(x => x is >= 0x20 and < 0x7F ? ((char)x).ToString() : $"\\x{x:x2}"));

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
            Requests.Enqueue($"{method} {target}");
            bool authed = headers.TryGetValue("authorization", out var auth) && auth == "PVEAPIToken=" + Token;
            if (method == "POST" && target.EndsWith("/termproxy", StringComparison.Ordinal))
            {
                int length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
                var body = new byte[length];
                await ssl.ReadExactlyAsync(body);
                Events.Add("post " + target + " body=" + Encoding.ASCII.GetString(body));
                if (!authed || RefuseApi)
                {
                    await WriteAsync(ssl, "HTTP/1.1 403 Permission check failed (/vms/100, VM.Console)\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }
                string port = PortAsString ? $"\"{TermPort}\"" : TermPort.ToString();
                string json = $"{{\"data\":{{\"upid\":\"UPID:testnode:1:2:3:vncproxy:100:{User}:\",\"port\":{port},\"ticket\":\"{Ticket}\",\"user\":\"{User}\"}}}}";
                await WriteAsync(ssl, $"HTTP/1.1 200 OK\r\nContent-Type: application/json;charset=UTF-8\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n{json}");
                return;
            }
            if (method == "GET" && target.Contains("/vncwebsocket?", StringComparison.Ordinal))
            {
                var query = target[(target.IndexOf('?') + 1)..].Split('&').Select(p => p.Split('=', 2))
                    .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                if (!authed || query.GetValueOrDefault("vncticket") != Ticket || query.GetValueOrDefault("port") != TermPort.ToString())
                {
                    await WriteAsync(ssl, "HTTP/1.1 401 permission denied - invalid vnc ticket\r\nContent-Length: 0\r\n\r\n");
                    return;
                }
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                Events.Add("ws protocol=" + headers.GetValueOrDefault("sec-websocket-protocol"));
                await WriteAsync(ssl, $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: upgrade\r\nSec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: binary\r\n\r\n");
                Interlocked.Increment(ref WebSocketCount);
                using var ws = WebSocket.CreateFromStream(ssl, new WebSocketCreationOptions { IsServer = true, SubProtocol = "binary", KeepAliveInterval = TimeSpan.Zero });
                await TermproxyAsync(ws);
                return;
            }
            await WriteAsync(ssl, "HTTP/1.1 501 not implemented\r\nContent-Length: 0\r\n\r\n");
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException
            or System.Security.Authentication.AuthenticationException)
        {
            Events.Add("conn-error " + e.GetType().Name);
        }
    }

    async Task TermproxyAsync(WebSocket ws)
    {
        var queue = new List<byte>();
        var buf = new byte[256 * 1024];
        bool authenticated = false;
        int remaining = 0;
        var line = new StringBuilder();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, _stop.Token);
            if (r.MessageType == WebSocketMessageType.Close) { Events.Add("ws-close"); return; }
            if (!r.EndOfMessage) FragmentSeen = true;
            queue.AddRange(buf.AsSpan(0, r.Count));
            if (!authenticated)
            {
                int nl = queue.IndexOf((byte)'\n');
                if (nl < 0) continue;
                string first = Encoding.UTF8.GetString(queue.GetRange(0, nl).ToArray());
                queue.RemoveRange(0, nl + 1);
                Events.Add("auth " + first);
                if (first != User + ":" + Ticket) return; // termproxy exits: the connection just drops
                authenticated = true;
                if (SplitOk)
                {
                    await ws.SendAsync("O"u8.ToArray(), WebSocketMessageType.Binary, true, _stop.Token);
                    await ws.SendAsync("K"u8.ToArray().Concat(Banner).ToArray(), WebSocketMessageType.Binary, true, _stop.Token);
                }
                else await ws.SendAsync("OK"u8.ToArray().Concat(Banner).ToArray(), WebSocketMessageType.Binary, true, _stop.Token);
            }
            // termproxy main.rs: process_queue / remove_number, including messages split across frames.
            while (queue.Count > 0)
            {
                if (remaining > 0)
                {
                    int n = Math.Min(remaining, queue.Count);
                    var data = queue.GetRange(0, n).ToArray();
                    queue.RemoveRange(0, n);
                    remaining -= n;
                    lock (Input) Input.Write(data);
                    Events.Add("data " + Escape(data));
                    if (EchoBytewise)
                        foreach (byte b in data) await ws.SendAsync(new[] { b }, WebSocketMessageType.Binary, true, _stop.Token);
                    else await ws.SendAsync(data, WebSocketMessageType.Binary, true, _stop.Token); // the pty echoes
                    foreach (byte b in data)
                    {
                        if (b == '\r')
                        {
                            if (line.ToString().EndsWith("exit", StringComparison.Ordinal)) { Events.Add("exit"); return; } // drop without a close frame
                            line.Clear();
                        }
                        else line.Append((char)b);
                    }
                    continue;
                }
                if (queue.Count < 2) break;
                byte type = (byte)(queue[0] - '0');
                if (type == 0)
                {
                    if (!TryNumber(queue, 2, out int len, out int used)) break;
                    queue.RemoveRange(0, 2 + used);
                    remaining = len;
                    if (len == 0) Events.Add("data ");
                }
                else if (type == 1)
                {
                    if (!TryNumber(queue, 2, out int cols, out int u1) || !TryNumber(queue, 2 + u1, out int rows, out int u2)) break;
                    queue.RemoveRange(0, 2 + u1 + u2);
                    Events.Add($"resize {cols}x{rows}");
                    await ws.SendAsync(Encoding.ASCII.GetBytes($"[resize {cols}x{rows}]"), WebSocketMessageType.Binary, true, _stop.Token);
                }
                else
                {
                    if (queue[0] == '2') Events.Add("ping");
                    queue.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>Digits up to ':' starting at <paramref name="start"/>; <paramref name="used"/> includes the ':'.</summary>
    static bool TryNumber(List<byte> q, int start, out int value, out int used)
    {
        value = used = 0;
        int colon = q.IndexOf((byte)':', start);
        if (colon < 0) return false;
        value = int.Parse(Encoding.ASCII.GetString(q.GetRange(start, colon - start).ToArray()));
        used = colon - start + 1;
        return true;
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
