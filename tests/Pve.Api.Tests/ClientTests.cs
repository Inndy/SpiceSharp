using System.Text.Json;
using Pve.Term.Tests;
using VvFile;

namespace Pve.Api.Tests;

/// <summary>Fake credentials and tickets, shaped like Proxmox's (pve-common PVE/Ticket.pm).</summary>
static class Fake
{
    public const string TokenId = "launcher@pve!viewer";
    public const string TokenSecret = "0b5e8f0e-1111-2222-3333-SECRETSECRET";
    public const string Password = "pass word: SECRET";
    public const string Ticket = "PVE:alice@pve:6720A1B2::c2lnbmF0dXJl+/AAA=";
    public const string Ticket2 = "PVE:alice@pve:6720A5C3::cmVuZXdlZA+/BBB=";
    public const string Csrf = "6720A1B2:Y3NyZnRva2Vu";
    public const string Csrf2 = "6720A5C3:cmVuZXdlZGNzcmY";
    /// <summary>The JSON is percent-escaped for ':' and '%' only, as assemble_rsa_ticket does.</summary>
    public static string TfaTicket(string challengeJson) =>
        "PVE:!tfa!" + challengeJson.Replace("%", "%25").Replace(":", "%3A") + ":6720A1B2::dGZhc2ln";

    public static FakeResponse TicketAnswer(string ticket, string? csrf = Csrf, string user = "alice@pve", bool needTfa = false) =>
        FakeResponse.Data(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["username"] = user,
            ["ticket"] = ticket,
            ["CSRFPreventionToken"] = csrf,
            ["NeedTFA"] = needTfa ? 1 : null,
        }, ApiTestJson.Default.DictionaryStringObject));

    public const string Version = """{"version":"9.2.1","release":"9.2","repoid":"abcdef12"}""";

    public const string SpiceAnswer = """
        {"type":"spice","host":"pvespiceproxy:6720a1b2:100:node1::0123456789abcdef0123456789abcdef01234567","proxy":"http://pve.example:3128",
         "tls-port":61000,"password":"SPICETICKET","ca":"-----BEGIN CERTIFICATE-----\\nMIIB\\n-----END CERTIFICATE-----\\n",
         "host-subject":"OU=PVE Cluster Node,O=Proxmox Virtual Environment,CN=node1.pve.example","title":"VM 100 - win11",
         "delete-this-file":1,"toggle-fullscreen":"Shift+F11","release-cursor":"Ctrl+Alt+R","secure-attention":"Ctrl+Alt+Ins"}
        """;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object?>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
partial class ApiTestJson : System.Text.Json.Serialization.JsonSerializerContext;

