using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pve.Term;

/// <summary>A <c>vncproxy</c> session as qemu-server's API2/Qemu.pm returns it. <see cref="Password"/> is the
/// 8-character VNC password Proxmox generates; older servers return none, and then the ticket is the password (its
/// first 8 characters are the same password, see docs/decisions/0043).</summary>
public sealed record VncproxySession(string User, string Ticket, int Port, string? Password)
{
    public string VncPassword => Password ?? Ticket;

    /// <summary>Leaves the ticket and password out, so logging the session can't leak them.</summary>
    public override string ToString() => $"VncproxySession {{ User = {User}, Port = {Port} }}";
}

/// <summary>
/// The bridge's upstream: Proxmox's <c>vncwebsocket</c> carrying QEMU's RFB stream (docs/decisions/0043). It runs the
/// security handshake itself with the session's password, then ClientInit and ServerInit; afterwards the WebSocket is
/// relayed byte for byte. Bytes that arrived after ServerInit stay in <see cref="Pending"/> for the relay.
/// </summary>
public sealed class VncUpstream : IAsyncDisposable
{
    /// <summary>pveproxy refuses client frames over 128 KiB and sends at most 128 KiB per frame (AnyEvent.pm).</summary>
    public const int BufferSize = 128 * 1024;

    readonly WebSocket _ws;
    readonly byte[] _buffer = new byte[BufferSize];
    int _start, _end;

    internal VncUpstream(WebSocket ws) => _ws = ws;

    public WebSocket WebSocket => _ws;
    /// <summary>The RFB minor version agreed with the server (QEMU: 8).</summary>
    public int Minor { get; private set; }
    /// <summary>Bytes received after ServerInit that the relay must pass on first.</summary>
    public ReadOnlyMemory<byte> Pending => _buffer.AsMemory(_start, _end - _start);

    /// <summary>Speaks RFB up to a successful SecurityResult: version, security type (VNC authentication with
    /// <paramref name="password"/>, or None if that is all the server offers) and the result.</summary>
    public static async Task<VncUpstream> HandshakeAsync(WebSocket ws, string password, CancellationToken ct)
    {
        var up = new VncUpstream(ws);
        await up.SecurityAsync(password, ct).ConfigureAwait(false);
        return up;
    }

    async Task SecurityAsync(string password, CancellationToken ct)
    {
        var version = new byte[RfbHandshake.VersionLength];
        await ReadExactAsync(version, ct).ConfigureAwait(false);
        Minor = RfbHandshake.ParseVersion(version);
        await SendAsync(RfbHandshake.VersionBytes(Minor), ct).ConfigureAwait(false);

        byte type;
        var four = new byte[4];
        if (Minor == 3)
        {
            await ReadExactAsync(four, ct).ConfigureAwait(false);
            uint t = BinaryPrimitives.ReadUInt32BigEndian(four);
            if (t == RfbHandshake.SecurityInvalid) throw new RfbHandshakeException("the VNC server refused the connection: " + await ReadReasonAsync(ct).ConfigureAwait(false));
            if (t is not (RfbHandshake.SecurityNone or RfbHandshake.SecurityVncAuth)) throw new RfbHandshakeException($"the VNC server wants security type {t}, which the bridge does not speak.");
            type = (byte)t;
        }
        else
        {
            var count = new byte[1];
            await ReadExactAsync(count, ct).ConfigureAwait(false);
            if (count[0] == 0) throw new RfbHandshakeException("the VNC server refused the connection: " + await ReadReasonAsync(ct).ConfigureAwait(false));
            var types = new byte[count[0]];
            await ReadExactAsync(types, ct).ConfigureAwait(false);
            // QEMU under Proxmox offers exactly VNC authentication (password=on, no TLS credentials).
            if (types.AsSpan().Contains(RfbHandshake.SecurityVncAuth)) type = RfbHandshake.SecurityVncAuth;
            else if (types.AsSpan().Contains(RfbHandshake.SecurityNone)) type = RfbHandshake.SecurityNone;
            else throw new RfbHandshakeException($"the VNC server offers security types {string.Join(", ", types)}, none of which the bridge speaks.");
            await SendAsync([type], ct).ConfigureAwait(false);
        }

        if (type == RfbHandshake.SecurityVncAuth)
        {
            var challenge = new byte[RfbHandshake.ChallengeLength];
            await ReadExactAsync(challenge, ct).ConfigureAwait(false);
            var response = new byte[RfbHandshake.ChallengeLength];
            RfbHandshake.VncAuthResponse(challenge, password, response);
            await SendAsync(response, ct).ConfigureAwait(false);
        }
        // RFC 6143 7.1.3: 3.8 always sends a SecurityResult; 3.3 and 3.7 only after authentication.
        if (type == RfbHandshake.SecurityNone && Minor < 8) return;
        await ReadExactAsync(four, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(four) == 0) return;
        string why = Minor >= 8 ? await ReadReasonAsync(ct).ConfigureAwait(false) : "authentication failed";
        throw new RfbHandshakeException($"the VNC server refused the console password ({why}). The console session expired or was replaced; open the console again.");
    }

