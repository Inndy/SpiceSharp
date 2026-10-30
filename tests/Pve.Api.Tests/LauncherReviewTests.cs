using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Pve.Term.Tests;

namespace Pve.Api.Tests;

/// <summary>Regressions for docs/reviews/2026-10-31-launcher.md. Synthetic secrets and loopback HTTPS only.</summary>
public sealed class LauncherReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReflectedPasswordDoesNotReachTheSignInException(bool inReason)
    {
        const string password = "review-password-marker";
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r =>
        {
            string secret = r.Form()["password"];
            return inReason
                ? new FakeResponse(401, "rejected " + secret, "{}")
                : new FakeResponse(400, "Parameter verification failed", "{\"errors\":{\"password\":\"invalid value: " + secret + "\"}}");
        };
        using var c = new PveClient(pve.Url, pve.CaPem);
        var error = await Assert.ThrowsAnyAsync<PveApiException>(() => c.LoginAsync("alice", "pve", password));
        Assert.False(error.ToString().Contains(password, StringComparison.Ordinal), "The sign-in exception exposed the submitted password.");
    }

    [Fact]
    public async Task ReflectedRenewalTicketDoesNotReachTheLog()
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Path != "/api2/json/access/ticket"
            ? FakeResponse.Data(Fake.Version)
            : r.Form()["password"] == Fake.Password
                ? Fake.TicketAnswer(Fake.Ticket)
                : new FakeResponse(503, "retry " + r.Form()["password"], "{}");
        var time = new ManualTime();
        var log = new List<string>();
        using var c = new PveClient(pve.Url, pve.CaPem, time) { Log = log.Add };
        await c.LoginAsync("alice", "pve", Fake.Password);
        time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("9.2.1", await c.GetVersionAsync());
        Assert.True(c.IsSignedIn); // a transient renewal error may retain this still-valid ticket
        Assert.False(string.Join('\n', log).Contains(Fake.Ticket, StringComparison.Ordinal), "A renewal log line exposed the session ticket.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReflectedAuthHeadersDoNotReachApiExceptions(bool token)
    {
        await using var pve = new FakePve(TestPki.Shared);
        pve.Handler = r => r.Path == "/api2/json/access/ticket" ? Fake.TicketAnswer(Fake.Ticket)
            : new FakeResponse(500, "request failed", "{\"errors\":{\"request\":\"" +
                (token ? r.Header("Authorization") : r.Header("Cookie") + " " + r.Header("CSRFPreventionToken")) + "\"}}");
        using var c = new PveClient(pve.Url, pve.CaPem);
        if (token) c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        else await c.LoginAsync("alice", "pve", Fake.Password);
        var error = await Assert.ThrowsAsync<PveApiException>(() => c.GetSpiceVvTextAsync("node1", 100));
        foreach (string secret in token ? new[] { Fake.TokenSecret } : new[] { Fake.Ticket, Fake.Csrf })
            Assert.False(error.ToString().Contains(secret, StringComparison.Ordinal), "An API exception exposed an authentication header value.");
    }

    [Theory]
    [InlineData(PveAuthKind.Password, "alice@pve", "Alice@pve")]
    [InlineData(PveAuthKind.Token, "alice@pve!viewer", "alice@pve!Viewer")]
    public void CaseDistinctAccountsHaveIndependentSavedSecrets(PveAuthKind kind, string first, string second)
    {
        // Real Windows semantics under an isolated prefix; never inspect the launcher's actual credentials.
        var store = new CredentialStore("SpiceViewer.Test-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var url = new Uri("https://localhost:8006/");
            string a = store.TargetFor(url, kind, first), b = store.TargetFor(url, kind, second);
            store.Write(a, first, "first-review-secret");
            store.Write(b, second, "second-review-secret");
            Assert.Equal(2, store.List().Count);
            Assert.Equal("first-review-secret", store.Read(a));
            Assert.Equal("second-review-secret", store.Read(b));
            Assert.True(store.Delete(a));
            Assert.Equal("second-review-secret", store.Read(b));
        }
        finally { store.DeleteAll(); }
    }

    [Fact]
    public async Task ResponseBodyIsCoveredByTheThirtySecondRequestDeadline()
    {
        await using var pve = new StreamingPve(sendOversizedBody: false);
        using var c = new PveClient(pve.Url, TestPki.Shared.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        using var cleanup = new CancellationTokenSource();
        Task<string> call = c.GetVersionAsync(cleanup.Token);
        try
        {
            await pve.HeadersSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Allow five seconds beyond the configured deadline; a stalled peer must not leave the window busy forever.
            await Assert.ThrowsAsync<PveApiException>(async () => await call.WaitAsync(TimeSpan.FromSeconds(35)));
        }
        finally
        {
            cleanup.Cancel();
            try { await call; } catch (Exception) { } // collect the cancelled request before disposing TLS
        }
    }

    [Fact]
    public async Task ChunkedResponseIsRejectedAtTheSizeLimitBeforeEndOfBody()
    {
        await using var pve = new StreamingPve(sendOversizedBody: true);
        using var c = new PveClient(pve.Url, TestPki.Shared.CaPem);
        c.UseApiToken(Fake.TokenId, Fake.TokenSecret);
        using var cleanup = new CancellationTokenSource();
        Task<string> call = c.GetVersionAsync(cleanup.Token);
        try
        {
            // The peer sends 17 MiB with no final chunk. A 16-MiB bound must reject it without waiting for EOF.
            Task ready = await Task.WhenAny(call, pve.BodySent.Task).WaitAsync(TimeSpan.FromSeconds(15));
            if (ready == pve.BodySent.Task) await pve.BodySent.Task;
            var error = await Assert.ThrowsAsync<PveApiException>(async () => await call.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Contains("too large", error.Message);
        }
        finally
        {
            cleanup.Cancel();
            try { await call; } catch (Exception) { }
        }
    }

    /// <summary>One loopback TLS request with a deliberately unfinished HTTP body; cancellation cleans up both ends.</summary>
    sealed class StreamingPve : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource _stop = new();
        readonly Task _serve;
        public Uri Url { get; }
        public TaskCompletionSource HeadersSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BodySent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StreamingPve(bool sendOversizedBody)
        {
            _listener.Start();
            Url = new Uri($"https://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _serve = ServeAsync(sendOversizedBody);
        }

        async Task ServeAsync(bool sendOversizedBody)
        {
            try
            {
                using var peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var tls = new SslStream(peer.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = TestPki.Shared.Server }, _stop.Token);
                // Read exactly through the request headers (these tests make GET requests with no body).
                byte[] one = new byte[1];
                int matched = 0;
                byte[] terminator = "\r\n\r\n"u8.ToArray();
                while (matched != terminator.Length)
                {
                    if (await tls.ReadAsync(one, _stop.Token) == 0) throw new IOException("The test client closed before its headers.");
                    matched = one[0] == terminator[matched] ? matched + 1 : one[0] == terminator[0] ? 1 : 0;
                }
                string headers = sendOversizedBody
                    ? "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n"
                    : "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nContent-Type: application/json\r\n\r\n";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(headers), _stop.Token);
                await tls.FlushAsync(_stop.Token);
                HeadersSent.TrySetResult();
                if (sendOversizedBody)
                {
                    byte[] chunk = new byte[64 * 1024];
                    Array.Fill(chunk, (byte)' ');
                    for (int i = 0; i < 17 * 16; i++)
                    {
                        await tls.WriteAsync("10000\r\n"u8.ToArray(), _stop.Token);
                        await tls.WriteAsync(chunk, _stop.Token);
                        await tls.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
                    }
                    await tls.FlushAsync(_stop.Token);
                }
                BodySent.TrySetResult();
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                if (!_stop.IsCancellationRequested)
                {
                    HeadersSent.TrySetException(e);
                    BodySent.TrySetException(e);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _serve;
            _stop.Dispose();
        }
    }
}