public class AuthTests
{
    [Fact]
    public async Task TokenGoesInThePveApiTokenHeaderAndNothingElse()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data(Fake.Version);
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        Assert.Equal("9.2.1", await c.GetVersionAsync());
        var r = Assert.Single(pve.Seen());
        Assert.Equal("GET", r.Method);
        Assert.Equal("/api2/json/version", r.Path);
        Assert.Equal($"PVEAPIToken={Fake.TokenId}={Fake.TokenSecret}", r.Header("Authorization"));
        Assert.Null(r.Header("Cookie"));
        Assert.Null(r.Header("CSRFPreventionToken"));
        Assert.Equal(Fake.TokenId, c.SignedInAs);
    }

    [Fact]
    public async Task TokenPostNeedsNoCsrf()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data(Fake.SpiceAnswer);
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        await c.GetSpiceVvTextAsync("node1", 100);
        var r = Assert.Single(pve.Seen());
        Assert.Equal("POST", r.Method);
        Assert.Null(r.Header("CSRFPreventionToken"));
        Assert.StartsWith("PVEAPIToken=", r.Header("Authorization"));
    }

    [Theory]
    [InlineData("root@pam")]          // no token name
    [InlineData("root!tok")]          // no realm
    [InlineData("root@pam!1tok")]     // token name must start with a letter
    [InlineData("ro ot@pam!tok")]
    [InlineData("")]
    public void MalformedTokenIdsAreRefused(string id)
    {
        Assert.False(PveClient.IsValidTokenId(id));
        using var c = new PveClient(new Uri("https://pve.example:8006"), null);
        Assert.Throws<PveApiException>(() => c.UseApiToken(id, "secret"));
    }

    [Fact]
    public void TokenSecretMustBeHeaderSafe()
    {
        using var c = new PveClient(new Uri("https://pve.example:8006"), null);
        Assert.Throws<PveAuthException>(() => c.UseApiToken(Fake.TokenId, "sec\r\nret"));
        Assert.Throws<PveAuthException>(() => c.UseApiToken(Fake.TokenId, "  "));
        Assert.True(PveClient.IsValidTokenId("john@example.com@ldap!tok-1.a_b"));
    }

    [Fact]
    public async Task PasswordLoginPostsTheTicketFormThenUsesTheCookie()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Path switch
        {
            "/api2/json/access/ticket" => Fake.TicketAnswer(Fake.Ticket),
            "/api2/json/version" => FakeResponse.Data(Fake.Version),
            _ => FakeResponse.Data(Fake.SpiceAnswer),
        };
        using var c = new PveClient(pve.Url, pve.CaPem);
        var (step, challenge) = await c.LoginAsync("alice", "pve", Fake.Password);
        Assert.Equal(SignInStep.Done, step);
        Assert.Null(challenge);
        Assert.Equal("alice@pve", c.SignedInAs);
        await c.GetVersionAsync();
        await c.GetSpiceVvTextAsync("node1", 100);

        var seen = pve.Seen();
        Assert.Equal(3, seen.Length);
        var login = seen[0];
        Assert.Equal("POST", login.Method);
        Assert.Equal("/api2/json/access/ticket", login.Path);
        Assert.Equal("application/x-www-form-urlencoded", login.Header("Content-Type"));
        var form = login.Form();
        Assert.Equal("alice@pve", form["username"]);
        Assert.Equal(Fake.Password, form["password"]);
        Assert.Null(login.Header("Cookie"));
        Assert.Null(login.Header("Authorization"));

        Assert.Equal("PVEAuthCookie=" + Fake.Ticket, seen[1].Header("Cookie"));
        Assert.Null(seen[1].Header("CSRFPreventionToken")); // GET carries no CSRF token
        Assert.Equal("PVEAuthCookie=" + Fake.Ticket, seen[2].Header("Cookie"));
        Assert.Equal(Fake.Csrf, seen[2].Header("CSRFPreventionToken")); // POST does
    }

    [Fact]
    public async Task RefusedPasswordIsAnAuthError()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => new FakeResponse(401, "authentication failure", "{\"data\":null}");
        using var c = new PveClient(pve.Url, pve.CaPem);
        var e = await Assert.ThrowsAsync<PveAuthException>(() => c.LoginAsync("alice", "pve", Fake.Password));
        Assert.Contains("authentication failure", e.Message);
        Assert.False(c.IsSignedIn);
    }

    [Fact]
    public async Task TotpIsTheSecondStepWithTheHalfTicketAsChallenge()
    {
        string half = Fake.TfaTicket("""{"totp":true,"recovery":[0,3]}""");
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r =>
        {
            if (r.Path == "/api2/json/version") return FakeResponse.Data(Fake.Version);
            var f = r.Form();
            if (!f.ContainsKey("tfa-challenge")) return Fake.TicketAnswer(half, Fake.Csrf, needTfa: true);
            return f["password"] == "totp:123456" ? Fake.TicketAnswer(Fake.Ticket, Fake.Csrf2) : new FakeResponse(401, "authentication failure", "{}");
        };
        using var c = new PveClient(pve.Url, pve.CaPem);
        var (step, challenge) = await c.LoginAsync("alice", "pve", Fake.Password);
        Assert.Equal(SignInStep.NeedsSecondFactor, step);
        Assert.NotNull(challenge);
        Assert.True(challenge.Totp);
        Assert.Equal(2, challenge.RecoveryKeysLeft);
        Assert.False(c.IsSignedIn);
        await Assert.ThrowsAsync<PveAuthException>(() => c.GetVersionAsync()); // the half ticket is never used for calls

        // A wrong code keeps the challenge; the right one completes the login.
        await Assert.ThrowsAsync<PveAuthException>(() => c.CompleteSecondFactorAsync(TfaChallenge.TotpResponse("000000")));
        await c.CompleteSecondFactorAsync(TfaChallenge.TotpResponse("123 456"));
        Assert.True(c.IsSignedIn);
        await c.GetVersionAsync();

        var posts = pve.Seen().Where(r => r.Path == "/api2/json/access/ticket").ToArray();
        Assert.Equal(3, posts.Length);
        var second = posts[2].Form();
        Assert.Equal("alice@pve", second["username"]);
        Assert.Equal(half, second["tfa-challenge"]);
        Assert.Equal("totp:123456", second["password"]);
        Assert.False(second.ContainsKey("otp"));
        Assert.Null(posts[2].Header("Cookie"));
        Assert.Equal("PVEAuthCookie=" + Fake.Ticket, pve.Seen()[^1].Header("Cookie"));
    }

    [Fact]
    public async Task SecondStepWithoutCsrfKeepsTheFirstOne()
    {
        string half = Fake.TfaTicket("""{"totp":true}""");
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Path switch
        {
            "/api2/json/access/ticket" when !r.Form().ContainsKey("tfa-challenge") => Fake.TicketAnswer(half, Fake.Csrf, needTfa: true),
            "/api2/json/access/ticket" => Fake.TicketAnswer(Fake.Ticket, csrf: null),
            _ => FakeResponse.Data(Fake.SpiceAnswer),
        };
        using var c = new PveClient(pve.Url, pve.CaPem);
        await c.LoginAsync("alice", "pve", Fake.Password);
        await c.CompleteSecondFactorAsync("totp:123456");
        await c.GetSpiceVvTextAsync("node1", 100);
        Assert.Equal(Fake.Csrf, pve.Seen()[^1].Header("CSRFPreventionToken"));
    }

    [Fact]
    public void ChallengeTicketsAreReadLikeTheWebUiReadsThem()
    {
        var all = TfaChallenge.FromTicket(Fake.TfaTicket("""{"totp":true,"recovery":[1],"webauthn":{"challenge":"x:y"},"u2f":null,"yubico":false}"""));
        Assert.True(all.Totp);
        Assert.True(all.Recovery);
        Assert.Equal(1, all.RecoveryKeysLeft);
        Assert.True(all.WebAuthn);
        Assert.False(all.U2f);
        Assert.False(all.Yubico);
        Assert.True(all.CanAnswer);

        var webauthnOnly = TfaChallenge.FromTicket(Fake.TfaTicket("""{"webauthn":{"challenge":"abc"},"recovery":[]}"""));
        Assert.False(webauthnOnly.CanAnswer);
        Assert.True(webauthnOnly.Recovery);
        Assert.Equal(0, webauthnOnly.RecoveryKeysLeft);
        Assert.Equal("WebAuthn", webauthnOnly.UnsupportedFactors);

        Assert.Throws<PveApiException>(() => TfaChallenge.FromTicket("PVE:!tfa!not json:6720A1B2::sig"));
        Assert.Throws<PveApiException>(() => TfaChallenge.FromTicket(Fake.Ticket));
        Assert.Equal("recovery:abcd-ef01-2345", TfaChallenge.RecoveryResponse(" abcd-ef01-2345 "));
        Assert.Throws<ArgumentException>(() => TfaChallenge.TotpResponse("12345"));
        Assert.Throws<ArgumentException>(() => TfaChallenge.TotpResponse("12345a"));
        Assert.Throws<ArgumentException>(() => TfaChallenge.RecoveryResponse("a:b"));
    }

    [Fact]
    public async Task AReflectedSecondFactorStaysOutOfTheMessage()
    {
        string half = Fake.TfaTicket("""{"totp":true}""");
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Form().ContainsKey("tfa-challenge")
            ? new FakeResponse(401, "bad code " + r.Form()["password"][5..] + " for " + Uri.EscapeDataString(r.Form()["tfa-challenge"]), "{}")
            : Fake.TicketAnswer(half, needTfa: true);
        using var c = new PveClient(pve.Url, pve.CaPem);
        await c.LoginAsync("alice", "pve", Fake.Password);
        var e = await Assert.ThrowsAsync<PveAuthException>(() => c.CompleteSecondFactorAsync("totp:482913"));
        Assert.DoesNotContain("482913", e.ToString());
        Assert.DoesNotContain("dGZhc2ln", e.ToString()); // the half ticket's signature, even percent-encoded
        Assert.StartsWith("Sign-in failed: 401 bad code [redacted]", e.Message);
    }

    [Fact]
    public void RedactionCoversEncodedSpellings()
    {
        // As sent, URL-escaped, and form-encoded with lowercase hex.
        string text = PveClient.Redact("a=pass word: SECRET b=pass%20word%3A%20SECRET c=pass+word%3a+SECRET", [Fake.Password]);
        Assert.Equal("a=[redacted] b=[redacted] c=[redacted]", text);
    }

    [Fact]
    public async Task OldStyleNeedTfaIsRefusedClearly()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => Fake.TicketAnswer(Fake.Ticket, needTfa: true);
        using var c = new PveClient(pve.Url, pve.CaPem);
        var e = await Assert.ThrowsAsync<PveApiException>(() => c.LoginAsync("alice", "pve", Fake.Password));
        Assert.Contains("pre-7.0", e.Message);
        Assert.False(c.IsSignedIn);
    }
}