    /// <summary>Sends ClientInit and returns the whole ServerInit (fixed part and name), checked by
    /// <see cref="RfbHandshake.CheckServerInit"/>.</summary>
    public async Task<byte[]> InitAsync(bool shared, CancellationToken ct)
    {
        await SendAsync([shared ? (byte)1 : (byte)0], ct).ConfigureAwait(false);
        var head = new byte[RfbHandshake.ServerInitFixed];
        await ReadExactAsync(head, ct).ConfigureAwait(false);
        int nameLength = RfbHandshake.CheckServerInit(head);
        var all = new byte[RfbHandshake.ServerInitFixed + nameLength];
        head.CopyTo(all, 0);
        await ReadExactAsync(all.AsMemory(RfbHandshake.ServerInitFixed), ct).ConfigureAwait(false);
        return all;
    }

    async Task<string> ReadReasonAsync(CancellationToken ct)
    {
        var four = new byte[4];
        await ReadExactAsync(four, ct).ConfigureAwait(false);
        uint n = BinaryPrimitives.ReadUInt32BigEndian(four);
        if (n > RfbHandshake.MaxText) throw new RfbHandshakeException("the VNC server sent an over-long failure reason.");
        var text = new byte[n];
        await ReadExactAsync(text, ct).ConfigureAwait(false);
        return RfbHandshake.ReasonText(text);
    }

    Task SendAsync(byte[] data, CancellationToken ct) => Translate(_ws.SendAsync(data.AsMemory(), WebSocketMessageType.Binary, true, ct).AsTask());

    async Task ReadExactAsync(Memory<byte> destination, CancellationToken ct)
    {
        while (!destination.IsEmpty)
        {
            if (_start == _end)
            {
                _start = _end = 0;
                ValueWebSocketReceiveResult r;
                try { r = await _ws.ReceiveAsync(_buffer.AsMemory(), ct).ConfigureAwait(false); }
                catch (Exception e) when (e is WebSocketException or IOException)
                {
                    throw new RfbHandshakeException("the VNC connection closed during the handshake (" + e.Message + ").");
                }
                if (r.MessageType == WebSocketMessageType.Close)
                    throw new RfbHandshakeException("the VNC server closed the connection during the handshake.");
                _end = r.Count;
                continue;
            }
            int n = Math.Min(destination.Length, _end - _start);
            _buffer.AsMemory(_start, n).CopyTo(destination);
            _start += n;
            destination = destination[n..];
        }
    }

    static async Task Translate(Task send)
    {
        try { await send.ConfigureAwait(false); }
        catch (Exception e) when (e is WebSocketException or IOException)
        {
            throw new RfbHandshakeException("the VNC connection closed during the handshake (" + e.Message + ").");
        }
    }

    int _disposed;

    /// <summary>Closes the WebSocket (a normal close when it is still open). Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, t.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        _ws.Dispose();
    }
}

