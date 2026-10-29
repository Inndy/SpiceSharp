namespace Spice.Transport;

public enum ConnectFailure
{
    /// <summary>TCP connect to the server or proxy failed or timed out.</summary>
    Network,
    /// <summary>The HTTP proxy refused the CONNECT request.</summary>
    Proxy,
    /// <summary>The certificate chain does not lead to the .vv file's CA.</summary>
    Untrusted,
    /// <summary>The certificate subject differs from host-subject.</summary>
    SubjectMismatch,
    /// <summary>TLS negotiation failed for another reason.</summary>
    Tls,
    /// <summary>The server rejected the ticket (SPICE_LINK_ERR_PERMISSION_DENIED): it expired or was already used.</summary>
    TicketRejected,
    /// <summary>The server spoke something other than the SPICE protocol we implement.</summary>
    Protocol,
    /// <summary>The connection closed.</summary>
    Closed,
}

public sealed class SpiceConnectException(ConnectFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ConnectFailure Failure { get; } = failure;
}
