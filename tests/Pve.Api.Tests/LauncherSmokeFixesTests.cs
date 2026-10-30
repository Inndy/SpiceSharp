using System.Net;
using System.Net.Sockets;
using Pve.Term.Tests;

namespace Pve.Api.Tests;

/// <summary>B5/B6 and error-redaction regressions. Synthetic secrets, loopback HTTPS, no launcher or GUI.</summary>
public sealed class LauncherSmokeFixesTests
{
    sealed class Prompt : ISignInPrompt
    {
        public Func<CancellationToken, Task<SecretAnswer?>> Secret = _ => Task.FromResult<SecretAnswer?>(null);
        public Func<CancellationToken, Task<string?>> Factor = _ => Task.FromResult<string?>(null);
        public List<string?> SecretProblems { get; } = [];
        public List<string?> FactorProblems { get; } = [];

        public Task<SecretAnswer?> AskSecretAsync(PveServerConfig server, string? problem, CancellationToken ct)
        {
            SecretProblems.Add(problem);
            return Secret(ct);
        }

        public Task<string?> AskSecondFactorAsync(PveServerConfig server, TfaChallenge challenge, string? problem, CancellationToken ct)
        {
            FactorProblems.Add(problem);
            return Factor(ct);
        }
    }

    static Prompt Answer(bool token) => new()
    {
        Secret = _ => Task.FromResult<SecretAnswer?>(new(token ? Fake.TokenSecret : Fake.Password, false)),
    };

    static PveServerConfig Server(FakePve pve, bool token) => new()
    {
        Url = pve.Url.ToString(), CaPem = pve.CaPem, Auth = token ? PveAuthKind.Token : PveAuthKind.Password,
        TokenId = Fake.TokenId, User = "alice", Realm = "pve",
    };

    static void SignedOut(PveClient client)
    {
        Assert.False(client.IsSignedIn);
        Assert.Null(client.SignedInAs);
        Assert.Null(client.ServerVersion);
    }

