using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json;

namespace Pve.Term;

/// <summary>Why a termproxy console could not be opened; the message never contains a secret.</summary>
public sealed class TermConnectException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A termproxy session as <c>POST .../termproxy</c> returns it (qemu-server API2/Qemu.pm, pve-manager API2/Nodes.pm).</summary>
public sealed record TermproxySession(string User, string Ticket, int Port)
{
    /// <summary>Leaves the ticket out, so logging the session can't leak it.</summary>
    public override string ToString() => $"TermproxySession {{ User = {User}, Port = {Port} }}";
}

/// <summary>
/// Opens a termproxy console: requests the session unless the descriptor carries one, connects the WebSocket
/// (subprotocol <c>binary</c>, the API credentials on the upgrade request), and completes the ticket handshake.
/// </summary>
public static class TermproxyConnector
{
    /// <summary>termproxy waits 10 s for the connection and 10 s for the ticket line (main.rs); one budget covers all.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public static async Task<TermproxyChannel> ConnectAsync(TermDescriptor d, TimeSpan keepAlive, CancellationToken ct)
    {
        var tls = new PveTls(d.CaPem);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        TermproxySession session;
        try
        {
            session = d.HasSession ? new TermproxySession(d.TermUser!, d.TermTicket!, d.TermPort!.Value)
                : await RequestSessionAsync(d, tls, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TermConnectException("timed out opening the console.");
        }

        var ws = new ClientWebSocket();
        try
        {
            ws.Options.AddSubProtocol("binary");
            // The default 30 s unsolicited pong would wake the CPU while idle; the termproxy ping covers keep-alive.
            ws.Options.KeepAliveInterval = TimeSpan.Zero;
            ws.Options.RemoteCertificateValidationCallback = tls.Validate;
            ws.Options.SetRequestHeader("User-Agent", "PveTerm");
            AddAuth(d, (name, value) => ws.Options.SetRequestHeader(name, value), includeCsrf: false);
            try { await ws.ConnectAsync(d.WebSocketUri(session.Port, session.Ticket), timeout.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or AuthenticationException)
            {
                throw new TermConnectException(Describe("could not open the console WebSocket", e, tls), e);
            }
            return await TermproxyChannel.HandshakeAsync(ws, session.User, session.Ticket, keepAlive, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new TermConnectException("timed out opening the console.");
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    /// <summary><c>POST /api2/json/nodes/{node}[/qemu|/lxc/{vmid}]/termproxy</c>.</summary>
    public static async Task<TermproxySession> RequestSessionAsync(TermDescriptor d, PveTls tls, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            SslOptions = { RemoteCertificateValidationCallback = tls.Validate },
        };
        using var http = new HttpClient(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, d.TermproxyUri);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("PveTerm", null));
        AddAuth(d, (name, value) => req.Headers.TryAddWithoutValidation(name, value), includeCsrf: true);
        var form = new List<KeyValuePair<string, string>>();
        if (d.Serial is not null) form.Add(new("serial", d.Serial));
        req.Content = new FormUrlEncodedContent(form);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw new TermConnectException(Describe("could not reach the PVE API", e, tls), e); }
        using (res)
        {
            // pveproxy puts its error text in the reason phrase, e.g. "403 Permission check failed (/vms/100, VM.Console)".
            if (res.StatusCode != HttpStatusCode.OK)
                throw new TermConnectException($"the PVE API refused the console: {(int)res.StatusCode} {res.ReasonPhrase}");
            var body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return ParseSession(body);
        }
    }

    /// <summary>Reads <c>{"data":{"user":..,"ticket":..,"port":..}}</c>; Perl's JSON may give the port as a string.</summary>
    internal static TermproxySession ParseSession(ReadOnlySpan<byte> json)
    {
        string? user = null, ticket = null;
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
                        else if (name == "port" && r.TokenType == JsonTokenType.Number) port = r.GetInt32();
                        else if (name == "port" && r.TokenType == JsonTokenType.String) int.TryParse(r.GetString(), out port);
                        else r.Skip();
                        break;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new TermConnectException("the PVE API's termproxy answer is not valid JSON.");
        }
        if (user is null || ticket is null || port is not (>= 5900 and <= 5999))
            throw new TermConnectException("the PVE API's termproxy answer lacks user, ticket or port.");
        return new TermproxySession(user, ticket, port);
    }

    static void AddAuth(TermDescriptor d, Action<string, string> add, bool includeCsrf)
    {
        if (d.ApiToken is not null) add("Authorization", "PVEAPIToken=" + d.ApiToken);
        else
        {
            add("Cookie", "PVEAuthCookie=" + d.AuthCookie);
            if (includeCsrf && d.CsrfToken is not null) add("CSRFPreventionToken", d.CsrfToken);
        }
    }

    static string Describe(string what, Exception e, PveTls tls)
    {
        if (tls.Failure is { } f) return $"{what}: TLS: {f}.";
        for (var x = e; x is not null; x = x.InnerException)
            if (x is AuthenticationException) return $"{what}: TLS handshake failed ({x.Message}).";
        return $"{what}: {e.Message}";
    }
}
