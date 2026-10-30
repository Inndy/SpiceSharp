using Pve.Term.Tests;

namespace Pve.Api.Tests;

/// <summary>The shared sign-in flow with a scripted prompt, a fake server and a test-only credential prefix.</summary>
public sealed class SignInTests : IDisposable
{
    readonly CredentialStore _store = new("SpiceViewer.Test-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() => _store.DeleteAll();

    sealed class Prompt : ISignInPrompt
    {
        public Queue<SecretAnswer?> Secrets { get; } = new();
        public Queue<string?> Factors { get; } = new();
        public List<string?> Problems { get; } = [];
        public TfaChallenge? Challenge;

        public Task<SecretAnswer?> AskSecretAsync(PveServerConfig server, string? problem, CancellationToken ct)
        {
            Problems.Add(problem);
            return Task.FromResult(Secrets.Count > 0 ? Secrets.Dequeue() : null);
        }

        public Task<string?> AskSecondFactorAsync(PveServerConfig server, TfaChallenge challenge, string? problem, CancellationToken ct)
        {
            Challenge = challenge;
            Problems.Add(problem);
            return Task.FromResult(Factors.Count > 0 ? Factors.Dequeue() : null);
        }
    }

    static PveServerConfig PasswordServer(FakePve pve) => new() { Url = pve.Url.ToString(), User = "alice", Realm = "pve", CaPem = pve.CaPem };
    static PveServerConfig TokenServer(FakePve pve) => new() { Url = pve.Url.ToString(), Auth = PveAuthKind.Token, TokenId = Fake.TokenId, CaPem = pve.CaPem };

    [Fact]
    public async Task ARememberedTokenIsSavedAndUsedNextTime()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Header("Authorization") == $"PVEAPIToken={Fake.TokenId}={Fake.TokenSecret}"
            ? FakeResponse.Data(Fake.Version) : new FakeResponse(401, "invalid token value!", "{}");
        var server = TokenServer(pve);
        var prompt = new Prompt();
        prompt.Secrets.Enqueue(new SecretAnswer("wrong", true));
        prompt.Secrets.Enqueue(new SecretAnswer(Fake.TokenSecret, true));
        using (var c = new PveClient(pve.Url, pve.CaPem))
        {
            Assert.True(await PveSignIn.SignInAsync(c, server, _store, prompt));
            Assert.Equal([null, "The server refused the credentials: 401 invalid token value!"], prompt.Problems);
        }
        Assert.Equal(Fake.TokenSecret, _store.Read(_store.TargetFor(server)));

        var silent = new Prompt();
        using (var c = new PveClient(pve.Url, pve.CaPem))
        {
            Assert.True(await PveSignIn.SignInAsync(c, server, _store, silent));
            Assert.Empty(silent.Problems); // the saved token was used without asking
        }
    }

