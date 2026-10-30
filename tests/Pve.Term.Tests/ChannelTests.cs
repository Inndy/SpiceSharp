using System.Text;

namespace Pve.Term.Tests;

/// <summary>The transport against the loopback fake termproxy, in process.</summary>
public class ChannelTests
{
    static async Task<string> ReadUntil(ITermChannel ch, string text)
    {
        var sb = new StringBuilder();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(15000);
        while (!sb.ToString().Contains(text, StringComparison.Ordinal))
        {
            int n = await ch.ReadAsync(buf, cts.Token);
            if (n == 0) throw new IOException("closed: " + ch.CloseReason + "; got " + sb);
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RequestsTheSessionThenRoundTrips(bool splitOk, bool portAsString)
    {
        await using var fake = new FakeTermproxy(TestPki.Shared) { SplitOk = splitOk, PortAsString = portAsString };
        await using var ch = await TermproxyConnector.ConnectAsync(fake.Descriptor(), TimeSpan.Zero, CancellationToken.None);
        Assert.Equal("post /api2/json/nodes/testnode/qemu/100/termproxy body=serial=serial0", fake.Expect("post"));
        Assert.Equal("ws protocol=binary", fake.Expect("ws "));
        Assert.Equal($"auth {FakeTermproxy.User}:{FakeTermproxy.Ticket}", fake.Expect("auth"));
        Assert.Contains("fake-login: ", await ReadUntil(ch, "fake-login: "));

        await ch.SendAsync(Encoding.UTF8.GetBytes("uname é中\r"));
        Assert.Equal("data uname \\xc3\\xa9\\xe4\\xb8\\xad\\x0d", fake.Expect("data"));
        await ReadUntil(ch, "uname é中");

        await ch.ResizeAsync(100, 30);
        await ch.ResizeAsync(100, 30); // deduplicated
        await ch.ResizeAsync(101, 30);
        Assert.Equal("resize 100x30", fake.Expect("resize"));
        Assert.Equal("resize 101x30", fake.Expect("resize"));

        await ch.SendAsync("exit\r"u8.ToArray());
        fake.Expect("exit");
        var buf = new byte[256];
        using var cts = new CancellationTokenSource(15000);
        while (await ch.ReadAsync(buf, cts.Token) > 0) { }
        Assert.Equal("the server closed the connection", ch.CloseReason);
        await ch.SendAsync("ignored"u8.ToArray()); // after the end: dropped, no exception
        Assert.False(fake.FragmentSeen);
        Assert.Single(fake.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LargeInputIsChunkedIntoUnfragmentedFrames()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        await using var ch = await TermproxyConnector.ConnectAsync(fake.Descriptor(), TimeSpan.Zero, CancellationToken.None);
        var big = Enumerable.Range(0, 100_000).Select(i => (byte)('a' + i % 26)).ToArray();
        await ch.SendAsync(big);
        var deadline = Environment.TickCount64 + 15000;
        while (fake.InputBytes().Length < big.Length && Environment.TickCount64 < deadline) fake.Expect("data");
        Assert.Equal(big, fake.InputBytes());
        Assert.False(fake.FragmentSeen);
    }

    [Fact]
    public async Task ASessionFromTheLauncherSkipsTheApiRequest()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        await using var ch = await TermproxyConnector.ConnectAsync(fake.Descriptor(withSession: true), TimeSpan.Zero, CancellationToken.None);
        fake.Expect("auth");
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KeepAlivePingsOnlyWhenIdle()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        await using var ch = await TermproxyConnector.ConnectAsync(fake.Descriptor(), TimeSpan.FromMilliseconds(300), CancellationToken.None);
        fake.Expect("ping", 10000);
        Assert.True(ch.PingsSent >= 1);
    }

    [Fact]
    public async Task NoKeepAliveMeansNoPing()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        await using (var ch = await TermproxyConnector.ConnectAsync(fake.Descriptor(), TimeSpan.Zero, CancellationToken.None))
        {
            await ch.SendAsync("x"u8.ToArray());
            fake.Expect("data");
            await Task.Delay(500);
            Assert.Equal(0, ch.PingsSent);
        }
        Assert.DoesNotContain(fake.Events, e => e == "ping");
    }

    [Fact]
    public async Task ApiRefusalIsReportedWithPveReason()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared) { RefuseApi = true };
        var e = await Assert.ThrowsAsync<TermConnectException>(() =>
            TermproxyConnector.ConnectAsync(fake.Descriptor(), TimeSpan.Zero, CancellationToken.None));
        Assert.Contains("403 Permission check failed (/vms/100, VM.Console)", e.Message);
    }

    [Fact]
    public async Task WrongTokenIsRefusedWithoutLeakingIt()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        const string bad = "test@pve!bridge=WRONG-SECRET";
        var e = await Assert.ThrowsAsync<TermConnectException>(() =>
            TermproxyConnector.ConnectAsync(fake.Descriptor(token: bad), TimeSpan.Zero, CancellationToken.None));
        Assert.DoesNotContain("WRONG-SECRET", e.Message);
    }

    [Fact]
    public async Task RejectedTicketLineIsAConnectError()
    {
        await using var fake = new FakeTermproxy(TestPki.Shared);
        var d = fake.Descriptor(withSession: true);
        var wrongUser = new TermDescriptor
        {
            ApiUrl = d.ApiUrl, Node = d.Node, Target = d.Target, Vmid = d.Vmid, Serial = d.Serial, ApiToken = d.ApiToken,
            CaPem = d.CaPem, TermUser = "someone@pve", TermTicket = d.TermTicket, TermPort = d.TermPort,
        };
        var e = await Assert.ThrowsAsync<TermConnectException>(() => TermproxyConnector.ConnectAsync(wrongUser, TimeSpan.Zero, CancellationToken.None));
        Assert.Contains("before accepting the ticket", e.Message);
    }
}