public class RenewalTests
{
    static (FakePve Pve, List<string> Posts) Server()
    {
        var pve = new FakePve(TestPki.Shared);
        var posts = new List<string>();
        pve.Handler = r =>
        {
            if (r.Path != "/api2/json/access/ticket") return FakeResponse.Data(Fake.Version);
            var f = r.Form();
            lock (posts) posts.Add(f["password"]);
            return f["password"] == Fake.Ticket ? Fake.TicketAnswer(Fake.Ticket2, Fake.Csrf2) : Fake.TicketAnswer(Fake.Ticket);
        };
        return (pve, posts);
    }

    [Fact]
    public async Task AFreshTicketIsNotRenewed()
    {
        var (pve, posts) = Server();
        await using var _ = pve;
        var time = new ManualTime();
        using var c = new PveClient(pve.Url, pve.CaPem, time);
        await c.LoginAsync("alice", "pve", Fake.Password);
        time.Advance(TimeSpan.FromMinutes(14));
        await c.GetVersionAsync();
        Assert.Single(posts);
        Assert.Equal("PVEAuthCookie=" + Fake.Ticket, pve.Seen()[^1].Header("Cookie"));
    }

    [Fact]
    public async Task AnOlderTicketIsTradedForANewOneBeforeTheRequest()
    {
        var (pve, posts) = Server();
        await using var _ = pve;
        var time = new ManualTime();
        using var c = new PveClient(pve.Url, pve.CaPem, time);
        await c.LoginAsync("alice", "pve", Fake.Password);
        time.Advance(TimeSpan.FromMinutes(16));
        await c.GetVersionAsync();

        Assert.Equal([Fake.Password, Fake.Ticket], posts); // renewal sends the ticket as the password
        var seen = pve.Seen();
        var renew = seen[1];
        Assert.Equal("/api2/json/access/ticket", renew.Path);
        Assert.Equal("alice@pve", renew.Form()["username"]);
        Assert.Null(renew.Header("Cookie"));
        Assert.Equal("PVEAuthCookie=" + Fake.Ticket2, seen[2].Header("Cookie"));

        // The renewed ticket's clock starts again: 10 more minutes is not yet time to renew it.
        time.Advance(TimeSpan.FromMinutes(10));
        await c.GetVersionAsync();
        Assert.Equal(2, posts.Count);
    }