    static async Task NoPendingFactor(PveClient client) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteSecondFactorAsync("totp:123456"));

    [Theory]
    [InlineData(true, 403)]
    [InlineData(true, 500)]
    [InlineData(false, 403)]
    [InlineData(false, 500)]
    public async Task NonAuthHttpFailuresPropagateWithoutReprompting(bool token, int status)
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = _ => new FakeResponse(status, "request failed", "{}");
        using var c = new PveClient(pve.Url, pve.CaPem);
        var prompt = Answer(token);
        var error = await Assert.ThrowsAsync<PveApiException>(() => PveSignIn.SignInAsync(c, Server(pve, token), null, prompt));
        Assert.Equal(status, error.StatusCode);
        Assert.Single(prompt.SecretProblems);
        Assert.Single(pve.Seen());
        SignedOut(c);
    }

    [Theory]
    [InlineData(true, "not JSON")]
    [InlineData(true, "{}")]
    [InlineData(false, "not JSON")]
    [InlineData(false, "{}")]
    [InlineData(false, "{\"data\":{\"username\":\"alice@pve\"}}")]
    public async Task MalformedSignInAnswersLeaveNoSession(bool token, string body)
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = _ => FakeResponse.Json(body);
        using var c = new PveClient(pve.Url, pve.CaPem);
        var prompt = Answer(token);
        await Assert.ThrowsAsync<PveApiException>(() => PveSignIn.SignInAsync(c, Server(pve, token), null, prompt));
        Assert.Single(prompt.SecretProblems);
        SignedOut(c);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefusedSecretsAreRetriedWhileSignedOut(bool token)
    {
        await using var pve = new FakePve(TestPki.Shared);
        int requests = 0;
        pve.Handler = _ => Interlocked.Increment(ref requests) == 1
            ? new FakeResponse(401, "credential refused", "{}")
            : token ? FakeResponse.Data(Fake.Version) : Fake.TicketAnswer(Fake.Ticket);
        using var c = new PveClient(pve.Url, pve.CaPem);
        var prompt = Answer(token);
        prompt.Secret = _ =>
        {
            SignedOut(c); // also holds while asking again after the 401
            return Task.FromResult<SecretAnswer?>(new(token ? Fake.TokenSecret : Fake.Password, false));
        };
        Assert.True(await PveSignIn.SignInAsync(c, Server(pve, token), null, prompt));
        Assert.True(c.IsSignedIn);
        Assert.Equal(2, pve.Seen().Length);
        Assert.Equal(2, prompt.SecretProblems.Count);
        Assert.Null(prompt.SecretProblems[0]);
        Assert.Contains("credential refused", prompt.SecretProblems[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExhaustingRefusedSecretsLeavesNoSession(bool token)
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = _ => new FakeResponse(401, "credential refused", "{}");
        using var c = new PveClient(pve.Url, pve.CaPem);
        var prompt = Answer(token);
        Assert.False(await PveSignIn.SignInAsync(c, Server(pve, token), null, prompt));
        Assert.Equal(5, pve.Seen().Length);
        Assert.Equal(5, prompt.SecretProblems.Count);
        SignedOut(c);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARefusedConnectionIsNotARefusedSecret(bool token)
    {
        // Reserve a loopback port without listening, so no other server can take it during the request.
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var url = new Uri($"https://127.0.0.1:{((IPEndPoint)reservation.LocalEndPoint!).Port}/");
        using var c = new PveClient(url, TestPki.Shared.CaPem);
        var server = new PveServerConfig { Url = url.ToString(), Auth = token ? PveAuthKind.Token : PveAuthKind.Password,
            TokenId = Fake.TokenId, User = "alice", Realm = "pve" };
        var prompt = Answer(token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<PveApiException>(() => PveSignIn.SignInAsync(c, server, null, prompt, deadline.Token));
        Assert.Null(error.StatusCode);
        Assert.Single(prompt.SecretProblems);
        SignedOut(c);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARefusedCaIsNotARefusedSecret(bool token)
    {
        await using var pve = new FakePve(TestPki.Shared);
        var other = TestPki.Create("Launcher smoke unrelated test CA");
        using var ca = other.Ca;
        using var certificate = other.Server;
        using var c = new PveClient(pve.Url, ca.ExportCertificatePem());
        var prompt = Answer(token);
        var error = await Assert.ThrowsAsync<PveApiException>(() => PveSignIn.SignInAsync(c, Server(pve, token), null, prompt));
        Assert.Contains("TLS", error.Message);
        Assert.Single(prompt.SecretProblems);
        Assert.Empty(pve.Seen());
        SignedOut(c);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequestCancellationClearsTheAttempt(bool token)
    {
        await using var pve = new FakePve(TestPki.Shared);
        using var ct = new CancellationTokenSource();
        var prompt = Answer(token);
        prompt.Secret = _ =>
        {
            ct.Cancel();
            return Task.FromResult<SecretAnswer?>(new(token ? Fake.TokenSecret : Fake.Password, false));
        };
        using var c = new PveClient(pve.Url, pve.CaPem);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PveSignIn.SignInAsync(c, Server(pve, token), null, prompt, ct.Token));
        Assert.Single(prompt.SecretProblems);
        SignedOut(c);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("password")]
    [InlineData("factor")]
    public async Task CancellationAfterTheRequestArrivesClearsTheAttempt(string step)
    {
        await using var pve = new FakePve(TestPki.Shared);
        using var ct = new CancellationTokenSource();
        pve.Handler = r =>
        {
            if (step == "factor" && !r.Form().ContainsKey("tfa-challenge"))
                return Fake.TicketAnswer(Fake.TfaTicket("{\"totp\":true}"), needTfa: true);
            ct.Cancel(); // after TLS and request transmission, before the client can accept the response
            return step == "token" ? FakeResponse.Data(Fake.Version) : Fake.TicketAnswer(Fake.Ticket);
        };
        var prompt = Answer(step == "token");
        prompt.Factor = _ => Task.FromResult<string?>("totp:123456");
        using var c = new PveClient(pve.Url, pve.CaPem);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PveSignIn.SignInAsync(c, Server(pve, step == "token"), null, prompt, ct.Token));
        SignedOut(c);
        await NoPendingFactor(c);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATransientFailureDoesNotRetryOrDeleteTheSavedSecret(bool token)
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = _ => new FakeResponse(503, "temporarily unavailable", "{}");
        var server = Server(pve, token);
        var store = new CredentialStore("SpiceViewer.Test-" + Guid.NewGuid().ToString("N")[..12]);
        string target = store.TargetFor(server), secret = token ? Fake.TokenSecret : Fake.Password;
        try
        {
            store.Write(target, server.Account, secret);
            using var c = new PveClient(pve.Url, pve.CaPem);
            var prompt = new Prompt();
            var error = await Assert.ThrowsAsync<PveApiException>(() => PveSignIn.SignInAsync(c, server, store, prompt));
            Assert.Equal(503, error.StatusCode);
            Assert.Empty(prompt.SecretProblems);
            Assert.Equal(secret, store.Read(target));
            SignedOut(c);
        }
        finally { store.DeleteAll(); }
    }

    [Fact]
    public async Task SavingFailureDoesNotUndoAnAcceptedPasswordSignIn()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = _ => Fake.TicketAnswer(Fake.Ticket);
        var store = new CredentialStore("SpiceViewer.Test-" + Guid.NewGuid().ToString("N")[..12]);
        var prompt = new Prompt
        {
            Secret = _ => Task.FromResult<SecretAnswer?>(new(new string('x', CredentialStore.MaxSecretChars + 1), true)),
        };
        try
        {
            using var c = new PveClient(pve.Url, pve.CaPem);
            await Assert.ThrowsAsync<CredentialStoreException>(() => PveSignIn.SignInAsync(c, Server(pve, false), store, prompt));
            Assert.True(c.IsSignedIn); // authentication succeeded; only the optional local save failed
            Assert.Equal("alice@pve", c.SignedInAs);
            Assert.Empty(store.List());
        }
        finally { store.DeleteAll(); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("throw")]
    [InlineData("request-cancel")]
    [InlineData("http-error")]
    [InlineData("unsupported")]
    [InlineData("malformed")]
    public async Task FailedSecondFactorDiscardsTheHalfTicket(string failure)
    {
        await using var pve = new FakePve(TestPki.Shared);
        string challenge = failure == "unsupported" ? "{\"webauthn\":{}}" : "{\"totp\":true}";
        pve.Handler = r => !r.Form().ContainsKey("tfa-challenge")
            ? Fake.TicketAnswer(Fake.TfaTicket(challenge), needTfa: true)
            : failure == "malformed" ? FakeResponse.Json("not JSON") : new FakeResponse(503, "temporarily unavailable", "{}");
        using var ct = new CancellationTokenSource();
        var prompt = Answer(false);
        prompt.Factor = _ =>
        {
            if (failure == "throw") throw new InvalidOperationException("synthetic prompt failure");
            if (failure == "request-cancel") ct.Cancel();
            return Task.FromResult<string?>(failure == "cancel" ? null : "totp:123456");
        };
        using var c = new PveClient(pve.Url, pve.CaPem);
        Task<bool> call = PveSignIn.SignInAsync(c, Server(pve, false), null, prompt, ct.Token);
        switch (failure)
        {
            case "cancel": Assert.False(await call); break;
            case "throw": await Assert.ThrowsAsync<InvalidOperationException>(() => call); break;
            case "request-cancel": await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); break;
            default: await Assert.ThrowsAsync<PveApiException>(() => call); break;
        }
        SignedOut(c);
        await NoPendingFactor(c);
        Assert.Single(prompt.SecretProblems); // a non-auth error must not restart the password prompt
    }

    [Fact]
    public async Task ThreeRefusedFactorsRestartThePasswordAndCancelClearsTheChallenge()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Form().ContainsKey("tfa-challenge")
            ? new FakeResponse(401, "code refused", "{}")
            : Fake.TicketAnswer(Fake.TfaTicket("{\"totp\":true}"), needTfa: true);
        var prompt = Answer(false);
        prompt.Secret = _ => Task.FromResult<SecretAnswer?>(prompt.SecretProblems.Count == 1 ? new(Fake.Password, false) : null);
        prompt.Factor = _ => Task.FromResult<string?>("totp:123456");
        using var c = new PveClient(pve.Url, pve.CaPem);
        Assert.False(await PveSignIn.SignInAsync(c, Server(pve, false), null, prompt));
        Assert.Equal(4, pve.Seen().Length);
        Assert.Equal([null, "The code was not accepted.", "The code was not accepted."], prompt.FactorProblems);
        Assert.Equal([null, "The second factor was refused too often."], prompt.SecretProblems);
        SignedOut(c);
        await NoPendingFactor(c);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("throw")]
    [InlineData("cancel-token")]
    public async Task AbandoningReauthenticationClearsTheRefusedTokenAndVersion(string failure)
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = _ => FakeResponse.Data(Fake.Version);
        using var c = new PveClient(pve.Url, pve.CaPem);
        var server = Server(pve, true);
        Assert.True(await PveSignIn.SignInAsync(c, server, null, Answer(true)));
        pve.Handler = _ => new FakeResponse(401, "token revoked", "{}");
        await Assert.ThrowsAsync<PveAuthException>(() => c.ListVmsAsync());
        // RunAsync calls SignInAsync again on this same client after an action returns 401.
        using var ct = new CancellationTokenSource();
        var prompt = new Prompt();
        prompt.Secret = _ =>
        {
            if (failure == "throw") throw new InvalidOperationException("synthetic prompt failure");
            if (failure == "cancel-token") { ct.Cancel(); return Task.FromCanceled<SecretAnswer?>(ct.Token); }
            return Task.FromResult<SecretAnswer?>(null);
        };
        Task<bool> call = PveSignIn.SignInAsync(c, server, null, prompt, ct.Token);
        if (failure == "throw") await Assert.ThrowsAsync<InvalidOperationException>(() => call);
        else if (failure == "cancel-token") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        else Assert.False(await call);
        SignedOut(c);
    }

    [Fact]
    public async Task VersionIsReusedForListsButResetForEachNewSignInAndSignOut()
    {
        await using var pve = new FakePve(TestPki.Shared);
        string version = "9.2.1";
        pve.Handler = r => r.Path == "/api2/json/access/ticket" ? Fake.TicketAnswer(Fake.Ticket)
            : r.Path == "/api2/json/version" ? FakeResponse.Data("{\"version\":\"" + version + "\"}") : FakeResponse.Data("[]");
        using var c = new PveClient(pve.Url, pve.CaPem);
        Assert.Null(c.ServerVersion);
        Assert.True(await PveSignIn.SignInAsync(c, Server(pve, true), null, Answer(true)));
        Assert.Equal(version, c.ServerVersion);
        await c.ListVmsAsync();
        await c.ListVmsAsync();
        Assert.Single(pve.Seen(), r => r.Path == "/api2/json/version");
        c.SignOut();
        SignedOut(c);
        version = "9.3.0";
        Assert.True(await PveSignIn.SignInAsync(c, Server(pve, true), null, Answer(true)));
        Assert.Equal(version, c.ServerVersion);
        Assert.Equal(2, pve.Seen().Count(r => r.Path == "/api2/json/version"));
        Assert.True(await PveSignIn.SignInAsync(c, Server(pve, false), null, Answer(false)));
        Assert.Null(c.ServerVersion); // password login starts a new cache lifetime, fetched with the first list
        Assert.Equal(version, await c.GetVersionAsync());
        Assert.Equal(version, c.ServerVersion);
        await c.ListVmsAsync();
        Assert.Equal(3, pve.Seen().Count(r => r.Path == "/api2/json/version"));
        c.Dispose();
        SignedOut(c);
    }

    [Fact]
    public async Task SwitchingServersKeepsTheirVersionsIndependent()
    {
        await using var a = new FakePve(TestPki.Shared);
        await using var b = new FakePve(TestPki.Shared);
        a.Handler = _ => FakeResponse.Data("{\"version\":\"8.4.1\"}");
        b.Handler = _ => FakeResponse.Data(Fake.Version);
        using var first = new PveClient(a.Url, a.CaPem);
        using var second = new PveClient(b.Url, b.CaPem);
        Assert.True(await PveSignIn.SignInAsync(first, Server(a, true), null, Answer(true)));
        Assert.Null(second.ServerVersion);
        Assert.True(await PveSignIn.SignInAsync(second, Server(b, true), null, Answer(true)));
        Assert.Equal("8.4.1", first.ServerVersion);
        Assert.Equal("9.2.1", second.ServerVersion);
        second.SignOut();
        SignedOut(second);
        Assert.Equal("8.4.1", first.ServerVersion);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("renewal-refused")]
    [InlineData("request-refused")]
    public async Task LosingThePasswordSessionInvalidatesItsVersion(string failure)
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Path == "/api2/json/access/ticket" ? Fake.TicketAnswer(Fake.Ticket) : FakeResponse.Data(Fake.Version);
        var time = new ManualTime();
        using var c = new PveClient(pve.Url, pve.CaPem, time);
        Assert.True(await PveSignIn.SignInAsync(c, Server(pve, false), null, Answer(false)));
        await c.GetVersionAsync();
        pve.Handler = _ => new FakeResponse(401, "credential refused", "{}");
        if (failure == "expired") time.Advance(TimeSpan.FromHours(2));
        if (failure == "renewal-refused") time.Advance(TimeSpan.FromMinutes(16));
        await Assert.ThrowsAsync<PveAuthException>(() => c.ListVmsAsync());
        Assert.False(c.IsSignedIn);
        Assert.Null(c.ServerVersion);
    }

    [Fact]
    public async Task MalformedHttpReflectingATokenDoesNotExposeItInDiagnostics()
    {
        await using var pve = new FakePve(TestPki.Shared);
        // A proxy echoes the submitted token as a malformed response-header line.
        pve.Handler = r => new FakeResponse(200, "OK\r\n" + r.Header("Authorization"), "{}");
        using var c = new PveClient(pve.Url, pve.CaPem);
        var error = await Assert.ThrowsAsync<PveApiException>(() => PveSignIn.SignInAsync(c, Server(pve, true), null, Answer(true)));
        SignedOut(c);
        Assert.False(error.Message.Contains(Fake.TokenSecret, StringComparison.Ordinal),
            "The transport exception message exposed the submitted token secret.");
        Assert.False(error.ToString().Contains(Fake.TokenSecret, StringComparison.Ordinal),
            "Transport exception diagnostics exposed the submitted token secret.");
    }
}