    [Fact]
    public async Task ARefusedSavedPasswordIsAskedForAgainAndNotSavedUnlessAsked()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Form().GetValueOrDefault("password") == Fake.Password ? Fake.TicketAnswer(Fake.Ticket) : new FakeResponse(401, "authentication failure", "{}");
        var server = PasswordServer(pve);
        string target = _store.TargetFor(server);
        _store.Write(target, server.Account, "stale password");
        var prompt = new Prompt();
        prompt.Secrets.Enqueue(new SecretAnswer(Fake.Password, false));
        using var c = new PveClient(pve.Url, pve.CaPem);
        Assert.True(await PveSignIn.SignInAsync(c, server, _store, prompt));
        Assert.Equal(["The saved credential was refused; enter it again."], prompt.Problems);
        Assert.Equal("stale password", _store.Read(target)); // not overwritten: the user did not ask to remember
    }

    [Fact]
    public async Task TotpIsAskedForAndRetried()
    {
        string half = Fake.TfaTicket("""{"totp":true}""");
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r =>
        {
            var f = r.Form();
            if (!f.ContainsKey("tfa-challenge")) return Fake.TicketAnswer(half, needTfa: true);
            return f["password"] == "totp:654321" ? Fake.TicketAnswer(Fake.Ticket) : new FakeResponse(401, "authentication failure", "{}");
        };
        var server = PasswordServer(pve);
        var prompt = new Prompt();
        prompt.Secrets.Enqueue(new SecretAnswer(Fake.Password, true));
        prompt.Factors.Enqueue("totp:111111");
        prompt.Factors.Enqueue("totp:654321");
        using var c = new PveClient(pve.Url, pve.CaPem);
        Assert.True(await PveSignIn.SignInAsync(c, server, _store, prompt));
        Assert.True(prompt.Challenge!.Totp);
        Assert.Equal([null, null, "The code was not accepted."], prompt.Problems);
        Assert.Equal(Fake.Password, _store.Read(_store.TargetFor(server)));
    }

    [Fact]
    public async Task CancellingTheSecondFactorSignsOut()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => Fake.TicketAnswer(Fake.TfaTicket("""{"totp":true}"""), needTfa: true);
        var prompt = new Prompt();
        prompt.Secrets.Enqueue(new SecretAnswer(Fake.Password, true));
        using var c = new PveClient(pve.Url, pve.CaPem);
        Assert.False(await PveSignIn.SignInAsync(c, PasswordServer(pve), _store, prompt));
        Assert.False(c.IsSignedIn);
        Assert.Empty(_store.List()); // nothing is saved for a sign-in that did not finish
    }

    [Fact]
    public async Task AnUnsupportedSecondFactorIsExplained()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => Fake.TicketAnswer(Fake.TfaTicket("""{"webauthn":{"challenge":"c"}}"""), needTfa: true);
        var prompt = new Prompt();
        prompt.Secrets.Enqueue(new SecretAnswer(Fake.Password, false));
        using var c = new PveClient(pve.Url, pve.CaPem);
        var e = await Assert.ThrowsAsync<PveApiException>(() => PveSignIn.SignInAsync(c, PasswordServer(pve), _store, prompt));
        Assert.Contains("WebAuthn", e.Message);
    }

    [Fact]
    public async Task NoSecretOrTicketReachesTheLogOrToString()
    {
        string half = Fake.TfaTicket("""{"totp":true}""");
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Path switch
        {
            "/api2/json/access/ticket" when !r.Form().ContainsKey("tfa-challenge") && r.Form()["password"] == Fake.Password => Fake.TicketAnswer(half, needTfa: true),
            "/api2/json/access/ticket" => Fake.TicketAnswer(Fake.Ticket2, Fake.Csrf2),
            "/api2/json/cluster/resources?type=vm" => FakeResponse.Data("[]"),
            _ => FakeResponse.Data(Fake.SpiceAnswer),
        };
        var log = new List<string>();
        var time = new ManualTime();
        var prompt = new Prompt();
        prompt.Secrets.Enqueue(new SecretAnswer(Fake.Password, false));
        prompt.Factors.Enqueue("totp:123456");
        using var c = new PveClient(pve.Url, pve.CaPem, time) { Log = log.Add };
        Assert.True(await PveSignIn.SignInAsync(c, PasswordServer(pve), _store, prompt));
        time.Advance(TimeSpan.FromMinutes(20));
        await c.ListVmsAsync();
        await c.GetSpiceVvTextAsync("node1", 100);
        var d = await c.CreateTermDescriptorAsync(new PveVm(100, "vm", "node1", PveGuestType.Qemu, "running", false));

        Assert.Contains(log, l => l.Contains("renewed"));
        string all = string.Join("\n", log) + c + new SecretAnswer(Fake.Password, true) + d;
        foreach (var secret in new[] { Fake.Password, "SECRET", Fake.Ticket, Fake.Ticket2, half, Fake.Csrf, Fake.Csrf2, "123456", "SPICETICKET" })
            Assert.DoesNotContain(secret, all);
    }
}