    [Fact]
    public async Task AnExpiredTicketIsDroppedWithoutARequest()
    {
        var (pve, posts) = Server();
        await using var _ = pve;
        var time = new ManualTime();
        using var c = new PveClient(pve.Url, pve.CaPem, time);
        await c.LoginAsync("alice", "pve", Fake.Password);
        time.Advance(PveClient.TicketLifetime - PveClient.ExpiryMargin);
        int before = pve.Seen().Length;
        var e = await Assert.ThrowsAsync<PveAuthException>(() => c.GetVersionAsync());
        Assert.Contains("expired", e.Message);
        Assert.Equal(before, pve.Seen().Length);
        Assert.False(c.IsSignedIn);
    }

    [Fact]
    public async Task AFailedRenewalKeepsTheValidTicket()
    {
        await using var pve = new FakePve(TestPki.Shared);
        int tickets = 0;
        pve.Handler = r => r.Path == "/api2/json/access/ticket"
            ? (Interlocked.Increment(ref tickets) == 1 ? Fake.TicketAnswer(Fake.Ticket) : new FakeResponse(503, "busy", "{}"))
            : FakeResponse.Data(Fake.Version);
        var time = new ManualTime();
        using var c = new PveClient(pve.Url, pve.CaPem, time);
        await c.LoginAsync("alice", "pve", Fake.Password);
        time.Advance(TimeSpan.FromMinutes(30));
        await c.GetVersionAsync();
        Assert.Equal("PVEAuthCookie=" + Fake.Ticket, pve.Seen()[^1].Header("Cookie"));
        Assert.True(c.IsSignedIn);
    }

