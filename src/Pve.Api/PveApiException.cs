namespace Pve.Api;

/// <summary>A PVE API call failed. The message is safe to show and never contains a secret or ticket.</summary>
public class PveApiException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The HTTP status, when the server answered.</summary>
    public int? StatusCode { get; } = statusCode;
}

/// <summary>The server refused the credentials or the session expired: sign in again.</summary>
public sealed class PveAuthException(string message, int? statusCode = null) : PveApiException(message, statusCode);
