using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using Pve.Term;

namespace Pve.Api;

/// <summary>The outcome of the first sign-in step.</summary>
public enum SignInStep { Done, NeedsSecondFactor }

/// <summary>
/// A client for one Proxmox VE API endpoint (docs/decisions/0038, 0039). It signs in with an API token, or with a
/// user name and password (<c>POST /access/ticket</c>, then the second factor if the account has one), keeps the
/// ticket and CSRF token in memory only, and renews the ticket lazily: a request made when the ticket is older than
/// <see cref="RenewAfter"/> first trades it for a new one. Nothing runs between requests, so an idle launcher has no
/// timer. TLS follows docs/decisions/0033 through <see cref="PveTls"/>. No message, log line or
/// <see cref="ToString"/> contains a secret or ticket.
/// </summary>
public sealed class PveClient : IDisposable
{
    /// <summary>Proxmox tickets live 2 hours (pve-access-control AccessControl.pm <c>$ticket_lifetime</c>).</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(2);
    /// <summary>A ticket this old is renewed before the next request (the web UI renews every 15 minutes).</summary>
    public static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(15);
    /// <summary>A ticket this close to its end is treated as expired, leaving room for clock skew and slow requests.</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);
    public const int DefaultPort = 8006;
    /// <summary>One deadline for a whole request: connect, headers and body (review L3).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    /// <summary>A successful answer larger than this is refused while it arrives (review L4).</summary>
    public const int MaxResponseBytes = 16 << 20;
    /// <summary>An error answer is only read for its message; the rest is dropped.</summary>
    const int MaxErrorBytes = 64 << 10;

    readonly SocketsHttpHandler _handler;
    readonly HttpClient _http;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _renewLock = new(1, 1);

    string? _apiToken;
    Ticket? _ticket;
    Ticket? _pending; // the first factor's half ticket while a second factor is asked for

    sealed record Ticket(string Username, string Value, string? Csrf, long IssuedAt, bool NeedTfa = false)
    {
        public override string ToString() => $"Ticket {{ Username = {Username} }}";
    }

    public Uri ApiUrl { get; }
    public PveTls Tls { get; }
    /// <summary>The pinned CA's PEM, or null for system trust; passed on to the terminal bridge.</summary>
    public string? CaPem { get; }
    /// <summary>One line per request and auth event; never a secret, ticket or form value.</summary>
    public Action<string>? Log { get; set; }

    public bool IsSignedIn => _apiToken is not null || _ticket is not null;
    /// <summary>USER@REALM (ticket) or the token ID, once signed in.</summary>
    public string? SignedInAs { get; private set; }

    /// <param name="apiUrl">https://host:port, as <see cref="NormalizeApiUrl"/> returns it.</param>
    /// <param name="caPem">A CA to pin, or null for the Windows trust roots (docs/decisions/0033).</param>
    /// <param name="time">Tests pass a manual clock to step through renewal and expiry.</param>
    public PveClient(Uri apiUrl, string? caPem, TimeProvider? time = null)
    {
        ApiUrl = NormalizeApiUrl(apiUrl.ToString());
        CaPem = string.IsNullOrWhiteSpace(caPem) ? null : caPem;
        try { Tls = new PveTls(CaPem); }
        catch (TermDescriptorException) { throw new PveApiException("The CA is not a valid PEM certificate."); }
        _time = time ?? TimeProvider.System;
        _handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Idle connections close after a minute; the pool's cleanup timer stops once the pool is empty, so an
            // idle launcher keeps no timer (energy rules).
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            SslOptions = { RemoteCertificateValidationCallback = Tls.Validate },
        };
        // The deadline is per request in SendAsync, covering the body too; HttpClient's own stops at the headers.
        _http = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public override string ToString() => $"PveClient {{ {ApiUrl.Authority}, {(IsSignedIn ? "signed in as " + SignedInAs : "signed out")} }}";