    [Fact]
    public async Task ARefusedRenewalSignsOut()
    {
        await using var pve = new FakePve(TestPki.Shared);
        int tickets = 0;
        pve.Handler = r => r.Path == "/api2/json/access/ticket"
            ? (Interlocked.Increment(ref tickets) == 1 ? Fake.TicketAnswer(Fake.Ticket) : new FakeResponse(401, "authentication failure", "{}"))
            : FakeResponse.Data(Fake.Version);
        var time = new ManualTime();
        using var c = new PveClient(pve.Url, pve.CaPem, time);
        await c.LoginAsync("alice", "pve", Fake.Password);
        time.Advance(TimeSpan.FromMinutes(30));
        await Assert.ThrowsAsync<PveAuthException>(() => c.GetVersionAsync());
        Assert.False(c.IsSignedIn);
    }

    [Fact]
    public async Task A401OnACallSignsOutATicketSession()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Path == "/api2/json/access/ticket" ? Fake.TicketAnswer(Fake.Ticket) : new FakeResponse(401, "No ticket", "{}");
        using var c = new PveClient(pve.Url, pve.CaPem);
        await c.LoginAsync("alice", "pve", Fake.Password);
        var e = await Assert.ThrowsAsync<PveAuthException>(() => c.ListVmsAsync());
        Assert.Contains("No ticket", e.Message);
        Assert.False(c.IsSignedIn);
    }

    [Fact]
    public async Task TheTerminalDescriptorGetsARenewedTicket()
    {
        var (pve, posts) = Server();
        await using var _ = pve;
        var time = new ManualTime();
        using var c = new PveClient(pve.Url, pve.CaPem, time);
        await c.LoginAsync("alice", "pve", Fake.Password);
        time.Advance(TimeSpan.FromMinutes(20));
        var d = await c.CreateTermDescriptorAsync(new PveVm(101, "web", "node1", PveGuestType.Lxc, "running", false));
        Assert.Equal(Fake.Ticket2, d.AuthCookie);
        Assert.Equal(Fake.Csrf2, d.CsrfToken);
        Assert.Null(d.ApiToken);
        Assert.Equal(Pve.Term.TermTarget.Lxc, d.Target);
        Assert.Equal("CT 101 web", d.Title);
        Assert.Equal(pve.CaPem, d.CaPem);
        Assert.Equal(2, posts.Count);
    }
}

