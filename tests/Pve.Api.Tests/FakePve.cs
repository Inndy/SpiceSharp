using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Pve.Term.Tests;

namespace Pve.Api.Tests;

/// <summary>One request as the fake server saw it.</summary>
sealed record FakeRequest(string Method, string Path, Dictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;

    public Dictionary<string, string> Form() => Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
}

sealed record FakeResponse(int Status, string Reason, string Body)
{
    public static FakeResponse Json(string body) => new(200, "OK", body);
    public static FakeResponse Data(string dataJson) => new(200, "OK", "{\"data\":" + dataJson + "}");
}

/// <summary>
/// Test-only stand-in for pveproxy on loopback: HTTPS with a per-run test CA, HTTP/1.1 with keep-alive (HttpClient
/// reuses connections), a handler per request, and a log of what arrived. Nothing listens beyond 127.0.0.1.
/// </summary>
sealed class FakePve : IAsyncDisposable
{
    readonly TcpListener _listener;
    readonly TestPki _pki;
    readonly CancellationTokenSource _stop = new();
    readonly Task _accept;

    public int Port { get; }
    public ConcurrentQueue<FakeRequest> Requests { get; } = [];
    public Func<FakeRequest, FakeResponse> Handler { get; set; } = _ => new FakeResponse(501, "Not Implemented", "{}");

    public FakePve(TestPki pki)
    {
        _pki = pki;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync();
    }

    public Uri Url => new($"https://localhost:{Port}/");
    public Uri IpUrl => new($"https://127.0.0.1:{Port}/");
    public string CaPem => _pki.CaPem;

    public FakeRequest[] Seen() => Requests.ToArray();

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
            var buffer = new List<byte>();
            var chunk = new byte[8192];
            while (true)
            {
                int headerEnd;
                while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8)) < 0)
                {
                    int n = await ssl.ReadAsync(chunk, _stop.Token);
                    if (n == 0) return;
                    buffer.AddRange(chunk.AsSpan(0, n));
                }
                string head = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray());
                buffer.RemoveRange(0, headerEnd + 4);
                var lines = head.Split("\r\n");
                var first = lines[0].Split(' ');
                var headers = new Dictionary<string, string>();
                foreach (var l in lines.Skip(1))
                {
                    int c = l.IndexOf(':');
                    if (c > 0) headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim();
                }
                int length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
                while (buffer.Count < length)
                {
                    int n = await ssl.ReadAsync(chunk, _stop.Token);
                    if (n == 0) return;
                    buffer.AddRange(chunk.AsSpan(0, n));
                }
                string body = Encoding.UTF8.GetString(buffer.GetRange(0, length).ToArray());
                buffer.RemoveRange(0, length);
                var req = new FakeRequest(first[0], first[1], headers, body);
                Requests.Enqueue(req);
                FakeResponse res;
                try { res = Handler(req); }
                catch (Exception e) { res = new FakeResponse(500, "fake handler: " + e.Message.Replace('\r', ' ').Replace('\n', ' '), "{}"); }
                var bytes = Encoding.UTF8.GetBytes(res.Body);
                var header = $"HTTP/1.1 {res.Status} {res.Reason}\r\nContent-Type: application/json;charset=UTF-8\r\nContent-Length: {bytes.Length}\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
                await ssl.WriteAsync(bytes, _stop.Token);
                await ssl.FlushAsync(_stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException
            or System.Security.Authentication.AuthenticationException) { }
    }

    static int IndexOf(List<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Count; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
            if (match) return i;
        }
        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _accept; } catch { }
        _stop.Dispose();
    }
}

/// <summary>A clock tests move by hand (ticket renewal and expiry).</summary>
sealed class ManualTime : TimeProvider
{
    long _ticks = TimeSpan.TicksPerDay;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