    /// <summary>
    /// Accepts <c>host</c>, <c>host:port</c> or <c>https://host[:port][/]</c> (a pasted web UI address with a
    /// <c>#...</c> fragment too) and returns <c>https://host:port/</c>; without a port, 8006.
    /// </summary>
    public static Uri NormalizeApiUrl(string text)
    {
        text = text.Trim();
        if (text.Length == 0) throw new PveApiException("Enter the server's address, for example https://pve.example:8006.");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.HostNameType == UriHostNameType.Unknown || u.Host.Length == 0)
            throw new PveApiException("The server address is not a valid URL.");
        if (u.Scheme != Uri.UriSchemeHttps) throw new PveApiException("The server address must use https.");
        if (u.UserInfo.Length > 0 || u.Query.Length > 0 || u.AbsolutePath != "/")
            throw new PveApiException("The server address takes only a host and port, for example https://pve.example:8006.");
        // Uri fills in 443 when no port was written; Proxmox listens on 8006 unless the user says otherwise.
        int start = text.IndexOf("://", StringComparison.Ordinal) + 3;
        int end = text.IndexOfAny(['/', '?', '#'], start);
        string authority = end < 0 ? text[start..] : text[start..end];
        bool portWritten = authority.StartsWith('[') ? authority.Contains("]:", StringComparison.Ordinal) : authority.Contains(':');
        int port = portWritten ? u.Port : DefaultPort;
        return new Uri($"https://{(u.HostNameType == UriHostNameType.IPv6 ? u.Host : u.IdnHost)}:{port}/");
    }

    /// <summary>The part before or after '@' in a PVE user ID: no whitespace, ':', '/' or '!' (pve-access-control
    /// Auth/Plugin.pm user and realm formats, a little looser on the realm).</summary>
    public static bool IsValidUserPart(string s) =>
        s.Length is > 0 and <= 64 && s.All(c => c > ' ' && c < 0x7F && c is not (':' or '/' or '!'));

    /// <summary>USER@REALM!TOKENNAME, with the token name in Proxmox's form ([A-Za-z][A-Za-z0-9._-]*).</summary>
    public static bool IsValidTokenId(string s)
    {
        int bang = s.LastIndexOf('!');
        if (bang <= 0) return false;
        string user = s[..bang], name = s[(bang + 1)..];
        int at = user.LastIndexOf('@');
        if (at <= 0 || !IsValidUserPart(user[..at]) || !IsValidUserPart(user[(at + 1)..])) return false;
        return name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    }

    static bool IsHeaderSafe(string s) => s.All(c => c is >= (char)0x21 and <= (char)0x7E);

    // ---- Sign-in -------------------------------------------------------------------------------------------------

    /// <summary>Uses an API token: <c>Authorization: PVEAPIToken=USER@REALM!TOKENID=SECRET</c>. Nothing is sent
    /// yet; <see cref="GetVersionAsync"/> checks it.</summary>
    public void UseApiToken(string tokenId, string secret)
    {
        if (!IsValidTokenId(tokenId)) throw new PveApiException("The token ID must look like user@realm!tokenname.");
        secret = secret.Trim();
        if (secret.Length is 0 or > 256 || !IsHeaderSafe(secret)) throw new PveAuthException("The token secret is empty or has characters a header can't carry.");
        SignOut();
        _apiToken = tokenId + "=" + secret;
        SignedInAs = tokenId;
    }

    /// <summary>
    /// First factor: <c>POST /access/ticket</c> with <c>username=USER@REALM</c> and the password. Returns
    /// <see cref="SignInStep.NeedsSecondFactor"/> with <paramref name="challenge"/> set when the account has TFA;
    /// then call <see cref="CompleteSecondFactorAsync"/>.
    /// </summary>
    public async Task<(SignInStep Step, TfaChallenge? Challenge)> LoginAsync(string user, string realm, string password, CancellationToken ct = default)
    {
        if (!IsValidUserPart(user) || !IsValidUserPart(realm)) throw new PveApiException("The user name or realm is not valid.");
        if (password.Length == 0) throw new PveAuthException("Enter the password.");
        SignOut();
        var t = await PostTicketAsync([new("username", $"{user}@{realm}"), new("password", password)], ct).ConfigureAwait(false);
        if (TfaChallenge.IsChallengeTicket(t.Value))
        {
            var challenge = TfaChallenge.FromTicket(t.Value);
            _pending = t;
            Log?.Invoke($"auth: {t.Username} needs a second factor (totp={challenge.Totp}, recovery keys={challenge.RecoveryKeysLeft})");
            return (SignInStep.NeedsSecondFactor, challenge);
        }
        if (t.NeedTfa) throw new PveApiException("This server asks for the second factor the pre-7.0 way, which is not supported.");
        Accept(t);
        return (SignInStep.Done, null);
    }

    /// <summary>
    /// Second factor: <c>POST /access/ticket</c> with <c>tfa-challenge</c> set to the half ticket and the response
    /// in <c>password</c> (pve-access-control API2/AccessControl.pm create_ticket_do; build it with
    /// <see cref="TfaChallenge.TotpResponse"/> or <see cref="TfaChallenge.RecoveryResponse"/>). A refused code keeps
    /// the challenge, so the user can try again.
    /// </summary>
    public async Task CompleteSecondFactorAsync(string response, CancellationToken ct = default)
    {
        var p = _pending ?? throw new InvalidOperationException("No second factor is pending.");
        var t = await PostTicketAsync([new("username", p.Username), new("tfa-challenge", p.Value), new("password", response)], ct).ConfigureAwait(false);
        if (TfaChallenge.IsChallengeTicket(t.Value) || t.NeedTfa) throw new PveApiException("The server asked for another factor; that is not supported.");
        // The web UI keeps the first step's CSRF token when the second step has none (LoginWindow.js).
        Accept(t with { Csrf = t.Csrf ?? p.Csrf });
    }

    public void SignOut()
    {
        _apiToken = null;
        _ticket = null;
        _pending = null;
        SignedInAs = null;
    }

    void Accept(Ticket t)
    {
        _pending = null;
        _ticket = t;
        SignedInAs = t.Username;
        Log?.Invoke($"auth: signed in as {t.Username}");
    }

    async Task<Ticket> PostTicketAsync(KeyValuePair<string, string>[] form, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, "/access/ticket", form, authenticated: false, ct).ConfigureAwait(false);
        var data = Data(doc);
        string? user = Str(data, "username"), ticket = Str(data, "ticket"), csrf = Str(data, "CSRFPreventionToken");
        if (user is null || ticket is null || !IsHeaderSafe(ticket) || csrf is not null && !IsHeaderSafe(csrf))
            throw new PveApiException("The server's sign-in answer lacks a usable ticket.");
        bool needTfa = data.TryGetProperty("NeedTFA", out var n) && n.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)
            && !(n.ValueKind == JsonValueKind.Number && n.GetDouble() == 0);
        return new Ticket(user, ticket, csrf, _time.GetTimestamp(), needTfa);
    }

    /// <summary>
    /// Renews the ticket when it is older than <see cref="RenewAfter"/>: <c>POST /access/ticket</c> with the ticket
    /// as the password (create_ticket_do accepts a valid ticket of the same user without asking for TFA again). A
    /// ticket past its lifetime is dropped and the caller must sign in again.
    /// </summary>
    async ValueTask EnsureFreshAsync(CancellationToken ct)
    {
        var t = _ticket;
        if (t is null) return;
        var age = _time.GetElapsedTime(t.IssuedAt);
        if (age >= TicketLifetime - ExpiryMargin)
        {
            if (_ticket == t) _ticket = null;
            Log?.Invoke("auth: the ticket expired");
            throw new PveAuthException("The session expired; sign in again.");
        }
        if (age < RenewAfter) return;
        await _renewLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ticket != t) return; // renewed meanwhile
            try
            {
                var fresh = await PostTicketAsync([new("username", t.Username), new("password", t.Value)], ct).ConfigureAwait(false);
                if (TfaChallenge.IsChallengeTicket(fresh.Value)) throw new PveAuthException("The server asked for a second factor again; sign in again.");
                _ticket = fresh with { Csrf = fresh.Csrf ?? t.Csrf };
                Log?.Invoke("auth: renewed the ticket");
            }
            catch (PveAuthException)
            {
                _ticket = null;
                SignedInAs = null;
                throw;
            }
            catch (PveApiException e)
            {
                // The ticket is still valid; try again before the next request.
                Log?.Invoke($"auth: renewing the ticket failed ({e.Message}); keeping the current one");
            }
        }
        finally { _renewLock.Release(); }
    }

    // ---- API calls -----------------------------------------------------------------------------------------------

    /// <summary><c>GET /version</c>: any signed-in user may call it, so it also checks a token.</summary>
    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/version", null, authenticated: true, ct).ConfigureAwait(false);
        var data = Data(doc);
        return Str(data, "version") ?? "unknown";
    }

    /// <summary><c>GET /cluster/resources?type=vm</c>: VMs and containers the account may see, by VMID.</summary>
    public async Task<IReadOnlyList<PveVm>> ListVmsAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/cluster/resources?type=vm", null, authenticated: true, ct).ConfigureAwait(false);
        var list = PveVm.ParseResources(Data(doc));
        Log?.Invoke($"api: {list.Count} guests listed");
        return list;
    }

    /// <summary>
    /// <c>POST /nodes/{node}/qemu/{vmid}/spiceproxy</c> with <c>proxy</c> set to this API's host, as the web UI
    /// does (pve-common JSONSchema.pm spice-proxy), and returns the answer as .vv text that
    /// <see cref="VvFile.VvConnection.Parse"/> accepts. The text holds a one-time ticket: hand it to the viewer
    /// through its standard input only.
    /// </summary>
    public async Task<string> GetSpiceVvTextAsync(string node, int vmid, CancellationToken ct = default)
    {
        CheckTarget(node, vmid);
        string proxy = ApiUrl.HostNameType == UriHostNameType.IPv6 ? ApiUrl.Host.Trim('[', ']') : ApiUrl.IdnHost;
        using var doc = await SendAsync(HttpMethod.Post, $"/nodes/{node}/qemu/{vmid}/spiceproxy", [new("proxy", proxy)], authenticated: true, ct).ConfigureAwait(false);
        return VvText.FromSpiceproxy(Data(doc));
    }

    /// <summary>
    /// The connection descriptor for the terminal bridge (docs/decisions/0036): the API token, or a freshly renewed
    /// ticket plus its CSRF token, and the pinned CA. PveTerm requests the termproxy session itself.
    /// </summary>
    public async Task<TermDescriptor> CreateTermDescriptorAsync(PveVm vm, CancellationToken ct = default)
    {
        CheckTarget(vm.Node, vm.Vmid);
        await EnsureFreshAsync(ct).ConfigureAwait(false);
        string? token = _apiToken;
        var ticket = _ticket;
        if (token is null && ticket is null) throw new PveAuthException("Sign in first.");
        var d = new TermDescriptor
        {
            ApiUrl = ApiUrl,
            Node = vm.Node,
            Target = vm.Type == PveGuestType.Lxc ? TermTarget.Lxc : TermTarget.Qemu,
            Vmid = vm.Vmid,
            ApiToken = token,
            AuthCookie = token is null ? ticket!.Value : null,
            CsrfToken = token is null ? ticket!.Csrf : null,
            CaPem = CaPem,
            Title = TerminalTitle(vm),
        };
        try { d.Validate(); }
        catch (TermDescriptorException e) { throw new PveApiException("The terminal can't be opened: " + e.Message); }
        return d;
    }

    static string TerminalTitle(PveVm vm)
    {
        string name = new(vm.Name.Where(c => !char.IsControl(c)).Take(64).ToArray());
        string title = (vm.Type == PveGuestType.Lxc ? "CT " : "VM ") + vm.Vmid + (name.Length > 0 ? " " + name : "");
        return vm.Type == PveGuestType.Lxc ? title : title + " serial";
    }

    static void CheckTarget(string node, int vmid)
    {
        if (!PveVm.IsSafeId(node)) throw new PveApiException("The node name is not valid.");
        if (vmid is < 100 or > 999_999_999) throw new PveApiException("The VMID is out of range.");
    }

    // ---- HTTP ----------------------------------------------------------------------------------------------------

    async Task<JsonDocument> SendAsync(HttpMethod method, string path, KeyValuePair<string, string>[]? form, bool authenticated, CancellationToken ct)
    {
        if (authenticated)
        {
            await EnsureFreshAsync(ct).ConfigureAwait(false);
            if (_apiToken is null && _ticket is null) throw new PveAuthException("Sign in first.");
        }
        using var req = new HttpRequestMessage(method, new Uri(ApiUrl, "/api2/json" + path));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("SpiceViewer-PveLauncher", null));
        // Everything secret this request carries. The server's reason phrase and error fields are untrusted text that
        // a server or proxy may fill with what it received, so they are scrubbed of these before they reach an
        // exception, and through it the log (review L1).
        var secrets = new List<string>(4);
        var ticket = _ticket;
        if (authenticated && _apiToken is { } token)
        {
            req.Headers.TryAddWithoutValidation("Authorization", "PVEAPIToken=" + token);
            secrets.Add(token);
            secrets.Add(token[(token.IndexOf('=') + 1)..]); // the secret alone (token IDs have no '=')
        }
        else if (authenticated && ticket is not null)
        {
            req.Headers.TryAddWithoutValidation("Cookie", "PVEAuthCookie=" + ticket.Value);
            secrets.Add(ticket.Value);
            // pveproxy checks the CSRF token on every request that isn't a GET (PVE::APIServer::AnyEvent).
            if (method != HttpMethod.Get && ticket.Csrf is not null)
            {
                req.Headers.TryAddWithoutValidation("CSRFPreventionToken", ticket.Csrf);
                secrets.Add(ticket.Csrf);
            }
        }
        if (form is not null)
        {
            req.Content = new FormUrlEncodedContent(form);
            foreach (var (name, value) in form)
            {
                if (name is not ("password" or "tfa-challenge")) continue;
                secrets.Add(value);
                // A second-factor response: the code or key alone, too.
                if (value.StartsWith("totp:", StringComparison.Ordinal) || value.StartsWith("recovery:", StringComparison.Ordinal))
                    secrets.Add(value[(value.IndexOf(':') + 1)..]);
            }
        }

        // One deadline for the whole exchange, body included (review L3). It exists only while a request runs.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestTimeout);
        HttpResponseMessage? res = null;
        try
        {
            try { res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw new PveApiException(Describe(e), null, e); }
            string pathOnly = path.Split('?')[0];
            Log?.Invoke($"api: {method} {pathOnly} -> {(int)res.StatusCode}");
            bool ok = res.StatusCode == HttpStatusCode.OK;
            byte[] body;
            try { body = await ReadBodyAsync(res.Content, ok ? MaxResponseBytes : MaxErrorBytes, truncate: !ok, deadline.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                throw new PveApiException("The connection broke while the server's answer arrived.", (int)res.StatusCode, e);
            }
            if (!ok)
            {
                string why = ErrorText(res, body, secrets);
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (authenticated && _ticket == ticket && ticket is not null) { _ticket = null; SignedInAs = null; }
                    throw new PveAuthException(path.StartsWith("/access/ticket", StringComparison.Ordinal)
                        ? "Sign-in failed: " + why : "The server refused the credentials: " + why, 401);
                }
                throw new PveApiException(why, (int)res.StatusCode);
            }
            try { return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { throw new PveApiException("The server's answer is not valid JSON.", 200); }
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new PveApiException($"The server did not answer within {RequestTimeout.TotalSeconds:0} seconds.", (int?)res?.StatusCode, e);
        }
        finally { res?.Dispose(); }
    }

    /// <summary>
    /// Reads the body in pieces, so a body without a length (chunked, or ended by closing) is refused as soon as it
    /// passes <paramref name="limit"/>, not after it is all in memory (review L4). With <paramref name="truncate"/>
    /// (error answers, read only for their message) the rest is dropped instead.
    /// </summary>
    static async Task<byte[]> ReadBodyAsync(HttpContent content, int limit, bool truncate, CancellationToken ct)
    {
        if (!truncate && content.Headers.ContentLength > limit) throw new PveApiException("The server's answer is too large.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var data = new MemoryStream((int)Math.Min(content.Headers.ContentLength ?? 16 << 10, limit));
        byte[] chunk = ArrayPool<byte>.Shared.Rent(64 << 10);
        try
        {
            int n;
            while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (data.Length + n > limit)
                {
                    if (!truncate) throw new PveApiException("The server's answer is too large.");
                    data.Write(chunk, 0, (int)(limit - data.Length));
                    break;
                }
                data.Write(chunk, 0, n);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
        return data.ToArray();
    }

    /// <summary>pveproxy puts the error in the reason phrase (e.g. "500 VM 100 not running"); parameter errors also
    /// come as <c>{"errors":{"field":"message"}}</c>. Both are the server's text: <paramref name="secrets"/> are
    /// replaced in it before it is returned.</summary>
    static string ErrorText(HttpResponseMessage res, byte[] body, List<string> secrets)
    {
        string text = $"{(int)res.StatusCode} {res.ReasonPhrase}".Trim();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Object)
            {
                var parts = errors.EnumerateObject().Take(8).Select(p => $"{p.Name}: {(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()?.Trim() : p.Value.ToString())}");
                text += " (" + string.Join("; ", parts) + ")";
            }
        }
        catch (JsonException) { }
        text = Redact(text, secrets);
        return text.Length > 600 ? text[..600] + "..." : text;
    }

    /// <summary>Replaces each secret, as sent and in its URL- and form-encoded spellings, longest first, in any case.</summary>
    internal static string Redact(string text, IEnumerable<string> secrets)
    {
        var forms = secrets.Where(s => s.Length > 0)
            .SelectMany(s => new[] { s, Uri.EscapeDataString(s), WebUtility.UrlEncode(s) })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length);
        // Ignoring case also catches percent-escapes written with lowercase hex; over-redacting an error is harmless.
        foreach (var s in forms) text = text.Replace(s, "[redacted]", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    string Describe(HttpRequestException e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x is AuthenticationException)
                // The policy's own reason when it refused the certificate; else Schannel's.
                return Tls.Failure is { } f ? $"TLS: {f}." : $"TLS handshake failed ({x.Message}).";
        return $"Could not reach {ApiUrl.Authority}: {e.Message}";
    }

    static JsonElement Data(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data))
            throw new PveApiException("The server's answer has no data.");
        return data;
    }

    static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose()
    {
        SignOut();
        _http.Dispose();
        _handler.Dispose();
        _renewLock.Dispose();
    }
}