public class TlsTests
{
    [Fact]
    public async Task APinnedCaIsTheOnlyTrust()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data(Fake.Version);
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        Assert.Equal("9.2.1", await c.GetVersionAsync());
        Assert.True(c.Tls.IsPinned);
    }

    [Fact]
    public async Task WithoutACaWindowsDecidesAndRefusesTheTestCa()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data(Fake.Version);
        using var c = new PveClient(pve.Url, caPem: null);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        var e = await Assert.ThrowsAsync<PveApiException>(() => c.GetVersionAsync());
        Assert.Contains("not trusted by Windows", e.Message);
        Assert.False(c.Tls.IsPinned);
        Assert.Empty(pve.Seen()); // the token never crossed an untrusted connection
    }

    [Fact]
    public async Task AnotherPinnedCaIsRefused()
    {
        await using var pve = new FakePve(TestPki.Shared);
        using var c = new PveClient(pve.Url, TestPki.Create("Other CA").CaPem);
        var e = await Assert.ThrowsAsync<PveApiException>(() => c.LoginAsync("alice", "pve", Fake.Password));
        Assert.Contains("pinned CA", e.Message);
        Assert.Empty(pve.Seen()); // nor did the password
    }

    [Fact]
    public async Task APinnedCaStillNeedsTheHostName()
    {
        var noIp = TestPki.Create("No-IP CA", includeIp: false);
        await using var pve = new FakePve(noIp);
        using var c = new PveClient(pve.IpUrl, noIp.CaPem);
        var e = await Assert.ThrowsAsync<PveApiException>(() => c.LoginAsync("alice", "pve", Fake.Password));
        Assert.Contains("does not name the host", e.Message);
        Assert.Empty(pve.Seen());
    }

    [Fact]
    public async Task AnUnreachableServerIsNotCalledATlsProblem()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // nothing listens there now
        using var c = new PveClient(new Uri($"https://localhost:{port}"), TestPki.Shared.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        var e = await Assert.ThrowsAsync<PveApiException>(() => c.GetVersionAsync());
        Assert.StartsWith($"Could not reach localhost:{port}", e.Message);
        Assert.Null(e.StatusCode);
    }

    [Fact]
    public void ABadCaIsRefusedUpFront()
    {
        Assert.Throws<PveApiException>(() => new PveClient(new Uri("https://pve.example:8006"), "not a pem"));
        Assert.Equal("The CA is not a valid PEM certificate.", new PveServerConfig { Url = "pve.example", User = "u", CaPem = "junk" }.Problem());
    }

    [Theory]
    [InlineData("pve.example", "https://pve.example:8006/")]
    [InlineData("pve.example:8443", "https://pve.example:8443/")]
    [InlineData("https://pve.example", "https://pve.example:8006/")]
    [InlineData("https://pve.example:443/", "https://pve.example:443/")]
    [InlineData("  https://PVE.example:8006/#v1:0:=qemu%2F100:4  ", "https://pve.example:8006/")]
    [InlineData("[fd00::1]", "https://[fd00::1]:8006/")]
    [InlineData("https://[fd00::1]:9000", "https://[fd00::1]:9000/")]
    [InlineData("192.0.2.10", "https://192.0.2.10:8006/")]
    public void ApiUrlsAreNormalized(string text, string expected) =>
        Assert.Equal(new Uri(expected), PveClient.NormalizeApiUrl(text)); // Uri compares 443 with an elided default port

    [Theory]
    [InlineData("http://pve.example:8006")]
    [InlineData("https://user:pw@pve.example")]
    [InlineData("https://pve.example/api2/json")]
    [InlineData("https://pve.example/?x=1")]
    [InlineData("")]
    public void BadApiUrlsAreRefused(string text) =>
        Assert.Throws<PveApiException>(() => PveClient.NormalizeApiUrl(text));
}

public class ResourceTests
{
    const string Resources = """
        {"data":[
          {"id":"qemu/101","type":"qemu","vmid":101,"name":"win11","node":"node1","status":"running","template":0,"maxmem":4294967296},
          {"id":"lxc/100","type":"lxc","vmid":"100","name":"dns","node":"node2","status":"stopped"},
          {"id":"qemu/9000","type":"qemu","vmid":9000,"name":"tmpl","node":"node1","status":"stopped","template":1},
          {"id":"storage/node1/local","type":"storage","node":"node1","storage":"local"},
          {"id":"qemu/102","type":"qemu","vmid":102,"node":"bad node","status":"running"},
          {"id":"qemu/103","type":"qemu","node":"node1","status":"running"},
          {"id":"qemu/104","type":"qemu","vmid":104,"node":"node1","status":"running"},
          "junk"
        ]}
        """;

    [Fact]
    public async Task ClusterResourcesAreParsedSortedAndFiltered()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Json(Resources);
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        var vms = await c.ListVmsAsync();
        Assert.Equal("/api2/json/cluster/resources?type=vm", Assert.Single(pve.Seen()).Path);
        Assert.Equal([100, 101, 104, 9000], vms.Select(v => v.Vmid));
        var dns = vms[0];
        Assert.Equal(PveGuestType.Lxc, dns.Type);
        Assert.Equal("dns", dns.Name);
        Assert.Equal("node2", dns.Node);
        Assert.Equal("stopped", dns.Status);
        Assert.False(dns.IsRunning);
        Assert.True(vms[1].IsRunning);
        Assert.Equal("qemu", vms[1].TypeText);
        Assert.Equal("", vms[2].Name);
        Assert.True(vms[3].IsTemplate);
    }

    [Fact]
    public void ANonListIsAnError() =>
        Assert.Throws<PveApiException>(() => PveVm.ParseResources(JsonDocument.Parse("{}").RootElement));
}

