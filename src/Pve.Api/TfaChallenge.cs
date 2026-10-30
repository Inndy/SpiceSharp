using System.Text.Json;

namespace Pve.Api;

/// <summary>
/// The second factors a half-signed-in user may answer with (docs/decisions/0039). Proxmox VE 7+ returns, from the
/// first <c>POST /access/ticket</c>, a ticket <c>PVE:!tfa!&lt;JSON&gt;:&lt;time&gt;::&lt;signature&gt;</c> whose data
/// is a <c>TfaChallenge</c> (proxmox-tfa) with ':' and '%' percent-escaped (pve-common PVE/Ticket.pm). The keys are
/// <c>totp</c> (bool), <c>recovery</c> (the indices of unused recovery keys), <c>webauthn</c>, <c>u2f</c> and
/// <c>yubico</c> (pve-manager LoginWindow.js and proxmox-widget-toolkit TfaWindow.js read it the same way).
/// </summary>
public sealed class TfaChallenge
{
    public const string TicketPrefix = "PVE:!tfa!";

    public bool Totp { get; init; }
    /// <summary>Whether recovery keys are set up at all; <see cref="RecoveryKeysLeft"/> may still be 0.</summary>
    public bool Recovery { get; init; }
    public int RecoveryKeysLeft { get; init; }
    public bool WebAuthn { get; init; }
    public bool U2f { get; init; }
    public bool Yubico { get; init; }

    /// <summary>True when this client can answer: TOTP, or a recovery key that is left.</summary>
    public bool CanAnswer => Totp || RecoveryKeysLeft > 0;

    /// <summary>The factors this client can't answer, for the message the user sees.</summary>
    public string UnsupportedFactors => string.Join(", ",
        new[] { (WebAuthn, "WebAuthn"), (U2f, "U2F"), (Yubico, "Yubico OTP") }.Where(f => f.Item1).Select(f => f.Item2));

    public static bool IsChallengeTicket(string ticket) => ticket.StartsWith(TicketPrefix, StringComparison.Ordinal);

    /// <summary>Reads the challenge out of a <c>PVE:!tfa!...</c> ticket.</summary>
    public static TfaChallenge FromTicket(string ticket)
    {
        if (!IsChallengeTicket(ticket)) throw new PveApiException("The server's second-factor challenge is malformed.");
        int end = ticket.IndexOf(':', TicketPrefix.Length);
        if (end < 0) throw new PveApiException("The server's second-factor challenge is malformed.");
        string json = Uri.UnescapeDataString(ticket[TicketPrefix.Length..end]);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PveApiException("The server's second-factor challenge is malformed.");
            bool Present(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.False);
            int left = root.TryGetProperty("recovery", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0;
            return new TfaChallenge
            {
                Totp = Present("totp"),
                Recovery = Present("recovery"),
                RecoveryKeysLeft = left,
                WebAuthn = Present("webauthn"),
                U2f = Present("u2f"),
                Yubico = Present("yubico"),
            };
        }
        catch (JsonException) { throw new PveApiException("The server's second-factor challenge is not valid JSON."); }
    }

    /// <summary>The <c>password</c> value for a TOTP code: <c>totp:123456</c> (TfaWindow.js loginTotp).</summary>
    public static string TotpResponse(string code)
    {
        code = code.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (code.Length is < 6 or > 8 || !code.All(char.IsAsciiDigit))
            throw new ArgumentException("A TOTP code is 6 to 8 digits.", nameof(code));
        return "totp:" + code;
    }

    /// <summary>The <c>password</c> value for a recovery key: <c>recovery:KEY</c> (TfaWindow.js loginRecovery).</summary>
    public static string RecoveryResponse(string key)
    {
        key = key.Trim();
        if (key.Length is 0 or > 128 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException("A recovery key is letters, digits and dashes.", nameof(key));
        return "recovery:" + key;
    }
}
