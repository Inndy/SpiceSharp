using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using VvFile;

namespace Spice.Transport;

/// <summary>Opens the byte stream for one channel. The label is the channel name, e.g. "display:0".</summary>
public interface IStreamOpener
{
    Task<Stream> OpenAsync(string label, CancellationToken ct);
}

/// <summary>
/// Opens one byte stream to the SPICE server per channel: TCP, then (with a proxy) an HTTP CONNECT tunnel,
/// then (with tls-port) TLS pinned to the .vv CA. Every channel gets its own tunnel and TLS session.
/// </summary>
public sealed class Connector : IStreamOpener
{
    readonly VvConnection _vv;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Wraps the plaintext SPICE stream (after TLS) — recording uses this. Null in normal use.</summary>
    public Func<string, Stream, Stream>? Tap { get; init; }

    public Connector(VvConnection vv)
    {
        _vv = vv;
        if (vv.UseTls && vv.CaPem is null)
            throw new SpiceConnectException(ConnectFailure.Untrusted,
                "The connection file asks for TLS but carries no CA certificate, so the server cannot be verified.");
    }

    public async Task<Stream> OpenAsync(string label, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var token = timeout.Token;

        int port = _vv.TargetPort;
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        Stream stream;
        try
        {
            if (_vv.Proxy is { } proxy)
            {
                await ConnectSocket(socket, proxy.Host, proxy.Port, "proxy", token);
                stream = new NetworkStream(socket, ownsSocket: true);
                await HttpConnect(stream, _vv.Host, port, token);
            }
            else
            {
                await ConnectSocket(socket, _vv.Host, port, "server", token);
                stream = new NetworkStream(socket, ownsSocket: true);
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        if (!_vv.UseTls) return Tap is null ? stream : Tap(label, stream);

        var trust = new TlsTrust(_vv.CaPem!, _vv.HostSubject, _vv.Host);
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false, trust.Validate);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                // The host may be an opaque proxy token; send SNI only for something that looks like a DNS name.
                TargetHost = Uri.CheckHostName(_vv.Host) == UriHostNameType.Dns ? _vv.Host : "",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            }, token);
        }
        catch (Exception e) when (e is AuthenticationException or IOException or OperationCanceledException)
        {
            await ssl.DisposeAsync();
            if (trust.Failure is not null) throw trust.Failure;
            if (e is OperationCanceledException && !ct.IsCancellationRequested)
                throw new SpiceConnectException(ConnectFailure.Network, "The server did not complete the secure connection in time.", e);
            throw new SpiceConnectException(ConnectFailure.Tls, $"The secure connection failed: {e.Message}", e);
        }
        return Tap is null ? ssl : Tap(label, ssl);
    }

    static async Task ConnectSocket(Socket s, string host, int port, string what, CancellationToken ct)
    {
        try { await s.ConnectAsync(host, port, ct); }
        catch (SocketException e)
        {
            throw new SpiceConnectException(ConnectFailure.Network, $"Cannot reach the {what} {host}:{port} ({e.SocketErrorCode}).", e);
        }
        catch (OperationCanceledException e)
        {
            throw new SpiceConnectException(ConnectFailure.Network, $"Timed out connecting to the {what} {host}:{port}.", e);
        }
    }

    /// <summary>Sends CONNECT with the host exactly as given (Proxmox uses an opaque token) and requires a 2xx reply.</summary>
    static async Task HttpConnect(Stream s, string host, int port, CancellationToken ct)
    {
        var target = host.Contains(':') && !host.StartsWith('[') && System.Net.IPAddress.TryParse(host, out _) ? $"[{host}]:{port}" : $"{host}:{port}";
        var req = Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n\r\n");
        await s.WriteAsync(req, ct);

        // Read the response head byte by byte so no tunnel bytes are consumed.
        var head = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one, ct);
            if (n == 0) throw new SpiceConnectException(ConnectFailure.Proxy, "The proxy closed the connection.");
            head.Add(one[0]);
            int c = head.Count;
            if (c >= 4 && head[c - 4] == '\r' && head[c - 3] == '\n' && head[c - 2] == '\r' && head[c - 1] == '\n') break;
            if (c > 8192) throw new SpiceConnectException(ConnectFailure.Proxy, "The proxy sent an oversized response.");
        }
        var text = Encoding.ASCII.GetString(head.ToArray());
        var status = text.Split("\r\n", 2)[0];
        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], out int code))
            throw new SpiceConnectException(ConnectFailure.Proxy, $"The proxy sent an invalid response: {status}");
        if (code / 100 != 2)
            throw new SpiceConnectException(ConnectFailure.Proxy, $"The proxy refused the connection: {status}");
    }
}