public class SpiceproxyTests
{
    [Fact]
    public async Task TheAnswerBecomesVvTextTheViewerAccepts()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => FakeResponse.Data(Fake.SpiceAnswer);
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        string text = await c.GetSpiceVvTextAsync("node1", 100);

        var req = Assert.Single(pve.Seen());
        Assert.Equal("/api2/json/nodes/node1/qemu/100/spiceproxy", req.Path);
        Assert.Equal("localhost", req.Form()["proxy"]); // the API's own host, as the web UI sends it

        Assert.StartsWith("[virt-viewer]\n", text);
        Assert.Contains("\ntls-port=61000\n", text);
        Assert.Contains("\ndelete-this-file=1\n", text);
        Assert.Contains("\nca=-----BEGIN CERTIFICATE-----\\nMIIB\\n-----END CERTIFICATE-----\\n\n", text);
        var vv = VvConnection.Parse(text);
        Assert.Equal(61000, vv.TlsPort);
        Assert.Equal("SPICETICKET", vv.Password);
        Assert.Equal("pve.example", vv.Proxy!.Host);
        Assert.Equal(3128, vv.Proxy.Port);
        Assert.Equal("VM 100 - win11", vv.Title);
        Assert.Equal("-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----", vv.CaPem); // VvConnection trims values
        Assert.Equal("OU=PVE Cluster Node,O=Proxmox Virtual Environment,CN=node1.pve.example", vv.HostSubject);
    }

    [Fact]
    public void RawLineBreaksCannotAddKeys()
    {
        var data = JsonDocument.Parse("""
            {"type":"spice","host":"h","tls-port":"61000","title":"evil\ndelete-this-file=0\r\nhost=attacker","bad key":"x","nested":{"a":1}}
            """).RootElement;
        string text = VvText.FromSpiceproxy(data);
        Assert.Equal(5, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length); // the section, then type, host, tls-port and title
        Assert.DoesNotContain("bad key", text);
        Assert.DoesNotContain("nested", text);
        var vv = VvConnection.Parse(text);
        Assert.Equal("h", vv.Host);
        Assert.Equal("evil\ndelete-this-file=0\r\nhost=attacker", vv.Title);
    }

    [Fact]
    public void AnAnswerWithoutSpiceIsRefused()
    {
        var e = Assert.Throws<PveApiException>(() => VvText.FromSpiceproxy(JsonDocument.Parse("""{"type":"vnc","host":"h","port":5900}""").RootElement));
        Assert.Contains("SPICE answer can't be used", e.Message);
        Assert.Throws<PveApiException>(() => VvText.FromSpiceproxy(JsonDocument.Parse("null").RootElement));
    }

    [Fact]
    public async Task TheServersErrorIsShown()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => new FakeResponse(500, "VM 100 not running", "{\"data\":null}");
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        var e = await Assert.ThrowsAsync<PveApiException>(() => c.GetSpiceVvTextAsync("node1", 100));
        Assert.Equal("500 VM 100 not running", e.Message);
        Assert.Equal(500, e.StatusCode);
    }

    [Fact]
    public async Task ParameterErrorsAreSpelledOut()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => new FakeResponse(400, "Parameter verification failed.", """{"data":null,"errors":{"proxy":"invalid format\n"}}""");
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        var e = await Assert.ThrowsAsync<PveApiException>(() => c.GetSpiceVvTextAsync("node1", 100));
        Assert.Equal("400 Parameter verification failed. (proxy: invalid format)", e.Message);
    }

    [Fact]
    public async Task BadTargetsNeverReachTheServer()
    {
        await using var pve = new FakePve(TestPki.Shared);
        using var c = new PveClient(pve.Url, pve.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        await Assert.ThrowsAsync<PveApiException>(() => c.GetSpiceVvTextAsync("../access", 100));
        await Assert.ThrowsAsync<PveApiException>(() => c.GetSpiceVvTextAsync("node1", 99));
        Assert.Empty(pve.Seen());
    }
}
