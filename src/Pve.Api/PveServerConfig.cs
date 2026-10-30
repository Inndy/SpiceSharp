namespace Pve.Api;

/// <summary>How the launcher signs in to a server (docs/decisions/0039).</summary>
public enum PveAuthKind { Token, Password }

/// <summary>
/// A saved PVE server. It holds no secret: the token secret or password, if the user chose to remember it, is in
/// Credential Manager (<see cref="CredentialStore"/>).
/// </summary>
public sealed class PveServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>What the list shows; defaults to the host.</summary>
    public string Name { get; set; } = "";
    /// <summary>https://host[:port]; the port defaults to 8006.</summary>
    public string Url { get; set; } = "";
    public PveAuthKind Auth { get; set; } = PveAuthKind.Password;
    /// <summary>Password sign-in: the user name without the realm, e.g. root.</summary>
    public string User { get; set; } = "";
    /// <summary>Password sign-in: the realm, e.g. pam or pve.</summary>
    public string Realm { get; set; } = "pam";
    /// <summary>Token sign-in: USER@REALM!TOKENID.</summary>
    public string TokenId { get; set; } = "";
    /// <summary>PEM of a CA to pin (docs/decisions/0033); null or empty uses the Windows trust roots.</summary>
    public string? CaPem { get; set; }

    /// <summary>USER@REALM for a password sign-in, the token ID for a token.</summary>
    public string Account => Auth == PveAuthKind.Token ? TokenId : $"{User}@{Realm}";

    public string DisplayName => Name.Length > 0 ? Name : Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url;

    /// <summary>Why this entry can't be used, or null when it is complete. The URL and account forms are checked.</summary>
    public string? Problem()
    {
        try { PveClient.NormalizeApiUrl(Url); }
        catch (PveApiException e) { return e.Message; }
        if (Auth == PveAuthKind.Token)
        {
            if (!PveClient.IsValidTokenId(TokenId)) return "The token ID must look like user@realm!tokenname.";
        }
        else
        {
            if (!PveClient.IsValidUserPart(User)) return "Enter a user name (without @realm).";
            if (!PveClient.IsValidUserPart(Realm)) return "Enter a realm, for example pam or pve.";
        }
        if (!string.IsNullOrWhiteSpace(CaPem))
        {
            try { _ = new Pve.Term.PveTls(CaPem); }
            catch (Pve.Term.TermDescriptorException) { return "The CA is not a valid PEM certificate."; }
        }
        return null;
    }

    public PveServerConfig Clone() => (PveServerConfig)MemberwiseClone();
}