/// <summary>
/// Opens a VM's VNC display through Proxmox: <c>POST .../vncproxy</c> with <c>websocket=1</c>, the
/// <c>vncwebsocket</c> upgrade (subprotocol <c>binary</c>, API credentials on the request, the same TLS policy as
/// termproxy), then the RFB security handshake (docs/decisions/0043).
/// </summary>
public static class VncproxyConnector
{
    /// <summary>vncproxy's worker waits 10 s for the connection, QEMU's password expires 30 s after it is set, and
    /// pveproxy accepts the ticket for 40 s (decision 0043); one 20 s budget covers request, upgrade and handshake.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public static async Task<VncUpstream> ConnectAsync(TermDescriptor d, CancellationToken ct)
    {
        if (d.Console != ConsoleKind.Vnc) throw new ArgumentException("not a VNC descriptor", nameof(d));
        var tls = new PveTls(d.CaPem);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        var ws = new ClientWebSocket();
        try
        {
            var session = await RequestSessionAsync(d, tls, timeout.Token).ConfigureAwait(false);
            ws.Options.AddSubProtocol("binary");
            // No unsolicited pongs: they would wake the CPU while idle (pveproxy answers pings and ignores pongs).
            ws.Options.KeepAliveInterval = TimeSpan.Zero;
            ws.Options.RemoteCertificateValidationCallback = tls.Validate;
            ws.Options.SetRequestHeader("User-Agent", "PveTerm");
            TermproxyConnector.AddAuth(d, (name, value) => ws.Options.SetRequestHeader(name, value), includeCsrf: false);
            try { await ws.ConnectAsync(d.WebSocketUri(session.Port, session.Ticket), timeout.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or AuthenticationException)
            {
                throw new TermConnectException(TermproxyConnector.Describe("could not open the VNC WebSocket", e, tls), e);
            }
            try { return await VncUpstream.HandshakeAsync(ws, session.VncPassword, timeout.Token).ConfigureAwait(false); }
            catch (RfbHandshakeException e) { throw new TermConnectException(e.Message, e); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new TermConnectException("timed out opening the VNC console.");
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary><c>POST /api2/json/nodes/{node}/qemu/{vmid}/vncproxy</c> with <c>websocket=1</c>.</summary>
    public static async Task<VncproxySession> RequestSessionAsync(TermDescriptor d, PveTls tls, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = tls.Validate },
        };
        using var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, d.VncproxyUri);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PveTerm", null));
        TermproxyConnector.AddAuth(d, (name, value) => req.Headers.TryAddWithoutValidation(name, value), includeCsrf: true);
        req.Content = new FormUrlEncodedContent([new("websocket", "1")]);
        HttpResponseMessage res;
        // Headers only: the body is read under TermproxyConnector.SessionBodyLimit, never buffered whole first.
        try { res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw new TermConnectException(TermproxyConnector.Describe("could not reach the PVE API", e, tls), e); }
        using (res)
        {
            if (res.StatusCode != HttpStatusCode.OK)
                throw new TermConnectException($"the PVE API refused the VNC console: {(int)res.StatusCode} {res.ReasonPhrase}");
            var body = await TermproxyConnector.ReadSessionBodyAsync(res, "vncproxy", ct).ConfigureAwait(false);
            try { return ParseSession(body); }
            finally { CryptographicOperations.ZeroMemory(body); }
        }
    }

    /// <summary>Reads <c>{"data":{"user","ticket","port","password",...}}</c>; Perl's JSON may give the port as a
    /// string. The password, when present, must be the 8 printable ASCII characters Proxmox generates.</summary>
    internal static VncproxySession ParseSession(ReadOnlySpan<byte> json)
    {
        string? user = null, ticket = null, password = null;
        int port = 0;
        try
        {
            var r = new Utf8JsonReader(json);
            int depth = 0;
            bool inData = false;
            while (r.Read())
            {
                switch (r.TokenType)
                {
                    case JsonTokenType.StartObject: depth++; break;
                    case JsonTokenType.EndObject: depth--; if (depth < 2) inData = false; break;
                    case JsonTokenType.PropertyName:
                        string name = r.GetString()!;
                        r.Read();
                        if (depth == 1 && name == "data" && r.TokenType == JsonTokenType.StartObject) { depth++; inData = true; break; }
                        if (!inData || depth != 2) { r.Skip(); break; }
                        if (name == "user" && r.TokenType == JsonTokenType.String) user = r.GetString();
                        else if (name == "ticket" && r.TokenType == JsonTokenType.String) ticket = r.GetString();
                        else if (name == "password" && r.TokenType == JsonTokenType.String) password = r.GetString();
                        else if (name == "port" && r.TokenType == JsonTokenType.Number) port = r.GetInt32();
                        else if (name == "port" && r.TokenType == JsonTokenType.String) int.TryParse(r.GetString(), out port);
                        else r.Skip();
                        break;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new TermConnectException("the PVE API's vncproxy answer is not valid JSON.");
        }
        if (user is null || ticket is null || ticket.Length is 0 or > 512 || port is not (>= 5900 and <= 5999))
            throw new TermConnectException("the PVE API's vncproxy answer lacks user, ticket or port.");
        if (password is not null && (password.Length != 8 || password.Any(c => c is < '!' or > '~')))
            throw new TermConnectException("the PVE API's vncproxy answer has a malformed password.");
        if (password is null && ticket.Any(c => c > 0x7F))
            throw new TermConnectException("the PVE API's vncproxy ticket is not ASCII.");
        return new VncproxySession(user, ticket, port, password);
    }
}
